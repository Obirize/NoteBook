using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Notlar;

static partial class Program
{
    static int checks;
    const string Password = "test-only long passphrase 927";
    static void Check(bool ok, string name) { if (!ok) throw new Exception(name); checks++; Console.WriteLine("PASS " + name); }
    static void Reject(Action action, string name) { bool rejected = false; try { action(); } catch { rejected = true; } Check(rejected, name); }
    static void dirty(MainWindow w) => w.MarkChanged();
    // With NOTLAR_SHOT=<folder> set, windows are saved as PNGs at interesting moments (a way to look at the UI without a mouse).
    static void Snapshot(Window window, string name)
    {
        var dir = Environment.GetEnvironmentVariable("NOTLAR_SHOT"); if (string.IsNullOrEmpty(dir)) return;
        Pump(); var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(window);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(dir); using var file = File.Create(Path.Combine(dir, name + ".png")); encoder.Save(file);
    }
    static void Pump() { var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false)); Dispatcher.PushFrame(frame); }
    static T Find<T>(Window window, string name) where T : class => (T)window.FindName(name);
    static void Click(Window window, string name) { Find<Button>(window, name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(); }
    static async Task Until(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (!condition()) { if (watch.Elapsed > TimeSpan.FromSeconds(10)) throw new Exception("UI operation timed out"); await Task.Delay(30); }
    }
    static void Wheel(UIElement target, int delta) => target.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta) { RoutedEvent = UIElement.PreviewMouseWheelEvent });
    static double Settle(Func<double> offset)
    {
        // Smooth scrolling animates over a few frames; wait until the offset stops changing.
        var watch = Stopwatch.StartNew(); double last = double.NaN; int stable = 0;
        while (watch.ElapsedMilliseconds < 3000) { Pump(); Thread.Sleep(16); double value = offset(); if (value == last && ++stable >= 8) break; if (value != last) stable = 0; last = value; }
        return last;
    }
    static IEnumerable<T> Visuals<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typed) yield return typed;
            foreach (var descendant in Visuals<T>(child)) yield return descendant;
        }
    }

    [STAThread] static int Main(string[] args)
    {
        string root = Path.Combine(Path.GetTempPath(), "Notlar-tests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var core = CoreChecks(root);
            using var session = core.Session;
            WindowChecks(root, session, core.Path, core.Book);
            Console.WriteLine("PASS TOTAL: " + checks);
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { try { Directory.Delete(root, true); } catch { } }
    }
}

