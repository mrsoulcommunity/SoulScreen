using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SoulScreen.App.Rendering;
using SoulScreen.Core.Media;
using SoulScreen.Core.Sources;
using SoulScreen.Media;

namespace SoulScreen.App.Controls;

/// <summary>
/// One tile in the multi-device grid: owns its own <see cref="VideoSurface"/> and subscribes to
/// its own <see cref="IMirrorSource"/>. Shows per-tile chrome (name, model, latency, recording
/// dot), a hover toolbar (screenshot, record, mute, swap), and a pause overlay.
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
    private string _blockedMessage = "Blocked";
    private readonly AppSettings _settings;

    public TileHost() : this(AppSettings.Load()) { }

    public TileHost(AppSettings settings)
    {
        _settings = settings;
        InitializeComponent();

        _pipeline = new VideoPipeline { RecordAudio = false };
        _pipeline.FrameDecoded += OnFrameDecoded;

        Loaded += (_, _) =>
        {
            if (_settings?.EnableAudio == true)
            {
                _audio = new AudioPipeline
                {
                    Muted = _isMuted,
                    Volume = 1f,
                    OutputDeviceId = _settings.AudioOutputDeviceId,
                };
                if (_source != null) _audio.Attach(_source);
            }
            Video.PresentationDelay = TimeSpan.FromMilliseconds(50);
        };
        Unloaded += async (_, _) =>
        {
            if (_audio is { } audio) await audio.DisposeAsync();
            await _pipeline.DisposeAsync();
        };
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
        if (d is TileHost t) t.AttachSource(e.OldValue as IMirrorSource, e.NewValue as IMirrorSource);
    }

    private void AttachSource(IMirrorSource? oldSource, IMirrorSource? newSource)
    {
        if (oldSource is not null)
        {
            oldSource.VideoFormatChanged -= OnVideoFormatChanged;
            oldSource.VideoSampleReady -= OnVideoSampleReady;
            oldSource.StateChanged -= OnStateChanged;
        }

        _source = newSource;

        if (newSource is not null)
        {
            newSource.VideoFormatChanged += OnVideoFormatChanged;
            newSource.VideoSampleReady += OnVideoSampleReady;
            newSource.StateChanged += OnStateChanged;
            _pipeline.Attach(newSource);
            _audio?.Attach(newSource);
            UpdateChrome(newSource.Device, newSource.State);
        }
        else
        {
            _pipeline.Detach();
            _audio?.Detach();
            UpdateChrome(null, MirrorSourceState.Stopped);
        }
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
        // The pipeline handles queue/decode; nothing to do here.
    }

    private void OnAudioSampleReady(object? sender, MediaSample sample)
    {
        // AudioPipeline.Attach subscribes to AudioSampleReady internally; this handler
        // is intentionally empty — do not add audio handling here.
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
        if (_isPaused) return;
        Video.Present(frame);
    }

    // --------------------------------------------------------------------- Chrome updates

    private void UpdateChrome(SourceDeviceInfo? device, MirrorSourceState state)
    {
        if (device is { } info)
        {
            DeviceName.Text = Truncate(info.Name, 18);
            DeviceName.ToolTip = info.Name;
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
            ? $"{Video.PresentedPerSecond:0.0} fps"
            : "";

        if (state == MirrorSourceState.Faulted)
            ShowBlocked("Connection error");
        else if (_isBlocked)
            ShowBlocked(_blockedMessage);
        else
            HideBlocked();
    }

    // --------------------------------------------------------------------- Per-tile controls

    public void Screenshot()
    {
        var snap = Video.Snapshot();
        if (snap is null) return;
        var path = CaptureSnapshot(snap);
        if (path is not null) System.Windows.Clipboard.SetFileDropList([path]);
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
    }

    public void StartRecording()
    {
        if (_isRecording) return;
        // TODO: wire per-tile SessionRecorder (requires tile-aware path from Feature 2)
        _isRecording = true;
        IsRecording = true;
    }

    public void StopRecording()
    {
        if (!_isRecording) return;
        // TODO: stop per-tile recorder
        _isRecording = false;
        IsRecording = false;
    }

    public void SwapToFull()
    {
        // Raises an event that MainWindow handles to promote this tile to full-window.
        TileSwapToFullRequested?.Invoke(this, TileIndex);
    }

    private static void OnIsRecordingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TileHost t) t.RecordingDot.Visibility = (bool)e.NewValue ? Visibility.Visible : Visibility.Collapsed;
    }

    // --------------------------------------------------------------------- Approval

    public void ShowApprovalPrompt(string deviceName)
    {
        ApprovalDeviceName.Text = $"{deviceName} (tile {TileIndex + 1})";
        ApprovalOverlay.Visibility = Visibility.Visible;
    }

    public event EventHandler<string>? ApprovalDecided; // "allow" or "block"

    private void OnApprovalAllow(object sender, RoutedEventArgs e)
    {
        ApprovalOverlay.Visibility = Visibility.Collapsed;
        ApprovalDecided?.Invoke(this, "allow");
    }

    private void OnApprovalBlock(object sender, RoutedEventArgs e)
    {
        ApprovalOverlay.Visibility = Visibility.Collapsed;
        ApprovalDecided?.Invoke(this, "block");
    }

    // --------------------------------------------------------------------- Events exposed to MainWindow

    public event EventHandler<int>? TileSwapToFullRequested;

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

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..(max - 1)] + "\u2026";

    private static string? CaptureSnapshot(System.Windows.Media.Imaging.BitmapSource bitmap)
    {
        try
        {
            var folder = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
                "SoulScreen");
            System.IO.Directory.CreateDirectory(folder);
            var name = $"SoulScreen-{DateTime.Now:yyyy-MM-dd-HH-mm-ss}.png";
            var path = System.IO.Path.Combine(folder, name);
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            using var stream = System.IO.File.Create(path);
            encoder.Save(stream);
            return path;
        }
        catch { return null; }
    }
}
