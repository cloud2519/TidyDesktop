using System.Runtime.InteropServices;

namespace TidyDesktop.Native;

// COM interfaces for talking to the desktop's shell view from outside explorer.exe.
// Only the vtable order matters for methods we never call, so their signatures are placeholders.

[ComImport, Guid("85CB6900-4D95-11CF-960C-0080C7F4EE85"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
internal interface IShellWindows
{
    int Count { get; }

    [return: MarshalAs(UnmanagedType.IDispatch)]
    object Item(object index);

    [return: MarshalAs(UnmanagedType.IUnknown)]
    object _NewEnum();

    void Register([MarshalAs(UnmanagedType.IDispatch)] object pid, int hwnd, int swClass, out int plCookie);
    void RegisterPending(int lThreadId, ref object pvarloc, ref object pvarlocRoot, int swClass, out int plCookie);
    void Revoke(int lCookie);
    void OnNavigate(int lCookie, ref object pvarLoc);
    void OnActivated(int lCookie, [MarshalAs(UnmanagedType.VariantBool)] bool fActive);

    [return: MarshalAs(UnmanagedType.IDispatch)]
    object FindWindowSW(ref object pvarLoc, ref object pvarLocRoot, int swClass, out int phwnd, int swfwOptions);
}

[ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IOleServiceProvider
{
    [PreserveSig]
    int QueryService(ref Guid guidService, ref Guid riid, out IntPtr ppvObject);
}

[ComImport, Guid("000214E2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellBrowser
{
    // IOleWindow
    void GetWindow(out IntPtr phwnd);
    void ContextSensitiveHelp(int fEnterMode);

    // IShellBrowser
    void InsertMenusSB(IntPtr hmenuShared, IntPtr lpMenuWidths);
    void SetMenuSB(IntPtr hmenuShared, IntPtr holemenuRes, IntPtr hwndActiveObject);
    void RemoveMenusSB(IntPtr hmenuShared);
    void SetStatusTextSB(IntPtr pszStatusText);
    void EnableModelessSB(int fEnable);
    void TranslateAcceleratorSB(IntPtr pmsg, ushort wID);
    void BrowseObject(IntPtr pidl, uint wFlags);
    void GetViewStateStream(uint grfMode, out IntPtr ppStrm);
    void GetControlWindow(uint id, out IntPtr phwnd);
    void SendControlMsg(uint id, uint uMsg, IntPtr wParam, IntPtr lParam, out IntPtr pret);

    [return: MarshalAs(UnmanagedType.IUnknown)]
    object QueryActiveShellView();
}

[ComImport, Guid("1af3a467-214f-4298-908e-06b03e0b39f9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IFolderView2
{
    // IFolderView
    [PreserveSig] int GetCurrentViewMode(out uint pViewMode);
    [PreserveSig] int SetCurrentViewMode(uint viewMode);
    [PreserveSig] int GetFolder(ref Guid riid, out IntPtr ppv);
    [PreserveSig] int Item(int iItemIndex, out IntPtr ppidl);
    [PreserveSig] int ItemCount(uint uFlags, out int pcItems);
    [PreserveSig] int Items(uint uFlags, ref Guid riid, out IntPtr ppv);
    [PreserveSig] int GetSelectionMarkedItem(out int piItem);
    [PreserveSig] int GetFocusedItem(out int piItem);
    [PreserveSig] int GetItemPosition(IntPtr pidl, out POINT ppt);
    [PreserveSig] int GetSpacing(ref POINT ppt);
    [PreserveSig] int GetDefaultSpacing(out POINT ppt);
    [PreserveSig] int GetAutoArrange();
    [PreserveSig] int SelectItem(int iItem, uint dwFlags);
    [PreserveSig] int SelectAndPositionItems(uint cidl,
        [MarshalAs(UnmanagedType.LPArray)] IntPtr[] apidl,
        [MarshalAs(UnmanagedType.LPArray)] POINT[] apt,
        uint dwFlags);

    // IFolderView2
    void SetGroupBy();
    void GetGroupBy();
    void SetViewProperty();
    void GetViewProperty();
    void SetTileViewProperties();
    void SetExtendedTileViewProperties();
    void SetText();
    [PreserveSig] int SetCurrentFolderFlags(uint dwMask, uint dwFlags);
    [PreserveSig] int GetCurrentFolderFlags(out uint pdwFlags);
}

[ComImport, Guid("000214F2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IEnumIDList
{
    [PreserveSig] int Next(uint celt, out IntPtr rgelt, out uint pceltFetched);
    [PreserveSig] int Skip(uint celt);
    [PreserveSig] int Reset();
    [PreserveSig] int Clone(out IEnumIDList ppenum);
}

internal static class ShellConst
{
    public static readonly Guid CLSID_ShellWindows = new("9BA05972-F6A8-11CF-A442-00A0C90A8F39");
    public static readonly Guid SID_STopLevelBrowser = new("4C96BE40-915C-11CF-99D3-00AA004AE837");
    public static readonly Guid IID_IShellBrowser = new("000214E2-0000-0000-C000-000000000046");
    public static readonly Guid IID_IEnumIDList = new("000214F2-0000-0000-C000-000000000046");

    public const int CSIDL_DESKTOP = 0;
    public const int SWC_DESKTOP = 8;
    public const int SWFO_NEEDDISPATCH = 1;

    public const uint SVGIO_ALLVIEW = 0x2;
    public const uint SVSI_POSITIONITEM = 0x80;

    public const uint FWF_AUTOARRANGE = 0x1;
    public const uint FWF_SNAPTOGRID = 0x4;
    public const uint FWF_NOICONS = 0x1000;

    public const uint SIGDN_NORMALDISPLAY = 0x00000000;
    public const uint SIGDN_DESKTOPABSOLUTEPARSING = 0x80028000;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern int SHGetNameFromIDList(IntPtr pidl, uint sigdnName, out IntPtr ppszName);

    public static string? GetName(IntPtr pidl, uint sigdn)
    {
        if (SHGetNameFromIDList(pidl, sigdn, out var p) != 0 || p == IntPtr.Zero)
            return null;
        try
        {
            return Marshal.PtrToStringUni(p);
        }
        finally
        {
            Marshal.FreeCoTaskMem(p);
        }
    }
}
