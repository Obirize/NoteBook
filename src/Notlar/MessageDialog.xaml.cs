using System.IO;
using System.Media;
using System.Reflection;
using System.Windows;

namespace Notlar;

// The application's own message box: themed, localized, with a soft click instead of the system alert sound.
public partial class MessageDialog : Window
{
    public MessageDialog(Window? owner, string heading, string message, string primaryText, string? secondaryText = null, bool danger = false)
    {
        Owner = owner;
        if (owner == null) WindowStartupLocation = WindowStartupLocation.CenterScreen;
        if (L10n.Current.RightToLeft) FlowDirection = FlowDirection.RightToLeft;
        InitializeComponent();
        Title = heading; Heading.Text = heading; Message.Text = message;
        Primary.Content = primaryText;
        Primary.Style = (Style)FindResource(danger ? "DangerButton" : "Primary");
        if (secondaryText == null) Secondary.Visibility = Visibility.Collapsed; else Secondary.Content = secondaryText;
        Loaded += (_, _) => { Sounds.Click(); (secondaryText != null && danger ? Secondary : Primary).Focus(); };
    }
    private void PrimaryClick(object sender, RoutedEventArgs e) => DialogResult = true;
    public static void Info(Window? owner, string message, string? heading = null) =>
        new MessageDialog(owner, heading ?? L10n.T("AppName"), message, L10n.T("OK")).ShowDialog();
    public static bool Ask(Window? owner, string heading, string message, string yesText, bool danger = false) =>
        new MessageDialog(owner, heading, message, yesText, L10n.T("Cancel"), danger).ShowDialog() == true;
}

public static class Sounds
{
    private static SoundPlayer? click;
    public static bool Enabled { get; set; } = true;
    // Plays the embedded click; never throws (no audio device, muted, tests).
    public static void Click()
    {
        if (!Enabled) return;
        try
        {
            click ??= Load("Notlar.Sounds.click.wav");
            click?.Play();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or TimeoutException) { }
    }
    private static SoundPlayer? Load(string resource)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource);
        if (stream == null) return null;
        var buffer = new MemoryStream(); stream.CopyTo(buffer); buffer.Position = 0;
        var player = new SoundPlayer(buffer); player.Load(); return player;
    }
}
