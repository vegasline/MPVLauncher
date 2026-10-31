using MpvLauncher.Gui.Services;
using Xunit;

namespace MpvLauncher.Gui.Tests
{
    /// <summary>
    /// The command-line surgery. It has to add one argument and leave every
    /// other character exactly as it was: the older build's bug was rewriting a
    /// "%1" placeholder, and a broken placeholder turns every Start-menu launch
    /// into a window that opens a file called "%1".
    /// </summary>
    public class ChromiumLaunchFlagTests
    {
        private const string Dir = @"C:\Users\tester\AppData\Roaming\MPVLauncher\extensions\chromium";
        private const string Flag = "--load-extension=\"C:\\Users\\tester\\AppData\\Roaming\\MPVLauncher\\extensions\\chromium\"";

        [Fact]
        public void The_flag_goes_after_the_executable_and_everything_else_survives()
        {
            string command = "\"C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe\" --single-argument %1";

            string updated = ChromiumLaunchFlag.InsertAfterExe(command, ChromiumLaunchFlag.Argument(Dir));

            Assert.Equal("\"C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe\" " + Flag + " --single-argument %1", updated);
        }

        [Fact]
        public void An_unquoted_command_keeps_its_arguments()
        {
            string updated = ChromiumLaunchFlag.InsertAfterExe("/usr/bin/browser --foo %1", "--load-extension=\"x\"");

            Assert.Equal("/usr/bin/browser --load-extension=\"x\" --foo %1", updated);
        }

        [Fact]
        public void A_command_with_no_arguments_just_gains_the_flag()
        {
            Assert.Equal("\"C:\\b.exe\" " + Flag, ChromiumLaunchFlag.InsertAfterExe("\"C:\\b.exe\"", Flag));
        }

        [Fact]
        public void The_legacy_single_argument_pair_is_dropped_but_the_placeholder_stays()
        {
            string updated = ChromiumLaunchFlag.AddFlag("\"C:\\b.exe\" --single-argument %1", Dir);

            Assert.Equal("\"C:\\b.exe\" " + Flag + " %1", updated);
            Assert.DoesNotContain("--single-argument", updated);
            Assert.Equal(1, updated.Split('%').Length - 1);
        }

        [Fact]
        public void Adding_twice_is_the_same_as_adding_once()
        {
            string once = ChromiumLaunchFlag.AddFlag("\"C:\\b.exe\" %1", Dir);
            string twice = ChromiumLaunchFlag.AddFlag(once, Dir);

            Assert.Equal(once, twice);
        }

        [Fact]
        public void Removing_puts_the_line_back()
        {
            string flagged = ChromiumLaunchFlag.AddFlag("\"C:\\b.exe\" %1", Dir);

            Assert.Equal("\"C:\\b.exe\" %1", ChromiumLaunchFlag.RemoveFlag(flagged, Dir));
            Assert.False(ChromiumLaunchFlag.HasFlag(ChromiumLaunchFlag.RemoveFlag(flagged, Dir), Dir));
        }
    }
}
