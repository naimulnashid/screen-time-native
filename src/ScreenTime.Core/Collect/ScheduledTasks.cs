using System.Runtime.InteropServices;

namespace ScreenTime.Core.Collect;

/// <summary>What Task Scheduler knows about one task.</summary>
public sealed record TaskInfo(bool Exists, bool Running, DateTime? LastRunUtc, int LastResult, DateTime? NextRunUtc);

/// <summary>
/// The app's one scheduled task, through Task Scheduler's own COM API rather
/// than by parsing schtasks output.
/// </summary>
/// <remarks>
/// There is nothing elevated to split off: reading this session's foreground
/// window needs no privilege at all, so the sampler is an ordinary per-logon
/// task and the installer asks for no administrator approval.
/// </remarks>
public static class ScheduledTasks
{
    public const string SamplerTask = "Screen Time Native Sampler";

    private const int TaskStateRunning = 4;

    private static dynamic Folder()
    {
        var type = Type.GetTypeFromProgID("Schedule.Service") ?? throw new InvalidOperationException("Task Scheduler is not available");
        dynamic service = Activator.CreateInstance(type)!;
        service.Connect();
        return service.GetFolder(@"\");
    }

    private static dynamic? Find(string name)
    {
        try { return Folder().GetTask(name); }
        catch (COMException) { return null; }
        catch (FileNotFoundException) { return null; }
    }

    public static TaskInfo Query(string name)
    {
        var task = Find(name);
        if (task is null) return new TaskInfo(false, false, null, 0, null);
        DateTime? Time(DateTime t) => t.Year < 2000 ? null : t.ToUniversalTime();
        return new TaskInfo(true, (int)task.State == TaskStateRunning, Time((DateTime)task.LastRunTime), (int)task.LastTaskResult, Time((DateTime)task.NextRunTime));
    }

    /// <summary>Start a registered task now.</summary>
    public static void Run(string name)
    {
        var task = Find(name) ?? throw new InvalidOperationException($"The '{name}' task is not registered. Run tools\\Install.ps1 to register it.");
        task.Run(null);
    }
}
