using System.Globalization;
using System.Text.RegularExpressions;

namespace ScreenTime.Core.Naming;

/// <summary>One stored identity, resolved: a stable key, what to show, and whether it is part of Windows.</summary>
public sealed record ResolvedApp(string Key, string Name, bool System);

/// <summary>
/// Turning what the sampler recorded into what a person calls the app. A line
/// for line port of the web dashboard's <c>resolveApp</c>, so the same path
/// gets the same key in both - which is what lets their renames, colours and
/// imported history line up.
/// </summary>
/// <remarks>
/// <para>Display names are resolved HERE, in code, and never stored, so these
/// rules can be corrected without re-ingesting a row.</para>
/// <list type="number">
/// <item><b>Versioned install paths split one app into many.</b> A packaged
/// app lives under <c>WindowsApps\Claude_1.40609.0.0_x64__pzs8sxrjxfjjc\</c>;
/// the key is the package family and publisher, which survive an update.</item>
/// <item><b>Generic basenames are keyed by their folder.</b> <c>setup.exe</c>
/// is a different program in every folder it appears in.</item>
/// <item><b>A display name is unique across keys</b>: the colour and logo are
/// looked up by name, so two keys sharing one would share both.</item>
/// <item><b>The fallback stays recognisable</b>: an unknown exe keeps the name
/// Task Manager shows, with word breaks inserted and nothing else.</item>
/// <item><b>A package family is not a display name.</b> <c>OpenAI.Codex</c>
/// ships <c>ChatGPT.exe</c>; names here were read from Windows, not guessed.</item>
/// </list>
/// </remarks>
public static partial class AppNames
{
    private sealed record Known(string Name, bool System = false, string? Key = null);

    /// <summary>Basenames, lowercase and without <c>.exe</c>. Checked BEFORE the packaged branch.</summary>
    private static readonly Dictionary<string, Known> KnownApps = new()
    {
        // Shell and desktop
        ["explorer"] = new("File Explorer", true),
        ["dwm"] = new("Desktop Window Manager", true),
        ["applicationframehost"] = new("Store App (unresolved)", true),
        ["searchhost"] = new("Windows Search", true),
        ["startmenuexperiencehost"] = new("Start Menu", true),
        // Three hosts draw the taskbar's flyouts, and which owns which moves
        // between Windows builds: one surface, one row.
        ["shellexperiencehost"] = new("Windows Shell", true, "exe:shellexperiencehost"),
        ["shellhost"] = new("Windows Shell", true, "exe:shellexperiencehost"),
        ["sihost"] = new("Windows Shell", true, "exe:shellexperiencehost"),
        ["textinputhost"] = new("Text Input", true),
        ["systemsettings"] = new("Windows Settings", true),
        ["taskmgr"] = new("Task Manager", true),
        ["lockapp"] = new("Lock Screen", true),
        ["logonui"] = new("Sign-in Screen", true),
        ["rundll32"] = new("Windows Host Process", true),
        ["pickerhost"] = new("File Picker", true),
        ["mmc"] = new("Microsoft Management Console", true),
        ["gameinputsvc"] = new("Game Input Service", true),
        // The runtime other apps host their UI in: not Edge, and not chosen.
        ["msedgewebview2"] = new("Microsoft Edge WebView2", true),

        // Browsers
        ["msedge"] = new("Microsoft Edge"),
        ["chrome"] = new("Google Chrome"),
        ["brave"] = new("Brave"),
        ["firefox"] = new("Firefox"),

        // Editors and terminals
        ["code"] = new("VS Code"),
        ["devenv"] = new("Visual Studio"),
        ["windowsterminal"] = new("Windows Terminal"),
        ["powershell"] = new("Windows PowerShell"),
        ["pwsh"] = new("PowerShell 7"),
        ["cmd"] = new("Command Prompt", true),
        ["conhost"] = new("Console Host", true),

        // Everyday
        ["claude"] = new("Claude"),
        ["vlc"] = new("VLC"),
        ["qbittorrent"] = new("qBittorrent"),
        ["spotify"] = new("Spotify"),
        ["discord"] = new("Discord"),
        ["telegram"] = new("Telegram"),
        ["whatsapp"] = new("WhatsApp"),
        // One app, two exes: sharing a KEY merges them, sharing only a name would not.
        ["7zg"] = new("7-Zip", Key: "exe:7-zip"),
        ["7zfm"] = new("7-Zip", Key: "exe:7-zip"),
        ["notepad"] = new("Notepad"),
        ["mspaint"] = new("Paint"),
        ["winword"] = new("Word"),
        ["excel"] = new("Excel"),
        ["powerpnt"] = new("PowerPoint"),
        ["outlook"] = new("Outlook"),

        // Store apps, keyed by the exe inside the package.
        ["chatgpt"] = new("ChatGPT"),
        ["armourycrate"] = new("Armoury Crate"),
        ["winstore.app"] = new("Microsoft Store"),
        ["snippingtool"] = new("Snipping Tool"),
        ["windowscamera"] = new("Windows Camera"),
        ["photos"] = new("Windows Photos"),

        // Desktop apps whose exe name is not their name (their FileDescription).
        ["googledrivefs"] = new("Google Drive"),
        ["studio64"] = new("Android Studio"),
        ["resolve"] = new("DaVinci Resolve"),
        ["powertoys.settings"] = new("PowerToys Settings"),
        // The container is recorded as a bare name (its path is refused
        // unelevated), so it is pointed at the Control Panel's package key.
        ["nvcplui"] = new("NVIDIA Control Panel", Key: "appx:nvidiacorp.nvidiacontrolpanel_56jybvy8sckqj"),
        ["nvdisplay.container"] = new("NVIDIA Control Panel", Key: "appx:nvidiacorp.nvidiacontrolpanel_56jybvy8sckqj"),
        ["qemu-system-x86_64"] = new("Android Emulator"),
    };

