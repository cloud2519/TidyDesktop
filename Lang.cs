using System.Globalization;
using System.IO;
using System.Text.Json;
using TidyDesktop.Boxes;

namespace TidyDesktop;

/// <summary>
/// The text the app shows, read from one language file per language (Languages\ko.json, en.json, …).
/// Each file maps keys to text; "_code", "_name" and "_culture" describe the language itself.
/// The built-in files can be overridden, or more languages added, by putting files in a
/// "Languages" folder next to the exe or in the settings folder.
/// </summary>
internal static class Lang
{
    private const string Fallback = "en";

    private sealed record Language(string Code, string Name, CultureInfo Culture, Dictionary<string, string> Text);

    private static readonly Dictionary<string, Language> Languages = Load();
    private static Language _current = Languages[Fallback];

    /// <summary>The setting value: a language code, or null to follow Windows.</summary>
    public static string? Choice { get; private set; }

    /// <summary>Every language there is a file for, as (code, name in that language).</summary>
    public static IEnumerable<(string Code, string Name)> Available =>
        Languages.Values.OrderBy(l => l.Name, StringComparer.CurrentCulture).Select(l => (l.Code, l.Name));

    public static void Set(string? choice)
    {
        Choice = choice != null && Languages.ContainsKey(choice) ? choice : null;
        var windows = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        _current = Languages[Choice ?? (Languages.ContainsKey(windows) ? windows : Fallback)];
    }

    /// <summary>The text for <paramref name="key"/>, with {0}, {1} … filled in from <paramref name="args"/>.</summary>
    public static string T(string key, params object?[] args)
    {
        var text = _current.Text.GetValueOrDefault(key) ?? Languages[Fallback].Text.GetValueOrDefault(key) ?? key;
        if (args.Length == 0)
            return text;
        try
        {
            return string.Format(_current.Culture, text, args);
        }
        catch (FormatException)
        {
            // A translation with a broken placeholder still shows something.
            return text;
        }
    }

    /// <summary>A date written with the pattern stored under <paramref name="formatKey"/>.</summary>
    public static string Date(DateTime time, string formatKey) => time.ToString(T(formatKey), _current.Culture);

    private static Dictionary<string, Language> Load()
    {
        var languages = new Dictionary<string, Language>(StringComparer.OrdinalIgnoreCase);
        var assembly = typeof(Lang).Assembly;
        foreach (var resource in assembly.GetManifestResourceNames().Where(n => n.StartsWith("Languages.", StringComparison.Ordinal)))
        {
            using var stream = assembly.GetManifestResourceStream(resource)!;
            Add(languages, stream, resource);
        }
        var folders = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Languages"),
            Path.Combine(AppData.Directory, "Languages"),
        };
        foreach (var folder in folders.Where(Directory.Exists))
        {
            foreach (var file in Directory.GetFiles(folder, "*.json"))
            {
                try
                {
                    using var stream = File.OpenRead(file);
                    Add(languages, stream, file);
                }
                catch (IOException ex)
                {
                    AppData.Log($"Could not read language file {file}: {ex.Message}");
                }
            }
        }
        return languages;
    }

    /// <summary>A file for a language that is already known adds to it and replaces its text.</summary>
    private static void Add(Dictionary<string, Language> languages, Stream stream, string source)
    {
        Dictionary<string, string>? text;
        try
        {
            text = JsonSerializer.Deserialize<Dictionary<string, string>>(stream);
        }
        catch (JsonException ex)
        {
            AppData.Log($"Language file {source} is not valid: {ex.Message}");
            return;
        }
        if (text == null || !text.TryGetValue("_code", out var code) || string.IsNullOrWhiteSpace(code))
        {
            AppData.Log($"Language file {source} has no \"_code\"");
            return;
        }
        if (languages.TryGetValue(code, out var known))
        {
            foreach (var (key, value) in text)
                known.Text[key] = value;
            return;
        }
        CultureInfo culture;
        try
        {
            culture = CultureInfo.GetCultureInfo(text.GetValueOrDefault("_culture") ?? code);
        }
        catch (CultureNotFoundException)
        {
            culture = CultureInfo.InvariantCulture;
        }
        languages[code] = new Language(code, text.GetValueOrDefault("_name") ?? code, culture, text);
    }
}
