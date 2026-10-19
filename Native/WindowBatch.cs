using System.Runtime.InteropServices;

namespace TidyDesktop.Native;

/// <summary>
/// Collects top-level window moves and applies them in one go (DeferWindowPos). Moving the
/// desktop-level windows one by one costs ~5 ms each, which adds up across many boxes.
/// </summary>
/// <example><c>using (WindowBatch.Begin()) { foreach (var view in views) view.ApplyBounds(); }</c></example>
internal sealed class WindowBatch : IDisposable
{
    [DllImport("user32.dll")]
    private static extern IntPtr BeginDeferWindowPos(int nNumWindows);

    [DllImport("user32.dll")]
    private static extern IntPtr DeferWindowPos(IntPtr hWinPosInfo, IntPtr hWnd, IntPtr hWndInsertAfter,
        int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EndDeferWindowPos(IntPtr hWinPosInfo);

    private readonly List<(IntPtr Hwnd, int X, int Y, int W, int H, uint Flags)> _moves = new();

    private WindowBatch()
    {
    }

    /// <summary>The batch moves go into, or null when moves apply immediately.</summary>
    public static WindowBatch? Current { get; private set; }

    /// <summary>Starts a batch; nested calls join the outer one.</summary>
    public static IDisposable Begin()
    {
        if (Current != null)
            return new Nested();
        Current = new WindowBatch();
        return Current;
    }

    /// <summary>Moves a top-level window now, or at the end of the current batch.</summary>
    public static void Move(IntPtr hwnd, int x, int y, int width, int height, uint flags)
    {
        if (Current != null)
            Current._moves.Add((hwnd, x, y, width, height, flags));
        else
            NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, x, y, width, height, flags);
    }

    public void Dispose()
    {
        Current = null;
        if (_moves.Count == 0)
            return;
        var info = BeginDeferWindowPos(_moves.Count);
        foreach (var m in _moves)
        {
            if (info == IntPtr.Zero)
                break;
            info = DeferWindowPos(info, m.Hwnd, IntPtr.Zero, m.X, m.Y, m.W, m.H, m.Flags);
        }
        if (info != IntPtr.Zero && EndDeferWindowPos(info))
            return;
        // A window in the batch failed (closed meanwhile, for instance); move the rest one by one.
        foreach (var m in _moves)
            NativeMethods.SetWindowPos(m.Hwnd, IntPtr.Zero, m.X, m.Y, m.W, m.H, m.Flags);
    }

    private sealed class Nested : IDisposable
    {
        public void Dispose()
        {
        }
    }
}
