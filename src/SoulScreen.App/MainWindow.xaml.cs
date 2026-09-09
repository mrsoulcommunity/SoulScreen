using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SoulScreen.AirPlay;
using SoulScreen.AirPlay.FairPlay;
using SoulScreen.Core.Logging;
using SoulScreen.Core.Media;
using SoulScreen.Core.Sources;
using SoulScreen.Media;

namespace SoulScreen.App;

public partial class MainWindow : Window
{
    /// <summary>Lines kept in the activity panel. Enough to cover a whole session handshake.</summary>
    private const int MaxLogLines = 500;

    private readonly ILogger _log = Log.For("ui");
    private readonly ObservableCollection<WarningItem> _warnings = [];
    private readonly Queue<string> _logLines = new();
    private readonly object _logLock = new();
    private readonly DispatcherTimer _metricsTimer;

    /// <summary>Set when new log lines have arrived but the panel has not been redrawn.</summary>
    private bool _logDirty;

    /// <summary>Hides the pointer after a moment of stillness in fullscreen.</summary>
    private DispatcherTimer? _cursorTimer;

    private AppSettings _settings = null!;
    private AirPlayReceiver? _receiver;
    private VideoPipeline? _pipeline;
    private AudioPipeline? _audio;

    private WindowState _stateBeforeFullscreen = WindowState.Normal;
    private bool _isFullscreen;
    private bool _adjustedForVideoSize;
    private bool _shuttingDown;

    public MainWindow()
    {
        InitializeComponent();

        WarningList.ItemsSource = _warnings;
        Video.VideoSizeChanged += OnVideoSizeChanged;

        VideoHost.MouseLeftButtonDown += OnVideoClicked;
        VideoHost.MouseMove += OnVideoPointerMoved;

        _metricsTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(500),
        };
        _metricsTimer.Tick += (_, _) =>
        {
            UpdateMetrics();
            RefreshLogPanel();
        };

        Log.Entry += OnLogEntry;

        Loaded += OnLoaded;
        Closing += OnClosing;
        KeyDown += OnKeyDown;
        StateChanged += OnWindowStateChanged;

