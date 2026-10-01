using System.Text;
using ScreenTime.Core.Collect;
using ScreenTime.Core.Data;
using ScreenTime.Core.Query;
using ScreenTime.Core.Sample;
using Microsoft.Data.Sqlite;

namespace ScreenTime.Core.Tests;

public class IngestTests
{
    private const string Code = @"C:\Users\x\AppData\Local\Programs\Microsoft VS Code\Code.exe";
    private const string Chrome = @"C:\Program Files\Google\Chrome\Application\chrome.exe";

    [Fact]
    public void ASpanIsSplitAtLocalHourBoundaries()
    {
        var start = DateTime.Today.AddHours(9).AddMinutes(40).ToUniversalTime();
        var segments = Time.Segments(TestDb.Span(start, start.AddMinutes(90)));
        Assert.Equal(3, segments.Count);
        Assert.Equal([20 * 60_000L, 60 * 60_000L, 10 * 60_000L], segments.Select(s => s.DurationMs));
        Assert.Equal([9, 10, 11], segments.Select(s => s.LocalHour));
        // One session, however many rows.
        Assert.Single(segments.Select(s => s.SessionStartUtc).Distinct());
        Assert.Equal("2026-09-30T12:00:00.000Z", Time.Iso(new DateTime(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc)));
    }

    private static void WriteDay(string dir, DateTime localDay, params Span[] spans)
    {
        var lines = string.Concat(spans.Select(s => System.Text.Json.JsonSerializer.Serialize(s) + "\n"));
        File.AppendAllText(Sampler.DayFile(dir, localDay), lines, new UTF8Encoding(false));
    }

    [Fact]
    public void IngestIsIdempotentAndKeepsFilesStillBeingWritten()
    {
        using var db = new TestDb();
        var today = DateTime.Now;
        var old = today.AddDays(-3);
        WriteDay(db.SamplerDir, old, TestDb.Span(TestDb.Noon(3), TestDb.Noon(3).AddMinutes(5), app: Code));
        WriteDay(db.SamplerDir, today, TestDb.Span(TestDb.Noon(0), TestDb.Noon(0).AddMinutes(5), app: Chrome));
        File.AppendAllText(Sampler.DayFile(db.SamplerDir, today), "{\"start\":\"torn mid-wri");

        var options = new IngestOptions { SamplerDir = db.SamplerDir, DataDir = db.Dir, AllowSystemDrive = true, Keep = true };
        var first = Ingest.Run(options);
        Assert.Equal("success", first.Status);
        Assert.Equal(2, first.Read);
        Assert.Equal(1, first.Malformed);
        Assert.True(first.Inserted >= 2);

        var second = Ingest.Run(options);
        Assert.Equal(0, second.Inserted);
        Assert.Equal("Up to date - read 2 spans, nothing new.", second.Describe());

        var tidy = Ingest.Run(new IngestOptions { SamplerDir = db.SamplerDir, DataDir = db.Dir, AllowSystemDrive = true });
        Assert.Equal(1, tidy.Removed);
        Assert.False(File.Exists(Sampler.DayFile(db.SamplerDir, old)));
        Assert.True(File.Exists(Sampler.DayFile(db.SamplerDir, today)));

        using var read = UsageDb.OpenRead(db.DbPath);
        Assert.Equal(Time.Iso(TestDb.Noon(3)), UsageDb.GetMeta(read, "native_first_utc"));
        Assert.Equal(3L, UsageDb.Scalar(read, "SELECT COUNT(*) FROM sync_log WHERE status = 'success'"));
    }

    [Fact]
    public void ActiveTimeIsTheAppRowsAndGapDaysAreNotDaysWithData()
    {
        using var db = new TestDb();
        var d1 = TestDb.Noon(2);
        db.Insert(
            TestDb.Span(d1, d1.AddMinutes(30), app: Code),
            TestDb.Span(d1.AddMinutes(30), d1.AddMinutes(40), "locked"),
            TestDb.Span(d1.AddMinutes(40), d1.AddMinutes(50), app: Code),
            TestDb.Span(d1.AddMinutes(50), d1.AddMinutes(51), "unknown"),
            TestDb.Span(d1.AddMinutes(51), d1.AddMinutes(61), app: Chrome),
            // A whole day the laptop slept: gap only.
            TestDb.Span(TestDb.Noon(1), TestDb.Noon(1).AddHours(2), "gap"));
        var q = new UsageQueries(db.DbPath);
        var o = q.Overview(Scope.All);
        Assert.Equal(50 * 60_000L, o.RangeTotal);
        Assert.Equal(1, o.DaysWithData);
        Assert.Equal(o.RangeTotal, o.DailyAverage);
        Assert.Equal(10 * 60_000L, o.Kinds.Locked);
        Assert.Equal(60_000L, o.Kinds.Unknown);
        // The newest day WITH DATA anchors the range, not the gap day after it.
        Assert.Equal(Time.LocalBuckets(d1).Date, o.LatestDate);

        var apps = q.ByApp(Scope.All);
        Assert.Equal(["VS Code", "Google Chrome"], apps.Apps.Select(a => a.Name));
        // Two Code spans with the lock screen between them are two opens.
        Assert.Equal(2, apps.Apps[0].Opens);
        Assert.Equal(o.RangeTotal, apps.Apps.Sum(a => a.Ms));

        var detail = q.AppDetailFor("exe:code", Scope.All)!;
        Assert.Equal(40 * 60_000L, detail.Ms);
        Assert.Equal(2, detail.Opens);
        Assert.Equal(30 * 60_000L, detail.LongestMs);
        Assert.Equal([Code], detail.Identities.Select(i => i.Path));
        Assert.Null(q.AppDetailFor("exe:nope", Scope.All));
        Assert.True(q.AppExists("exe:chrome"));
    }

