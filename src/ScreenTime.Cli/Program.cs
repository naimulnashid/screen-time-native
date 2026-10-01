using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using ScreenTime.Core;
using ScreenTime.Core.Collect;
using ScreenTime.Core.Data;
using ScreenTime.Core.Naming;
using ScreenTime.Core.Query;
using ScreenTime.Core.Sample;
using ScreenTime.Core.View;

namespace ScreenTime.Cli;

/// <summary>
/// <c>screentime</c>: the sampler and the ingest by hand, the diagnostics,
/// the import and the demo data. Everything the app does to the database can
/// be done and checked from here.
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        if (args.Length == 0) return Usage();
        var rest = args[1..];
        try
        {
            return args[0] switch
            {
                "sample" => Sample(rest),
                "ingest" => IngestCommand(rest),
                "status" => Status(),
                "stats" => Stats(rest),
                "import-web" => ImportWeb(rest),
                "names" => Names(rest),
                "demo-data" => DemoCommand.Run(rest),
                "where" => Where(),
                _ => Usage(),
            };
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            Console.Error.WriteLine("error: " + ex.Message);
            return 1;
        }
    }

    private static int Usage()
    {
        Console.WriteLine("""
            screentime sample [--out <dir>] [--seconds N] [--no-ingest]
                Run the sampler in this console (Ctrl+C stops it cleanly). With --out,
                a separate folder, so it can run beside the installed one.
            screentime ingest [--keep]
                Fold the sampler's spans into the database now, as Sync now does.
            screentime status
                Is the sampler running, and what is it recording?
            screentime stats [--days N]
                Totals and the top apps.
            screentime import-web --db <web backup .db> [--config <collector.json>] [--logos <dir>]... [--colours <app-colours.json>]
                Copy the web dashboard's laptop history, up to where this sampler began.
            screentime names
                Every recorded identity resolved, and any two apps sharing a display name.
            screentime demo-data <dir>
                An invented history for screenshots. Prints the variables to point the app at it.
            screentime where
                Where the data, the sampler and the logs live.
            """);
        return 2;
    }

    private static bool Flag(string[] args, string name) => args.Contains("--" + name);

    private static string? Arg(string[] args, string name)
    {
        var i = Array.IndexOf(args, "--" + name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static IEnumerable<string> Args(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (args[i] == "--" + name) yield return args[i + 1];
    }

    private static string RequireDataDir() =>
        AppPaths.DataDir ?? throw new InvalidOperationException($"no data folder chosen. Set {AppPaths.DataDirVariable} or run the app once.");

    private static int Where()
    {
        var dir = AppPaths.DataDir;
        Console.WriteLine($"data folder : {dir ?? "(not chosen)"}");
        if (dir is not null)
        {
            Console.WriteLine($"database    : {AppPaths.DatabasePath(dir)}");
            Console.WriteLine($"backup      : {AppPaths.BackupPath(dir)}");
            Console.WriteLine($"logos       : {AppPaths.LogosDir(dir)}");
        }
        Console.WriteLine($"local       : {AppPaths.LocalDir}");
        Console.WriteLine($"sampler     : {AppPaths.SamplerDir}");
        Console.WriteLine($"logs        : {AppPaths.LogsDir}");
        var info = ScheduledTasks.Query(ScheduledTasks.SamplerTask);
        Console.WriteLine($"task        : {ScheduledTasks.SamplerTask}: {(info.Exists ? $"registered, {(info.Running ? "running" : "not running")}, last start {info.LastRunUtc?.ToLocalTime():yyyy-MM-dd HH:mm}" : "NOT registered")}");
        return 0;
    }

    private static int Sample(string[] args)
    {
        using var cancel = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cancel.Cancel();
        };
        var outDir = Arg(args, "out");
        var sampler = new Sampler(new SamplerOptions
        {
            OutDir = outDir ?? AppPaths.SamplerDir,
            RunSeconds = int.TryParse(Arg(args, "seconds"), out var s) ? s : 0,
            // A test folder is not what the database is fed from.
            Ingest = outDir is null && !Flag(args, "no-ingest"),
            Log = m => Console.WriteLine($"{DateTime.Now:HH:mm:ss} {m}"),
            Cancel = cancel.Token,
        });
        return sampler.Run();
    }

    private static int IngestCommand(string[] args)
    {
        var r = Ingest.Run(new IngestOptions { Keep = Flag(args, "keep"), ForceBackup = true });
        Console.WriteLine(r.Describe());
        Console.WriteLine($"files {r.Files}, spans {r.Read}, new segments {r.Inserted}, already present {r.Skipped}, malformed {r.Malformed}, removed {r.Removed}, backup {r.BackupStatus ?? "-"}, {r.DurationMs} ms");
        return r.Status == "success" ? 0 : 1;
    }

    private static int Status()
    {
        var s = SamplerStatus.Read(AppPaths.SamplerDir);
        Console.WriteLine(s.Alive ? $"sampler running, last tick {s.StaleSeconds}s ago, since {s.Started}" : $"sampler NOT running (last heartbeat {s.Updated ?? "never"})");
        if (s.InFlight is { } f) Console.WriteLine($"in flight: {f.Kind} {(f.Kind == "app" ? AppNames.Resolve(f.App).Name : "")} for {Format.Duration(f.Ms)}");
        return s.Alive ? 0 : 1;
    }

    private static int Stats(string[] args)
    {
        var dir = RequireDataDir();
        var days = int.TryParse(Arg(args, "days"), out var n) ? n : Scope.AllDays;
        var q = new UsageQueries(AppPaths.DatabasePath(dir), AppPaths.ColoursPath(dir));
        if (!q.DatabaseExists) throw new InvalidOperationException("no database yet: " + q.DatabasePath);
        var scope = new Scope(days);
        var o = q.Overview(scope);
        var apps = q.ByApp(scope);
        var k = o.Kinds;
        Console.WriteLine($"rows {Format.Count(q.RowCount())}, latest day {o.LatestDate}, {o.DaysWithData} days with data in {scope.Label}");
        Console.WriteLine($"latest {Format.Duration(o.Latest)} | average {Format.Duration(o.DailyAverage)} | range {Format.Duration(o.RangeTotal)}");
        Console.WriteLine($"active {Format.Duration(k.Active)}, locked {Format.Duration(k.Locked)}, unattributed {Format.Duration(k.Unknown)} ({Format.Percent(k.UnknownShare)}), asleep {Format.Duration(k.Gap)}");
        Console.WriteLine($"{apps.Apps.Count} apps, {apps.Apps.Count(a => a.Listed)} listed, {apps.Apps.Count(a => a.Detailed)} with a detail page");
        foreach (var a in apps.Apps.Take(15)) Console.WriteLine($"  {a.Name,-32} {Format.Duration(a.Ms),9} {Format.Percent(a.Share),7} {a.Opens,6} opens {a.Days,4} days");
        foreach (var r in q.Sync(5).Runs) Console.WriteLine($"  run {r.Id} {r.StartedAt} {r.Status} read {r.RowsRead} new {r.RowsInserted} backup {r.BackupStatus} {r.Error}");
        return 0;
    }

    private static int ImportWeb(string[] args)
    {
        var source = Arg(args, "db") ?? throw new ArgumentException("--db <web backup .db> is required");
        var dir = RequireDataDir();
        using var db = UsageDb.Open(AppPaths.DatabasePath(dir), AppPaths.AllowSystemDrive || Flag(args, "allow-system-drive"));
        var sw = Stopwatch.StartNew();
        var r = WebImport.Run(db, source, AppPaths.SamplerDir);
        Console.WriteLine($"segments : {r.Rows} before the cutoff, {r.RowsNew} new here ({r.Rows - r.RowsNew} already present), {r.Clipped} clipped");
        Console.WriteLine($"cutoff   : {r.Cutoff ?? "none - this sampler has not recorded anything yet"}");
        Console.WriteLine($"runs {r.Runs}, renames {r.Renames}, colours {r.Colours} ({sw.ElapsedMilliseconds} ms)");

        if (Arg(args, "config") is { } config)
        {
            var json = JsonNode.Parse(File.ReadAllText(config));
            var settings = DeviceSettings.Load(dir);
            settings.DeviceLabel ??= json?["deviceLabel"]?.GetValue<string>();
            settings.Save(dir);
            Console.WriteLine($"settings : device '{settings.DeviceLabel}'");
        }
        var logos = Args(args, "logos").ToList();
        if (logos.Count > 0) Console.WriteLine($"logos    : {WebImport.CopyLogos(logos, AppPaths.LogosDir(dir))} copied");
        if (Arg(args, "colours") is { } colours)
        {
            var names = new UsageQueries(AppPaths.DatabasePath(dir)).AppNamesByKey().Values.SelectMany(v => new[] { v.Name, v.Base }).Distinct();
            Console.WriteLine($"colours  : {WebImport.CopyColours(colours, names, AppPaths.ColoursPath(dir))} brand colours added to colours.json");
        }

        UsageDb.Checkpoint(db);
        Console.WriteLine($"backup   : {UsageDb.Backup(db, AppPaths.BackupPath(dir))}");
        return 0;
    }

    /// <summary>Every identity resolved, and any display name two keys share (they would share a colour and a logo).</summary>
    private static int Names(string[] args)
    {
        var dir = RequireDataDir();
        using var db = UsageDb.OpenRead(AppPaths.DatabasePath(dir));
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT app_path FROM windows_segments WHERE kind = 'app'";
        var ids = new List<string>();
        using (var r = cmd.ExecuteReader()) while (r.Read()) ids.Add(r.GetString(0));
        var resolved = ids.Select(i => (Id: i, App: AppNames.Resolve(i))).ToList();
        Console.WriteLine($"{ids.Count} recorded identities -> {resolved.Select(r => r.App.Key).Distinct().Count()} apps");
        var clashes = resolved.GroupBy(r => r.App.Name).Where(g => g.Select(r => r.App.Key).Distinct().Count() > 1).ToList();
        foreach (var g in clashes) Console.WriteLine($"  DUPLICATE NAME '{g.Key}': {string.Join(", ", g.Select(r => r.App.Key).Distinct())}");
        if (Flag(args, "list"))
            foreach (var g in resolved.GroupBy(r => r.App.Key).OrderBy(g => g.First().App.Name))
                Console.WriteLine($"  {g.First().App.Name} [{g.Key}]: {string.Join(", ", g.Select(r => r.Id))}");
        return clashes.Count == 0 ? 0 : 1;
    }
}
