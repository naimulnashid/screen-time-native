using Microsoft.Win32;

namespace ScreenTime.App.State;

/// <summary>
/// Start at login, through the per-user Run key: no admin rights, and it shows
/// in Task Manager's Startup apps where it can be turned off like any other.
/// Off until the user turns it on. Collection does NOT depend on it - the
/// scheduled task collects whether or not the app is running.
/// </summary>
public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Screen Time";

    /// <summary>What the Run key launches: this exe, straight to the tray.</summary>
    private static string Command => $"\"{Environment.ProcessPath}\" --tray";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string value
                   && value.Contains(Environment.ProcessPath ?? "\0", StringComparison.OrdinalIgnoreCase);
        }
    }

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        if (enabled) key.SetValue(ValueName, Command);
        else if (key.GetValue(ValueName) is not null) key.DeleteValue(ValueName);
    }
}
