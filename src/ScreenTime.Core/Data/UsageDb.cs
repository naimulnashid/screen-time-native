using System.Globalization;
using Microsoft.Data.Sqlite;

namespace ScreenTime.Core.Data;

/// <summary>The outcome of one ingest, as <c>sync_log</c> records it.</summary>
public sealed class SyncResult
{
    public string Status { get; set; } = "failed";
    public long RowsRead { get; set; }
    public long RowsInserted { get; set; }
    public long RowsSkipped { get; set; }
    public string? SourceOldestUtc { get; set; }
    public string? SourceNewestUtc { get; set; }
    public string? BackupStatus { get; set; }
    public long DurationMs { get; set; }
    public string? Error { get; set; }
}

/// <summary>Opening the database, and every write the ingest makes.</summary>
public static class UsageDb
{
    /// <summary>
    /// The one rule the project exists to enforce. A database under the system
    /// drive is destroyed by the Windows reset it is meant to survive, and
    /// failing loudly now beats finding out after one.
    /// </summary>
    public static void AssertNotOnSystemDrive(string path)
    {
        if (AppPaths.IsOnSystemDrive(path))
            throw new InvalidOperationException(
                $"Refusing to use a database on the system drive: {Path.GetFullPath(path)}. " +
                "It would be destroyed by a Windows reset, which is the exact failure this app exists to prevent. " +
                "Choose a data folder on another drive.");
    }

    /// <summary>
    /// Open for writing, creating the file and schema as needed.
    /// <paramref name="allowSystemDrive"/> is for tests and the demo, whose
    /// throwaway databases live in TEMP or the repo - and for an install whose
    /// owner ticked the box on the setup screen.
    /// </summary>
    public static SqliteConnection Open(string path, bool allowSystemDrive = false)
    {
        if (!allowSystemDrive) AssertNotOnSystemDrive(path);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString());
        conn.Open();
        Exec(conn, "PRAGMA busy_timeout = 10000;");
        Exec(conn, Schema.Sql);
        SetMeta(conn, "schema_version", Schema.Version.ToString(CultureInfo.InvariantCulture));
        return conn;
    }

    /// <summary>
    /// Open for reading, per use. Not pooled: the ingest writes on a schedule,
    /// and a long-lived handle across a WAL checkpoint serves stale numbers.
    /// Opening costs well under a millisecond.
    /// </summary>
    public static SqliteConnection OpenRead(string path)
    {
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        conn.Open();
        Exec(conn, "PRAGMA busy_timeout = 5000;");
        return conn;
    }

    public static int Exec(SqliteConnection conn, string sql, params (string Name, object? Value)[] args)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd.ExecuteNonQuery();
    }

    public static object? Scalar(SqliteConnection conn, string sql, params (string Name, object? Value)[] args)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        var v = cmd.ExecuteScalar();
        return v is DBNull ? null : v;
    }

    public static string NowIso() => Time.Iso(DateTime.UtcNow);

    /// <summary>
    /// Insert every segment the key has not seen, in one transaction (a day of
    /// spans one commit at a time is seconds instead of milliseconds). Returns
    /// how many were new; the rest were already there.
    /// </summary>
    public static long InsertSegments(SqliteConnection conn, IEnumerable<Segment> segments)
    {
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT OR IGNORE INTO windows_segments
              (session_start_utc, start_utc, end_utc, duration_ms, local_date, local_hour,
               kind, app_path, unresolved, idle_ms_at_end)
            VALUES ($s, $a, $b, $d, $ld, $lh, $k, $p, $u, $i)
            """;
        var p = new[] { "$s", "$a", "$b", "$d", "$ld", "$lh", "$k", "$p", "$u", "$i" }
            .Select(n => cmd.Parameters.Add(new SqliteParameter { ParameterName = n })).ToArray();
        cmd.Prepare();

        long inserted = 0;
        foreach (var s in segments)
        {
            p[0].Value = s.SessionStartUtc;
            p[1].Value = s.StartUtc;
            p[2].Value = s.EndUtc;
            p[3].Value = s.DurationMs;
            p[4].Value = s.LocalDate;
            p[5].Value = s.LocalHour;
            p[6].Value = s.Kind;
            p[7].Value = s.AppPath;
            p[8].Value = s.Unresolved ? 1 : 0;
            p[9].Value = s.IdleMsAtEnd;
            inserted += cmd.ExecuteNonQuery();
        }
        tx.Commit();
        return inserted;
    }

    public static long StartRun(SqliteConnection conn, string? startedAt = null)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO sync_log(started_at, status) VALUES($s, 'running'); SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("$s", startedAt ?? NowIso());
        return (long)cmd.ExecuteScalar()!;
    }

    public static void FinishRun(SqliteConnection conn, long id, SyncResult r, string? finishedAt = null) =>
        Exec(conn, """
            UPDATE sync_log SET
              finished_at = $f, status = $st, rows_read = $rr, rows_inserted = $ri,
              rows_skipped = $rs, source_oldest_utc = $o, source_newest_utc = $n,
              backup_status = $b, duration_ms = $d, error = $e
            WHERE id = $id
            """,
            ("$f", finishedAt ?? NowIso()), ("$st", r.Status), ("$rr", r.RowsRead), ("$ri", r.RowsInserted),
            ("$rs", r.RowsSkipped), ("$o", r.SourceOldestUtc), ("$n", r.SourceNewestUtc),
            ("$b", r.BackupStatus), ("$d", r.DurationMs), ("$e", r.Error), ("$id", id));

    /// <summary>
    /// Fold the WAL into the main file and empty it. The live file sits in a
    /// synced folder; this shrinks the window in which its three files disagree
    /// from constant to momentary. It does not make it a restore source.
    /// </summary>
    public static void Checkpoint(SqliteConnection conn)
    {
        try { Exec(conn, "PRAGMA wal_checkpoint(TRUNCATE);"); }
        catch (SqliteException) { }
    }

    /// <summary>
    /// A consistent copy through SQLite's backup API - never a file copy, which
    /// can be torn mid-write and misses the WAL. Written beside the target and
    /// swapped in, so a crash mid-backup cannot leave the restore source half
    /// written. Returns a status for sync_log rather than throwing: a failed
    /// backup must not discard a successful ingest.
    /// </summary>
    public static string Backup(SqliteConnection conn, string backupPath)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(backupPath))!);
            var tmp = backupPath + ".tmp";
            if (File.Exists(tmp)) File.Delete(tmp);
            using (var dest = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = tmp, Pooling = false }.ToString()))
            {
                dest.Open();
                conn.BackupDatabase(dest);
                // The copy inherits WAL mode; a restore source must be one file.
                Exec(dest, "PRAGMA journal_mode = DELETE;");
            }
            File.Move(tmp, backupPath, overwrite: true);
            return "ok";
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            return "failed: " + ex.Message;
        }
    }

    public static void SetMeta(SqliteConnection conn, string key, string value) =>
        Exec(conn, "INSERT INTO meta(key, value) VALUES($k, $v) ON CONFLICT(key) DO UPDATE SET value = excluded.value", ("$k", key), ("$v", value));

    public static string? GetMeta(SqliteConnection conn, string key) => Scalar(conn, "SELECT value FROM meta WHERE key = $k", ("$k", key)) as string;
}
