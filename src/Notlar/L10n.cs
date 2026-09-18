using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows.Markup;

namespace Notlar;

// UI strings live in Languages/<code>.json (embedded). English is the fallback for any missing key.
public static class L10n
{
    public sealed record Language(string Code, string NativeName, string Culture, bool RightToLeft = false);
    public static readonly Language[] Languages =
    [
        new("en", "English", "en-US"),
        new("tr", "Türkçe", "tr-TR"),
        new("es", "Español", "es-ES"),
        new("zh", "中文（简体）", "zh-CN"),
        new("hi", "हिन्दी", "hi-IN"),
        new("ar", "العربية", "ar-EG", true),
        new("pt", "Português", "pt-BR"),
        new("ru", "Русский", "ru-RU"),
        new("ja", "日本語", "ja-JP"),
        new("de", "Deutsch", "de-DE"),
        new("fr", "Français", "fr-FR"),
        new("id", "Bahasa Indonesia", "id-ID"),
        new("ko", "한국어", "ko-KR"),
    ];
    private static readonly Dictionary<string, string> fallback = Load("en");
    private static Dictionary<string, string> strings = fallback;
    public static Language Current { get; private set; } = Languages[0];
    public static CultureInfo Culture { get; private set; } = CultureInfo.GetCultureInfo("en-US");
    public const string SettingsFile = "settings.json";

    public static Dictionary<string, string> Load(string code)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Notlar.Languages." + code + ".json")
            ?? throw new FileNotFoundException("Language file missing: " + code);
        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? [];
    }
    public static void Use(string code)
    {
        var language = Languages.FirstOrDefault(l => l.Code == code) ?? Languages[0];
        strings = language.Code == "en" ? fallback : Load(language.Code);
        Current = language;
        Culture = CultureInfo.GetCultureInfo(language.Culture);
    }
    // Saved choice wins; otherwise the Windows display language, falling back to English.
    public static string Detect(string dataDirectory)
    {
        try
        {
            string path = Path.Combine(dataDirectory, SettingsFile);
            if (File.Exists(path))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                if (doc.RootElement.TryGetProperty("language", out var saved) && saved.GetString() is string code && Languages.Any(l => l.Code == code)) return code;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        string system = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        return Languages.Any(l => l.Code == system) ? system : "en";
    }
    public static void Save(string dataDirectory, string code)
    {
        var settings = AppSettings.Load(dataDirectory); settings.Language = code; settings.Save();
    }
    public static string T(string key) => strings.TryGetValue(key, out var s) ? s : fallback.TryGetValue(key, out var f) ? f : key;
    public static string T(string key, params object[] args) => string.Format(Culture, T(key), args);
    public static string Count(string oneKey, string manyKey, int count) => T(count == 1 ? oneKey : manyKey, count);
}

// XAML: Text="{local:T MyNotes}"
[MarkupExtensionReturnType(typeof(string))]
public sealed class TExtension(string key) : MarkupExtension
{
    public string Key { get; set; } = key;
    public override object ProvideValue(IServiceProvider serviceProvider) => L10n.T(Key);
}
