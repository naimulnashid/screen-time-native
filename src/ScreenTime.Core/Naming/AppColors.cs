using System.Globalization;
using System.Text.Json;

namespace ScreenTime.Core.Naming;

/// <summary>
/// Per-app chart colours: brand first, a generated palette second.
/// </summary>
/// <remarks>
/// <para>Colour is recognition, not encoding: bar length and the axis carry the
/// data. So honest collisions stay - several Microsoft blues sit in one chart -
/// and the logo beside every legend entry carries the identification the
/// colour cannot carry alone.</para>
/// <para>Brands come from two places: the built-in list below, of well-known
/// apps, and <c>colours.json</c> in the data folder, which this machine's own
/// apps go in. That file is local, like the logos, because together they are
/// an inventory of what this machine runs.</para>
/// </remarks>
public static class AppColors
{
    /// <summary>Brand colours by DISPLAY NAME, which is unique per app (see <see cref="AppNames"/>).</summary>
    private static readonly Dictionary<string, string> Builtin = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Microsoft Edge"] = "#35c1f1",
        ["Microsoft Edge WebView2"] = "#35c1f1",
        ["Google Chrome"] = "#fcd209",
        ["Brave"] = "#fb542b",
        ["Firefox"] = "#ff7139",
        ["VS Code"] = "#007acc",
        ["Visual Studio"] = "#5c2d91",
        ["Windows PowerShell"] = "#4b84e7",
        ["PowerShell 7"] = "#2c5591",
        ["Claude"] = "#d97757",
        ["ChatGPT"] = "#10a37f",
        ["VLC"] = "#f48200",
        ["qBittorrent"] = "#356ebf",
        ["Spotify"] = "#1db954",
        ["Discord"] = "#5865f2",
        ["Telegram"] = "#24a1de",
        ["WhatsApp"] = "#25d366",
        ["Word"] = "#1146ac",
        ["Excel"] = "#107c41",
        ["PowerPoint"] = "#c43e1c",
        ["Outlook"] = "#0078d4",
        ["Notepad"] = "#43afcf",
        ["File Explorer"] = "#ffc928",
        ["Windows Search"] = "#0078d4",
        ["Start Menu"] = "#0a85da",
        ["Windows Shell"] = "#00bbf8",
        ["Windows Settings"] = "#0967b6",
        ["Task Manager"] = "#9cddff",
        ["Microsoft Store"] = "#0669bc",
        ["Snipping Tool"] = "#d83901",
        ["Windows Camera"] = "#9794f2",
        ["Windows Photos"] = "#2678c6",
        ["Google Drive"] = "#0066da",
        ["Android Studio"] = "#3ddc84",
        ["Android Emulator"] = "#4285f4",
        ["DaVinci Resolve"] = "#253c54",
        ["NVIDIA Control Panel"] = "#76b900",
        ["Armoury Crate"] = "#c9ced6",
        ["7-Zip"] = "#ececf1",
        ["Zoom"] = "#2d8cff",
    };

    /// <summary>
    /// For apps with no brand: vivid, mutually distinct, adjacent entries
    /// contrasting (adjacent ranks sit side by side in a stacked band). The
    /// accent violet is absent - it is reserved for UI chrome.
    /// </summary>
    private static readonly string[] Palette =
    [
        "#ec4899", "#22d3ee", "#f59e0b", "#10b981", "#f97316", "#3b82f6",
        "#84cc16", "#fb7185", "#14b8a6", "#eab308", "#d946ef", "#60a5fa",
    ];

    /// <summary>The stacked charts' remainder.</summary>
    public const string Other = "#4b4b55";

    /// <summary>
    /// The local brand list: <c>{ "Display Name": "#rrggbb" }</c>. Missing or
    /// unreadable means none; a bad hex is skipped.
    /// </summary>
    public static Dictionary<string, string> ReadLocal(string? path)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (path is null || !File.Exists(path)) return map;
        try
        {
            var raw = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path).TrimStart('﻿')) ?? [];
            foreach (var (name, hex) in raw)
                if (ColorOverrides.Clean(hex) is { } clean) map[name] = clean;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
        }
        return map;
    }

    public static string? Brand(string name, IReadOnlyDictionary<string, string>? local = null) =>
        local?.GetValueOrDefault(name) ?? Builtin.GetValueOrDefault(name);

    /// <summary>
    /// By rank, brands first. Ranking by all-time time (not a name hash, which
    /// collides) is what keeps an app's colour stable across ranges and pages.
    /// </summary>
    public static Dictionary<string, string> Assign(IEnumerable<string> namesByRank, IReadOnlyDictionary<string, string>? local = null)
    {
        var map = new Dictionary<string, string>();
        var i = 0;
        foreach (var name in namesByRank)
        {
            if (map.ContainsKey(name)) continue;
            if (name == "Other") map[name] = Other;
            else if (Brand(name, local) is { } brand) map[name] = brand;
            else map[name] = Palette[i++ % Palette.Length];
        }
        return map;
    }

    public static string Of(IReadOnlyDictionary<string, string> map, string name) =>
        name == "Other" ? Other : map.GetValueOrDefault(name) ?? Builtin.GetValueOrDefault(name) ?? Other;

    public static bool TryParse(string hex, out (int R, int G, int B) rgb)
    {
        rgb = default;
        if (hex.Length != 7 || hex[0] != '#' || !int.TryParse(hex.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var n)) return false;
        rgb = ((n >> 16) & 255, (n >> 8) & 255, n & 255);
        return true;
    }

    private static double Channel(int c)
    {
        var s = c / 255.0;
        return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
    }

    /// <summary>CIE L*, 0-100.</summary>
    public static double Lightness((int R, int G, int B) c)
    {
        var y = 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
        return y > 0.008856 ? 116 * Math.Cbrt(y) - 16 : 903.3 * y;
    }

    /// <summary>
    /// Heat map step 0-5 for a day against the period's peak, in EVEN steps.
    /// A day of screen time cannot pass 24 hours, so there are no outlier days
    /// an order of magnitude above the rest to make room for (the Data Usage
    /// app skews its steps for exactly those).
    /// </summary>
    public static int HeatStep(long value, long max)
    {
        if (value <= 0 || max <= 0) return 0;
        return Math.Clamp((int)Math.Ceiling((double)value / max * 5), 1, 5);
    }
}
