using ScreenTime.Core.View;

namespace ScreenTime.Core.Query;

/// <summary>The range the page shows, counted back from the newest day with data.</summary>
public readonly record struct Scope(int Days)
{
    /// <summary>
    /// "All": a century rather than a sentinel, so every query keeps the same
    /// <c>local_date &gt;= ?</c> comparison and there is no special case to forget.
    /// </summary>
    public const int AllDays = 36500;

    public static readonly int[] Ranges = [7, 30, 90, AllDays];

    public static Scope All => new(AllDays);

    public bool IsAll => Days >= AllDays;

    public string Label => IsAll ? "All" : $"{Days}d";
}

/// <summary>
/// Where the recorded time went. Together these are every millisecond the
/// sampler accounted for: the kinds partition it.
/// </summary>
public sealed record KindTotals(long Active, long Locked, long Unknown, long Gap)
{
    public static readonly KindTotals Empty = new(0, 0, 0, 0);

    public long Tracked => Active + Locked + Unknown;

    /// <summary>How much of the tracked time the sampler declined to attribute, 0-100.</summary>
    public double UnknownShare => Tracked > 0 ? (double)Unknown / Tracked * 100 : 0;
}

/// <summary>
/// A stacked chart: its bands (an app's key, or null for Other), each band's
/// display name in the same order, and the days.
/// </summary>
public sealed record StackData(IReadOnlyList<StackSeries> Series, IReadOnlyList<string> Names, IReadOnlyList<StackPoint> Points)
{
    public static readonly StackData Empty = new([], [], []);
}

public sealed record OverviewData(
    string? LatestDate,
    long Latest,
    long DailyAverage,
    long RangeTotal,
    int DaysWithData,
    int AppCount,
    KindTotals Kinds,
    IReadOnlyList<DailyPoint> Daily,
    IReadOnlyList<HourPoint> Hourly,
    StackData TimeByApp,
    StackData OpensByApp);

public sealed class AppRow
{
    public required string Key { get; init; }
    public required string Name { get; init; }
    /// <summary>The name without the user's rename.</summary>
    public required string BaseName { get; init; }
    public bool System { get; init; }
    public long Ms { get; set; }
    /// <summary>Visits, not raw spans: see <see cref="Visits"/>.</summary>
    public int Opens { get; set; }
    public int Days { get; set; }
    public double Share { get; set; }
    /// <summary>Has a detail page: a minute or more in the range.</summary>
    public bool Detailed { get; set; }
    /// <summary>Shown before "Show all": see <see cref="AppList"/>.</summary>
    public bool Listed { get; set; }
}

public sealed record ByAppData(IReadOnlyList<AppRow> Apps, int DaysWithData);

public sealed record AppIdentity(string Path, long Ms);

public sealed record AppDetail(
    string Key,
    string Name,
    string BaseName,
    bool System,
    long Ms,
    int Opens,
    int Days,
    double Share,
    long MedianMs,
    long LongestMs,
    IReadOnlyList<DailyPoint> Daily,
    IReadOnlyList<DailyPoint> DailyOpens,
    IReadOnlyList<HourPoint> Hourly,
    IReadOnlyList<HourPoint> HourlyOpens,
    IReadOnlyList<AppIdentity> Identities);

public sealed record SyncRun(
    long Id,
    string StartedAt,
    string? FinishedAt,
    string Status,
    long RowsRead,
    long RowsInserted,
    long RowsSkipped,
    string? BackupStatus,
    long? DurationMs,
    string? Error);

public sealed record SyncData(
    IReadOnlyList<SyncRun> Runs,
    long TotalRuns,
    SyncRun? LastSuccess,
    double? HoursSinceSuccess,
    long ConsecutiveFailures,
    KindTotals Stored,
    int DaysWithData,
    string? FirstDate,
    string? LatestDate);
