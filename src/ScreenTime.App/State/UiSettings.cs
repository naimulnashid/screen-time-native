using System.Text.Json;
using ScreenTime.Core;
using ScreenTime.Core.Query;

namespace ScreenTime.App.State;

/// <summary>
/// The app's own preferences, in the local folder: cheap to lose, so they do
/// not belong with the history on the data drive. An unreadable file means
/// the defaults.
/// </summary>
public sealed class UiSettings
{
    /// <summary>The range chips. Opens on All: the app leads with everything it holds.</summary>
    public int ScopeDays { get; set; } = Scope.AllDays;

    /// <summary>Re-read the database when the sampler saves to it.</summary>
    public bool AutoRefresh { get; set; } = true;

    /// <summary>Closing the window keeps the app in the tray rather than exiting.</summary>
    public bool CloseToTray { get; set; } = true;

    /// <summary>A Windows notification when collection fails or stalls.</summary>
    public bool NotifyProblems { get; set; } = true;

    /// <summary>
    /// "dark", "light" or "system". Dark by default: it is what this app has
    /// always been. System follows Windows' app mode and keeps following it.
    /// </summary>
    public string Theme { get; set; } = "dark";

    /// <summary>Page zoom, 0.5 to 2: Ctrl+Plus / Ctrl+Minus / Ctrl+0, as in a browser.</summary>
    public double Zoom { get; set; } = 1;

    /// <summary>Whether the "still running in the tray" note has been shown once.</summary>
    public bool TrayNoteShown { get; set; }

    /// <summary>The newest failed run already notified, so one failure is one notification.</summary>
    public long NotifiedFailureId { get; set; }

    /// <summary>The last success a stall was notified against, so one stall is one notification.</summary>
    public string? NotifiedStallSince { get; set; }

    private static string FilePath => Path.Combine(AppPaths.LocalDir, "app-settings.json");

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static UiSettings Load()
    {
        try
        {
            if (File.Exists(FilePath)) return JsonSerializer.Deserialize<UiSettings>(File.ReadAllText(FilePath)) ?? new UiSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
        }
        return new UiSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.LocalDir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Options));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A preference that did not stick is not worth an error.
        }
    }
}
