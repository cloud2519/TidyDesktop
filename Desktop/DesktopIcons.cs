using System.Runtime.InteropServices;
using TidyDesktop.Boxes;
using TidyDesktop.Native;
using static TidyDesktop.Native.NativeMethods;

namespace TidyDesktop.Desktop;

/// <summary>One desktop icon. <see cref="Position"/> is the item's top-left in physical screen pixels.</summary>
internal sealed class DesktopIcon
{
    public required string Key { get; init; }
    public required string DisplayName { get; init; }
    public required POINT Position { get; init; }
    internal IntPtr Pidl { get; init; }
}

/// <summary>
/// Icons captured at one moment. Holds the item IDs needed to move them, so dispose it after use.
/// </summary>
internal sealed class IconSnapshot : IDisposable
{
    public List<DesktopIcon> Icons { get; } = new();

    public void Dispose()
    {
        foreach (var icon in Icons)
            Marshal.FreeCoTaskMem(icon.Pidl);
        Icons.Clear();
    }
}

/// <summary>
/// Reads and positions the real desktop icons through the desktop's IFolderView,
/// from outside explorer.exe (no injection).
/// </summary>
internal sealed class DesktopIcons
{
    private readonly IFolderView2 _view;
    private readonly IntPtr _listView;

    private DesktopIcons(IFolderView2 view, IntPtr listView)
    {
        _view = view;
        _listView = listView;
    }

    public static DesktopIcons Connect(DesktopHost host)
    {
        var type = Type.GetTypeFromCLSID(ShellConst.CLSID_ShellWindows, throwOnError: true)!;
        var windows = (IShellWindows)Activator.CreateInstance(type)!;

        object location = ShellConst.CSIDL_DESKTOP;
        object root = null!;
        var dispatch = windows.FindWindowSW(ref location, ref root, ShellConst.SWC_DESKTOP, out _, ShellConst.SWFO_NEEDDISPATCH);

        var serviceProvider = (IOleServiceProvider)dispatch;
        var sid = ShellConst.SID_STopLevelBrowser;
        var iid = ShellConst.IID_IShellBrowser;
        Marshal.ThrowExceptionForHR(serviceProvider.QueryService(ref sid, ref iid, out var browserPtr));

        IShellBrowser browser;
        try
        {
            browser = (IShellBrowser)Marshal.GetObjectForIUnknown(browserPtr);
        }
        finally
        {
            Marshal.Release(browserPtr);
        }

        var view = (IFolderView2)browser.QueryActiveShellView();
        return new DesktopIcons(view, host.ListView);
    }

    /// <summary>Size of one icon cell in physical pixels.</summary>
    public POINT Spacing
    {
        get
        {
            var spacing = new POINT();
            Marshal.ThrowExceptionForHR(_view.GetSpacing(ref spacing));
            return spacing;
        }
    }

    private (int Offset, int Width)? _itemBox;
    private DateTime _nextMeasure;

    /// <summary>Whether <see cref="ItemBox"/> was measured rather than assumed.</summary>
    public bool ItemBoxMeasured => _itemBox != null;

    /// <summary>
    /// The visible cell of an icon (picture and label) across: how far its left edge lies to the
    /// left of the icon's position, and its width, in physical pixels. The position marks the
    /// picture, not the cell, so laying icons out by position alone leaves a wider gap on the
    /// right. Until it can be measured (icons hidden, explorer starting) the whole spacing at
    /// the position is assumed.
    /// </summary>
    public (int Offset, int Width) ItemBox
    {
        get
        {
            if (_itemBox is { } known)
                return known;
            if (DateTime.Now >= _nextMeasure)
            {
                _itemBox = MeasureItemBox();
                _nextMeasure = DateTime.Now.AddSeconds(5);
                if (_itemBox is { } measured)
                {
                    AppData.Log($"Icon cell: offset {measured.Offset}, width {measured.Width}");
                    return measured;
                }
            }
            return (0, Spacing.X);
        }
    }

    [DllImport("oleacc.dll")]
    private static extern int AccessibleObjectFromWindow(IntPtr hwnd, uint objectId, ref Guid iid,
        [MarshalAs(UnmanagedType.Interface)] out object accessible);

    private const uint OBJID_CLIENT = 0xFFFFFFFC;
    private static readonly Guid IID_IAccessible = new("618736E0-3C3D-11CF-810C-00AA00389B71");

    /// <summary>Compares a few icons' positions with their on-screen cells, asked through accessibility.</summary>
    private (int Offset, int Width)? MeasureItemBox()
    {
        try
        {
            var iid = IID_IAccessible;
            if (AccessibleObjectFromWindow(_listView, OBJID_CLIENT, ref iid, out var obj) != 0 ||
                obj is not Accessibility.IAccessible accessible)
                return null;
            try
            {
                if (_view.ItemCount(ShellConst.SVGIO_ALLVIEW, out var count) != 0)
                    return null;
                // Accessibility numbers the list view's items from 1, in the view's order.
                for (var i = 0; i < Math.Min(count, 10); i++)
                {
                    if (_view.Item(i, out var pidl) != 0)
                        continue;
                    try
                    {
                        if (_view.GetItemPosition(pidl, out var position) != 0)
                            continue;
                        ClientToScreen(_listView, ref position);
                        accessible.accLocation(out var left, out _, out var width, out _, i + 1);
                        var offset = position.X - left;
                        if (width > 0 && offset >= 0 && offset < width)
                            return (offset, width);
                    }
                    finally
                    {
                        Marshal.FreeCoTaskMem(pidl);
                    }
                }
            }
            finally
            {
                Marshal.ReleaseComObject(accessible);
            }
        }
        catch (Exception ex) when (ex is COMException or ArgumentException or InvalidCastException)
        {
        }
        return null;
    }

