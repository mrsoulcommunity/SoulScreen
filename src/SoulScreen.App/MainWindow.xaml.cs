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
    private readonly DispatcherTimer _metricsTimer;

    private AppSettings _settings = null!;
    private AirPlayReceiver? _receiver;
    private VideoPipeline? _pipeline;

    private WindowState _stateBeforeFullscreen = WindowState.Normal;
    private bool _isFullscreen;
    private bool _adjustedForVideoSize;
    private bool _shuttingDown;

    public MainWindow()
    {
        InitializeComponent();

        WarningList.ItemsSource = _warnings;
        Video.VideoSizeChanged += OnVideoSizeChanged;

        _metricsTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(500),
        };
        _metricsTimer.Tick += (_, _) => UpdateMetrics();

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
        ApplySettingsToChrome();
        PopulateSettingsForm();
        RefreshWarnings();
        _metricsTimer.Start();

        if (_settings.StartReceiverOnLaunch)
            await StartReceiverAsync();
        else
            SetIdleState("Receiver stopped", "Start it when you are ready to mirror.", MirrorSourceState.Stopped);
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
        }
        catch (FFmpegUnavailableException ex)
        {
            // Without a decoder the receiver is still worth running: the handshake can be
            // verified and the protocol log is useful even with no picture.
            _log.Warn(ex.Message);
            _pipeline = null;
        }

        _receiver = new AirPlayReceiver(_settings.ToAirPlayOptions());
        _receiver.StateChanged += OnReceiverStateChanged;
        _receiver.VideoFormatChanged += OnVideoFormatChanged;
        if (_pipeline is not null) _pipeline.Attach(_receiver);

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
            await pipeline.DisposeAsync();
        }

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

            DeviceLabel.Text = e.Device?.ToString() ?? string.Empty;
        });
    }

    private void OnVideoFormatChanged(object? sender, VideoFormat format) =>
        Dispatcher.BeginInvoke(() => _adjustedForVideoSize = false);

    /// <summary>
    /// Runs on the decode thread. <see cref="Rendering.VideoSurface.Present"/> is safe to
    /// call from here and returns immediately, so the decoder is never blocked on the UI.
    /// </summary>
    private void OnFrameDecoded(object? sender, DecodedVideoFrame frame) => Video.Present(frame);

    private void OnVideoSizeChanged(object? sender, Size size)
    {
        // Reshape the window once per new geometry so a portrait phone gets a portrait
        // window instead of black bars, but never fight the user's own sizing afterwards.
        if (_adjustedForVideoSize || _isFullscreen || WindowState != WindowState.Normal) return;
        _adjustedForVideoSize = true;

        if (size.Width <= 0 || size.Height <= 0) return;

        var chromeHeight = Toolbar.ActualHeight + StatusBar.ActualHeight;
        var contentWidth = ActualWidth - (ActualWidth - VideoHost.ActualWidth);
        if (contentWidth <= 0) contentWidth = ActualWidth;

        var desiredContentHeight = contentWidth * size.Height / size.Width;
        var desiredHeight = desiredContentHeight + chromeHeight;

        var workArea = SystemParameters.WorkArea;
        Height = Math.Min(desiredHeight, workArea.Height - 40);
        if (Top + Height > workArea.Bottom) Top = Math.Max(workArea.Top, workArea.Bottom - Height - 10);
    }

    // ------------------------------------------------------------------- panels

    private void ShowVideo()
    {
        VideoHost.Visibility = Visibility.Visible;
        IdlePanel.Visibility = Visibility.Collapsed;
        SnapshotButton.IsEnabled = true;
        StatusDot.Fill = (Brush)FindResource("Success");
    }

    private void ShowIdle()
    {
        VideoHost.Visibility = Visibility.Collapsed;
        IdlePanel.Visibility = Visibility.Visible;
        SnapshotButton.IsEnabled = false;
        Video.Clear();
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
        _warnings.Clear();

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
        }
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
        }
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
        if (LogButton.IsChecked == true) LogScroller.ScrollToEnd();
    }

    private void OnCopyLog(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(LogText.Text); }
        catch (Exception ex) { _log.Warn("could not copy the log to the clipboard", ex); }
    }

    private void OnClearLog(object sender, RoutedEventArgs e)
    {
        _logLines.Clear();
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

        var parts = new List<string>(4);
        if (Video.VideoSize.Width > 0)
            parts.Add($"{(int)Video.VideoSize.Width}x{(int)Video.VideoSize.Height}");
        if (_pipeline.DecodedFrameCount > 0)
            parts.Add($"{_pipeline.FramesPerSecond:0.#} fps");
        if (_pipeline.DroppedSampleCount > 0)
            parts.Add($"{_pipeline.DroppedSampleCount} dropped");

        MetricsText.Text = string.Join("   ", parts);
    }

    // -------------------------------------------------------------------- log

    private void OnLogEntry(LogEntry entry)
    {
        if (entry.Level < LogLevel.Debug) return;

        var line = entry.Exception is null
            ? entry.ToString()
            : $"{entry.TimestampUtc.ToLocalTime():HH:mm:ss.fff} {entry.Level.ToString().ToUpperInvariant(),-5} [{entry.Category}] {entry.Message}: {entry.Exception.Message}";

        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            _logLines.Enqueue(line);
            while (_logLines.Count > MaxLogLines) _logLines.Dequeue();

            if (LogPanel.Visibility != Visibility.Visible) return;

            var builder = new StringBuilder(_logLines.Count * 80);
            foreach (var existing in _logLines) builder.AppendLine(existing);
            LogText.Text = builder.ToString();
            LogScroller.ScrollToEnd();
        });
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
        Video.Dispose();

        try { await StopReceiverAsync(); }
        catch (Exception ex) { _log.Warn("shutdown was not clean", ex); }

        Close();
    }

    /// <summary>A blocking setup problem, shown on the idle screen.</summary>
    private sealed record WarningItem(string Title, string Detail);
}
