using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace TidyDesktop.Native;

/// <summary>
/// Icons and opening for desktop items by their parsing name: a file or folder path, or a shell
/// item such as "::{GUID}" (This PC), which has no path to start.
/// </summary>
internal static class ShellItems
{
    private const uint SHGFI_ICON = 0x100;
    private const uint SHGFI_LARGEICON = 0x0;
    private const uint SHGFI_PIDL = 0x8;
    private const uint SEE_MASK_INVOKEIDLIST = 0xC;
    private const int SW_SHOWNORMAL = 1;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHELLEXECUTEINFO
    {
        public int cbSize;
        public uint fMask;
        public IntPtr hwnd;
        public string? lpVerb;
        public string? lpFile;
        public string? lpParameters;
        public string? lpDirectory;
        public int nShow;
        public IntPtr hInstApp;
        public IntPtr lpIDList;
        public string? lpClass;
        public IntPtr hkeyClass;
        public uint dwHotKey;
        public IntPtr hIcon;
        public IntPtr hProcess;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(string name, IntPtr bindingContext, out IntPtr pidl, uint sfgaoIn, out uint sfgaoOut);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(IntPtr pidl, uint attributes, ref SHFILEINFO info, uint size, uint flags);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShellExecuteEx(ref SHELLEXECUTEINFO info);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icon);

    // Reading an icon takes a few milliseconds, which adds up over a whole desktop on every open.
    private static readonly Dictionary<string, ImageSource?> IconCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The item's large icon as explorer shows it, or null if it cannot be read.</summary>
    public static ImageSource? Icon(string key)
    {
        if (IconCache.TryGetValue(key, out var cached))
            return cached;
        if (IconCache.Count > 2000)
            IconCache.Clear();

        ImageSource? image = null;
        if (SHParseDisplayName(key, IntPtr.Zero, out var pidl, 0, out _) == 0)
        {
            try
            {
                var info = new SHFILEINFO();
                SHGetFileInfo(pidl, 0, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), SHGFI_PIDL | SHGFI_ICON | SHGFI_LARGEICON);
                if (info.hIcon != IntPtr.Zero)
                {
                    image = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    image.Freeze();
                    DestroyIcon(info.hIcon);
                }
            }
            finally
            {
                Marshal.FreeCoTaskMem(pidl);
            }
        }
        IconCache[key] = image;
        return image;
    }

    /// <summary>Opens the item the way a double-click on the desktop does.</summary>
    public static bool Open(string key)
    {
        if (SHParseDisplayName(key, IntPtr.Zero, out var pidl, 0, out _) != 0)
            return false;
        try
        {
            var info = new SHELLEXECUTEINFO
            {
                cbSize = Marshal.SizeOf<SHELLEXECUTEINFO>(),
                fMask = SEE_MASK_INVOKEIDLIST,
                lpIDList = pidl,
                nShow = SW_SHOWNORMAL,
            };
            return ShellExecuteEx(ref info);
        }
        finally
        {
            Marshal.FreeCoTaskMem(pidl);
        }
    }
}
