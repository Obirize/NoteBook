using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Notlar;

public static class VisualTreeExtensions
{
    public static T? FindAncestor<T>(this DependencyObject node) where T : DependencyObject
    {
        for (DependencyObject? current = node; current != null; current = current is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current))
            if (current is T match) return match;
        return null;
    }
}

// Shared by every ScrollViewer, including the editor's content host.
public static class SmoothScroll
{
    // One wheel notch (120 delta) scrolls the Windows "lines per notch" setting worth of editor lines (28 px each).
    public const double LinePixels = 28;
    public static double NotchPixels { get; } = LinePixels * (SystemParameters.WheelScrollLines > 0 ? SystemParameters.WheelScrollLines : 3);
    // How far ahead of the current position rapid input may queue before it is capped.
    public static double MaxLead => NotchPixels * 3;
    // Multiplies the notch distance; inherited, so it can be set on a ListBox for its inner ScrollViewer.
    public static readonly DependencyProperty ScaleProperty = DependencyProperty.RegisterAttached("Scale", typeof(double), typeof(SmoothScroll), new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.Inherits));
    public static double GetScale(DependencyObject obj) => (double)obj.GetValue(ScaleProperty);
    public static void SetScale(DependencyObject obj, double value) => obj.SetValue(ScaleProperty, value);
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached("Enabled", typeof(bool), typeof(SmoothScroll), new PropertyMetadata(false, Changed));
    public static bool GetEnabled(DependencyObject obj) => (bool)obj.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject obj, bool value) => obj.SetValue(EnabledProperty, value);
    private static void Changed(DependencyObject obj, DependencyPropertyChangedEventArgs e)
    { if (obj is ScrollViewer viewer && (bool)e.NewValue) _ = new Motion(viewer); }
    private sealed class Motion
    {
        private readonly ScrollViewer viewer;
        private bool active;
        private double target, position;
        private long last;
        public Motion(ScrollViewer viewer)
        {
            this.viewer = viewer;
            viewer.PreviewMouseWheel += Wheel;
            viewer.PreviewMouseDown += (_, _) => Stop();
            viewer.PreviewKeyDown += (_, _) => Stop();
            viewer.Unloaded += (_, _) => Stop();
            // Extent and viewport changes are handled by clamping every frame; stopping here would stutter virtualized lists.
        }
        private void Wheel(object sender, MouseWheelEventArgs e)
        {
            if (e.Handled || Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) return;
            e.Handled = true;
            double delta = -e.Delta * (NotchPixels * GetScale(viewer) / 120);
            if (!active) { position = target = viewer.VerticalOffset; }
            if (Math.Sign(delta) != Math.Sign(target - position)) target = position;
            // Do not accumulate a long inertial tail after rapid wheel input.
            target = Math.Clamp(target + delta, Math.Max(0, position - MaxLead * GetScale(viewer)), Math.Min(viewer.ScrollableHeight, position + MaxLead * GetScale(viewer)));
            if (active) return;
            active = true; last = Stopwatch.GetTimestamp(); CompositionTarget.Rendering += Frame;
        }
        private void Frame(object? sender, EventArgs e)
        {
            long now = Stopwatch.GetTimestamp();
            double dt = Math.Clamp((now - last) / (double)Stopwatch.Frequency, 0, 0.05); last = now;
            target = Math.Clamp(target, 0, viewer.ScrollableHeight);
            position += (target - position) * (1 - Math.Exp(-dt / 0.06));
            // Whole pixels keep text crisp and avoid sub-pixel jitter between frames.
            if (Math.Abs(target - position) < 0.5) { position = target; viewer.ScrollToVerticalOffset(Math.Round(position)); Stop(); }
            else viewer.ScrollToVerticalOffset(Math.Round(position));
        }
        private void Stop() { if (!active) return; active = false; CompositionTarget.Rendering -= Frame; }
    }
}
