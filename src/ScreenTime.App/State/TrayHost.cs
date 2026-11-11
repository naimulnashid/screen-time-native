using System.Drawing;
using H.NotifyIcon.Core;
using ScreenTime.Core.View;

namespace ScreenTime.App.State;

/// <summary>
/// The notification-area icon: the latest day's active time in its tooltip,
/// click to open, right-click for the menu. It belongs to the tray process
/// (<see cref="TrayProcess"/>), which outlives the window. Recording never
/// depends on it - the sampler is its own process, started by the logon task -
/// but the tray keeps the numbers and the problem notifications live.
/// </summary>
public sealed class TrayHost : IDisposable
{
    private readonly TrayIconWithContextMenu _tray;
    private readonly Icon _icon;
    private readonly PopupMenuItem _startAtLogin;
    private readonly PopupMenuItem _autoRefresh;
    private readonly PopupMenuItem _notify;

    /// <param name="onUi">Marshals a menu click, which arrives on the icon's thread, onto the UI thread.</param>
    /// <param name="toggleAutoRefresh">Flips the setting and returns its new value; <paramref name="toggleNotify"/> likewise.</param>
    public TrayHost(Action<Action> onUi, UiSettings settings, Action open, Action syncNow, Func<bool> toggleAutoRefresh, Func<bool> toggleNotify, Action exit)
    {
        _icon = new Icon(Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico"), 16, 16);
        // The id is per exe path. Windows binds a tray icon's id to the exe
        // that first registered it and silently refuses it from any other:
        // once a Debug build has run, the installed app comes up with no tray
        // icon at all (HKCU\Control Panel\NotifyIconSettings holds the Debug
        // path), and closed to the tray it cannot be brought back.
        _tray = new TrayIconWithContextMenu($"ScreenTimeNative.Tray|{Environment.ProcessPath}")
        {
            Icon = _icon.Handle,
            ToolTip = "Screen Time",
        };

        _startAtLogin = new PopupMenuItem("Start at login", (_, _) => onUi(() =>
        {
            try { StartupRegistration.Set(!StartupRegistration.IsEnabled); }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException) { }
            _startAtLogin!.Checked = StartupRegistration.IsEnabled;
        }))
        { Checked = StartupRegistration.IsEnabled };

        _autoRefresh = new PopupMenuItem("Refresh when new data lands", (_, _) => onUi(() => _autoRefresh!.Checked = toggleAutoRefresh()))
        { Checked = settings.AutoRefresh };

        _notify = new PopupMenuItem("Notify when recording stops", (_, _) => onUi(() => _notify!.Checked = toggleNotify()))
        { Checked = settings.NotifyProblems };

        _tray.ContextMenu = new PopupMenu
        {
            Items =
            {
                new PopupMenuItem("Open Screen Time", (_, _) => onUi(open)),
                new PopupMenuItem("Sync now", (_, _) => onUi(syncNow)),
                new PopupMenuSeparator(),
                _autoRefresh,
                _notify,
                _startAtLogin,
                new PopupMenuSeparator(),
                new PopupMenuItem("Exit", (_, _) => onUi(exit)),
            },
        };

        _tray.MessageWindow.MouseEventReceived += (_, e) =>
        {
            if (e.MouseEvent is MouseEvent.IconLeftMouseUp or MouseEvent.IconDoubleClick) onUi(open);
        };
        _tray.Create();
        MenuTheme.Allow(_tray.MessageWindow.Handle);
    }

    public void SyncChecks(UiSettings settings)
    {
        _autoRefresh.Checked = settings.AutoRefresh;
        _notify.Checked = settings.NotifyProblems;
        _startAtLogin.Checked = StartupRegistration.IsEnabled;
    }

    /// <summary>"Today: 6h 42m" - or the latest day held, named - and whether the sampler is recording.</summary>
    public void ShowStatus((string? Date, long Ms) latest, bool samplerAlive, long failedSaves)
    {
        var lines = new List<string> { "Screen Time" };
        if (latest is { Date: { } date, Ms: var ms })
        {
            var today = DateOnly.FromDateTime(DateTime.Now).ToString("yyyy-MM-dd");
            lines.Add($"{(date == today ? "Today" : Format.DayShort(date))}: {Format.Duration(ms)}");
        }
        if (!samplerAlive) lines.Add("Not recording - the sampler is stopped");
        if (failedSaves > 0) lines.Add($"Last {failedSaves} save(s) failed");
        // The shell truncates a tray tooltip at 127 characters.
        var text = string.Join("\n", lines);
        _tray.UpdateToolTip(text.Length > 127 ? text[..127] : text);
    }

    public void Notify(string title, string message, bool warning = false) =>
        _tray.ShowNotification(title, message, warning ? NotificationIcon.Warning : NotificationIcon.Info, null, false, false, false, false, null);

    public void Dispose()
    {
        _tray.Dispose();
        _icon.Dispose();
    }
}
