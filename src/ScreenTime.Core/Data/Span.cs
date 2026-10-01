using System.Globalization;
using System.Text.Json.Serialization;

namespace ScreenTime.Core.Data;

/// <summary>
/// One completed span, as the sampler writes it: one JSON object per line.
/// The field names are the web dashboard's sampler's, so either one's files
/// can be read by either ingest.
/// </summary>
public sealed class Span
{
    [JsonPropertyName("start")] public string Start { get; set; } = "";
    [JsonPropertyName("end")] public string End { get; set; } = "";
    [JsonPropertyName("ms")] public long Ms { get; set; }
    /// <summary>app | locked | gap | unknown.</summary>
    [JsonPropertyName("kind")] public string Kind { get; set; } = "";
    [JsonPropertyName("app")] public string App { get; set; } = "";
    [JsonPropertyName("idle_ms")] public long IdleMs { get; set; }

    /// <summary>Written only when true, so the common case stays one short line.</summary>
    [JsonPropertyName("unresolved")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Unresolved { get; set; }
}

/// <summary>One hour-bounded piece of a span: a <c>windows_segments</c> row.</summary>
public sealed record Segment(string SessionStartUtc, string StartUtc, string EndUtc, long DurationMs, string LocalDate, int LocalHour,
    string Kind, string AppPath, bool Unresolved, long IdleMsAtEnd);

/// <summary>Instants and local buckets, in one format everywhere.</summary>
public static class Time
{
    /// <summary>
    /// <c>2026-09-30T12:00:00.000Z</c> - JavaScript's <c>toISOString</c>, so a
    /// span recorded by either sampler has the same key, and ISO strings in
    /// this one format compare correctly as text.
    /// </summary>
    public static string Iso(DateTime utc) => utc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    public static bool TryParse(string? iso, out DateTime utc)
    {
        utc = default;
        if (string.IsNullOrWhiteSpace(iso)) return false;
        if (!DateTime.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t)) return false;
        utc = DateTime.SpecifyKind(t, DateTimeKind.Utc);
        return true;
    }

    /// <summary>
    /// The machine-local day and hour of an instant. LOCAL, never UTC: grouping
    /// raw UTC into days shifts every daily total by the offset, which puts an
    /// evening's use on the wrong day.
    /// </summary>
    public static (string Date, int Hour) LocalBuckets(DateTime utc)
    {
        var local = utc.ToLocalTime();
        return (local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), local.Hour);
    }

    public static string LocalToday() => DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>
    /// A span cut at every local hour boundary it crosses. Walks the
    /// boundaries in LOCAL time rather than adding an hour of milliseconds: on
    /// a daylight-saving change a local hour is 0 or 2 real hours long.
    /// </summary>
    public static List<(DateTime Start, DateTime End)> SplitIntoHours(DateTime startUtc, DateTime endUtc)
    {
        var pieces = new List<(DateTime, DateTime)>();
        var cur = startUtc;
        var guard = 0;
        while (cur < endUtc && guard++ < 100_000)
        {
            var local = cur.ToLocalTime();
            var nextLocal = new DateTime(local.Year, local.Month, local.Day, local.Hour, 0, 0, DateTimeKind.Local).AddHours(1);
            var next = nextLocal.ToUniversalTime();
            if (next <= cur) next = cur.AddHours(1);
            var end = next < endUtc ? next : endUtc;
            pieces.Add((cur, end));
            cur = end;
        }
        return pieces;
    }

    /// <summary>A span as table rows, one per local clock hour it covers.</summary>
    public static List<Segment> Segments(Span span)
    {
        if (!TryParse(span.Start, out var start) || !TryParse(span.End, out var end) || end <= start) return [];
        var session = Iso(start);
        return SplitIntoHours(start, end).Select(p =>
        {
            var (date, hour) = LocalBuckets(p.Start);
            return new Segment(session, Iso(p.Start), Iso(p.End), (long)Math.Round((p.End - p.Start).TotalMilliseconds),
                date, hour, span.Kind, span.App ?? "", span.Unresolved, span.IdleMs);
        }).ToList();
    }
}
