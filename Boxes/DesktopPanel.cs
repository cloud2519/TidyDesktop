using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using TidyDesktop.Native;
using static TidyDesktop.Native.NativeMethods;
using Screen = System.Windows.Forms.Screen;

namespace TidyDesktop.Boxes;

/// <summary>A desktop item for the panel. <see cref="Position"/> is set for items outside boxes.</summary>
internal sealed record PanelItem(string Key, string Name, POINT? Position);

internal sealed record PanelBox(BoxModel Model, IReadOnlyList<PanelItem> Items);

/// <summary>What the panel shows, with the icon cell size in physical pixels.</summary>
internal sealed record PanelContent(IReadOnlyList<PanelBox> Boxes, IReadOnlyList<PanelItem> Loose, POINT Spacing);

/// <summary>
/// The desktop shown on top of every window, so its items can be opened without minimizing
/// anything (Win+D). The real icons belong to explorer and cannot be raised above other apps, so
/// the panel draws the same boxes and items itself, where they are on the desktop. Typing filters
/// by name; Enter opens the first match, Esc or a click on empty space closes it.
/// </summary>
internal sealed class DesktopPanel
{
    private static readonly Brush Shade = Frozen(new SolidColorBrush(Color.FromArgb(0xB0, 0x0A, 0x0A, 0x10)));
    private static readonly Brush Hover = Frozen(new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush Light = Frozen(new SolidColorBrush(Color.FromRgb(0xF2, 0xF2, 0xF4)));
    private static readonly Brush Muted = Frozen(new SolidColorBrush(Color.FromRgb(0xA0, 0xA0, 0xAA)));

    private readonly List<ScreenWindow> _windows = new();
    private readonly List<(FrameworkElement Tile, PanelItem Item)> _tiles = new();

    public bool IsOpen => _windows.Count > 0;

    /// <summary>Opens the panel, or closes it when it is already open.</summary>
    public void Toggle(PanelContent content)
    {
        if (IsOpen)
        {
            Close();
            return;
        }

        foreach (var screen in Screen.AllScreens)
        {
            var b = screen.Bounds;
            _windows.Add(new ScreenWindow(this, new RECT { Left = b.Left, Top = b.Top, Right = b.Right, Bottom = b.Bottom },
                ScaleAt(b.X + b.Width / 2, b.Y + b.Height / 2)));
        }
        var primary = WindowAt(Screen.PrimaryScreen!.Bounds.X + 1, Screen.PrimaryScreen.Bounds.Y + 1) ?? _windows[0];

        foreach (var box in content.Boxes)
        {
            var (x, y, w, h) = (box.Model.X, box.Model.Y, box.Model.Width, box.Model.Height);
            var window = WindowAt(x + w / 2, y + h / 2);
            if (window == null)
            {
                // On a monitor that is switched off: shown on the main one rather than not at all.
                window = primary;
                var area = Screen.PrimaryScreen.WorkingArea;
                x = Math.Clamp(x, area.Left, Math.Max(area.Left, area.Right - w));
                y = Math.Clamp(y, area.Top, Math.Max(area.Top, area.Bottom - h));
            }
            window.Place(BoxElement(box, window.Scale, content.Spacing), x, y, w, h);
        }
        foreach (var item in content.Loose)
        {
            if (item.Position is not { } p || WindowAt(p.X + content.Spacing.X / 2, p.Y + content.Spacing.Y / 2) is not { } window)
                continue;
            window.Place(Tile(item, window.Scale, content.Spacing), p.X, p.Y, content.Spacing.X, content.Spacing.Y);
        }

        GetCursorPos(out var cursor);
        var active = WindowAt(cursor.X, cursor.Y) ?? primary;
        var search = active.AddSearch();
        search.TextChanged += (_, _) => Filter(search.Text);
        search.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && _tiles.FirstOrDefault(t => Matches(t.Item, search.Text)) is { Item: { } first } &&
                search.Text.Length > 0)
                Open(first);
        };

