using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Forms;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;

namespace Notlar;

// Closing the window hides it: the app keeps running in the notification area so the phone can still sync,
// and "Exit" there is the only way out. The installer can register "--minimized" to start straight into the tray.
public partial class MainWindow
{
    private NotifyIcon? tray;
    private ContextMenu? trayMenu;
    private Window? trayHelper;
    private bool exiting;
    private AppSettings settings = null!;

    private void InitializeTray()
    {
        settings = AppSettings.Load(Path.GetDirectoryName(session.FilePath)!);
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Notlar.notes.ico");
        tray = new NotifyIcon { Text = L10n.T("AppName"), Visible = true, Icon = stream != null ? new System.Drawing.Icon(stream) : System.Drawing.SystemIcons.Application };
        tray.MouseUp += (_, e) => { if (e.Button == MouseButtons.Right) ShowTrayMenu(); };
        tray.DoubleClick += (_, _) => ShowFromTray();
        trayMenu = new ContextMenu();
        var open = new MenuItem { Header = L10n.T("TrayOpen"), FontWeight = FontWeights.SemiBold }; open.Click += (_, _) => ShowFromTray();
        var sync = new MenuItem { Header = L10n.T("PhoneSync") + "…" }; sync.Click += (_, _) => { ShowFromTray(); PhoneSyncClick(this, new RoutedEventArgs()); };
        var exit = new MenuItem { Header = L10n.T("TrayExit") }; exit.Click += (_, _) => ExitFromTray();
        // Over the taskbar a popup cannot be transparent, so this menu uses the square, shadowless template.
        trayMenu.Style = (System.Windows.Style)FindResource("TrayMenu");
        trayMenu.Items.Add(open); trayMenu.Items.Add(sync); trayMenu.Items.Add(exit);
        trayMenu.Closed += (_, _) => { trayHelper?.Hide(); };
        tray.BalloonTipClicked += (_, _) => ShowFromTray();
        Closed += (_, _) => { if (tray != null) { tray.Visible = false; tray.Dispose(); tray = null; } trayHelper?.Close(); };
    }
    // A WPF menu next to the tray icon needs a focused window behind it, otherwise it does not close on an outside click.
    private void ShowTrayMenu()
    {
        if (trayMenu == null) return;
        trayHelper ??= new Window { Width = 0, Height = 0, WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = true, Opacity = 0, AllowsTransparency = true, Background = null, Topmost = true };
        trayHelper.Show(); trayHelper.Activate();
        trayMenu.Placement = PlacementMode.MousePoint; trayMenu.IsOpen = true;
    }
    public void ShowFromTray()
    {
        Show(); CheckUpdatesIfStale();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }
    private void HideToTray()
    {
        Hide();
        if (settings.TrayHintShown || tray == null) return;
        settings.TrayHintShown = true;
        try { settings.Save(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        tray.ShowBalloonTip(4000, L10n.T("AppName"), L10n.T("TrayHint"), ToolTipIcon.None);
    }
    public void ExitFromTray() { exiting = true; Close(); }
    // True when the close should only hide the window.
    private bool CloseHides => !exiting && !RestartRequested;
}
