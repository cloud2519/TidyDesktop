using System.IO;
using System.Windows.Threading;

namespace TidyDesktop.Desktop;

/// <summary>
/// Reports files and folders that appear on, leave, or are renamed on the desktop (the user's and
/// the shared desktop folder), on the UI thread. Uses change notifications, not polling.
/// </summary>
internal sealed class DesktopWatcher : IDisposable
{
    // Files that are still being written: downloads in progress and editor temp files.
    private static readonly string[] TempExtensions = { ".tmp", ".crdownload", ".part", ".partial", ".download", ".opdownload" };

    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly Dispatcher _dispatcher;

    // Paths TidyDesktop is changing itself, with when to stop ignoring them.
    private readonly Dictionary<string, DateTime> _ignored = new(StringComparer.OrdinalIgnoreCase);

    public DesktopWatcher()
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        foreach (var folder in Folders)
        {
            var watcher = new FileSystemWatcher(folder)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName,
            };
            watcher.Created += (_, e) => Post(() => OnCreated(e.FullPath));
            watcher.Deleted += (_, e) => Post(() => OnDeleted(e.FullPath));
            watcher.Renamed += (_, e) => Post(() => OnRenamed(e.OldFullPath, e.FullPath));
            watcher.Error += (_, e) => Boxes.AppData.Log($"Desktop watcher error: {e.GetException().Message}");
            watcher.EnableRaisingEvents = true;
            _watchers.Add(watcher);
        }
    }

    /// <summary>The folders whose items show on the desktop.</summary>
    public static IEnumerable<string> Folders =>
        new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
            }
            .Where(path => path.Length > 0 && Directory.Exists(path))
            .Distinct(StringComparer.OrdinalIgnoreCase);

    /// <summary>The user's own desktop folder, where new shortcuts are made.</summary>
    public static string UserFolder => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

    public event Action<string>? Added;
    public event Action<string>? Removed;
    public event Action<string, string>? Renamed;

    /// <summary>Ignores changes to <paramref name="path"/> for a few seconds, for moves TidyDesktop makes itself.</summary>
    public void Ignore(string path) => _ignored[path] = DateTime.Now.AddSeconds(5);

    private bool IsIgnored(string path) =>
        _ignored.TryGetValue(path, out var until) && DateTime.Now < until;

    public static bool IsTemporary(string path)
    {
        var name = Path.GetFileName(path);
        return name.StartsWith("~$", StringComparison.Ordinal) ||
               name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase) ||
               TempExtensions.Contains(Path.GetExtension(name), StringComparer.OrdinalIgnoreCase);
    }

    private void Post(Action action) => _dispatcher.BeginInvoke(action);

    private void OnCreated(string path)
    {
        if (!IsIgnored(path) && !IsTemporary(path))
            Added?.Invoke(path);
    }

    private void OnDeleted(string path)
    {
        if (!IsIgnored(path) && !IsTemporary(path))
            Removed?.Invoke(path);
    }

    private void OnRenamed(string oldPath, string newPath)
    {
        if (IsIgnored(oldPath) || IsIgnored(newPath))
            return;
        // A finished download is renamed from its temporary name: to the user it is a new file.
        if (IsTemporary(oldPath))
        {
            if (!IsTemporary(newPath))
                Added?.Invoke(newPath);
            return;
        }
        if (!IsTemporary(newPath))
            Renamed?.Invoke(oldPath, newPath);
    }

    public void Dispose()
    {
        foreach (var watcher in _watchers)
            watcher.Dispose();
        _watchers.Clear();
    }
}
