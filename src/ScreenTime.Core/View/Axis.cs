namespace ScreenTime.Core.View;

/// <summary>What a chart's value axis counts.</summary>
public enum Measure
{
    /// <summary>Milliseconds of foreground time.</summary>
    Time,

    /// <summary>A whole number: opens.</summary>
    Count,
}

/// <summary>
/// Value axes that END AT THE DATA. A chart picking a "nice" ceiling from a
/// fixed tick count overshoots to reach it - measured on the web dashboard,
/// a peak of 11h 5m drew an axis to 17h, half as much empty space again as
/// data. So the STEP is chosen first, from steps that mean something to a
/// person, and the ceiling falls out of it.
/// </summary>
public static class Axis
{
    private const long Minute = 60_000, Hour = 3_600_000;

    private static readonly long[] TimeSteps =
    [
        Minute, 2 * Minute, 5 * Minute, 10 * Minute, 15 * Minute, 30 * Minute,
        Hour, 2 * Hour, 3 * Hour, 4 * Hour, 6 * Hour, 12 * Hour, 24 * Hour,
        // Range totals run to hundreds of hours on All, and a fallback of
        // "peak over six" drew 47h / 94h / 141h. Whole days of hours instead.
        48 * Hour, 72 * Hour, 96 * Hour, 120 * Hour, 240 * Hour, 480 * Hour, 960 * Hour,
    ];

    /// <summary>Decimal 1/2/5 rungs, plus 25 and 250, where counts land.</summary>
    private static readonly long[] CountSteps = [1, 2, 5, 10, 20, 25, 50, 100, 200, 250, 500, 1000];

    private const int MaxIntervals = 6;

    /// <summary>The ticks from 0 to the ceiling; the last is the axis top.</summary>
    public static List<long> Ticks(double peak, Measure measure)
    {
        if (!(peak > 0)) return measure == Measure.Time ? [0, Minute] : [0, 1];
        var steps = measure == Measure.Time ? TimeSteps : CountSteps;
        var unit = measure == Measure.Time ? Hour : 1000;
        var step = steps.FirstOrDefault(s => peak / s <= MaxIntervals);
        // Past the ladder (40 days in one bar), whole hours or thousands.
        if (step == 0) step = (long)Math.Ceiling(peak / MaxIntervals / unit) * unit;
        var max = (long)Math.Ceiling(peak / step) * step;
        var ticks = new List<long>();
        for (var t = 0L; t <= max; t += step) ticks.Add(t);
        return ticks;
    }

    /// <summary>
    /// A tick label, exact rather than rounded: a 30-minute step must not print
    /// 1h30 as "2h", one tick below the real 2h with the same label.
    /// </summary>
    public static string TimeTick(long ms)
    {
        if (ms == 0) return "0";
        if (ms < Hour) return $"{Math.Round(ms / (double)Minute)}m";
        var h = ms / Hour;
        var m = (long)Math.Round(ms % Hour / (double)Minute);
        return m == 0 ? $"{h}h" : $"{h}h{m}";
    }

    public static string Tick(long value, Measure measure) => measure == Measure.Time ? TimeTick(value) : Format.Count(value);

    /// <summary>A value as a tooltip or callout says it.</summary>
    public static string Value(long value, Measure measure) => measure == Measure.Time ? Format.Duration(value) : Format.Opens(value);
}
