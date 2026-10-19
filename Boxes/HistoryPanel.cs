using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using TidyDesktop.Desktop;
using TidyDesktop.Native;
using static TidyDesktop.Native.NativeMethods;

namespace TidyDesktop.Boxes;

/// <summary>
/// A panel on the desktop listing recent changes: new, removed and renamed items, and what boxes
/// and rules did with them. Moves made by rules can be undone from here.
/// </summary>
internal sealed class HistoryPanel : DesktopLevelWindow
{
    public const double HeaderDip = 30;
    private const int ShownEntries = 150;

    private static readonly Brush Text = new SolidColorBrush(Color.FromRgb(0xF2, 0xF2, 0xF4));
    private static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(0xA0, 0xA0, 0xAA));
    private static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(0x7F, 0xB8, 0xFF));

    private readonly DesktopHistory _history;
    private readonly HistoryPanelSettings _settings;
    private readonly Func<HistoryEntry, bool> _undo;
    private readonly Action _changed;
    private readonly StackPanel _list = new();
    private readonly ScrollViewer _scroller;
    private readonly TextBlock _title = new()
    {
        Foreground = Text,
        FontSize = 13,
        FontWeight = FontWeights.SemiBold,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(10, 0, 0, 0),
    };

    // Physical-pixel drag state for moving (header) and resizing (corner).
    private POINT _dragStart;
    private (int X, int Y, int W, int H) _boundsAtStart;
    private bool _resizing;

    /// <param name="undo">Puts a moved file back; returns whether it worked.</param>
    /// <param name="changed">Called after the panel was moved, resized or rolled up, to save that.</param>
    public HistoryPanel(DesktopHistory history, HistoryPanelSettings settings, Func<HistoryEntry, bool> undo, Action changed)
    {
        _history = history;
        _settings = settings;
        _undo = undo;
        _changed = changed;

        var clear = HeaderButton(Lang.T("history.clear"), Lang.T("history.clearsWholeList"));
        clear.Click += (_, _) =>
        {
            if (MessageBox.Show(Lang.T("history.clearAllRecentChanges"), "TidyDesktop", MessageBoxButton.OKCancel,
                    MessageBoxImage.Question) == MessageBoxResult.OK)
                _history.Clear();
        };
        var collapse = HeaderButton("▾", Lang.T("box.rollUpUnroll"));
        collapse.Click += (_, _) => ToggleCollapsed();

        var header = new DockPanel { Height = HeaderDip, Background = new SolidColorBrush(Color.FromArgb(0xF0, 0x26, 0x26, 0x2E)) };
        DockPanel.SetDock(collapse, Dock.Right);
        DockPanel.SetDock(clear, Dock.Right);
        header.Children.Add(collapse);
        header.Children.Add(clear);
        header.Children.Add(_title);
        header.Cursor = Cursors.SizeAll;
        header.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2)
            {
                ToggleCollapsed();
                return;
            }
            BeginDrag(resize: false, (UIElement)header);
        };
        header.MouseMove += (_, _) => ContinueDrag();
        header.MouseLeftButtonUp += (_, _) => EndDrag();

        _scroller = new ScrollViewer
        {
            Content = _list,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(0, 2, 0, 2),
        };

        var corner = new Border
        {
            Width = 14,
            Height = 14,
            Background = new SolidColorBrush(Color.FromArgb(0x02, 0, 0, 0)),
            Cursor = Cursors.SizeNWSE,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Child = new TextBlock { Text = "◢", FontSize = 10, Foreground = Muted, HorizontalAlignment = HorizontalAlignment.Right },
        };
        corner.MouseLeftButtonDown += (_, _) => BeginDrag(resize: true, corner);
        corner.MouseMove += (_, _) => ContinueDrag();
        corner.MouseLeftButtonUp += (_, _) => EndDrag();

        var body = new Grid();
        body.Children.Add(_scroller);
        body.Children.Add(corner);

        var layout = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        layout.Children.Add(header);
        layout.Children.Add(body);

        Content = new Border
        {
            CornerRadius = new CornerRadius(8),
            ClipToBounds = true,
            Background = new SolidColorBrush(Color.FromArgb(0xD8, 0x18, 0x18, 0x1C)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            Child = layout,
        };

        _history.Changed += Refresh;
        Refresh();
    }

    private double Scale => ScaleAt(_settings.X + 10, _settings.Y + 10);

    private int Px(double dip) => (int)Math.Round(dip * Scale);

    /// <summary>Puts the panel where the settings say, or at the top right of the main screen the first time.</summary>
    public void ApplyBounds()
    {
        if (_settings.Width <= 0 || _settings.Height <= 0)
        {
            var area = System.Windows.Forms.Screen.PrimaryScreen!.WorkingArea;
            var scale = ScaleAt(area.Right - 1, area.Top + 1);
            _settings.Width = (int)Math.Round(340 * scale);
            _settings.Height = (int)Math.Round(420 * scale);
            _settings.X = area.Right - _settings.Width - (int)Math.Round(16 * scale);
            _settings.Y = area.Top + (int)Math.Round(16 * scale);
        }
        var height = _settings.Collapsed ? Px(HeaderDip) : _settings.Height;
        SetBounds(_settings.X, _settings.Y, _settings.Width, height);
    }

    private void ToggleCollapsed()
    {
        _settings.Collapsed = !_settings.Collapsed;
        ApplyBounds();
        Refresh();
        _changed();
    }

    private void BeginDrag(bool resize, UIElement capture)
    {
        GetCursorPos(out _dragStart);
        _boundsAtStart = (_settings.X, _settings.Y, _settings.Width, _settings.Height);
        _resizing = resize;
        capture.CaptureMouse();
    }

    private void ContinueDrag()
    {
        if (Mouse.Captured == null)
            return;
        if (!IsPrimaryButtonDown())
        {
            EndDrag();
            return;
        }
        GetCursorPos(out var now);
        var (dx, dy) = (now.X - _dragStart.X, now.Y - _dragStart.Y);
        if (_resizing)
        {
            _settings.Width = Math.Max(Px(220), _boundsAtStart.W + dx);
            _settings.Height = Math.Max(Px(120), _boundsAtStart.H + dy);
        }
        else
        {
            _settings.X = _boundsAtStart.X + dx;
            _settings.Y = _boundsAtStart.Y + dy;
        }
        ApplyBounds();
    }

    private void EndDrag()
    {
        if (Mouse.Captured == null)
            return;
        Mouse.Capture(null);
        _changed();
    }

    private void Refresh()
    {
        var entries = _history.Entries;
        _title.Text = Lang.T("history.recentChanges", entries.Count);
        _list.Children.Clear();
        if (_settings.Collapsed)
            return;
        if (entries.Count == 0)
        {
            _list.Children.Add(new TextBlock
            {
                Text = Lang.T("history.empty"),
                Foreground = Muted,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(10, 8, 10, 8),
            });
            return;
        }

        DateTime? day = null;
        foreach (var entry in entries.Take(ShownEntries))
        {
            if (day != entry.Time.Date)
            {
                day = entry.Time.Date;
                _list.Children.Add(new TextBlock
                {
                    Text = day == DateTime.Today ? Lang.T("history.today")
                        : day == DateTime.Today.AddDays(-1) ? Lang.T("history.yesterday")
                        : Lang.Date(day!.Value, "format.dayHeading"),
                    Foreground = Muted,
                    FontSize = 11,
                    Margin = new Thickness(10, 8, 10, 2),
                });
            }
            _list.Children.Add(Row(entry));
        }
    }

    private UIElement Row(HistoryEntry entry)
    {
        var (glyph, summary) = Describe(entry);
        var grid = new Grid { Margin = new Thickness(6, 1, 6, 1), Background = Brushes.Transparent, Cursor = Cursors.Hand };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var time = new TextBlock { Text = $"{entry.Time:HH:mm}", Foreground = Muted, FontSize = 11, Margin = new Thickness(4, 3, 0, 0) };
        var icon = new TextBlock { Text = glyph, Foreground = Text, FontSize = 12, Margin = new Thickness(0, 2, 0, 0) };
        var text = new StackPanel();
        text.Children.Add(new TextBlock
        {
            Text = entry.Name,
            Foreground = entry.Undone ? Muted : Text,
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextDecorations = entry.Undone ? TextDecorations.Strikethrough : null,
        });
        text.Children.Add(new TextBlock
        {
            Text = summary,
            Foreground = Muted,
            FontSize = 11,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        Grid.SetColumn(time, 0);
        Grid.SetColumn(icon, 1);
        Grid.SetColumn(text, 2);
        grid.Children.Add(time);
        grid.Children.Add(icon);
        grid.Children.Add(text);

        if (entry.CanUndo)
        {
            var undo = new Button
            {
                Content = Lang.T("history.undo"),
                FontSize = 11,
                Foreground = Accent,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                Padding = new Thickness(6, 2, 2, 2),
                VerticalAlignment = VerticalAlignment.Center,
            };
            undo.Click += (_, e) =>
            {
                e.Handled = true;
                _undo(entry);
            };
            Grid.SetColumn(undo, 3);
            grid.Children.Add(undo);
        }

        grid.ToolTip = string.Join("\n", new[] { entry.Path, entry.Destination == MoveTracker.RecycleBin ? Lang.T("history.recycleBin") : entry.Destination }
            .Where(p => p != null).Distinct());
        grid.MouseLeftButtonUp += (_, _) => Open(entry);
        grid.MouseEnter += (_, _) => grid.Background = new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF));
        grid.MouseLeave += (_, _) => grid.Background = Brushes.Transparent;
        return grid;
    }

    private static (string Glyph, string Summary) Describe(HistoryEntry e) => e.Kind switch
    {
        HistoryKind.Added => ("＋", Directory.Exists(e.Path) ? Lang.T("history.newFolder") : Lang.T("history.newFile")),
        HistoryKind.Removed => e.Destination switch
        {
            MoveTracker.RecycleBin => ("🗑", Lang.T("history.movedRecycleBin")),
            { } destination => ("→", Lang.T("history.movedFolder", FolderName(destination))),
            null when e.Traced => ("－", Lang.T("history.deletedForGoodNot")),
            _ => ("－", Lang.T("history.deletedOrMovedElsewhere")),
        },
        HistoryKind.Renamed => ("✎", Lang.T("history.renamedWas", DesktopHistory.DisplayName(e.OldPath ?? ""))),
        HistoryKind.Boxed => ("▣", e.Detail ?? Lang.T("history.putInBox")),
        HistoryKind.Moved => ("→", Lang.T("history.movedByRule", FolderName(e.Destination)) +
                                  $" · {e.Detail}{(e.Undone ? Lang.T("history.undone") : "")}"),
        HistoryKind.Undone => ("↩", e.Detail ?? Lang.T("history.putBackOnDesktop")),
        _ => ("!", e.Detail ?? Lang.T("history.somethingWentWrong")),
    };

    private static string FolderName(string? path) => Path.GetFileName(Path.GetDirectoryName(path)) ?? "";

    /// <summary>
    /// Where the item is now: its desktop path, or, once it has left the desktop, where it went
    /// (also when this entry is older than the move).
    /// </summary>
    private string? CurrentPath(HistoryEntry entry)
    {
        if (entry.Kind == HistoryKind.Moved && !entry.Undone)
            return entry.Destination;
        if (entry.Kind == HistoryKind.Removed)
            return entry.Destination;
        if (File.Exists(entry.Path) || Directory.Exists(entry.Path))
            return entry.Path;
        // The history is newest first: the latest departure of this path that came after the entry.
        var left = _history.Entries.FirstOrDefault(e =>
            e.Time >= entry.Time && e.Destination != null &&
            ((e.Kind == HistoryKind.Moved && !e.Undone) || e.Kind == HistoryKind.Removed) &&
            string.Equals(e.Path, entry.Path, StringComparison.OrdinalIgnoreCase));
        return left?.Destination;
    }

    /// <summary>
    /// An item still on the desktop is opened, like double-clicking it. One that went somewhere else
    /// is shown in its folder, selected; one in the recycle bin opens the recycle bin.
    /// </summary>
    private void Open(HistoryEntry entry)
    {
        var current = CurrentPath(entry);
        try
        {
            if (current == MoveTracker.RecycleBin)
                Process.Start("explorer.exe", "shell:RecycleBinFolder");
            else if (current == null || !(File.Exists(current) || Directory.Exists(current)))
            {
                // Gone, or moved again since: the folder it went to is the best guess.
                if (Path.GetDirectoryName(current ?? "") is { Length: > 0 } folder && Directory.Exists(folder) &&
                    !DesktopWatcher.Folders.Contains(folder, StringComparer.OrdinalIgnoreCase))
                    Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
            }
            else if (DesktopWatcher.Folders.Contains(Path.GetDirectoryName(current) ?? "", StringComparer.OrdinalIgnoreCase))
                Process.Start(new ProcessStartInfo(current) { UseShellExecute = true });
            else
                Process.Start("explorer.exe", $"/select,\"{current}\"");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            AppData.Log($"Could not open {current}: {ex.Message}");
        }
    }

    private static Button HeaderButton(string text, string tip) => new()
    {
        Content = text,
        ToolTip = tip,
        Foreground = Muted,
        Background = Brushes.Transparent,
        BorderThickness = new Thickness(0),
        Padding = new Thickness(8, 0, 8, 0),
        FontSize = 11,
        Cursor = Cursors.Hand,
    };

    protected override void OnClosed(EventArgs e)
    {
        _history.Changed -= Refresh;
        base.OnClosed(e);
    }
}
