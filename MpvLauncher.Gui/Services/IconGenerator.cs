using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MpvLauncher.Gui.Services
{
    public static class IconGenerator
    {
        public static void EnsureIcons()
        {
            try
            {
                // Sadece AppData altındaki eklenti klasörlerine ikonları yaz
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
                    foreach (int size in new[] { 16, 32, 48, 128 })
                    {
                        string path = Path.Combine(dir, $"icon{size}.png");
                        if (!File.Exists(path))
                        {
                            SaveIconPng(size, path);
                        }
                    }
                }
            }
            catch { }
        }

        public static void SaveIco(string targetPath, int[] sizes)
        {
            try
            {
                var pngStreams = new List<byte[]>();
                foreach (int s in sizes)
                {
                    byte[] png = RenderPngBytes(s);
                    if (png.Length > 0) pngStreams.Add(png);
                }

                if (pngStreams.Count == 0) return;

                Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                using var fs = File.Create(targetPath);
                using var bw = new BinaryWriter(fs);

                // ICO Header
                bw.Write((short)0); // Reserved
                bw.Write((short)1); // Type = 1 (ICO)
                bw.Write((short)pngStreams.Count); // Image count

                int offset = 6 + (16 * pngStreams.Count);

                for (int i = 0; i < pngStreams.Count; i++)
                {
                    int size = sizes[i];
                    byte bSize = size >= 256 ? (byte)0 : (byte)size;

                    bw.Write(bSize); // Width
                    bw.Write(bSize); // Height
                    bw.Write((byte)0); // Colors
                    bw.Write((byte)0); // Reserved
                    bw.Write((short)1); // Color planes
                    bw.Write((short)32); // Bit count
                    bw.Write(pngStreams[i].Length); // Bytes in res
                    bw.Write(offset); // Image offset

                    offset += pngStreams[i].Length;
                }

                foreach (var png in pngStreams)
                {
                    bw.Write(png);
                }
            }
            catch { }
        }

        private static byte[] RenderPngBytes(int size)
        {
            try
            {
                var visual = new DrawingVisual();
                using (var dc = visual.RenderOpen())
                {
                    double radius = size * 0.22;
                    var bgBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#6366F1"));
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

                var rtb = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(visual);

                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(rtb));

                using var ms = new MemoryStream();
                encoder.Save(ms);
                return ms.ToArray();
            }
            catch
            {
                return Array.Empty<byte>();
            }
        }

        public static void SaveIconPng(int size, string targetFile)
        {
            try
            {
                var visual = new DrawingVisual();
                using (var dc = visual.RenderOpen())
                {
                    double radius = size * 0.22;
                    var bgBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#6366F1"));
                    bgBrush.Freeze();

                    // Arka plan yuvarlak kare
                    dc.DrawRoundedRectangle(bgBrush, null, new Rect(0, 0, size, size), radius, radius);

                    // Beyaz Play Üçgeni
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

                var rtb = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(visual);

                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(rtb));

                Directory.CreateDirectory(Path.GetDirectoryName(targetFile)!);
                using var fs = File.Create(targetFile);
                encoder.Save(fs);
            }
            catch { }
        }

        public static ImageSource CreateAppIcon(int size = 48)
        {
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                double radius = size * 0.22;
                var bgBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#6366F1"));
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

            var rtb = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(visual);

            // BitmapFrame olarak wrap et — WPF WindowStyle=None modunda
            // taskbar ve ALT+TAB'da ikonun görünmesi için gerekli
            var frame = BitmapFrame.Create(rtb);
            frame.Freeze();
            return frame;
        }
    }
}