        SourceInitialized += (_, _) =>
        {
            // Ask for the native dark caption first; where the request is honoured the app
            // keeps the real title bar. The custom caption below covers the builds where it
            // is silently ignored, and the two do not conflict.
            WindowFrame.TryDarkTitleBar(this);
            WindowFrame.UseCustomCaption(this);
        };
    }

    // ------------------------------------------------------------------ startup

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _settings = AppSettings.Load();
        // Protocol tracing is only useful if the sink lets trace entries through.
        Log.MinimumLevel = _settings.TraceProtocol ? LogLevel.Trace : LogLevel.Debug;
        LogRenderCapability();
        ApplySettingsToChrome();
        PopulateSettingsForm();
        RefreshWarnings();
        _metricsTimer.Start();

        if (_settings.StartReceiverOnLaunch)
            await StartReceiverAsync();
        else
            SetIdleState("Receiver stopped", "Start it when you are ready to mirror.", MirrorSourceState.Stopped);
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
                AboutReceiver: false));
        }
    }

    private void ApplySettingsToChrome()
    {
        Title = $"SoulScreen - {_settings.DeviceName}";
        ReceiverName.Text = _settings.DeviceName;
        IdleReceiverName.Text = _settings.DeviceName;
        PinButton.IsChecked = _settings.AlwaysOnTop;
        Topmost = _settings.AlwaysOnTop;
    }

    // ---------------------------------------------------------------- receiver

    private async Task StartReceiverAsync()
    {
        await StopReceiverAsync();

        try
        {
            _pipeline = new VideoPipeline();
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
            _audio = new AudioPipeline { Muted = MuteButton.IsChecked == true };
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

        if (!_shuttingDown)
        {
            ShowIdle();
            ReceiverToggle.Content = "Start receiver";
        }
    }

    private async void OnToggleReceiver(object sender, RoutedEventArgs e)
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

    // -------------------------------------------------------------- source events

    private void OnReceiverStateChanged(object? sender, MirrorSourceStateChangedEventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            switch (e.State)
            {
                case MirrorSourceState.Ready:
                    SetIdleState("Waiting for your iPhone", "This PC is advertising itself on your network.", e.State);
                    break;
                case MirrorSourceState.Connecting:
                    SetIdleState("Connecting", e.Device is { } d ? $"{d.Name} is starting a session." : "A device is starting a session.", e.State);
                    break;
                case MirrorSourceState.Streaming:
                    ShowVideo();
                    break;
                case MirrorSourceState.Faulted:
                    SetIdleState("The receiver stopped", e.Message ?? "See the activity log.", e.State);
                    break;
                default:
                    SetIdleState("Receiver stopped", "Start it when you are ready to mirror.", e.State);
                    break;
            }

        });
    }

    private void OnVideoFormatChanged(object? sender, VideoFormat format) =>
        Dispatcher.BeginInvoke(() => _adjustedForVideoSize = false);

    /// <summary>
    /// Runs on the decode thread. <see cref="Rendering.VideoSurface.Present"/> is safe to
    /// call from here and returns immediately, so the decoder is never blocked on the UI.
    /// </summary>
    private void OnFrameDecoded(object? sender, DecodedVideoFrame frame) => Video.Present(frame);

    /// <summary>
    /// Reshapes the window to the phone's aspect ratio, once per session, so a portrait
    /// screen fills the window instead of sitting between black bars.
    /// <para>
    /// Both dimensions move, not just the height: matching a 9:19.5 phone by growing the
    /// height alone produces a window taller than the screen, which then gets clamped and
    /// leaves the bars it was meant to remove.
    /// </para>
    /// </summary>
    private void OnVideoSizeChanged(object? sender, Size size)
    {
        // Never fight the user's own sizing after the first fit.
        if (_adjustedForVideoSize || _isFullscreen || WindowState != WindowState.Normal) return;
        if (size.Width <= 0 || size.Height <= 0) return;
        _adjustedForVideoSize = true;

        var workArea = SystemParameters.WorkArea;
        var chromeHeight = Toolbar.ActualHeight + StatusBar.ActualHeight;
        // Border and caption sit outside the client area the layout measured.
        var borderWidth = Math.Max(ActualWidth - VideoHost.ActualWidth, 0);

        // Leave a margin so the window never sits flush against the screen edges.
        var maxWidth = workArea.Width * 0.9;
        var maxHeight = workArea.Height * 0.9;

        // Start from the current width and let the aspect ratio decide the height, then fall
        // back to fitting the height when that would not fit.
        var contentWidth = Math.Max(VideoHost.ActualWidth, MinWidth - borderWidth);
        var contentHeight = contentWidth * size.Height / size.Width;

        if (contentHeight + chromeHeight > maxHeight)
        {
            contentHeight = maxHeight - chromeHeight;
            contentWidth = contentHeight * size.Width / size.Height;
        }

        if (contentWidth + borderWidth > maxWidth)
        {
            contentWidth = maxWidth - borderWidth;
            contentHeight = contentWidth * size.Height / size.Width;
        }

        Width = Math.Max(contentWidth + borderWidth, MinWidth);
        Height = Math.Max(contentHeight + chromeHeight, MinHeight);

        // Nudge back on screen if the new size pushed an edge past the work area.
        if (Left + Width > workArea.Right) Left = Math.Max(workArea.Left, workArea.Right - Width - 8);
        if (Top + Height > workArea.Bottom) Top = Math.Max(workArea.Top, workArea.Bottom - Height - 8);
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
        StatusDot.Fill = (Brush)FindResource("Success");
        // Figures from a previous session would make the first seconds of this one unreadable.
        Video.ResetStatistics();

        // Windows measures idleness by input, so without this the monitor blanks part-way
        // through watching a mirrored phone.
        DisplaySleep.Hold();

        var device = _receiver?.Device?.Name;
        Title = device is null ? $"SoulScreen - {_settings.DeviceName}" : $"{device} - SoulScreen";
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
        Video.Clear();

        DisplaySleep.Release();
        if (_settings is not null) Title = $"SoulScreen - {_settings.DeviceName}";
    }

    private void SetIdleState(string title, string subtitle, MirrorSourceState state)
    {
        ShowIdle();
        IdleTitle.Text = title;
        IdleSubtitle.Text = subtitle;
        StatusDot.Fill = (Brush)FindResource(state switch
        {
            MirrorSourceState.Ready => "Accent",
            MirrorSourceState.Connecting => "Warning",
            MirrorSourceState.Streaming => "Success",
            MirrorSourceState.Faulted => "Danger",
            _ => "TextTertiary",
        });
    }

    /// <summary>Surfaces the setup steps that silently break mirroring if they are missing.</summary>
    private void RefreshWarnings()
    {
        // Keep anything that is not about the receiver, such as the render-tier notice.
        for (var i = _warnings.Count - 1; i >= 0; i--)
            if (_warnings[i].AboutReceiver) _warnings.RemoveAt(i);

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

    // ------------------------------------------------------------------ toolbar

    private void OnPinChanged(object sender, RoutedEventArgs e)
    {
        Topmost = PinButton.IsChecked == true;
        if (_settings is null) return;
        _settings.AlwaysOnTop = Topmost;
        _settings.Save();
    }

    /// <summary>Double-click toggles fullscreen, which is what every video player does.</summary>
    private void OnVideoClicked(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2) return;
        ToggleFullscreen();
        e.Handled = true;
    }

    /// <summary>
    /// Brings the pointer back on movement and restarts the countdown that hides it again.
    /// Only in fullscreen: hiding it over a windowed picture would strand the user with no
    /// way to reach the toolbar.
    /// </summary>
    private void OnVideoPointerMoved(object sender, MouseEventArgs e)
    {
        if (!_isFullscreen) return;
        ShowPointer();
        _cursorTimer?.Stop();
        _cursorTimer?.Start();
    }

    private void ShowPointer()
    {
        if (Cursor != Cursors.None) return;
        Cursor = null;
    }

    private void HidePointer()
    {
        _cursorTimer?.Stop();
        if (_isFullscreen) Cursor = Cursors.None;
    }

    private void OnMinimise(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximise(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        // Restore glyph while maximised, maximise glyph otherwise.
        MaximiseButton.Content = WindowState == WindowState.Maximized ? "" : "";
        MaximiseButton.ToolTip = WindowState == WindowState.Maximized ? "Restore" : "Maximise";
        RootGrid.Margin = _isFullscreen ? new Thickness(0) : WindowFrame.MaximisedPadding(this);
    }

    private void OnToggleFullscreen(object sender, RoutedEventArgs e) => ToggleFullscreen();

    private void ToggleFullscreen()
    {
        if (_isFullscreen)
        {
            _isFullscreen = false;
            WindowFrame.UseCustomCaption(this);
            WindowStyle = WindowStyle.SingleBorderWindow;
            ResizeMode = ResizeMode.CanResize;
            WindowState = _stateBeforeFullscreen;
            Toolbar.Visibility = Visibility.Visible;
            StatusBar.Visibility = Visibility.Visible;
            FullscreenButton.Content = "";
            RootGrid.Margin = WindowFrame.MaximisedPadding(this);

            _cursorTimer?.Stop();
            Cursor = null;
        }
        else
        {
            _stateBeforeFullscreen = WindowState;
            _isFullscreen = true;
            // The custom chrome reserves a resize border that would show as a seam against
            // the screen edge, so fullscreen drops it entirely.
            WindowFrame.RemoveCustomCaption(this);
            // Normal first: going straight from Maximized to fullscreen leaves the taskbar
            // drawn over the window.
            WindowState = WindowState.Normal;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Maximized;
            Toolbar.Visibility = Visibility.Collapsed;
            StatusBar.Visibility = Visibility.Collapsed;
            FullscreenButton.Content = "";
            RootGrid.Margin = new Thickness(0);

            _cursorTimer ??= CreateCursorTimer();
            _cursorTimer.Start();
        }
    }

    private DispatcherTimer CreateCursorTimer()
    {
        var timer = new DispatcherTimer(DispatcherPriority.Input)
        {
            Interval = TimeSpan.FromSeconds(2),
        };
        timer.Tick += (_, _) => HidePointer();
        return timer;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.F11:
                ToggleFullscreen();
                e.Handled = true;
                break;
            case Key.Escape when _isFullscreen:
                ToggleFullscreen();
                e.Handled = true;
                break;
            case Key.S when Keyboard.Modifiers == ModifierKeys.Control:
                SaveSnapshot();
                e.Handled = true;
                break;
            case Key.M when Keyboard.Modifiers == ModifierKeys.Control:
                MuteButton.IsChecked = MuteButton.IsChecked != true;
                e.Handled = true;
                break;
            case Key.R when Keyboard.Modifiers == ModifierKeys.Control && RecordButton.IsEnabled:
                RecordButton.IsChecked = RecordButton.IsChecked != true;
                e.Handled = true;
                break;
        }
    }

    private void OnRecordChanged(object sender, RoutedEventArgs e)
    {
        var recording = RecordButton.IsChecked == true;
        RecordDot.Fill = (Brush)FindResource(recording ? "Danger" : "TextSecondary");

        if (_pipeline is null)
        {
            RecordButton.IsChecked = false;
            return;
        }

        if (recording)
        {
            Directory.CreateDirectory(_settings.CaptureDirectory);
            var path = Path.Combine(_settings.CaptureDirectory,
                $"SoulScreen-{DateTime.Now:yyyyMMdd-HHmmss}.mp4");
            _pipeline.StartRecording(path);
            StatusText.Text = $"Recording to {Path.GetFileName(path)}";
        }
        else
        {
            _pipeline.StopRecording();
        }
    }

    private void OnRecordingFinished(object? sender, string path) =>
        Dispatcher.BeginInvoke(() =>
        {
            RecordButton.IsChecked = false;
            StatusText.Text = $"Saved {Path.GetFileName(path)}";
            _log.Info($"recording saved to {path}");
        });

    private void OnMuteChanged(object sender, RoutedEventArgs e)
    {
        var muted = MuteButton.IsChecked == true;
        MuteButton.Content = muted ? "" : "";
        MuteButton.ToolTip = muted ? "Unmute the phone's audio (Ctrl+M)" : "Mute the phone's audio (Ctrl+M)";
        if (_audio is not null) _audio.Muted = muted;
    }

    private void OnSnapshot(object sender, RoutedEventArgs e) => SaveSnapshot();

    private void SaveSnapshot()
    {
        var snapshot = Video.Snapshot();
        if (snapshot is null)
        {
            _log.Info("nothing to capture yet");
            return;
        }

        try
        {
            Directory.CreateDirectory(_settings.CaptureDirectory);
            var path = Path.Combine(_settings.CaptureDirectory,
                $"SoulScreen-{DateTime.Now:yyyyMMdd-HHmmss}.png");

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(snapshot));
            using var stream = File.Create(path);
            encoder.Save(stream);

            _log.Info($"saved {path}");
            StatusText.Text = $"Saved {Path.GetFileName(path)}";
        }
        catch (Exception ex)
        {
            _log.Error("could not save the screenshot", ex);
        }
    }

    private void OnLogToggled(object sender, RoutedEventArgs e)
    {
        LogPanel.Visibility = LogButton.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        if (LogButton.IsChecked != true) return;

        lock (_logLock) _logDirty = true;
        RefreshLogPanel();
    }

    private void OnCopyLog(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(LogText.Text); }
        catch (Exception ex) { _log.Warn("could not copy the log to the clipboard", ex); }
    }

    private void OnClearLog(object sender, RoutedEventArgs e)
    {
        lock (_logLock)
        {
            _logLines.Clear();
            _logDirty = false;
        }
        LogText.Text = string.Empty;
    }

    // ----------------------------------------------------------------- settings

    private void OnSettingsToggled(object sender, RoutedEventArgs e)
    {
        if (SettingsButton.IsChecked == true)
        {
            PopulateSettingsForm();
            IdentityText.Text = _receiver is { } receiver
                ? $"device id  {receiver.Identity.DeviceId}\npublic key {receiver.Identity.Ed25519PublicKeyHex[..24]}...\nport       {_settings.Port}"
                : "The receiver is not running.";
            SettingsPanel.Visibility = Visibility.Visible;
        }
        else
        {
            SettingsPanel.Visibility = Visibility.Collapsed;
        }
    }

    private void PopulateSettingsForm()
    {
        NameBox.Text = _settings.DeviceName;
        PortBox.Text = _settings.Port.ToString(CultureInfo.InvariantCulture);
        WidthBox.Text = _settings.DisplayWidth.ToString(CultureInfo.InvariantCulture);
        HeightBox.Text = _settings.DisplayHeight.ToString(CultureInfo.InvariantCulture);
        RefreshBox.Text = _settings.DisplayRefreshRate.ToString(CultureInfo.InvariantCulture);
        AudioCheck.IsChecked = _settings.EnableAudio;
        AutoStartCheck.IsChecked = _settings.StartReceiverOnLaunch;
        TraceCheck.IsChecked = _settings.TraceProtocol;
    }

    private async void OnApplySettings(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        if (name.Length == 0)
        {
            MessageBox.Show("The receiver needs a name.", "SoulScreen", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!ushort.TryParse(PortBox.Text, out var port) || port == 0)
        {
            MessageBox.Show("The control port must be between 1 and 65535.", "SoulScreen",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _settings.DeviceName = name;
        _settings.Port = port;
        if (int.TryParse(WidthBox.Text, out var width)) _settings.DisplayWidth = width;
        if (int.TryParse(HeightBox.Text, out var height)) _settings.DisplayHeight = height;
        if (int.TryParse(RefreshBox.Text, out var refresh)) _settings.DisplayRefreshRate = refresh;
        _settings.EnableAudio = AudioCheck.IsChecked == true;
        _settings.StartReceiverOnLaunch = AutoStartCheck.IsChecked == true;
        _settings.TraceProtocol = TraceCheck.IsChecked == true;
        _settings.Save();

        ApplySettingsToChrome();
        SettingsButton.IsChecked = false;
        SettingsPanel.Visibility = Visibility.Collapsed;

        await StartReceiverAsync();
    }

    private void OnCancelSettings(object sender, RoutedEventArgs e)
    {
        SettingsButton.IsChecked = false;
        SettingsPanel.Visibility = Visibility.Collapsed;
    }

    // ------------------------------------------------------------------ metrics

    private void UpdateMetrics()
    {
        if (_receiver is null)
        {
            StatusText.Text = "Receiver stopped";
            MetricsText.Text = string.Empty;
            return;
        }

        StatusText.Text = _receiver.State switch
        {
            MirrorSourceState.Ready => $"Advertising as \"{_receiver.AdvertisedName}\" on port {_settings.Port}",
            MirrorSourceState.Connecting => "Negotiating with the phone",
            MirrorSourceState.Streaming => _receiver.Device?.ToString() ?? "Mirroring",
            MirrorSourceState.Faulted => "Faulted - see the activity log",
            _ => "Stopped",
        };

        if (_pipeline is null)
        {
            MetricsText.Text = "no decoder";
            return;
        }

        var parts = new List<string>(6);
        if (Video.VideoSize.Width > 0)
            parts.Add($"{(int)Video.VideoSize.Width}x{(int)Video.VideoSize.Height}");
        if (_pipeline.DecodedFrameCount > 0)
            parts.Add($"{_pipeline.FramesPerSecond:0.#} fps");

        // Decode-to-screen time. Frames superseded within one composition pass are normal
        // when the phone outruns the monitor, so they are not reported as a problem;
        // genuinely dropped or skipped samples are.
        var latency = Video.AveragePresentLatencyMilliseconds;
        if (latency > 0) parts.Add($"{latency:0.#} ms");

        var lost = _pipeline.DroppedSampleCount + _pipeline.SkippedSampleCount;
        if (lost > 0) parts.Add($"{lost} lost");
        if (_audio is { IsPlaying: true })
            parts.Add(_audio.Muted ? "muted" : $"audio {_audio.BufferedDuration.TotalMilliseconds:0} ms");
        if (_pipeline.IsRecording)
        {
            if (_pipeline.RecordingPath is null)
            {
                // Recording begins on the next keyframe, which the phone may not send for
                // a moment; saying so beats a counter stuck at zero.
                parts.Add("REC waiting for a keyframe");
            }
            else
            {
                var elapsed = _pipeline.RecordingDuration;
                var megabytes = _pipeline.RecordingSizeBytes / 1024.0 / 1024.0;
                parts.Add($"REC {(int)elapsed.TotalMinutes:00}:{elapsed.Seconds:00}  {megabytes:0.#} MB");
            }
        }

        MetricsText.Text = string.Join("   ", parts);
    }

    // -------------------------------------------------------------------- log

    /// <summary>
    /// Runs on whatever thread logged. Deliberately does no dispatcher work: with protocol
    /// tracing on this is called often, and posting a redraw per line put enough on the UI
    /// thread to be visible in the picture. The panel is redrawn on the metrics tick instead.
    /// </summary>
    private void OnLogEntry(LogEntry entry)
    {
        if (entry.Level < LogLevel.Debug) return;

        var line = entry.Exception is null
            ? entry.ToString()
            : $"{entry.TimestampUtc.ToLocalTime():HH:mm:ss.fff} {entry.Level.ToString().ToUpperInvariant(),-5} [{entry.Category}] {entry.Message}: {entry.Exception.Message}";

        lock (_logLock)
        {
            _logLines.Enqueue(line);
            while (_logLines.Count > MaxLogLines) _logLines.Dequeue();
            _logDirty = true;
        }
    }

    /// <summary>Redraws the activity panel at the metrics cadence, and only while visible.</summary>
    private void RefreshLogPanel()
    {
        if (LogPanel.Visibility != Visibility.Visible) return;

        string text;
        lock (_logLock)
        {
            if (!_logDirty) return;
            _logDirty = false;

            var builder = new StringBuilder(_logLines.Count * 80);
            foreach (var existing in _logLines) builder.AppendLine(existing);
            text = builder.ToString();
        }

        LogText.Text = text;
        LogScroller.ScrollToEnd();
    }

    // --------------------------------------------------------------- shutdown

    private async void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_shuttingDown) return;

        // Teardown is asynchronous - sockets and the decode thread both need to unwind -
        // so cancel this close, drain, then close for real.
        e.Cancel = true;
        _shuttingDown = true;

        Log.Entry -= OnLogEntry;
        _metricsTimer.Stop();
        _cursorTimer?.Stop();
        DisplaySleep.Release();
        Video.Dispose();

        try { await StopReceiverAsync(); }
        catch (Exception ex) { _log.Warn("shutdown was not clean", ex); }

        Close();
    }

    /// <summary>
    /// A blocking setup problem, shown on the idle screen.
    /// </summary>
    /// <param name="AboutReceiver">
    /// True for problems re-evaluated each time the receiver starts. False for facts about
    /// the machine, which are established once and must survive that refresh.
    /// </param>
    private sealed record WarningItem(string Title, string Detail, bool AboutReceiver = true);
}
