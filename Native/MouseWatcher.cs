using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace TidyDesktop.Native;

/// <summary>
/// Watches the left mouse button anywhere: reports releases, so an icon dropped on a box can be
/// picked up immediately, and double-clicks, so one on empty desktop can hide everything.
/// Low-level hooks run in this process; nothing is injected.
/// </summary>
/// <remarks>
/// Windows calls a low-level hook for every mouse event system-wide and waits for it, so the hook
/// runs on its own thread: if it shared the UI thread, any UI work would make the cursor stutter.
/// </remarks>
internal sealed class MouseWatcher : IDisposable
{
    private const int WH_MOUSE_LL = 14;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_LBUTTONUP = 0x0202;
    private const int SM_CXDOUBLECLK = 36;
    private const int SM_CYDOUBLECLK = 37;
    private const uint WM_QUIT = 0x0012;

    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll")]
    private static extern uint GetDoubleClickTime();

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    private readonly LowLevelMouseProc _proc;
    private readonly Dispatcher _dispatcher;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _started = new();
    private IntPtr _hook;
    private uint _threadId;

    public event Action? LeftButtonUp;

    /// <summary>A second left press close enough in time and place to the first; carries the screen point.</summary>
    public event Action<POINT>? DoubleClick;

    // The previous press, to recognize double-clicks. Only touched on the hook thread.
    private POINT _lastDown;
    private uint _lastDownTime;
    private bool _hasLastDown;

    public MouseWatcher()
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        // Keep the delegate in a field so the GC does not collect it while the hook is installed.
        _proc = HookProc;
        _thread = new Thread(Run) { IsBackground = true, Name = "TidyDesktop mouse hook" };
        _thread.Start();
        _started.Wait(TimeSpan.FromSeconds(2));
    }

    public bool IsInstalled => _hook != IntPtr.Zero;

    private void Run()
    {
        _threadId = GetCurrentThreadId();
        _hook = SetWindowsHookEx(WH_MOUSE_LL, _proc, GetModuleHandle(null), 0);
        _started.Set();
        if (_hook == IntPtr.Zero)
            return;

        while (GetMessage(out _, IntPtr.Zero, 0, 0) > 0)
        {
        }
        UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && wParam == WM_LBUTTONUP)
            _dispatcher.BeginInvoke(() => LeftButtonUp?.Invoke());
        else if (nCode >= 0 && wParam == WM_LBUTTONDOWN)
            OnButtonDown(Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam));
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private void OnButtonDown(MSLLHOOKSTRUCT info)
    {
        var isDouble = _hasLastDown &&
                       info.time - _lastDownTime <= GetDoubleClickTime() &&
                       Math.Abs(info.pt.X - _lastDown.X) <= GetSystemMetrics(SM_CXDOUBLECLK) / 2 &&
                       Math.Abs(info.pt.Y - _lastDown.Y) <= GetSystemMetrics(SM_CYDOUBLECLK) / 2;
        // A third quick press starts a new pair rather than counting as another double-click.
        _hasLastDown = !isDouble;
        (_lastDown, _lastDownTime) = (info.pt, info.time);
        if (isDouble)
        {
            var pt = info.pt;
            _dispatcher.BeginInvoke(() => DoubleClick?.Invoke(pt));
        }
    }

    public void Dispose()
    {
        if (_threadId != 0)
            PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _started.Dispose();
    }
}