    [Fact]
    public void AltTabbingAwayAndBackQuicklyIsOneOpen()
    {
        using var db = new TestDb();
        var t = TestDb.Noon(1);
        db.Insert(
            TestDb.Span(t, t.AddMinutes(10), app: Code),
            TestDb.Span(t.AddMinutes(10), t.AddMinutes(10).AddSeconds(5), "unknown"),
            TestDb.Span(t.AddMinutes(10).AddSeconds(5), t.AddMinutes(20), app: Code));
        Assert.Equal(1, new UsageQueries(db.DbPath).ByApp(Scope.All).Apps[0].Opens);
    }

    [Fact]
    public void RenamesAndColoursReachTheColourMap()
    {
        using var db = new TestDb();
        db.Insert(TestDb.Span(TestDb.Noon(1), TestDb.Noon(1).AddHours(1), app: Code));
        var q = new UsageQueries(db.DbPath);
        Assert.Equal("#007acc", q.ColorMap()["VS Code"]);
        UsageDb.Exec(db.Conn, "INSERT INTO app_renames VALUES ('exe:code', 'Editor', 'x')");
        // A renamed app keeps its original's colour...
        Assert.Equal("#007acc", q.ColorMap()["Editor"]);
        UsageDb.Exec(db.Conn, "INSERT INTO app_colours VALUES ('exe:code', '#123456', 'x')");
        // ...until the user picks one.
        Assert.Equal("#123456", q.ColorMap()["Editor"]);
        Assert.Equal("Editor", q.DisplayName(Code));
    }

