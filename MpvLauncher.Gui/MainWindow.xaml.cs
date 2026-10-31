using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using MpvLauncher.Gui.Services;

namespace MpvLauncher.Gui
{
    /// <summary>
    /// The application window: one place that owns every service instance and
    /// wires the UI to it.
    ///
    /// Services are constructed here rather than in App so their creation order
    /// is visible: config first, because the theme and language that follow are
    /// chosen by it, and the browser integration last, because it needs the
    /// extension files to already exist.
    ///
    /// The window also acts as the single-instance gate. A second launch is
    /// detected and forwarded here instead of opening a rival window.
    /// </summary>
    public partial class MainWindow : Window
    {
        // One instance of each service for the lifetime of the window. They are
        // stateless apart from ConfigService, which caches the live AppConfig.
        private readonly ConfigService _configService;
        private readonly UpdateService _updateService;

        /// <summary>
        /// Watches history.json while the window is open. Playbacks started from
        /// the browser are written by the native host, which is a different
        /// process entirely, so the file is the only thing both sides share.
        /// </summary>
        private FileSystemWatcher? _historyWatcher;

        /// <summary>Debounces the watcher: one host save raises several events.</summary>
        private System.Windows.Threading.DispatcherTimer? _historyRefreshTimer;

        /// <summary>
        /// What the Updates card is currently showing.
        ///
        /// Held rather than rendered once and forgotten, because a language
        /// change redraws every other label on the window and this card would
        /// otherwise be the one place left speaking the previous language. It is
        /// also what keeps the install button: re-applying the label blindly on a
        /// language switch would replace "Restart &amp; install" with "Check now"
        /// while the staged update was still waiting behind it.
        /// </summary>
        private UpdateStatus? _updateStatus;

        /// <summary>
        /// Set after an update is installed, which is not a status the service
        /// reports because it only knows about checks.
        /// </summary>
        private bool _updateInstalled;

        /// <summary>
        /// Why an install failed, kept as a key and its arguments rather than a
        /// sentence so the message can be redrawn in a new language like
        /// everything else on the card. Null when there is nothing to report.
        /// </summary>
        private sealed record UpdateFailure(string Key, string[] Args);

        private UpdateFailure? _updateFailure;

        /// <summary>
        /// Set while a check or an install is in flight, holding which one so
        /// the progress line survives a language change.
        /// </summary>
        private string? _updateBusyKey;

        /// <summary>
        /// Whether this launch has already put the offer in the status line.
        /// </summary>
        private bool _updateAnnounced;

        /// <summary>
        /// Extension version written during startup, when the build shipped a
        /// newer one than the machine had. Null when nothing changed.
        /// </summary>
        private readonly string? _refreshedExtensionVersion;

        private readonly LocalizationService _locService;
        private readonly ThemeService _themeService;
        private readonly ProcessService _procService;
        private readonly DependencyService _depService;
        private readonly BrowserIntegrationService _browserService;
        private readonly DownloadInstallService _downloadService;
        private readonly ToolUpdateService _toolUpdateService;
        private readonly ModernZAnime4kService _modernZService;
        private readonly TorrentHelperInstallService _torrentInstaller;
        private readonly TorrentSessionService _torrentSession;

        /// <summary>The mpv process started for a torrent stream, if any.</summary>
        private Process? _torrentMpv;

        /// <summary>The source the current card refers to, for history and logs.</summary>
        private TorrentSource? _torrentSource;

        /// <summary>Info hash reported by the helper, when metadata is available.</summary>
        private string _torrentInfoHash = "";

        /// <summary>File list from the last metadata event, used by the picker.</summary>
        private IReadOnlyList<TorrentFileEntry> _torrentFiles = Array.Empty<TorrentFileEntry>();

        /// <summary>
        /// Torrent name from the metadata event. Kept separately from the card
        /// title because localization re-applies labels to that title, and the
        /// picker header must still name the torrent the files belong to.
        /// </summary>
        private string _torrentName = "";

        /// <summary>True between the click and the helper's first state event.</summary>
        private bool _torrentStarting;

        /// <summary>True while a deliberate shutdown is in progress, so an exit is not an error.</summary>
        private bool _torrentStopping;

        /// <summary>Corner radius used by the borderless window chrome.</summary>
        private const double WindowCornerRadius = 8;

        /// <summary>
        /// Guards the install/uninstall buttons. A second click while a download
        /// or registry write is running would interleave two installs.
        /// </summary>
        private bool _isInstalling;

