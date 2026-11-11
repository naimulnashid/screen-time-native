using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using ScreenTime.Core;
using ScreenTime.Core.Collect;
using ScreenTime.Core.Query;
using ScreenTime.Core.Sample;

namespace ScreenTime.App.State;

/// <summary>
/// The tray process: the icon, its menu and tooltip, the problem
/// notifications, and the window's launcher. It is what stays when the window
/// closes, so it never loads WinUI.
/// </summary>
/// <remarks>
/// <para><b>Why two processes:</b> WinUI cannot be unloaded once loaded, and
/// held about 150 MB with the window closed to the tray (measured
/// 2026-11-11). The window is now its own process (<c>--dashboard</c>), gone
/// when it closes. Don't add a WinUI type (anything under
/// <c>Microsoft.UI.Xaml</c>) to what this class touches. The pattern is AI
/// Usage Native's.</para>
/// <para><b>It reads the database for itself</b>: three small queries (the
/// newest day, the last runs, the heartbeat file) when a save lands and every
/// ten minutes, whether or not a window is open. So the tooltip and the
/// notifications need nothing from the dashboard, and <b>health is checked
/// here only</b>: one process decides that a failure has been notified.</para>
/// </remarks>
public sealed class TrayProcess : IDisposable
{
    private readonly DispatcherQueue _ui;
    private readonly TrayHost _tray;
    private readonly DispatcherQueueTimer _health;
    private readonly CancellationTokenSource _stopping = new();
    private readonly Windows.UI.ViewManagement.UISettings _systemColors = new();
    private UiSettings _settings;
    private Process? _dashboard;
    private string? _dataDir;
    private UsageQueries? _queries;
    private FileSystemWatcher? _watcher;
    private DispatcherQueueTimer? _debounce;
    private int _reading;

    /// <param name="openDashboard">False when started at login (<c>--tray</c>): the icon only.</param>
    public TrayProcess(DispatcherQueue ui, bool openDashboard)
    {
        _ui = ui;
        UiSettings.IsTray = true;
        _settings = UiSettings.Load();

        // Before the icon exists: its menu window takes the theme as it is made.
        ApplyMenuTheme();
        // "System" follows Windows while the app runs, not only at launch.
        _systemColors.ColorValuesChanged += (_, _) => ui.TryEnqueue(ApplyMenuTheme);

        _tray = new TrayHost(action => ui.TryEnqueue(() => action()), _settings, OpenDashboard, SyncNow,
            () => Toggle(s => s.AutoRefresh = !s.AutoRefresh).AutoRefresh,
            () => Toggle(s => s.NotifyProblems = !s.NotifyProblems).NotifyProblems,
            () => Exit(closeDashboard: true));

        // A stall can begin with no write at all, so health is also checked on a clock.
        _health = ui.CreateTimer();
        _health.Interval = TimeSpan.FromMinutes(10);
        _health.Tick += (_, _) => Read();
        _health.Start();

        TrayLink.Serve(parts => ui.TryEnqueue(() => Handle(parts)), _stopping.Token);
        if (openDashboard) OpenDashboard();
        Read();
    }

    private void ApplyMenuTheme() => MenuTheme.Apply(MenuTheme.IsLight(_settings.Theme, _systemColors));