    [Fact]
    public void HeartbeatIsReadWithABomAndGoesStale()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ScreenTimeNative-hb-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            Assert.False(SamplerStatus.Read(dir).Alive);
            var now = DateTime.UtcNow;
            File.WriteAllText(Path.Combine(dir, Heartbeat.FileName),
                $"\uFEFF{{\"updated\":\"{Time.Iso(now)}\",\"interval_seconds\":2,\"closed\":false,\"in_flight\":{{\"start\":\"{Time.Iso(now)}\",\"kind\":\"app\",\"app\":\"x\",\"ms\":5}}}}",
                new UTF8Encoding(true));
            var live = SamplerStatus.Read(dir, now.AddSeconds(5));
            Assert.True(live.Alive);
            Assert.Equal("app", live.InFlight!.Kind);
            Assert.False(SamplerStatus.Read(dir, now.AddSeconds(90)).Alive);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void TheSamplerRecordsAndClosesItsHeartbeat()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ScreenTimeNative-sampler-" + Guid.NewGuid().ToString("N"));
        try
        {
            var code = new Sampler(new SamplerOptions { OutDir = dir, IntervalSeconds = 1, RunSeconds = 2, Ingest = false }).Run();
            Assert.Equal(0, code);
            var hb = Heartbeat.Read(dir)!;
            Assert.True(hb.Closed);
            Assert.True(File.Exists(Path.Combine(dir, Sampler.FirstStartFile)));
            var lines = File.ReadAllLines(Sampler.DayFile(dir, DateTime.Now));
            Assert.NotEmpty(lines);
            // Titles are never captured: a span holds kind, app and times only.
            Assert.DoesNotContain(lines, l => l.Contains("title", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void AnUnclosedRunIsRecoveredAsItsLastSpanAndAGap()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ScreenTimeNative-recover-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var then = DateTime.UtcNow.AddMinutes(-10);
            new Heartbeat
            {
                Updated = Time.Iso(then),
                Closed = false,
                InFlight = new InFlight { Start = Time.Iso(then.AddMinutes(-3)), Kind = "app", App = Code },
            }.Write(dir);
            new Sampler(new SamplerOptions { OutDir = dir, IntervalSeconds = 1, RunSeconds = 1, Ingest = false }).Run();
            var spans = File.ReadAllLines(Sampler.DayFile(dir, DateTime.Now)).Select(l => System.Text.Json.JsonSerializer.Deserialize<Span>(l)!).ToList();
            Assert.Equal("app", spans[0].Kind);
            Assert.Equal(Time.Iso(then), spans[0].End);
            Assert.Equal("gap", spans[1].Kind);
            Assert.Equal(Time.Iso(then), spans[1].Start);
            // The gap ends where the new run's first span begins.
            Assert.Equal(spans[1].End, spans[2].Start);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void TheWebImportStopsWhereThisSamplerBegan()
    {
        using var db = new TestDb();
        var web = Path.Combine(db.Dir, "web.db");
        using (var w = new SqliteConnection($"Data Source={web};Pooling=False"))
        {
            w.Open();
            UsageDb.Exec(w, """
                CREATE TABLE windows_segments (id INTEGER PRIMARY KEY AUTOINCREMENT, device_id TEXT, session_start_utc TEXT, start_utc TEXT, end_utc TEXT,
                  duration_ms INTEGER, local_date TEXT, local_hour INTEGER, kind TEXT, app_path TEXT, unresolved INTEGER, idle_ms_at_end INTEGER);
                CREATE TABLE sync_log (id INTEGER PRIMARY KEY AUTOINCREMENT, device_id TEXT, source TEXT, started_at TEXT, finished_at TEXT, status TEXT,
                  rows_read INTEGER, rows_inserted INTEGER, rows_skipped INTEGER, source_oldest_utc TEXT, source_newest_utc TEXT, backup_status TEXT, duration_ms INTEGER, error TEXT);
                CREATE TABLE app_renames (device_id TEXT, app_key TEXT, name TEXT, updated_at TEXT);
                CREATE TABLE app_colours (device_id TEXT, app_key TEXT, colour TEXT, updated_at TEXT);
                INSERT INTO windows_segments VALUES
                  (1, 'zephyrus', '2026-09-01T10:00:00.000Z', '2026-09-01T10:00:00.000Z', '2026-09-01T10:30:00.000Z', 1800000, '2026-09-01', 16, 'app', 'code', 0, 0),
                  (2, 'zephyrus', '2026-09-01T10:30:00.000Z', '2026-09-01T10:30:00.000Z', '2026-09-01T11:00:00.000Z', 1800000, '2026-09-01', 16, 'app', 'chrome', 0, 0),
                  (3, 'zephyrus', '2026-09-01T11:00:00.000Z', '2026-09-01T11:00:00.000Z', '2026-09-01T11:30:00.000Z', 1800000, '2026-09-01', 17, 'app', 'code', 0, 0),
                  (4, 'demo-phone', '2026-09-01T10:00:00.000Z', '2026-09-01T10:00:00.000Z', '2026-09-01T10:30:00.000Z', 1800000, '2026-09-01', 16, 'app', 'phone', 0, 0);
                INSERT INTO sync_log (device_id, source, started_at, status) VALUES ('zephyrus', 'win-sampler', '2026-09-01T09:00:00.000Z', 'success'), ('zephyrus', 'win-sampler', '2026-09-01T09:30:00.000Z', 'running'), ('demo-phone', 'android-events', '2026-09-01T09:00:00.000Z', 'success');
                INSERT INTO app_renames VALUES ('zephyrus', 'exe:code', 'Editor', '2026-09-01'), ('demo-phone', 'com.x', 'Phone App', '2026-09-01');
                """);
        }
        // This sampler's first span began at 10:45.
        UsageDb.SetMeta(db.Conn, "native_first_utc", "2026-09-01T10:45:00.000Z");

        var r = WebImport.Run(db.Conn, web, db.SamplerDir);
        Assert.Equal(2, r.Rows);
        Assert.Equal(2, r.RowsNew);
        Assert.Equal(1, r.Clipped);
        Assert.Equal(1, r.Runs);
        Assert.Equal(1, r.Renames);
        Assert.Equal(45 * 60_000L, UsageDb.Scalar(db.Conn, "SELECT SUM(duration_ms) FROM windows_segments"));
        Assert.Equal("2026-09-01T10:45:00.000Z", UsageDb.Scalar(db.Conn, "SELECT MAX(end_utc) FROM windows_segments"));
        Assert.Equal(0L, UsageDb.Scalar(db.Conn, "SELECT COUNT(*) FROM windows_segments WHERE app_path = 'phone'"));

        var again = WebImport.Run(db.Conn, web, db.SamplerDir);
        Assert.Equal(0, again.RowsNew);
        Assert.Equal(0, again.Runs);
    }
}
