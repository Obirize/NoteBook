using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace Notlar;

// data/settings.json: the few preferences that are not part of the notebook. Unknown keys are kept as they are.
public sealed class AppSettings
{
    public const string FileName = "settings.json";
    private readonly JsonObject json;
    private readonly string path;
    private AppSettings(string path, JsonObject json) { this.path = path; this.json = json; }

    public static AppSettings Load(string dataDirectory)
    {
        string path = Path.Combine(dataDirectory, FileName);
        JsonObject? json = null;
        try { if (File.Exists(path)) json = JsonNode.Parse(File.ReadAllText(path)) as JsonObject; }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        return new AppSettings(path, json ?? []);
    }
    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + ".tmp";
        File.WriteAllText(temp, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, path, true);
    }
    public string? Language { get => json["language"]?.GetValue<string>(); set => json["language"] = value; }
    // The window closes to the tray and the app starts with Windows unless the user says otherwise.
    public bool StartWithWindows { get => json["startWithWindows"]?.GetValue<bool>() ?? true; set => json["startWithWindows"] = value; }
    public bool TrayHintShown { get => json["trayHintShown"]?.GetValue<bool>() ?? false; set => json["trayHintShown"] = value; }

    // HKCU\...\Run: no administrator rights, only this user's sign-in. "--minimized" starts straight into the tray.
    public const string RunValueName = "Notlar";
    public static string StartupCommand => "\"" + Environment.ProcessPath + "\" --minimized";
    public static void ApplyStartup(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            if (key == null) return;
            if (enabled) key.SetValue(RunValueName, StartupCommand); else key.DeleteValue(RunValueName, false);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
    }
    public static bool StartupRegistered()
    {
        try { using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"); return key?.GetValue(RunValueName) is string; }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return false; }
    }
}
