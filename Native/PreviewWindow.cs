using System.Runtime.InteropServices;

namespace TidyDesktop.Native;

/// <summary>
/// A translucent outline that shows where a box will go while it is moved or resized.
/// A plain Win32 popup with one alpha value for the whole window, so moving it costs almost nothing.
/// </summary>
internal sealed class PreviewWindow : IDisposable
{
    private const string ClassName = "TidyDesktopPreview";
    private const uint WM_PAINT = 0x000F;
    private const uint WM_ERASEBKGND = 0x0014;
    private const uint WM_NCHITTEST = 0x0084;
    private const uint LWA_ALPHA = 0x2;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private const int WS_EX_LAYERED = 0x00080000;
    private const int OffScreen = -32000;

    private delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public int cbSize;
        public uint style;
        public WndProc lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PAINTSTRUCT
    {
        public IntPtr hdc;
        public int fErase;
        public RECT rcPaint;
        public int fRestore;
        public int fIncUpdate;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        public byte[] rgbReserved;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(int dwExStyle, string lpClassName, string lpWindowName, int dwStyle,
        int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr BeginPaint(IntPtr hWnd, out PAINTSTRUCT lpPaint);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EndPaint(IntPtr hWnd, ref PAINTSTRUCT lpPaint);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InvalidateRect(IntPtr hWnd, IntPtr lpRect, [MarshalAs(UnmanagedType.Bool)] bool bErase);

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, [MarshalAs(UnmanagedType.Bool)] bool bRedraw);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern int FillRect(IntPtr hDC, ref RECT lprc, IntPtr hbr);

    [DllImport("user32.dll")]
    private static extern int FrameRect(IntPtr hDC, ref RECT lprc, IntPtr hbr);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateSolidBrush(int color);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr ho);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRoundRectRgn(int x1, int y1, int x2, int y2, int w, int h);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    private static readonly WndProc SharedProc = Dispatch;
    private static readonly Dictionary<IntPtr, PreviewWindow> Windows = new();
    private static bool _registered;

    private IntPtr _hwnd;
    private IntPtr _fill;
    private IntPtr _band;
    private IntPtr _border;
    private int _bandHeight;
    private int _radius;
    private int _width;
    private int _height;

    public PreviewWindow(IntPtr owner)
    {
        var instance = GetModuleHandle(null);
        if (!_registered)
        {
            var wc = new WNDCLASSEX
            {
                cbSize = Marshal.SizeOf<WNDCLASSEX>(),
                lpfnWndProc = SharedProc,
                hInstance = instance,
                lpszClassName = ClassName,
            };
            RegisterClassEx(ref wc);
            _registered = true;
        }

        // A popup owned by Progman stays just above the desktop, under every app window.
        _hwnd = CreateWindowEx(
            WS_EX_LAYERED | NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TRANSPARENT,
            ClassName, "", NativeMethods.WS_POPUP, 0, 0, 1, 1, owner, IntPtr.Zero, instance, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero)
            throw new InvalidOperationException($"CreateWindowEx failed: {Marshal.GetLastWin32Error()}");
        Windows[_hwnd] = this;
        SetLayeredWindowAttributes(_hwnd, 0, 150, LWA_ALPHA);

        // Changing the z-order of a desktop-level window costs ~50 ms, so do it once here and keep
        // the window shown but parked off-screen; showing the outline is then just a move.
        NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_BOTTOM, OffScreen, OffScreen, 1, 1,
            NativeMethods.SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }

    /// <summary>Shows the outline over <paramref name="bounds"/> (screen pixels) in the given colors (0xRRGGBB).</summary>
    public void Show(RECT bounds, int fillRgb, int bandRgb, int borderRgb, int bandHeight, int radius)
    {
        ReplaceBrush(ref _fill, fillRgb);
        ReplaceBrush(ref _band, bandRgb);
        ReplaceBrush(ref _border, borderRgb);
        _bandHeight = bandHeight;
        _radius = radius;
        _width = _height = 0;
        Move(bounds);
        InvalidateRect(_hwnd, IntPtr.Zero, false);
    }

    /// <summary>Moves without re-sending HWND_BOTTOM, which is far slower than the move itself.</summary>
    public void Move(RECT bounds)
    {
        var (width, height) = (bounds.Right - bounds.Left, bounds.Bottom - bounds.Top);
        if (width != _width || height != _height)
        {
            (_width, _height) = (width, height);
            SetWindowRgn(_hwnd, CreateRoundRectRgn(0, 0, width + 1, height + 1, _radius * 2, _radius * 2), true);
        }
        NativeMethods.SetWindowPos(_hwnd, IntPtr.Zero, bounds.Left, bounds.Top, width, height,
            NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOZORDER);
    }

    public void Hide() =>
        NativeMethods.SetWindowPos(_hwnd, IntPtr.Zero, OffScreen, OffScreen, 1, 1,
            NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOZORDER);

    private static void ReplaceBrush(ref IntPtr brush, int rgb)
    {
        if (brush != IntPtr.Zero)
            DeleteObject(brush);
        // GDI colors are 0x00BBGGRR.
        brush = CreateSolidBrush(((rgb & 0xFF) << 16) | (rgb & 0xFF00) | ((rgb >> 16) & 0xFF));
    }

    private static IntPtr Dispatch(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (Windows.TryGetValue(hWnd, out var window))
        {
            switch (msg)
            {
                case WM_ERASEBKGND:
                    return new IntPtr(1);
                case WM_NCHITTEST:
                    return new IntPtr(-1); // HTTRANSPARENT: clicks pass through
                case WM_PAINT:
                    window.Paint();
                    return IntPtr.Zero;
            }
        }
        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    private void Paint()
    {
        var dc = BeginPaint(_hwnd, out var ps);
        if (_fill != IntPtr.Zero)
        {
            var all = new RECT { Left = 0, Top = 0, Right = _width, Bottom = _height };
            FillRect(dc, ref all, _fill);
            var band = all with { Bottom = Math.Min(_height, _bandHeight) };
            FillRect(dc, ref band, _band);
            // A double-width frame reads as an outline even at low opacity.
            FrameRect(dc, ref all, _border);
            var inner = new RECT { Left = 1, Top = 1, Right = _width - 1, Bottom = _height - 1 };
            FrameRect(dc, ref inner, _border);
        }
        EndPaint(_hwnd, ref ps);
    }

    public void Dispose()
    {
        if (_hwnd == IntPtr.Zero)
            return;
        Windows.Remove(_hwnd);
        DestroyWindow(_hwnd);
        _hwnd = IntPtr.Zero;
        foreach (var brush in new[] { _fill, _band, _border })
        {
            if (brush != IntPtr.Zero)
                DeleteObject(brush);
        }
        _fill = _band = _border = IntPtr.Zero;
    }
}
