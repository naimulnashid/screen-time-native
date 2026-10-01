using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ScreenTime.Core.Data;

namespace ScreenTime.Core.Collect;

public sealed class IngestOptions
{
    public string SamplerDir { get; init; } = AppPaths.SamplerDir;

    /// <summary>Override the data folder (tests); otherwise <see cref="AppPaths.DataDir"/>.</summary>
    public string? DataDir { get; init; }

    public bool AllowSystemDrive { get; init; } = AppPaths.AllowSystemDrive;

    /// <summary>Keep the JSONL after folding it in (tests, and a by-hand run).</summary>
    public bool Keep { get; init; }

    /// <summary>Back up even if the last backup is recent (the Sync button).</summary>
    public bool ForceBackup { get; init; }

    /// <summary>The sampler's write lock, when the sampler itself ingests: no torn last line.</summary>
    public object? ReadLock { get; init; }
}

public sealed record IngestResult(string Status, string? Note, int Files, long Read, long Inserted, long Skipped, long Malformed, int Removed,
    string? BackupStatus, long DurationMs, string? Error)
{
    /// <summary>"Added 120 segments" / "Up to date". One sentence for a button or a toast.</summary>
    public string Describe()
    {
        if (Status != "success") return "Sync failed: " + (Error ?? "unknown error");
        if (Note is not null) return Note;
        if (Inserted == 0) return $"Up to date - read {Read} span{(Read == 1 ? "" : "s")}, nothing new.";
        var malformed = Malformed > 0 ? $", {Malformed} malformed line{(Malformed == 1 ? "" : "s")} skipped" : "";
        return $"Added {Inserted:N0} segment{(Inserted == 1 ? "" : "s")} from {Read:N0} span{(Read == 1 ? "" : "s")}{malformed}.";
    }
}

/// <summary>
/// Folds the sampler's JSONL into SQLite. Called by the sampler every 15
/// minutes, by the app's Sync now, and by <c>screentime ingest</c>.
/// </summary>
/// <remarks>
/// <para><b>Idempotent.</b> A completed span is immutable, so re-reading the same
/// files inserts nothing. That is what makes it safe to read today's file
/// while the sampler is still appending to it: a line torn mid-write is
/// counted as malformed and simply read whole next time.</para>
/// <para><b>Serialised</b> by a named mutex per data folder, so the sampler's
/// timer and the Sync button never run two at once.</para>
/// <para><b>Files are deleted only once nothing can still be appended to
/// them</b>: today's and yesterday's stay. The backup is written at most hourly
/// (or on Sync now): it is a full copy of the database into a synced folder.</para>
/// </remarks>
public static partial class Ingest
{
    [GeneratedRegex(@"^sessions-(\d{4}-\d{2}-\d{2})\.jsonl$")]
    private static partial Regex DayFile();

    private static readonly TimeSpan BackupEvery = TimeSpan.FromMinutes(55);

    public static IngestResult Run(IngestOptions o)
    {
        var dataDir = o.DataDir ?? AppPaths.DataDir;
        if (dataDir is null) return new IngestResult("success", "No data folder chosen yet - open the app once to choose one.", 0, 0, 0, 0, 0, 0, null, 0, null);

        using var mutex = new Mutex(false, MutexName(dataDir));
        bool owned;
        try { owned = mutex.WaitOne(TimeSpan.FromSeconds(60)); }
        catch (AbandonedMutexException) { owned = true; }
        if (!owned) return new IngestResult("success", "Another ingest is running.", 0, 0, 0, 0, 0, 0, null, 0, null);
        try
        {
            return RunLocked(o, dataDir);
        }
        finally
        {
            mutex.ReleaseMutex();
        }
    }

