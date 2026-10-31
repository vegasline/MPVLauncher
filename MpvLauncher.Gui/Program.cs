using System;
using System.Threading;
using MpvLauncher.Gui.Services;

namespace MpvLauncher.Gui
{
    /// <summary>
    /// Single entry point for the executable. The same binary serves two roles:
    /// the desktop GUI, and the native messaging host the browser extension talks
    /// to. Which one runs is decided before any window is created, because in
    /// host mode there is no UI and the process talks over stdin/stdout.
    /// </summary>
    public static class Program
    {
        // "Local\" scopes the name to this Windows session, so a second user on
        // the same machine still gets their own window.
        private const string InstanceMutexName = @"Local\MPVLauncher.SingleInstance";
        private const string ActivationEventName = @"Local\MPVLauncher.Activate";

        [STAThread]
        public static void Main(string[] args)
        {
            // Both roles need the data folders to exist first: the host writes
            // its log there, and the GUI reads its templates from there.
            AppPaths.EnsureLayout();

            // Host mode is checked first and returns without ever creating a
            // window. See NativeHostService.IsHostMode for how a legitimate
            // caller is recognised.
            if (NativeHostService.IsHostMode(args))
            {
                NativeHostService.Run(args);
                return;
            }

            // The updater starts this binary beside the copy it is replacing and
            // asks it to delete the old exe. That process must not be turned
            // into "focus the window that is already open", so it skips the
            // single-instance check entirely.
            bool maintenance = args.Length == 2 && args[0] == UpdateService.PurgeArgument;

            Mutex? mutex = null;
            bool firstInstance = true;
            if (!maintenance)
            {
                mutex = new Mutex(initiallyOwned: true, InstanceMutexName, out firstInstance);

                if (!firstInstance)
                {
                    // Another copy owns the window: ask it to come forward and
                    // leave this one to exit. A missing event only means the
                    // other process is still starting up, which is harmless.
                    try
                    {
                        EventWaitHandle.OpenExisting(ActivationEventName).Set();
                    }
                    catch (Exception)
                    {
                        // Nothing to focus; exiting is still the right answer.
                    }

                    mutex.Dispose();
                    return;
                }
            }

            // Extract only what is missing, so a user-edited file in
            // %APPDATA% is not overwritten on every start.
            ResourceSeeder.ExtractMissing();

            var app = new App();
            app.InitializeComponent();

            EventWaitHandle? activation = null;
            if (!maintenance)
            {
                // The first copy listens for the event a second launch sets. The
                // wait runs on a background thread and hops to the UI thread,
                // because the event also arrives while a dialog is open.
                activation = new EventWaitHandle(false, EventResetMode.AutoReset, ActivationEventName);
                var listener = new Thread(() =>
                {
                    while (activation.WaitOne())
                    {
                        app.Dispatcher.BeginInvoke(new Action(() => app.ActivateMainWindow()));
                    }
                })
                {
                    IsBackground = true,
                    Name = "MPVLauncher activation listener"
                };
                listener.Start();
            }

            app.Run();

            activation?.Dispose();
            mutex?.Dispose();
        }
    }
}
