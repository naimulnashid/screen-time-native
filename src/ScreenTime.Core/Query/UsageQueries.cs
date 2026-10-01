using ScreenTime.Core.Data;
using ScreenTime.Core.Naming;
using ScreenTime.Core.View;
using Microsoft.Data.Sqlite;

namespace ScreenTime.Core.Query;

/// <summary>
/// Every read the app performs.
/// </summary>
/// <remarks>
/// <para><b>The rule that governs this file:</b> active time is
/// <c>SUM(duration_ms) WHERE kind = 'app'</c>. The sampler partitions tracked
/// time - exactly one span is open at any instant - so the app rows ARE the
/// total by construction, and the By App table sums to the Overview's figure.
/// <c>gap</c> and <c>locked</c> are never active time; <c>unknown</c> is never
/// folded into either.</para>
/// <para><b>A day holding only <c>gap</c> is not a day with data.</b> The
/// sampler writes a gap across every sleep and shutdown, so counting such days
/// would divide averages by days nobody used the machine and draw them as
/// recorded quiet days.</para>
/// <para><b>Ranges are anchored on the newest day with data</b>, not on today:
/// a laptop left off for a week shows its last real week, not seven empty
/// columns. Sync Status is what surfaces the staleness.</para>
/// <para>Names are resolved in code (<see cref="AppNames"/>), so grouping by
/// app happens here rather than in SQL.</para>
/// </remarks>
public sealed class UsageQueries(string databasePath, string? coloursPath = null)
{
    public string DatabasePath { get; } = databasePath;

    public bool DatabaseExists => File.Exists(DatabasePath);

    /// <summary>A detail page shows behaviour over time; under a minute in all is a stray focus event.</summary>
    public static bool EarnsDetailPage(long ms) => ms >= 60_000;

    private T With<T>(Func<SqliteConnection, T> fn)
    {
        using var db = UsageDb.OpenRead(DatabasePath);
        return fn(db);
    }

