using System.IO;
using System.Windows.Threading;

namespace TidyDesktop.Desktop;

/// <summary>
/// Finds out where an item that left the desktop went. Change notifications on each local drive
/// show the same name turning up somewhere else (moved), as a "$R…" name in the recycle bin
/// (deleted to the recycle bin), or not at all (deleted for good). A move to another drive
/// copies before it deletes, so names seen shortly before the item left count as well.
/// </summary>
internal sealed class MoveTracker : IDisposable
{
    /// <summary>The marker <see cref="Find"/> reports for the recycle bin.</summary>
    public const string RecycleBin = "::RecycleBin";

    private static readonly TimeSpan Window = TimeSpan.FromSeconds(8);

    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly Dispatcher _dispatcher;
    private readonly object _lock = new();
    private readonly List<(DateTime Time, string Path)> _recent = new();

    public MoveTracker()
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable) || !drive.IsReady)
                    continue;
                var watcher = new FileSystemWatcher(drive.RootDirectory.FullName)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName,
                    InternalBufferSize = 64 * 1024,
                };
                watcher.Created += (_, e) => Note(e.FullPath);
                watcher.Renamed += (_, e) => Note(e.FullPath);
                // A lost batch only means one move may go unexplained.
                watcher.Error += (_, _) => { };
                watcher.EnableRaisingEvents = true;
                _watchers.Add(watcher);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                Boxes.AppData.Log($"Cannot watch {drive.Name} for moves: {ex.Message}");
            }
        }
    }

    // Runs on the watchers' threads.
    private void Note(string path)
    {
        var now = DateTime.Now;
        lock (_lock)
        {
            _recent.Add((now, path));
            var expired = _recent.FindIndex(r => now - r.Time <= Window);
            _recent.RemoveRange(0, expired < 0 ? _recent.Count : expired);
        }
    }

    /// <summary>
    /// Reports on the UI thread where <paramref name="removedPath"/> went: a path, <see cref="RecycleBin"/>,
    /// or null when it did not turn up anywhere (deleted for good, or moved off this computer).
    /// </summary>
    public void Find(string removedPath, Action<string?> found)
    {
        var name = Path.GetFileName(removedPath);
        var extension = Path.GetExtension(name);
        var since = DateTime.Now - Window;
        var started = DateTime.Now;
        var desktops = DesktopWatcher.Folders.ToList();

        string? Match()
        {
            List<string> candidates;
            lock (_lock)
                candidates = _recent.Where(r => r.Time >= since).Select(r => r.Path).Reverse().ToList();

            foreach (var path in candidates)
            {
                if (path.Equals(removedPath, StringComparison.OrdinalIgnoreCase) ||
                    path.Contains(@"\$Recycle.Bin\", StringComparison.OrdinalIgnoreCase))
                    continue;
                var folder = Path.GetDirectoryName(path) ?? "";
                if (!desktops.Any(f => folder.Equals(f, StringComparison.OrdinalIgnoreCase)) &&
                    Path.GetFileName(path).Equals(name, StringComparison.OrdinalIgnoreCase) &&
                    (File.Exists(path) || Directory.Exists(path)))
                    return path;
            }
            foreach (var path in candidates)
            {
                if (path.Contains(@"\$Recycle.Bin\", StringComparison.OrdinalIgnoreCase) &&
                    Path.GetFileName(path).StartsWith("$R", StringComparison.OrdinalIgnoreCase) &&
                    Path.GetExtension(path).Equals(extension, StringComparison.OrdinalIgnoreCase) &&
                    IsRecycled(path, removedPath))
                    return RecycleBin;
            }
            return null;
        }

        // The other side of a move can be reported a moment after the desktop's side.
        var timer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = TimeSpan.FromMilliseconds(500) };
        timer.Tick += (_, _) =>
        {
            var match = Match();
            if (match == null && DateTime.Now - started < TimeSpan.FromSeconds(4))
                return;
            timer.Stop();
            found(match);
        };
        timer.Start();
    }

    /// <summary>
    /// The recycle bin keeps each item as "$R…" next to a "$I…" record that holds its original path.
    /// </summary>
    private static bool IsRecycled(string recycledPath, string originalPath)
    {
        var record = Path.Combine(Path.GetDirectoryName(recycledPath)!, "$I" + Path.GetFileName(recycledPath)[2..]);
        try
        {
            var text = System.Text.Encoding.Unicode.GetString(File.ReadAllBytes(record));
            return text.Contains(originalPath, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        foreach (var watcher in _watchers)
            watcher.Dispose();
        _watchers.Clear();
    }
}
