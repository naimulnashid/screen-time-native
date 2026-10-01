using ScreenTime.Core;
using ScreenTime.Core.Collect;
using ScreenTime.Core.Data;
using ScreenTime.Core.Naming;
using ScreenTime.Core.Query;
using ScreenTime.Core.Sample;
using Microsoft.UI.Dispatching;

namespace ScreenTime.App.State;

/// <summary>
/// Where the app's data comes from, and when it changes.
/// </summary>
/// <remarks>
/// <para>The pages read only the database; the sampler's ingest is what writes
/// it, every 15 minutes. So "live refresh" is watching the database file and
/// reloading when an ingest lands - throttled, since one ingest writes several
/// times in a second.</para>
/// <para>Queries run off the UI thread, and each page asks for its own data;
/// this class only holds what every page shares: the device settings, the
/// range, the colour map, the logos and the health of collection.</para>
/// </remarks>
public sealed class AppState : IDisposable
{
    private readonly DispatcherQueue _ui;
    private readonly UiSettings _settings;
    private FileSystemWatcher? _watcher;
    private DispatcherQueueTimer? _debounce;
    private string? _signature;

    public AppState(DispatcherQueue ui, UiSettings settings)
    {
        _ui = ui;
        _settings = settings;
        Scope = new Scope(Math.Clamp(settings.ScopeDays, 1, Scope.AllDays));
        Logos = new LogoStore(ui);
        Logos.Changed += () => _ = ReloadAsync();
        Open();
    }

    public string? DataDir { get; private set; }

    public DeviceSettings Device { get; private set; } = new();

    public UsageQueries? Queries { get; private set; }

    public Scope Scope { get; private set; }

    public LogoStore Logos { get; }

    /// <summary>App name to colour, from all-time totals.</summary>
    public IReadOnlyDictionary<string, string> Colors { get; private set; } = new Dictionary<string, string>();

    /// <summary>App key to the colour the user chose, for the apps that have one.</summary>
    public IReadOnlyDictionary<string, string> ColorOverrides { get; private set; } = new Dictionary<string, string>();

    /// <summary>Name to logo, renames included, keyed by <see cref="AppIcons.Key"/>.</summary>
    public IReadOnlyDictionary<string, AppIcon> Icons { get; private set; } = new Dictionary<string, AppIcon>();

    /// <summary>The ingest's health, as of the last reload.</summary>
    public SyncData? Sync { get; private set; }

    /// <summary>The sampler's heartbeat, as of the last reload.</summary>
    public SamplerStatus Sampler { get; private set; } = SamplerStatus.Dead;

    /// <summary>The newest day with data and its active time.</summary>
    public (string? Date, long Ms) Latest { get; private set; }

    /// <summary>Bumped whenever something a page shows may have changed.</summary>
    public int Version { get; private set; }

    public bool HasData => Queries is { DatabaseExists: true };

    /// <summary>Raised on the UI thread after a reload.</summary>
    public event Action? Changed;

    /// <summary>Raised on the UI thread for a problem worth a notification: title, message.</summary>
    public event Action<string, string>? Problem;

    public void OnUi(Action action) => _ui.TryEnqueue(() => action());

    /// <summary>(Re)reads where the data folder is, and watches its database.</summary>
    public void Open()
    {
        DataDir = AppPaths.DataDir;
        _watcher?.Dispose();
        _watcher = null;
        if (DataDir is null)
        {
            Queries = null;
            return;
        }
        Device = DeviceSettings.Load(DataDir);
        Queries = new UsageQueries(AppPaths.DatabasePath(DataDir), AppPaths.ColoursPath(DataDir));
        Logos.SetFolder(AppPaths.LogosDir(DataDir));

        var live = Path.GetDirectoryName(AppPaths.DatabasePath(DataDir))!;
        try
        {
            Directory.CreateDirectory(live);
            _watcher = new FileSystemWatcher(live, "screen-time.db*") { EnableRaisingEvents = true, NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName };
            _watcher.Changed += (_, _) => Touch();
            _watcher.Created += (_, _) => Touch();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Unwatchable: F5 still works.
        }
    }

    public void SetScope(Scope scope)
    {
        if (scope == Scope) return;
        Scope = scope;
        _settings.ScopeDays = scope.Days;
        _settings.Save();
        Version++;
        Changed?.Invoke();
    }

    public void SaveDevice(DeviceSettings device)
    {
        if (DataDir is null) return;
        device.Save(DataDir);
        Open();
        _ = ReloadAsync();
    }