        public MainWindow()
        {
            InitializeComponent();

            AppPaths.EnsureLayout();
            _configService = new ConfigService();

            // On a first run the embedded templates are authoritative and are
            // written even if a file already exists; afterwards the user's copies
            // win so their edits survive.
            AppPaths.SeedTemplates(overwriteEmbedded: _configService.Config.FirstRun);

            // Older builds rewrote the browsers' launch commands to force the
            // unpacked extension in. Chrome refuses that switch now, so all it
            // left behind was a broken Start-menu launch and a warning bar over
            // every window. Strip it before this session touches the browser.
            ExtensionInstallService.RemoveLegacyLaunchOverrides();

            // A new exe carries a new browser extension. Refreshing it before
            // the window exists means the next browser start already runs the
            // matching scripts; the status line names the version afterwards.
            _refreshedExtensionVersion = ExtensionInstallService.RefreshIfOutdated();

            _locService = new LocalizationService(AppPaths.LanguagesDir, _configService.Config.Language);
            _themeService = new ThemeService(AppPaths.ThemesDir, _configService.Config.Theme);
            _procService = new ProcessService(_configService);
            _updateService = new UpdateService(_configService);

            // Anything the previous update renamed out of the way can go now:
            // the old image was locked while it was still running.
            UpdateService.CleanUpPreviousUpdate();
            _depService = new DependencyService(_procService);
            _browserService = new BrowserIntegrationService();
            _downloadService = new DownloadInstallService(_configService);
            _toolUpdateService = new ToolUpdateService(_procService, _depService, _downloadService);
            _modernZService = new ModernZAnime4kService();
            _torrentInstaller = new TorrentHelperInstallService(_configService);
            _torrentSession = new TorrentSessionService();

            // A previous run that crashed leaves session folders behind. They
            // are all under our cache directory and no other process can be
            // using them, because the helper exits with its launcher.
            TorrentSessionService.CleanStaleSessions();

            _torrentSession.EventReceived += e => Dispatcher.InvokeAsync(() => OnTorrentEvent(e));
            _torrentSession.Ready += e => Dispatcher.InvokeAsync(() => OnTorrentReady(e));
            _torrentSession.Exited += code => Dispatcher.InvokeAsync(() => OnTorrentExited(code));

            // Theme and language changes come from the settings pages, so the
            // window subscribes rather than being told.
            _locService.LanguageChanged += UpdateLocalization;
            _themeService.ThemeChanged += UpdateTheme;

            try
            {
                Icon = IconGenerator.CreateAppIcon(64);
                IconGenerator.EnsureIcons();
            }
            catch { }

            Loaded += MainWindow_Loaded;

            // Closing rather than Closed, because the purge helper has to be
            // started while this process is still the locked one it is waiting
            // on - Closed is too late, the process is already going.
            Closing += (_, _) => UpdateService.ScheduleOldExecutableRemoval();

            // The helper also notices a dead launcher by itself; this is only
            // the fast path so the port and the files go away immediately. It
            // must not block the UI thread while the helper shuts down.
            Closing += (_, _) => _ = _torrentSession.StopAsync(TimeSpan.FromSeconds(2));
        }

        /// <summary>
        /// Fills every control from the saved config, then shows what was found
        /// on disk. Runs on Loaded rather than in the constructor because the
        /// named controls do not exist until InitializeComponent has run and the
        /// window has a visual tree.
        /// </summary>
        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            // A config written before the palette was applied as a whole holds
            // the dark defaults for every field, which is why switching themes
            // used to leave the window looking the same. Repair that once by
            // taking the active theme's palette as the new baseline. It has to
            // happen before the first UpdateTheme, or the window paints with the
            // stale overrides and only corrects itself on the next change.
            if (_configService.Config.AppearanceSchema < 1)
            {
                UiTheme.AdoptTheme(_themeService.CurrentTheme, _configService.Config);
                _configService.Config.AppearanceSchema = 1;
                _configService.Save();
            }

            UpdateLocalization();
            UpdateTheme();
            LoadHistory();
            StartHistoryWatcher();

            // A history carried over from an older build has links where titles
            // belong. Fill the first few now, then leave the rest for later
            // starts; the list is short and this never blocks the window.
            _ = BackfillHistoryTitlesAsync();

            PopulateLanguages();
            PopulateThemes();
            InitializeReportPage();


            // ---- Playback settings ----
            TxtMpvPath.Text = _configService.Config.MpvPath;
            ChkAlwaysOnTop.IsChecked = _configService.Config.AlwaysOnTop;

            // The combo stores its mode in Tag, so the saved string is matched
            // back to an item rather than assigned directly.
            string fw = _configService.Config.ForceWindow ?? "immediate";
            foreach (ComboBoxItem item in CmbForceWindow.Items)
            {
                if (string.Equals(item.Tag?.ToString(), fw, StringComparison.OrdinalIgnoreCase))
                {
                    CmbForceWindow.SelectedItem = item;
                    break;
                }
            }

