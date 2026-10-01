namespace ScreenTime.Core.View;

/// <summary>One recorded span of an app, as epoch milliseconds.</summary>
public readonly record struct RawSession(string App, long Start, long End);

/// <summary>Consecutive spans of one app, stitched. <see cref="Ms"/> is their SUM.</summary>
public sealed class Visit
{
    public required string App { get; init; }
    public long Start { get; init; }
    public long End { get; set; }
    public long Ms { get; set; }
    public int Parts { get; set; }
}

/// <summary>Where an open happened: the local day and hour its visit began.</summary>
public readonly record struct SessionBucket(string Date, int Hour);

/// <summary>
/// Turning raw spans into VISITS - what a person means by opening an app.
/// </summary>
/// <remarks>
/// <para>The sampler ends a span on every focus change, so alt-tabbing away to
/// check something and back is three spans of one app. Consecutive spans of
/// one app are stitched when nothing else held the foreground in between and
/// the gap is under <see cref="GapMs"/>.</para>
/// <para>Two rules keep it honest. <b>Nothing else in between</b>: adjacency is
/// judged against the GLOBAL order across every app, so leaving and coming
/// back after using something else stays two visits however short the gap.
/// <b>Duration is summed, never spanned</b>: spanning would invent the gap as
/// usage.</para>
/// </remarks>
public static class Visits
{
    /// <summary>A judgement, not a measurement - hence one named constant.</summary>
    public const long GapMs = 30_000;

    /// <summary><paramref name="sessions"/> must hold EVERY app's spans in the window.</summary>
    public static List<Visit> Stitch(IEnumerable<RawSession> sessions, long gapMs = GapMs)
    {
        var sorted = sessions.OrderBy(s => s.Start).ThenBy(s => s.End).ToList();
        var output = new List<Visit>();
        Visit? current = null;
        foreach (var s in sorted)
        {
            if (current is not null && current.App == s.App && s.Start - current.End <= gapMs)
            {
                current.End = Math.Max(current.End, s.End);
                current.Ms += s.End - s.Start;
                current.Parts++;
                continue;
            }
            if (current is not null) output.Add(current);
            current = new Visit { App = s.App, Start = s.Start, End = s.End, Ms = s.End - s.Start, Parts = 1 };
        }
        if (current is not null) output.Add(current);
        return output;
    }

    /// <summary>
    /// How many visits, the median (not the mean: one three-hour sitting drags a
    /// mean where no visit ever was) and the longest, for one app.
    /// </summary>
    public static (int Visits, long MedianMs, long LongestMs) Stats(IEnumerable<Visit> visits, string app)
    {
        var mine = visits.Where(v => v.App == app).Select(v => v.Ms).Order().ToList();
        return mine.Count == 0 ? (0, 0, 0) : (mine.Count, mine[mine.Count / 2], mine[^1]);
    }

    public static Dictionary<string, int> Counts(IEnumerable<Visit> visits)
    {
        var counts = new Dictionary<string, int>();
        foreach (var v in visits) counts[v.App] = counts.GetValueOrDefault(v.App) + 1;
        return counts;
    }

    /// <summary>
    /// One app's opens by local day and hour. AN OPEN IS AN INSTANT: filed under
    /// the day and hour its visit BEGAN, unlike time, which is split across a
    /// boundary because it really was spent on both sides. A visit whose start
    /// is not in <paramref name="bucketOf"/> began before the range and is
    /// dropped rather than filed under a day it was not opened on.
    /// </summary>
    public static (Dictionary<string, int> ByDate, Dictionary<int, int> ByHour) OpenBuckets(IEnumerable<Visit> visits, string app, IReadOnlyDictionary<long, SessionBucket> bucketOf)
    {
        var byDate = new Dictionary<string, int>();
        var byHour = new Dictionary<int, int>();
        foreach (var v in visits)
        {
            if (v.App != app || !bucketOf.TryGetValue(v.Start, out var at)) continue;
            byDate[at.Date] = byDate.GetValueOrDefault(at.Date) + 1;
            byHour[at.Hour] = byHour.GetValueOrDefault(at.Hour) + 1;
        }
        return (byDate, byHour);
    }

    /// <summary>Opens per local day for every app at once, by the same rule.</summary>
    public static List<StackInput> OpensByDay(IEnumerable<Visit> visits, IReadOnlyDictionary<long, SessionBucket> bucketOf)
    {
        var counts = new Dictionary<(string Date, string App), long>();
        foreach (var v in visits)
        {
            if (!bucketOf.TryGetValue(v.Start, out var at)) continue;
            counts[(at.Date, v.App)] = counts.GetValueOrDefault((at.Date, v.App)) + 1;
        }
        return counts.Select(kv => new StackInput(kv.Key.Date, kv.Key.App, kv.Value)).ToList();
    }
}
