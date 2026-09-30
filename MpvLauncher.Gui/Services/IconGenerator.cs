using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MpvLauncher.Gui.Services
{
    /// <summary>
    /// Draws the application icon and writes out the sizes browsers and Windows
    /// ask for.
    ///
    /// There is no image asset: the mark is composed in WPF at runtime - an
    /// indigo rounded square with a white play triangle - so it stays crisp at
    /// any size and adds nothing to the build output.
    /// </summary>
    public static class IconGenerator
    {
        /// <summary>Brand colour, matching the default accent in the shipped themes.</summary>
        private const string AccentHex = "#6366F1";

        /// <summary>Sizes an extension manifest is expected to provide.</summary>
        private static readonly int[] ExtensionIconSizes = { 16, 32, 48, 128 };

        /// <summary>
        /// Writes icon16/32/48/128.png into the extension folders.
        ///
        /// Existing files are left alone: a user who replaced the icon keeps it.
        /// Only the app's own %APPDATA% folders are touched, never a folder
        /// inside a browser profile.
        /// </summary>
        public static void EnsureIcons()
        {
            try
            {
                var targetDirs = new[]
                {
                    AppPaths.ExtensionBundleDir,
                    AppPaths.ChromiumExtensionDir,
                    AppPaths.FirefoxExtensionDir
                };

                foreach (var dir in targetDirs)
                {
                    if (string.IsNullOrWhiteSpace(dir)) continue;
                    Directory.CreateDirectory(dir);
                    foreach (int size in ExtensionIconSizes)
                    {
                        string path = Path.Combine(dir, $"icon{size}.png");
                        if (!File.Exists(path)) SaveIconPng(size, path);
                    }
                }
            }
            catch { /* an icon is never worth failing an install over */ }
        }

        /// <summary>
        /// Packs rendered PNGs into a multi-resolution .ico.
        ///
        /// An ICO is a small header, then one 16-byte directory entry per image,
        /// then the image bytes. PNG-compressed entries are what Windows expects
        /// for 256px, and are accepted for every size here.
        /// </summary>
        public static void SaveIco(string targetPath, int[] sizes)
        {
            try
            {
                // Sizes and payloads are kept in step: if one render fails it is
                // skipped from both, otherwise the directory entry would declare
                // the wrong dimensions for the bytes that follow it.
                var pngStreams = new List<byte[]>();
                var keptSizes = new List<int>();
                foreach (int s in sizes)
                {
                    byte[] png = RenderPngBytes(s);
                    if (png.Length == 0) continue;
                    pngStreams.Add(png);
                    keptSizes.Add(s);
                }

                if (pngStreams.Count == 0) return;

                Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                using var fs = File.Create(targetPath);
                using var bw = new BinaryWriter(fs);

                // ICO header: reserved, type (1 = icon), image count.
                bw.Write((short)0);
                bw.Write((short)1);
                bw.Write((short)pngStreams.Count);

                // The first image starts right after the directory.
                int offset = 6 + (16 * pngStreams.Count);

                for (int i = 0; i < pngStreams.Count; i++)
                {
                    int size = keptSizes[i];
                    // 0 means 256 in this field; there is no room for 256 itself.
                    byte bSize = size >= 256 ? (byte)0 : (byte)size;

                    bw.Write(bSize);                    // width
                    bw.Write(bSize);                    // height
                    bw.Write((byte)0);                  // palette size
                    bw.Write((byte)0);                  // reserved
                    bw.Write((short)1);                 // colour planes
                    bw.Write((short)32);                // bits per pixel
                    bw.Write(pngStreams[i].Length);     // bytes in this image
                    bw.Write(offset);                   // where that image starts

                    offset += pngStreams[i].Length;
                }

                foreach (var png in pngStreams) bw.Write(png);
            }
            catch { }
        }

        /// <summary>Renders the mark and encodes it as PNG bytes.</summary>
        private static byte[] RenderPngBytes(int size)
        {
            try
            {
                using var ms = new MemoryStream();
                SavePng(size, ms);
                return ms.ToArray();
            }
            catch
            {
                return Array.Empty<byte>();
            }
        }

        /// <summary>Writes the mark to disk as a PNG of the given size.</summary>
        public static void SaveIconPng(int size, string targetFile)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(targetFile)!);
                using var fs = File.Create(targetFile);
                SavePng(size, fs);
            }
            catch { }
        }

        /// <summary>
        /// The icon as a WPF image source, for the window and the taskbar.
        ///
        /// The result is wrapped in a frozen BitmapFrame rather than handed over
        /// as a raw RenderTargetBitmap: with WindowStyle=None the icon is taken
        /// from the taskbar entry and the Alt+Tab list, and those run on a
        /// different thread than the UI. Freezing makes it safe to pass across.
        /// </summary>
        public static ImageSource CreateAppIcon(int size = 48)
        {
            var rtb = RenderVisual(size);
            var frame = BitmapFrame.Create(rtb);
            frame.Freeze();
            return frame;
        }

        /// <summary>Composites the mark: rounded square plus play triangle.</summary>
        private static DrawingVisual BuildVisual(int size)
        {
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                // Everything is expressed as a fraction of the requested size so
                // one routine covers 16px favicons and 256px shell icons alike.
                double radius = size * 0.22;
                var bgBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(AccentHex));
                bgBrush.Freeze();

                dc.DrawRoundedRectangle(bgBrush, null, new Rect(0, 0, size, size), radius, radius);

                var playBrush = Brushes.White;
                double left = size * 0.35;
                double top = size * 0.28;
                double right = size * 0.72;
                double bottom = size * 0.72;
                double midY = size * 0.50;

                var pathGeo = new PathGeometry();
                var fig = new PathFigure { StartPoint = new Point(left, top), IsClosed = true };
                fig.Segments.Add(new LineSegment(new Point(right, midY), true));
                fig.Segments.Add(new LineSegment(new Point(left, bottom), true));
                pathGeo.Figures.Add(fig);
                pathGeo.Freeze();

                dc.DrawGeometry(playBrush, null, pathGeo);
            }
            return visual;
        }

        /// <summary>Composites and rasterises the mark at 96 DPI.</summary>
        private static RenderTargetBitmap RenderVisual(int size)
        {
            var rtb = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(BuildVisual(size));
            return rtb;
        }

        /// <summary>Encodes the mark at the given size as PNG into a stream.</summary>
        private static void SavePng(int size, Stream target)
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(RenderVisual(size)));
            encoder.Save(target);
        }
    }
}