            TxtMpvGeometry.Text = _configService.Config.Geometry;
            TxtMpvProfile.Text = _configService.Config.MpvProfile;
            TxtMpvExtraArgs.Text = _configService.Config.ExtraArgs;

            SyncAppearanceControls();

            TxtInstallDir.Text = _locService.Get("tools_folder_label", "Tools folder:") + " " + AppPaths.BinDir;
            TxtDataFolderPath.Text = AppPaths.Root;
            TxtStatus.Text = _locService.Get("status_ready", "Ready");

            // An extension refreshed during startup only takes effect when the
            // browser restarts, so the status line names the version that
            // arrived - otherwise the new scripts look like they did nothing.
            if (!string.IsNullOrEmpty(_refreshedExtensionVersion))
            {
                TxtStatus.Text = _locService.Format(
                    "extension_refreshed",
                    "Browser extension updated to {0}. Restart your browser to use it.",
                    _refreshedExtensionVersion!);
            }

            PopulateTorrentSettings();

            _ = RefreshDependenciesAsync();
            _ = InitializeBrowserIntegrationAsync();
            _ = CheckForUpdatesInBackgroundAsync();

            RenderUpdateCard();
        }


        #region Window
        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
                DragMove();
        }

        private void BtnMinimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
        private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

        /**
         * Keeps the rounded corners in step with the window size.
         *
         * The window has no OS frame (WindowStyle=None), so the rounding is a
         * geometry clip applied to the content element. Re-clipping on resize is
         * what keeps the corners from becoming square as the window grows.
         */
        private void WindowContentClip_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            double r = _configService != null ? _configService.Config.WindowCornerRadius : WindowCornerRadius;
            WindowContentClip.Clip = new RectangleGeometry(
                new Rect(0, 0, e.NewSize.Width, e.NewSize.Height),
                r,
                r);
        }
        #endregion

        #region Navigation
        private Button? _activeNavButton;

        /**
         * Shows one panel and highlights its nav button.
         *
         * All five panels are declared in XAML and collapsed rather than created
         * on demand, so switching pages is a visibility toggle with no layout
         * cost. Every button is reset first because there is no "previous" state
         * to diff against - with only five entries the exhaustive reset is
         * clearer than tracking one.
         */
        private void SetActiveTab(StackPanel activePanel, Button activeButton)
        {
            _activeNavButton = activeButton;

            PanelPlayer.Visibility = Visibility.Collapsed;
            PanelTools.Visibility = Visibility.Collapsed;
            PanelSettings.Visibility = Visibility.Collapsed;
            PanelThemes.Visibility = Visibility.Collapsed;
            PanelGuide.Visibility = Visibility.Collapsed;
            PanelReport.Visibility = Visibility.Collapsed;

            BtnNavPlayer.Background = Brushes.Transparent;
            BtnNavTools.Background = Brushes.Transparent;
            BtnNavSettings.Background = Brushes.Transparent;
            BtnNavThemes.Background = Brushes.Transparent;
            BtnNavGuide.Background = Brushes.Transparent;
            BtnNavReport.Background = Brushes.Transparent;

            var ui = UiTheme.Current;
            BtnNavPlayer.Foreground = ui.TextSecondary;
            BtnNavTools.Foreground = ui.TextSecondary;
            BtnNavSettings.Foreground = ui.TextSecondary;
            BtnNavThemes.Foreground = ui.TextSecondary;
            BtnNavGuide.Foreground = ui.TextSecondary;
            BtnNavReport.Foreground = ui.TextSecondary;

            activePanel.Visibility = Visibility.Visible;
            activeButton.Background = ui.Accent;
            activeButton.Foreground = ui.OnAccent;
        }

        private void BtnNavPlayer_Click(object sender, RoutedEventArgs e) => SetActiveTab(PanelPlayer, BtnNavPlayer);
        private void BtnNavTools_Click(object sender, RoutedEventArgs e) => SetActiveTab(PanelTools, BtnNavTools);
        private void BtnNavSettings_Click(object sender, RoutedEventArgs e) => SetActiveTab(PanelSettings, BtnNavSettings);
        private void BtnNavThemes_Click(object sender, RoutedEventArgs e) => SetActiveTab(PanelThemes, BtnNavThemes);
        private void BtnNavGuide_Click(object sender, RoutedEventArgs e) => SetActiveTab(PanelGuide, BtnNavGuide);
        private void BtnNavReport_Click(object sender, RoutedEventArgs e) => SetActiveTab(PanelReport, BtnNavReport);
        #endregion

    }
}
