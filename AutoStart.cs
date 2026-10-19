using Microsoft.Win32;

namespace TidyDesktop;

/// <summary>Starting with Windows, through the current user's Run key (no admin rights needed).</summary>
internal static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "TidyDesktop";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) != null;
        }
    }

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
            key.SetValue(ValueName, $"\"{Environment.ProcessPath}\"");
        else
            key.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    /// <summary>Points the entry at this exe again, in case it was moved since it was turned on.</summary>
    public static void Refresh()
    {
        if (IsEnabled)
            Set(true);
    }
}
