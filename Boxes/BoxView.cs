using TidyDesktop.Desktop;
using TidyDesktop.Native;
using static TidyDesktop.Native.NativeMethods;

namespace TidyDesktop.Boxes;

/// <summary>The windows that together draw one box: title bar, body and resize handles.</summary>
internal sealed class BoxView : IDisposable
{
    private BoxBackground _body;

    public BoxView(BoxModel model, DesktopHost host)
    {
        Model = model;
        _body = new BoxBackground(host, model);

        Title = new BoxTitleBar(model);
        Title.AttachTo(host.Owner);
        Title.Show();

        Grips = new[] { ResizeEdge.Left, ResizeEdge.Right, ResizeEdge.Bottom, ResizeEdge.BottomLeft, ResizeEdge.BottomRight }
            .Select(edge => new BoxGrip(edge))
            .ToArray();
        foreach (var grip in Grips)
        {
            grip.AttachTo(host.Owner);
            grip.Show();
        }

        ApplyBounds();
    }

    public BoxModel Model { get; }
    public BoxTitleBar Title { get; }

    /// <summary>Resize handles along the left, right and bottom edges and the two bottom corners.</summary>
    public BoxGrip[] Grips { get; }

    public double Scale => ScaleAt(Model.X + Model.Width / 2, Model.Y + Model.Height / 2);

    public int Px(double dip) => (int)Math.Round(dip * Scale);

    public int TitleHeight => Px(BoxTitleBar.HeightDip);

    /// <summary>Height currently on screen: just the title bar while rolled up.</summary>
    public int VisibleHeight => Model.Collapsed ? TitleHeight : Model.Height;

    public RECT Body => new()
    {
        Left = Model.X,
        Top = Model.Y + TitleHeight,
        Right = Model.X + Model.Width,
        Bottom = Model.Y + Model.Height,
    };

    public bool Contains(POINT pt) =>
        pt.X >= Model.X && pt.X < Model.X + Model.Width && pt.Y >= Model.Y && pt.Y < Model.Y + VisibleHeight;

    /// <summary>Hidden along with the desktop icons (double-click on empty desktop).</summary>
    public bool Hidden { get; private set; }

    public void SetHidden(bool hidden)
    {
        if (Hidden == hidden)
            return;
        Hidden = hidden;
        ApplyBounds();
    }

    public void ApplyBounds()
    {
        // Windows are moved off-screen rather than hidden: see DesktopLevelWindow.Park.
        if (Hidden)
        {
            Title.Park();
            _body.Hide();
            HideGrips();
            return;
        }

        var titleHeight = TitleHeight;
        Title.SetBounds(Model.X, Model.Y, Model.Width, titleHeight);
        if (Model.Collapsed)
        {
            _body.Hide();
            HideGrips();
            return;
        }

        _body.SetBounds(Model.X, Model.Y + titleHeight, Model.Width, Model.Height - titleHeight);
        // A locked box cannot be resized, so it has no handles.
        if (Model.Locked)
        {
            HideGrips();
            return;
        }

        var e = Px(BoxGrip.EdgeDip);
        var c = Px(BoxGrip.CornerDip);
        var (x, y, w, h) = (Model.X, Model.Y, Model.Width, Model.Height);
        var sideTop = y + titleHeight;
        var sideHeight = Math.Max(1, h - titleHeight - c);
        foreach (var grip in Grips)
        {
            switch (grip.Edge)
            {
                case ResizeEdge.Left:
                    grip.SetBounds(x, sideTop, e, sideHeight);
                    break;
                case ResizeEdge.Right:
                    grip.SetBounds(x + w - e, sideTop, e, sideHeight);
                    break;
                case ResizeEdge.Bottom:
                    grip.SetBounds(x + c, y + h - e, Math.Max(1, w - 2 * c), e);
                    break;
                case ResizeEdge.BottomLeft:
                    grip.SetBounds(x, y + h - c, c, c);
                    break;
                case ResizeEdge.BottomRight:
                    grip.SetBounds(x + w - c, y + h - c, c, c);
                    break;
            }
        }
    }

    private void HideGrips()
    {
        foreach (var grip in Grips)
            grip.Park();
    }

    public void ApplyStyle()
    {
        Title.ApplyStyle(Model);
        _body.ApplyStyle(Model);
    }

    /// <summary>Fades the box while a preview shows where it is being moved or resized to.</summary>
    public void SetDimmed(bool dimmed)
    {
        Title.Opacity = dimmed ? 0.45 : 1;
        _body.SetDimmed(dimmed);
    }

    /// <summary>Recreates the body on a new wallpaper layer after explorer.exe restarted.</summary>
    public void Reattach(DesktopHost host)
    {
        _body.Dispose();
        _body = new BoxBackground(host, Model);
        Title.AttachTo(host.Owner);
        foreach (var grip in Grips)
            grip.AttachTo(host.Owner);
        ApplyBounds();
    }

    /// <summary>Finishes a move or resize whose button-up was never delivered.</summary>
    public void EndDragIfReleased()
    {
        Title.EndDragIfReleased();
        foreach (var grip in Grips)
            grip.EndDragIfReleased();
    }

    public void Dispose()
    {
        _body.Dispose();
        Title.Close();
        foreach (var grip in Grips)
            grip.Close();
    }
}
