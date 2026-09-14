using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using SoulScreen.AirPlay;
using SoulScreen.AirPlay.FairPlay;
using SoulScreen.Core.Logging;
using SoulScreen.Core.Media;
using SoulScreen.Core.Sources;
using SoulScreen.Media;

namespace SoulScreen.App;

/// <summary>
/// The one window. Split across partial files by concern: chrome and fullscreen, the
/// picture, settings, the log, the tray, and the metrics tick. This file holds the
/// lifecycle: startup, the receiver, the idle/streaming panels, and shutdown.
/// </summary>
public partial class MainWindow : Window
{
    private readonly ILogger _log = Log.For("ui");
    private readonly ObservableCollection<WarningItem> _warnings = [];
    private readonly DispatcherTimer _metricsTimer;
    private readonly DispatcherTimer _toastTimer;

    private AppSettings _settings;
    private AirPlayReceiver? _receiver;
    private DemoSource? _demo;
    private VideoPipeline? _pipeline;
    private AudioPipeline? _audio;

    /// <summary>Whatever is feeding the picture: the receiver, or the demo pattern.</summary>
    private IMirrorSource? ActiveSource => _receiver is not null ? _receiver : _demo;

    /// <summary>True while the demo is what is on screen, so it is kept out of the history.</summary>
    private bool _sessionIsDemo;

    private bool _shuttingDown;

    /// <summary>Set by the tray's Quit and by a real close; a close with "close to tray" on
    /// only hides the window otherwise.</summary>
    private bool _quitRequested;

    /// <summary>
    /// True between the start and end of a receiver start or stop. Starting is a long
    /// await - it binds an mDNS port and advertises before returning - and a second press
    /// inside that window would tear down a half-built receiver and desync the toolbar
    /// from reality, so the buttons simply refuse while one is under way.
    /// </summary>
    private bool _receiverBusy;

    /// <summary>The last idle state shown. A faulted start is followed by the receiver's
    /// own cleanup reporting Stopped; only a deliberate new session may move past the
    /// error text it just put up.</summary>
    private MirrorSourceState? _stateShown;

    /// <summary>When the session now on screen began, for the timer and the history.</summary>
    private DateTime? _sessionStartedUtc;
    private SourceDeviceInfo? _sessionDevice;

