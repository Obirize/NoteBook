using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

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
    public bool TrayHintShown { get => json["trayHintShown"]?.GetValue<bool>() ?? false; set => json["trayHintShown"] = value; }

}
