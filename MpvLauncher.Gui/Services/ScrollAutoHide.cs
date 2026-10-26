// Original implementation, MIT licensed.

using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace MpvLauncher.Gui.Services
{
    /// <summary>
    /// Hides a scroll viewer's bars until the content actually moves.
    ///
    /// A permanently visible bar on a translucent card is a bright stripe that
    /// does not belong to the theme, and on short lists it is pure noise. The
    /// bars stay hit-testable while invisible, so the mouse can still grab one
    /// by moving to the edge, and they return the moment the wheel is used.
    /// </summary>
    public static class ScrollAutoHide
    {
        public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
            "Enabled",
            typeof(bool),
            typeof(ScrollAutoHide),
            new PropertyMetadata(false, OnEnabledChanged));

        public static void SetEnabled(DependencyObject element, bool value) => element.SetValue(EnabledProperty, value);

        public static bool GetEnabled(DependencyObject element) => (bool)element.GetValue(EnabledProperty);

        private static readonly Dictionary<ScrollViewer, DispatcherTimer> Timers = new();

        private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not ScrollViewer viewer || e.NewValue is not true) return;
            if (Timers.ContainsKey(viewer)) return;

            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                if (!AnyBarHovered(viewer)) Hide(viewer);
            };
            Timers[viewer] = timer;

            // Collapsed until something happens: the initial render of a long
            // page must not flash a bar.
            viewer.Loaded += (_, _) =>
            {
                Apply(viewer, 0);
                Reveal(viewer);
            };
            viewer.Unloaded += (_, _) =>
            {
                timer.Stop();
                Timers.Remove(viewer);
            };

            viewer.ScrollChanged += (_, args) =>
            {
                if (args.VerticalChange != 0 || args.HorizontalChange != 0 || args.ExtentHeightChange != 0)
                {
                    Reveal(viewer);
                }
            };

            viewer.PreviewMouseWheel += (_, _) => Reveal(viewer);
            viewer.PreviewMouseMove += (_, _) =>
            {
                // Keep the bars up while the pointer is on them; otherwise a
                // user reaching for the thumb watches it fade away.
                if (AnyBarHovered(viewer)) Reveal(viewer);
            };
        }

        private static void Reveal(ScrollViewer viewer)
        {
            Apply(viewer, 1);
            if (Timers.TryGetValue(viewer, out var timer))
            {
                timer.Stop();
                timer.Start();
            }
        }

        private static void Hide(ScrollViewer viewer)
        {
            Apply(viewer, 0);
        }

        private static void Apply(ScrollViewer viewer, double target)
        {
            foreach (var bar in Bars(viewer))
            {
                var animation = new DoubleAnimation(target, TimeSpan.FromMilliseconds(target > 0 ? 120 : 260))
                {
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                };
                bar.BeginAnimation(UIElement.OpacityProperty, animation);
            }
        }

        private static bool AnyBarHovered(ScrollViewer viewer)
        {
            foreach (var bar in Bars(viewer))
            {
                if (bar.IsMouseOver) return true;
            }
            return false;
        }

        private static IEnumerable<ScrollBar> Bars(DependencyObject root)
        {
            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is ScrollBar bar)
                {
                    yield return bar;
                }
                else
                {
                    foreach (var nested in Bars(child))
                    {
                        yield return nested;
                    }
                }
            }
        }
    }
}
