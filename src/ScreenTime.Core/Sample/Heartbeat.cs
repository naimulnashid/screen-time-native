using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ScreenTime.Core.Data;

namespace ScreenTime.Core.Sample;

/// <summary>The span the sampler is in right now, not yet written.</summary>
public sealed class InFlight
{
    [JsonPropertyName("start")] public string Start { get; set; } = "";
    [JsonPropertyName("kind")] public string Kind { get; set; } = "";
    [JsonPropertyName("app")] public string App { get; set; } = "";
    [JsonPropertyName("unresolved")] public bool Unresolved { get; set; }
    [JsonPropertyName("ms")] public long Ms { get; set; }
}

/// <summary>
/// <c>sampler-status.json</c>, rewritten every tick. The web sampler's shape,
/// plus when this run started.
/// </summary>
/// <remarks>
/// It carries the in-flight span for two readers: the Sync page, which says
/// what is being recorded now, and the NEXT sampler, which recovers it when
/// this one was killed without flushing (a logoff does that). <see cref="Closed"/>
/// is set only after the in-flight span is on disk, so false means "there is
/// something to recover".
/// </remarks>
public sealed class Heartbeat
{
    [JsonPropertyName("updated")] public string Updated { get; set; } = "";
    [JsonPropertyName("started")] public string? Started { get; set; }
    [JsonPropertyName("interval_seconds")] public int IntervalSeconds { get; set; } = 2;
    [JsonPropertyName("closed")] public bool Closed { get; set; }
    [JsonPropertyName("in_flight")] public InFlight? InFlight { get; set; }

    public const string FileName = "sampler-status.json";

    private static readonly JsonSerializerOptions Options = new();

    /// <summary>
    /// Reads a heartbeat, tolerating a BOM (PowerShell 5.1 writes one, and
    /// JSON parsers throw on it). Null when there is none or it is unreadable.
    /// </summary>
    public static Heartbeat? Read(string dir)
    {
        try
        {
            var path = Path.Combine(dir, FileName);
            if (!File.Exists(path)) return null;
            var text = File.ReadAllText(path, Encoding.UTF8).TrimStart('﻿');
            return JsonSerializer.Deserialize<Heartbeat>(text, Options);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Written to a temp file and moved over, so a reader never sees half of one.</summary>
    public void Write(string dir)
    {
        var path = Path.Combine(dir, FileName);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Options), new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }
}

/// <summary>Is the sampler running, and what is it recording?</summary>
public sealed record SamplerStatus(bool Alive, string? Updated, long? StaleSeconds, string? Started, InFlight? InFlight)
{
    public static readonly SamplerStatus Dead = new(false, null, null, null, null);

    /// <summary>
    /// Read from the heartbeat, never from the task state. A heartbeat older
    /// than a minute (or ten intervals) means dead: a sampler that has
    /// silently stopped loses time that nothing backfills.
    /// </summary>
    public static SamplerStatus Read(string dir, DateTime? nowUtc = null)
    {
        var hb = Heartbeat.Read(dir);
        if (hb is null || !Time.TryParse(hb.Updated, out var updated)) return Dead;
        var age = ((nowUtc ?? DateTime.UtcNow) - updated).TotalSeconds;
        var alive = !hb.Closed && age >= 0 && age < Math.Max(60, hb.IntervalSeconds * 10);
        return new SamplerStatus(alive, hb.Updated, (long)Math.Round(age), hb.Started, hb.InFlight);
    }
}
