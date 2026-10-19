using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using TidyDesktop.Native;
using static TidyDesktop.Native.NativeMethods;

namespace TidyDesktop.Boxes;

/// <summary>Reports pointer drags in physical pixels, relative to where the drag started.</summary>
public delegate void PixelDragHandler(int dx, int dy);

/// <summary>Orders for the icons in a box.</summary>
public enum BoxSortOrder
{
    Name,
    Kind,
    Modified,
    Size,
}

/// <summary>
/// Title bar of a box: double-click to roll up or down, drag to move, right-click for the menu
/// (which also has renaming).
/// </summary>
public sealed class BoxTitleBar : DesktopLevelWindow
{
    public const double HeightDip = 30;

    private readonly BoxModel _model;
    private readonly TextBlock _title;
    private readonly TextBox _editor;
    private readonly Border _surface;

    // Running between the first click and the end of the double-click time.
    private readonly DispatcherTimer _clickTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(Forms_DoubleClickTime()),
    };

    private POINT _pressStart;
    private bool _pressed;
    private bool _dragging;

    public event Action? DragStarted;
    public event PixelDragHandler? Dragging;
    public event Action? DragEnded;
    public event Action<string>? Renamed;
    public event Action? CollapseToggled;
    public event Action<BoxSortOrder>? SortRequested;
    public event Action? LockToggled;
    public event Action? DeleteRequested;
    public event Action? NewBoxRequested;
    public event Action? StyleRequested;
    public event Action<bool>? ArrangeRequested;

    public BoxTitleBar(BoxModel model)
    {
        _model = model;
        _title = new TextBlock
        {
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 10, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        _editor = new TextBox
        {
            Visibility = Visibility.Collapsed,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 6, 0),
            FontSize = 13,
        };
        _editor.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
                EndRename(commit: true);
            else if (e.Key == Key.Escape)
                EndRename(commit: false);
        };
        _editor.LostKeyboardFocus += (_, _) => EndRename(commit: true);

        var grid = new Grid();
        grid.Children.Add(_title);
        grid.Children.Add(_editor);

        _surface = new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1, 1, 1, 0),
            Child = grid,
            Cursor = Cursors.SizeAll,
            ContextMenu = BuildMenu(),
        };
        // Built afresh each time so it follows a change of language.
        _surface.ContextMenuOpening += (_, _) => _surface.ContextMenu = BuildMenu();
        _surface.MouseLeftButtonDown += OnMouseDown;
        _surface.MouseMove += OnMouseMove;
        _surface.MouseLeftButtonUp += OnMouseUp;
        _surface.LostMouseCapture += (_, _) => EndPress();
        Content = _surface;

        // A single click does nothing by itself (renaming is in the menu); the timer only marks
        // how long a second click still counts as a double-click.
        _clickTimer.Tick += (_, _) => _clickTimer.Stop();
        ApplyStyle(model);
    }

    private static int Forms_DoubleClickTime() => System.Windows.Forms.SystemInformation.DoubleClickTime;

    public void ApplyStyle(BoxModel model)
    {
        var text = model.Collapsed ? $"▸ {model.Title}  ({model.Items.Count})" : model.Title;
        _title.Text = model.Locked ? $"🔒 {text}" : text;
        _surface.Cursor = model.Locked ? Cursors.Arrow : Cursors.SizeAll;
        _title.Foreground = BoxStyle.TextBrush(model);
        _surface.Background = BoxStyle.TitleBrush(model);
        // Rolled up, the title bar is the whole box, so round every corner.
        _surface.CornerRadius = model.Collapsed ? new CornerRadius(8) : new CornerRadius(8, 8, 0, 0);
        _surface.BorderThickness = model.Collapsed ? new Thickness(1) : new Thickness(1, 1, 1, 0);
    }

    public void BeginRename()
    {
        _editor.Text = _model.Title;
        _editor.Visibility = Visibility.Visible;
        _title.Visibility = Visibility.Hidden;
        Activate();
        _editor.Focus();
        _editor.SelectAll();
    }

    private void EndRename(bool commit)
    {
        if (_editor.Visibility != Visibility.Visible)
            return;
        _editor.Visibility = Visibility.Collapsed;
        _title.Visibility = Visibility.Visible;
        var text = _editor.Text.Trim();
        if (commit && text.Length > 0 && text != _model.Title)
            Renamed?.Invoke(text);
    }

    private ContextMenu BuildMenu()
    {
        var menu = new ContextMenu();
        menu.Items.Add(MenuItem(Lang.T("box.customizeNameColorOpacity"),
            () => StyleRequested?.Invoke()));
        menu.Items.Add(MenuItem(Lang.T("box.rename"), BeginRename));
        menu.Items.Add(MenuItem(Lang.T("box.rollUpUnroll"), () => CollapseToggled?.Invoke()));
        var sort = new MenuItem { Header = Lang.T("box.sort") };
        sort.Items.Add(MenuItem(Lang.T("box.byName"), () => SortRequested?.Invoke(BoxSortOrder.Name)));
        sort.Items.Add(MenuItem(Lang.T("box.byKind"), () => SortRequested?.Invoke(BoxSortOrder.Kind)));
        sort.Items.Add(MenuItem(Lang.T("box.byDateModifiedNewest"),
            () => SortRequested?.Invoke(BoxSortOrder.Modified)));
        sort.Items.Add(MenuItem(Lang.T("box.bySizeLargestFirst"), () => SortRequested?.Invoke(BoxSortOrder.Size)));
        menu.Items.Add(sort);
        var lockItem = new MenuItem { Header = Lang.T("box.lockPosition"), IsCheckable = true };
        lockItem.Click += (_, _) => LockToggled?.Invoke();
        menu.Opened += (_, _) => lockItem.IsChecked = _model.Locked;
        menu.Items.Add(lockItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem(Lang.T("box.lineUpAllLeft"), () => ArrangeRequested?.Invoke(false)));
        menu.Items.Add(MenuItem(Lang.T("box.lineUpAllRight"), () => ArrangeRequested?.Invoke(true)));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem(Lang.T("app.newBox"), () => NewBoxRequested?.Invoke()));
        menu.Items.Add(MenuItem(Lang.T("box.deleteThisBoxIcons"),
            () => DeleteRequested?.Invoke()));
        return menu;
    }

    private static MenuItem MenuItem(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_editor.Visibility == Visibility.Visible)
            return;
        GetCursorPos(out _pressStart);
        _pressed = ((UIElement)sender).CaptureMouse();
        _dragging = false;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_pressed)
            return;
        // The button-up message can get lost; never keep dragging once the button is released.
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            OnMouseUp(sender, null);
            return;
        }
        GetCursorPos(out var now);
        var dx = now.X - _pressStart.X;
        var dy = now.Y - _pressStart.Y;
        if (!_dragging)
        {
            // A locked box only takes clicks; it is never dragged.
            if (_model.Locked)
                return;
            // Small wobbles during a click are not a drag.
            var threshold = Math.Max(4, (int)SystemParameters.MinimumHorizontalDragDistance);
            if (Math.Abs(dx) < threshold && Math.Abs(dy) < threshold)
                return;
            _dragging = true;
            _clickTimer.Stop();
            DragStarted?.Invoke();
        }
        Dragging?.Invoke(dx, dy);
    }

    /// <summary>Ends a drag whose button-up never reached this window.</summary>
    public void EndDragIfReleased()
    {
        if (_dragging && !IsPrimaryButtonDown())
            EndPress();
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs? e)
    {
        if (!_pressed)
            return;
        var wasDrag = _dragging;
        EndPress();
        if (wasDrag)
            return;

        if (_clickTimer.IsEnabled)
        {
            _clickTimer.Stop();
            CollapseToggled?.Invoke();
        }
        else
        {
            _clickTimer.Start();
        }
    }

    private void EndPress()
    {
        if (!_pressed)
            return;
        _pressed = false;
        Mouse.Capture(null);
        if (_dragging)
        {
            _dragging = false;
            DragEnded?.Invoke();
        }
    }
}
