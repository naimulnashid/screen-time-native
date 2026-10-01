namespace ScreenTime.Core.Data;

/// <summary>
/// The database schema. <c>windows_segments</c> has the web dashboard's
/// columns minus <c>device_id</c> - this app is about the one PC it is
/// installed on - so that dashboard's laptop history imports row for row.
/// </summary>
/// <remarks>
/// <para><b>The rule every query obeys.</b> The sampler produces an EXCLUSIVE
/// PARTITION of tracked time: at any instant exactly one span is open, and it
/// is <c>app</c>, <c>locked</c>, <c>gap</c> or <c>unknown</c>. So active time
/// is <c>SUM(duration_ms) WHERE kind = 'app'</c>, by construction. <c>gap</c>
/// and <c>locked</c> are never active time, and <c>unknown</c> is never
/// quietly folded into either.</para>
/// <para><b>The write rule.</b> A span is written only once it has ENDED, so a
/// row never grows after it is first written: <c>INSERT OR IGNORE</c>, and
/// re-ingesting the same file inserts nothing.</para>
/// </remarks>
public static class Schema
{
    public const int Version = 1;

    public const string Sql = """
        PRAGMA journal_mode = WAL;

        -- One row per completed span, split at local HOUR boundaries, so an
        -- hour-of-day chart files a three-hour evening under three hours and
        -- not under the one it started in. session_start_utc survives the
        -- split: COUNT(DISTINCT session_start_utc) is still how many spans.
        CREATE TABLE IF NOT EXISTS windows_segments (
          id                INTEGER PRIMARY KEY AUTOINCREMENT,
          session_start_utc TEXT    NOT NULL,
          start_utc         TEXT    NOT NULL,
          end_utc           TEXT    NOT NULL,
          duration_ms       INTEGER NOT NULL,
          -- Machine-local day and hour, computed at ingest. Grouping raw UTC
          -- into days puts an evening on the wrong day.
          local_date        TEXT    NOT NULL,
          local_hour        INTEGER NOT NULL,
          -- app | locked | gap | unknown. Never collapsed to "app or not":
          -- a growing unknown share is the sign the sampler is mis-seeing the
          -- desktop, and it must stay visible.
          kind              TEXT    NOT NULL,
          -- Full exe path where Windows gave one, the bare process name where
          -- it refused. Display names are resolved in code, never stored.
          app_path          TEXT    NOT NULL DEFAULT '',
          -- The UWP frame-host lookup could not get past ApplicationFrameHost.
          unresolved        INTEGER NOT NULL DEFAULT 0,
          -- Idle at the end of the parent span. Stored raw, never applied:
          -- whether "focused but idle" counts is a policy question.
          idle_ms_at_end    INTEGER NOT NULL DEFAULT 0,
          UNIQUE (session_start_utc, start_utc, kind, app_path)
        );

        CREATE INDEX IF NOT EXISTS idx_seg_date ON windows_segments(local_date);
        CREATE INDEX IF NOT EXISTS idx_seg_app  ON windows_segments(app_path, local_date);

        -- One row per ingest. The history survives only if the sampler and the
        -- ingest actually run, so a broken one must show.
        CREATE TABLE IF NOT EXISTS sync_log (
          id                INTEGER PRIMARY KEY AUTOINCREMENT,
          started_at        TEXT    NOT NULL,
          finished_at       TEXT,
          status            TEXT    NOT NULL,   -- running | success | failed
          rows_read         INTEGER NOT NULL DEFAULT 0,
          rows_inserted     INTEGER NOT NULL DEFAULT 0,
          rows_skipped      INTEGER NOT NULL DEFAULT 0,
          -- The oldest and newest span the source files held this run.
          source_oldest_utc TEXT,
          source_newest_utc TEXT,
          backup_status     TEXT,
          duration_ms       INTEGER,
          error             TEXT
        );

        CREATE INDEX IF NOT EXISTS idx_sync_started ON sync_log(started_at DESC);

        CREATE TABLE IF NOT EXISTS meta (
          key   TEXT PRIMARY KEY,
          value TEXT NOT NULL
        );

        -- The user's own display names, keyed by the RESOLVED app key. In the
        -- database rather than a settings file because a rename is part of the
        -- history: backed up, and it survives the reset.
        CREATE TABLE IF NOT EXISTS app_renames (
          app_key    TEXT PRIMARY KEY,
          name       TEXT NOT NULL,
          updated_at TEXT NOT NULL
        );

        -- Chart colours chosen in the app, keyed like app_renames, as
        -- lowercase #rrggbb. Only the user's overrides.
        CREATE TABLE IF NOT EXISTS app_colours (
          app_key    TEXT PRIMARY KEY,
          colour     TEXT NOT NULL,
          updated_at TEXT NOT NULL
        );
        """;
}
