using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using SoulScreen.AirPlay;
using SoulScreen.AirPlay.FairPlay;
using SoulScreen.App.Logic;
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

    /// <summary>What this session has produced, for the summary when it ends.</summary>
    private readonly SessionTally _sessionTally = new();

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

    /// <summary>Watches for displays being plugged or unplugged, for the display picker.</summary>
    private DisplayService.DisplayWatcher? _displayWatcher;

    /// <summary>A display arrived or left: the picker's list is rebuilt if settings are
    /// open, and a window left hanging past the edge of a monitor that has gone is pulled
    /// back onto one that remains.</summary>
    private void OnDisplaysChanged()
    {
        if (_shuttingDown) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (SettingsPanel.Visibility == Visibility.Visible) PopulateDisplayChoices();

            var desktop = DisplayService.VirtualDesktop(this);
            var (left, top) = Logic.DisplayLayout.ClampToDisplays(Left, Top, ActualWidth, ActualHeight, desktop);
            if (Math.Abs(left - Left) > 0.5 || Math.Abs(top - Top) > 0.5)
            {
                Left = left;
                Top = top;
            }
        });
    }

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

    /// <summary>Set when the user ends a session from this side - Disconnect, or stopping the
    /// receiver - and read by the bookkeeping when the session actually ends, because the
    /// state change arrives later and on its own.</summary>
    private bool _userStopRequested;

    /// <summary>Which receiver session the decision in force - shown, asked about or turned away -
    /// was made for, so a phone that takes the receiver over from another is decided afresh.</summary>
    private DateTime? _decidedSessionKey;

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
        InitialisePalette();
        InitialiseCaptures();
        InitialiseMiniPlayer();
        InitialiseMiniMenu();
        InitialiseSettingsNav();
        InitialiseMarkup();
        InitialiseHotkeysAndTaskbar();

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
        // A toast with an action waits while the pointer is on it, so it cannot vanish from
        // under a click that is on its way.
        Toast.MouseEnter += (_, _) => _toastTimer.Stop();
        Toast.MouseLeave += (_, _) =>
        {
            if (Toast.Visibility == Visibility.Visible && Toast.IsHitTestVisible) _toastTimer.Start();
        };

        // The waiting animation is only worth running while someone could be looking at it.
        IsVisibleChanged += (_, _) => UpdateRipple();

        Closing += OnClosing;
        ThemeManager.Changed += OnThemeApplied;
        UpdateAccessibilityPalette();

        // Displays arriving and leaving change both the picker's list and whether the
        // chosen display still exists; the window is kept reachable either way.
        _displayWatcher = new DisplayService.DisplayWatcher(this, OnDisplaysChanged);

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
        ShowWelcomeIfFirstRun();

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
            // The button is drawn as "Stop receiver" until told otherwise, which on a receiver
            // that was never started offered to stop something that was not running.
            SetReceiverToggle(running: false);
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
        Topmost = _settings.AlwaysOnTop || _isMiniPlayer;
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
        RecolourAccentSwatches();
        UpdateAccessibilityPalette();
    }

    /// <summary>Refreshes the live accessibility metadata after a theme/accent change.
    /// Screen readers should describe the state that is visible, not the state from the
    /// previous palette.</summary>
    private void UpdateAccessibilityPalette()
    {
        var mode = ThemeManager.IsDark ? "dark" : "light";
        System.Windows.Automation.AutomationProperties.SetHelpText(
            RootGrid, $"SoulScreen is using the {mode} theme with the {ThemeManager.Accent} accent.");
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
            SetReceiverToggle(running: true);
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
        // Unhooking the events means the session's end arrives through the cleanup below
        // rather than the state handler; name the reason before they go.
        if (VideoHost.Visibility == Visibility.Visible && !_sessionIsDemo) _userStopRequested = true;
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
            SetReceiverToggle(running: false);
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
        _userStopRequested = true;
        ShowToast("Disconnecting the iPhone", "");
    }

    // -------------------------------------------------------------------- demo

    private void OnDemoLink(object sender, RoutedEventArgs e)
    {
        DemoMenu.PlacementTarget = DemoLink;
        DemoMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Top;
        DemoMenu.IsOpen = true;
    }

    private async void OnStartDemoPattern(object sender, RoutedEventArgs e)
    {
        var pattern = sender is MenuItem { Tag: string tag } && Enum.TryParse<DemoPattern>(tag, out var parsed)
            ? parsed
            : DemoPattern.MotionTest;
        if (!TryBeginReceiverWork()) return;
        try { await StartDemoAsync(pattern); }
        finally { EndReceiverWork(); }
    }

    /// <summary>
    /// Replaces the receiver with the test pattern. The receiver is stopped rather than left
    /// running beside it, so a phone cannot take the picture over half way through.
    /// </summary>
    private async Task StartDemoAsync(DemoPattern pattern = DemoPattern.MotionTest)
    {
        // Ending the demo puts back what was there before it: a receiver that had been stopped
        // stays stopped, rather than the PC starting to advertise itself unasked.
        _resumeReceiverAfterDemo = _receiver is not null;
        await StopReceiverAsync();

        try
        {
            _pipeline = new VideoPipeline { RecordAudio = false };
            _pipeline.FrameDecoded += OnFrameDecoded;
            _pipeline.RecordingFinished += OnRecordingFinished;

            _demo = new DemoSource(pattern: pattern);
            _demo.StateChanged += OnReceiverStateChanged;
            _demo.VideoFormatChanged += OnVideoFormatChanged;
            _pipeline.Attach(_demo);
            _sessionIsDemo = true;
            await _demo.StartAsync();
            SetReceiverToggle(running: false);
            UpdateTray();
        }
        catch (Exception ex)
        {
            _log.Error("the demo could not start", ex);
            await StopReceiverAsync();
            SetIdleState("The demo could not start", ex.Message, MirrorSourceState.Faulted);
        }
    }

    /// <summary>Whether the receiver was running when the demo replaced it.</summary>
    private bool _resumeReceiverAfterDemo;

    /// <summary>Ends the demo and brings the receiver back, if it was running before.</summary>
    private async Task EndDemoAsync()
    {
        if (!TryBeginReceiverWork()) return;
        try
        {
            await StopReceiverAsync();
            if (_resumeReceiverAfterDemo)
            {
                await StartReceiverAsync();
            }
            else
            {
                SetReceiverToggle(running: false);
                SetIdleState("Receiver stopped", "Start it when you are ready to mirror.", MirrorSourceState.Stopped);
            }
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
                    // The phone that was just on screen may be about to come back: iOS drops a
                    // session and sets a new one up for a sleep, a change of network or a
                    // moment out of range, and the recording should survive that.
                    if (HoldSessionForReconnect()) break;
                    EndSessionBookkeeping(SessionEndReason.PhoneEnded);
                    SetIdleState("Waiting for your iPhone", "This PC is advertising itself on your network.", e.State);
                    break;
                case MirrorSourceState.Connecting:
                    // While a session is being held open for the phone that dropped, the same
                    // phone negotiating again leaves the picture where it is; anybody else
                    // ends the wait and is dealt with as an ordinary new session.
                    if (IsHeldForReconnect)
                    {
                        if (e.Device is null || IsReconnectSession(e.Device)) break;
                        _log.Info("another device is connecting while the session waits for the one that dropped");
                        EndHeldSession(SessionEndReason.TakenOver);
                    }

                    // A question on screen, or a session being turned away, is not interrupted -
                    // nor is a phone already on screen by another merely starting a handshake,
                    // which may yet be cancelled. Tearing the picture down here ended a recording
                    // of the first phone for a second that never arrived.
                    if (_approvalPending || _sessionRejected || VideoHost.Visibility == Visibility.Visible) break;
                    SetIdleState("Connecting", e.Device is { } d ? $"{d.Name} is starting a session." : "A device is starting a session.", e.State);
                    HoldAudioFor(e.Device);
                    break;
                case MirrorSourceState.Streaming:
                {
                    var sessionKey = _receiver?.SessionStartedAtUtc;

                    // The phone whose session is being held open, back inside its minute: the
                    // recording, the counters and the clock all carry on, and nothing is asked
                    // of a phone that has already been let in once.
                    if (IsReconnectSession(e.Device ?? ActiveSource?.Device))
                    {
                        _decidedSessionKey = sessionKey;
                        _stateShown = e.State;
                        CompleteReconnect(e.Device ?? ActiveSource?.Device);
                        break;
                    }

                    // AirPlay lets a second phone take the receiver over, and it arrives as another
                    // Streaming with no end to the first. Whatever was decided - shown, asked about,
                    // turned away - was about the phone before, so this one is decided afresh.
                    if (sessionKey is not null && _decidedSessionKey is not null && sessionKey != _decidedSessionKey)
                    {
                        _log.Info("another device took the receiver over");
                        EndSessionBookkeeping(SessionEndReason.TakenOver);
                        // The first phone's picture comes down before the second is decided on:
                        // left up, it went on being captured and recorded - showing the new
                        // phone - while that phone was still waiting to be allowed.
                        ShowIdle();
                    }

                    if (_approvalPending || _sessionRejected) break;
                    if (_sessionStartedUtc is null)
                    {
                        _decidedSessionKey = sessionKey;
                        // A new session is let in, asked about or turned away before anything is shown.
                        if (!AdmitSession(e.Device ?? ActiveSource?.Device)) break;
                    }
                    _stateShown = e.State;
                    StartShowingSession(e.Device);
                    break;
                }
                case MirrorSourceState.Faulted:
                    EndSessionBookkeeping(SessionEndReason.Faulted);
                    SetIdleState("The receiver stopped", e.Message ?? "See the activity log.", e.State);
                    break;
                default:
                    EndSessionBookkeeping(SessionEndReason.PhoneEnded);
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

    /// <summary>Puts a session that may be seen on screen, and runs the connect actions once.</summary>
    private void StartShowingSession(SourceDeviceInfo? device)
    {
        var newSession = BeginSessionBookkeeping(device);
        ShowVideo();
        // Once the picture is laid out, so fullscreen and recording act on it.
        if (newSession) Dispatcher.BeginInvoke(RunConnectActions, DispatcherPriority.Loaded);
    }

    /// <returns>True if this began a session, false if one was already under way.</returns>
    private bool BeginSessionBookkeeping(SourceDeviceInfo? device)
    {
        if (_sessionStartedUtc is not null) return false;
        _sessionStartedUtc = _receiver?.SessionStartedAtUtc ?? DateTime.UtcNow;
        _sessionDevice = device ?? ActiveSource?.Device;
        _sessionIsDemo = _demo is not null;
        // A new session starts with empty counters, wherever the last one left them.
        _sessionTally.Clear();
        _connectionAdvice.Reset();

        if (_sessionIsDemo)
        {
            ShowToast("Demo running - Disconnect ends it", "");
            return true;
        }

        var name = _sessionDevice?.Name ?? "iPhone";
        ShowToast($"{name} connected", "");
        NotifyFromTray($"{name} connected", "Screen mirroring has started.");
        return true;
    }

    /// <summary>What the user asked to happen whenever a phone starts mirroring.</summary>
    private void RunConnectActions()
    {
        if (_shuttingDown || VideoHost.Visibility != Visibility.Visible) return;

        if (_settings.BringToFrontOnConnect && (!IsVisible || WindowState == WindowState.Minimized))
            ActivateFromAnotherInstance();

        // The display preference names where a session is meant to be watched.
        MoveWindowToResolvedDisplay();

        // Not from the taskbar: a minimised window sent fullscreen remembers "minimised" as the
        // state to go back to, and Esc would then throw it off the screen.
        if (_settings.FullscreenOnConnect && IsVisible && WindowState != WindowState.Minimized && !_isFullscreen && !_isMiniPlayer)
            ToggleFullscreen();

        // A recording nobody asked for of a test pattern is clutter, not a feature.
        if (_settings.RecordOnConnect && !_sessionIsDemo && RecordButton.IsEnabled && RecordButton.IsChecked != true)
            RecordButton.IsChecked = true;
    }

    /// <summary>Closes the books on a session: history, a toast, the timer. The reason is
    /// only words on the summary, so the four call sites that do not name one mean the phone
    /// simply went away.</summary>
    private void EndSessionBookkeeping(SessionEndReason reason = SessionEndReason.PhoneEnded)
    {
        _decidedSessionKey = null;
        // However the session ended, the phone is no longer being waited for.
        ClearReconnectHold();
        if (_sessionStartedUtc is not { } started) return;
        if (_userStopRequested && reason == SessionEndReason.PhoneEnded) reason = SessionEndReason.StoppedByUser;
        _userStopRequested = false;
        var duration = DateTime.UtcNow - started;
        var device = _sessionDevice;
        _sessionStartedUtc = null;
        _sessionDevice = null;

        // Focus mode would otherwise leave an empty black window where the session was.
        LeaveFocusModeForSessionEnd();

        var summary = new SessionSummary(
            device?.Name ?? "iPhone",
            duration,
            _sessionTally.Screenshots,
            _sessionTally.Recordings,
            _sessionTally.RecordedBytes,
            _sessionIsDemo,
            reason);
        _sessionTally.Clear();

        if (device is { } info && !_sessionIsDemo)
        {
            _settings.RememberDevice(info.Name, info.Model, duration);
            _settings.Save();
            if (SettingsPanel.Visibility == Visibility.Visible) RefreshRecentList();

            if (!_shuttingDown)
            {
                var detail = summary.DeservesToast ? summary.Detail : null;
                ShowToast(summary.Headline, "\uE8BB", detail is null ? null : "View", ShowCaptures);
                NotifyFromTray($"{info.Name} disconnected", $"{summary.Headline}{(detail is null ? "" : $" - {detail}")}.");
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
        MarkupButton.Visibility = Visibility.Visible;
        SessionTimer.Visibility = Visibility.Visible;
        _quality.Reset();
        QualityIndicator.Visibility = _demo is null ? Visibility.Visible : Visibility.Collapsed;
        ShowQuality(ConnectionQualityLevel.Unknown);
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
        // A new session starts live, whatever the last one was left in.
        SetPaused(false, announce: false);
        UpdateRecordingPill();
        UpdateMiniControls();
        UpdateRipple();
        PositionOverlays();

        // Windows measures idleness by input, so without this the monitor blanks part-way
        // through watching a mirrored phone.
        if (_settings.KeepDisplayAwake) DisplaySleep.Hold();

        var device = ActiveSource?.Device?.Name;
        Title = device is null ? $"SoulScreen - {_settings.DeviceName}" : $"{device} - SoulScreen";
        // While a phone is on screen the toolbar names the phone, as the window's title does.
        ReceiverName.Text = device ?? _settings.DeviceName;

        UpdateControlBar();
        UpdateAspectLock();
        UpdatePictureCorners();
        UpdateTaskbar();
        FadeContentIn(VideoHost);
    }

    private void ShowIdle()
    {
        var wasShowingVideo = VideoHost.Visibility == Visibility.Visible;
        if (!_shuttingDown)
        {
            // A phone-sized floating window has nothing to float once the phone has gone, and
            // a screen filled with the idle panel is not what anyone was watching.
            if (_isMiniPlayer) ExitMiniPlayer();
            if (wasShowingVideo && _isFullscreen && _settings.LeaveFullscreenOnDisconnect) ToggleFullscreen();
        }

        VideoHost.Visibility = Visibility.Collapsed;
        IdlePanel.Visibility = Visibility.Visible;
        ResetApprovalState();
        SnapshotButton.IsEnabled = false;
        RecordButton.IsEnabled = false;
        RecordButton.IsChecked = false;
        RecordButton.Visibility = Visibility.Collapsed;
        MarkupButton.IsChecked = false;
        MarkupButton.Visibility = Visibility.Collapsed;
        QualityIndicator.Visibility = Visibility.Collapsed;
        _quality.Reset();
        StatsHud.Visibility = Visibility.Collapsed;
        ZoomBadge.Visibility = Visibility.Collapsed;
        SessionTimer.Visibility = Visibility.Collapsed;
        NoticeBar.Visibility = Visibility.Collapsed;
        SetPaused(false, announce: false);
        RecordingPill.Visibility = Visibility.Collapsed;
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
        ReceiverName.Text = _settings.DeviceName;
        UpdateControlBar();
        UpdateAspectLock();
        RefreshNetworkLine();
        UpdatePictureCorners();
        UpdateTaskbar();
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
        UpdateRipple();
    }

    /// <summary>
    /// The primary look for "Start receiver", since starting is the one thing to do on a
    /// stopped receiver; a quiet one for "Stop receiver", which is not.
    /// </summary>
    private void SetReceiverToggle(bool running)
    {
        ReceiverToggle.Content = running ? "Stop receiver" : "Start receiver";
        ReceiverToggle.Style = (Style)FindResource(running ? "GhostButton" : "PrimaryButton");
        System.Windows.Automation.AutomationProperties.SetName(
            ReceiverToggle, $"{(running ? "Stop" : "Start")} receiver");
    }

    private bool _rippling;

    /// <summary>
    /// Rings spreading out from the idle glyph while the receiver waits for a phone - the
    /// "broadcasting" look of AirPlay itself. Only while it can be seen, and never when Windows
    /// has been asked to keep animation to a minimum.
    /// </summary>
    private void UpdateRipple()
    {
        var wanted = !_shuttingDown
                     && IsVisible
                     && WindowState != WindowState.Minimized
                     && IdlePanel.Visibility == Visibility.Visible
                     && _stateShown == MirrorSourceState.Ready
                     && Motion.ShouldAnimate(_settings);
        if (wanted == _rippling) return;
        _rippling = wanted;

        if (wanted)
        {
            AnimateRipple(RippleInner, TimeSpan.Zero);
            AnimateRipple(RippleOuter, TimeSpan.FromMilliseconds(1300));
            return;
        }

        foreach (var ring in new[] { RippleInner, RippleOuter })
        {
            var scale = (ScaleTransform)ring.RenderTransform;
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            ring.BeginAnimation(OpacityProperty, null);
        }
    }

    private static void AnimateRipple(Ellipse ring, TimeSpan delay)
    {
        var period = TimeSpan.FromMilliseconds(2600);
        var grow = new DoubleAnimation(1, 1.6, period)
        {
            BeginTime = delay,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        var scale = (ScaleTransform)ring.RenderTransform;
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        ring.BeginAnimation(OpacityProperty, new DoubleAnimation(0.5, 0, period)
        {
            BeginTime = delay,
            RepeatBehavior = RepeatBehavior.Forever,
        });
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

    private Action? _toastAction;

    /// <summary>
    /// Shows a one-line message over the content for a moment, optionally with one action -
    /// "View" - in which case it stays a little longer and waits while the pointer is on it.
    /// </summary>
    private void ShowToast(string text, string glyph, string? actionLabel = null, Action? action = null)
    {
        if (_shuttingDown) return;
        ToastText.Text = text;
        ToastGlyph.Text = glyph;
        _toastAction = action;
        ToastAction.Content = actionLabel;
        ToastAction.Visibility = action is null ? Visibility.Collapsed : Visibility.Visible;
        Toast.IsHitTestVisible = action is not null;
        _toastTimer.Interval = TimeSpan.FromMilliseconds(action is null ? 2800 : 5000);
        Toast.Visibility = Visibility.Visible;

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        Toast.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease });
        ToastSlide.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(0, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });

        _toastTimer.Stop();
        _toastTimer.Start();
    }

    private void OnToastAction(object sender, RoutedEventArgs e)
    {
        var action = _toastAction;
        _toastAction = null;
        HideToast();
        action?.Invoke();
    }

    private void HideToast()
    {
        _toastTimer.Stop();
        Toast.IsHitTestVisible = false;
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

    /// <summary>Set once teardown has finished and the close it cancelled may go through.</summary>
    private bool _teardownComplete;

    private async void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // A second close while teardown is under way - a double click on X, Alt+F4 pressed
        // twice - must wait for it too: let through, it ended the process while a recording's
        // index was still being written.
        if (_shuttingDown)
        {
            e.Cancel = !_teardownComplete;
            return;
        }

        // Close-to-tray: the window goes away, the receiver does not. Only where there is a
        // notification area to go to - otherwise cancelling the close would leave a window
        // that cannot be shut at all. Fullscreen too: hiding leaves fullscreen first.
        if (_settings.CloseToTray && _tray is not null && !_quitRequested)
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
        _displayWatcher?.Dispose();
        DisplaySleep.Release();
        _tray?.Dispose();
        _aspectLock?.Dispose();
        _hotkeys?.Dispose();
        _laserTimer?.Stop();
        _snapTimer?.Stop();
        StopViewerMedia();
        Video.Dispose();

        try { await StopReceiverAsync(); }
        catch (Exception ex) { _log.Warn("shutdown was not clean", ex); }

        _teardownComplete = true;
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
