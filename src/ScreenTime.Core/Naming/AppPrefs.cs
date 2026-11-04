using ScreenTime.Core.Data;
using Microsoft.Data.Sqlite;

namespace ScreenTime.Core.Naming;

public enum MergeError
{
    None,
    UnknownApp,
    SameApp,
}

/// <summary>
/// Two choices the user makes about apps on the By App page: hiding one, and
/// merging one into another. Stored beside renames and colours (schema 2),
/// keyed the same way, by the RESOLVED app key: the web dashboard's
/// <c>app_hidden</c> and <c>app_merges</c> without its device column.
/// </summary>
/// <remarks>
/// <para><b>Hiding is a view choice.</b> A hidden app leaves the By App list,
/// grid and both rankings, and nothing else: totals, charts and every other
/// page still count it, or the apps would stop summing to the active total.</para>
/// <para><b>Merging is a naming choice</b>, like a rename one level up: the
/// merged app is read as part of its target everywhere, exactly as
/// <see cref="AppNames"/> folds a versioned WindowsApps folder into one app.
/// The target's Merged apps card lists it and separates it again. No time
/// moves.</para>
/// <para>One level only: a target is never itself merged. Merging into a
/// merged app stores the merge against ITS target, and merging an app others
/// were merged into carries them along, so a lookup is one step and a chain
/// can never loop.</para>
/// </remarks>
public static class AppPrefs
{
    /// <summary>App key to the key it is merged into.</summary>
    public static Dictionary<string, string> ReadMerges(SqliteConnection db) =>
        ReadPairs(db, "SELECT app_key, into_key FROM app_merges");

    /// <summary>App keys hidden from the By App page.</summary>
    public static HashSet<string> ReadHidden(SqliteConnection db) =>
        [.. ReadPairs(db, "SELECT app_key, app_key FROM app_hidden").Keys];

    private static Dictionary<string, string> ReadPairs(SqliteConnection db, string sql)
    {
        var map = new Dictionary<string, string>();
        try
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = sql;
            using var r = cmd.ExecuteReader();
            while (r.Read()) map[r.GetString(0)] = r.GetString(1);
        }
        catch (SqliteException)
        {
            // "no such table" on a database no write path has touched yet.
        }
        return map;
    }

    public static void SetHidden(SqliteConnection db, string key, bool hidden)
    {
        if (hidden)
            UsageDb.Exec(db, "INSERT INTO app_hidden (app_key, hidden_at) VALUES ($k, $t) ON CONFLICT (app_key) DO NOTHING",
                ("$k", key), ("$t", UsageDb.NowIso()));
        else
            UsageDb.Exec(db, "DELETE FROM app_hidden WHERE app_key = $k", ("$k", key));
    }

    /// <summary>
    /// Merge <paramref name="key"/> into <paramref name="into"/>, or separate it
    /// again when <paramref name="into"/> is null. <paramref name="known"/> is
    /// every app key BEFORE merges.
    /// </summary>
    public static MergeError SetMerge(SqliteConnection db, string key, string? into, IReadOnlySet<string> known)
    {
        if (!known.Contains(key)) return MergeError.UnknownApp;
        if (into is null)
        {
            UsageDb.Exec(db, "DELETE FROM app_merges WHERE app_key = $k", ("$k", key));
            return MergeError.None;
        }
        if (!known.Contains(into)) return MergeError.UnknownApp;
        var target = ReadMerges(db).GetValueOrDefault(into) ?? into;
        if (target == key) return MergeError.SameApp;

        var now = UsageDb.NowIso();
        // BEGIN as SQL rather than BeginTransaction: Exec's commands carry no
        // transaction object, which Microsoft.Data.Sqlite refuses inside one.
        UsageDb.Exec(db, "BEGIN");
        try
        {
            // Whatever was merged into `key` follows it to the new target.
            UsageDb.Exec(db, "UPDATE app_merges SET into_key = $t, updated_at = $n WHERE into_key = $k",
                ("$t", target), ("$n", now), ("$k", key));
            UsageDb.Exec(db, """
                INSERT INTO app_merges (app_key, into_key, updated_at) VALUES ($k, $t, $n)
                ON CONFLICT (app_key) DO UPDATE SET into_key = excluded.into_key, updated_at = excluded.updated_at
                """, ("$k", key), ("$t", target), ("$n", now));
            // A merged app is not on the list to be hidden from.
            UsageDb.Exec(db, "DELETE FROM app_hidden WHERE app_key = $k", ("$k", key));
            UsageDb.Exec(db, "COMMIT");
        }
        catch
        {
            UsageDb.Exec(db, "ROLLBACK");
            throw;
        }
        return MergeError.None;
    }

    public static string Message(MergeError e) => e switch
    {
        MergeError.UnknownApp => "That app is not in the history.",
        MergeError.SameApp => "An app cannot be merged into itself.",
        _ => "",
    };
}
