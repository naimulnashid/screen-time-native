namespace ScreenTime.Core.Naming;

/// <summary>A logo file, and whether it needs a light plate behind it.</summary>
public sealed record AppIcon(string Path, bool Plate);

/// <summary>
/// App logos: files dropped into the data folder's <c>logos\</c>, matched to
/// display names by file name, ignoring case AND spaces - <c>VS Code.svg</c>
/// and <c>VSCode.png</c> both find "VS Code", as the web dashboard's
/// <c>logoKey()</c> matched them. An app with no file shows its colour swatch:
/// the normal case, not a gap.
/// </summary>
/// <remarks>
/// <see cref="Aliases"/> exists only for names that cannot match a file: a
/// shared mark, or a file named differently from the app. Which logos need a
/// light plate is measured, not guessed; none of the laptop's do today, so
/// <see cref="PlateStems"/> holds only marks known to be near-black.
/// </remarks>
public static class AppIcons
{
    public static readonly HashSet<string> Renderable = new(StringComparer.OrdinalIgnoreCase)
    {
        ".svg", ".png", ".jpg", ".jpeg", ".webp", ".gif", ".bmp", ".ico",
    };

    public static readonly HashSet<string> PlateStems = new(StringComparer.OrdinalIgnoreCase) { "x", "github", "cursor", "opencode" };

    /// <summary>Display name to file stem.</summary>
    public static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Google Chrome"] = "Chrome",
        ["NVIDIA Control Panel"] = "nvidiacontrolpanel",
        ["Command Prompt"] = "Windows Terminal",
        ["Console Host"] = "Windows Terminal",
    };

    /// <summary>The web dashboard's <c>logoKey()</c>: lowercase, no spaces.</summary>
    public static string Key(string name) => new string(name.Where(c => !char.IsWhiteSpace(c)).ToArray()).ToLowerInvariant();

    /// <summary>
    /// Every name that resolves to a logo, keyed by <see cref="Key"/>. File
    /// stems first, then aliases (a curated pointer overrides a coincidental
    /// stem), then renames: a renamed app with no file of its own keeps its
    /// original's logo, and a file named for the new name still wins.
    /// </summary>
    public static Dictionary<string, AppIcon> Map(string? folder, IEnumerable<(string Name, string Base)>? renamed = null)
    {
        var map = new Dictionary<string, AppIcon>();
        if (folder is null || !Directory.Exists(folder)) return map;

        var byStem = new Dictionary<string, AppIcon>();
        foreach (var file in Directory.EnumerateFiles(folder).Order(StringComparer.OrdinalIgnoreCase))
        {
            if (!Renderable.Contains(System.IO.Path.GetExtension(file))) continue;
            var stem = System.IO.Path.GetFileNameWithoutExtension(file);
            // First wins, so an .svg sorted beside a .png of one name is stable.
            byStem.TryAdd(Key(stem), new AppIcon(file, PlateStems.Contains(stem)));
        }

        foreach (var (stem, icon) in byStem) map[stem] = icon;
        foreach (var (name, stem) in Aliases)
            if (byStem.TryGetValue(Key(stem), out var icon)) map[Key(name)] = icon;
        foreach (var (name, @base) in renamed ?? [])
            if (Key(name) != Key(@base) && !map.ContainsKey(Key(name)) && map.TryGetValue(Key(@base), out var from))
                map[Key(name)] = from;
        return map;
    }

    public static AppIcon? Find(IReadOnlyDictionary<string, AppIcon> map, string name) => map.GetValueOrDefault(Key(name));

    /// <summary>A file name for a logo set from the app: the display name, made safe.</summary>
    public static string FileStemFor(string displayName)
    {
        var invalid = System.IO.Path.GetInvalidFileNameChars();
        var chars = displayName.Select(c => invalid.Contains(c) ? ' ' : c).ToArray();
        return new string(chars).Trim().TrimEnd('.');
    }
}
