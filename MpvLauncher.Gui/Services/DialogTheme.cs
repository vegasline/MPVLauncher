// Original implementation, MIT licensed.

using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace MpvLauncher.Gui.Services
{
    /// <summary>
    /// Gives a window that is built in code the active palette.
    ///
    /// The styles live in the main window's resource dictionary, so a second
    /// window has no way to resolve "AccentBrush" on its own. Copying the
    /// palette into the dialog's own resources makes the same DynamicResource
    /// references work there, and keeps the dialog in step when the user
    /// changes the theme while it is open.
    /// </summary>
    public static class DialogTheme
    {
        private static readonly string[] ButtonStyles = { "ModernButton", "SecondaryButton", "DangerButton" };

        /// <summary>
        /// Applies the palette and keeps following it until the window closes.
        /// Call after the owner is assigned, before the window is shown.
        /// </summary>
        public static void Apply(Window window)
        {
            if (window == null) return;

            CopyStyles(window);
            CopyPalette(window);

            if (window.FontFamily == null || string.IsNullOrEmpty(window.FontFamily.Source))
                window.FontFamily = new FontFamily("Segoe UI, Inter, Arial");

            // Callers apply once at construction and again when the owner is
            // known; only the first call may subscribe, or every theme change
            // would run the handler twice.
            if (window.Resources.Contains("__DialogThemeHooked")) return;
            window.Resources["__DialogThemeHooked"] = true;

            UseDarkTitleBar(window);

            Action follow = () => CopyPalette(window);
            UiTheme.PaletteChanged += follow;
            window.Closed += (_, _) => UiTheme.PaletteChanged -= follow;
        }

        /// <summary>
        /// Asks DWM for the dark caption. A themed dialog with a white title bar
        /// looks like a window from another program, and WPF has no property for
        /// it. The attribute number moved from 19 to 20 in Windows 10 20H1, so
        /// both are tried and a failure is silent (older builds just stay light).
        /// </summary>
        private static void UseDarkTitleBar(Window window)
        {
            window.SourceInitialized += (_, _) =>
            {
                try
                {
                    IntPtr handle = new WindowInteropHelper(window).Handle;
                    int enabled = 1;
                    if (DwmSetWindowAttribute(handle, 20, ref enabled, sizeof(int)) != 0)
                        DwmSetWindowAttribute(handle, 19, ref enabled, sizeof(int));
                }
                catch
                {
                    // Not fatal: the caption simply keeps the system colour.
                }
            };
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        /// <summary>
        /// The style a button should use. A dialog constructed in code has no
        /// owner yet, so the main window is asked directly - its dictionary is
        /// where the styles live, and the palette copy in this window is what
        /// those styles resolve their brushes against.
        /// </summary>
        public static Style? Style(Window window, string key)
        {
            if (window.Resources[key] is Style local) return local;
            if (window.Owner?.Resources[key] is Style owned) return owned;

            // A dialog is built before its Owner is assigned and before the
            // window is registered, so Application.Current.MainWindow is not
            // reliable here either. Any open window with the key will do: the
            // styles are identical everywhere they are copied from.
            var app = Application.Current;
            if (app != null)
            {
                foreach (Window open in app.Windows)
                {
                    if (open.Resources[key] is Style style)
                    {
                        window.Resources[key] = style;
                        return style;
                    }
                }
            }

            return null;
        }

        private static void CopyStyles(Window window)
        {
            var source = window.Owner?.Resources;
            if (source == null) return;

            foreach (string key in ButtonStyles)
            {
                if (source[key] is Style style) window.Resources[key] = style;
            }

            // Implicit styles are keyed by type, which is how the dialog's
            // scrollbar and list pick up the themed template.
            foreach (object key in new object[] { typeof(System.Windows.Controls.Primitives.ScrollBar), typeof(System.Windows.Controls.ListBoxItem) })
            {
                if (source[key] is Style style) window.Resources[key] = style;
            }
        }

        private static void CopyPalette(Window window)
        {
            UiPalette palette = UiTheme.Current;
            ResourceDictionary target = window.Resources;

            target["AccentBrush"] = palette.Accent;
            target["AccentHoverBrush"] = palette.AccentHover;
            target["OnAccentBrush"] = palette.OnAccent;
            target["TextPrimaryBrush"] = palette.TextPrimary;
            target["TextSecondaryBrush"] = palette.TextSecondary;
            target["TextMutedBrush"] = palette.TextMuted;
            target["CardBrush"] = palette.Card;
            target["CardBorderBrush"] = palette.CardBorder;
            target["HairlineBrush"] = palette.Hairline;
            target["SurfaceBrush"] = palette.Surface;
            target["InputBrush"] = palette.Input;
            target["PopupBrush"] = palette.Popup;
            target["WindowBrush"] = palette.Window;
            target["WindowBorderBrush"] = palette.WindowBorder;
            target["SuccessBrush"] = palette.Success;
            target["WarningBrush"] = palette.Warning;
            target["DangerBrush"] = palette.Danger;
            target["SecondaryButtonBrush"] = palette.SecondaryButton;
            target["DangerButtonBrush"] = palette.DangerButton;
            target["HoverBrush"] = palette.Surface;
            target["ButtonOpacity"] = palette.ButtonOpacity;

            // A transparent window draws its own chrome; forcing an opaque
            // background here would paint out the rounded shell and shadow.
            if (!window.AllowsTransparency)
            {
                window.Background = palette.Popup;
            }

            window.Foreground = palette.TextPrimary;
        }
    }
}