    /// <summary>A write burst is many events; one reload a few seconds after the last.</summary>
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
                _debounce.Tick += (_, _) => _ = ReloadAsync(onlyIfChanged: true);
            }
            _debounce.Stop();
            _debounce.Start();
        });
    }

    private int _reloading;

    /// <summary>
    /// Re-reads the shared data. With <paramref name="onlyIfChanged"/>, a write
    /// that changed nothing a page shows (a checkpoint, an ingest that found
    /// nothing new) does not rebuild the page.
    /// </summary>
    public async Task ReloadAsync(bool onlyIfChanged = false)
    {
        if (Interlocked.Exchange(ref _reloading, 1) == 1) return;
        try
        {
            Sampler = SamplerStatus.Read(AppPaths.SamplerDir);
            var queries = Queries;
            if (queries is null || !queries.DatabaseExists)
            {
                Version++;
                Changed?.Invoke();
                CheckHealth();
                return;
            }
            var logos = Logos.Folder;
            var result = await Task.Run(() =>
            {
                var signature = Signature(queries.DatabasePath);
                var names = queries.AppNamesByKey();
                return (Signature: signature,
                    Colors: queries.ColorMap(),
                    Overrides: queries.ColorOverrideMap(),
                    Icons: AppIcons.Map(logos, names.Values.Select(v => (v.Name, v.Base))),
                    Sync: queries.Sync(10),
                    Latest: queries.Latest());
            });
            Sync = result.Sync;
            if (!(onlyIfChanged && result.Signature == _signature))
            {
                _signature = result.Signature;
                Colors = result.Colors;
                ColorOverrides = result.Overrides;
                Icons = result.Icons;
                Latest = result.Latest;
                Version++;
                Changed?.Invoke();
            }
            CheckHealth();
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException)
        {
            // Mid-write, or the drive is gone: the next change tries again.
        }
        finally
        {
            Interlocked.Exchange(ref _reloading, 0);
        }
    }

    /// <summary>What changes when anything a page shows changes.</summary>
    private static string Signature(string db)
    {
        using var conn = UsageDb.OpenRead(db);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT (SELECT COUNT(*) FROM windows_segments) || '|' ||
                   (SELECT COUNT(*) || ':' || COALESCE(MAX(updated_at), '') FROM app_renames) || '|' ||
                   (SELECT COUNT(*) || ':' || COALESCE(MAX(updated_at), '') FROM app_colours)
            """;
        return cmd.ExecuteScalar() as string ?? "";
    }

    /// <summary>
    /// The Sync button: fold the sampler's spans in now, rather than at its
    /// next quarter hour. Everything it reads is already on this disk, so it
    /// takes well under a second, and it is idempotent: a second press adds
    /// nothing and says "Up to date".
    /// </summary>
    public async Task<IngestResult> IngestNowAsync()
    {
        var result = await Task.Run(() => Ingest.Run(new IngestOptions { ForceBackup = true }));
        await ReloadAsync();
        return result;
    }

    /// <summary>
    /// One notification per failed ingest, and one per stopped sampler. A
    /// sampler that has stopped is the failure that matters most here: nothing
    /// backfills, so every minute it is down is a minute lost for good.
    /// </summary>
    private void CheckHealth()
    {
        if (!_settings.NotifyProblems) return;
        var newest = Sync?.Runs.FirstOrDefault();
        if (newest is { Status: "failed" } && newest.Id > _settings.NotifiedFailureId)
        {
            _settings.NotifiedFailureId = newest.Id;
            _settings.Save();
            Problem?.Invoke("Screen Time could not save its data", Trim(newest.Error ?? "The ingest reported a failure. Open Sync Status for details."));
            return;
        }
        // A heartbeat that never existed is a sampler not installed, not one
        // that stopped: the Sync page says so, without a notification.
        if (!Sampler.Alive && Sampler.Updated is { } since && Sampler.StaleSeconds > 300 && since != _settings.NotifiedStallSince)
        {
            _settings.NotifiedStallSince = since;
            _settings.Save();
            Problem?.Invoke("Screen Time has stopped recording", $"The sampler's last heartbeat was {Core.View.Format.DateTimeLocal(since)}. Time passing now is not being recorded. Open Sync Status.");
        }
    }

    /// <summary>The sampler can stop with no write at all, so health is also checked on a clock.</summary>
    public void CheckHealthNow() => _ = ReloadAsync(onlyIfChanged: true);

    private static string Trim(string s) => s.Length > 200 ? s[..197] + "..." : s;

    public void Dispose()
    {
        _watcher?.Dispose();
        Logos.Dispose();
    }
}
