using System.Text.RegularExpressions;
using ScreenTime.Core.Data;
using Microsoft.Data.Sqlite;

namespace ScreenTime.Core.Naming;

public enum ColorError
{
    None,
    UnknownApp,
    BadColor,
}

/// <summary>
/// Chart colours the user chose, overriding the assigned ones. Stored in the
/// database beside the renames and keyed the same way, for the same reason:
/// it is part of the history, backed up with it, and it survives a reset.
/// </summary>
/// <remarks>
/// <see cref="Query.UsageQueries.ColorMap"/> applies an override LAST, over
/// brand and palette and over a rename's inherited colour. Two apps may share
/// a colour; unlike a name, a colour keys nothing.
/// </remarks>
public static partial class ColorOverrides
{
    [GeneratedRegex("^#?([0-9a-fA-F]{3}|[0-9a-fA-F]{6})$")]
    private static partial Regex HexPattern();

    /// <summary>
    /// <c>#rgb</c> or <c>#rrggbb</c>, any case, with or without the <c>#</c>,
    /// to lowercase <c>#rrggbb</c>; null for anything else.
    /// </summary>
    public static string? Clean(string? raw)
    {
        if (raw is null) return null;
        var m = HexPattern().Match(raw.Trim());
        if (!m.Success) return null;
        var hex = m.Groups[1].Value.ToLowerInvariant();
        if (hex.Length == 3) hex = string.Concat(hex.Select(c => $"{c}{c}"));
        return "#" + hex;
    }

    /// <summary>App key to #rrggbb. Empty when there are none, or no table yet.</summary>
    public static Dictionary<string, string> Read(SqliteConnection db)
    {
        var map = new Dictionary<string, string>();
        try
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT app_key, colour FROM app_colours";
            using var r = cmd.ExecuteReader();
            // Re-validated on the way out: a hand-edited row must not reach a brush.
            while (r.Read())
                if (Clean(r.GetString(1)) is { } c) map[r.GetString(0)] = c;
        }
        catch (SqliteException)
        {
            // "no such table" on a database no write path has touched yet.
        }
        return map;
    }

    /// <summary>
    /// Validate and store, or clear, one colour. An empty string clears it and
    /// the app goes back to its brand or palette colour.
    /// </summary>
    public static (ColorError Error, string? Color) Save(SqliteConnection db, string key, string? requested, IReadOnlySet<string> known)
    {
        if (!known.Contains(key)) return (ColorError.UnknownApp, null);
        var reset = requested is not null && requested.Trim().Length == 0;
        var color = reset ? null : Clean(requested);
        if (!reset && color is null) return (ColorError.BadColor, null);

        if (color is null)
            UsageDb.Exec(db, "DELETE FROM app_colours WHERE app_key = $k", ("$k", key));
        else
            UsageDb.Exec(db, """
                INSERT INTO app_colours (app_key, colour, updated_at) VALUES ($k, $c, $t)
                ON CONFLICT (app_key) DO UPDATE SET colour = excluded.colour, updated_at = excluded.updated_at
                """, ("$k", key), ("$c", color), ("$t", UsageDb.NowIso()));
        return (ColorError.None, color);
    }

    public static string Message(ColorError e) => e switch
    {
        ColorError.UnknownApp => "That app is not in the history.",
        ColorError.BadColor => "A colour is a hex code like #7c5cff.",
        _ => "",
    };
}
