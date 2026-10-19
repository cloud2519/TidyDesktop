using System.Runtime.InteropServices;

namespace TidyDesktop.Native;

/// <summary>
/// A plain Win32 child window inside explorer's wallpaper window, painted with one color at a
/// window-wide opacity. On Windows 11 24H2 the desktop only composites children of the wallpaper
/// WorkerW that use SetLayeredWindowAttributes; per-pixel (UpdateLayeredWindow, which WPF uses)
/// and opaque children stay invisible there.
/// </summary>
internal sealed class LayeredChildWindow : IDisposable
{
    private const string ClassName = "TidyDesktopLayeredChild";
    private const int WS_EX_LAYERED = 0x00080000;
    private const int LWA_ALPHA = 0x2;
    private const uint WM_PAINT = 0x000F;
    private const uint WM_ERASEBKGND = 0x0014;

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
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, int crKey, byte bAlpha, int dwFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr BeginPaint(IntPtr hWnd, out PAINTSTRUCT lpPaint);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EndPaint(IntPtr hWnd, ref PAINTSTRUCT lpPaint);

    [DllImport("user32.dll")]
    private static extern int FillRect(IntPtr hDC, ref RECT lprc, IntPtr hbr);

    [DllImport("user32.dll")]
    private static extern int FrameRect(IntPtr hDC, ref RECT lprc, IntPtr hbr);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InvalidateRect(IntPtr hWnd, IntPtr lpRect, [MarshalAs(UnmanagedType.Bool)] bool bErase);

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, [MarshalAs(UnmanagedType.Bool)] bool bRedraw);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateSolidBrush(int color);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRoundRectRgn(int x1, int y1, int x2, int y2, int w, int h);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr ho);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    // Kept alive for the process lifetime; Windows calls it for every window of the class.
    private static readonly WndProc SharedProc = Dispatch;
    private static readonly Dictionary<IntPtr, LayeredChildWindow> Windows = new();
    private static bool _registered;

    private IntPtr _hwnd;
    private IntPtr _fill;
    private IntPtr _border;
    private int _width;
    private int _height;
    private int _radius;

    public LayeredChildWindow(IntPtr parent)
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

        _hwnd = CreateWindowEx(
            WS_EX_LAYERED | NativeMethods.WS_EX_TRANSPARENT | NativeMethods.WS_EX_NOACTIVATE,
            ClassName, "", NativeMethods.WS_CHILD | NativeMethods.WS_VISIBLE,
            0, 0, 1, 1, parent, IntPtr.Zero, instance, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero)
            throw new InvalidOperationException($"CreateWindowEx failed: {Marshal.GetLastWin32Error()}");
        Windows[_hwnd] = this;
    }

    public IntPtr Handle => _hwnd;

    /// <summary>Sets the fill and border colors (0xRRGGBB) and the window-wide opacity.</summary>
    public void SetAppearance(int fillRgb, int borderRgb, double opacity)
    {
        ReplaceBrush(ref _fill, fillRgb);
        ReplaceBrush(ref _border, borderRgb);
        SetLayeredWindowAttributes(_hwnd, 0, (byte)Math.Round(Math.Clamp(opacity, 0, 1) * 255), LWA_ALPHA);
        InvalidateRect(_hwnd, IntPtr.Zero, true);
    }

    /// <summary>Rounds the bottom corners; call after the window's size changes.</summary>
    public void SetShape(int width, int height, int radius)
    {
        if (width == _width && height == _height && radius == _radius)
            return;
        (_width, _height, _radius) = (width, height, radius);
        // Starting the rounded rectangle above the window keeps the top corners square.
        var region = CreateRoundRectRgn(0, -radius * 2, width + 1, height + 1, radius * 2, radius * 2);
        SetWindowRgn(_hwnd, region, true); // the window owns the region from here on
    }

    private static void ReplaceBrush(ref IntPtr brush, int rgb)
    {
        if (brush != IntPtr.Zero)
            DeleteObject(brush);
        // GDI wants 0x00BBGGRR.
        brush = CreateSolidBrush(((rgb & 0xFF) << 16) | (rgb & 0xFF00) | ((rgb >> 16) & 0xFF));
    }

    private static IntPtr Dispatch(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (Windows.TryGetValue(hWnd, out var window))
        {
            if (msg == WM_ERASEBKGND)
                return new IntPtr(1);
            if (msg == WM_PAINT)
            {
                window.Paint();
                return IntPtr.Zero;
            }
        }
        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    private void Paint()
    {
        var dc = BeginPaint(_hwnd, out var ps);
        GetClientRect(_hwnd, out var rc);
        if (_fill != IntPtr.Zero)
            FillRect(dc, ref rc, _fill);
        if (_border != IntPtr.Zero)
            FrameRect(dc, ref rc, _border);
        EndPaint(_hwnd, ref ps);
    }

    public void Dispose()
    {
        if (_hwnd == IntPtr.Zero)
            return;
        Windows.Remove(_hwnd);
        DestroyWindow(_hwnd);
        _hwnd = IntPtr.Zero;
        DeleteObject(_fill);
        DeleteObject(_border);
        _fill = _border = IntPtr.Zero;
    }
}
