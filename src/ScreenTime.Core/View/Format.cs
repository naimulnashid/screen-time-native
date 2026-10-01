using System.Globalization;

namespace ScreenTime.Core.View;

/// <summary>A duration as at most two rungs: "6h 42m". <see cref="Sub"/> is absent when it would be zero.</summary>
public sealed record DurationParts(string Value, string Unit, (string Value, string Unit)? Sub);

/// <summary>
/// Formatting. Every number drawn from here is rendered with tabular figures,
/// so digits keep their width during count-ups.
/// </summary>
/// <remarks>
/// <para><b>Durations are not bytes.</b> Bytes have one ladder with one base,
/// and one rung is always enough. Durations have a mixed-radix ladder (60 s,
/// 60 min) where one rung routinely is not: "6h" threw away 42 minutes, and
/// "6.7h" is precise and unreadable. So <see cref="SplitDuration"/> returns
/// two rungs, and a caller that wants one number has to ask.</para>
/// <para><b>The ladder stops at hours.</b> A multi-day total reads "51h 12m",
/// never "2d 3h": screen time is judged against a 24-hour day, and "d" already
/// means a count of calendar days on these pages ("11 days with data").</para>
/// </remarks>
public static class Format
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly string[] MonthNames = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];

    private const long Second = 1000, Minute = 60 * Second, Hour = 60 * Minute;

    /// <summary>
    /// The two rungs: hours and minutes from an hour up, minutes and seconds
    /// from a minute, seconds below. Sub-second floors to "0s": milliseconds
    /// are a measurement artefact, not a fact about the person.
    /// </summary>
    public static DurationParts SplitDuration(long ms)
    {
        var t = Math.Max(0, ms);
        if (t >= Hour)
        {
            var h = t / Hour;
            var m = t % Hour / Minute;
            return new DurationParts(h.ToString(Inv), "h", m > 0 ? (m.ToString(Inv), "m") : null);
        }
        if (t >= Minute)
        {
            var m = t / Minute;
            var s = t % Minute / Second;
            return new DurationParts(m.ToString(Inv), "m", s > 0 ? (s.ToString(Inv), "s") : null);
        }
        return new DurationParts((t / Second).ToString(Inv), "s", null);
    }

    /// <summary>"6h 42m". The rendering wherever a duration is plain text.</summary>
    public static string Duration(long ms)
    {
        var p = SplitDuration(ms);
        return p.Sub is { } sub ? $"{p.Value}{p.Unit} {sub.Value}{sub.Unit}" : $"{p.Value}{p.Unit}";
    }

    /// <summary>
    /// <paramref name="ms"/> in the rungs <paramref name="final"/> would use,
    /// for the count-up: re-formatting the climbing value would race through
    /// "3s", "2m 10s", "58m 4s" before settling into hours.
    /// </summary>
    public static string DurationLike(long ms, long final)
    {
        var t = Math.Max(0, ms);
        var shape = SplitDuration(final);
        var hasSub = shape.Sub is not null;
        return shape.Unit switch
        {
            "h" => hasSub ? $"{t / Hour}h {t % Hour / Minute}m" : $"{t / Hour}h",
            "m" => hasSub ? $"{t / Minute}m {t % Minute / Second}s" : $"{t / Minute}m",
            _ => $"{t / Second}s",
        };
    }

    public static string Percent(double n, int decimals = 1) => n.ToString("F" + decimals, Inv) + "%";

    public static string Count(long n) => n.ToString("N0", CultureInfo.GetCultureInfo("en-US"));

    /// <summary>1 to "1 open", 1234 to "1,234 opens".</summary>
    public static string Opens(long n) => $"{Count(n)} open{(n == 1 ? "" : "s")}";

    /// <summary>"2026-08-21" to "Aug 21".</summary>
    public static string DayShort(string iso)
    {
        if (!DateOnly.TryParseExact(iso, "yyyy-MM-dd", Inv, DateTimeStyles.None, out var d)) return iso;
        return $"{MonthNames[d.Month - 1]} {d.Day}";
    }

    /// <summary>"2026-08-21" to "Thursday, 21 August 2026".</summary>
    public static string DayLong(string iso)
    {
        if (!DateOnly.TryParseExact(iso, "yyyy-MM-dd", Inv, DateTimeStyles.None, out var d)) return iso;
        return d.ToString("dddd, d MMMM yyyy", CultureInfo.GetCultureInfo("en-GB"));
    }

    /// <summary>"21 Aug 2026, 14:05" in local time.</summary>
    public static string DateTimeLocal(string iso) =>
        Data.Time.TryParse(iso, out var t) ? t.ToLocalTime().ToString("dd MMM yyyy, HH:mm", CultureInfo.GetCultureInfo("en-GB")) : iso;

    /// <summary>
    /// "12 min ago". Rounded FIRST, then compared, so 59.7 minutes does not
    /// print as "60 min ago"; and a fresh sync reads "just now", not "0 min".
    /// </summary>
    public static string Relative(double hours)
    {
        var minutes = Math.Round(hours * 60);
        if (minutes < 1) return "just now";
        if (minutes < 60) return $"{minutes.ToString(Inv)} min ago";
        if (hours < 24) return $"{Math.Round(hours).ToString(Inv)}h ago";
        var days = hours / 24;
        return days < 2 ? "yesterday" : $"{Math.Round(days).ToString(Inv)} days ago";
    }

    /// <summary>
    /// How long an ingest RUN took. Named apart from <see cref="Duration"/> on
    /// purpose: this one measures the tool, that one the person, and they want
    /// opposite precision.
    /// </summary>
    public static string Elapsed(long? ms) =>
        ms is null ? "—" : ms < 1000 ? $"{ms} ms" : $"{(ms.Value / 1000.0).ToString("F1", Inv)} s";

    /// <summary>14 to "2 PM".</summary>
    public static string HourOfDay(int hour) => hour switch
    {
        0 => "12 AM",
        12 => "12 PM",
        < 12 => $"{hour} AM",
        _ => $"{hour - 12} PM",
    };
}
