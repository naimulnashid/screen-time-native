using System.Runtime.InteropServices;

namespace ScreenTime.App.State;

/// <summary>
/// The theme of this process's Win32 menus. The tray's right-click menu is one
/// (TrackPopupMenuEx), so it follows neither WinUI's RequestedTheme nor
/// Windows' app mode on its own.
/// </summary>
public static class MenuTheme
{
    // Undocumented uxtheme ordinals, stable since Windows 10 1903, which
    // Explorer and Notepad++ use for the same job: no public API themes a
    // Win32 menu. Ported from Data Usage Native, which took them from AI Usage
    // Native, where they were verified.
    [DllImport("uxtheme.dll", EntryPoint = "#133")]
    private static extern bool AllowDarkModeForWindow(IntPtr hwnd, bool allow);

    [DllImport("uxtheme.dll", EntryPoint = "#135")]
    private static extern int SetPreferredAppMode(int mode);

    [DllImport("uxtheme.dll", EntryPoint = "#136")]
    private static extern void FlushMenuThemes();

    /// <summary>Whether a theme choice means light, reading Windows' app mode for "system".</summary>
    public static bool IsLight(string choice, Windows.UI.ViewManagement.UISettings system) => choice switch
    {
        "light" => true,
        "system" => system.GetColorValue(Windows.UI.ViewManagement.UIColorType.Background) is { R: > 128 },
        _ => false,
    };

    /// <summary>Sets it for the whole process to the app's resolved theme, "system" included.</summary>
    public static void Apply(bool light)
    {
        try
        {
            SetPreferredAppMode(light ? 3 : 2); // ForceLight : ForceDark
            FlushMenuThemes();
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            // An older Windows: the menu keeps the system's look.
        }
    }

    /// <summary>Lets the window that owns a menu take the theme set by <see cref="Apply"/>.</summary>
    public static void Allow(IntPtr hwnd)
    {
        try
        {
            AllowDarkModeForWindow(hwnd, true);
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
        }
    }
}