        foreach (var window in _windows)
            window.Show();
        active.Activate();
        search.Focus();
    }

    public void Close()
    {
        var windows = _windows.ToList();
        _windows.Clear();
        _tiles.Clear();
        foreach (var window in windows)
            window.Close();
    }

    private ScreenWindow? WindowAt(int x, int y) =>
        _windows.FirstOrDefault(w => x >= w.Bounds.Left && x < w.Bounds.Right && y >= w.Bounds.Top && y < w.Bounds.Bottom);

    private void Open(PanelItem item)
    {
        // Closed first: the panel stays above everything, including what is being opened.
        Close();
        if (!ShellItems.Open(item.Key))
            AppData.Log($"Panel: could not open '{item.Key}' ({Marshal.GetLastWin32Error()})");
    }

    private static bool Matches(PanelItem item, string text) =>
        text.Length == 0 || item.Name.Contains(text.Trim(), StringComparison.CurrentCultureIgnoreCase);

    private void Filter(string text)
    {
        foreach (var (tile, item) in _tiles)
            tile.Opacity = Matches(item, text) ? 1 : 0.2;
    }

    /// <summary>Closes the panel once none of its windows is active, as when the user switched apps.</summary>
    private void OnDeactivated() =>
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            if (IsOpen && !_windows.Any(w => w.IsActive))
                Close();
        });

    private FrameworkElement BoxElement(PanelBox box, double scale, POINT spacing)
    {
        var c = BoxStyle.ParseColor(box.Model.Color);
        // More solid than on the desktop: the panel sits over busy app windows, not a wallpaper.
        var alpha = (byte)Math.Round(Math.Max(BoxStyle.ClampOpacity(box.Model.Opacity), 0.85) * 255);
        var items = new WrapPanel();
        foreach (var item in box.Items)
            items.Children.Add(Tile(item, scale, spacing));

        var title = new TextBlock
        {
            Text = box.Model.Title,
            Foreground = BoxStyle.TextBrush(box.Model),
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Height = BoxTitleBar.HeightDip,
            Padding = new Thickness(10, 6, 10, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        DockPanel.SetDock(title, Dock.Top);
        var body = new DockPanel();
        body.Children.Add(title);
        body.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = items });
        return new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x60, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Child = body,
        };
    }

    private FrameworkElement Tile(PanelItem item, double scale, POINT spacing)
    {
        var content = new StackPanel();
        content.Children.Add(new Image
        {
            Source = ShellItems.Icon(item.Key),
            Width = 32,
            Height = 32,
            Margin = new Thickness(0, 4, 0, 2),
        });
        content.Children.Add(new TextBlock
        {
            Text = item.Name,
            Foreground = Light,
            FontSize = 12,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxHeight = 34,
            Margin = new Thickness(2, 0, 2, 0),
        });
        var tile = new Border
        {
            Width = spacing.X / scale,
            Height = spacing.Y / scale,
            CornerRadius = new CornerRadius(4),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            ToolTip = item.Name,
            Child = content,
        };
        tile.MouseEnter += (_, _) => tile.Background = Hover;
        tile.MouseLeave += (_, _) => tile.Background = Brushes.Transparent;
        tile.MouseLeftButtonUp += (_, _) => Open(item);
        _tiles.Add((tile, item));
        return tile;
    }

    private static Brush Frozen(Brush brush)
    {
        brush.Freeze();
        return brush;
    }

    /// <summary>The part of the panel that covers one monitor.</summary>
    private sealed class ScreenWindow : Window
    {
        private readonly DesktopPanel _panel;
        private readonly Canvas _canvas = new() { Background = Shade };

        public RECT Bounds { get; }
        public double Scale { get; }

        public ScreenWindow(DesktopPanel panel, RECT bounds, double scale)
        {
            _panel = panel;
            Bounds = bounds;
            Scale = scale;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            Opacity = 0;
            Content = _canvas;

            _canvas.MouseLeftButtonDown += (_, e) =>
            {
                if (e.OriginalSource == _canvas)
                    _panel.Close();
            };
            KeyDown += (_, e) =>
            {
                if (e.Key == Key.Escape)
                    _panel.Close();
            };
            Deactivated += (_, _) => _panel.OnDeactivated();
        }

        /// <summary>Puts an element over a rectangle given in physical screen pixels.</summary>
        public void Place(FrameworkElement element, int x, int y, int width, int height)
        {
            Canvas.SetLeft(element, (x - Bounds.Left) / Scale);
            Canvas.SetTop(element, (y - Bounds.Top) / Scale);
            element.Width = width / Scale;
            element.Height = height / Scale;
            _canvas.Children.Add(element);
        }

        public TextBox AddSearch()
        {
            const double width = 440;
            var box = new TextBox
            {
                FontSize = 17,
                Foreground = Light,
                CaretBrush = Light,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                VerticalContentAlignment = VerticalAlignment.Center,
            };
            var placeholder = new TextBlock
            {
                Text = Lang.T("panel.search"),
                Foreground = Muted,
                FontSize = 17,
                Margin = new Thickness(3, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false,
            };
            box.TextChanged += (_, _) => placeholder.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            var field = new Grid();
            field.Children.Add(placeholder);
            field.Children.Add(box);

            var bar = new StackPanel { Width = width };
            bar.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0xF0, 0x26, 0x26, 0x2E)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x60, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12, 8, 12, 8),
                Child = field,
            });
            bar.Children.Add(new TextBlock
            {
                Text = Lang.T("panel.hint"),
                Foreground = Muted,
                FontSize = 12,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 6, 0, 0),
            });
            Canvas.SetLeft(bar, ((Bounds.Right - Bounds.Left) / Scale - width) / 2);
            Canvas.SetTop(bar, 24);
            _canvas.Children.Add(bar);
            return box;
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            var handle = new WindowInteropHelper(this).Handle;
            AddExStyle(handle, WS_EX_TOOLWINDOW);
            HwndSource.FromHwnd(handle)!.AddHook(WndProc);
            SetWindowPos(handle, IntPtr.Zero, Bounds.Left, Bounds.Top, Bounds.Right - Bounds.Left, Bounds.Bottom - Bounds.Top,
                SWP_NOZORDER | SWP_NOACTIVATE);
        }

        protected override void OnContentRendered(EventArgs e)
        {
            base.OnContentRendered(e);
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120)));
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_WINDOWPOSCHANGING)
            {
                // Moving onto a monitor with a different scale makes WPF resize the window to keep
                // its size in DIPs; it must cover exactly its monitor instead.
                var pos = Marshal.PtrToStructure<WINDOWPOS>(lParam);
                const uint SWP_NOSIZE_OR_MOVE = SWP_NOSIZE | SWP_NOMOVE;
                if ((pos.flags & SWP_NOSIZE_OR_MOVE) != SWP_NOSIZE_OR_MOVE)
                {
                    pos.flags &= ~SWP_NOSIZE_OR_MOVE;
                    (pos.x, pos.y, pos.cx, pos.cy) = (Bounds.Left, Bounds.Top, Bounds.Right - Bounds.Left, Bounds.Bottom - Bounds.Top);
                    Marshal.StructureToPtr(pos, lParam, false);
                }
            }
            return IntPtr.Zero;
        }
    }
}
