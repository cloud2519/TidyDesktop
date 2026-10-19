using System.IO;
using System.Text.Json;

namespace TidyDesktop.Boxes;

/// <summary>A box. Geometry is in physical screen pixels and covers the title bar plus the body.</summary>
public sealed class BoxModel
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = Lang.T("boxes.newBox");
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }

    /// <summary>Desktop parsing names of the icons in this box, in display order.</summary>
    public List<string> Items { get; set; } = new();

    /// <summary>Background color as #RRGGBB.</summary>
    public string Color { get; set; } = BoxStyle.DefaultColor;

    /// <summary>Body opacity from 0.1 to 1.0; the title bar is drawn slightly more opaque.</summary>
    public double Opacity { get; set; } = BoxStyle.DefaultOpacity;

    /// <summary>Id of the <see cref="Category"/> whose new desktop items join this box, or null.</summary>
    public string? Category { get; set; }

    /// <summary>Rolled up to just the title bar, with its icons parked off-screen.</summary>
    public bool Collapsed { get; set; }

    /// <summary>Position and size are fixed: the box cannot be dragged or resized.</summary>
    public bool Locked { get; set; }

    /// <summary>Takes over everything but the id from <paramref name="other"/>.</summary>
    public void CopyFrom(BoxModel other)
    {
        Title = other.Title;
        (X, Y, Width, Height) = (other.X, other.Y, other.Width, other.Height);
        Items = new List<string>(other.Items);
        Color = other.Color;
        Opacity = other.Opacity;
        Category = other.Category;
        Collapsed = other.Collapsed;
        Locked = other.Locked;
    }
}

public sealed class AppConfig
{
    public List<BoxModel> Boxes { get; set; } = new();

    /// <summary>Desktop folder flags before TidyDesktop turned off auto-arrange/snap-to-grid.</summary>
    public uint? OriginalFolderFlags { get; set; }

    /// <summary>
    /// Double-clicking empty desktop hides boxes and icons, and shows them again. Off until the
    /// user turns it on: an unexpectedly empty desktop is alarming.
    /// </summary>
    public bool DoubleClickHides { get; set; }

    /// <summary>"ko" or "en"; null follows the Windows display language.</summary>
    public string? Language { get; set; }

    /// <summary>
    /// 2 once boxes made before icons had gaps between them were widened to keep their columns.
    /// </summary>
    public int LayoutVersion { get; set; }

    /// <summary>
    /// Desktop icons are currently hidden by TidyDesktop; remembered so they are shown again even
    /// if the app was closed while they were hidden.
    /// </summary>
    public bool IconsHidden { get; set; }

    /// <summary>What to do with items that newly appear on the desktop, tried in order.</summary>
    public List<FileRule> Rules { get; set; } = new();

    public HistoryPanelSettings HistoryPanel { get; set; } = new();
}

/// <summary>Where the "recent changes" panel sits, in physical pixels; zero size means not placed yet.</summary>
public sealed class HistoryPanelSettings
{
    public bool Visible { get; set; } = true;
    public bool Collapsed { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
}

/// <summary>Boxes and icon positions at one moment, so the desktop can be put back that way.</summary>
public sealed class LayoutSnapshot
{
    public DateTime TakenAt { get; set; }

    /// <summary>Why it was taken, in the language of the time, shown when choosing one.</summary>
    public string Reason { get; set; } = "";

    public List<BoxModel> Boxes { get; set; } = new();
    public List<IconBackupEntry> Icons { get; set; } = new();
}

public sealed class IconBackup
{
    public DateTime TakenAt { get; set; }
    public uint FolderFlags { get; set; }
    public List<IconBackupEntry> Icons { get; set; } = new();
}

public sealed class IconBackupEntry
{
    public string Key { get; set; } = "";
    public int X { get; set; }
    public int Y { get; set; }
}

internal static class AppData
{
    public static string Directory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TidyDesktop");

    private static string ConfigPath => Path.Combine(Directory, "boxes.json");
    private static string BackupPath => Path.Combine(Directory, "icon-backup.json");
    private static string LogPath => Path.Combine(Directory, "log.txt");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    // Early builds saved the same data as "fences.json" with a "Fences" list.
    private static string LegacyConfigPath => Path.Combine(Directory, "fences.json");

    private sealed class LegacyConfig
    {
        public List<BoxModel> Fences { get; set; } = new();
        public uint? OriginalFolderFlags { get; set; }
    }

    /// <summary>Whether TidyDesktop has run on this PC before (its settings exist).</summary>
    public static bool HasConfig => File.Exists(ConfigPath) || File.Exists(LegacyConfigPath);

    public static AppConfig LoadConfig()
    {
        if (Load<AppConfig>(ConfigPath) is { } config)
            return config;
        if (Load<LegacyConfig>(LegacyConfigPath) is not { } legacy)
            return new AppConfig();

        config = new AppConfig { Boxes = legacy.Fences, OriginalFolderFlags = legacy.OriginalFolderFlags };
        SaveConfig(config);
        Log($"Migrated {legacy.Fences.Count} boxes from fences.json");
        return config;
    }

    public static void SaveConfig(AppConfig config) => Save(ConfigPath, config);

    public static IconBackup? LoadBackup() => Load<IconBackup>(BackupPath);

    public static bool HasBackup => File.Exists(BackupPath);

    public static void SaveBackup(IconBackup backup) => Save(BackupPath, backup);

    private static string HistoryPath => Path.Combine(Directory, "history.json");

    public static List<HistoryEntry> LoadHistory() => Load<List<HistoryEntry>>(HistoryPath) ?? new();

    public static void SaveHistory(List<HistoryEntry> entries) => Save(HistoryPath, entries);

    private static string SnapshotDirectory => Path.Combine(Directory, "snapshots");

    /// <summary>How many snapshots are kept; older ones are deleted.</summary>
    private const int SnapshotsKept = 30;

    public static void SaveSnapshot(LayoutSnapshot snapshot)
    {
        System.IO.Directory.CreateDirectory(SnapshotDirectory);
        Save(Path.Combine(SnapshotDirectory, $"{snapshot.TakenAt:yyyyMMdd-HHmmss-fff}.json"), snapshot);
        foreach (var old in SnapshotFiles().Skip(SnapshotsKept))
        {
            try
            {
                File.Delete(old);
            }
            catch (IOException ex)
            {
                Log($"Could not delete old snapshot {old}: {ex.Message}");
            }
        }
    }

    /// <summary>Saved snapshots, newest first.</summary>
    public static List<(string Path, LayoutSnapshot Snapshot)> LoadSnapshots() =>
        SnapshotFiles()
            .Select(path => (path, Load<LayoutSnapshot>(path)))
            .Where(entry => entry.Item2 != null)
            .Select(entry => (entry.path, entry.Item2!))
            .ToList();

    public static LayoutSnapshot? LoadSnapshot(string path) => Load<LayoutSnapshot>(path);

    // File names start with the time taken, so name order is age order.
    private static IEnumerable<string> SnapshotFiles() =>
        System.IO.Directory.Exists(SnapshotDirectory)
            ? System.IO.Directory.GetFiles(SnapshotDirectory, "*.json").OrderByDescending(p => p, StringComparer.Ordinal)
            : Enumerable.Empty<string>();

    public static void Log(string message)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
        catch (IOException)
        {
        }
    }

    private static T? Load<T>(string path) where T : class
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOptions) : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            Log($"Failed to read {path}: {ex.Message}");
            return null;
        }
    }

    private static void Save<T>(string path, T value)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(value, JsonOptions));
        File.Move(temp, path, overwrite: true);
    }
}
