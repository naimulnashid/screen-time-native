using System.Globalization;

namespace ScreenTime.Core.View;

/// <summary>One day of a daily series: null on a day nothing was recorded.</summary>
public sealed record DailyPoint(string Date, long? Ms);

/// <summary>One hour of the day, summed over a range.</summary>
public sealed record HourPoint(int Hour, long Value);

/// <summary>
/// Calendar days, and which of them hold a recording.
/// </summary>
/// <remarks>
/// A day the sampler recorded nothing on (it was off, or the laptop was) is
/// NOT a quiet day, and is never counted as one: averages divide by days with
/// data. On the trend it is drawn at zero - the owner's call on 2026-09-23,
/// one unbroken line - while the tooltip says "Not recorded", and the null is
/// kept in the data so nothing downstream mistakes it for a zero.
/// </remarks>
public static class Days
{
    public static DateOnly Parse(string iso) => DateOnly.ParseExact(iso, "yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static string Add(string date, int n) => Iso(Parse(date).AddDays(n));

    /// <summary>Every date from <paramref name="from"/> to <paramref name="to"/>, both included.</summary>
    public static IEnumerable<string> Each(string from, string to)
    {
        if (from.Length == 0 || string.CompareOrdinal(from, to) > 0) yield break;
        for (var d = Parse(from); d <= Parse(to); d = d.AddDays(1)) yield return Iso(d);
    }

    /// <summary>One point per day from the first row to the last; a day without a row is null.</summary>
    public static List<DailyPoint> Fill(IEnumerable<(string Date, long Ms)> rows)
    {
        var byDate = rows.ToDictionary(r => r.Date, r => r.Ms);
        if (byDate.Count == 0) return [];
        var first = byDate.Keys.Min(StringComparer.Ordinal)!;
        var last = byDate.Keys.Max(StringComparer.Ordinal)!;
        return Each(first, last).Select(d => new DailyPoint(d, byDate.TryGetValue(d, out var v) ? v : null)).ToList();
    }

    /// <summary>All 24 hours in order; an hour with no rows is zero, never missing.</summary>
    public static List<HourPoint> FillHours(IReadOnlyDictionary<int, long> byHour) =>
        Enumerable.Range(0, 24).Select(h => new HourPoint(h, byHour.GetValueOrDefault(h))).ToList();
}
