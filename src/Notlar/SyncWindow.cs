using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using Notlar.Sync;

namespace Notlar;

// The phone link screen: on/off, the three setup steps (certificate, Home Screen app, pairing code) and paired phones.
public sealed class SyncWindow : Window
{
    private readonly SyncService service;
    private readonly StackPanel panel = new() { Margin = new Thickness(32, 20, 32, 28) };
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    public SyncWindow(Window owner, SyncService service)
    {
        this.service = service; Owner = owner; Title = L10n.T("PhoneSync");
        Style = (Style)FindResource(typeof(Window));
        Width = 640; Height = Math.Min(860, SystemParameters.WorkArea.Height - 40); MinWidth = 520; MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.CanResize; ShowInTaskbar = false;
        if (L10n.Current.RightToLeft) FlowDirection = FlowDirection.RightToLeft;
        WindowChrome.SetWindowChrome(this, new WindowChrome { CaptionHeight = 48, ResizeBorderThickness = new Thickness(6), CornerRadius = new CornerRadius(12), GlassFrameThickness = new Thickness(0), UseAeroCaptionButtons = false });
        SourceInitialized += (_, _) => { int rounded = 2; DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 33, ref rounded, 4); };
        var close = new Button { Content = "", Style = (Style)FindResource("IconButton"), Focusable = false, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 6, 8, 0) };
        WindowChrome.SetIsHitTestVisibleInChrome(close, true); close.Click += (_, _) => Close();
        System.Windows.Automation.AutomationProperties.SetName(close, L10n.T("Close"));
        var root = new Grid();
        root.Children.Add(new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 40, 0, 0) });
        root.Children.Add(close);
        Content = new Border { BorderThickness = new Thickness(1), Child = root, BorderBrush = (Brush)FindResource("Rule") };
        service.StatusChanged += Changed;
        Closed += (_, _) => { service.StatusChanged -= Changed; service.ClearPairCode(); };
        PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) Close(); };
        if (service.Running) service.NewPairCode();
        Render();
    }
    private void Changed() => Dispatcher.BeginInvoke(() => { if (service.Running && service.PairCode == null) service.NewPairCode(); Render(); });

    private TextBlock Text(string value, double size = 14, string brush = "Ink", Thickness? margin = null, FontWeight? weight = null)
    {
        var block = new TextBlock { Text = value, FontSize = size, TextWrapping = TextWrapping.Wrap, Margin = margin ?? new Thickness(0, 0, 0, 8), LineHeight = size * 1.5, FontWeight = weight ?? FontWeights.Normal };
        block.SetResourceReference(TextBlock.ForegroundProperty, brush);
        panel.Children.Add(block); return block;
    }
    private void Heading(string value) => Text(value, 18, "Ink", new Thickness(0, 22, 0, 6), FontWeights.SemiBold);
    private Button Action(string title, Action action, bool primary = false, bool danger = false)
    {
        var button = new Button { Content = title, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 8), Padding = new Thickness(16, 9, 16, 9), FontSize = 13 };
        if (primary) button.Style = (Style)FindResource("Primary");
        if (danger) button.SetResourceReference(ForegroundProperty, "Danger");
        button.Click += (_, _) => { try { action(); Render(); } catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException or InvalidOperationException) { MessageDialog.Info(this, ex.Message, L10n.T("PhoneSync")); } };
        panel.Children.Add(button); return button;
    }
    // A QR code beside its explanation; the address is repeated as text for typing by hand.
    private void Step(string heading, string url, string help)
    {
        Heading(heading);
        var row = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); row.ColumnDefinitions.Add(new ColumnDefinition());
        var image = new Image { Source = QrCode.Encode(url).ToBitmap(4), Width = 168, Height = 168, Margin = new Thickness(0, 0, 18, 0), VerticalAlignment = VerticalAlignment.Top };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.NearestNeighbor);
        row.Children.Add(new Border { Background = Brushes.White, CornerRadius = new CornerRadius(8), Padding = new Thickness(4), Child = image, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 0, 18, 0) });
        var text = new StackPanel(); Grid.SetColumn(text, 1);
        var helpBlock = new TextBlock { Text = help, FontSize = 13, TextWrapping = TextWrapping.Wrap, LineHeight = 20, Margin = new Thickness(0, 0, 0, 8) }; helpBlock.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        var urlBox = new TextBox { Text = url, IsReadOnly = true, FontSize = 13, TextWrapping = TextWrapping.Wrap, Padding = new Thickness(10, 8, 10, 8), BorderThickness = new Thickness(0) }; urlBox.SetResourceReference(BackgroundProperty, "Surface");
        text.Children.Add(helpBlock); text.Children.Add(new Border { CornerRadius = new CornerRadius(8), Child = urlBox, Background = (Brush)FindResource("Surface") });
        row.Children.Add(text);
        panel.Children.Add(row);
    }
    private void Render()
    {
        panel.Children.Clear();
        Text(L10n.T("PhoneSync"), 26, "Ink", new Thickness(0, 0, 0, 6), FontWeights.SemiBold);
        Text(L10n.T("SyncIntro"), 13, "Muted");
        Action(L10n.T(service.Settings.Enabled ? "SyncDisable" : "SyncEnable"), () => { service.SetEnabled(!service.Settings.Enabled); if (service.Running) service.NewPairCode(); else service.ClearPairCode(); }, primary: !service.Settings.Enabled);
        if (service.Error != null) Text(service.Error, 13, "Danger");
        if (!service.Running) { Text(L10n.T("SyncStatusOff"), 13, "Muted"); return; }
        Text(L10n.T("SyncStatusOn", service.AppUrl), 13, "Muted");
        Step(L10n.T("SyncSetup"), service.SetupUrl, L10n.T("SyncSetupHelp"));
        Text(L10n.T("SetupFingerprint") + "\n" + service.Certs!.RootFingerprint, 11, "Muted", new Thickness(0, 6, 0, 0));
        Step(L10n.T("SyncPair"), service.AppUrl, L10n.T("SyncPairHelp"));
        Heading(L10n.T("SyncCode"));
        string code = service.PairCode ?? service.NewPairCode();
        var codeBlock = new TextBlock { Text = code[..3] + " " + code[3..], FontSize = 40, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) };
        codeBlock.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
        System.Windows.Automation.AutomationProperties.SetName(codeBlock, L10n.T("SyncCode"));
        panel.Children.Add(codeBlock);
        Text(L10n.T("SyncCodeHelp", SyncService.PairCodeMinutes), 13, "Muted");
        Action(L10n.T("SyncCodeNew"), () => service.NewPairCode());
        Heading(L10n.T("SyncDevices"));
        if (service.Settings.Devices.Count == 0) Text(L10n.T("SyncNoDevices"), 13, "Muted");
        var connected = service.Connected.Select(c => c.Id).ToHashSet();
        foreach (var device in service.Settings.Devices.OrderByDescending(d => d.LastSeen ?? d.PairedAt))
        {
            string when = device.LastSeen?.LocalDateTime.ToString("g", L10n.Culture) ?? "";
            Text((connected.Contains(device.Id) ? "● " : "○ ") + device.Name + " · " + (connected.Contains(device.Id) ? L10n.T("SyncDeviceOnline") : L10n.T("SyncDeviceSeen", when)), 13, "Ink", new Thickness(0, 0, 0, 2));
        }
        Text(L10n.T("SyncNetworkHelp"), 12, "Muted", new Thickness(0, 14, 0, 8));
        Action(L10n.T("SyncReset"), () => { if (MessageDialog.Ask(this, L10n.T("SyncReset"), L10n.T("SyncResetConfirm"), L10n.T("SyncReset"), danger: true)) { service.ResetKey(); service.NewPairCode(); } }, danger: true);
    }
}
