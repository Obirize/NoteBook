using System.IO;
using System.Windows;
using System.Windows.Input;

namespace Notlar;

public partial class GateWindow : Window
{
    private readonly string directory, path;
    private readonly bool setup;
    private bool recovery, resetPassword, busy;
    private VaultSession? pending;
    public VaultSession? Session { get; private set; }
    public GateWindow(string dataDirectory)
    {
        InitializeComponent();
        directory = dataDirectory; path = Path.Combine(directory, "notes.vault");
        setup = !File.Exists(path) && !File.Exists(path + ".bak");
        Description.Text = setup ? "Düşüncelerinize küçük, sakin bir yer. Notlarınızı korumak için en az 12 karakterlik bir parola belirleyin." : "Notlarınız kilitli. Kaldığınız yerden devam etmek için parolanızı girin.";
        ConfirmPanel.Visibility = setup ? Visibility.Visible : Visibility.Collapsed;
        Recover.Visibility = setup ? Visibility.Collapsed : Visibility.Visible;
        Submit.Content = setup ? "Notlarımı koru" : "Kilidi aç";
        if (setup && LegacyImport.Names.Any(n => File.Exists(Path.Combine(directory, n))))
        {
            MigrationNotice.Visibility = Visibility.Visible;
            MigrationNotice.Text = "Mevcut notlarınız şifreli kasaya aktarılacak. Doğrulama sonrası eski, şifresiz not dosyaları kaldırılacak.";
        }
        if (!File.Exists(path) && File.Exists(path + ".bak")) { UseBackup.Visibility = Visibility.Visible; UseBackup.IsChecked = true; }
        Loaded += (_, _) => Password.Focus();
        Closing += (_, e) => { if (busy && Session == null) e.Cancel = true; };
        Closed += (_, _) => { Password.Clear(); ConfirmPassword.Clear(); RecoveryCode.Clear(); if (Session == null) pending?.Dispose(); };
    }
    private void MinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void MaximizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void CloseClick(object sender, RoutedEventArgs e) => Close();
    private void PasswordKeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) SubmitClick(sender, e); }
    private void RecoverClick(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        recovery = !recovery; Password.Clear(); Error.Text = "";
        PasswordLabel.Text = recovery ? "Kurtarma anahtarı" : "Parola";
        Recover.Content = recovery ? "Parola ile aç" : "Kurtarma anahtarını kullan";
        Description.Text = recovery ? "Kurulumda sakladığınız kurtarma anahtarını girin. Ardından yeni bir parola belirleyebilirsiniz." : "Notlarınız kilitli. Kaldığınız yerden devam etmek için parolanızı girin.";
        Password.Focus();
    }
    private void SetBusy(bool value)
    {
        busy = value; Credentials.IsEnabled = !value; Finish.IsEnabled = !value;
        if (value) Error.Text = "Birazdan hazır…";
    }
    private async void SubmitClick(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        string password = Password.Password;
        if ((setup || resetPassword) && (password.Length < 12 || password != ConfirmPassword.Password))
        { Error.Text = "En az 12 karakter kullanın ve iki parolanın aynı olduğundan emin olun."; return; }
        SetBusy(true);
        try
        {
            if (resetPassword)
            {
                await Task.Run(() => pending!.ChangePassword(password));
                Complete(pending!); return;
            }
            if (setup)
            {
                var result = await Task.Run(() => VaultSession.Create(path, password, LegacyImport.Read(directory)));
                pending = result.Session;
                RecoveryCode.Text = string.Join(" ", Enumerable.Range(0, 8).Select(i => result.RecoveryCode.Substring(i * 8, 8)));
                Heading.Text = "Bir yedek anahtar.";
                Description.Text = "Parolanızı unutursanız bu anahtar notlarınızı açar. Güvenli bir yerde saklayın. Parola ve anahtar birlikte kaybolursa notlar kurtarılamaz.";
                Credentials.Visibility = Visibility.Collapsed; RecoveryPanel.Visibility = Visibility.Visible;
                Password.Clear(); ConfirmPassword.Clear(); Error.Text = "";
            }
            else
            {
                bool backup = UseBackup.IsChecked == true;
                var session = await Task.Run(() => VaultSession.Open(backup ? path + ".bak" : path, password, recovery, path));
                pending = session;
                if (backup)
                {
                    await Task.Run(() =>
                    {
                        if (File.Exists(path)) File.Copy(path, path + ".damaged-" + DateTime.UtcNow.Ticks);
                        // Atomic restore; never overwrite the good backup with the damaged primary.
                        string temp = path + ".restore";
                        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                        { stream.Write(File.ReadAllBytes(path + ".bak")); stream.Flush(true); }
                        if (File.Exists(path)) File.Replace(temp, path, null, true); else File.Move(temp, path);
                    });
                }
                if (recovery)
                {
                    resetPassword = true; Heading.Text = "Yeni bir parola."; Description.Text = "Notlarınız açıldı. Devam etmek için yeni parolanızı belirleyin.";
                    PasswordLabel.Text = "Yeni parola"; ConfirmPanel.Visibility = Visibility.Visible; Recover.Visibility = Visibility.Collapsed;
                    UseBackup.Visibility = Visibility.Collapsed; Submit.Content = "Parolayı yenile ve aç"; Password.Clear(); Error.Text = "";
                }
                else Complete(session);
            }
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or FormatException or System.Text.Json.JsonException or InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            Error.Text = setup || resetPassword ? "İşlem tamamlanamadı. Dosyalarınız korundu. Klasörün yazılabilir olduğunu kontrol edin." : "Kilit açılamadı. Parolanızı / anahtarınızı kontrol edin. Dosya hasarlıysa önceki şifreli kaydı deneyebilirsiniz.";
            if (!setup && !resetPassword && File.Exists(path + ".bak")) UseBackup.Visibility = Visibility.Visible;
            if (!resetPassword) { pending?.Dispose(); pending = null; }
        }
        finally { SetBusy(false); }
    }
    private async void FinishClick(object sender, RoutedEventArgs e)
    {
        if (busy || pending == null) return;
        if (Acknowledged.IsChecked != true) { Error.Text = "Devam etmeden önce kurtarma anahtarını sakladığınızı onaylayın."; return; }
        SetBusy(true);
        try
        {
            await Task.Run(() => { pending.Save(); pending.VerifySaved(); pending.Save(); });
            Complete(pending);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        { Error.Text = "Kasa kaydedilemedi. Eski notlarınız korundu; tekrar deneyebilirsiniz."; }
        finally { SetBusy(false); }
    }
    private void Complete(VaultSession session)
    {
        session.VerifySaved();
        try { LegacyImport.RemoveVerifiedOriginals(directory, session); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { MessageBox.Show(this, "Şifreli kasa hazır; ancak eski şifresiz dosyalardan bazıları kaldırılamadı. Eski uygulamayı kapatıp Notlar'ı yeniden açın. data klasöründeki notes.json ve notes.backup.json dosyalarını kontrol edin.", "Aktarım bilgisi"); }
        Session = session; DialogResult = true;
    }
}