    /* ---------------------------------------------------------- Dashboard */

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);

    /// <summary>
    /// Opens the window: a new dashboard process, which hands itself to one
    /// already open. Windows gives the foreground to the process the user
    /// last acted on, which is this one (a click on the icon), so the right
    /// is passed on, or the window would open behind whatever is in front.
    /// </summary>
    public void OpenDashboard()
    {
        if (_dashboard is { HasExited: false } open) AllowSetForegroundWindow(open.Id);
        if (Start("--dashboard") is { } started)
        {
            AllowSetForegroundWindow(started.Id);
            started.Dispose();
        }
    }

    /// <summary>
    /// Passes a request to the open dashboard: the flag makes the new process
    /// a messenger, which never becomes a window (see <see cref="Program"/>).
    /// </summary>
    private void Tell(string flag) => Start("--dashboard " + flag)?.Dispose();

    private static Process? Start(string arguments)
    {
        try
        {
            return Process.Start(new ProcessStartInfo(Environment.ProcessPath!, arguments) { UseShellExecute = false });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }

    private void Handle(string[] parts)
    {
        switch (parts[0])
        {
            case "hello" when parts.Length > 1 && int.TryParse(parts[1], out var pid):
                Adopt(pid);
                break;
            case "settings":
                ReloadSettings();
                Read();
                break;
            case "exit":
                Exit(closeDashboard: false);
                break;
        }
    }

    /// <summary>A dashboard said hello: watch it end, however it was started.</summary>
    private void Adopt(int pid)
    {
        if (_dashboard is { } current && !current.HasExited && current.Id == pid) return;
        try
        {
            var process = Process.GetProcessById(pid);
            process.EnableRaisingEvents = true;
            process.Exited += (_, _) => _ui.TryEnqueue(() => DashboardClosed(process));
            _dashboard = process;
            if (process.HasExited) DashboardClosed(process);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Gone before it could be watched: nothing is open.
        }
    }

    private void DashboardClosed(Process process)
    {
        if (!ReferenceEquals(_dashboard, process)) return;
        _dashboard = null;
        process.Dispose();
        ReloadSettings();
        if (!_settings.CloseToTray)
        {
            Exit(closeDashboard: false);
            return;
        }
        if (!_settings.TrayNoteShown)
        {
            _settings.TrayNoteShown = true;
            _settings.Save();
            _tray.Notify("Screen Time is still running", "It keeps its numbers up to date from the notification area. Right-click the icon to exit. Recording carries on either way: the sampler is its own process.");
        }
        Read();
    }

    /* ----------------------------------------------------------- Settings */

    /// <summary>The file is the window's as much as this process's: read it again before trusting or changing it.</summary>
    private void ReloadSettings()
    {
        _settings = UiSettings.Load();
        _tray.SyncChecks(_settings);
        ApplyMenuTheme();
    }

    private UiSettings Toggle(Action<UiSettings> change)
    {
        _settings = UiSettings.Load();
        change(_settings);
        _settings.Save();
        if (_dashboard is not null) Tell("--reload-settings");
        return _settings;
    }

    /* ------------------------------------------------------------ Reading */

    /// <summary>(Re)reads where the data folder is, and watches its database: it can be chosen or moved while this runs.</summary>
    private void Open()
    {
        var dir = AppPaths.DataDir;
        if (dir == _dataDir && (_watcher is not null || dir is null)) return;
        _dataDir = dir;
        _watcher?.Dispose();
        _watcher = null;
        _queries = null;
        if (dir is null) return;
        _queries = new UsageQueries(AppPaths.DatabasePath(dir), AppPaths.ColoursPath(dir));
        var live = Path.GetDirectoryName(AppPaths.DatabasePath(dir))!;
        try
        {
            Directory.CreateDirectory(live);
            _watcher = new FileSystemWatcher(live, "screen-time.db*") { EnableRaisingEvents = true, NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName };
            // Not the -shm file: every reader of a WAL database writes it, this
            // process's own read included, so each read scheduled the next -
            // one every three seconds, all day (seen 2026-11-11).
            _watcher.Changed += (_, e) => { if (!IsSharedMemory(e)) Touch(); };
            _watcher.Created += (_, e) => { if (!IsSharedMemory(e)) Touch(); };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Unwatchable: the ten-minute clock still reads it.
        }
    }

    internal static bool IsSharedMemory(FileSystemEventArgs e) => e.Name?.EndsWith("-shm", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>A write burst is many events; one read a few seconds after the last.</summary>
    private void Touch()
    {
        if (!_settings.AutoRefresh) return;
        _ui.TryEnqueue(() =>
        {
            if (_debounce is null)
            {
                _debounce = _ui.CreateTimer();
                _debounce.Interval = TimeSpan.FromSeconds(3);
                _debounce.IsRepeating = false;
                _debounce.Tick += (_, _) => Read();
            }
            _debounce.Stop();
            _debounce.Start();
        });
    }

    /// <summary>The tooltip's figures and the health of collection, off the UI thread.</summary>
    private async void Read()
    {
        if (Interlocked.Exchange(ref _reading, 1) == 1) return;
        try
        {
            Open();
            var queries = _queries;
            var (sampler, sync, latest) = await Task.Run(() =>
            {
                var status = SamplerStatus.Read(AppPaths.SamplerDir);
                if (queries is null || !queries.DatabaseExists) return (status, (SyncData?)null, ((string? Date, long Ms))default);
                return (status, queries.Sync(10), queries.Latest());
            });
            _tray.ShowStatus(latest, sampler.Alive, sync?.ConsecutiveFailures ?? 0);
            // Afresh: CheckHealth may save, and must not write back a zoom or
            // theme the window has changed since.
            _settings = UiSettings.Load();
            CheckHealth(sampler, sync);
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException)
        {
            // Mid-write, or the drive is gone: the next change tries again.
        }
        finally
        {
            Interlocked.Exchange(ref _reading, 0);
        }
    }

    /// <summary>
    /// One notification per failed ingest, and one per stopped sampler. A
    /// sampler that has stopped is the failure that matters most here: nothing
    /// backfills, so every minute it is down is a minute lost for good.
    /// </summary>
    private void CheckHealth(SamplerStatus sampler, SyncData? sync)
    {
        if (!_settings.NotifyProblems) return;
        var newest = sync?.Runs.FirstOrDefault();
        if (newest is { Status: "failed" } && newest.Id > _settings.NotifiedFailureId)
        {
            _settings.NotifiedFailureId = newest.Id;
            _settings.Save();
            _tray.Notify("Screen Time could not save its data", Trim(newest.Error ?? "The ingest reported a failure. Open Sync Status for details."), warning: true);
            return;
        }
        // A heartbeat that never existed is a sampler not installed, not one
        // that stopped: the Sync page says so, without a notification.
        if (!sampler.Alive && sampler.Updated is { } since && sampler.StaleSeconds > 300 && since != _settings.NotifiedStallSince)
        {
            _settings.NotifiedStallSince = since;
            _settings.Save();
            _tray.Notify("Screen Time has stopped recording", $"The sampler's last heartbeat was {Core.View.Format.DateTimeLocal(since)}. Time passing now is not being recorded. Open Sync Status.", warning: true);
        }
    }

    private static string Trim(string s) => s.Length > 200 ? s[..197] + "..." : s;

    /// <summary>
    /// The menu's Sync now: fold the sampler's spans in straight away, here,
    /// whether or not a window is open - an open one sees the database change.
    /// A failure is a failed run, which <see cref="CheckHealth"/> reports.
    /// </summary>
    private async void SyncNow()
    {
        if (AppPaths.DataDir is null) return;
        try
        {
            await Task.Run(() => Ingest.Run(new IngestOptions { ForceBackup = true }));
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException or UnauthorizedAccessException)
        {
        }
        Read();
    }

    /* --------------------------------------------------------------- Exit */

    private void Exit(bool closeDashboard)
    {
        if (closeDashboard && _dashboard is not null) Tell("--exit");
        Dispose();
        Environment.Exit(0);
    }

    public void Dispose()
    {
        _stopping.Cancel();
        _health.Stop();
        _watcher?.Dispose();
        _tray.Dispose();
    }
}