    public static string MutexName(string dataDir)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(dataDir).TrimEnd('\\').ToLowerInvariant()));
        return "Local\\ScreenTimeNative.Ingest-" + Convert.ToHexString(hash, 0, 8);
    }

    private static IngestResult RunLocked(IngestOptions o, string dataDir)
    {
        var clock = Stopwatch.StartNew();
        using var db = UsageDb.Open(AppPaths.DatabasePath(dataDir), o.AllowSystemDrive);

        var files = Directory.Exists(o.SamplerDir)
            ? Directory.EnumerateFiles(o.SamplerDir).Where(f => DayFile().IsMatch(Path.GetFileName(f))).Order(StringComparer.Ordinal).ToList()
            : [];
        if (files.Count == 0) return new IngestResult("success", $"No sampler output in {o.SamplerDir} yet.", 0, 0, 0, 0, 0, 0, null, clock.ElapsedMilliseconds, null);

        var runId = UsageDb.StartRun(db);
        long read = 0, inserted = 0, segments = 0, malformed = 0;
        string? oldest = null, newest = null;
        try
        {
            foreach (var file in files)
            {
                string text;
                if (o.ReadLock is { } gate)
                    lock (gate) text = ReadShared(file);
                else text = ReadShared(file);

                var batch = new List<Segment>();
                foreach (var raw in text.Split('\n'))
                {
                    var line = raw.Trim().TrimStart('﻿');
                    if (line.Length == 0) continue;
                    Span? span;
                    try { span = JsonSerializer.Deserialize<Span>(line); }
                    catch (JsonException) { span = null; }
                    var pieces = span is null ? [] : Time.Segments(span);
                    if (pieces.Count == 0)
                    {
                        malformed++;
                        continue;
                    }
                    read++;
                    if (oldest is null || string.CompareOrdinal(span!.Start, oldest) < 0) oldest = span!.Start;
                    if (newest is null || string.CompareOrdinal(span.End, newest) > 0) newest = span.End;
                    batch.AddRange(pieces);
                }
                segments += batch.Count;
                inserted += UsageDb.InsertSegments(db, batch);
            }

            // The first span this sampler ever recorded: an import of the web
            // dashboard's history stops there, or the two would overlap.
            if (oldest is not null && UsageDb.GetMeta(db, "native_first_utc") is null)
                UsageDb.SetMeta(db, "native_first_utc", oldest);

            UsageDb.Checkpoint(db);
            string? backup = null;
            var lastBackup = UsageDb.GetMeta(db, "last_backup_utc");
            if (o.ForceBackup || inserted > 0 && (!Time.TryParse(lastBackup, out var at) || DateTime.UtcNow - at >= BackupEvery))
            {
                backup = UsageDb.Backup(db, AppPaths.BackupPath(dataDir));
                if (backup == "ok") UsageDb.SetMeta(db, "last_backup_utc", UsageDb.NowIso());
            }

            UsageDb.FinishRun(db, runId, new SyncResult
            {
                Status = "success",
                RowsRead = read,
                RowsInserted = inserted,
                RowsSkipped = segments - inserted,
                SourceOldestUtc = oldest,
                SourceNewestUtc = newest,
                BackupStatus = backup,
                DurationMs = clock.ElapsedMilliseconds,
            });

            var removed = o.Keep ? 0 : RemoveFinished(files);
            return new IngestResult("success", null, files.Count, read, inserted, segments - inserted, malformed, removed, backup, clock.ElapsedMilliseconds, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            UsageDb.FinishRun(db, runId, new SyncResult { Status = "failed", RowsRead = read, Error = ex.Message, DurationMs = clock.ElapsedMilliseconds, SourceOldestUtc = oldest, SourceNewestUtc = newest });
            return new IngestResult("failed", null, files.Count, read, 0, 0, malformed, 0, null, clock.ElapsedMilliseconds, ex.Message);
        }
    }

    /// <summary>Read without blocking the sampler, which keeps today's file open for appending.</summary>
    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, new UTF8Encoding(false));
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Deletes the files nothing can still append to. A span is filed under the
    /// day it ENDED, so today's file is still growing; yesterday's stays a day
    /// longer as a margin. Everything in them is in the database by now.
    /// </summary>
    private static int RemoveFinished(List<string> files)
    {
        var keepFrom = DateTime.Now.Date.AddDays(-1).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var removed = 0;
        foreach (var file in files)
        {
            var date = DayFile().Match(Path.GetFileName(file)).Groups[1].Value;
            if (string.CompareOrdinal(date, keepFrom) >= 0) continue;
            try
            {
                File.Delete(file);
                removed++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return removed;
    }
}
