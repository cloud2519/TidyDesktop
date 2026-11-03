using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;

namespace TidyDesktop.Native;

/// <summary>
/// One system-wide keyboard shortcut, such as "Ctrl+Alt+D", that works whichever app has the focus.
/// Windows hands each combination to one program only, so registering can fail when another has it.
/// </summary>
internal sealed class GlobalHotKey : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_NOREPEAT = 0x4000;
    private const int Id = 1;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private readonly HwndSource _window;
    private bool _registered;

    public event Action? Pressed;

    public GlobalHotKey()
    {
        // A message-only window: it receives WM_HOTKEY and is never shown.
        _window = new HwndSource(new HwndSourceParameters("TidyDesktopHotKey") { ParentWindow = new IntPtr(-3), WindowStyle = 0 });
        _window.AddHook(WndProc);
    }

    /// <summary>
    /// Takes over <paramref name="gesture"/> ("Ctrl+Alt+D"), giving up the previous one. An empty
    /// gesture turns the shortcut off. Returns false when it is not valid or another program has it.
    /// </summary>
    public bool Set(string? gesture)
    {
        Clear();
        if (string.IsNullOrWhiteSpace(gesture) || Parse(gesture) is not { } parsed)
            return false;
        // ModifierKeys uses the same bit values as RegisterHotKey's MOD_ALT/CONTROL/SHIFT/WIN.
        _registered = RegisterHotKey(_window.Handle, Id, (uint)parsed.Modifiers | MOD_NOREPEAT,
            (uint)KeyInterop.VirtualKeyFromKey(parsed.Key));
        return _registered;
    }

    public void Clear()
    {
        if (_registered)
            UnregisterHotKey(_window.Handle, Id);
        _registered = false;
    }

    public static KeyGesture? Parse(string gesture)
    {
        try
        {
            return new KeyGestureConverter().ConvertFromInvariantString(gesture) as KeyGesture;
        }
        catch (Exception ex) when (ex is NotSupportedException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>"Ctrl+Alt+D" for a key and modifiers, or null for a combination that cannot be a shortcut.</summary>
    public static string? Format(Key key, ModifierKeys modifiers)
    {
        try
        {
            return new KeyGesture(key, modifiers).GetDisplayStringForCulture(CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is NotSupportedException or ArgumentException)
        {
            return null;
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == Id)
        {
            handled = true;
            Pressed?.Invoke();
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        Clear();
        _window.Dispose();
    }
}
