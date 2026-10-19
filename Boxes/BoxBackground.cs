using TidyDesktop.Desktop;
using TidyDesktop.Native;
using static TidyDesktop.Native.NativeMethods;

namespace TidyDesktop.Boxes;

/// <summary>
/// The box body. Lives on the wallpaper layer, so the real desktop icons draw on top of it
/// and clicks on the body go straight to the desktop.
/// </summary>
internal sealed class BoxBackground : IDisposable
{
    private const uint SWP_ASYNCWINDOWPOS = 0x4000;

    private readonly LayeredChildWindow _window;
    private readonly DesktopHost _host;

    public BoxBackground(DesktopHost host, BoxModel model)
    {
        _host = host;
        _window = new LayeredChildWindow(host.WallpaperLayer);
        ApplyStyle(model);
    }

    public IntPtr Handle => _window.Handle;

    private (int Fill, int Border, double Opacity) _style;
    private bool _dimmed;

    public void ApplyStyle(BoxModel model)
    {
        var c = BoxStyle.ParseColor(model.Color);
        var fill = (c.R << 16) | (c.G << 8) | c.B;
        // A slightly lighter edge so the box stays visible on dark wallpapers.
        var border = (Lighten(c.R) << 16) | (Lighten(c.G) << 8) | Lighten(c.B);
        _style = (fill, border, BoxStyle.ClampOpacity(model.Opacity));
        Paint();
    }

    public void SetDimmed(bool dimmed)
    {
        if (_dimmed == dimmed)
            return;
        _dimmed = dimmed;
        Paint();
    }

    private void Paint() =>
        _window.SetAppearance(_style.Fill, _style.Border, _dimmed ? _style.Opacity * 0.4 : _style.Opacity);

    private static int Lighten(byte channel) => channel + (255 - channel) / 4;

    public void SetBounds(int screenX, int screenY, int width, int height)
    {
        var pt = new POINT(screenX, screenY);
        ScreenToClient(_host.WallpaperLayer, ref pt);
        // The parent belongs to explorer.exe; don't wait on its thread while dragging.
        SetWindowPos(Handle, _host.WallpaperInsertAfter, pt.X, pt.Y, width, height,
            SWP_NOACTIVATE | SWP_SHOWWINDOW | SWP_ASYNCWINDOWPOS);
        _window.SetShape(width, height, (int)Math.Round(8 * ScaleAt(screenX, screenY)));
    }

    public void Hide()
    {
        const uint SWP_HIDEWINDOW = 0x0080;
        SetWindowPos(Handle, IntPtr.Zero, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_HIDEWINDOW | SWP_ASYNCWINDOWPOS);
    }

    public void Dispose() => _window.Dispose();
}
