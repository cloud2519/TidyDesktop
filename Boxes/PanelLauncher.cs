using System.Runtime.InteropServices;
using System.Windows.Threading;
using TidyDesktop.Native;
using Screen = System.Windows.Forms.Screen;

namespace TidyDesktop.Boxes;

/// <summary>
/// The ways to open the <see cref="DesktopPanel"/>: a system-wide shortcut and, when turned on,
/// resting the pointer in the top-left corner of a screen. Bottom-right is not used: on the main
/// screen that is the taskbar's own "show desktop" button.
/// </summary>
internal sealed class PanelLauncher : IDisposable
{
    // How long the pointer must rest in the corner, so passing through it does nothing.
    private static readonly TimeSpan CornerDwell = TimeSpan.FromMilliseconds(300);

    private const int QUNS_ACCEPTS_NOTIFICATIONS = 5;

    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out int state);

    private readonly PanelSettings _settings;
    private readonly Func<PanelContent> _content;
    private readonly Action _save;
    private readonly DesktopPanel _panel = new();
    private readonly GlobalHotKey _hotKey = new();
    private readonly DispatcherTimer _cornerTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private DateTime? _inCornerSince;
    private bool _cornerArmed = true;

    /// <param name="content">What the panel shows, read each time it opens.</param>
    /// <param name="save">Called after a setting changed, to store it.</param>
    public PanelLauncher(PanelSettings settings, Func<PanelContent> content, Action save)
    {
        _settings = settings;
        _content = content;
        _save = save;
        _hotKey.Pressed += Toggle;
        HotkeyWorks = _hotKey.Set(settings.Hotkey);
        if (!HotkeyWorks && !string.IsNullOrEmpty(settings.Hotkey))
            AppData.Log($"Panel shortcut {settings.Hotkey} is taken by another program");
        _cornerTimer.Tick += (_, _) => CheckCorner();
        if (settings.HotCorner)
            _cornerTimer.Start();
    }

    /// <summary>The shortcut, such as "Ctrl+Alt+D"; empty when turned off.</summary>
    public string Hotkey => _settings.Hotkey;

    /// <summary>The shortcut is registered; false when it is off or another program has it.</summary>
    public bool HotkeyWorks { get; private set; }

    /// <summary>Changes the shortcut; an empty one turns it off. Returns whether it now works.</summary>
    public bool SetHotkey(string gesture)
    {
        _settings.Hotkey = gesture;
        _save();
        return HotkeyWorks = _hotKey.Set(gesture);
    }

    /// <summary>Lets go of the shortcut while a new one is being typed, so typing it does not open the panel.</summary>
    public void PauseHotkey(bool paused)
    {
        if (paused)
            _hotKey.Clear();
        else
            HotkeyWorks = _hotKey.Set(_settings.Hotkey);
    }

    public bool HotCorner
    {
        get => _settings.HotCorner;
        set
        {
            _settings.HotCorner = value;
            _save();
            if (value)
                _cornerTimer.Start();
            else
                _cornerTimer.Stop();
        }
    }

    public void Toggle()
    {
        try
        {
            _panel.Toggle(_content());
        }
        catch (COMException ex)
        {
            // explorer.exe is restarting; the next try finds it again.
            AppData.Log($"Panel: could not read the desktop: {ex.Message}");
        }
    }

    public void Close() => _panel.Close();

    private void CheckCorner()
    {
        NativeMethods.GetCursorPos(out var cursor);
        var screen = Screen.FromPoint(new System.Drawing.Point(cursor.X, cursor.Y)).Bounds;
        if (cursor.X > screen.Left + 1 || cursor.Y > screen.Top + 1)
        {
            _inCornerSince = null;
            _cornerArmed = true;
            return;
        }
        // Once per visit to the corner, not while dragging, and never over a full-screen game or slideshow.
        if (!_cornerArmed || _panel.IsOpen || NativeMethods.IsPrimaryButtonDown() ||
            SHQueryUserNotificationState(out var state) != 0 || state != QUNS_ACCEPTS_NOTIFICATIONS)
            return;
        _inCornerSince ??= DateTime.Now;
        if (DateTime.Now - _inCornerSince < CornerDwell)
            return;
        _cornerArmed = false;
        Toggle();
    }

    public void Dispose()
    {
        _cornerTimer.Stop();
        _panel.Close();
        _hotKey.Dispose();
    }
}
