using System.Windows;
using SoulScreen.Core.Sources;

namespace SoulScreen.App;

/// <summary>The notification-area icon and the window's comings and goings through it.</summary>
public partial class MainWindow
{
    private TrayIcon? _tray;
    private WindowState _stateBeforeHide = WindowState.Normal;
    private bool _hiddenToTray;
    private bool _trayHintShown;

    private void InitialiseTray()
    {
        try
        {
            _tray = new TrayIcon();
        }
        catch (Exception ex)
        {
            // A shell without a notification area - some server sessions - is not a reason
            // to refuse to start; the window simply cannot be hidden there.
            _log.Warn("the notification-area icon is unavailable", ex);
            return;
        }

        _tray.ShowRequested += () => Dispatcher.BeginInvoke(RestoreFromTray);
        _tray.ToggleReceiverRequested += () => Dispatcher.BeginInvoke(async () => await ToggleReceiverAsync());
        _tray.DisconnectRequested += () => Dispatcher.BeginInvoke(DisconnectDevice);
        _tray.SettingsRequested += () => Dispatcher.BeginInvoke(() =>
        {
            RestoreFromTray();
            SettingsButton.IsChecked = true;
        });
        _tray.ScreenshotRequested += () => Dispatcher.BeginInvoke(() =>
        {
            if (SaveSnapshot()) NotifyFromTray("Screenshot saved", "It is in Captures.");
        });
        _tray.RecordRequested += () => Dispatcher.BeginInvoke(() =>
        {
            if (RecordButton.IsEnabled) RecordButton.IsChecked = RecordButton.IsChecked != true;
        });
        _tray.MiniPlayerRequested += () => Dispatcher.BeginInvoke(() =>
        {
            RestoreFromTray();
            ToggleMiniPlayer();
        });
        _tray.CapturesRequested += () => Dispatcher.BeginInvoke(() =>
        {
            RestoreFromTray();
            ShowCaptures();
        });
        _tray.QuitRequested += () => Dispatcher.BeginInvoke(() =>
        {
            _quitRequested = true;
            Close();
        });
    }

    /// <summary>Keeps the icon's presence, hover text and menu in step with the app.</summary>
    private void UpdateTray()
    {
        UpdateTaskbar();
        if (_tray is null) return;

        _tray.Visible = _settings.MinimizeToTray || _settings.CloseToTray || _hiddenToTray;

        var source = ActiveSource;
        var status = source?.State switch
        {
            MirrorSourceState.Ready => "waiting for your iPhone",
            MirrorSourceState.Connecting => "connecting",
            MirrorSourceState.Streaming => source.Device?.Name is { } name ? $"mirroring {name}" : "mirroring",
            MirrorSourceState.Faulted => "receiver faulted",
            _ => "receiver stopped",
        };
        _tray.Update(status, source is not null, VideoHost.Visibility == Visibility.Visible, RecordButton.IsChecked == true);
    }

    /// <summary>Takes the window off the screen and the taskbar; the receiver keeps running.</summary>
    public void HideToTray()
    {
        if (_tray is null || _shuttingDown) return;
        if (!_hiddenToTray)
        {
            // The window comes back from the tray as a window, not as a floating player.
            if (_isMiniPlayer) ExitMiniPlayer();
            _stateBeforeHide = WindowState == WindowState.Minimized ? WindowState.Normal : WindowState;
            if (_isFullscreen) ExitFullscreen();
        }
        _hiddenToTray = true;
        _tray.Visible = true;
        Hide();

        if (!_trayHintShown)
        {
            _trayHintShown = true;
            _tray.Notify("SoulScreen is still running", "Your iPhone can still mirror to this PC. Click the icon to bring the window back.");
        }
    }

    private void RestoreFromTray()
    {
        if (_shuttingDown) return;
        if (_hiddenToTray)
        {
            _hiddenToTray = false;
            Show();
            WindowState = _stateBeforeHide;
        }
        else if (WindowState == WindowState.Minimized)
        {
            WindowState = _settings.WindowMaximized ? WindowState.Maximized : WindowState.Normal;
        }

        Activate();
        UpdateTray();
    }

    /// <summary>A second launch of the app has asked for the window. Runs on the UI thread.</summary>
    public void ActivateFromAnotherInstance()
    {
        RestoreFromTray();
        // Activate alone does not always lift a window above the one that had focus;
        // a moment of topmost does, and is undone at once so the setting is not changed.
        if (!Topmost)
        {
            Topmost = true;
            Topmost = false;
        }
        Activate();
        Focus();
    }

    /// <summary>A toast from the notification area, only while the window is out of sight.</summary>
    private void NotifyFromTray(string title, string message)
    {
        if (_tray is null || !_settings.TrayNotifications) return;
        if (IsVisible && WindowState != WindowState.Minimized) return;
        _tray.Notify(title, message);
    }
}