    private static List<T> Rows<T>(SqliteConnection db, string sql, Func<SqliteDataReader, T> map, params object?[] args)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        for (var i = 0; i < args.Length; i++) cmd.Parameters.AddWithValue($"$p{i}", args[i] ?? DBNull.Value);
        using var r = cmd.ExecuteReader();
        var list = new List<T>();
        while (r.Read()) list.Add(map(r));
        return list;
    }

    private static object? Scalar(SqliteConnection db, string sql, params object?[] args)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        for (var i = 0; i < args.Length; i++) cmd.Parameters.AddWithValue($"$p{i}", args[i] ?? DBNull.Value);
        var v = cmd.ExecuteScalar();
        return v is DBNull ? null : v;
    }

    private static long L(SqliteDataReader r, int i) => r.IsDBNull(i) ? 0 : r.GetInt64(i);

    private static string? S(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    /// <summary>A resolved app with the user's rename laid over it, and the name it had before.</summary>
    public sealed record Named(string Key, string Name, string BaseName, bool System);

    /// <summary>
    /// <see cref="AppNames.Resolve"/> with the user's renames applied. Every
    /// query that shows a name goes through this, so a rename reaches tables,
    /// charts, legends and the detail page from one place.
    /// </summary>
    private static Func<string, Named> Resolver(SqliteConnection db)
    {
        var renames = Renames.Read(db);
        var cache = new Dictionary<string, Named>();
        return path =>
        {
            if (cache.TryGetValue(path, out var hit)) return hit;
            var r = AppNames.Resolve(path);
            var named = new Named(r.Key, renames.GetValueOrDefault(r.Key) ?? r.Name, r.Name, r.System);
            cache[path] = named;
            return named;
        };
    }

    private static string? Newest(SqliteConnection db) => Scalar(db, "SELECT MAX(local_date) FROM windows_segments WHERE kind <> 'gap'") as string;

    private static string From(string newest, Scope scope) => Days.Add(newest, -(Math.Min(scope.Days, Scope.AllDays) - 1));

    /// <summary>Distinguishes an empty database from an empty range.</summary>
    public long RowCount() => With(db => (long)(Scalar(db, "SELECT COUNT(*) FROM windows_segments") ?? 0L));

    /// <summary>The newest day with data and its active time: the tray's figure.</summary>
    public (string? Date, long Ms) Latest() => With(db =>
    {
        var newest = Newest(db);
        return newest is null ? (null, 0L) : (newest, (long)(Scalar(db, "SELECT COALESCE(SUM(duration_ms), 0) FROM windows_segments WHERE kind = 'app' AND local_date = $p0", newest) ?? 0L));
    });

    private static KindTotals Kinds(SqliteConnection db, string from, string to)
    {
        long active = 0, locked = 0, unknown = 0, gap = 0;
        foreach (var (kind, ms) in Rows(db, "SELECT kind, SUM(duration_ms) FROM windows_segments WHERE local_date >= $p0 AND local_date <= $p1 GROUP BY kind", r => (r.GetString(0), L(r, 1)), from, to))
        {
            switch (kind)
            {
                case "app": active += ms; break;
                case "locked": locked += ms; break;
                case "gap": gap += ms; break;
                default: unknown += ms; break;
            }
        }
        return new KindTotals(active, locked, unknown, gap);
    }

    private static int DaysWithData(SqliteConnection db, string from, string to) =>
        (int)(long)(Scalar(db, "SELECT COUNT(DISTINCT local_date) FROM windows_segments WHERE kind <> 'gap' AND local_date >= $p0 AND local_date <= $p1", from, to) ?? 0L);

    /// <summary>
    /// Active time per day with data. A day holding locked or unknown time but
    /// no app time is still a RECORDED day, at zero - the sampler was running.
    /// </summary>
    private static List<DailyPoint> Daily(SqliteConnection db, string from, string to)
    {
        var rows = Rows(db, """
            SELECT local_date, SUM(CASE WHEN kind = 'app' THEN duration_ms ELSE 0 END)
              FROM windows_segments
             WHERE kind <> 'gap' AND local_date >= $p0 AND local_date <= $p1
             GROUP BY local_date
            """, r => (r.GetString(0), L(r, 1)), from, to);
        return Days.Fill(rows);
    }

    /// <summary>
    /// Every app span in the window, across EVERY app, keyed by resolved app -
    /// visits are stitched by global adjacency, which one app's rows alone
    /// cannot judge. A span cut at an hour edge is put back together here.
    /// </summary>
    private static List<RawSession> Sessions(SqliteConnection db, string from, string to, Func<string, Named> resolve) =>
        Rows(db, """
            SELECT app_path, session_start_utc, MAX(end_utc)
              FROM windows_segments
             WHERE kind = 'app' AND local_date >= $p0 AND local_date <= $p1
             GROUP BY app_path, session_start_utc
            """, r => (Path: r.GetString(0), S: r.GetString(1), E: r.GetString(2)), from, to)
            .Select(x => new RawSession(resolve(x.Path).Key, Epoch(x.S), Epoch(x.E)))
            .ToList();

    private static long Epoch(string iso) => Time.TryParse(iso, out var t) ? new DateTimeOffset(t).ToUnixTimeMilliseconds() : 0;

    /// <summary>Where each span began, read off its FIRST row: the stored local buckets, not recomputed.</summary>
    private static Dictionary<long, SessionBucket> SessionStarts(SqliteConnection db, string from, string to)
    {
        var map = new Dictionary<long, SessionBucket>();
        foreach (var (s, d, h) in Rows(db, """
            SELECT session_start_utc, local_date, local_hour FROM windows_segments
             WHERE kind = 'app' AND start_utc = session_start_utc AND local_date >= $p0 AND local_date <= $p1
            """, r => (r.GetString(0), r.GetString(1), r.GetInt32(2)), from, to))
            map[Epoch(s)] = new SessionBucket(d, h);
        return map;
    }

    // ---- Overview ---------------------------------------------------------------------------

    public OverviewData Overview(Scope scope) => With(db =>
    {
        var newest = Newest(db);
        if (newest is null) return new OverviewData(null, 0, 0, 0, 0, 0, KindTotals.Empty, [], [], StackData.Empty, StackData.Empty);
        var from = From(newest, scope);
        var resolve = Resolver(db);

        var kinds = Kinds(db, from, newest);
        var days = DaysWithData(db, from, newest);
        var latest = (long)(Scalar(db, "SELECT COALESCE(SUM(duration_ms), 0) FROM windows_segments WHERE kind = 'app' AND local_date = $p0", newest) ?? 0L);
        var daily = Daily(db, from, newest);

        var hourly = Rows(db, "SELECT local_hour, SUM(duration_ms) FROM windows_segments WHERE kind = 'app' AND local_date >= $p0 AND local_date <= $p1 GROUP BY local_hour",
            r => (r.GetInt32(0), L(r, 1)), from, newest).ToDictionary(x => x.Item1, x => x.Item2);

        // The two stacked charts, each ranked by its OWN measure: the apps you
        // spend longest in are not the ones you open most.
        var names = new Dictionary<string, string>();
        var time = new List<StackInput>();
        foreach (var (d, path, ms) in Rows(db, "SELECT local_date, app_path, SUM(duration_ms) FROM windows_segments WHERE kind = 'app' AND local_date >= $p0 AND local_date <= $p1 GROUP BY local_date, app_path",
                     r => (r.GetString(0), r.GetString(1), L(r, 2)), from, newest))
        {
            var n = resolve(path);
            names[n.Key] = n.Name;
            time.Add(new StackInput(d, n.Key, ms));
        }
        var visits = Visits.Stitch(Sessions(db, from, newest, resolve));
        var opens = Visits.OpensByDay(visits, SessionStarts(db, from, newest));
        var recorded = daily.Where(p => p.Ms is not null).Select(p => p.Date).ToList();

        StackData Stacked(IEnumerable<StackInput> rows)
        {
            var (series, points) = Stack.ByApp(rows, recorded);
            return new StackData(series, series.Select(s => s.Id is null ? "Other" : names.GetValueOrDefault(s.Id, s.Id)).ToList(), points);
        }

        return new OverviewData(newest, latest, days > 0 ? kinds.Active / days : 0, kinds.Active, days, names.Count, kinds,
            daily, Days.FillHours(hourly), Stacked(time), Stacked(opens));
    });

    /// <summary>Active time per day across all history, for the heat map: days with data only.</summary>
    public List<(string Date, long Total)> HeatmapDays() => With(db =>
        Rows(db, """
            SELECT local_date, SUM(CASE WHEN kind = 'app' THEN duration_ms ELSE 0 END)
              FROM windows_segments WHERE kind <> 'gap' GROUP BY local_date ORDER BY local_date
            """, r => (r.GetString(0), L(r, 1))));

    // ---- By App ------------------------------------------------------------------------------

    public ByAppData ByApp(Scope scope) => With(db =>
    {
        var newest = Newest(db);
        if (newest is null) return new ByAppData([], 0);
        var from = From(newest, scope);
        var resolve = Resolver(db);

        var apps = new Dictionary<string, AppRow>();
        var days = new Dictionary<string, HashSet<string>>();
        foreach (var (d, path, ms) in Rows(db, "SELECT local_date, app_path, SUM(duration_ms) FROM windows_segments WHERE kind = 'app' AND local_date >= $p0 AND local_date <= $p1 GROUP BY local_date, app_path",
                     r => (r.GetString(0), r.GetString(1), L(r, 2)), from, newest))
        {
            var n = resolve(path);
            if (!apps.TryGetValue(n.Key, out var row))
            {
                apps[n.Key] = row = new AppRow { Key = n.Key, Name = n.Name, BaseName = n.BaseName, System = n.System };
                days[n.Key] = [];
            }
            row.Ms += ms;
            days[n.Key].Add(d);
        }

        // Opens are VISITS: alt-tabbing away and back is three spans of one app.
        var opens = Visits.Counts(Visits.Stitch(Sessions(db, from, newest, resolve)));
        var total = apps.Values.Sum(a => a.Ms);
        foreach (var a in apps.Values)
        {
            a.Days = days[a.Key].Count;
            a.Opens = opens.GetValueOrDefault(a.Key);
            a.Share = total > 0 ? (double)a.Ms / total * 100 : 0;
            a.Detailed = EarnsDetailPage(a.Ms);
            a.Listed = AppList.IsListed(a.Ms, a.Opens, a.Days);
        }
        var list = apps.Values.Where(a => a.Ms > 0).OrderByDescending(a => a.Ms).ToList();
        // If nothing clears the bar, show everything: an empty table above a
        // button is worse than a table of small numbers.
        if (list.Count > 0 && list.All(a => !a.Listed)) foreach (var a in list) a.Listed = true;
        return new ByAppData(list, DaysWithData(db, from, newest));
    });

    // ---- App detail -----------------------------------------------------------------------------

    /// <summary>One app in the range, or null when it has no time in it (see <see cref="AppExists"/>).</summary>
    public AppDetail? AppDetailFor(string key, Scope scope) => With(db =>
    {
        var newest = Newest(db);
        if (newest is null) return null;
        var from = From(newest, scope);
        var resolve = Resolver(db);

        Named? app = null;
        long ms = 0, all = 0;
        var byDate = new Dictionary<string, long>();
        var byHour = new Dictionary<int, long>();
        var byPath = new Dictionary<string, long>();
        foreach (var (path, d, h, dur) in Rows(db, "SELECT app_path, local_date, local_hour, SUM(duration_ms) FROM windows_segments WHERE kind = 'app' AND local_date >= $p0 AND local_date <= $p1 GROUP BY app_path, local_date, local_hour",
                     r => (r.GetString(0), r.GetString(1), r.GetInt32(2), L(r, 3)), from, newest))
        {
            all += dur;
            var n = resolve(path);
            if (n.Key != key) continue;
            app = n;
            ms += dur;
            byDate[d] = byDate.GetValueOrDefault(d) + dur;
            byHour[h] = byHour.GetValueOrDefault(h) + dur;
            byPath[path] = byPath.GetValueOrDefault(path) + dur;
        }
        if (app is null || ms <= 0) return null;

        var visits = Visits.Stitch(Sessions(db, from, newest, resolve));
        var (count, median, longest) = Visits.Stats(visits, key);
        var (opensByDate, opensByHour) = Visits.OpenBuckets(visits, key, SessionStarts(db, from, newest));
        var dates = byDate.Keys.Order(StringComparer.Ordinal).ToList();

        return new AppDetail(app.Key, app.Name, app.BaseName, app.System, ms, count, dates.Count, all > 0 ? (double)ms / all * 100 : 0,
            median, longest,
            dates.Select(d => new DailyPoint(d, byDate[d])).ToList(),
            // Every day with TIME gets an opens column, even with no open of its
            // own: a day carried by a visit begun the night before is a real zero.
            dates.Select(d => new DailyPoint(d, opensByDate.GetValueOrDefault(d))).ToList(),
            Days.FillHours(byHour),
            Days.FillHours(opensByHour.ToDictionary(kv => kv.Key, kv => (long)kv.Value)),
            byPath.OrderByDescending(kv => kv.Value).Select(kv => new AppIdentity(kv.Key, kv.Value)).ToList());
    });

    /// <summary>Whether anything ever recorded resolves to this key, whatever the range.</summary>
    public bool AppExists(string key) => AppNamesByKey().ContainsKey(key);

    /// <summary>Every app ever seen: as shown, and as it would be without a rename.</summary>
    public Dictionary<string, (string Name, string Base)> AppNamesByKey() => With(db =>
    {
        var resolve = Resolver(db);
        var map = new Dictionary<string, (string, string)>();
        foreach (var path in Rows(db, "SELECT DISTINCT app_path FROM windows_segments WHERE kind = 'app'", r => r.GetString(0)))
        {
            var n = resolve(path);
            map.TryAdd(n.Key, (n.Name, n.BaseName));
        }
        return map;
    });

    /// <summary>The name a raw identity shows, rename applied - the Sync page's "currently in".</summary>
    public string DisplayName(string path)
    {
        var r = AppNames.Resolve(path);
        if (!DatabaseExists) return r.Name;
        try { return With(db => Renames.Read(db).GetValueOrDefault(r.Key)) ?? r.Name; }
        catch (SqliteException) { return r.Name; }
    }

    // ---- Colours --------------------------------------------------------------------------------

    /// <summary>
    /// Display name to colour. Brands first, then a palette by ALL-TIME rank,
    /// so an app keeps its colour across ranges and pages. A renamed app keeps
    /// its original's colour; the user's own colour wins over everything.
    /// </summary>
    public Dictionary<string, string> ColorMap() => With(db =>
    {
        var resolve = Resolver(db);
        var totals = new Dictionary<string, long>();
        var renamed = new Dictionary<string, string>();
        var shownAs = new Dictionary<string, string>();
        foreach (var (path, ms) in Rows(db, "SELECT app_path, SUM(duration_ms) FROM windows_segments WHERE kind = 'app' GROUP BY app_path", r => (r.GetString(0), L(r, 1))))
        {
            var n = resolve(path);
            totals[n.BaseName] = totals.GetValueOrDefault(n.BaseName) + ms;
            if (n.Name != n.BaseName) renamed[n.Name] = n.BaseName;
            shownAs[n.Key] = n.Name;
        }
        var local = AppColors.ReadLocal(coloursPath);
        var map = AppColors.Assign(totals.OrderByDescending(kv => kv.Value).Select(kv => kv.Key), local);
        foreach (var (name, @base) in renamed)
            map[name] = AppColors.Brand(name, local) ?? map.GetValueOrDefault(@base, AppColors.Other);
        foreach (var (key, color) in ColorOverrides.Read(db))
            if (shownAs.TryGetValue(key, out var name)) map[name] = color;
        return map;
    });

    /// <summary>App key to the colour the user chose for it.</summary>
    public Dictionary<string, string> ColorOverrideMap() => With(ColorOverrides.Read);

    // ---- Sync Status -------------------------------------------------------------------------

    public SyncData Sync(int limit = 25, int offset = 0) => With(db =>
    {
        static SyncRun Run(SqliteDataReader r) => new(r.GetInt64(0), r.GetString(1), S(r, 2), r.GetString(3), L(r, 4), L(r, 5), L(r, 6), S(r, 7), r.IsDBNull(8) ? null : r.GetInt64(8), S(r, 9));
        const string Columns = "id, started_at, finished_at, status, rows_read, rows_inserted, rows_skipped, backup_status, duration_ms, error";

        var runs = Rows(db, $"SELECT {Columns} FROM sync_log ORDER BY id DESC LIMIT $p0 OFFSET $p1", Run, limit, offset);
        var total = (long)(Scalar(db, "SELECT COUNT(*) FROM sync_log") ?? 0L);
        var last = Rows(db, $"SELECT {Columns} FROM sync_log WHERE status = 'success' ORDER BY id DESC LIMIT 1", Run).FirstOrDefault();
        double? hours = last?.FinishedAt is { } f && Time.TryParse(f, out var t) ? Math.Max(0, (DateTime.UtcNow - t).TotalHours) : null;
        var failures = (long)(Scalar(db, "SELECT COUNT(*) FROM sync_log WHERE status = 'failed' AND id > COALESCE((SELECT MAX(id) FROM sync_log WHERE status = 'success'), 0)") ?? 0L);

        var span = Rows(db, "SELECT MIN(local_date), MAX(local_date) FROM windows_segments WHERE kind <> 'gap'", r => (S(r, 0), S(r, 1))).First();
        var stored = span.Item1 is null ? KindTotals.Empty : Kinds(db, "0000-00-00", "9999-99-99");
        var days = span.Item1 is null ? 0 : DaysWithData(db, span.Item1, span.Item2!);
        return new SyncData(runs, total, last, hours, failures, stored, days, span.Item1, span.Item2);
    });
}
