using TidyDesktop.Native;
using Screen = System.Windows.Forms.Screen;

namespace TidyDesktop.Boxes;

/// <summary>
/// Magnetic placement while a box is moved or resized, so boxes line up without careful dragging.
/// An edge near another box's edge, the usual gap beside another box, or the screen's margin
/// catches on it, and a guide line shows what it lined up with. A resized box also catches on its
/// neighbours' sizes and otherwise grows in whole icon cells. Everything is in physical pixels.
/// </summary>
internal sealed class BoxSnapper
{
    /// <summary>Smallest box, in device-independent pixels.</summary>
    public const double MinWidthDip = 160, MinHeightDip = 110;

    /// <summary>How icons are laid out in a box, for sizing it in whole cells (see BoxManager.Grid).</summary>
    public sealed record Cells(int Width, int Gap, int Pad, int RowHeight, int TitleHeight, int Extra)
    {
        public int WidthFor(int columns) => columns * Width + (columns + 1) * Gap + 2 * Pad;

        public int HeightFor(int rows) => TitleHeight + rows * RowHeight + 2 * Pad + Extra;
    }

    /// <param name="Label">Columns × rows the box holds, shown while resizing.</param>
    public sealed record Result(RECT Bounds, IReadOnlyList<RECT> Guides, string? Label);

    // An edge on the given axis (x for vertical lines) and the span of what it lines up with.
    private readonly record struct Line(int At, bool Low, int From, int To);

    private readonly IReadOnlyList<RECT> _others;
    private readonly Cells? _cells;
    private readonly int _reach;
    private readonly int _gap;
    private readonly int _margin;
    private readonly int _guideWidth;
    private readonly int _minWidth;
    private readonly int _minHeight;

    /// <param name="others">The other boxes, as they are on screen.</param>
    /// <param name="cells">Null while the icon size is unknown; sizes then change freely.</param>
    /// <param name="px">Turns device-independent pixels into physical ones where the box is.</param>
    public BoxSnapper(IReadOnlyList<RECT> others, Cells? cells, Func<double, int> px)
    {
        _others = others;
        _cells = cells;
        _reach = px(10);
        // The same gap and screen margin "line up boxes" uses, so both give the same look.
        _gap = px(14);
        _margin = px(16);
        _guideWidth = Math.Max(1, px(2));
        _minWidth = px(MinWidthDip);
        _minHeight = px(MinHeightDip);
    }

    public Result Move(RECT r, bool magnetic)
    {
        if (!magnetic)
            return new Result(r, Array.Empty<RECT>(), null);
        var area = AreaFor(r);
        var x = Catch(r, _others, area, low: true, high: true, matchSizes: false);
        var y = Catch(Flip(r), _others.Select(Flip).ToList(), Flip(area), low: true, high: true, matchSizes: false);

        var dx = x is { } cx ? cx.At - (cx.Low ? r.Left : r.Right) : 0;
        var dy = y is { } cy ? cy.At - (cy.Low ? r.Top : r.Bottom) : 0;
        r = new RECT { Left = r.Left + dx, Top = r.Top + dy, Right = r.Right + dx, Bottom = r.Bottom + dy };
        return new Result(r, Guides(r, x, y), null);
    }

    public Result Resize(RECT r, ResizeEdge edge, bool magnetic)
    {
        var area = AreaFor(r);
        var (left, right) = (edge.HasFlag(ResizeEdge.Left), edge.HasFlag(ResizeEdge.Right));
        Line? x = null, y = null;

        if (left || right)
        {
            x = magnetic ? Catch(r, _others, area, left, right, matchSizes: true) : null;
            if (x is { } cx && (cx.Low ? r.Right - cx.At : cx.At - r.Left) >= _minWidth)
                (r.Left, r.Right) = cx.Low ? (cx.At, r.Right) : (r.Left, cx.At);
            else
            {
                x = null;
                var width = magnetic && _cells != null ? FitWidth(r.Right - r.Left) : Math.Max(_minWidth, r.Right - r.Left);
                (r.Left, r.Right) = left ? (r.Right - width, r.Right) : (r.Left, r.Left + width);
            }
        }

        if (edge.HasFlag(ResizeEdge.Bottom))
        {
            y = magnetic ? Catch(Flip(r), _others.Select(Flip).ToList(), Flip(area), low: false, high: true, matchSizes: true) : null;
            if (y is { } cy && cy.At - r.Top >= _minHeight)
                r.Bottom = cy.At;
            else
            {
                y = null;
                r.Bottom = r.Top + (magnetic && _cells != null ? FitHeight(r.Bottom - r.Top) : Math.Max(_minHeight, r.Bottom - r.Top));
            }
        }

        return new Result(r, Guides(r, x, y), Label(r));
    }

