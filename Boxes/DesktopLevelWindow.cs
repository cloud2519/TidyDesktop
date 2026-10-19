using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using TidyDesktop.Native;
using static TidyDesktop.Native.NativeMethods;

namespace TidyDesktop.Boxes;

/// <summary>
/// Base for the interactive parts of a box (title bar, resize grip). These windows are owned by
/// Progman and pinned to the bottom of the z-order, so they sit on the desktop under every app window.
/// </summary>
public class DesktopLevelWindow : Window
{
    private IntPtr _owner;

    public DesktopLevelWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = System.Windows.Media.Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        Width = 1;
        Height = 1;
        Left = -32000;
        Top = -32000;
    }

    public IntPtr Handle { get; private set; }

    public void AttachTo(IntPtr progman)
    {
        _owner = progman;
        if (Handle == IntPtr.Zero)
            return;
        SetWindowLongPtr(Handle, GWLP_HWNDPARENT, progman);
        SetWindowPos(Handle, HWND_BOTTOM, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    /// <summary>
    /// Moves without touching the z-order: re-sending HWND_BOTTOM costs ~50 ms per call on the
    /// desktop, while a plain move takes ~2 ms. The window already sits at the bottom, and the
    /// WM_WINDOWPOSCHANGING hook keeps it there.
    /// </summary>
    public void SetBounds(int x, int y, int width, int height) =>
        WindowBatch.Move(Handle, x, y, width, height, SWP_NOACTIVATE | SWP_NOZORDER);

    /// <summary>
    /// Takes the window off the screen without hiding it. Hiding and showing a window changes its
    /// z-order, which costs ~50 ms on the desktop, and resizing makes WPF redraw it; a plain move
    /// takes ~2 ms.
    /// </summary>
    public void Park() =>
        WindowBatch.Move(Handle, -32000, -32000, 0, 0, SWP_NOACTIVATE | SWP_NOZORDER | SWP_NOSIZE);

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        Handle = new WindowInteropHelper(this).Handle;
        AddExStyle(Handle, WS_EX_TOOLWINDOW);
        HwndSource.FromHwnd(Handle)!.AddHook(WndProc);
        if (_owner != IntPtr.Zero)
            AttachTo(_owner);
    }

    private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_WINDOWPOSCHANGING)
        {
            // Activation and clicks would raise the window above apps; keep it at desktop level.
            var pos = Marshal.PtrToStructure<WINDOWPOS>(lParam);
            if ((pos.flags & SWP_NOZORDER) == 0 && pos.hwndInsertAfter != HWND_BOTTOM)
            {
                pos.hwndInsertAfter = HWND_BOTTOM;
                Marshal.StructureToPtr(pos, lParam, false);
            }
        }
        return IntPtr.Zero;
    }
}
