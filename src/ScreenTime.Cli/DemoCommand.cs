using System.Globalization;
using ScreenTime.Core.Data;

namespace ScreenTime.Cli;

/// <summary>
/// An invented history for screenshots: an invented laptop ("My Laptop"),
/// generic apps, made-up days. A screenshot of the real app is a screenshot of
/// somebody's day.
/// </summary>
/// <remarks>
/// Not a mock: spans go through the real hour split and the real insert
/// (<see cref="Time.Segments"/>, <see cref="UsageDb.InsertSegments"/>), and
/// runs through the real run log. It reproduces what the pages must handle:
/// sleep as <c>gap</c>, the lock screen between work blocks, a sliver of
/// <c>unknown</c>, a store app under a versioned WindowsApps folder, a week
/// the sampler was not running, and one failed ingest. Deterministic for a
/// given day; <c>SCREENTIME_DEMO_NOW</c> pins the clock, so a capture just
/// after midnight does not draw a "today" four minutes long.
/// </remarks>
public static class DemoCommand
{
    private const string Marker = ".screen-time-demo";
    private const int HistoryDays = 120;
    private const double Minute = 60_000, Hour = 60 * Minute;

    private const string Pf = @"C:\Program Files";

    private static readonly (string Path, double Weight)[] Apps =
    [
        (@"C:\Users\demo\AppData\Local\Programs\Microsoft VS Code\Code.exe", 30),
        ($@"{Pf}\Google\Chrome\Application\chrome.exe", 24),
        ($@"{Pf}\WindowsApps\Microsoft.WindowsTerminal_1.21.2361.0_x64__8wekyb3d8bbwe\WindowsTerminal.exe", 12),
        (@"C:\Users\demo\AppData\Roaming\Spotify\Spotify.exe", 6),
        (@"C:\Windows\explorer.exe", 6),
        (@"C:\Users\demo\AppData\Local\Discord\app-1.0.9170\Discord.exe", 5),
        ($@"{Pf}\Microsoft Office\root\Office16\WINWORD.EXE", 5),
        ($@"{Pf}\Mozilla Firefox\firefox.exe", 4),
        (@"C:\Windows\System32\notepad.exe", 3),
        ($@"{Pf}\VideoLAN\VLC\vlc.exe", 3),
        ($@"{Pf}\WindowsApps\Microsoft.WindowsTerminal_1.20.11781.0_x64__8wekyb3d8bbwe\WindowsTerminal.exe", 1),
        (@"C:\Windows\System32\Taskmgr.exe", 0.6),
        (@"C:\Windows\ImmersiveControlPanel\SystemSettings.exe", 0.5),
    ];

    private static uint _seed;

    // mulberry32: a fixed history, not a new one per run.
    private static double Rand()
    {
        unchecked
        {
            _seed += 0x6d2b79f5;
            var t = _seed;
            t = (t ^ (t >> 15)) * (1 | t);
            t = (t + ((t ^ (t >> 7)) * (61 | t))) ^ t;
            return (t ^ (t >> 14)) / 4294967296.0;
        }
    }

    private static double Between(double lo, double hi) => lo + Rand() * (hi - lo);

    private static string Pick()
    {
        var total = Apps.Sum(a => a.Weight);
        var r = Rand() * total;
        foreach (var (path, weight) in Apps)
            if ((r -= weight) <= 0) return path;
        return Apps[^1].Path;
    }