    public MainWindow()
    {
        _settings = App.Settings ?? AppSettings.Load();
        InitializeComponent();

        RestoreWindowBounds();
        InitialiseChrome();
        InitialiseVideo();
        InitialiseTray();
        InitialiseLog();
        InitialiseShortcutList();

        WarningList.ItemsSource = _warnings;

        _metricsTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(500),
        };
        _metricsTimer.Tick += (_, _) =>
        {
            UpdateMetrics();
            RefreshLogPanel();
        };

        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2800) };
        _toastTimer.Tick += (_, _) => HideToast();

        Closing += OnClosing;
        ThemeManager.Changed += OnThemeApplied;

        // Started from the dispatcher rather than from Loaded: a window hidden to the tray
        // straight after launch may never raise Loaded, and one shown again later must not
        // start a second receiver.
        Dispatcher.BeginInvoke(InitialiseAsync, DispatcherPriority.Loaded);
    }

    // ------------------------------------------------------------------ startup

    private async Task InitialiseAsync()
    {
        LogRenderCapability();
        ApplySettingsToChrome();
        ApplyPictureSettings();
        RefreshNetworkLine();
        RefreshWarnings();
        DemoLink.Visibility = DemoSource.IsAvailable ? Visibility.Visible : Visibility.Collapsed;
        _metricsTimer.Start();

        if (_settings.StartReceiverOnLaunch)
        {
            // Same gate as the toolbar: a click on the toggle during this start would
            // otherwise tear down what is only half built.
            if (TryBeginReceiverWork())
            {
                try { await StartReceiverAsync(); }
                finally { EndReceiverWork(); }
            }
        }
        else
        {
            SetIdleState("Receiver stopped", "Start it when you are ready to mirror.", MirrorSourceState.Stopped);
        }
    }

    /// <summary>
    /// Records how WPF will composite. Tier 0 means everything is drawn on the CPU - over a
    /// remote desktop, or with no usable GPU driver - and no amount of work on this side
    /// will make a 60 fps mirror smooth in that state, so it is worth knowing up front.
    /// </summary>
    private void LogRenderCapability()
    {
        var tier = RenderCapability.Tier >> 16;
        var description = tier switch
        {
            0 => "software rendering - the picture will not be smooth",
            1 => "partial hardware acceleration",
            _ => "full hardware acceleration",
        };
        _log.Info($"render tier {tier}: {description}");

        if (tier == 0)
        {
            _warnings.Add(new WarningItem(
                "Windows is compositing this window in software",
                "Usually a remote desktop session or a missing graphics driver. Mirroring will " +
                "work but the picture will stutter.",
                WarningKind.Machine));
        }
    }

    private void ApplySettingsToChrome()
    {
        Title = $"SoulScreen - {_settings.DeviceName}";
        ReceiverName.Text = _settings.DeviceName;
        IdleReceiverName.Text = _settings.DeviceName;
        PinButton.IsChecked = _settings.AlwaysOnTop;
        Topmost = _settings.AlwaysOnTop;
        // Restores the persisted mute. The Checked handler fires, finds no audio pipeline
        // yet, and re-saves the same value - harmless, and it keeps checkbox, pipeline and
        // file in step from the first frame.
        MuteButton.IsChecked = _settings.Muted;
        StatsButton.IsChecked = _settings.ShowStats;
        UpdateTray();
    }

    private void OnThemeApplied()
    {
        WindowFrame.SetDarkFrame(this, ThemeManager.IsDark);
    }

    // ---------------------------------------------------------------- receiver

    /// <summary>
    /// One-shot gate for the long receiver transitions. The check and the set happen with
    /// no await between them, which is what makes it safe: two clicks racing through an
    /// awaited check would both get through.
    /// </summary>
    private bool TryBeginReceiverWork()
    {
        if (_receiverBusy) return false;
        _receiverBusy = true;
        ReceiverToggle.IsEnabled = false;
        return true;
    }

    private void EndReceiverWork()
    {
        _receiverBusy = false;
        ReceiverToggle.IsEnabled = true;
    }

    private async Task StartReceiverAsync()
    {
        await StopReceiverAsync();

        try
        {
            _pipeline = new VideoPipeline { RecordAudio = _settings.RecordAudio && _settings.EnableAudio };
            _pipeline.FrameDecoded += OnFrameDecoded;
            _pipeline.RecordingFinished += OnRecordingFinished;
        }
        catch (FFmpegUnavailableException ex)
        {
            // Without a decoder the receiver is still worth running: the handshake can be
            // verified and the protocol log is useful even with no picture.
            _log.Warn(ex.Message);
            _pipeline = null;
        }

        if (_settings.EnableAudio && FFmpegRuntime.IsAvailable)
        {
            // Both restore from settings, not from the checkbox: the checkbox is the
            // display of the state, and the file is what survives a restart.
            _audio = new AudioPipeline
            {
                Muted = MuteButton.IsChecked == true,
                Volume = (float)_settings.Volume,
                OutputDeviceId = _settings.AudioOutputDeviceId,
                Reserve = AppSettings.AudioReserveFor(AppSettings.PresentationDelayFor(_settings.Latency)),
            };
            _audio.FormatChanged += (_, format) => Dispatcher.BeginInvoke(() => _log.Info($"audio: {format}"));
        }

        _receiver = new AirPlayReceiver(_settings.ToAirPlayOptions());
        _receiver.StateChanged += OnReceiverStateChanged;
        _receiver.VideoFormatChanged += OnVideoFormatChanged;
        _pipeline?.Attach(_receiver);
        _audio?.Attach(_receiver);

        try
        {
            await _receiver.StartAsync();
            ReceiverToggle.Content = "Stop receiver";
        }
        catch (Exception ex)
        {
            _log.Error("could not start the receiver", ex);
            SetIdleState("The receiver could not start", ex.Message, MirrorSourceState.Faulted);
            await StopReceiverAsync();
        }

        RefreshWarnings();
        UpdateTray();
    }

    private async Task StopReceiverAsync()
    {
        var receiver = _receiver;
        _receiver = null;
        if (receiver is not null)
        {
            receiver.StateChanged -= OnReceiverStateChanged;
            receiver.VideoFormatChanged -= OnVideoFormatChanged;
            await receiver.DisposeAsync();
        }

        var demo = _demo;
        _demo = null;
        if (demo is not null)
        {
            demo.StateChanged -= OnReceiverStateChanged;
            demo.VideoFormatChanged -= OnVideoFormatChanged;
            await demo.DisposeAsync();
        }

        var pipeline = _pipeline;
        _pipeline = null;
        if (pipeline is not null)
        {
            pipeline.FrameDecoded -= OnFrameDecoded;
            pipeline.RecordingFinished -= OnRecordingFinished;
            await pipeline.DisposeAsync();
        }

        var audio = _audio;
        _audio = null;
        if (audio is not null) await audio.DisposeAsync();

        EndSessionBookkeeping();

        if (!_shuttingDown)
        {
            ShowIdle();
            ReceiverToggle.Content = "Start receiver";
            UpdateTray();
        }
    }

    private async void OnToggleReceiver(object sender, RoutedEventArgs e) => await ToggleReceiverAsync();

    private async Task ToggleReceiverAsync()
    {
        if (!TryBeginReceiverWork()) return;

        try
        {
            if (_receiver is null)
            {
                await StartReceiverAsync();
            }
            else
            {
                await StopReceiverAsync();
                SetIdleState("Receiver stopped", "Start it when you are ready to mirror.", MirrorSourceState.Stopped);
            }
        }
        finally
        {
            EndReceiverWork();
        }
    }

    /// <summary>Restarts the receiver with the current settings, if it is running.</summary>
    private async Task RestartReceiverAsync()
    {
        if (_receiver is null) return;
        if (!TryBeginReceiverWork()) return;
        try { await StartReceiverAsync(); }
        finally { EndReceiverWork(); }
    }

    private void OnDisconnect(object sender, RoutedEventArgs e) => DisconnectDevice();

    private void DisconnectDevice()
    {
        if (_demo is not null)
        {
            // Ending the demo puts the receiver back, which is what was running before it.
            _ = EndDemoAsync();
            return;
        }

        if (_receiver?.Disconnect() != true) return;
        ShowToast("Disconnecting the iPhone", "");
    }

    // -------------------------------------------------------------------- demo

    private async void OnStartDemo(object sender, RoutedEventArgs e)
    {
        if (!TryBeginReceiverWork()) return;
        try { await StartDemoAsync(); }
        finally { EndReceiverWork(); }
    }

    /// <summary>
    /// Replaces the receiver with the test pattern. The receiver is stopped rather than left
    /// running beside it, so a phone cannot take the picture over half way through.
    /// </summary>
    private async Task StartDemoAsync()
    {
        await StopReceiverAsync();

        try
        {
            _pipeline = new VideoPipeline { RecordAudio = false };
            _pipeline.FrameDecoded += OnFrameDecoded;
            _pipeline.RecordingFinished += OnRecordingFinished;

            _demo = new DemoSource();
            _demo.StateChanged += OnReceiverStateChanged;
            _demo.VideoFormatChanged += OnVideoFormatChanged;
            _pipeline.Attach(_demo);
            _sessionIsDemo = true;
            await _demo.StartAsync();
            ReceiverToggle.Content = "Start receiver";
            UpdateTray();
        }
        catch (Exception ex)
        {
            _log.Error("the demo could not start", ex);
            await StopReceiverAsync();
            SetIdleState("The demo could not start", ex.Message, MirrorSourceState.Faulted);
        }
    }

    /// <summary>Ends the demo and brings the receiver back.</summary>
    private async Task EndDemoAsync()
    {
        if (!TryBeginReceiverWork()) return;
        try
        {
            await StopReceiverAsync();
            await StartReceiverAsync();
        }
        finally
        {
            EndReceiverWork();
        }
    }

    // -------------------------------------------------------------- source events

    private void OnReceiverStateChanged(object? sender, MirrorSourceStateChangedEventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            // A faulted start is followed by the receiver's own cleanup, which reports
            // Stopped and would overwrite the error text the user is meant to read. Only a
            // deliberate new session - Ready, or a device connecting - may move past it.
            if (_stateShown is MirrorSourceState.Faulted
                && e.State is not (MirrorSourceState.Ready or MirrorSourceState.Connecting))
            {
                return;
            }

            switch (e.State)
            {
                case MirrorSourceState.Ready:
                    EndSessionBookkeeping();
                    SetIdleState("Waiting for your iPhone", "This PC is advertising itself on your network.", e.State);
                    break;
                case MirrorSourceState.Connecting:
                    SetIdleState("Connecting", e.Device is { } d ? $"{d.Name} is starting a session." : "A device is starting a session.", e.State);
                    break;
                case MirrorSourceState.Streaming:
                    _stateShown = e.State;
                    BeginSessionBookkeeping(e.Device);
                    ShowVideo();
                    break;
                case MirrorSourceState.Faulted:
                    EndSessionBookkeeping();
                    SetIdleState("The receiver stopped", e.Message ?? "See the activity log.", e.State);
                    break;
                default:
                    EndSessionBookkeeping();
                    SetIdleState("Receiver stopped", "Start it when you are ready to mirror.", e.State);
                    break;
            }

            UpdateTray();
        });
    }

    private void OnVideoFormatChanged(object? sender, VideoFormat format) =>
        Dispatcher.BeginInvoke(() => _adjustedForVideoSize = false);

    /// <summary>
    /// Runs on the decode thread. <see cref="Rendering.VideoSurface.Present"/> is safe to
    /// call from here and returns immediately, so the decoder is never blocked on the UI.
    /// </summary>
    private void OnFrameDecoded(object? sender, DecodedVideoFrame frame) => Video.Present(frame);

    // ---------------------------------------------------------------- sessions

    private void BeginSessionBookkeeping(SourceDeviceInfo? device)
    {
        if (_sessionStartedUtc is not null) return;
        _sessionStartedUtc = _receiver?.SessionStartedAtUtc ?? DateTime.UtcNow;
        _sessionDevice = device ?? ActiveSource?.Device;
        _sessionIsDemo = _demo is not null;

        if (_sessionIsDemo)
        {
            ShowToast("Demo running - Disconnect ends it", "");
            return;
        }

        var name = _sessionDevice?.Name ?? "iPhone";
        ShowToast($"{name} connected", "");
        NotifyFromTray($"{name} connected", "Screen mirroring has started.");
    }

    /// <summary>Closes the books on a session: history, a toast, the timer.</summary>
    private void EndSessionBookkeeping()
    {
        if (_sessionStartedUtc is not { } started) return;
        var duration = DateTime.UtcNow - started;
        var device = _sessionDevice;
        _sessionStartedUtc = null;
        _sessionDevice = null;

        if (device is { } info && !_sessionIsDemo)
        {
            _settings.RememberDevice(info.Name, info.Model, duration);
            _settings.Save();
            if (SettingsPanel.Visibility == Visibility.Visible) RefreshRecentList();

            if (!_shuttingDown)
            {
                ShowToast($"{info.Name} disconnected", "");
                NotifyFromTray($"{info.Name} disconnected", $"Mirrored for {FormatDuration(duration)}.");
            }
        }
    }

    internal static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalHours >= 1) return $"{(int)duration.TotalHours}h {duration.Minutes:00}m";
        if (duration.TotalMinutes >= 1) return $"{duration.Minutes}m {duration.Seconds:00}s";
        return $"{Math.Max(duration.Seconds, 0)}s";
    }

    // ------------------------------------------------------------------- panels

    private void ShowVideo()
    {
        VideoHost.Visibility = Visibility.Visible;
        IdlePanel.Visibility = Visibility.Collapsed;
        SnapshotButton.IsEnabled = true;
        RecordButton.IsEnabled = _pipeline is not null;
        RecordButton.Visibility = _pipeline is not null ? Visibility.Visible : Visibility.Collapsed;
        MuteButton.Visibility = _audio is not null ? Visibility.Visible : Visibility.Collapsed;
        VolumeSlider.Visibility = _audio is not null ? Visibility.Visible : Visibility.Collapsed;
        VolumeLabel.Visibility = _audio is not null ? Visibility.Visible : Visibility.Collapsed;
        DisconnectButton.Visibility = Visibility.Visible;
        StatsButton.Visibility = Visibility.Visible;
        SessionTimer.Visibility = Visibility.Visible;
        // The pipeline was built with the persisted mute and level; the controls follow so
        // they cannot disagree with it from the first frame.
        MuteButton.IsChecked = _settings.Muted;
        _suppressVolumeEvents = true;
        VolumeSlider.Value = _settings.Volume;
        _suppressVolumeEvents = false;
        UpdateVolumeLabel();
        StatusDot.SetResourceReference(Shape.FillProperty, "Success");
        // Figures from a previous session would make the first seconds of this one unreadable.
        Video.ResetStatistics();
        ResetBitrateMeter();
        _cadenceWarned = false;
        _noticeDismissed = false;
        ResetZoom();
        ApplyStatsVisibility();

        // Windows measures idleness by input, so without this the monitor blanks part-way
        // through watching a mirrored phone.
        if (_settings.KeepDisplayAwake) DisplaySleep.Hold();

        var device = ActiveSource?.Device?.Name;
        Title = device is null ? $"SoulScreen - {_settings.DeviceName}" : $"{device} - SoulScreen";

        // The session's own controls just appeared, and they may not all fit.
        LayoutToolbar();
        UpdateAspectLock();
        FadeContentIn(VideoHost);
    }

    private void ShowIdle()
    {
        VideoHost.Visibility = Visibility.Collapsed;
        IdlePanel.Visibility = Visibility.Visible;
        SnapshotButton.IsEnabled = false;
        RecordButton.IsEnabled = false;
        RecordButton.IsChecked = false;
        RecordButton.Visibility = Visibility.Collapsed;
        MuteButton.Visibility = Visibility.Collapsed;
        VolumeSlider.Visibility = Visibility.Collapsed;
        VolumeLabel.Visibility = Visibility.Collapsed;
        DisconnectButton.Visibility = Visibility.Collapsed;
        StatsButton.Visibility = Visibility.Collapsed;
        StatsHud.Visibility = Visibility.Collapsed;
        ZoomBadge.Visibility = Visibility.Collapsed;
        SessionTimer.Visibility = Visibility.Collapsed;
        NoticeBar.Visibility = Visibility.Collapsed;
        Video.Clear();
        // The counters belong to the session that has just ended; a new one starts from zero.
        Video.ResetStatistics();
        ResetZoom();

        // The cadence warning belongs to a session's source rate. Left behind it would
        // claim a mismatch still in force while this screen says nothing is streaming at
        // all; a fresh session re-measures within a couple of seconds anyway.
        ClearCadenceWarnings();
        _cadenceWarned = false;

        DisplaySleep.Release();
        Title = $"SoulScreen - {_settings.DeviceName}";
        LayoutToolbar();
        UpdateAspectLock();
        RefreshNetworkLine();
        FadeContentIn(IdlePanel);
    }

    private void SetIdleState(string title, string subtitle, MirrorSourceState state)
    {
        _stateShown = state;
        ShowIdle();
        IdleTitle.Text = title;
        IdleSubtitle.Text = subtitle;

        var brushKey = state switch
        {
            MirrorSourceState.Ready => "Accent",
            MirrorSourceState.Connecting => "Warning",
            MirrorSourceState.Streaming => "Success",
            MirrorSourceState.Faulted => "Danger",
            _ => "TextTertiary",
        };
        // Resource references rather than brush instances, so a theme change recolours
        // these along with everything else.
        StatusDot.SetResourceReference(Shape.FillProperty, brushKey);
        IdleGlyph.SetResourceReference(TextBlock.ForegroundProperty, state == MirrorSourceState.Faulted ? "Danger" : "Accent");
        IdleGlyph.Text = state switch
        {
            MirrorSourceState.Faulted => "",
            MirrorSourceState.Stopped => "",
            MirrorSourceState.Connecting => "",
            _ => "",
        };
        SetHaloPulsing(state == MirrorSourceState.Connecting);
    }

    /// <summary>A gentle breathing animation on the idle glyph while a phone is negotiating.</summary>
    private void SetHaloPulsing(bool pulsing)
    {
        if (pulsing)
        {
            var pulse = new DoubleAnimation(1, 0.35, TimeSpan.FromMilliseconds(700))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            };
            IdleHalo.BeginAnimation(OpacityProperty, pulse);
        }
        else
        {
            IdleHalo.BeginAnimation(OpacityProperty, null);
            IdleHalo.Opacity = 1;
        }
    }

    /// <summary>A short fade so panel changes read as transitions rather than swaps.</summary>
    private static void FadeContentIn(UIElement element)
    {
        element.BeginAnimation(OpacityProperty, null);
        element.Opacity = 0;
        element.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });
    }

    private void RefreshNetworkLine()
    {
        var endpoints = NetworkInfo.ActiveEndpoints();
        if (endpoints.Count == 0)
        {
            NetworkGlyph.Text = "";
            NetworkText.Text = "Not connected to a network";
            return;
        }

        var first = endpoints[0];
        NetworkGlyph.Text = first.IsWireless ? "" : "";
        NetworkText.Text = endpoints.Count == 1
            ? $"{first.AdapterName} · {first.Address}"
            : $"{first.AdapterName} · {first.Address}  +{endpoints.Count - 1} more";
        NetworkLine.ToolTip = string.Join("\n", endpoints.Select(e => $"{e.AdapterName}: {e.Address}"));
    }

    /// <summary>Removes the refresh-rate warning and its overlay. Only ever called when
    /// the session it belonged to has ended.</summary>
    private void ClearCadenceWarnings()
    {
        for (var i = _warnings.Count - 1; i >= 0; i--)
            if (_warnings[i].Kind == WarningKind.Cadence) _warnings.RemoveAt(i);
        NoticeBar.Visibility = Visibility.Collapsed;
    }

    /// <summary>Surfaces the setup steps that silently break mirroring if they are missing.</summary>
    private void RefreshWarnings()
    {
        // Keep anything that is not about the receiver, such as the render-tier notice.
        for (var i = _warnings.Count - 1; i >= 0; i--)
            if (_warnings[i].Kind == WarningKind.Receiver) _warnings.RemoveAt(i);

        if (!NativeFairPlay.IsAvailable)
        {
            _warnings.Add(new WarningItem(
                "FairPlay support is not installed",
                "The iPhone will find this PC but refuse to mirror. Build the helper with " +
                "'pwsh tools/build-fairplay.ps1' and restart SoulScreen."));
        }

        if (!FFmpegRuntime.IsAvailable)
        {
            _warnings.Add(new WarningItem(
                "FFmpeg is not installed",
                "The session will connect but nothing will be drawn. Run 'pwsh tools/fetch-ffmpeg.ps1' " +
                "and restart SoulScreen."));
        }
    }

    // -------------------------------------------------------------------- toast

    /// <summary>Shows a one-line message over the content for a moment.</summary>
    private void ShowToast(string text, string glyph)
    {
        if (_shuttingDown) return;
        ToastText.Text = text;
        ToastGlyph.Text = glyph;
        Toast.Visibility = Visibility.Visible;

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        Toast.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease });
        ToastSlide.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(0, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });

        _toastTimer.Stop();
        _toastTimer.Start();
    }

    private void HideToast()
    {
        _toastTimer.Stop();
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(260))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
        };
        fade.Completed += (_, _) =>
        {
            // Only collapse if nothing re-showed the toast during the fade.
            if (!_toastTimer.IsEnabled) Toast.Visibility = Visibility.Collapsed;
        };
        Toast.BeginAnimation(OpacityProperty, fade);
        ToastSlide.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(8, TimeSpan.FromMilliseconds(260)));
    }

    // --------------------------------------------------------------- shutdown

    private async void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_shuttingDown) return;

        // Close-to-tray: the window goes away, the receiver does not. Only where there is a
        // notification area to go to - otherwise cancelling the close would leave a window
        // that cannot be shut at all.
        if (_settings.CloseToTray && _tray is not null && !_quitRequested && !_isFullscreen)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }

        // Teardown is asynchronous - sockets and the decode thread both need to unwind -
        // so cancel this close, drain, then close for real.
        e.Cancel = true;
        _shuttingDown = true;

        SaveWindowBounds();
        _settings.Save();

        Log.Entry -= OnLogEntry;
        ThemeManager.Changed -= OnThemeApplied;
        _metricsTimer.Stop();
        _toastTimer.Stop();
        _cursorTimer?.Stop();
        DisplaySleep.Release();
        _tray?.Dispose();
        _aspectLock?.Dispose();
        Video.Dispose();

        try { await StopReceiverAsync(); }
        catch (Exception ex) { _log.Warn("shutdown was not clean", ex); }

        Close();
    }

    /// <summary>What produced a warning, and therefore what clears it again.</summary>
    private enum WarningKind
    {
        /// <summary>Re-evaluated whenever the receiver starts.</summary>
        Receiver,
        /// <summary>A fact about this machine, established once.</summary>
        Machine,
        /// <summary>Refresh rate against source rate; re-evaluated while streaming.</summary>
        Cadence,
    }

    /// <summary>Something worth telling the user about, shown on the idle screen.</summary>
    private sealed record WarningItem(string Title, string Detail, WarningKind Kind = WarningKind.Receiver);
}
