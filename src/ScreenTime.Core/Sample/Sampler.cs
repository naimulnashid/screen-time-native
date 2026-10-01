using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ScreenTime.Core.Collect;
using ScreenTime.Core.Data;

namespace ScreenTime.Core.Sample;

public sealed class SamplerOptions
{
    /// <summary>
    /// Polling interval. Every boundary is quantised to it, and a session
    /// shorter than it can be missed. 2 s costs nothing measurable.
    /// </summary>
    public int IntervalSeconds { get; init; } = 2;

    /// <summary>
    /// A wall-clock jump this large means the process was frozen: the machine
    /// slept. Comfortably above the interval, or scheduling jitter reads as sleep.
    /// </summary>
    public int GapSeconds { get; init; } = 30;

    /// <summary>Where the JSONL and the heartbeat go.</summary>
    public string OutDir { get; init; } = AppPaths.SamplerDir;

    /// <summary>Stop after this long; 0 runs until stopped.</summary>
    public int RunSeconds { get; init; }

    /// <summary>How often completed spans are folded into the database.</summary>
    public TimeSpan IngestEvery { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>Fold spans into the database at all (off for a test run with its own folder).</summary>
    public bool Ingest { get; init; } = true;

    public Action<string>? Log { get; init; }

    public CancellationToken Cancel { get; init; }
}

/// <summary>
/// The foreground sampler: <c>ScreenTime.exe --sample</c>, started at logon by
/// the one scheduled task, unelevated. A C# port of the web dashboard's
/// <c>sampler.ps1</c>, keeping every one of its measured rules.
/// </summary>
/// <remarks>
/// <para><b>What it writes.</b> Append-only JSONL, one completed span per line,
/// one file per local day, in the local folder - and a heartbeat carrying the
/// span in flight. Every line is a complete span, so a kill loses at most the
/// one in flight, and the next start recovers even that from the heartbeat.
/// Every <see cref="SamplerOptions.IngestEvery"/> it folds the files into the
/// database, opening it only for that moment: a writer holding a SQLite handle
/// for weeks across WAL checkpoints is how a reader ends up stale.</para>
/// <para><b>The rules, each found by running the PowerShell one.</b></para>
/// <list type="number">
/// <item><b>Sleep makes the clock jump.</b> A tick that finds the wall clock
/// moved more than <see cref="SamplerOptions.GapSeconds"/> closes the span at
/// the LAST GOOD SAMPLE and writes a <c>gap</c>, rather than stretching the
/// span over the sleep.</item>
/// <item><b>Lock state comes first</b>, from WTS. A null foreground window is
/// NOT a lock (it happens while a window closes), and a locked machine whose
/// screen is off has no foreground window at all. Only 0 means locked; -1 and
/// -2 fall through, because a false lock is subtracted from active time and
/// under-reports invisibly.</item>
/// <item><b>Store apps all look like ApplicationFrameHost</b>, so the frame's
/// child windows are walked; a lookup that still lands on the host is
/// written as <c>unresolved</c>, so the gap is measurable.</item>
/// <item><b>Window titles are never captured.</b> A title carries the document,
/// the page, the person. This records which app and for how long.</item>
/// <item><b>A logoff is a kill.</b> No <c>finally</c> runs, so the heartbeat's
/// <c>closed</c> stays false, and the next start writes the dead run's
/// in-flight span up to its last sample and a <c>gap</c> from there.</item>
/// <item><b>One sampler per folder</b>, by named mutex - two would double-count
/// every span. Named from a hash of the folder, so a test run with its own
/// folder works beside the real one. Not the web sampler's name: the two
/// are meant to run side by side.</item>
/// </list>
/// </remarks>
public sealed class Sampler
{
    private static readonly HashSet<string> LockProcesses = new(StringComparer.OrdinalIgnoreCase) { "lockapp", "logonui", "consent", "credentialuihost" };

    private readonly SamplerOptions _o;
    private readonly string _stopPath;
    private readonly object _write = new();
    private static readonly JsonSerializerOptions Json = new();

    public Sampler(SamplerOptions options)
    {
        _o = options;
        _stopPath = Path.Combine(options.OutDir, StopFile);
    }

    public const string StopFile = "sampler.stop";

    /// <summary>Written once, on the first start ever: what an import must stop before.</summary>
    public const string FirstStartFile = "first-start.txt";

    private record struct Target(string Kind, string App, bool Unresolved);

