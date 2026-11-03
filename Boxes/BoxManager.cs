using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using TidyDesktop.Desktop;
using TidyDesktop.Native;
using Screen = System.Windows.Forms.Screen;

namespace TidyDesktop.Boxes;

/// <summary>
/// Keeps boxes and real desktop icons in agreement:
/// an icon dropped inside a box joins it, an icon dragged out leaves it,
/// and member icons are laid out on a grid inside the box body.
/// </summary>
internal sealed class BoxManager : IDisposable
{
    private const uint ArrangeFlags = ShellConst.FWF_AUTOARRANGE | ShellConst.FWF_SNAPTOGRID;
    private const int PositionTolerance = 4;

    // Far outside every monitor; explorer keeps icons placed here, which hides them.
    private static readonly POINT ParkingSpot = new(-12000, -12000);

    private readonly List<BoxView> _views = new();

    // Where each boxed icon was last placed. An icon away from its target was moved by the user.
    private readonly Dictionary<string, POINT> _lastTargets = new(StringComparer.OrdinalIgnoreCase);

    // Every item seen so far, so items that newly appear (downloads, saved files) can be told apart.
    private readonly HashSet<string> _knownKeys = new(StringComparer.OrdinalIgnoreCase);
    private bool _knownKeysReady;

    // Fallback polling for changes without a mouse release (keyboard moves, new files).
    private readonly DispatcherTimer _syncTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };

    // Delays after a mouse release; the second pass catches drops explorer commits late.
    private static readonly int[] DropSyncDelaysMs = { 40, 200 };

    private MouseWatcher? _mouse;
    private DesktopHistory? _history;
    private DesktopWatcher? _watcher;
    private MoveTracker? _moves;
    private RuleEngine? _ruleEngine;
    private HistoryPanel? _historyPanel;

    /// <summary>Opens the desktop above all windows; set up by <see cref="Start"/>.</summary>
    public PanelLauncher? Panel { get; private set; }

    /// <summary>A short message to show the user (title, text), for example after a rule moved a file.</summary>
    public event Action<string, string>? Notify;
    public event Action? SettingsRequested;
    private bool _desktopHidden;
    private DateTime _lastAutoSnapshot;
    private AppConfig _config = new();
    private DesktopHost? _host;
    private DesktopIcons? _icons;
    private bool _interacting;
    private DateTime _suspendSyncUntil;

    // The move or resize in progress. The box stays where it is while a shaded outline shows
    // where it will go; the box and its icons move once, at the drop.
    private Interaction? _interaction;

    // The latest sync's snapshot, reused to place icons at a drop.
    private IconSnapshot? _lastSnapshot;
    private Dictionary<string, DesktopIcon>? _lastIcons;
    private PreviewWindow? _preview;
    private bool _framePending;

    // One repaint sweep after drops, restarted by each drop rather than stacking up.
    private readonly DispatcherTimer _sweepTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };

    private sealed class Interaction
    {
        public required BoxView View { get; init; }
        public RECT Target { get; set; }
        public DateTime StartedAt { get; } = DateTime.Now;
        public int Frames { get; set; }
        public required BoxSnapper Snapper { get; init; }
        public IReadOnlyList<RECT> Guides { get; set; } = Array.Empty<RECT>();

        /// <summary>The size in icon cells, shown on the outline while resizing.</summary>
        public string? Label { get; set; }
    }

    /// <summary>This is the first time TidyDesktop runs on this PC.</summary>
    public bool IsFirstRun { get; private set; }

    public void Start()
    {
        IsFirstRun = !AppData.HasConfig;
        _config = AppData.LoadConfig();
        Attach();
        EnsureBackup();
        PrepareFolderFlags();
        WidenForIconGaps();

        foreach (var model in _config.Boxes)
            AddView(model);
        if (_views.Count == 0)
            CreateBox(Lang.T("boxes.tidyBox"));

        _syncTimer.Tick += (_, _) => Sync();
        _sweepTimer.Tick += (_, _) =>
        {
            _sweepTimer.Stop();
            // Clears any icon pictures explorer left behind after a drop.
            _icons?.RepaintAll();
        };
        _syncTimer.Start();

        _mouse = new MouseWatcher();
        _mouse.LeftButtonUp += SyncSoon;
        _mouse.DoubleClick += OnDoubleClick;
        if (!_mouse.IsInstalled)
            AppData.Log("Mouse hook unavailable; relying on polling only");

        _history = new DesktopHistory();
        _watcher = new DesktopWatcher();
        _ruleEngine = new RuleEngine(() => _config.Rules, _history, _watcher);
        _ruleEngine.Notify += (title, text) => Notify?.Invoke(title, text);
        _watcher.Added += OnItemAdded;
        _moves = new MoveTracker();
        _watcher.Removed += OnItemRemoved;
        _watcher.Renamed += OnItemRenamed;
        if (_config.HistoryPanel.Visible)
            ShowHistoryPanel();
        Panel = new PanelLauncher(_config.Panel, PanelContent, Save);

        // Icons hidden when the app last closed (or crashed) are shown again.
        if (_config.IconsHidden)
            SetIconsHidden(false);

        Sync();
        TakeSnapshot(Lang.T("boxes.atStart"));
    }

    private void OnItemAdded(string path)
    {
        _history!.Add(HistoryKind.Added, path);
        // A moving rule takes the file away once it is complete; otherwise the next sync puts it
        // in its box.
        _ruleEngine!.OnNewItem(path);
        SyncSoon();
    }

    private void OnItemRemoved(string path)
    {
        var entry = _history!.Add(HistoryKind.Removed, path);
        _moves!.Find(path, where =>
        {
            entry.Destination = where;
            entry.Traced = true;
            _history.Update(entry);
            AppData.Log($"'{entry.Name}' left the desktop: {where ?? "deleted"}");
        });
    }

    /// <summary>A renamed item stays in its box: the box keeps it under the new name.</summary>
    private void OnItemRenamed(string oldPath, string newPath)
    {
        _history!.Add(HistoryKind.Renamed, newPath, oldPath: oldPath);
        var changed = false;
        foreach (var view in _views)
        {
            var index = view.Model.Items.FindIndex(key => string.Equals(key, oldPath, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
                continue;
            view.Model.Items[index] = newPath;
            changed = true;
        }
        if (_lastTargets.Remove(oldPath, out var target))
            _lastTargets[newPath] = target;
        _knownKeys.Add(newPath);
        if (changed)
            Save();
    }

    public bool HistoryPanelVisible => _historyPanel != null;

    public void SetHistoryPanelVisible(bool visible)
    {
        _config.HistoryPanel.Visible = visible;
        if (visible)
            ShowHistoryPanel();
        else
        {
            _historyPanel?.Close();
            _historyPanel = null;
        }
        Save();
    }

    private void ShowHistoryPanel()
    {
        if (_historyPanel != null || _history == null || _host == null)
            return;
        _historyPanel = new HistoryPanel(_history, _config.HistoryPanel, entry => _ruleEngine!.Undo(entry), Save);
        _historyPanel.AttachTo(_host.Owner);
        _historyPanel.Show();
        _historyPanel.ApplyBounds();
    }

    public void OpenRules()
    {
        var boxes = () => (IReadOnlyList<(string Id, string Title)>)_views.Select(v => (v.Model.Id, v.Model.Title)).ToList();
        new RulesWindow(_config.Rules, boxes, ApplyRuleNow, Save).ShowDialog();
    }

    /// <summary>Applies one rule to what is on the desktop now.</summary>
    /// <returns>How many items it moved or put in a box.</returns>
    private int ApplyRuleNow(FileRule rule)
    {
        if (rule.MovesFiles)
            return _ruleEngine!.ApplyNow(rule);
        if (_icons == null || _views.FirstOrDefault(v => v.Model.Id == rule.BoxId) is not { } target)
            return 0;
        using var snapshot = _icons.Snapshot();
        var byKey = ByKey(snapshot);
        var boxed = _views.SelectMany(v => v.Model.Items).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var count = 0;
        foreach (var icon in byKey.Values.Where(i => !boxed.Contains(i.Key) && rule.Matches(i.Key)))
        {
            target.Model.Items.Add(icon.Key);
            _history!.Add(HistoryKind.Boxed, icon.Key, Lang.T("boxes.putInBoxRule", target.Model.Title, rule.Name));
            count++;
        }
        ApplyLayout(new[] { target }, byKey);
        Save();
        return count;
    }

    public bool DesktopHidden => _desktopHidden;

    /// <summary>The boxes and the items outside them, as the latest sync saw them.</summary>
    private PanelContent PanelContent()
    {
        if (_lastIcons == null && _icons != null)
            KeepSnapshot(_icons.Snapshot());
        var icons = _lastIcons ?? new Dictionary<string, DesktopIcon>();
        string Name(string key) => icons.TryGetValue(key, out var icon) ? icon.DisplayName : Path.GetFileName(key);

        var boxed = _views.SelectMany(v => v.Model.Items).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var boxes = _views
            .Select(v => new PanelBox(v.Model, v.Model.Items.Select(key => new PanelItem(key, Name(key), null)).ToList()))
            .ToList();
        var loose = icons.Values
            .Where(icon => !boxed.Contains(icon.Key))
            .Select(icon => new PanelItem(icon.Key, icon.DisplayName, icon.Position))
            .ToList();
        return new PanelContent(boxes, loose, _icons?.Spacing ?? new POINT(96, 96));
    }

    /// <summary>Stores the language and redraws the text that is already on screen.</summary>
    public void SetLanguage(string? code)
    {
        _config.Language = code;
        Save();
        if (_historyPanel != null)
        {
            _historyPanel.Close();
            _historyPanel = null;
            ShowHistoryPanel();
        }
    }

    public bool DoubleClickHides
    {
        get => _config.DoubleClickHides;
        set
        {
            _config.DoubleClickHides = value;
            Save();
        }
    }

    /// <summary>A double-click on empty desktop hides boxes and icons, or brings them back.</summary>
    private void OnDoubleClick(POINT pt)
    {
        if (!_config.DoubleClickHides || _host == null || _interacting || !IsEmptyDesktop(pt))
            return;
        SetDesktopHidden(!_desktopHidden);
    }

    private bool IsEmptyDesktop(POINT pt)
    {
        // Only the desktop itself: its root is Progman (or the wallpaper layer on older layouts).
        // Box title bars and handles are separate top-level windows, so clicks on them do not count.
        var root = NativeMethods.GetAncestor(NativeMethods.WindowFromPoint(pt), NativeMethods.GA_ROOT);
        if (root != _host!.Owner && root != _host.Progman && root != NativeMethods.GetAncestor(_host.WallpaperLayer, NativeMethods.GA_ROOT))
            return false;
        // While hidden there is nothing to hit, so any double-click on the desktop brings it back.
        if (_desktopHidden)
            return true;
        // Double-clicking an icon opens it; that must never also hide the desktop. Ask the desktop
        // itself (through accessibility, as screen readers do) whether an icon is under the pointer;
        // positions read earlier can be out of date. When in doubt, do nothing.
        var onIcon = NativeMethods.IsListItemAt(pt);
        AppData.Log($"Desktop double-click at {pt.X},{pt.Y}: {(onIcon switch { true => "icon", false => "empty", null => "unknown" })}");
        if (onIcon != false)
            return false;
        // A second opinion from the last known icon positions.
        if (_lastIcons == null || _icons == null)
            return false;
        var spacing = _icons.Spacing;
        return !_lastIcons.Values.Any(icon =>
            pt.X >= icon.Position.X && pt.X < icon.Position.X + spacing.X &&
            pt.Y >= icon.Position.Y && pt.Y < icon.Position.Y + spacing.Y);
    }

    public void SetDesktopHidden(bool hidden)
    {
        if (_desktopHidden == hidden)
            return;
        _desktopHidden = hidden;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        // Boxes first: their windows take a few hundred ms, the icons almost none, so this way
        // both change at about the same moment.
        using (WindowBatch.Begin())
        {
            foreach (var view in _views)
                view.SetHidden(hidden);
        }
        SetIconsHidden(hidden);
        if (hidden)
            _historyPanel?.Park();
        else
            _historyPanel?.ApplyBounds();
        // Shown again, icons may have been rearranged meanwhile; let explorer settle first.
        _suspendSyncUntil = DateTime.Now.AddMilliseconds(600);
        AppData.Log($"{(hidden ? "Desktop hidden" : "Desktop shown")} in {sw.ElapsedMilliseconds} ms");
        DesktopHiddenChanged?.Invoke(hidden);
    }

    /// <summary>The desktop was hidden (true) or shown again (false).</summary>
    public event Action<bool>? DesktopHiddenChanged;

    private void SetIconsHidden(bool hidden)
    {
        try
        {
            _icons?.SetFolderFlags(ShellConst.FWF_NOICONS, hidden ? ShellConst.FWF_NOICONS : 0);
        }
        catch (COMException ex)
        {
            AppData.Log($"Could not {(hidden ? "hide" : "show")} desktop icons: {ex.Message}");
        }
        _config.IconsHidden = hidden;
        Save();
    }

    /// <summary>Saves the boxes and every icon position, so the desktop can be put back this way later.</summary>
    public void TakeSnapshot(string reason)
    {
        if (_icons == null)
            return;
        try
        {
            using var snapshot = _icons.Snapshot();
            // A copy, so later changes to the live boxes do not alter the saved one.
            var boxes = System.Text.Json.JsonSerializer.Deserialize<List<BoxModel>>(
                System.Text.Json.JsonSerializer.Serialize(_config.Boxes))!;
            AppData.SaveSnapshot(new LayoutSnapshot
            {
                TakenAt = DateTime.Now,
                Reason = reason,
                Boxes = boxes,
                Icons = snapshot.Icons
                    .Where(icon => icon.Position.X > ParkingSpot.X / 2) // parked icons are placed by their box
                    .Select(icon => new IconBackupEntry { Key = icon.Key, X = icon.Position.X, Y = icon.Position.Y })
                    .ToList(),
            });
            _lastAutoSnapshot = DateTime.Now;
            AppData.Log($"Snapshot taken ({reason}): {boxes.Count} boxes, {snapshot.Icons.Count} icons");
        }
        catch (Exception ex) when (ex is COMException or IOException)
        {
            AppData.Log($"Snapshot failed: {ex.Message}");
        }
    }

    /// <summary>Puts boxes and icons back the way a snapshot recorded them.</summary>
    public void RestoreSnapshot(string path)
    {
        if (_icons == null || AppData.LoadSnapshot(path) is not { } saved)
            return;
        if (_interaction != null)
            EndInteraction(_interaction.View);
        var restoreTimer = System.Diagnostics.Stopwatch.StartNew();
        SetDesktopHidden(false);
        TakeSnapshot(Lang.T("boxes.beforeGoingBack"));

        // Boxes that still exist keep their windows (making them is slow); only the difference
        // is created or removed.
        var existing = _views.ToDictionary(v => v.Model.Id);
        var kept = new HashSet<string>(saved.Boxes.Select(b => b.Id));
        foreach (var view in _views.Where(v => !kept.Contains(v.Model.Id)).ToList())
        {
            _views.Remove(view);
            view.Dispose();
        }
        _lastTargets.Clear();
        var batch = WindowBatch.Begin();
        _config.Boxes = saved.Boxes
            .Select(box =>
            {
                if (!existing.TryGetValue(box.Id, out var view))
                    return box;
                view.Model.CopyFrom(box);
                view.ApplyStyle();
                view.ApplyBounds();
                return view.Model;
            })
            .ToList();
        batch.Dispose();
        foreach (var model in _config.Boxes.Where(b => !existing.ContainsKey(b.Id)))
            AddView(model);

        using (var snapshot = _icons.Snapshot())
        {
            var byKey = ByKey(snapshot);
            // Loose icons go back to their saved places; boxed ones are then laid out by their boxes.
            var boxed = _config.Boxes.SelectMany(b => b.Items).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var moves = saved.Icons
                .Where(entry => !boxed.Contains(entry.Key) && byKey.ContainsKey(entry.Key))
                .Select(entry => (byKey[entry.Key], new POINT(entry.X, entry.Y)))
                .ToList();
            _icons.Move(moves);
            ApplyLayout(_views, byKey, force: true);
        }
        _suspendSyncUntil = DateTime.Now.AddMilliseconds(800);
        Save();
        AppData.Log($"Restored snapshot from {saved.TakenAt:yyyy-MM-dd HH:mm:ss} ({saved.Reason}) in {restoreTimer.ElapsedMilliseconds} ms");
    }

    private void SyncSoon()
    {
        if (_interacting)
        {
            // A drag whose button-up the window missed would otherwise follow the pointer forever.
            foreach (var view in _views.ToList())
                view.EndDragIfReleased();
            return;
        }
        foreach (var delay in DropSyncDelaysMs)
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(delay) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                Sync();
            };
            timer.Start();
        }
    }

    public void CreateBox(string? title = null)
    {
        title ??= Lang.T("boxes.newBox");
        if (_host == null || _icons == null)
            return;
        PrepareFolderFlags();

        var scale = NativeMethods.ScaleAt(Screen.PrimaryScreen!.Bounds.X + 1, Screen.PrimaryScreen.Bounds.Y + 1);
        var width = (int)Math.Round(380 * scale);
        var height = (int)Math.Round(230 * scale);
        var (x, y) = FindFreeSpot(width, height);

        var model = new BoxModel { Title = title, X = x, Y = y, Width = width, Height = height };
        _config.Boxes.Add(model);
        Save();
        AddView(model);
        AppData.Log($"Created box '{title}' at {x},{y} {width}x{height}");
    }

    /// <summary>Removes every box and puts icons back where they were before TidyDesktop first ran.</summary>
    public bool RestoreBackup()
    {
        var backup = AppData.LoadBackup();
        if (backup == null)
            return false;
        TakeSnapshot(Lang.T("boxes.beforeRestoringOriginalDesktop"));
        if (_icons == null)
            Attach();

        foreach (var view in _views)
            view.Dispose();
        _views.Clear();
        _config.Boxes.Clear();
        _lastTargets.Clear();

        using (var snapshot = _icons!.Snapshot())
        {
            var byKey = ByKey(snapshot);
            var moves = backup.Icons
                .Where(entry => byKey.ContainsKey(entry.Key))
                .Select(entry => (byKey[entry.Key], new POINT(entry.X, entry.Y)))
                .ToList();
            _icons.Move(moves);
            AppData.Log($"Restored {moves.Count} of {backup.Icons.Count} icon positions from {backup.TakenAt}");
        }

        var original = _config.OriginalFolderFlags ?? backup.FolderFlags;
        _icons.SetFolderFlags(ArrangeFlags, original & ArrangeFlags);
        _config.OriginalFolderFlags = null;
        Save();
        return true;
    }

    /// <summary>Writes what TidyDesktop can see of the desktop to the log, for troubleshooting.</summary>
    public void DumpDiagnostics()
    {
        Attach();
        AppData.Log($"Progman={_host!.Progman} DefView={_host.DefView} ListView={_host.ListView} " +
                    $"WallpaperLayer={_host.WallpaperLayer} InsertAfter={_host.WallpaperInsertAfter}");
        var spacing = _icons!.Spacing;
        AppData.Log($"Spacing={spacing.X}x{spacing.Y} FolderFlags=0x{_icons.FolderFlags:X}");
        using var snapshot = _icons.Snapshot();
        foreach (var icon in snapshot.Icons)
            AppData.Log($"  icon '{icon.DisplayName}' at {icon.Position.X},{icon.Position.Y} " +
                        $"[{Categorizer.Classify(icon)?.Title ?? "none"}] key={icon.Key}");
        AppData.Log($"  total {snapshot.Icons.Count} icons");
    }

    private void Attach()
    {
        _host = DesktopHost.Find() ?? throw new InvalidOperationException(Lang.T("boxes.couldNotFindDesktop"));
        _icons = DesktopIcons.Connect(_host);
        AppData.Log($"Attached: Progman={_host.Progman} Owner={_host.Owner} ListView={_host.ListView} WallpaperLayer={_host.WallpaperLayer} " +
                    $"runtime={System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription} version={App.Version} " +
                    $"windows={Environment.OSVersion.Version} layout={(_host.Owner == _host.Progman ? "24H2" : "classic")}");
    }

    private void Reattach()
    {
        try
        {
            Attach();
            // The outline is owned by the old Progman; a new one is made on the next drag.
            _preview?.Dispose();
            _preview = null;
            DisposeGuides();
            foreach (var view in _views)
                view.Reattach(_host!);
            _historyPanel?.AttachTo(_host!.Owner);
            _lastTargets.Clear();
            PrepareFolderFlags();
        }
        catch (Exception ex) when (ex is InvalidOperationException or COMException)
        {
            // explorer.exe is probably still starting; the next tick tries again.
            _host = null;
            _icons = null;
            AppData.Log($"Reattach failed: {ex.Message}");
        }
    }

    private void EnsureBackup()
    {
        if (AppData.HasBackup)
            return;
        using var snapshot = _icons!.Snapshot();
        AppData.SaveBackup(new IconBackup
        {
            TakenAt = DateTime.Now,
            FolderFlags = _icons.FolderFlags,
            Icons = snapshot.Icons
                .Select(icon => new IconBackupEntry { Key = icon.Key, X = icon.Position.X, Y = icon.Position.Y })
                .ToList(),
        });
        AppData.Log($"Saved icon backup with {snapshot.Icons.Count} icons");
    }

    /// <summary>
    /// Icons used to sit edge to edge in a box. Now that they have gaps, a box made before that
    /// would fit fewer columns and its icons would spill out the bottom, so it grows wider
    /// instead. Done once, as soon as the icon size can be measured.
    /// </summary>
    private void WidenForIconGaps()
    {
        if (_config.LayoutVersion >= 2)
            return;
        if (_config.Boxes.Count > 0)
        {
            var cell = _icons!.ItemBox;
            if (!_icons.ItemBoxMeasured)
                return;
            var spacing = _icons.Spacing;
            var before = _config.Boxes.ToDictionary(m => m, m => (m.X, m.Width));
            foreach (var model in _config.Boxes)
            {
                var scale = NativeMethods.ScaleAt(model.X + model.Width / 2, model.Y + model.Height / 2);
                int Px(double dip) => (int)Math.Round(dip * scale);
                var pad = Px(6);
                var gap = Px(IconGapDip);
                var inner = model.Width - 2 * pad;
                var oldColumns = Math.Max(1, inner / Math.Max(1, spacing.X));
                var wanted = Math.Max(1, Math.Min(oldColumns, model.Items.Count));
                var columns = Math.Max(1, (inner - gap) / Math.Max(1, cell.Width + gap));
                if (columns >= wanted)
                    continue;
                var width = GridWidth(wanted, gap) + 2 * pad;
                AppData.Log($"Widened '{model.Title}' from {model.Width} to {width} to keep {wanted} columns");
                model.Width = width;
            }

            // A wider box pushes the boxes it now runs into to the right, left to right so a push
            // can carry on to the next box. Boxes that overlapped already are left alone.
            bool Overlap(BoxModel a, int ax, int aw, BoxModel b, int bx, int bw) =>
                ax < bx + bw && bx < ax + aw && a.Y < b.Y + b.Height && b.Y < a.Y + a.Height;
            var ordered = _config.Boxes.OrderBy(m => m.X).ToList();
            for (var i = 0; i < ordered.Count; i++)
            {
                var box = ordered[i];
                var gap = (int)Math.Round(14 * NativeMethods.ScaleAt(box.X, box.Y));
                for (var j = 0; j < i; j++)
                {
                    var left = ordered[j];
                    if (!Overlap(left, left.X, left.Width, box, box.X, box.Width) ||
                        Overlap(left, before[left].X, before[left].Width, box, before[box].X, before[box].Width))
                        continue;
                    box.X = left.X + left.Width + gap;
                    AppData.Log($"Moved '{box.Title}' right to {box.X} to make room for '{left.Title}'");
                }
            }
        }
        _config.LayoutVersion = 2;
        Save();
    }

    /// <summary>Boxed layouts need free icon placement, so auto-arrange and snap-to-grid are turned off.</summary>
    private void PrepareFolderFlags()
    {
        var flags = _icons!.FolderFlags;
        if ((flags & ArrangeFlags) == 0)
            return;
        _config.OriginalFolderFlags ??= flags;
        _icons.SetFolderFlags(ArrangeFlags, 0);
        Save();
        AppData.Log($"Turned off auto-arrange/snap-to-grid (flags were 0x{flags:X})");
    }

    private void AddView(BoxModel model)
    {
        var view = new BoxView(model, _host!);
        _views.Add(view);

        view.Title.DragStarted += () => BeginInteraction(view);
        view.Title.Dragging += (dx, dy) =>
            UpdateTarget(view, Bounds(model.X + dx, model.Y + dy, model.Width, view.VisibleHeight), edge: null);
        view.Title.DragEnded += () => EndInteraction(view);

        foreach (var grip in view.Grips)
        {
            var edge = grip.Edge;
            grip.DragStarted += () => BeginInteraction(view);
            grip.Dragging += (dx, dy) =>
            {
                var (left, right, bottom) = (model.X, model.X + model.Width, model.Y + model.Height);
                if (edge.HasFlag(ResizeEdge.Right))
                    right += dx;
                // The right edge stays put while the left edge follows the pointer.
                if (edge.HasFlag(ResizeEdge.Left))
                    left += dx;
                if (edge.HasFlag(ResizeEdge.Bottom))
                    bottom += dy;
                UpdateTarget(view, Bounds(left, model.Y, right - left, bottom - model.Y), edge);
            };
            grip.DragEnded += () => EndInteraction(view);
        }
        view.Title.LockToggled += () =>
        {
            model.Locked = !model.Locked;
            view.ApplyStyle();
            view.ApplyBounds();
            Save();
        };
        view.Title.Renamed += title =>
        {
            model.Title = title;
            view.ApplyStyle();
            Save();
        };
        view.Title.CollapseToggled += () => ToggleCollapse(view);
        view.Title.ArrangeRequested += fromRight => ArrangeBoxes(fromRight);
        view.Title.StyleRequested += () =>
        {
            if (new BoxStyleWindow(model, view.ApplyStyle).ShowDialog() == true)
                Save();
        };
        view.Title.SortRequested += order => Sort(view, order);
        view.Title.NewBoxRequested += () => CreateBox();
        view.Title.SettingsRequested += () => SettingsRequested?.Invoke();
        view.Title.DeleteRequested += () => DeleteBox(view);
    }

    private static RECT Bounds(int x, int y, int width, int height) =>
        new() { Left = x, Top = y, Right = x + width, Bottom = y + height };

    private static bool Same(RECT a, RECT b) =>
        a.Left == b.Left && a.Top == b.Top && a.Right == b.Right && a.Bottom == b.Bottom;

    private void BeginInteraction(BoxView view)
    {
        // A drop still being handled is finished first, so two moves never share state.
        if (_interaction != null)
            EndInteraction(_interaction.View);
        if (_host == null)
            return;

        var model = view.Model;
        _interaction = new Interaction
        {
            View = view,
            Target = Bounds(model.X, model.Y, model.Width, view.VisibleHeight),
            Snapper = CreateSnapper(view),
        };
        _interacting = true;
        view.SetDimmed(true);

        var c = BoxStyle.ParseColor(model.Color);
        int Mix(double toWhite) =>
            ((int)(c.R + (255 - c.R) * toWhite) << 16) | ((int)(c.G + (255 - c.G) * toWhite) << 8) | (int)(c.B + (255 - c.B) * toWhite);
        _preview ??= new PreviewWindow(_host.Owner);
        _preview.Show(_interaction.Target, Mix(0.15), Mix(0.3), Mix(0.75), view.TitleHeight, view.Px(8));
    }

    private BoxSnapper CreateSnapper(BoxView view)
    {
        var others = _views
            .Where(v => v != view && !v.Hidden)
            .Select(v => Bounds(v.Model.X, v.Model.Y, v.Model.Width, v.VisibleHeight))
            .ToList();
        BoxSnapper.Cells? cells = null;
        try
        {
            if (_icons != null)
                cells = CellsFor(view);
        }
        catch (COMException ex)
        {
            AppData.Log($"Icon size unavailable for snapping: {ex.Message}");
        }
        return new BoxSnapper(others, cells, dip => view.Px(dip));
    }

    // The same measures Grid() and AutoOrganize lay icons out with.
    private BoxSnapper.Cells CellsFor(BoxView view) =>
        new(_icons!.ItemBox.Width, view.Px(IconGapDip), view.Px(6), _icons.Spacing.Y, view.TitleHeight, view.Px(10));

    /// <summary>
    /// Shrinks a box to its icons in whole cells. It keeps its number of columns while they are all
    /// used, and gains columns rather than grow taller than <paramref name="maxHeight"/>.
    /// </summary>
    private void FitToContents(BoxView view, int maxHeight)
    {
        var model = view.Model;
        if (model.Locked)
            return;
        var cells = CellsFor(view);
        var count = model.Items.Count;
        var columns = Math.Clamp(Grid(view).Columns, 1, Math.Max(1, count));
        int Rows() => Math.Max(1, (count + columns - 1) / columns);
        while (columns < count && cells.HeightFor(Rows()) > maxHeight)
            columns++;
        model.Width = Math.Max(view.Px(BoxSnapper.MinWidthDip), cells.WidthFor(columns));
        // A rolled-up box keeps the height it unrolls to.
        if (!model.Collapsed)
            model.Height = Math.Max(view.Px(BoxSnapper.MinHeightDip), cells.HeightFor(Rows()));
    }

    /// <summary>
    /// The first place, leftmost and then topmost, where a box of this size fits inside
    /// <paramref name="area"/> at least <paramref name="gap"/> away from every placed box.
    /// Tried at the area's corner and against the right and bottom sides of placed boxes, so boxes
    /// fill the gaps beside and under each other.
    /// </summary>
    private static RECT? FindSpot(RECT area, IReadOnlyList<RECT> placed, int width, int height, int gap)
    {
        var xs = placed.Select(p => p.Right + gap).Prepend(area.Left).Distinct().OrderBy(x => x).ToList();
        var ys = placed.SelectMany(p => new[] { p.Top, p.Bottom + gap }).Prepend(area.Top).Distinct().OrderBy(y => y).ToList();
        foreach (var x in xs)
        {
            foreach (var y in ys)
            {
                var spot = Bounds(x, y, width, height);
                if (spot.Left < area.Left || spot.Top < area.Top || spot.Right > area.Right || spot.Bottom > area.Bottom)
                    continue;
                if (!placed.Any(p => Intersects(spot, new RECT
                    {
                        Left = p.Left - gap, Top = p.Top - gap, Right = p.Right + gap, Bottom = p.Bottom + gap,
                    })))
                    return spot;
            }
        }
        return null;
    }

    // Swaps left and right, so packing from the right edge is packing from the left in a mirror.
    private static RECT Mirror(RECT r) => new() { Left = -r.Right, Top = r.Top, Right = -r.Left, Bottom = r.Bottom };

    /// <param name="edge">The edge being dragged when resizing; null when moving.</param>
    private void UpdateTarget(BoxView view, RECT target, ResizeEdge? edge)
    {
        if (_interaction?.View != view)
            return;
        // Holding Alt places the box exactly where it is dropped.
        var magnetic = !NativeMethods.IsAltDown();
        var snapped = edge is { } e
            ? _interaction.Snapper.Resize(target, e, magnetic)
            : _interaction.Snapper.Move(target, magnetic);
        _interaction.Target = snapped.Bounds;
        _interaction.Guides = snapped.Guides;
        _interaction.Label = snapped.Label;
        RequestFrame();
    }

    // Lines showing what a dragged box lined up with: at most one across and one down.
    private readonly PreviewWindow?[] _guides = new PreviewWindow?[2];
    private const int GuideRgb = 0x4FC3F7;

    private void ShowGuides(IReadOnlyList<RECT> guides)
    {
        for (var i = 0; i < _guides.Length; i++)
        {
            if (i >= guides.Count)
                _guides[i]?.Hide();
            else if (_guides[i] is { } guide)
                guide.Move(guides[i]);
            else if (_host != null)
                (_guides[i] = new PreviewWindow(_host.Owner)).Show(guides[i], GuideRgb, GuideRgb, GuideRgb, 0, 0);
        }
    }

    private void DisposeGuides()
    {
        for (var i = 0; i < _guides.Length; i++)
        {
            _guides[i]?.Dispose();
            _guides[i] = null;
        }
    }

    /// <summary>
    /// Mouse moves arrive far more often than the screen refreshes; move the outline to the
    /// latest position once per rendered frame.
    /// </summary>
    private void RequestFrame()
    {
        if (_framePending)
            return;
        _framePending = true;
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Render, () =>
        {
            _framePending = false;
            if (_interaction == null)
                return;
            _interaction.Frames++;
            _preview?.Move(_interaction.Target);
            _preview?.SetLabel(_interaction.Label);
            ShowGuides(_interaction.Guides);
        });
    }

    private void EndInteraction(BoxView view)
    {
        var interaction = _interaction;
        if (interaction == null || interaction.View != view)
            return;
        // Cleared before anything that can pump messages, so a new press during the drop starts clean.
        _interaction = null;

        var model = view.Model;
        var before = Bounds(model.X, model.Y, model.Width, view.VisibleHeight);
        var target = interaction.Target;
        var seconds = (DateTime.Now - interaction.StartedAt).TotalSeconds;
        var timing = "";

        if (!Same(before, target))
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            model.X = target.Left;
            model.Y = target.Top;
            model.Width = target.Right - target.Left;
            if (!model.Collapsed)
                model.Height = target.Bottom - target.Top;
            // Give explorer a moment to settle before treating positions as user moves.
            _suspendSyncUntil = DateTime.Now.AddMilliseconds(600);
            // Icons first: explorer takes a little longer to show them than the box needs to move,
            // so this way both arrive together.
            PlaceAfterDrop(view);
            var iconsMs = sw.Elapsed.TotalMilliseconds;
            view.ApplyBounds();
            var boxMs = sw.Elapsed.TotalMilliseconds - iconsMs;
            Save();
            _sweepTimer.Stop();
            _sweepTimer.Start();
            timing = $" drop: box={boxMs:F0}ms icons={iconsMs:F0}ms total={sw.Elapsed.TotalMilliseconds:F0}ms";
        }

        // The outline stays until the box and its icons are in place, so nothing flickers in between.
        _preview?.Hide();
        ShowGuides(Array.Empty<RECT>());
        view.SetDimmed(false);
        _interacting = _interaction != null;
        AppData.Log($"Drag '{model.Title}': {seconds:F1}s frames={interaction.Frames} " +
                    $"({interaction.Frames / Math.Max(0.1, seconds):F0}/s){timing}");
    }

    /// <summary>
    /// Lays out a dropped box's icons from the last sync's snapshot; reading every icon again
    /// here would hold up the drop by ~50 ms.
    /// </summary>
    private void PlaceAfterDrop(BoxView view)
    {
        if (_lastIcons != null)
        {
            try
            {
                // The snapshot's positions may be out of date, so every icon is placed regardless.
                ApplyLayout(new[] { view }, _lastIcons, force: true);
                return;
            }
            catch (COMException ex)
            {
                // Most likely an icon was deleted since the snapshot.
                AppData.Log($"Drop layout from snapshot failed, reading icons again: {ex.Message}");
            }
        }
        Layout(new[] { view });
    }

    /// <summary>Keeps the latest sync's snapshot for the next drop; the previous one is released.</summary>
    private void KeepSnapshot(IconSnapshot snapshot)
    {
        _lastSnapshot?.Dispose();
        _lastSnapshot = snapshot;
        _lastIcons = ByKey(snapshot);
    }

    /// <summary>Rolls a box up to its title bar, parking its icons off-screen, or back down.</summary>
    public void ToggleCollapse(BoxView view)
    {
        view.Model.Collapsed = !view.Model.Collapsed;
        view.ApplyStyle();
        view.ApplyBounds();
        Layout(new[] { view });
        _suspendSyncUntil = DateTime.Now.AddMilliseconds(600);
        Save();
    }

    /// <summary>
    /// Shrinks every box to its icons and packs the boxes on each screen tightly against the left
    /// or right edge, without gaps. Desktop icons that would end up under a box are moved to the left edge.
    /// </summary>
    public void ArrangeBoxes(bool fromRight)
    {
        if (_icons == null || _views.Count == 0)
            return;
        TakeSnapshot(Lang.T("boxes.beforeLiningUpBoxes"));
        var spacing = _icons.Spacing;
        var primary = Screen.PrimaryScreen!;

        var batch = WindowBatch.Begin();
        foreach (var group in _views.GroupBy(v => Screen.FromPoint(new System.Drawing.Point(
                     v.Model.X + v.Model.Width / 2, v.Model.Y + v.TitleHeight / 2)).DeviceName))
        {
            var screen = Screen.AllScreens.First(s => s.DeviceName == group.Key);
            var area = screen.WorkingArea;
            var scale = NativeMethods.ScaleAt(area.X + 1, area.Y + 1);
            var margin = (int)Math.Round(16 * scale);
            var gap = (int)Math.Round(14 * scale);
            // On the main screen keep the leftmost column free for icons that are not in a box.
            var reserved = screen.Primary && !fromRight ? spacing.X + margin : 0;

            var room = new RECT
            {
                Left = area.Left + margin + reserved,
                Top = area.Top + margin,
                Right = area.Right - margin,
                Bottom = area.Bottom - margin,
            };
            foreach (var view in group)
                FitToContents(view, room.Bottom - room.Top);

            // Packed like falling blocks, the tallest first, which leaves the fewest holes. Locked
            // boxes stay where they are and the others fit around them.
            RECT Flip(RECT r) => fromRight ? Mirror(r) : r;
            var placed = group.Where(v => v.Model.Locked)
                .Select(v => Flip(Bounds(v.Model.X, v.Model.Y, v.Model.Width, v.VisibleHeight)))
                .ToList();
            foreach (var view in group.Where(v => !v.Model.Locked)
                         .OrderByDescending(v => v.VisibleHeight).ThenByDescending(v => v.Model.Width))
            {
                if (FindSpot(Flip(room), placed, view.Model.Width, view.VisibleHeight, gap) is not { } spot)
                {
                    AppData.Log($"No room for '{view.Model.Title}' when lining up; left where it was");
                    placed.Add(Flip(Bounds(view.Model.X, view.Model.Y, view.Model.Width, view.VisibleHeight)));
                    continue;
                }
                placed.Add(spot);
                var bounds = Flip(spot);
                (view.Model.X, view.Model.Y) = (bounds.Left, bounds.Top);
            }
            foreach (var view in group)
                view.ApplyBounds();
        }
        batch.Dispose();

        using var snapshot = _icons.Snapshot();
        var byKey = ByKey(snapshot);
        var boxed = _views.SelectMany(v => v.Model.Items).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var covered = byKey.Values
            .Where(icon => !boxed.Contains(icon.Key) && _views.Any(v =>
                v.Contains(new POINT(icon.Position.X + spacing.X / 2, icon.Position.Y + spacing.Y / 2))))
            .ToList();
        var left = primary.WorkingArea.Left + spacing.X / 4;
        var top = primary.WorkingArea.Top + spacing.Y / 4;
        var taken = byKey.Values.Where(i => !boxed.Contains(i.Key) && !covered.Contains(i)).Select(i => i.Position).ToList();
        var moves = new List<(DesktopIcon, POINT)>();
        var row = 0;
        foreach (var icon in covered)
        {
            POINT slot;
            do
                slot = new POINT(left, top + row++ * spacing.Y);
            while (taken.Any(p => Near(p, slot, spacing.Y / 2)));
            moves.Add((icon, slot));
        }
        _icons.Move(moves);

        ApplyLayout(_views, byKey);
        _suspendSyncUntil = DateTime.Now.AddMilliseconds(800);
        Save();
        AppData.Log($"Arranged {_views.Count} boxes from the {(fromRight ? "right" : "left")}; moved {moves.Count} uncovered icons");
    }

    private void DeleteBox(BoxView view)
    {
        TakeSnapshot(Lang.T("boxes.beforeDeleting", view.Model.Title));
        foreach (var key in view.Model.Items)
            _lastTargets.Remove(key);
        _views.Remove(view);
        _config.Boxes.Remove(view.Model);
        view.Dispose();
        Save();
    }

    private void Sort(BoxView view, BoxSortOrder order)
    {
        if (_icons == null)
            return;
        using var snapshot = _icons.Snapshot();
        var icons = ByKey(snapshot);
        string Name(string key) => icons.TryGetValue(key, out var icon) ? icon.DisplayName : key;
        // Items that are not plain files or folders (This PC, Recycle Bin) have no date or size
        // and sort after the ones that do.
        FileSystemInfo? Info(string key) =>
            File.Exists(key) ? new FileInfo(key) : Directory.Exists(key) ? new DirectoryInfo(key) : null;

        var byName = StringComparer.CurrentCultureIgnoreCase;
        IOrderedEnumerable<string> sorted = order switch
        {
            BoxSortOrder.Kind => view.Model.Items
                .OrderBy(key => icons.TryGetValue(key, out var icon) && Categorizer.Classify(icon) is { } category
                    ? Array.IndexOf(Categorizer.All, category)
                    : int.MaxValue)
                .ThenBy(Name, byName),
            BoxSortOrder.Modified => view.Model.Items
                .OrderByDescending(key => Info(key)?.LastWriteTime ?? DateTime.MinValue)
                .ThenBy(Name, byName),
            BoxSortOrder.Size => view.Model.Items
                // Folders count as size zero rather than being added up.
                .OrderByDescending(key => Info(key) is FileInfo file ? file.Length : -1)
                .ThenBy(Name, byName),
            _ => view.Model.Items.OrderBy(Name, byName),
        };
        view.Model.Items = sorted.ToList();
        ApplyLayout(new[] { view }, icons);
        Save();
        AppData.Log($"Sorted '{view.Model.Title}' by {order}");
    }

    private void Sync()
    {
        // Hidden icons report no useful positions, so leave everything as it is until shown again.
        if (_interacting || _desktopHidden || DateTime.Now < _suspendSyncUntil)
            return;
        if (WaitForScreens())
            return;
        if (_host == null || _icons == null || !_host.IsAlive)
        {
            Reattach();
            return;
        }

        try
        {
            var snapshot = _icons.Snapshot();
            KeepSnapshot(snapshot);
            if (snapshot.Icons.Count == 0)
                return;

            var byKey = ByKey(snapshot);
            var spacing = _icons.Spacing;
            var changed = false;

            var newKeys = _knownKeysReady
                ? byKey.Keys.Where(key => !_knownKeys.Contains(key)).ToHashSet(StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>();
            _knownKeys.UnionWith(byKey.Keys);
            _knownKeysReady = true;

            // While explorer re-reads the desktop (after files change) the list it gives can be
            // missing items that still exist. So an item leaves its box only once it is really gone:
            // a file or folder no longer on disk, or another item missing from two lists in a row.
            var missing = _views.SelectMany(v => v.Model.Items).Where(key => !byKey.ContainsKey(key))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var gone = missing.Where(key => IsFileSystemPath(key)
                    ? !File.Exists(key) && !Directory.Exists(key)
                    : _missingLastSync.Contains(key))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            _missingLastSync = missing;
            foreach (var view in _views)
                changed |= view.Model.Items.RemoveAll(gone.Contains) > 0;
            if (missing.Count > gone.Count)
            {
                // Some boxed items are not in the list but still exist: the list is incomplete, so
                // wait for the next one rather than move anything based on it.
                AppData.Log($"Desktop list incomplete ({missing.Count - gone.Count} boxed items missing); waiting");
                if (changed)
                    Save();
                return;
            }

            foreach (var icon in byKey.Values)
            {
                var center = new POINT(icon.Position.X + spacing.X / 2, icon.Position.Y + spacing.Y / 2);
                var owner = _views.FirstOrDefault(v => v.Model.Items.Contains(icon.Key, StringComparer.OrdinalIgnoreCase));
                var dropTarget = _views.FirstOrDefault(v => v.Contains(center));

                if (owner != null)
                {
                    // Not laid out yet, still where we put it, or parked by a rolled-up box
                    // (if explorer pulled it back on screen, the layout below parks it again).
                    if (owner.Model.Collapsed ||
                        !_lastTargets.TryGetValue(icon.Key, out var target) || Near(target, icon.Position))
                        continue;
                    owner.Model.Items.RemoveAll(k => string.Equals(k, icon.Key, StringComparison.OrdinalIgnoreCase));
                    _lastTargets.Remove(icon.Key);
                }
                else if (dropTarget == null && newKeys.Contains(icon.Key) && HomeFor(icon) is { } home)
                {
                    // A new item on the desktop goes to the box a rule names, or the one collecting its category.
                    home.Box.Model.Items.Add(icon.Key);
                    changed = true;
                    _history?.Add(HistoryKind.Boxed, icon.Key, Lang.T("boxes.putInBox", home.Box.Model.Title, home.Reason));
                    AppData.Log($"New item '{icon.DisplayName}' -> {home.Box.Model.Title} ({home.Reason})");
                    continue;
                }

                if (dropTarget != null)
                    dropTarget.Model.Items.Insert(SlotIndex(dropTarget, center, spacing), icon.Key);

                if (owner != null || dropTarget != null)
                {
                    changed = true;
                    AppData.Log($"'{icon.DisplayName}': {owner?.Model.Title ?? "desktop"} -> {dropTarget?.Model.Title ?? "desktop"}");
                }
            }

            if (changed)
            {
                Save();
                // Rolled-up title bars show how many icons they hold.
                foreach (var view in _views.Where(v => v.Model.Collapsed))
                    view.Title.ApplyStyle(view.Model);
            }
            ApplyLayout(_views, byKey);
        }
        catch (COMException ex)
        {
            AppData.Log($"Sync failed, reconnecting: {ex.Message}");
            Reattach();
        }
    }

    private HashSet<string> _missingLastSync = new(StringComparer.OrdinalIgnoreCase);

    // How long the monitor setup must stay the same before the boxes are rebuilt on it.
    private static readonly TimeSpan ScreensSettleTime = TimeSpan.FromSeconds(3);
    private (int, int, int, int, int, int, int) _screenLayout = NativeMethods.ScreenLayout();
    private DateTime? _screensSettleAt;

    /// <summary>
    /// Holds syncing back after the monitors change (Win+P, plugging one in or out). Explorer resizes
    /// its desktop windows and moves icons around for a few seconds afterwards; reading the desktop
    /// then would take explorer's moves for the user's, and the box bodies are left at their old
    /// offsets inside the resized wallpaper window. Once the setup has settled, everything is
    /// attached again from scratch, as on a fresh start.
    /// </summary>
    private bool WaitForScreens()
    {
        var layout = NativeMethods.ScreenLayout();
        if (layout != _screenLayout)
        {
            AppData.Log($"Screens changed: {_screenLayout} -> {layout}");
            _screenLayout = layout;
            _screensSettleAt = DateTime.Now + ScreensSettleTime;
            return true;
        }
        if (_screensSettleAt == null)
            return false;
        if (DateTime.Now < _screensSettleAt)
            return true;
        _screensSettleAt = null;
        Reattach();
        return _host == null;
    }

    /// <summary>A plain file or folder path, as opposed to a shell item such as "::{GUID}" (This PC).</summary>
    private static bool IsFileSystemPath(string key) =>
        !key.StartsWith("::", StringComparison.Ordinal) && Path.IsPathRooted(key);

    private (BoxView Box, string Reason)? HomeFor(DesktopIcon icon)
    {
        if (_ruleEngine?.RuleFor(icon.Key) is { } rule)
        {
            // A moving rule is about to take the file off the desktop.
            if (rule.MovesFiles)
                return null;
            if (_views.FirstOrDefault(v => v.Model.Id == rule.BoxId) is { } ruled)
                return (ruled, Lang.T("boxes.rule", rule.Name));
        }
        var category = Categorizer.Classify(icon);
        if (category == null || _views.FirstOrDefault(v => v.Model.Category == category.Id) is not { } home)
            return null;
        return (home, Lang.T("boxes.kind", category.Title));
    }

    /// <summary>
    /// Puts every icon that is not already in a box into a box for its purpose, creating boxes as needed.
    /// Items with no category (Recycle Bin, This PC) are lined up at the left edge of the main screen.
    /// </summary>
    /// <returns>How many icons were sorted into boxes.</returns>
    /// <summary>
    /// Everything in one go: sorts loose icons into boxes by kind, then shrinks the boxes to their
    /// icons and packs them against the left edge.
    /// </summary>
    /// <returns>How many icons were put in boxes.</returns>
    public int TidyUp()
    {
        var sorted = AutoOrganize();
        ArrangeBoxes(fromRight: false);
        return sorted;
    }

    public int AutoOrganize()
    {
        if (_icons == null)
            return 0;
        TakeSnapshot(Lang.T("boxes.beforeSortingByKind"));
        PrepareFolderFlags();

        using var snapshot = _icons.Snapshot();
        var byKey = ByKey(snapshot);
        var spacing = _icons.Spacing;
        var boxed = _views.SelectMany(v => v.Model.Items).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var loose = byKey.Values
            .Where(icon => !boxed.Contains(icon.Key))
            .Select(icon => (Icon: icon, Category: Categorizer.Classify(icon)))
            .ToList();
        var groups = loose
            .Where(x => x.Category != null)
            .GroupBy(x => x.Category!)
            .ToDictionary(
                g => g.Key,
                g => g.Select(x => x.Icon).OrderBy(i => i.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList());
        var stayOnDesktop = loose.Where(x => x.Category == null).Select(x => x.Icon).ToList();

        var primary = Screen.PrimaryScreen!;
        var scale = NativeMethods.ScaleAt(primary.Bounds.X + 1, primary.Bounds.Y + 1);
        int Px(double dip) => (int)Math.Round(dip * scale);
        var margin = Px(16);
        var gap = Px(14);
        var pad = Px(6);
        var titleHeight = Px(BoxTitleBar.HeightDip);

        var screens = Screen.AllScreens.OrderByDescending(s => s.Primary).ThenBy(s => s.Bounds.X).ToList();
        var screenIndex = 0;
        var area = screens[0].WorkingArea;
        // Leave a column on the left of the main screen for the items that stay on the desktop.
        var x = area.Left + margin + (stayOnDesktop.Count > 0 ? spacing.X + margin : 0);
        var y = area.Top + margin;
        var columnWidth = 0;
        var occupied = _views.Select(v => new RECT
        {
            Left = v.Model.X - gap,
            Top = v.Model.Y - gap,
            Right = v.Model.X + v.Model.Width + gap,
            Bottom = v.Model.Y + v.Model.Height + gap,
        }).ToList();

        var created = new List<BoxModel>();
        foreach (var category in Categorizer.All)
        {
            if (!groups.TryGetValue(category, out var icons))
                continue;
            var keys = icons.Select(i => i.Key).ToList();

            if (_views.FirstOrDefault(v => v.Model.Category == category.Id) is { } existing)
            {
                existing.Model.Items.AddRange(keys);
                continue;
            }

            var maxRows = Math.Max(1, (area.Height - 2 * margin - titleHeight - 2 * pad - Px(10)) / spacing.Y);
            var columns = Math.Max(4, (int)Math.Ceiling(icons.Count / (double)maxRows));
            var rows = (int)Math.Ceiling(icons.Count / (double)columns);
            var width = GridWidth(columns, Px(IconGapDip)) + 2 * pad;
            var height = titleHeight + rows * spacing.Y + 2 * pad + Px(10);

            // Fill columns top to bottom, then move right, then on to the next screen.
            while (true)
            {
                if (y + height > area.Bottom - margin && y > area.Top + margin)
                {
                    x += columnWidth + gap;
                    y = area.Top + margin;
                    columnWidth = 0;
                }
                if (x + width > area.Right - margin && screenIndex + 1 < screens.Count)
                {
                    area = screens[++screenIndex].WorkingArea;
                    x = area.Left + margin;
                    y = area.Top + margin;
                    columnWidth = 0;
                    continue;
                }

                var candidate = new RECT { Left = x, Top = y, Right = x + width, Bottom = y + height };
                var hit = occupied.FindIndex(r => Intersects(r, candidate));
                if (hit < 0 || x + width > area.Right - margin)
                    break;
                y = occupied[hit].Bottom;
                columnWidth = Math.Max(columnWidth, occupied[hit].Right - x);
            }

            var model = new BoxModel
            {
                Title = category.Title,
                Color = category.Color,
                Category = category.Id,
                X = x,
                Y = y,
                Width = width,
                Height = height,
                Items = keys,
            };
            created.Add(model);
            occupied.Add(new RECT { Left = x - gap, Top = y - gap, Right = x + width + gap, Bottom = y + height + gap });
            y += height + gap;
            columnWidth = Math.Max(columnWidth, width);
        }

        foreach (var model in created)
        {
            _config.Boxes.Add(model);
            AddView(model);
        }

        var left = primary.WorkingArea.Left + margin / 2;
        var top = primary.WorkingArea.Top + margin;
        _icons.Move(stayOnDesktop.Select((icon, i) => (icon, new POINT(left, top + i * spacing.Y))).ToList());

        Save();
        ApplyLayout(_views, byKey);
        _suspendSyncUntil = DateTime.Now.AddMilliseconds(800);

        var sorted = groups.Values.Sum(g => g.Count);
        AppData.Log($"Auto-organized {sorted} icons into {groups.Count} categories ({created.Count} new boxes); " +
                    $"{stayOnDesktop.Count} left on the desktop");
        return sorted;
    }

    private void Layout(IEnumerable<BoxView> views)
    {
        if (_icons == null)
            return;
        try
        {
            using var snapshot = _icons.Snapshot();
            ApplyLayout(views, ByKey(snapshot));
        }
        catch (COMException ex)
        {
            AppData.Log($"Layout failed: {ex.Message}");
        }
    }

    /// <returns>How many icons were moved.</returns>
    /// <param name="force">Move every icon, even ones the snapshot says are already in place.</param>
    private int ApplyLayout(IEnumerable<BoxView> views, Dictionary<string, DesktopIcon> byKey, bool force = false)
    {
        var spacing = _icons!.Spacing;
        var moves = new List<(DesktopIcon, POINT)>();
        foreach (var view in views)
        {
            var slots = view.Model.Collapsed
                ? Enumerable.Repeat(ParkingSpot, view.Model.Items.Count).ToArray()
                : Slots(view, view.Model.Items.Count, spacing);
            for (var i = 0; i < view.Model.Items.Count; i++)
            {
                if (!byKey.TryGetValue(view.Model.Items[i], out var icon))
                    continue;
                _lastTargets[icon.Key] = slots[i];
                if (force || !Near(slots[i], icon.Position, 0))
                    moves.Add((icon, slots[i]));
            }
        }
        _icons!.Move(moves);
        if (moves.Count > 0)
            _icons.RepaintAll();
        return moves.Count;
    }

    /// <summary>Least space between two icons side by side in a box, in device-independent pixels.</summary>
    private const double IconGapDip = 12;

    /// <summary>Width a row of icons needs, with a gap between them and at both ends.</summary>
    private int GridWidth(int columns, int gap) => columns * _icons!.ItemBox.Width + (columns + 1) * gap;

    /// <returns>Where the first icon goes and how far apart the columns are.</returns>
    private (int Columns, int Left, int Top, int Stride) Grid(BoxView view)
    {
        var (offset, cell) = _icons!.ItemBox;
        var body = view.Body;
        var pad = view.Px(6);
        var inner = body.Right - body.Left - 2 * pad;
        var gap = view.Px(IconGapDip);
        var columns = Math.Max(1, (inner - gap) / Math.Max(1, cell + gap));
        // What is left over is shared equally by the two edges and the gaps between icons,
        // so the margins on both sides match.
        var space = Math.Max(0, (inner - columns * cell) / (columns + 1));
        return (columns, body.Left + pad + space + offset, body.Top + pad, cell + space);
    }

    private POINT[] Slots(BoxView view, int count, POINT spacing)
    {
        var (columns, left, top, stride) = Grid(view);
        var slots = new POINT[count];
        for (var i = 0; i < count; i++)
            slots[i] = new POINT(left + i % columns * stride, top + i / columns * spacing.Y);
        return slots;
    }

    private int SlotIndex(BoxView view, POINT center, POINT spacing)
    {
        var (columns, left, top, stride) = Grid(view);
        // The center is measured from the position, so the slot's position counts from its middle.
        var column = Math.Clamp((center.X - left + stride / 2 - spacing.X / 2) / Math.Max(1, stride), 0, columns - 1);
        var row = Math.Max(0, (center.Y - top) / Math.Max(1, spacing.Y));
        return Math.Min(row * columns + column, view.Model.Items.Count);
    }

    private (int X, int Y) FindFreeSpot(int width, int height)
    {
        var area = Screen.PrimaryScreen!.WorkingArea;
        var margin = 24;
        var occupied = new List<RECT>();

        using (var snapshot = _icons!.Snapshot())
        {
            var spacing = _icons.Spacing;
            occupied.AddRange(snapshot.Icons.Select(icon => new RECT
            {
                Left = icon.Position.X,
                Top = icon.Position.Y,
                Right = icon.Position.X + spacing.X,
                Bottom = icon.Position.Y + spacing.Y,
            }));
        }
        occupied.AddRange(_views.Select(v => new RECT
        {
            Left = v.Model.X - margin,
            Top = v.Model.Y - margin,
            Right = v.Model.X + v.Model.Width + margin,
            Bottom = v.Model.Y + v.Model.Height + margin,
        }));

        for (var y = area.Top + margin; y + height <= area.Bottom - margin; y += 40)
        {
            for (var x = area.Right - width - margin; x >= area.Left + margin; x -= 40)
            {
                var candidate = new RECT { Left = x, Top = y, Right = x + width, Bottom = y + height };
                if (!occupied.Any(r => Intersects(r, candidate)))
                    return (x, y);
            }
        }

        return (area.Left + (area.Width - width) / 2, area.Top + (area.Height - height) / 2);
    }

    private static bool Intersects(RECT a, RECT b) =>
        a.Left < b.Right && b.Left < a.Right && a.Top < b.Bottom && b.Top < a.Bottom;

    private static bool Near(POINT a, POINT b, int tolerance = PositionTolerance) =>
        Math.Abs(a.X - b.X) <= tolerance && Math.Abs(a.Y - b.Y) <= tolerance;

    private static Dictionary<string, DesktopIcon> ByKey(IconSnapshot snapshot)
    {
        var byKey = new Dictionary<string, DesktopIcon>(StringComparer.OrdinalIgnoreCase);
        foreach (var icon in snapshot.Icons)
            byKey.TryAdd(icon.Key, icon);
        return byKey;
    }

    private static readonly TimeSpan AutoSnapshotInterval = TimeSpan.FromMinutes(30);

    private void Save()
    {
        AppData.SaveConfig(_config);
        // Every so often keep a copy of the layout, so a bad change can be undone later.
        if (DateTime.Now - _lastAutoSnapshot > AutoSnapshotInterval && _views.Count > 0 && !_desktopHidden)
            TakeSnapshot(Lang.T("boxes.savedAutomatically"));
    }

    public void Dispose()
    {
        _syncTimer.Stop();
        _sweepTimer.Stop();
        _mouse?.Dispose();
        _watcher?.Dispose();
        _moves?.Dispose();
        _historyPanel?.Close();
        Panel?.Dispose();
        // Never leave the desktop without icons when the app closes.
        if (_desktopHidden || _config.IconsHidden)
            SetIconsHidden(false);
        foreach (var view in _views)
            view.Dispose();
        _views.Clear();
        _preview?.Dispose();
        DisposeGuides();
        _lastSnapshot?.Dispose();
    }
}
