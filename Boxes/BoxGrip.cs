using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using TidyDesktop.Native;
using static TidyDesktop.Native.NativeMethods;

namespace TidyDesktop.Boxes;

/// <summary>Which sides of a box a resize handle moves.</summary>
[Flags]
public enum ResizeEdge
{
    Left = 1,
    Right = 2,
    Bottom = 4,
    BottomLeft = Bottom | Left,
    BottomRight = Bottom | Right,
}

/// <summary>
/// An invisible strip along a box edge (or a corner square) that resizes the box when dragged.
/// The body itself sits below the desktop icons and cannot take the mouse, so these sit above them.
/// </summary>
public sealed class BoxGrip : DesktopLevelWindow
{
    /// <summary>Thickness of an edge strip.</summary>
    public const double EdgeDip = 6;

    /// <summary>Size of a corner square.</summary>
    public const double CornerDip = 14;

    private POINT _dragStart;
    private bool _dragging;

    public event Action? DragStarted;
    public event PixelDragHandler? Dragging;
    public event Action? DragEnded;

    public BoxGrip(ResizeEdge edge)
    {
        Edge = edge;
        // Nearly transparent fill so the whole strip receives the mouse.
        var surface = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x02, 0, 0, 0)),
            Cursor = edge switch
            {
                ResizeEdge.Left or ResizeEdge.Right => Cursors.SizeWE,
                ResizeEdge.Bottom => Cursors.SizeNS,
                ResizeEdge.BottomLeft => Cursors.SizeNESW,
                _ => Cursors.SizeNWSE,
            },
        };
        if (edge == ResizeEdge.BottomRight)
        {
            surface.Child = new TextBlock
            {
                Text = "◢",
                Foreground = new SolidColorBrush(Color.FromArgb(0xA0, 0xFF, 0xFF, 0xFF)),
                FontSize = 10,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 2, 0),
            };
        }

        surface.MouseLeftButtonDown += (s, _) =>
        {
            GetCursorPos(out _dragStart);
            _dragging = ((UIElement)s).CaptureMouse();
            if (_dragging)
                DragStarted?.Invoke();
        };
        surface.MouseMove += (_, e) =>
        {
            if (!_dragging)
                return;
            // The button-up message can get lost; never keep resizing once the button is released.
            if (e.LeftButton != MouseButtonState.Pressed)
            {
                EndDrag();
                return;
            }
            GetCursorPos(out var now);
            Dragging?.Invoke(now.X - _dragStart.X, now.Y - _dragStart.Y);
        };
        surface.MouseLeftButtonUp += (_, _) => EndDrag();
        surface.LostMouseCapture += (_, _) => EndDrag();
        Content = surface;
    }

    public ResizeEdge Edge { get; }

    /// <summary>Ends a resize whose button-up never reached this window.</summary>
    public void EndDragIfReleased()
    {
        if (_dragging && !IsPrimaryButtonDown())
            EndDrag();
    }

    private void EndDrag()
    {
        if (!_dragging)
            return;
        _dragging = false;
        Mouse.Capture(null);
        DragEnded?.Invoke();
    }
}
