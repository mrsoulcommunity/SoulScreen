using System.Windows;
using System.Windows.Threading;
using SoulScreen.Core.Logging;

namespace SoulScreen.App;

public partial class App : Application
{
    private static readonly ILogger Log_ = Log.For("app");

    private FileLogSink? _logFile;
    private SingleInstance? _instance;

    /// <summary>The settings in force. Loaded before the window exists so the theme can be
    /// applied before anything is drawn.</summary>
    public static AppSettings Settings { get; internal set; } = null!;

    /// <summary>True when launched at sign-in: the window opens minimised.</summary>
    public static bool StartMinimised { get; private set; }

    /// <summary>Folder the log file is written to, for the "open log folder" button.</summary>
    public static string? LogDirectory { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        // Attached before anything else so a failure during startup is on disk even if the
        // window never appears.
        _logFile = FileLogSink.Attach(AppSettings.Directory);
        LogDirectory = _logFile?.LogDirectory;

        // An unhandled exception on the UI thread would otherwise kill the window with a
        // stack trace the user never sees; the log panel and a dialog are more use.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log_.Error("unhandled exception", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log_.Error("unobserved task exception", args.Exception);
            args.SetObserved();
        };

        _instance = SingleInstance.TryAcquire();
        if (_instance is null)
        {
            // The running copy has been asked to come to the front; this one has nothing to do.
            Log_.Info("SoulScreen is already running; handing over to it");
            Shutdown(0);
            return;
        }

        Settings = AppSettings.Load();
        StartMinimised = e.Args.Contains(StartupRegistration.MinimisedArgument, StringComparer.OrdinalIgnoreCase);
        Log.MinimumLevel = Settings.TraceProtocol ? LogLevel.Trace : LogLevel.Debug;

        // Before the window is built, so its first frame is already in the right colours.
        ThemeManager.Apply(Settings.Theme, Settings.Accent, animate: false);

        Log_.Info($"SoulScreen starting; log file at {_logFile?.Path ?? "(none)"}");
        base.OnStartup(e);

        var window = new MainWindow();
        MainWindow = window;
        _instance.ListenForActivation(() => Dispatcher.BeginInvoke(window.ActivateFromAnotherInstance));

        if (StartMinimised) window.WindowState = WindowState.Minimized;
        window.Show();
        if (StartMinimised && Settings.MinimizeToTray) window.HideToTray();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log_.Error("unhandled UI exception", e.Exception);

        // Swallowing an exception thrown before the window is up would leave a process
        // running with nothing on screen, so those are fatal.
        var windowIsUp = MainWindow is { IsLoaded: true };
        if (!windowIsUp)
        {
            MessageBox.Show(
                $"SoulScreen could not start.\n\n{e.Exception}",
                "SoulScreen",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            e.Handled = true;
            Shutdown(1);
            return;
        }

        MessageBox.Show(
            $"{e.Exception.Message}\n\nSoulScreen will keep running; the activity log has the details.",
            "SoulScreen hit a problem",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);

        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log_.Info("SoulScreen exiting");
        _instance?.Dispose();
        _logFile?.Dispose();
        base.OnExit(e);
    }
}
