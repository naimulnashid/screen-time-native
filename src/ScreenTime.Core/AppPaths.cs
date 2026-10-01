using System.Text.Json;
using System.Text.Json.Nodes;

namespace ScreenTime.Core;

/// <summary>
/// Where everything lives. Two places, on purpose:
/// <list type="bullet">
/// <item><b>The data folder</b> (default <c>D:\PersistentData\screen-time-native</c>)
/// holds what must survive a Windows reset: the database, its backup, logos
/// and settings. It must not be on the system drive - that is the whole point.</item>
/// <item><b>The local folder</b> (<c>%LOCALAPPDATA%\Screen Time Native</c>) holds
/// what is cheap to lose: UI preferences, the pointer to the data folder, logs
/// and the sampler's working files. The sampler rewrites its heartbeat every
/// two seconds, so it must stay out of the synced data folder: a sync client
/// would upload it all day.</item>
/// </list>
/// </summary>
public static class AppPaths
{
    public const string DataDirVariable = "SCREENTIME_DATA_DIR";

    /// <summary>What the installer offers, and what this machine uses.</summary>
    public const string DefaultDataDir = @"D:\PersistentData\screen-time-native";

    public static string LocalDir { get; } = Environment.GetEnvironmentVariable("SCREENTIME_LOCAL_DIR") is { Length: > 0 } local
        ? local
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Screen Time Native");

    public static string LocationFile => Path.Combine(LocalDir, "location.json");

    public static string LogsDir => Path.Combine(LocalDir, "logs");

    /// <summary>
    /// The sampler's spans (one JSONL file per local day) and its heartbeat.
    /// Spans wait here until the next ingest folds them into the database, so
    /// a reset loses at most the last quarter of an hour.
    /// </summary>
    public static string SamplerDir => Path.Combine(LocalDir, "sampler");

    /// <summary>
    /// The data folder, or null when none has been chosen yet (a fresh install
    /// before its first run, or after a reset). <see cref="DataDirVariable"/>
    /// overrides it, which the demo and screenshot runs rely on.
    /// </summary>
    public static string? DataDir => Environment.GetEnvironmentVariable(DataDirVariable) is { Length: > 0 } env
        ? Path.GetFullPath(env)
        : ReadLocation().DataDir;

    public static string DatabasePath(string dataDir) => Path.Combine(dataDir, "live", "screen-time.db");

    /// <summary>
    /// The restore source. Written with SQLite's backup API, so it is one
    /// internally consistent file - unlike the live database, whose WAL
    /// sidecars a sync client can upload out of step with it.
    /// </summary>
    public static string BackupPath(string dataDir) => Path.Combine(dataDir, "screen-time.db");

    public static string LogosDir(string dataDir) => Path.Combine(dataDir, "logos");

    public static string SettingsPath(string dataDir) => Path.Combine(dataDir, "settings.json");

    /// <summary>
    /// Brand colours this machine knows beyond the built-in ones, display name
    /// to hex. Local, like the logos: together they are an inventory of the
    /// apps on this machine, which is nobody else's business.
    /// </summary>
    public static string ColoursPath(string dataDir) => Path.Combine(dataDir, "colours.json");

    public sealed record Location(string? DataDir, bool AllowSystemDrive);

    public static Location ReadLocation()
    {
        try
        {
            if (!File.Exists(LocationFile)) return new(null, false);
            var node = JsonNode.Parse(File.ReadAllText(LocationFile));
            var dir = node?["dataDir"]?.GetValue<string>();
            var allow = node?["allowSystemDrive"]?.GetValue<bool>() ?? false;
            return new(string.IsNullOrWhiteSpace(dir) ? null : dir, allow);
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or UnauthorizedAccessException)
        {
            return new(null, false);
        }
    }

    /// <summary>
    /// Whether the database may sit on the system drive: the setup screen's
    /// deliberate tick, or a demo or test run pointed elsewhere by the variable.
    /// </summary>
    public static bool AllowSystemDrive => ReadLocation().AllowSystemDrive || Environment.GetEnvironmentVariable(DataDirVariable) is { Length: > 0 };

    public static void WriteLocation(string dataDir, bool allowSystemDrive)
    {
        Directory.CreateDirectory(LocalDir);
        var json = new JsonObject { ["dataDir"] = Path.GetFullPath(dataDir), ["allowSystemDrive"] = allowSystemDrive };
        File.WriteAllText(LocationFile, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>True when <paramref name="path"/> is on the drive Windows is installed on.</summary>
    public static bool IsOnSystemDrive(string path)
    {
        var system = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) ?? @"C:\";
        return string.Equals(Path.GetPathRoot(Path.GetFullPath(path)), system, StringComparison.OrdinalIgnoreCase);
    }
}
