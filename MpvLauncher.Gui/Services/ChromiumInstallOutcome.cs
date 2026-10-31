// Original implementation, MIT licensed.

namespace MpvLauncher.Gui.Services
{
    /// <summary>
    /// What installing into one picked browser actually achieved.
    ///
    /// Kept apart from the all-browsers result because the interesting part is
    /// per browser: a Chromium build that refuses the launch switch gets files
    /// and a native host and nothing else, and the UI has to be able to say that
    /// instead of showing a green tick.
    /// </summary>
    public sealed class ChromiumInstallOutcome
    {
        public bool Success { get; init; }

        public string Error { get; init; } = "";

        /// <summary>The native host manifest written for Chromium.</summary>
        public string ManifestPath { get; init; } = "";

        /// <summary>Whether the Start-menu command now carries the load switch.</summary>
        public bool CommandWritten { get; init; }

        /// <summary>How many ProgID commands (the link-opening ones) were rewritten.</summary>
        public int ProgIdsPatched { get; init; }

        /// <summary>How many shortcuts were rewritten to carry it.</summary>
        public int ShortcutsPatched { get; init; }

        /// <summary>Whether the browser already reports our extension as loaded.</summary>
        public bool Loaded { get; init; }
    }
}
