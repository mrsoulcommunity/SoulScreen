using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SoulScreen.App.Logic;
using SoulScreen.App.Rendering;
using SoulScreen.Core.Media;
using SoulScreen.Core.Sources;
using SoulScreen.Media;

namespace SoulScreen.App.Controls;

/// <summary>
/// One tile in the multi-device grid: owns its own <see cref="VideoSurface"/> and subscribes to
/// its own <see cref="IMirrorSource"/>. Shows per-tile chrome (name, model, rate, recording dot),
/// a hover toolbar (screenshot, record, mute, swap), and approval, pause and blocked overlays.
/// <para>
/// The tile deliberately contains no trust logic of its own: the host's
/// <see cref="MainWindow.SetApproval"/> call is what decides whether a phone may show, and
/// Allow/Block merely report the choice back through <see cref="ApprovalDecided"/>.
/// </para>
/// </summary>
public sealed partial class TileHost : UserControl
{
    private readonly VideoPipeline _pipeline;
    private AudioPipeline? _audio;
    private IMirrorSource? _source;
    private bool _isMuted;
    private bool _isPaused;
    private bool _isRecording;
    private bool _isBlocked;
    private bool _disposed;
    private string _blockedMessage = "Blocked";
    private readonly AppSettings? _settings;

    /// <summary>The path the running tile recording is writing to, or null while idle.</summary>
    private string? _recordingPath;

    public TileHost(AppSettings? settings = null)
    {
        _settings = settings;
        InitializeComponent();

        _pipeline = new VideoPipeline { RecordAudio = false };
        _pipeline.FrameDecoded += OnFrameDecoded;
        _pipeline.RecordingFinished += OnPipelineRecordingFinished;

        Loaded += (_, _) =>
        {
            if (_settings is null || !_settings.EnableAudio) return;
            _audio ??= new AudioPipeline
            {
                Muted = _isMuted,
                Volume = 1f,
                OutputDeviceId = _settings.AudioOutputDeviceId,
            };
            if (_source is not null) _audio.Attach(_source);
            Video.PresentationDelay = TimeSpan.FromMilliseconds(50);
        };
        Unloaded += (_, _) => _ = DisposeAsync();
    }

