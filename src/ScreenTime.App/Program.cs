using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace ScreenTime.App;

/// <summary>
/// A custom entry point, for two reasons.
/// <list type="bullet">
/// <item><b>The sampler.</b> The logon task runs this same exe with
/// <c>--sample</c>, and it runs for the whole session. That path returns
/// before WinUI starts, so it opens no window - the exe is a WinExe, so there
/// is no console to flash either, and no wrapper script is needed. (The web
/// dashboard needed a .vbs launcher for exactly that.)</item>
/// <item><b>A single instance.</b> The app lives in the tray and can start at
/// login, so a second launch must bring the running copy forward.</item>
/// </list>
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains("--sample", StringComparer.OrdinalIgnoreCase)) return Sample();

        WinRT.ComWrappersSupport.InitializeComWrappers();
        var instance = AppInstance.FindOrRegisterForKey(InstanceKey());
        if (!instance.IsCurrent)
        {
            var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
            instance.RedirectActivationToAsync(activation).AsTask().Wait();
            return 0;
        }

        instance.Activated += (_, _) => App.OnRedirected();

        Application.Start(callback =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });
        return 0;
    }

    /// <summary>
    /// The headless sampler, until the session ends or a stop file asks it to
    /// leave. Its log is the only place a problem here can be seen, so it goes
    /// to <c>logs\sampler.log</c>, started afresh past a megabyte.
    /// </summary>
    private static int Sample()
    {
        var log = Path.Combine(Core.AppPaths.LogsDir, "sampler.log");
        void Log(string message)
        {
            try
            {
                Directory.CreateDirectory(Core.AppPaths.LogsDir);
                if (File.Exists(log) && new FileInfo(log).Length > 1 << 20) File.Move(log, log + ".old", overwrite: true);
                File.AppendAllText(log, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A log that cannot be written is not worth stopping the recording.
            }
        }
        try
        {
            return new Core.Sample.Sampler(new Core.Sample.SamplerOptions { Log = Log }).Run();
        }
        catch (Exception ex)
        {
            Log("sampler crashed: " + ex);
            return 1;
        }
    }

    /// <summary>
    /// One instance per data folder, not per machine. A copy pointed elsewhere
    /// by SCREENTIME_DATA_DIR - the demo, a screenshot run - starts beside the
    /// one in the tray instead of handing its launch to it.
    /// </summary>
    private static string InstanceKey()
    {
        if (Environment.GetEnvironmentVariable(Core.AppPaths.DataDirVariable) is not { Length: > 0 } dir) return "ScreenTimeNative.Main";
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(dir).ToUpperInvariant()));
        return "ScreenTimeNative." + Convert.ToHexString(hash, 0, 8);
    }
}
