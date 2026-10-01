using ScreenTime.App.State;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace ScreenTime.App;

public partial class App : Application
{
    private static App? _instance;
    private static DispatcherQueue? _ui;

    private MainWindow? _window;
    private AppState? _state;
    private TrayHost? _tray;
    private DispatcherQueueTimer? _health;
    private UiSettings _settings = new();

    public App()
    {
        InitializeComponent();
        _instance = this;
    }

    /// <summary>A second launch was redirected here: bring the window back.</summary>
    internal static void OnRedirected() => _ui?.TryEnqueue(() => _instance?._window?.ShowAndActivate());

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _ui = DispatcherQueue.GetForCurrentThread();
        _settings = UiSettings.Load();
        _state = new AppState(_ui, _settings);
        _window = new MainWindow(_state, _settings, Quit);

        _tray = new TrayHost(_state, _settings, () => _window.ShowAndActivate(), () => _ = _window.SyncNowAsync(), Quit);
        _window.SettingsChanged += () => _tray.SyncChecks(_settings);
        _window.ClosedToTray += () =>
        {
            if (_settings.TrayNoteShown) return;
            _settings.TrayNoteShown = true;
            _settings.Save();
            _tray.Notify("Screen Time is still running", "It keeps its numbers up to date from the notification area. Right-click the icon to exit. Recording carries on either way: the sampler is its own process.");
        };
        _state.Problem += (title, message) => _tray.Notify(title, message, warning: true);

        // A stall can begin with no write at all, so health is also checked on a clock.
        _health = _ui.CreateTimer();
        _health.Interval = TimeSpan.FromMinutes(10);
        _health.Tick += (_, _) => _state.CheckHealthNow();
        _health.Start();

        // Launched by the Run key at login: straight to the tray.
        var startInTray = Environment.GetCommandLineArgs().Contains("--tray", StringComparer.OrdinalIgnoreCase);
        if (startInTray) _window.EnterTray();
        else _window.OpenMaximized();

        _ = _state.ReloadAsync();
    }

    private void Quit()
    {
        _health?.Stop();
        _state?.Dispose();
        _tray?.Dispose();
        Exit();
        Environment.Exit(0);
    }
}
