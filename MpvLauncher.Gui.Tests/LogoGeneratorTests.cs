// Original implementation, MIT licensed.

using System.IO;
using System.Threading;
using MpvLauncher.Gui.Services;
using Xunit.Sdk;

namespace MpvLauncher.Gui.Tests;

/// <summary>
/// The launcher draws its icon at runtime, so a broken renderer would only
/// show up as an empty taskbar button. These tests exercise the generator on
/// an STA thread, the apartment WPF's bitmap classes require.
/// </summary>
public class LogoGeneratorTests
{
    private static void OnStaThread(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ex; }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure != null) throw new XunitException(failure.ToString());
    }

    private static string TempIconPath()
    {
        string dir = Path.Combine(Path.GetTempPath(), "mpvlauncher-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "icon.ico");
    }

    [Fact]
    public void SaveIco_writes_a_readable_multi_size_icon()
    {
        string path = TempIconPath();

        try
        {
            OnStaThread(() => IconGenerator.SaveIco(path, new[] { 16, 32, 48 }));

            byte[] bytes = File.ReadAllBytes(path);

            // ICONDIR: reserved 0, type 1 (icon), then the image count.
            Assert.Equal(0, bytes[0]);
            Assert.Equal(0, bytes[1]);
            Assert.Equal(1, bytes[2]);
            Assert.Equal(0, bytes[3]);
            Assert.Equal(3, bytes[4]);
            Assert.Equal(0, bytes[5]);

            // A 48px PNG frame is far more than an empty entry would be.
            Assert.True(bytes.Length > 2048, $"icon was only {bytes.Length} bytes");
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Theory]
    [InlineData("Neon Blossom", "neon_blossom")]
    [InlineData("  My theme  ", "my_theme")]
    [InlineData("Halloween Night", "halloween_night")]
    [InlineData("../../etc/passwd", "etcpasswd")]
    [InlineData("Türkçe Tema", "türkçe_tema")]
    [InlineData("!!!", "")]
    public void Slug_turns_a_theme_name_into_a_safe_file_name(string name, string expected)
    {
        Assert.Equal(expected, ThemeService.Slug(name));
    }
}
