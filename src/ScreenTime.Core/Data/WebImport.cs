using System.Text.Json;
using System.Text.Json.Nodes;
using ScreenTime.Core.Naming;
using ScreenTime.Core.Sample;
using Microsoft.Data.Sqlite;

namespace ScreenTime.Core.Data;

public sealed record ImportResult(long Rows, long RowsNew, long Clipped, string? Cutoff, long Runs, long Renames, long Colours);

/// <summary>
/// A copy of the web dashboard's LAPTOP history into this database: its
/// segments up to the moment this app's own sampler began, its run log, and
/// the laptop's renames and chosen colours. Nothing Android.
/// </summary>
/// <remarks>
/// <para><b>The two samplers run side by side</b> (the owner's choice, until
/// the two have been compared), each recording the same foreground into its
/// own database. So the import must STOP where this app's history starts, or
/// every minute after that would be counted twice. The cutoff is the earliest
/// of: the first span this database's ingest ever stored
/// (<c>meta.native_first_utc</c>), and the sampler's own
/// <see cref="Sampler.FirstStartFile"/>, which covers spans still waiting in
/// JSONL. A segment straddling the cutoff is clipped to it.</para>
/// <para>It is a COPY of a copy: the source file is copied to TEMP and
/// attached from there, so nothing here can write to, lock or journal the
/// other project's database. Re-running inserts nothing twice: the dedup key
/// recognises every row, and runs are matched on their start instant.</para>
/// </remarks>
public static class WebImport
{
    /// <summary>The web dashboard's id for the laptop.</summary>
    public const string WebDeviceId = "zephyrus";

    public static string? Cutoff(SqliteConnection target, string samplerDir)
    {
        var candidates = new List<string>();
        if (UsageDb.GetMeta(target, "native_first_utc") is { } first) candidates.Add(first);
        var file = Path.Combine(samplerDir, Sampler.FirstStartFile);
        if (File.Exists(file) && Time.TryParse(File.ReadAllText(file).Trim(), out var started)) candidates.Add(Time.Iso(started));
        return candidates.Count == 0 ? null : candidates.Min(StringComparer.Ordinal);
    }

