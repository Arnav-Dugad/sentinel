using Microsoft.Win32;

namespace Sentinel.App.Services;

/// <summary>
/// Adds or removes Sentinel's own "start when I sign in" entry under the current user's Run key.
/// Only ever called when the user flips the switch in Settings; Sentinel touches no other startup entries.
/// </summary>
public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Sentinel";

    public static bool IsEnabled()
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey);
        return k?.GetValue(ValueName) is string;
    }

    public static void Set(bool enabled)
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey, writable: true) ?? Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
        {
            var exe = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Sentinel.exe");
            k.SetValue(ValueName, $"\"{exe}\" --background");
        }
        else if (k.GetValue(ValueName) is not null)
        {
            k.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}
