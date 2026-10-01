using ScreenTime.Core.Collect;
using ScreenTime.Core.Data;
using ScreenTime.Core.Query;
using ScreenTime.Core.Sample;
using ScreenTime.Core.View;

namespace ScreenTime.App.Views;

/// <summary>
/// Stand-in data for the loading skeletons (<see cref="Skeleton"/>): a page is
/// built by its own <c>Build()</c> from these, then turned into a skeleton, so
/// every height in it comes from the real layout code.
/// </summary>
/// <remarks>
/// Shaped like a typical history - 100 days, the top eight apps and Other, a
/// page of runs - because what can differ from the real page is only what
/// depends on the data: how many rows a list has, whether a legend wraps.
/// Generic names only: nothing here is anyone's usage.
/// </remarks>
public static class Placeholder
{
    private const long Minute = 60_000, Hour = 60 * Minute;

    private static readonly string[] Apps =
        ["VS Code", "Google Chrome", "Windows Terminal", "Word", "File Explorer", "Firefox", "Discord", "Spotify"];

    private static string Day(int daysAgo) => DateOnly.FromDateTime(DateTime.Now).AddDays(-daysAgo).ToString("yyyy-MM-dd");

    private static List<DailyPoint> Daily(int days = 100) =>
        Enumerable.Range(0, days).Reverse().Select(i => new DailyPoint(Day(i), (3 + i * 7 % 6) * Hour)).ToList();

    private static List<HourPoint> Hourly() => Enumerable.Range(0, 24).Select(h => new HourPoint(h, (h % 7 + 1) * 10 * Minute)).ToList();

    public static List<(string Date, long Total)> Heat(int days = 100) =>
        Enumerable.Range(0, days).Select(i => (Day(i), (1 + i % 9) * Hour)).ToList();

    private static StackData Stack(long unit)
    {
        var names = Apps.Append("Other").ToList();
        var series = names.Select((n, i) => new StackSeries(n == "Other" ? null : n, (9 - i) * unit * 100)).ToList();
        var points = Daily().Select(d => new StackPoint(d.Date, true, names.Select((_, i) => (9 - i) * unit).ToArray())).ToList();
        return new StackData(series, names, points);
    }

    public static OverviewData Overview()
    {
        var daily = Daily();
        return new OverviewData(daily[^1].Date, 4 * Hour + 12 * Minute, 6 * Hour + 40 * Minute, 664 * Hour, daily.Count, 24,
            new KindTotals(664 * Hour, 310 * Hour, 2 * Hour, 1400 * Hour), daily, Hourly(), Stack(20 * Minute), Stack(4));
    }

    public static ByAppData ByApp()
    {
        var rows = Apps.Concat(["Notepad", "VLC", "Task Manager", "Windows Settings", "Snipping Tool", "Microsoft Store", "Setup (setup.exe)"])
            .Select((name, i) => new AppRow
            {
                Key = "placeholder-" + i,
                Name = name,
                BaseName = name,
                System = false,
                Ms = (16 - i) * 10 * Hour,
                Opens = 400 - i * 20,
                Days = 90,
                Share = 100.0 / (i + 2),
                Detailed = true,
                // The last is too small to list, as one usually is: that is
                // what puts the table's "Show all" footer under it.
                Listed = i < 14,
            }).ToList();
        return new ByAppData(rows, 100);
    }

    public static AppDetail App(string key) => new(
        key, "VS Code", "VS Code", false, 180 * Hour, 520, 94, 27.1, 12 * Minute, 3 * Hour,
        Daily(), Daily().Select(d => new DailyPoint(d.Date, 6)).ToList(), Hourly(), Hourly().Select(h => new HourPoint(h.Hour, 5)).ToList(),
        [new AppIdentity(@"C:\Users\you\AppData\Local\Programs\Microsoft VS Code\Code.exe", 180 * Hour)]);

    public static SyncData Sync()
    {
        var runs = Enumerable.Range(0, PageSizeRuns).Select(i =>
        {
            var at = Time.Iso(DateTime.UtcNow.AddMinutes(-15 * i));
            return new SyncRun(PageSizeRuns - i, at, at, "success", 30, 25, 5, i % 4 == 0 ? "ok" : null, 240, null);
        }).ToList();
        return new SyncData(runs, 400, runs[0], 0.1, 0, new KindTotals(664 * Hour, 310 * Hour, 2 * Hour, 1400 * Hour), 100, Day(99), Day(0));
    }

    private const int PageSizeRuns = 25;

    public static SamplerStatus Sampler() => new(true, Time.Iso(DateTime.UtcNow), 1, Time.Iso(DateTime.UtcNow.AddHours(-3)),
        new InFlight { Kind = "app", App = "Code.exe", Ms = 4 * Minute, Start = Time.Iso(DateTime.UtcNow) });

    public static TaskInfo Task() => new(true, true, DateTime.UtcNow.AddHours(-3), 0x41301, null);
}
