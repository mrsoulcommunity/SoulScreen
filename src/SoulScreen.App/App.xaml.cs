using System.Windows;
using System.Windows.Threading;
using SoulScreen.Core.Logging;

namespace SoulScreen.App;

public partial class App : Application
{
    private static readonly ILogger Log_ = Log.For("app");

    private FileLogSink? _logFile;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Attached before anything else so a failure during startup is on disk even if the
        // window never appears.
        _logFile = FileLogSink.Attach(AppSettings.Directory);

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

        Log_.Info($"SoulScreen starting; log file at {_logFile?.Path ?? "(none)"}");
        base.OnStartup(e);
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
        _logFile?.Dispose();
        base.OnExit(e);
    }
}
