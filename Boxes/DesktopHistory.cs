using System.IO;

namespace TidyDesktop.Boxes;

public enum HistoryKind
{
    Added,
    Removed,
    Renamed,
    Boxed,
    Moved,
    Undone,
    Failed,
}

/// <summary>One thing that happened to a desktop item.</summary>
public sealed class HistoryEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime Time { get; set; } = DateTime.Now;
    public HistoryKind Kind { get; set; }

    /// <summary>File name as shown to the user.</summary>
    public string Name { get; set; } = "";

    /// <summary>Where the item is (or was, for removals and moves) on the desktop.</summary>
    public string Path { get; set; } = "";

    /// <summary>The previous path, for renames.</summary>
    public string? OldPath { get; set; }

    /// <summary>
    /// Where a rule moved the file, or where a removed item turned up
    /// (<see cref="Desktop.MoveTracker.RecycleBin"/> for the recycle bin).
    /// </summary>
    public string? Destination { get; set; }

    /// <summary>The shortcut a rule left on the desktop, removed again on undo.</summary>
    public string? ShortcutPath { get; set; }

    /// <summary>Extra text such as the rule or box name.</summary>
    public string? Detail { get; set; }

    /// <summary>A move that has been put back.</summary>
    public bool Undone { get; set; }

    /// <summary>For removals: the search for where it went is over, so no destination means deleted.</summary>
    public bool Traced { get; set; }

    public bool CanUndo => Kind == HistoryKind.Moved && !Undone;
}

/// <summary>Recent desktop changes, newest first, kept for <see cref="KeepDays"/> days.</summary>
internal sealed class DesktopHistory
{
    private const int KeepDays = 30;
    private const int MaxEntries = 500;

    private readonly List<HistoryEntry> _entries;

    public DesktopHistory()
    {
        _entries = AppData.LoadHistory()
            .Where(e => e.Time > DateTime.Now.AddDays(-KeepDays))
            .OrderByDescending(e => e.Time)
            .Take(MaxEntries)
            .ToList();
    }

    public IReadOnlyList<HistoryEntry> Entries => _entries;

    public event Action? Changed;

    public HistoryEntry Add(HistoryKind kind, string path, string? detail = null, string? oldPath = null)
    {
        var entry = new HistoryEntry
        {
            Kind = kind,
            Name = DisplayName(path),
            Path = path,
            OldPath = oldPath,
            Detail = detail,
        };
        Add(entry);
        return entry;
    }

    public void Add(HistoryEntry entry)
    {
        _entries.Insert(0, entry);
        if (_entries.Count > MaxEntries)
            _entries.RemoveRange(MaxEntries, _entries.Count - MaxEntries);
        Save();
    }

    public void Update(HistoryEntry entry) => Save();

    public void RemoveWhere(Predicate<HistoryEntry> match)
    {
        if (_entries.RemoveAll(match) > 0)
            Save();
    }

    public void Clear()
    {
        _entries.Clear();
        Save();
    }

    /// <summary>Shortcuts show without ".lnk"/".url" on the desktop, so they are named the same here.</summary>
    public static string DisplayName(string path)
    {
        var name = System.IO.Path.GetFileName(path);
        var extension = System.IO.Path.GetExtension(name);
        return extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".url", StringComparison.OrdinalIgnoreCase)
            ? System.IO.Path.GetFileNameWithoutExtension(name)
            : name;
    }

    private void Save()
    {
        try
        {
            AppData.SaveHistory(_entries);
        }
        catch (IOException ex)
        {
            AppData.Log($"Could not save history: {ex.Message}");
        }
        Changed?.Invoke();
    }
}