    /// <summary>Too generic to name an app alone: the parent folder joins the key.</summary>
    private static readonly HashSet<string> Generic =
    [
        "setup", "install", "installer", "update", "updater", "launcher",
        "app", "main", "start", "run", "host", "helper", "service", "client",
    ];

    private static readonly string[] SystemDirs = [@"\windows\", @"\system32\", @"\syswow64\"];

    public const string UnknownKey = "unknown";

    /// <summary>Every known basename's resolution, for the duplicate-name check in <c>screentime names</c> and the tests.</summary>
    public static IEnumerable<(string Base, string Name, string? Key)> KnownEntries() =>
        KnownApps.Select(kv => (kv.Key, kv.Value.Name, kv.Value.Key));

    [GeneratedRegex(@"([a-z0-9])([A-Z])(?=[a-z])")]
    private static partial Regex WordBreak();

    [GeneratedRegex(@"\\windowsapps\\([^\\]+)\\", RegexOptions.IgnoreCase)]
    private static partial Regex WindowsApps();

    [GeneratedRegex(@"[-_]+")]
    private static partial Regex Separators();

    /// <summary>
    /// CamelCase word breaks WITHOUT wrecking acronyms: a break only where a
    /// capital is followed by a lowercase letter, so <c>HWiNFO64</c> survives
    /// while <c>InternetSpeedMeter</c> becomes "Internet Speed Meter".
    /// </summary>
    public static string SplitWords(string s) => WordBreak().Replace(s, "$1 $2");

    private static string TitleCase(string s)
    {
        var spaced = Separators().Replace(s, " ");
        return Regex.Replace(spaced, @"\b\w", m => m.Value.ToUpper(CultureInfo.InvariantCulture));
    }

    private static string Basename(string path)
    {
        var cleaned = path.TrimEnd('\\', '/');
        var last = cleaned.Split('\\', '/').Last();
        return last.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? last[..^4] : last;
    }

    private static string ParentDir(string path)
    {
        var parts = path.Split('\\', '/').Where(p => p.Length > 0).ToArray();
        return parts.Length >= 2 ? parts[^2] : "";
    }

    /// <summary><c>Name_Version_Arch__Publisher</c>: name and publisher survive an update.</summary>
    private static (string Key, string Name)? Packaged(string path)
    {
        var m = WindowsApps().Match(path);
        if (!m.Success) return null;
        var parts = m.Groups[1].Value.Split('_');
        if (parts.Length < 2) return null;
        var family = parts[0];
        var publisher = parts[^1];
        // Everything before the last dot is a publisher namespace, not the name.
        var leaf = family.Split('.').Last();
        if (leaf.Length == 0) leaf = family;
        return ($"appx:{family.ToLowerInvariant()}_{publisher.ToLowerInvariant()}", SplitWords(leaf));
    }

    /// <summary>
    /// Resolve one stored identity: a full path, or a bare process name where
    /// Windows refused the path.
    /// </summary>
    public static ResolvedApp Resolve(string? identity)
    {
        var raw = (identity ?? "").Trim();
        if (raw.Length == 0) return new ResolvedApp(UnknownKey, "Unknown", true);

        var lower = raw.ToLowerInvariant();
        var isPath = raw.Contains('\\') || raw.Contains('/');
        var baseName = Basename(raw).ToLowerInvariant();
        var packaged = isPath ? Packaged(raw) : null;
        var inSystemDir = isPath && SystemDirs.Any(lower.Contains);

        if (KnownApps.TryGetValue(baseName, out var known))
            return new ResolvedApp(known.Key ?? packaged?.Key ?? $"exe:{baseName}", known.Name, known.System);

        if (packaged is { } p) return new ResolvedApp(p.Key, p.Name, false);

        if (Generic.Contains(baseName) && isPath)
        {
            var dir = ParentDir(raw);
            return new ResolvedApp($"exe:{dir.ToLowerInvariant()}/{baseName}", dir.Length > 0 ? $"{TitleCase(dir)} ({baseName}.exe)" : $"{baseName}.exe", inSystemDir);
        }

        // An installer unpacked into TEMP runs under a random name; keep it,
        // and say what kind of thing it was.
        if (baseName.EndsWith(".tmp", StringComparison.Ordinal))
            return new ResolvedApp($"exe:{baseName}", $"Installer ({Basename(raw)})", false);

        return new ResolvedApp($"exe:{baseName}", SplitWords(Basename(raw)), inSystemDir);
    }
}
