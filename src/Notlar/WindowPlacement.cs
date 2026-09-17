using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Notlar;

// A borderless (WindowStyle=None) window maximizes over the taskbar and 8 px past every screen edge by default.
// Answering WM_GETMINMAXINFO pins the maximized size and position to the monitor's work area instead.
public static class WindowPlacement
{
    private const int WM_GETMINMAXINFO = 0x0024, MONITOR_DEFAULTTONEAREST = 2;
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MINMAXINFO { public POINT ptReserved, ptMaxSize, ptMaxPosition, ptMinTrackSize, ptMaxTrackSize; }
    [StructLayout(LayoutKind.Sequential)] private struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public int dwFlags; }
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    public static void Attach(Window window)
    {
        window.SourceInitialized += (_, _) => HwndSource.FromHwnd(new WindowInteropHelper(window).Handle)?.AddHook(Hook);
    }
    public static bool TryGetWorkArea(IntPtr hwnd, out RECT work, out RECT monitor)
    {
        work = monitor = default;
        IntPtr handle = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (handle == IntPtr.Zero || !GetMonitorInfo(handle, ref info)) return false;
        work = info.rcWork; monitor = info.rcMonitor; return true;
    }
    private static IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_GETMINMAXINFO || !TryGetWorkArea(hwnd, out var work, out var monitor)) return IntPtr.Zero;
        var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
        mmi.ptMaxPosition = new POINT { X = work.Left - monitor.Left, Y = work.Top - monitor.Top };
        mmi.ptMaxSize = new POINT { X = work.Right - work.Left, Y = work.Bottom - work.Top };
        Marshal.StructureToPtr(mmi, lParam, true);
        handled = true;
        return IntPtr.Zero;
    }
}