    /// <summary>Releases the decode pipeline and audio output exactly once. Guarded because
    /// both Unloaded and the host's teardown can reach this, and a decode callback may still
    /// be in flight.</summary>
    public async Task DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        if (_source is not null) DetachSource(_source);
        Source = null;
        if (_audio is not null) await _audio.DisposeAsync();
        _audio = null;
        await _pipeline.DisposeAsync();
    }

    // --------------------------------------------------------------------- Dependency properties

    public static readonly DependencyProperty SourceProperty =
        DependencyProperty.Register(nameof(Source), typeof(IMirrorSource), typeof(TileHost),
            new PropertyMetadata(null, OnSourceChanged));

    public static readonly DependencyProperty IsRecordingProperty =
        DependencyProperty.Register(nameof(IsRecording), typeof(bool), typeof(TileHost),
            new PropertyMetadata(false, OnIsRecordingChanged));

    public static readonly DependencyProperty BlockedMessageProperty =
        DependencyProperty.Register(nameof(BlockedMessage), typeof(string), typeof(TileHost),
            new PropertyMetadata("Blocked"));

    public static readonly DependencyProperty TileIndexProperty =
        DependencyProperty.Register(nameof(TileIndex), typeof(int), typeof(TileHost),
            new PropertyMetadata(0));

    public IMirrorSource? Source
    {
        get => (IMirrorSource?)GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public bool IsRecording
    {
        get => (bool)GetValue(IsRecordingProperty);
        set => SetValue(IsRecordingProperty, value);
    }

    public string BlockedMessage
    {
        get => (string)GetValue(BlockedMessageProperty);
        set => SetValue(BlockedMessageProperty, value);
    }

    public int TileIndex
    {
        get => (int)GetValue(TileIndexProperty);
        set => SetValue(TileIndexProperty, value);
    }

    // --------------------------------------------------------------------- Source wiring

    private static void OnSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TileHost t && !t._disposed) t.AttachSource(e.OldValue as IMirrorSource, e.NewValue as IMirrorSource);
    }

    private void AttachSource(IMirrorSource? oldSource, IMirrorSource? newSource)
    {
        if (oldSource is not null) DetachSource(oldSource);
        _source = newSource;
        if (newSource is null) return;

        newSource.VideoFormatChanged += OnVideoFormatChanged;
        newSource.VideoSampleReady += OnVideoSampleReady;
        newSource.StateChanged += OnStateChanged;
        _pipeline.Attach(newSource);
        _audio?.Attach(newSource);
        UpdateChrome(newSource.Device, newSource.State);
    }

    private void DetachSource(IMirrorSource source)
    {
        source.VideoFormatChanged -= OnVideoFormatChanged;
        source.VideoSampleReady -= OnVideoSampleReady;
        source.StateChanged -= OnStateChanged;
        if (ReferenceEquals(_source, source)) _source = null;
    }

    private void OnVideoFormatChanged(object? sender, VideoFormat format)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (sender is IMirrorSource src) UpdateChrome(src.Device, src.State);
        });
    }

    private void OnVideoSampleReady(object? sender, MediaSample sample)
    {
        // The pipeline handles queue and decode; nothing to do here.
    }

    private void OnStateChanged(object? sender, MirrorSourceStateChangedEventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (sender is IMirrorSource src) UpdateChrome(src.Device, src.State);
        });
    }

    private void OnFrameDecoded(object? sender, DecodedVideoFrame frame)
    {
        // Guard both the disposed flag and the pipeline's own state: a frame can still be
        // in flight when the pipeline is being torn down.
        if (_disposed) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (_disposed) return;
            if (_isPaused) return;
            Video.Present(frame);
        });
    }

    private void OnPipelineRecordingFinished(object? sender, string path)
    {
        Dispatcher.BeginInvoke(() =>
        {
            _isRecording = false;
            IsRecording = false;
            RecordingFinished?.Invoke(this, new TileRecordingFinished(path, FileSizeOf(path)));
            ShowToast?.Invoke(this, new TileToast("Recording finished", "\uE714"));
        });
    }

    private static long FileSizeOf(string path)
    {
        try { return new System.IO.FileInfo(path).Length; } catch { return 0; }
    }

    // --------------------------------------------------------------------- Chrome updates

    private void UpdateChrome(SourceDeviceInfo? device, MirrorSourceState state)
    {
        if (device is { } info)
        {
            DeviceName.Text = info.Name;
            ModelName.Text = info.Model ?? "";
            ModelName.Visibility = string.IsNullOrEmpty(info.Model) ? Visibility.Collapsed : Visibility.Visible;
        }
        else
        {
            DeviceName.Text = state switch
            {
                MirrorSourceState.Stopped => "No device",
                MirrorSourceState.Ready => "Waiting…",
                MirrorSourceState.Connecting => "Connecting…",
                MirrorSourceState.Streaming => "Streaming…",
                MirrorSourceState.Faulted => "Error",
                _ => "Unknown",
            };
            ModelName.Visibility = Visibility.Collapsed;
        }

        LatencyText.Text = Video.PresentedPerSecond > 0
            ? $"{Video.PresentedPerSecond:0} fps"
            : "";

        if (state == MirrorSourceState.Faulted)
            ShowBlocked("Connection error");
        else if (_isBlocked)
            ShowBlocked(_blockedMessage);
        else
            HideBlocked();
    }

    // --------------------------------------------------------------------- Per-tile controls

    /// <summary>Saves this tile's picture with the same timestamp naming the main window
    /// uses, and reports the path through <see cref="ScreenshotSaved"/>.</summary>
    public void Screenshot()
    {
        var snap = Video.Snapshot();
        if (snap is null) return;

        try
        {
            var directory = _settings?.CaptureDirectory
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "SoulScreen");
            Directory.CreateDirectory(directory);

            var path = CaptureTimestampFormatter.NewPath(
                directory, DateTime.Now, ".png",
                new TimestampSettings(), CultureInfo.CurrentCulture,
                deviceName: _source?.Device?.Name ?? "");

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(snap));
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
            encoder.Save(stream);

            ScreenshotSaved?.Invoke(this, path);
            ShowToast?.Invoke(this, new TileToast("Screenshot saved", "\uE714"));
        }
        catch
        {
            ShowToast?.Invoke(this, new TileToast("The screenshot could not be saved", "\uE7BA"));
        }
    }

    public void ToggleMute()
    {
        _isMuted = !_isMuted;
        MicIcon.Visibility = _isMuted ? Visibility.Visible : Visibility.Collapsed;
        if (_audio != null) _audio.Muted = _isMuted;
    }

    public void TogglePause()
    {
        _isPaused = !_isPaused;
        PauseOverlay.Visibility = _isPaused ? Visibility.Visible : Visibility.Collapsed;
        // Freeze the surface itself, like the main window's SetPaused does: hiding the
        // overlay alone would leave live frames still being presented underneath it.
        if (Video is not null) Video.IsFrozen = _isPaused;
    }

    public void StartRecording()
    {
        if (_isRecording) return;
        if (_source is null || _pipeline is not { IsRecording: false }) return;

        try
        {
            Directory.CreateDirectory(_settings?.CaptureDirectory
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "SoulScreen"));
        }
        catch
        {
            ShowToast?.Invoke(this, new TileToast("The capture folder cannot be created", "\uE7BA"));
            return;
        }

        _recordingPath = CaptureTimestampFormatter.NewPath(
            _settings?.CaptureDirectory
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "SoulScreen"),
            DateTime.Now, ".mp4",
            new TimestampSettings(), CultureInfo.CurrentCulture,
            deviceName: _source.Device?.Name ?? "");

        _pipeline.StartRecording(_recordingPath);
        _isRecording = true;
        IsRecording = true;
        ShowToast?.Invoke(this, new TileToast("Recording", "\uE714"));
    }

    public void StopRecording()
    {
        if (!_isRecording) return;
        _pipeline.StopRecording();
        // IsRecording and the event follow OnPipelineRecordingFinished, once the file is done.
    }

    public void SwapToFull()
    {
        TileSwapToFullRequested?.Invoke(this, TileIndex);
    }

    private static void OnIsRecordingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TileHost t) t.RecordingDot.Visibility = (bool)e.NewValue ? Visibility.Visible : Visibility.Collapsed;
    }

    // --------------------------------------------------------------------- Approval

    /// <summary>Host-set approval state: whether this tile's phone may show, and what the
    /// tile should name it. The tile never decides for itself.</summary>
    public void SetApproval(ConnectDecision decision, SourceDeviceInfo? device)
    {
        switch (decision)
        {
            case ConnectDecision.Allow:
                _isBlocked = false;
                ApprovalOverlay.Visibility = Visibility.Collapsed;
                HideBlocked();
                break;
            case ConnectDecision.Ask:
                _isBlocked = false;
                ApprovalDeviceName.Text = device?.Name ?? "A device";
                ApprovalOverlay.Visibility = Visibility.Visible;
                HideBlocked();
                break;
            case ConnectDecision.Block:
                _isBlocked = true;
                ApprovalOverlay.Visibility = Visibility.Collapsed;
                ShowBlocked(device is null ? "Blocked" : $"{device.Value.Name} is blocked");
                break;
        }
    }

    /// <summary>The tile's Allow/Block buttons, reported to the host, which owns the trust
    /// lists and does the bookkeeping.</summary>
    public event EventHandler<bool>? ApprovalDecided;

    private void OnApprovalAllow(object sender, RoutedEventArgs e)
    {
        ApprovalOverlay.Visibility = Visibility.Collapsed;
        ApprovalDecided?.Invoke(this, true);
    }

    private void OnApprovalBlock(object sender, RoutedEventArgs e)
    {
        ApprovalOverlay.Visibility = Visibility.Collapsed;
        ApprovalDecided?.Invoke(this, false);
    }

    // --------------------------------------------------------------------- Events exposed to MainWindow

    public event EventHandler<int>? TileSwapToFullRequested;
    public event EventHandler<string>? ScreenshotSaved;
    public event EventHandler<TileRecordingFinished>? RecordingFinished;

    /// <summary>Optional hook for the host's own toast. Null and unused inside the tile,
    /// which shows nothing global of its own.</summary>
    public event EventHandler<TileToast>? ShowToast;

    // --------------------------------------------------------------------- Toolbar button handlers

    private void OnTileScreenshot(object sender, RoutedEventArgs e) => Screenshot();

    private void OnTileRecord(object sender, RoutedEventArgs e)
    {
        if (_isRecording) StopRecording(); else StartRecording();
    }

    private void OnTileMute(object sender, RoutedEventArgs e) => ToggleMute();

    private void OnTileSwap(object sender, RoutedEventArgs e) => SwapToFull();

    // --------------------------------------------------------------------- Helpers

    private void ShowBlocked(string message)
    {
        _isBlocked = true;
        BlockedMessage = message;
        BlockedOverlay.Visibility = Visibility.Visible;
    }

    private void HideBlocked()
    {
        _isBlocked = false;
        BlockedOverlay.Visibility = Visibility.Collapsed;
    }
}

/// <summary>A finished tile recording: where it went and how big it is.</summary>
public sealed record TileRecordingFinished(string Path, long Bytes);

/// <summary>A short message the tile wants shown on the window's toast.</summary>
public sealed record TileToast(string Message, string Glyph);