    public uint FolderFlags
    {
        get
        {
            Marshal.ThrowExceptionForHR(_view.GetCurrentFolderFlags(out var flags));
            return flags;
        }
    }

    public void SetFolderFlags(uint mask, uint flags) =>
        Marshal.ThrowExceptionForHR(_view.SetCurrentFolderFlags(mask, flags));

    public IconSnapshot Snapshot()
    {
        var snapshot = new IconSnapshot();
        var iid = ShellConst.IID_IEnumIDList;
        Marshal.ThrowExceptionForHR(_view.Items(ShellConst.SVGIO_ALLVIEW, ref iid, out var enumPtr));

        IEnumIDList enumerator;
        try
        {
            enumerator = (IEnumIDList)Marshal.GetObjectForIUnknown(enumPtr);
        }
        finally
        {
            Marshal.Release(enumPtr);
        }

        while (enumerator.Next(1, out var pidl, out var fetched) == 0 && fetched == 1)
        {
            // Desktop items are children of the namespace root, so the child ID is also absolute.
            var key = ShellConst.GetName(pidl, ShellConst.SIGDN_DESKTOPABSOLUTEPARSING);
            if (key == null || _view.GetItemPosition(pidl, out var clientPos) != 0)
            {
                Marshal.FreeCoTaskMem(pidl);
                continue;
            }

            ClientToScreen(_listView, ref clientPos);
            snapshot.Icons.Add(new DesktopIcon
            {
                Key = key,
                DisplayName = ShellConst.GetName(pidl, ShellConst.SIGDN_NORMALDISPLAY) ?? key,
                Position = clientPos,
                Pidl = pidl,
            });
        }

        return snapshot;
    }

    /// <summary>
    /// Moves icons to the given top-left positions in physical screen pixels.
    /// Explorer's own positioning call shows the icons only ~300 ms later, so they are first
    /// placed straight in the desktop list view, which repaints at once (~30 ms). The explorer
    /// call follows with the same positions so explorer records them as well.
    /// </summary>
    public void Move(IReadOnlyList<(DesktopIcon Icon, POINT ScreenPosition)> moves)
    {
        if (moves.Count == 0)
            return;

        var pidls = new IntPtr[moves.Count];
        var points = new POINT[moves.Count];
        for (var i = 0; i < moves.Count; i++)
        {
            pidls[i] = moves[i].Icon.Pidl;
            var pt = moves[i].ScreenPosition;
            ScreenToClient(_listView, ref pt);
            points[i] = pt;
        }

        MoveInListView(moves, points);
        Marshal.ThrowExceptionForHR(
            _view.SelectAndPositionItems((uint)moves.Count, pidls, points, ShellConst.SVSI_POSITIONITEM));
    }

    private const uint LVM_SETITEMPOSITION = 0x1000 + 15;

    private void MoveInListView(IReadOnlyList<(DesktopIcon Icon, POINT ScreenPosition)> moves, POINT[] points)
    {
        // The message packs both coordinates into 16 bits each.
        if (points.Any(p => p.X is < short.MinValue or > short.MaxValue || p.Y is < short.MinValue or > short.MaxValue))
            return;

        // The view's item index is the list view's item index; look it up by name in case items
        // were added or removed since the snapshot.
        if (_view.ItemCount(ShellConst.SVGIO_ALLVIEW, out var count) != 0)
            return;
        var wanted = moves.Select(m => m.Icon.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var indexOf = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < count && indexOf.Count < wanted.Count; i++)
        {
            if (_view.Item(i, out var pidl) != 0)
                continue;
            var key = ShellConst.GetName(pidl, ShellConst.SIGDN_DESKTOPABSOLUTEPARSING);
            Marshal.FreeCoTaskMem(pidl);
            if (key != null && wanted.Contains(key))
                indexOf[key] = i;
        }

        for (var i = 0; i < moves.Count; i++)
        {
            if (!indexOf.TryGetValue(moves[i].Icon.Key, out var index))
                continue;
            var lParam = (IntPtr)((points[i].Y << 16) | (points[i].X & 0xFFFF));
            SendMessageTimeout(_listView, LVM_SETITEMPOSITION, (IntPtr)index, lParam, SMTO_ABORTIFHUNG, 200, out _);
        }
    }

    [DllImport("user32.dll", EntryPoint = "RedrawWindow")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RedrawWholeWindow(IntPtr hWnd, IntPtr lprcUpdate, IntPtr hrgnUpdate, uint flags);

    private const uint RDW_INVALIDATE = 0x0001;
    private const uint RDW_ERASE = 0x0004;
    private const uint RDW_ALLCHILDREN = 0x0080;

    /// <summary>
    /// Repaints the whole icon layer. Only invalidates: waiting for explorer to finish painting
    /// the whole desktop would stall the caller.
    /// </summary>
    public void RepaintAll() =>
        RedrawWholeWindow(_listView, IntPtr.Zero, IntPtr.Zero, RDW_INVALIDATE | RDW_ERASE | RDW_ALLCHILDREN);

    private const uint SMTO_ABORTIFHUNG = 0x0002;

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam,
        uint flags, uint timeout, out IntPtr result);
}