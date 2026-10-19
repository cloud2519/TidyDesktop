using TidyDesktop.Native;
using static TidyDesktop.Native.NativeMethods;

namespace TidyDesktop.Desktop;

/// <summary>
/// Locates the shell windows that make up the desktop:
/// Progman (owner of everything), SHELLDLL_DefView/SysListView32 (the icon layer)
/// and the wallpaper layer that sits underneath the icons.
/// </summary>
internal sealed class DesktopHost
{
    public IntPtr Progman { get; }

    /// <summary>
    /// The top-level window that holds the icons: Progman on Windows 11 24H2+, a WorkerW on older
    /// builds. TidyDesktop's own windows are owned by it, which keeps them just above the desktop:
    /// owned by Progman on older builds they would end up behind the icon layer, out of sight.
    /// </summary>
    public IntPtr Owner { get; }
    public IntPtr DefView { get; }
    public IntPtr ListView { get; }

    /// <summary>Parent for windows that must render above the wallpaper but below the icons.</summary>
    public IntPtr WallpaperLayer { get; }

    /// <summary>Z-order position to use for children of <see cref="WallpaperLayer"/>.</summary>
    public IntPtr WallpaperInsertAfter { get; }

    private DesktopHost(IntPtr progman, IntPtr defView, IntPtr listView, IntPtr wallpaperLayer, IntPtr insertAfter)
    {
        Progman = progman;
        DefView = defView;
        Owner = GetAncestor(defView, GA_ROOT);
        ListView = listView;
        WallpaperLayer = wallpaperLayer;
        WallpaperInsertAfter = insertAfter;
    }

    public bool IsAlive => IsWindow(Progman) && IsWindow(Owner) && IsWindow(ListView) && IsWindow(WallpaperLayer);

    public static DesktopHost? Find()
    {
        var progman = FindWindow("Progman", null);
        if (progman == IntPtr.Zero)
            return null;

        // Undocumented: asks Progman to create the WorkerW that hosts the wallpaper behind the icons.
        SendMessageTimeout(progman, 0x052C, new IntPtr(0xD), new IntPtr(0x1), SMTO_NORMAL, 1000, out _);

        var defView = FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
        IntPtr workerW;

        if (defView != IntPtr.Zero)
        {
            // Windows 11 24H2+: DefView and the wallpaper WorkerW are both children of Progman,
            // with the WorkerW below DefView in sibling z-order.
            workerW = FindWindowEx(progman, IntPtr.Zero, "WorkerW", null);
        }
        else
        {
            // Older builds: DefView lives in a top-level WorkerW and the wallpaper WorkerW is the
            // next top-level window after it.
            workerW = IntPtr.Zero;
            EnumWindows((top, _) =>
            {
                var dv = FindWindowEx(top, IntPtr.Zero, "SHELLDLL_DefView", null);
                if (dv == IntPtr.Zero)
                    return true;
                defView = dv;
                workerW = FindWindowEx(IntPtr.Zero, top, "WorkerW", null);
                return false;
            }, IntPtr.Zero);
        }

        if (defView == IntPtr.Zero)
            return null;

        var listView = FindWindowEx(defView, IntPtr.Zero, "SysListView32", "FolderView");
        if (listView == IntPtr.Zero)
            return null;

        // Children of WorkerW go on top of any live-wallpaper windows already in it.
        // Without a WorkerW, fall back to Progman and slot in just below DefView when it is a sibling.
        if (workerW != IntPtr.Zero)
            return new DesktopHost(progman, defView, listView, workerW, HWND_TOP);

        var defViewIsChild = FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null) == defView;
        return new DesktopHost(progman, defView, listView, progman, defViewIsChild ? defView : HWND_TOP);
    }
}