    /// <summary>
    /// Runs until stopped. Returns 0, or 2 when another sampler already holds
    /// this folder (which is not a failure: that one is recording).
    /// </summary>
    public int Run()
    {
        var outDir = Path.GetFullPath(_o.OutDir);
        Directory.CreateDirectory(outDir);
        using var mutex = new Mutex(false, MutexName(outDir));
        bool owned;
        try { owned = mutex.WaitOne(0); }
        catch (AbandonedMutexException) { owned = true; }
        if (!owned)
        {
            Log($"another sampler is already recording to {outDir} - exiting");
            return 2;
        }

        try
        {
            if (File.Exists(_stopPath)) File.Delete(_stopPath);
            var firstStart = Path.Combine(outDir, FirstStartFile);
            if (!File.Exists(firstStart)) File.WriteAllText(firstStart, Time.Iso(DateTime.UtcNow));

            Log($"sampler started: out {outDir}, every {_o.IntervalSeconds}s, gap at {_o.GapSeconds}s, titles NOT captured");
            Loop(outDir);
            return 0;
        }
        finally
        {
            mutex.ReleaseMutex();
        }
    }

    public static string MutexName(string outDir)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(outDir).TrimEnd('\\').ToLowerInvariant()));
        return "Local\\ScreenTimeNative.Sampler-" + Convert.ToHexString(hash, 0, 8);
    }

    private void Loop(string outDir)
    {
        var current = CurrentTarget();
        var currentStart = DateTime.UtcNow;
        var lastSample = currentStart;
        var started = currentStart;
        var nextIngest = currentStart.AddSeconds(20);
        Task? ingest = null;

        // Before the first tick, so the gap ends exactly where this run begins;
        // the heartbeat is rewritten straight after, so a run that dies in its
        // first seconds does not leave the OLD run to be recovered twice.
        RecoverUnclosed(outDir, currentStart);
        Beat(outDir, currentStart, current, currentStart, started, closed: false);

        try
        {
            while (!_o.Cancel.IsCancellationRequested)
            {
                if (_o.Cancel.WaitHandle.WaitOne(TimeSpan.FromSeconds(_o.IntervalSeconds))) break;
                var now = DateTime.UtcNow;

                // 1. Did the machine sleep, or were we frozen?
                if ((now - lastSample).TotalSeconds > _o.GapSeconds)
                {
                    WriteSpan(outDir, currentStart, lastSample, current, 0);
                    WriteSpan(outDir, lastSample, now, new Target("gap", "", false), 0);
                    current = CurrentTarget();
                    currentStart = now;
                    lastSample = now;
                    Beat(outDir, now, current, currentStart, started, closed: false);
                    continue;
                }

                // 2. Did the foreground change?
                var target = CurrentTarget();
                if (target.Kind != current.Kind || target.App != current.App)
                {
                    WriteSpan(outDir, currentStart, now, current, Win32.IdleMs());
                    current = target;
                    currentStart = now;
                }

                lastSample = now;
                Beat(outDir, now, current, currentStart, started, closed: false);

                if (_o.Ingest && now >= nextIngest && ingest is not { IsCompleted: false })
                {
                    nextIngest = now + _o.IngestEvery;
                    // Off the sampling thread: a backup on a slow drive must not
                    // stretch a tick past the gap threshold and fake a sleep.
                    ingest = Task.Run(() => IngestNow(outDir));
                }

                if (_o.RunSeconds > 0 && (now - started).TotalSeconds >= _o.RunSeconds) break;
                // A stop request leaves the loop, so the in-flight span is written
                // rather than lost to a kill.
                if (File.Exists(_stopPath))
                {
                    TryDelete(_stopPath);
                    Log("stop requested");
                    break;
                }
            }
        }
        finally
        {
            var now = DateTime.UtcNow;
            WriteSpan(outDir, currentStart, now, current, Win32.IdleMs());
            // Only now, with the span on disk, may the heartbeat say closed.
            Beat(outDir, now, current, currentStart, started, closed: true);
            ingest?.Wait(TimeSpan.FromSeconds(30));
            if (_o.Ingest) IngestNow(outDir);
            Log("sampler stopped");
        }
    }

    private void IngestNow(string outDir)
    {
        try
        {
            var result = Ingest.Run(new IngestOptions { SamplerDir = outDir, ReadLock = _write });
            if (result.Note is not null) Log("ingest: " + result.Note);
            else if (result.Status != "success") Log("ingest failed: " + result.Error);
        }
        catch (Exception ex)
        {
            // Never let the database stop the recording: the spans wait on disk.
            Log("ingest failed: " + ex.Message);
        }
    }

    private static Target CurrentTarget()
    {
        if (Win32.SessionLockState() == 0) return new Target("locked", "", false);

        var hwnd = Win32.GetForegroundWindow();
        // Not a lock: nothing holds focus while a window closes or the desktop
        // switches. 'unknown' is visible in the data; a false lock is not.
        if (hwnd == IntPtr.Zero) return new Target("unknown", "", false);
        var pid = Win32.RealProcessId(hwnd);
        if (pid == 0) return new Target("unknown", "", false);

        var path = Win32.ImagePath(pid);
        string name;
        if (path is not null) name = Path.GetFileNameWithoutExtension(path);
        else
        {
            try
            {
                using var p = Process.GetProcessById((int)pid);
                name = p.ProcessName;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                return new Target("unknown", "", false);
            }
        }

        if (LockProcesses.Contains(name)) return new Target("locked", "", false);
        var unresolved = string.Equals(name, "ApplicationFrameHost", StringComparison.OrdinalIgnoreCase);
        // A path Windows refused still leaves the process NAME, which is what
        // Task Manager shows: a partial answer, not a failure.
        return new Target("app", path ?? name, unresolved);
    }

    public static string DayFile(string outDir, DateTime local) => Path.Combine(outDir, $"sessions-{local:yyyy-MM-dd}.jsonl");

    private void WriteSpan(string outDir, DateTime start, DateTime end, Target t, long idleMs)
    {
        var ms = (long)Math.Round((end - start).TotalMilliseconds);
        if (ms <= 0) return;
        var span = new Span { Start = Time.Iso(start), End = Time.Iso(end), Ms = ms, Kind = t.Kind, App = t.App, IdleMs = idleMs, Unresolved = t.Unresolved };
        var line = JsonSerializer.Serialize(span, Json) + "\n";
        // No BOM, ever: one at the head of a file breaks its first JSON line.
        lock (_write) File.AppendAllText(DayFile(outDir, DateTime.Now), line, new UTF8Encoding(false));
    }

    private void Beat(string outDir, DateTime now, Target t, DateTime currentStart, DateTime started, bool closed)
    {
        try
        {
            new Heartbeat
            {
                Updated = Time.Iso(now),
                Started = Time.Iso(started),
                IntervalSeconds = _o.IntervalSeconds,
                Closed = closed,
                InFlight = new InFlight
                {
                    Start = Time.Iso(currentStart),
                    Kind = t.Kind,
                    App = t.App,
                    Unresolved = t.Unresolved,
                    Ms = (long)Math.Round((now - currentStart).TotalMilliseconds),
                },
            }.Write(outDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A reader holding the file a moment too long; the next tick rewrites it.
        }
    }

    /// <summary>
    /// The previous run died without closing: write its in-flight span up to
    /// its last sample, and a gap from there to now. Re-running is harmless -
    /// the recovered spans are the same every time and the ingest ignores
    /// repeats. A failure here must never stop sampling.
    /// </summary>
    private void RecoverUnclosed(string outDir, DateTime now)
    {
        try
        {
            var hb = Heartbeat.Read(outDir);
            if (hb is null || hb.Closed || !Time.TryParse(hb.Updated, out var last)) return;
            if (last > now)
            {
                Log("recovery skipped: the previous heartbeat is in the future (the clock moved back)");
                return;
            }
            if (hb.InFlight is { } f && Time.TryParse(f.Start, out var start))
                WriteSpan(outDir, start, last, new Target(f.Kind, f.App, f.Unresolved), 0);
            WriteSpan(outDir, last, now, new Target("gap", "", false), 0);
            Log($"recovery: the last run stopped without closing at {hb.Updated}; wrote its last span and a {(now - last).TotalSeconds:N0}s gap");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log("recovery skipped: " + ex.Message);
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private void Log(string message) => _o.Log?.Invoke(message);

    /// <summary>
    /// Asks a running sampler to stop cleanly, writing its in-flight span -
    /// unlike killing it, which loses that span until the next start recovers it.
    /// </summary>
    public static void RequestStop(string outDir) => File.WriteAllText(Path.Combine(outDir, StopFile), "");
}
