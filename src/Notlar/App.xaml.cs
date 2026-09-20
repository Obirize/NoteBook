using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Windows;

namespace Notlar;

public partial class App : Application
{
    public static string DataDirectory { get; } = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "data"));
    private static readonly string InstanceName = "Notlar-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(DataDirectory)))[..20];
    private Mutex? mutex;
    private CancellationTokenSource? pipeStop;
    private VaultSession? session;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        L10n.Use(L10n.Detect(DataDirectory));
        AttachmentStore.CleanTemporary();
        var files = e.Args.Where(a => TextFiles.IsSupported(a) && File.Exists(a)).Select(Path.GetFullPath).ToList();
        bool fresh = e.Args.Contains("--new", StringComparer.OrdinalIgnoreCase);
        bool minimized = e.Args.Contains("--minimized", StringComparer.OrdinalIgnoreCase) && files.Count == 0 && !fresh;
        mutex = new Mutex(true, "Local\\" + InstanceName, out bool owns);
        if (!owns)
        {
            // Hand the request to the running window instead of showing a second copy.
            if (!Forward(files, fresh)) MessageDialog.Info(null, L10n.T("AlreadyOpen"));
            mutex.Dispose(); mutex = null; Shutdown(); return;
        }
        MainWindow editor;
        try
        {
            session = OpenNotes();
            if (session == null) { Finish(false); return; }
            editor = new MainWindow(session);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException or System.Text.Json.JsonException or ArgumentException)
        { MessageDialog.Info(null, L10n.T("OpenFailed")); Finish(false); return; }
        MainWindow = editor;
        pipeStop = new CancellationTokenSource();
        _ = Listen(editor, pipeStop.Token);
        // Windows shutdown or sign-out must not be held up by the "close hides" behaviour.
        SessionEnding += (_, _) => editor.ExitFromTray();
        editor.Closed += (_, _) => { pipeStop.Cancel(); Finish(editor.RestartRequested); };
        if (!minimized) editor.Show();
        foreach (string file in files) Open(editor, file);
        if (fresh) editor.CreateNote();
    }
    // The last steps for every way out: release the vault and the single-instance lock, relaunch after a language change.
    private void Finish(bool restart)
    {
        session?.Dispose(); session = null;
        if (mutex != null) { try { mutex.ReleaseMutex(); } catch (ApplicationException) { } mutex.Dispose(); mutex = null; }
        if (restart && Environment.ProcessPath is string self) System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(self) { UseShellExecute = true });
        Shutdown();
    }
    private static void Open(MainWindow editor, string file)
    {
        try { editor.ImportTextFile(file); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        { MessageDialog.Info(editor, ex is InvalidDataException ? ex.Message : L10n.T("OpenFileFailed", file)); }
    }
    private static bool Forward(List<string> files, bool fresh)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", InstanceName, PipeDirection.Out);
            pipe.Connect(3000);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false));
            writer.WriteLine("activate");
            foreach (string file in files) writer.WriteLine("open " + file);
            if (fresh) writer.WriteLine("new");
            return true;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException) { return false; }
    }
    private static async Task Listen(MainWindow editor, CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(InstanceName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(stop);
                using var reader = new StreamReader(pipe, Encoding.UTF8);
                string? line;
                while ((line = await reader.ReadLineAsync(stop)) != null)
                {
                    string command = line;
                    // Nothing that happens in the window may take the listener down with it.
                    try { await editor.Dispatcher.InvokeAsync(() =>
                    {
                        editor.ShowFromTray();
                        if (command.StartsWith("open ") && File.Exists(command[5..])) Open(editor, command[5..]);
                        else if (command == "new") editor.CreateNote();
                    }); }
                    catch (Exception ex) when (ex is InvalidOperationException or TaskCanceledException) { }
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
    private VaultSession? OpenNotes()
    {
        string path = Path.Combine(DataDirectory, "notes.vault");
        if (!File.Exists(path) && !File.Exists(path + ".bak"))
        {
            var created = VaultSession.CreateDevice(path);
            try { created.Save(); created.VerifySaved(); return created; }
            catch { created.Dispose(); throw; }
        }
        string source = File.Exists(path) ? path : path + ".bak";
        VaultSession? session = null;
        try
        {
            // Password-locked vaults were only ever written by the first releases; 1.8.2 was the last version that converted them.
            if (VaultSession.ReadVersion(source) == 1) { MessageDialog.Info(null, L10n.T("VaultTooOld")); return null; }
            session = VaultSession.OpenDevice(source, path);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Security.Cryptography.CryptographicException or System.Text.Json.JsonException or ArgumentException)
        {
            session?.Dispose();
            if (!File.Exists(path + ".bak") || source.EndsWith(".bak") ||
                !MessageDialog.Ask(null, L10n.T("AppName"), L10n.T("TryBackup"), L10n.T("OK"))) throw;
            session = VaultSession.OpenDevice(path + ".bak", path);
            source = path + ".bak";
        }
        try
        {
            if (source.EndsWith(".bak"))
            {
                // Preserve both existing encrypted files until the replacement is durable.
                if (File.Exists(path)) File.Copy(path, path + ".damaged-" + DateTime.UtcNow.Ticks);
                var temp = path + ".restore";
                using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                { stream.Write(File.ReadAllBytes(source)); stream.Flush(true); }
                if (File.Exists(path)) File.Replace(temp, path, null, true); else File.Move(temp, path);
            }
            session!.VerifySaved(); return session;
        }
        catch { session?.Dispose(); throw; }
    }
}
