using ScreenTime.App.State;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace ScreenTime.App;

/// <summary>
/// The dashboard process: the window and its state. The tray icon and the
/// problem notifications are another process (<see cref="TrayProcess"/>),
/// told what it needs over <see cref="TrayLink"/>.
/// </summary>
public partial class App : Application
{
    private static App? _instance;
    private static DispatcherQueue? _ui;

    private MainWindow? _window;
    private AppState? _state;
    private UiSettings _settings = new();
    private string? _dataDir;
    private bool _quitting;

    public App()
    {
        InitializeComponent();
        _instance = this;
    }

    /// <summary>
    /// A second <c>--dashboard</c> launch was redirected here: one of the
    /// tray's requests (<see cref="Program.Requests"/>), or else someone
    /// asking for the window.
    /// </summary>
    internal static void OnRedirected(string? arguments) => _ui?.TryEnqueue(() =>
    {
        if (_instance is not { _window: { } window } app) return;
        var args = (arguments ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        bool Asked(string flag) => args.Contains(flag, StringComparer.OrdinalIgnoreCase);

        if (Asked("--exit")) app.Quit();
        else if (Asked("--reload-settings"))
        {
            // The two settings the tray's menu changes; the rest are this window's own.
            var onDisk = UiSettings.Load();
            app._settings.AutoRefresh = onDisk.AutoRefresh;
            app._settings.NotifyProblems = onDisk.NotifyProblems;
        }
        else
        {
            // A tray started after this window does not know it yet.
            Hello();
            window.ShowAndActivate();
        }
    });

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _ui = DispatcherQueue.GetForCurrentThread();
        _settings = UiSettings.Load();
        _state = new AppState(_ui, _settings);
        _window = new MainWindow(_state, _settings, QuitAll);
        _window.SettingsChanged += () => TrayLink.Send("settings");
        // The window is the process: closing it gives WinUI's memory back.
        // Whether the tray icon stays is the tray's business ("Keep running
        // in the tray when closed").
        _window.Closed += (_, _) => Quit();

        // The tray watches the database of the data folder it knows: tell it
        // when the setup screen chooses one.
        _dataDir = _state.DataDir;
        _state.Changed += () =>
        {
            if (_state.DataDir == _dataDir) return;
            _dataDir = _state.DataDir;
            TrayLink.Send("settings");
        };

        Hello();
        _window.OpenMaximized();
        _ = _state.ReloadAsync();
    }

    /// <summary>Tells the tray this window exists. No tray: a window on its own.</summary>
    private static void Hello() => TrayLink.Send("hello", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>Exit, chosen in the window: the tray goes too.</summary>
    private void QuitAll()
    {
        TrayLink.Send("exit");
        Quit();
    }

    private void Quit()
    {
        // Exit() closes the window, whose Closed comes back here.
        if (_quitting) return;
        _quitting = true;
        _state?.Dispose();
        Exit();
        Environment.Exit(0);
    }
}