    /// <summary>
    /// The nearest line within reach for the low (left) and/or high (right) edge of
    /// <paramref name="r"/>; the y axis is handled by flipping rectangles first.
    /// </summary>
    private Line? Catch(RECT r, IReadOnlyList<RECT> others, RECT area, bool low, bool high, bool matchSizes)
    {
        var lines = new List<Line>();
        foreach (var o in others)
        {
            if (low)
                lines.Add(new Line(o.Left, true, o.Top, o.Bottom));
            if (high)
                lines.Add(new Line(o.Right, false, o.Top, o.Bottom));
            // Side by side only with boxes level with this one; far above or below it would just look odd.
            if (r.Top < o.Bottom + _gap && o.Top < r.Bottom + _gap)
            {
                if (low)
                    lines.Add(new Line(o.Right + _gap, true, o.Top, o.Bottom));
                if (high)
                    lines.Add(new Line(o.Left - _gap, false, o.Top, o.Bottom));
            }
            if (matchSizes)
            {
                // The same size as another box; the guide runs along this box only.
                if (low)
                    lines.Add(new Line(r.Right - (o.Right - o.Left), true, r.Top, r.Bottom));
                if (high)
                    lines.Add(new Line(r.Left + (o.Right - o.Left), false, r.Top, r.Bottom));
            }
        }
        if (low)
            lines.Add(new Line(area.Left + _margin, true, r.Top, r.Bottom));
        if (high)
            lines.Add(new Line(area.Right - _margin, false, r.Top, r.Bottom));

        Line? best = null;
        var bestDistance = _reach + 1;
        foreach (var line in lines)
        {
            var distance = Math.Abs(line.At - (line.Low ? r.Left : r.Right));
            if (distance < bestDistance)
                (best, bestDistance) = (line, distance);
        }
        return best;
    }

    private IReadOnlyList<RECT> Guides(RECT r, Line? x, Line? y)
    {
        var guides = new List<RECT>();
        if (x is { } vx)
            guides.Add(Guide(r, vx));
        if (y is { } hy)
            guides.Add(Flip(Guide(Flip(r), hy)));
        return guides;
    }

    // A vertical line at the caught edge, long enough to reach what it lined up with.
    private RECT Guide(RECT r, Line line) => new()
    {
        Left = line.At - _guideWidth / 2,
        Right = line.At - _guideWidth / 2 + _guideWidth,
        Top = Math.Min(line.From, r.Top),
        Bottom = Math.Max(line.To, r.Bottom),
    };

    private int FitWidth(int width)
    {
        var columns = Math.Max(1, (int)Math.Round((width - 2 * _cells!.Pad - _cells.Gap) / (double)(_cells.Width + _cells.Gap)));
        while (_cells.WidthFor(columns) < _minWidth)
            columns++;
        return _cells.WidthFor(columns);
    }

    private int FitHeight(int height)
    {
        var fixedPart = _cells!.HeightFor(0);
        var rows = Math.Max(1, (int)Math.Round((height - fixedPart) / (double)_cells.RowHeight));
        while (_cells.HeightFor(rows) < _minHeight)
            rows++;
        return _cells.HeightFor(rows);
    }

    private string? Label(RECT r)
    {
        if (_cells == null)
            return null;
        var columns = Math.Max(1, (r.Right - r.Left - 2 * _cells.Pad - _cells.Gap) / (_cells.Width + _cells.Gap));
        var rows = Math.Max(0, (int)Math.Round((r.Bottom - r.Top - _cells.HeightFor(0)) / (double)_cells.RowHeight));
        return $"{columns} × {rows}";
    }

    private static RECT AreaFor(RECT r)
    {
        var area = Screen.FromRectangle(System.Drawing.Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom)).WorkingArea;
        return new RECT { Left = area.Left, Top = area.Top, Right = area.Right, Bottom = area.Bottom };
    }

    // Swaps x and y, so the code for vertical edges also handles horizontal ones.
    private static RECT Flip(RECT r) => new() { Left = r.Top, Top = r.Left, Right = r.Bottom, Bottom = r.Right };
}
