namespace ScreenTime.Core.View;

/// <summary>One app's value on one day: milliseconds, or a count of opens.</summary>
public sealed record StackInput(string Date, string Id, long Value);

/// <summary>A band of a stacked chart: an app's key, or null for Other.</summary>
public sealed record StackSeries(string? Id, long Total);

/// <summary>One day: a value per series, in series order. <see cref="Recorded"/> is false on a day nothing was recorded.</summary>
public sealed record StackPoint(string Date, bool Recorded, long[] Values);

/// <summary>
/// The Overview's stacked "by day" charts: the top apps by their own measure,
/// everything else folded into Other.
/// </summary>
public static class Stack
{
    /// <summary>
    /// The top <paramref name="topN"/> by total over the range, plus Other only
    /// when there IS an everything else. Days run from the first to the last in
    /// <paramref name="recorded"/> or the rows; a day in neither is not recorded.
    /// </summary>
    public static (List<StackSeries> Series, List<StackPoint> Points) ByApp(IEnumerable<StackInput> rows, IEnumerable<string> recorded, int topN = 8)
    {
        var totals = new Dictionary<string, long>();
        var perDay = new Dictionary<string, Dictionary<string, long>>();
        foreach (var r in rows)
        {
            if (r.Value <= 0) continue;
            totals[r.Id] = totals.GetValueOrDefault(r.Id) + r.Value;
            if (!perDay.TryGetValue(r.Date, out var day)) perDay[r.Date] = day = [];
            day[r.Id] = day.GetValueOrDefault(r.Id) + r.Value;
        }

        var ranked = totals.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).ToList();
        var series = ranked.Take(topN).Select(kv => new StackSeries(kv.Key, kv.Value)).ToList();
        var rest = ranked.Skip(topN).Sum(kv => kv.Value);
        if (ranked.Count > topN) series.Add(new StackSeries(null, rest));
        var index = series.Select((s, i) => (s.Id, i)).Where(x => x.Id is not null).ToDictionary(x => x.Id!, x => x.i);
        var other = ranked.Count > topN ? series.Count - 1 : -1;

        var recordedSet = recorded.ToHashSet();
        var days = recordedSet.Concat(perDay.Keys).Distinct().Order(StringComparer.Ordinal).ToList();
        var points = new List<StackPoint>();
        if (days.Count == 0) return (series, points);
        foreach (var date in Days.Each(days[0], days[^1]))
        {
            var values = new long[series.Count];
            if (perDay.TryGetValue(date, out var day))
                foreach (var (id, value) in day)
                {
                    var i = index.TryGetValue(id, out var at) ? at : other;
                    if (i >= 0) values[i] += value;
                }
            points.Add(new StackPoint(date, recordedSet.Contains(date) || perDay.ContainsKey(date), values));
        }
        return (series, points);
    }

    /// <summary>
    /// The first item with the strictly greatest POSITIVE value, for a card's
    /// callout. Null when every value is zero: "busiest hour 12 AM, 0 opens"
    /// would be the first bucket winning a tie, not a finding.
    /// </summary>
    public static T? PeakOf<T>(IEnumerable<T> items, Func<T, long> value) where T : class
    {
        T? best = null;
        long bestValue = 0;
        foreach (var item in items)
        {
            var v = value(item);
            if (v > bestValue)
            {
                best = item;
                bestValue = v;
            }
        }
        return best;
    }
}

/// <summary>
/// Which apps the By App table lists before "Show all" is pressed. An app is
/// listed with at least 10 minutes in the range, OR 30+ opens spread over 5+
/// days - time alone is enough, and the days floor keeps one afternoon of
/// alt-tabbing through a dialog from counting as a habit. Thresholds are
/// absolute, so "listed" means the same on 7 days as on All.
/// </summary>
public static class AppList
{
    public const long MinMs = 10 * 60_000;
    public const int MinOpens = 30;
    public const int MinDays = 5;

    public static bool IsListed(long ms, int opens, int days) => ms >= MinMs || (opens >= MinOpens && days >= MinDays);

    public static string Rule => $"Listed: {MinMs / 60_000} min or more, or {MinOpens}+ opens across {MinDays}+ days.";
}
