using System.IO;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Runtime.InteropServices;

namespace Notlar;

// Shows one attachment decrypted in memory (pictures) or from a self-deleting temporary file (videos).
public partial class AttachmentWindow : Window
{
    private readonly Attachment attachment;
    private readonly AttachmentStore store;
    private string? temporary;
    private bool playing, seeking;
    private readonly DispatcherTimer clock = new() { Interval = TimeSpan.FromMilliseconds(250) };
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    public Func<bool>? SaveCopy { get; set; }
    public AttachmentWindow(Window owner, AttachmentStore store, Attachment attachment)
    {
        this.attachment = attachment; this.store = store;
        Owner = owner;
        if (L10n.Current.RightToLeft) FlowDirection = FlowDirection.RightToLeft;
        InitializeComponent();
        Title = attachment.Name; NameText.Text = attachment.Name;
        DetailText.Text = attachment.SizeLabel + (attachment.Width > 0 ? " · " + attachment.Width + "×" + attachment.Height : "") + (attachment.IsVideo ? " · " + L10n.T("AttachmentViewerHint") : "");
        if (owner.ActualWidth >= 600 && owner.ActualHeight >= 400) { Width = Math.Min(1000, owner.ActualWidth * 0.9); Height = Math.Min(720, owner.ActualHeight * 0.9); }
        SourceInitialized += (_, _) => { int rounded = 2; DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 33, ref rounded, 4); };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { Close(); e.Handled = true; }
            else if (e.Key == Key.Space && attachment.IsVideo) { TogglePlay(this, e); e.Handled = true; }
        };
        clock.Tick += (_, _) => { if (!seeking && Player.NaturalDuration.HasTimeSpan) { Seek.Value = Player.Position.TotalSeconds; TimeText.Text = Clock(Player.Position) + " / " + Clock(Player.NaturalDuration.TimeSpan); } };
        Loaded += (_, _) => Load();
        Closed += (_, _) =>
        {
            clock.Stop(); Picture.Source = null;
            try { Player.Stop(); Player.Close(); Player.Source = null; } catch (InvalidOperationException) { }
            if (temporary != null) { AttachmentStore.DeleteTemporary(temporary); temporary = null; }
        };
    }
    private static string Clock(TimeSpan t) => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
    private void Load()
    {
        try
        {
            if (attachment.IsVideo)
            {
                temporary = store.WriteTemporary(attachment);
                Player.Visibility = Visibility.Visible; Controls.Visibility = Visibility.Visible;
                Player.Source = new Uri(temporary);
                Player.Play(); playing = true; PlayButton.Content = "\uE769"; clock.Start();
            }
            else
            {
                var image = new BitmapImage();
                using (var stream = new MemoryStream(store.ReadAll(attachment)))
                {
                    image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile; image.StreamSource = stream; image.EndInit();
                }
                image.Freeze();
                Picture.Source = image; Picture.Visibility = Visibility.Visible;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException or NotSupportedException or FileFormatException or ArgumentException or InvalidOperationException)
        { Fail(); }
    }
    private void Fail() { Picture.Visibility = Player.Visibility = Controls.Visibility = Visibility.Collapsed; ErrorText.Visibility = Visibility.Visible; clock.Stop(); }
    private void MediaOpened(object sender, RoutedEventArgs e) { if (Player.NaturalDuration.HasTimeSpan) Seek.Maximum = Player.NaturalDuration.TimeSpan.TotalSeconds; }
    private void MediaEnded(object sender, RoutedEventArgs e) { Player.Position = TimeSpan.Zero; Player.Pause(); playing = false; PlayButton.Content = "\uE768"; }
    private void MediaFailed(object sender, ExceptionRoutedEventArgs e) => Fail();
    private void TogglePlay(object sender, RoutedEventArgs e)
    {
        if (!attachment.IsVideo || ErrorText.Visibility == Visibility.Visible) return;
        if (playing) Player.Pause(); else Player.Play();
        playing = !playing; PlayButton.Content = playing ? "\uE769" : "\uE768";
    }
    private void SeekStarted(object sender, DragStartedEventArgs e) => seeking = true;
    private void SeekCompleted(object sender, DragCompletedEventArgs e) { seeking = false; Player.Position = TimeSpan.FromSeconds(Seek.Value); }
    private void SeekChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // Clicking the track (no thumb drag) should also jump.
        if (!seeking && Player.NaturalDuration.HasTimeSpan && Math.Abs(Player.Position.TotalSeconds - e.NewValue) > 1) Player.Position = TimeSpan.FromSeconds(e.NewValue);
    }
    private void SaveClick(object sender, RoutedEventArgs e) => SaveCopy?.Invoke();
    private void CloseClick(object sender, RoutedEventArgs e) => Close();
}