    public static ImportResult Run(SqliteConnection target, string sourcePath, string samplerDir)
    {
        if (!File.Exists(sourcePath)) throw new FileNotFoundException("web dashboard database not found", sourcePath);

        var copy = Path.Combine(Path.GetTempPath(), $"ScreenTimeNative-import-{Guid.NewGuid():N}.db");
        File.Copy(sourcePath, copy);
        UsageDb.Exec(target, "ATTACH DATABASE $src AS web", ("$src", copy));
        try
        {
            var cutoff = Cutoff(target, samplerDir) ?? "9999-12-31T00:00:00.000Z";
            var rows = (long)(UsageDb.Scalar(target, "SELECT COUNT(*) FROM web.windows_segments WHERE device_id = $d AND start_utc < $c", ("$d", WebDeviceId), ("$c", cutoff)) ?? 0L);
            var clipped = (long)(UsageDb.Scalar(target, "SELECT COUNT(*) FROM web.windows_segments WHERE device_id = $d AND start_utc < $c AND end_utc > $c", ("$d", WebDeviceId), ("$c", cutoff)) ?? 0L);

            using var tx = target.BeginTransaction();
            // ISO strings in one format compare as text, so the cutoff is a
            // plain MIN(); a clipped row's duration is recomputed from its ends.
            var rowsNew = Exec(target, tx, """
                INSERT OR IGNORE INTO windows_segments
                  (session_start_utc, start_utc, end_utc, duration_ms, local_date, local_hour,
                   kind, app_path, unresolved, idle_ms_at_end)
                SELECT session_start_utc, start_utc,
                       CASE WHEN end_utc > $c THEN $c ELSE end_utc END,
                       CASE WHEN end_utc > $c
                            THEN CAST(ROUND((julianday($c) - julianday(start_utc)) * 86400000) AS INTEGER)
                            ELSE duration_ms END,
                       local_date, local_hour, kind, app_path, unresolved, idle_ms_at_end
                  FROM web.windows_segments
                 WHERE device_id = $d AND start_utc < $c
                 ORDER BY id
                """, ("$d", WebDeviceId), ("$c", cutoff));

            // A run the backup caught mid-flight would read "running" here
            // forever: left out. Measured on the first real import, 2026-10-01.
            var runs = Exec(target, tx, """
                INSERT INTO sync_log (started_at, finished_at, status, rows_read, rows_inserted, rows_skipped,
                  source_oldest_utc, source_newest_utc, backup_status, duration_ms, error)
                SELECT started_at, finished_at, status, COALESCE(rows_read, 0), COALESCE(rows_inserted, 0), COALESCE(rows_skipped, 0),
                  source_oldest_utc, source_newest_utc, backup_status, duration_ms, error
                  FROM web.sync_log w
                 WHERE w.device_id = $d AND w.started_at < $c AND w.status <> 'running'
                   AND NOT EXISTS (SELECT 1 FROM sync_log s WHERE s.started_at = w.started_at)
                 ORDER BY w.id
                """, ("$d", WebDeviceId), ("$c", cutoff));

            // The newer side wins: a rename made here since is not undone.
            var renames = TableExists(target, tx, "app_renames") ? Exec(target, tx, """
                INSERT INTO app_renames (app_key, name, updated_at)
                SELECT app_key, name, updated_at FROM web.app_renames WHERE device_id = $d
                ON CONFLICT (app_key) DO UPDATE SET name = excluded.name, updated_at = excluded.updated_at
                  WHERE excluded.updated_at > app_renames.updated_at
                """, ("$d", WebDeviceId)) : 0;
            var colours = TableExists(target, tx, "app_colours") ? Exec(target, tx, """
                INSERT INTO app_colours (app_key, colour, updated_at)
                SELECT app_key, colour, updated_at FROM web.app_colours WHERE device_id = $d
                ON CONFLICT (app_key) DO UPDATE SET colour = excluded.colour, updated_at = excluded.updated_at
                  WHERE excluded.updated_at > app_colours.updated_at
                """, ("$d", WebDeviceId)) : 0;
            tx.Commit();

            UsageDb.SetMeta(target, "imported_from_web", UsageDb.NowIso());
            if (cutoff[0] != '9') UsageDb.SetMeta(target, "web_import_cutoff", cutoff);
            return new ImportResult(rows, rowsNew, clipped, cutoff[0] == '9' ? null : cutoff, runs, renames, colours);
        }
        finally
        {
            UsageDb.Exec(target, "DETACH DATABASE web");
            SqliteConnection.ClearAllPools();
            try { File.Delete(copy); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>Older web databases predate the rename and colour tables.</summary>
    private static bool TableExists(SqliteConnection conn, SqliteTransaction tx, string table)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT COUNT(*) FROM web.sqlite_master WHERE type = 'table' AND name = $t";
        cmd.Parameters.AddWithValue("$t", table);
        return (long)cmd.ExecuteScalar()! > 0;
    }

    private static int Exec(SqliteConnection conn, SqliteTransaction tx, string sql, params (string Name, object? Value)[] args)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Copy logo files that are not already here, from each folder in turn.
    /// Existing files win: a logo set in this app is never overwritten.
    /// </summary>
    public static int CopyLogos(IEnumerable<string> sourceDirs, string targetDir)
    {
        Directory.CreateDirectory(targetDir);
        var have = Directory.EnumerateFiles(targetDir).Select(f => AppIcons.Key(Path.GetFileNameWithoutExtension(f))).ToHashSet();
        var copied = 0;
        foreach (var dir in sourceDirs.Where(Directory.Exists))
            foreach (var file in Directory.EnumerateFiles(dir).Order(StringComparer.OrdinalIgnoreCase))
            {
                if (!AppIcons.Renderable.Contains(Path.GetExtension(file))) continue;
                var key = AppIcons.Key(Path.GetFileNameWithoutExtension(file));
                if (!have.Add(key)) continue;
                File.Copy(file, Path.Combine(targetDir, Path.GetFileName(file)));
                copied++;
            }
        return copied;
    }

    /// <summary>
    /// The web dashboard's brand colours for the apps this machine has, into
    /// the local <c>colours.json</c>, keyed by display name. Its file is keyed
    /// by logo identity (lowercase, no spaces), so it is matched through
    /// <see cref="AppIcons.Key"/>. Entries already here win.
    /// </summary>
    public static int CopyColours(string sourceJson, IEnumerable<string> appNames, string targetJson)
    {
        if (!File.Exists(sourceJson)) return 0;
        var node = JsonNode.Parse(File.ReadAllText(sourceJson).TrimStart('﻿')) as JsonObject;
        var entries = (node?["colours"] as JsonObject) ?? node;
        if (entries is null) return 0;
        var byKey = new Dictionary<string, string>();
        foreach (var (key, value) in entries)
        {
            var hex = value switch
            {
                JsonValue v when v.TryGetValue<string>(out var s) => s,
                JsonObject o => o["hex"]?.GetValue<string>(),
                _ => null,
            };
            // Device-scoped entries ("nothinga001/camera") are the phones'.
            if (key.Contains('/') || ColorOverrides.Clean(hex) is not { } clean) continue;
            byKey[key] = clean;
        }

        var existing = AppColors.ReadLocal(targetJson);
        var added = 0;
        foreach (var name in appNames)
        {
            if (existing.ContainsKey(name) || !byKey.TryGetValue(AppIcons.Key(name), out var hex)) continue;
            existing[name] = hex;
            added++;
        }
        if (added > 0)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(targetJson))!);
            var sorted = existing.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase).ToDictionary(kv => kv.Key, kv => kv.Value);
            File.WriteAllText(targetJson, JsonSerializer.Serialize(sorted, new JsonSerializerOptions { WriteIndented = true }));
        }
        return added;
    }
}
