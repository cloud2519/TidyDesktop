using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using Windows.ApplicationModel;

namespace TidyDesktop;

/// <summary>
/// Starting with Windows. The zip build uses the current user's Run key (no admin rights needed);
/// the Microsoft Store (MSIX) build cannot, because registry writes there stay inside the package,
/// so it uses the StartupTask declared in Package\AppxManifest.xml instead.
/// </summary>
internal static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "TidyDesktop";
    private const string StartupTaskId = "TidyDesktopStartup";

    private const int AppModelErrorNoPackage = 15700;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(ref uint length, StringBuilder? name);

    /// <summary>True when running from the Microsoft Store (MSIX) package.</summary>
    public static bool IsPackaged { get; } = DetectPackaged();

    private static bool DetectPackaged()
    {
        uint length = 0;
        return GetCurrentPackageFullName(ref length, null) != AppModelErrorNoPackage;
    }

    public static bool IsEnabled
    {
        get
        {
            if (IsPackaged)
                return GetTask()?.State is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy;
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) != null;
        }
    }

    public static void Set(bool enabled)
    {
        if (IsPackaged)
        {
            SetTask(enabled);
            return;
        }
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
            key.SetValue(ValueName, $"\"{Environment.ProcessPath}\"");
        else
            key.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    /// <summary>Points the entry at this exe again, in case it was moved since it was turned on.</summary>
    public static void Refresh()
    {
        // The Store build is always started through its package, so there is no path to refresh.
        if (!IsPackaged && IsEnabled)
            Set(true);
    }

    // StartupTask is async-only; run it off the UI thread so waiting here cannot deadlock.
    private static StartupTask? GetTask()
    {
        try { return Task.Run(() => StartupTask.GetAsync(StartupTaskId).AsTask()).GetAwaiter().GetResult(); }
        catch { return null; }
    }

    private static void SetTask(bool enabled)
    {
        try
        {
            var task = GetTask();
            if (task == null) return;
            // When the user turned it off in Windows Settings (DisabledByUser) the request is ignored;
            // callers re-read IsEnabled afterwards, so the checkbox shows the real state.
            if (enabled)
                Task.Run(() => task.RequestEnableAsync().AsTask()).GetAwaiter().GetResult();
            else
                task.Disable();
        }
        catch { }
    }
}
