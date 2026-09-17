using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows.Forms;
static class Launcher {
    [STAThread] static void Main(string[] args) {
        string exe = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app", "Notlar.exe");
        // Forward file paths (e.g. a .txt opened via "Open with") to the application.
        var quoted = new StringBuilder();
        foreach (string a in args) quoted.Append('"').Append(a.Replace("\"", "\\\"")).Append("\" ");
        try { Process.Start(new ProcessStartInfo(exe) { Arguments = quoted.ToString().TrimEnd(), WorkingDirectory = Path.GetDirectoryName(exe), UseShellExecute = true }); }
        catch { MessageBox.Show("Uygulama dosyaları bulunamadı. Not Defteri.exe ile app klasörünü birlikte tutun veya build.cmd çalıştırın.", "Notlar"); }
    }
}
