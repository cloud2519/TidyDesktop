using System.IO;
using System.Windows.Threading;
using TidyDesktop.Desktop;

namespace TidyDesktop.Boxes;

/// <summary>
/// Carries out the file-moving rules for items that newly appear on the desktop, records what
/// it did in the history, and can put a moved file back.
/// </summary>
internal sealed class RuleEngine
{
    // How long to wait for a file that is still being copied or downloaded before giving up.
    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan ReadyPoll = TimeSpan.FromSeconds(1);

    private readonly Func<IReadOnlyList<FileRule>> _rules;
    private readonly DesktopHistory _history;
    private readonly DesktopWatcher _watcher;

    // Files waiting until they can be moved, so one file is never handled twice.
    private readonly HashSet<string> _pending = new(StringComparer.OrdinalIgnoreCase);

    public RuleEngine(Func<IReadOnlyList<FileRule>> rules, DesktopHistory history, DesktopWatcher watcher)
    {
        _rules = rules;
        _history = history;
        _watcher = watcher;
    }

    /// <summary>A short message for the user: title and text.</summary>
    public event Action<string, string>? Notify;

    /// <summary>The first enabled rule that matches the item, or null.</summary>
    public FileRule? RuleFor(string path) => _rules().FirstOrDefault(rule => rule.Matches(path));

    /// <summary>Moves the file if a moving rule applies, once it is completely written.</summary>
    public void OnNewItem(string path)
    {
        if (RuleFor(path) is not { MovesFiles: true } || !_pending.Add(path))
            return;
        var started = DateTime.Now;
        var timer = new DispatcherTimer { Interval = ReadyPoll };
        timer.Tick += (_, _) =>
        {
            if (!File.Exists(path))
            {
                Stop();
                return;
            }
            if (!IsReady(path))
            {
                if (DateTime.Now - started > ReadyTimeout)
                {
                    Stop();
                    _history.Add(HistoryKind.Failed, path, Lang.T("ruleEngine.fileStayedInUse"));
                }
                return;
            }
            Stop();
            // Rules may have changed while waiting.
            if (RuleFor(path) is { MovesFiles: true } rule)
                Apply(rule, path);
        };
        timer.Start();

        void Stop()
        {
            timer.Stop();
            _pending.Remove(path);
        }
    }

    /// <summary>Applies one moving rule to the matching files already on the desktop.</summary>
    /// <returns>How many files were moved.</returns>
    public int ApplyNow(FileRule rule)
    {
        if (!rule.MovesFiles)
            return 0;
        var moved = 0;
        foreach (var folder in DesktopWatcher.Folders)
        {
            foreach (var path in Directory.GetFiles(folder))
            {
                if (DesktopWatcher.IsTemporary(path) || !rule.Matches(path) || !IsReady(path))
                    continue;
                if (Apply(rule, path))
                    moved++;
            }
        }
        return moved;
    }

    /// <summary>A file is complete once nobody else has it open.</summary>
    private static bool IsReady(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private bool Apply(FileRule rule, string path)
    {
        var name = DesktopHistory.DisplayName(path);
        if (string.IsNullOrWhiteSpace(rule.Folder))
            return false;
        try
        {
            Directory.CreateDirectory(rule.Folder);
            var destination = UniquePath(Path.Combine(rule.Folder, Path.GetFileName(path)));
            _watcher.Ignore(path);
            File.Move(path, destination);

            string? shortcut = null;
            if (rule.Action == RuleAction.MoveAndLeaveShortcut)
            {
                shortcut = UniquePath(Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileName(path) + ".lnk"));
                _watcher.Ignore(shortcut);
                CreateShortcut(shortcut, destination);
            }

            _history.Add(new HistoryEntry
            {
                Kind = HistoryKind.Moved,
                Name = name,
                Path = path,
                Destination = destination,
                ShortcutPath = shortcut,
                Detail = Lang.T("boxes.rule", rule.Name),
            });
            var folderName = Path.GetFileName(Path.TrimEndingDirectorySeparator(rule.Folder));
            Notify?.Invoke(Lang.T("ruleEngine.rule", rule.Name),
                Lang.T("ruleEngine.movedFolderYouCan", name, folderName));
            AppData.Log($"Rule '{rule.Name}' moved {path} -> {destination}");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _history.Add(HistoryKind.Failed, path, Lang.T("ruleEngine.ruleCouldNotMove", rule.Name, ex.Message));
            AppData.Log($"Rule '{rule.Name}' failed for {path}: {ex.Message}");
            return false;
        }
    }

    /// <summary>Puts a moved file back on the desktop and removes the shortcut left for it.</summary>
    public bool Undo(HistoryEntry entry)
    {
        if (!entry.CanUndo || entry.Destination == null)
            return false;
        try
        {
            if (!File.Exists(entry.Destination))
            {
                Notify?.Invoke(Lang.T("ruleEngine.couldNotUndo"),
                Lang.T("ruleEngine.noLongerIn", entry.Name, Path.GetDirectoryName(entry.Destination)));
                return false;
            }
            if (entry.ShortcutPath != null && File.Exists(entry.ShortcutPath))
            {
                _watcher.Ignore(entry.ShortcutPath);
                File.Delete(entry.ShortcutPath);
            }
            var back = UniquePath(entry.Path);
            _watcher.Ignore(back);
            File.Move(entry.Destination, back);

            entry.Undone = true;
            _history.Update(entry);
            _history.Add(new HistoryEntry
            {
                Kind = HistoryKind.Undone,
                Name = entry.Name,
                Path = back,
                Detail = Lang.T("ruleEngine.putBackOnDesktop", Path.GetFileName(Path.GetDirectoryName(entry.Destination))),
            });
            AppData.Log($"Undid move of {entry.Destination} -> {back}");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Notify?.Invoke(Lang.T("ruleEngine.couldNotUndo"), ex.Message);
            return false;
        }
    }

    /// <summary>The path itself, or "name (2).ext" and so on if it is taken. Never overwrites.</summary>
    private static string UniquePath(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
            return path;
        var folder = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        for (var i = 2; ; i++)
        {
            var candidate = Path.Combine(folder, $"{stem} ({i}){extension}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
                return candidate;
        }
    }

    private static void CreateShortcut(string shortcutPath, string target)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell")
                        ?? throw new IOException(Lang.T("ruleEngine.cannotMakeShortcutsWscript"));
        dynamic shell = Activator.CreateInstance(shellType)!;
        try
        {
            dynamic link = shell.CreateShortcut(shortcutPath);
            link.TargetPath = target;
            link.WorkingDirectory = Path.GetDirectoryName(target);
            link.Save();
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
        }
    }
}