    public static int Run(string[] args)
    {
        var outDir = Path.GetFullPath(args.Length > 0 ? args[0] : "demo-data");
        if (Directory.Exists(outDir))
        {
            if (Directory.EnumerateFileSystemEntries(outDir).Any() && !File.Exists(Path.Combine(outDir, Marker)))
                throw new InvalidOperationException($"{outDir} is not empty and was not made by demo-data (no {Marker}). Refusing to clear it.");
            Directory.Delete(outDir, recursive: true);
        }
        Directory.CreateDirectory(outDir);
        File.WriteAllText(Path.Combine(outDir, Marker), "Synthetic demo data from `screentime demo-data`. Safe to delete.\n");

        _seed = 20261001;
        var now = Environment.GetEnvironmentVariable("SCREENTIME_DEMO_NOW") is { Length: > 0 } pinned
            ? DateTime.Parse(pinned, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal)
            : DateTime.Now;

        var spans = new List<Span>();
        for (var n = HistoryDays - 1; n >= 0; n--)
        {
            var start = now.Date.AddDays(-n);
            // A week the sampler was not running: no rows at all, so the pages
            // show an unrecorded stretch rather than seven quiet days.
            if (n is >= 40 and < 47) continue;
            spans.AddRange(Day(start, start.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday, now));
        }

        using var db = UsageDb.Open(AppPaths(outDir), allowSystemDrive: true);
        var segments = spans.SelectMany(Time.Segments).ToList();
        var inserted = UsageDb.InsertSegments(db, segments);

        // A run history on the sampler's real cadence, a quarter of an hour
        // apart, on the REAL clock so the page reads "collected minutes ago".
        var runNow = DateTime.UtcNow;
        for (var i = 39; i >= 0; i--)
        {
            var at = runNow.AddMinutes(-(i * 15 + Between(1, 4)));
            var took = (long)Between(60, 400);
            var failed = i == 23;
            var id = UsageDb.StartRun(db, Time.Iso(at));
            var stored = failed ? 0 : (long)Between(4, 40);
            UsageDb.FinishRun(db, id, new SyncResult
            {
                Status = failed ? "failed" : "success",
                RowsRead = failed ? 0 : stored + (long)Between(0, 6),
                RowsInserted = stored,
                RowsSkipped = failed ? 0 : (long)Between(0, 6),
                BackupStatus = failed ? null : i % 4 == 0 ? "ok" : null,
                DurationMs = took,
                Error = failed ? "database is locked" : null,
            }, Time.Iso(at.AddMilliseconds(took)));
        }
        UsageDb.Checkpoint(db);

        new Core.DeviceSettings { DeviceLabel = "My Laptop" }.Save(outDir);
        Console.WriteLine($"demo written to {outDir}");
        Console.WriteLine($"  {spans.Count} spans, {inserted} hour-segments over {HistoryDays} days");
        Console.WriteLine($"  point the app at it:  $env:SCREENTIME_DATA_DIR = '{outDir}'; $env:SCREENTIME_LOCAL_DIR = '{Path.Combine(outDir, "local")}'");
        return 0;
    }

    private static string AppPaths(string dataDir) => Core.AppPaths.DatabasePath(dataDir);

    /// <summary>Work blocks on weekdays, a shorter afternoon and evening at weekends; locked between, asleep around.</summary>
    private static List<Span> Day(DateTime start, bool weekend, DateTime now)
    {
        var spans = new List<Span>();
        void Push(DateTime from, DateTime to, string kind, string app = "")
        {
            if (to > now) to = now;
            if (to <= from) return;
            spans.Add(new Span
            {
                Start = Time.Iso(from.ToUniversalTime()),
                End = Time.Iso(to.ToUniversalTime()),
                Ms = (long)(to - from).TotalMilliseconds,
                Kind = kind,
                App = app,
                IdleMs = kind == "app" ? (long)Between(0, 40_000) : 0,
            });
        }

        (double, double)[] blocks = weekend
            ? [(Between(11, 13), Between(14, 16)), (Between(20, 21), Between(22, 23.5))]
            : [(Between(8.5, 9.5), Between(12, 12.8)), (Between(13.3, 14), Between(17.5, 18.8)), (Between(20.5, 21.5), Between(22, 23.2))];
        var last = start;
        foreach (var (from, to) in blocks)
        {
            var a = start.AddMilliseconds(from * Hour);
            var b = start.AddMilliseconds(to * Hour);
            Push(last, a, last == start ? "gap" : "locked");
            var t = a;
            while (t < b)
            {
                // Now and then nothing holds focus for a moment: 'unknown'. The
                // spans still butt end to end - the sampler partitions time.
                var unknown = Rand() < 0.04;
                var next = unknown ? t.AddSeconds(Between(4, 40)) : t.AddMilliseconds(Between(2, weekend ? 45 : 35) * Minute);
                if (next > b) next = b;
                if (unknown) Push(t, next, "unknown");
                else Push(t, next, "app", Pick());
                t = next;
            }
            last = b;
        }
        Push(last, start.AddDays(1), "gap");
        return spans;
    }
}
