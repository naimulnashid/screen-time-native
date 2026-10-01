using System.Text.RegularExpressions;
using ScreenTime.Core.Data;
using Microsoft.Data.Sqlite;

namespace ScreenTime.Core.Naming;

public enum RenameError
{
    None,
    UnknownApp,
    BadName,
    Reserved,
    Taken,
}

/// <summary>
/// Display names the user chose, overriding the resolved ones. Stored in the
/// database, keyed by the RESOLVED key: a rename covers every path that
/// resolves to the app, and never touches a recorded row.
/// </summary>
/// <remarks>
/// An app's display name keys its colour and its logo, so a new name may not
/// be one another app already shows (the two would silently share both), may
/// not be <c>Other</c> (the stacked charts' remainder), and a renamed app
/// keeps its ORIGINAL name's colour and logo unless one matches the new name.
/// </remarks>
public static partial class Renames
{
    public const int MaxLength = 60;

    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase) { "other", "unknown" };

    [GeneratedRegex(@"[\u0000-\u001f\u007f]")]
    private static partial Regex ControlChars();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    /// <summary>App key to chosen name. Empty when there are none, or no table yet.</summary>
    public static Dictionary<string, string> Read(SqliteConnection db)
    {
        var map = new Dictionary<string, string>();
        try
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT app_key, name FROM app_renames";
            using var r = cmd.ExecuteReader();
            while (r.Read()) map[r.GetString(0)] = r.GetString(1);
        }
        catch (SqliteException)
        {
            // "no such table" on a database no write path has touched yet.
        }
        return map;
    }

    /// <summary>Trim, collapse whitespace, drop control characters; null when unusable.</summary>
    public static string? Clean(string? raw)
    {
        if (raw is null) return null;
        var name = Whitespace().Replace(ControlChars().Replace(raw, ""), " ").Trim();
        return name.Length == 0 || name.Length > MaxLength ? null : name;
    }

    /// <summary>
    /// Validate and store, or clear, one rename. <paramref name="current"/> is
    /// every app as shown now (renames included), across ALL history; an empty
    /// name or the base name clears the override.
    /// </summary>
    public static (RenameError Error, string? Name) Save(SqliteConnection db, string key, string? requested,
        IReadOnlyDictionary<string, string> current, string baseName)
    {
        if (!current.ContainsKey(key)) return (RenameError.UnknownApp, null);
        var reset = requested is not null && requested.Trim().Length == 0;
        var name = reset ? baseName : Clean(requested);
        if (name is null) return (RenameError.BadName, null);

        if (!reset && name != baseName)
        {
            if (Reserved.Contains(name)) return (RenameError.Reserved, null);
            foreach (var (k, shown) in current)
                if (k != key && string.Equals(shown, name, StringComparison.OrdinalIgnoreCase)) return (RenameError.Taken, null);
        }

        if (name == baseName)
            UsageDb.Exec(db, "DELETE FROM app_renames WHERE app_key = $k", ("$k", key));
        else
            UsageDb.Exec(db, """
                INSERT INTO app_renames (app_key, name, updated_at) VALUES ($k, $n, $t)
                ON CONFLICT (app_key) DO UPDATE SET name = excluded.name, updated_at = excluded.updated_at
                """, ("$k", key), ("$n", name), ("$t", UsageDb.NowIso()));
        return (RenameError.None, name);
    }

    public static string Message(RenameError e) => e switch
    {
        RenameError.UnknownApp => "That app is not in the history.",
        RenameError.BadName => $"Use 1 to {MaxLength} characters.",
        RenameError.Reserved => "That name is reserved.",
        RenameError.Taken => "Another app already has that name.",
        _ => "",
    };
}
