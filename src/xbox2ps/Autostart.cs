using Microsoft.Win32;

namespace Xbox2Ps;

/// <summary>"Launch with Windows" through the per-user Run key; no admin needed.</summary>
internal static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "xbox2ps";

    private static string Command => $"\"{Environment.ProcessPath}\" --minimized";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string;
        }
    }

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(ValueName, Command);
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    /// <summary>If the exe moved since autostart was turned on, point the entry at the new place.</summary>
    public static void Refresh()
    {
        if (IsEnabled) Set(true);
    }
}
