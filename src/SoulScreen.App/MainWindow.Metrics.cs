using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using SoulScreen.App.Logic;
using SoulScreen.Core.Sources;

namespace SoulScreen.App;

/// <summary>
/// The half-second tick: the status bar, the statistics overlay, the session timer, the
/// refresh-rate warning, and the tray's hover text.
/// </summary>
public partial class MainWindow
{
    /// <summary>Agreeing readings needed before the refresh-rate warning is raised or
    /// withdrawn: three ticks, so a second and a half of steady measurement.</summary>
    private const int CadenceStreakNeeded = 3;

    private bool _cadenceWarned;
    private bool _cadenceStreakMismatched;
    private int _cadenceStreak;
    private bool _noticeDismissed;

    /// <summary>When the capture drive was last looked at during a recording, and whether the
    /// low-space warning has been given for this recording.</summary>
    private DateTime _diskCheckedUtc;
    private bool _diskLowWarned;

    /// <summary>A short-lived status line, e.g. "Saved X". Held on the status bar for a
    /// few seconds against the metrics tick that rewrites that line every half second.</summary>
    private string? _transientStatus;
    private string? _statusFile;
    private DateTime _transientStatusUntil;

    private long _bytesAtLastTick;
    private long _ticksAtLastBitrate;
    private double _bitsPerSecond;

    private readonly Dictionary<string, TextBlock> _hudValues = new();

    /// <summary>Puts a short-lived message on the status bar. The metrics tick rewrites
    /// that line every half second, so the message is re-set on every tick until its
    /// moment has passed rather than being written once and immediately lost.</summary>
    private void ShowTransientStatus(string message, string? file)
    {
        _transientStatus = message;
        _statusFile = file;
        _transientStatusUntil = DateTime.UtcNow + TimeSpan.FromSeconds(6);
        StatusText.Text = message;
        StatusLink.Visibility = file is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ResetBitrateMeter()
    {
        _bytesAtLastTick = _pipeline?.ReceivedByteCount ?? 0;
        _ticksAtLastBitrate = Stopwatch.GetTimestamp();
        _bitsPerSecond = 0;
    }

    private void UpdateMetrics()
    {
        // A one-shot status ("Saved X") holds the line for a few seconds; after that the
        // ordinary receiver state takes the bar back.
        if (_transientStatus is not null && DateTime.UtcNow >= _transientStatusUntil)
        {
            _transientStatus = null;
            _statusFile = null;
            StatusLink.Visibility = Visibility.Collapsed;
        }

        UpdateSessionTimer();
        UpdateRecordingPill();
        CheckRecordingDiskSpace();
        CheckRecordingTimer();
        SampleMetricsHistory();

        var source = ActiveSource;
        if (source is null)
        {
            StatusText.Text = _transientStatus ?? "Receiver stopped";
            MetricsText.Text = string.Empty;
            return;
        }

        StatusText.Text = _transientStatus ?? source.State switch
        {
            MirrorSourceState.Ready when _receiver is not null =>
                $"Advertising as \"{_receiver.AdvertisedName}\" on port {_settings.Port}",
            MirrorSourceState.Connecting => _demo is null ? "Negotiating with the phone" : "Starting the demo",
            MirrorSourceState.Streaming when _approvalPending => $"Waiting for you to allow {source.Device?.Name ?? "the iPhone"}",
            MirrorSourceState.Streaming when _sessionRejected => $"Disconnecting {source.Device?.Name ?? "the iPhone"}",
            MirrorSourceState.Streaming => _demo is null
                ? source.Device?.ToString() ?? "Mirroring"
                : "Demo pattern - the receiver is paused",
            MirrorSourceState.Faulted => "Faulted - see the activity log",
            _ => "Stopped",
        };

        if (_pipeline is null)
        {
            MetricsText.Text = "no decoder";
            return;
        }

        // Figures from the session that just ended would otherwise sit on the bar while the
        // idle screen says nothing is streaming, which reads as a fault that is not there.
        // Nor while a session is being held back or turned away: it is not on screen to measure.
        if (source.State != MirrorSourceState.Streaming || _approvalPending || _sessionRejected)
        {
            MetricsText.Text = string.Empty;
            return;
        }

        UpdateBitrate();
        UpdateQuality();

        var parts = new List<string>(8);
        if (Video.VideoSize.Width > 0)
            parts.Add($"{(int)Video.VideoSize.Width}x{(int)Video.VideoSize.Height}");
        if (_pipeline.DecodedFrameCount > 0)
            parts.Add($"{_pipeline.FramesPerSecond:0.#} fps");

        // The display rate belongs next to the source rate: how smooth the motion looks
        // depends on the ratio between them far more than on either number alone.
        var refresh = Video.CompositionPerSecond;
        if (refresh > 0) parts.Add($"{refresh:0} Hz");

        var latency = Video.AveragePresentLatencyMilliseconds;
        if (latency > 0) parts.Add($"{latency:0.#} ms");

        // The pacing cushion, which is most of that latency. Sitting near its target is
        // healthy; sitting at zero means the picture is being shown the moment it arrives,
        // jitter and all, which is the state the cushion exists to prevent.
        if (Video.PresentedFrameCount > 0) parts.Add($"buf {Video.BufferedFrameCount}");

        var lost = _pipeline.DroppedSampleCount + _pipeline.SkippedSampleCount + Video.SupersededFrameCount;
        if (lost > 0) parts.Add($"{lost} skipped");
        if (Video.IsFrozen) parts.Add("paused");
        PositionOverlays();

        CheckCadence(_pipeline.FramesPerSecond, refresh);
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

        // While a one-shot status is up, the metrics give way rather than both trying to
        // share the bar. A recording says so in its own entry every tick anyway.
        if (_transientStatus is not null) parts.Clear();

        MetricsText.Text = string.Join("   ", parts);

        if (StatsHud.Visibility == Visibility.Visible) UpdateStatsHud(refresh, latency, lost);
        UpdatePerformanceGraph();
        UpdateTray();
    }

    /// <summary>
    /// Keeps what floats over the picture clear of everything else up there: the notice bar
    /// across the top and, in fullscreen, the toolbar and status bar that come and go over the
    /// edges. Held at the same place whether those strips are showing or not, so nothing jumps
    /// each time the pointer moves.
    /// </summary>
    private void PositionOverlays()
    {
        var chromeTop = _isFullscreen ? Toolbar.Height : 0;
        var chromeBottom = _isFullscreen ? StatusBar.ActualHeight : 0;

        SetMargin(NoticeBar, new Thickness(0, chromeTop, 0, 0));
        var top = chromeTop + (NoticeBar.Visibility == Visibility.Visible ? NoticeBar.ActualHeight : 0) + 14;
        SetMargin(StatsHud, new Thickness(14, top, 0, 0));
        SetMargin(PictureBadges, new Thickness(0, top, 0, 0));
        SetMargin(ZoomBadge, new Thickness(0, top, 14, 0));

        // The foot of the picture holds the control bar, or the markup bar in its place, and the
        // activity log docks under both. Toasts rise above whichever is there - by the bar's
        // height whether it is faded in or not, so a toast never jumps as the pointer moves.
        var bottom = chromeBottom + (LogPanel.Visibility == Visibility.Visible ? LogPanel.ActualHeight : 0);
        var barLift = IsMarkupActive ? 54
            : ControlBar.Visibility == Visibility.Visible ? Math.Max(ControlBar.ActualHeight, 42) + 12
            : 0;
        SetMargin(ControlBar, new Thickness(12, 0, 12, 16 + bottom));
        SetMargin(MarkupBar, new Thickness(12, 0, 12, 16 + bottom));
        SetMargin(Toast, new Thickness(16, 0, 16, 20 + bottom + barLift));
    }

    // ------------------------------------------------------------------ quality

    private readonly ConnectionQualityMeter _quality = new();
    private ConnectionQualityLevel? _qualityShown;

    /// <summary>Watches for a connection that stays poor, and offers advice about it once,
    /// rather than for every hiccup.</summary>
    private readonly ConnectionAdvice _connectionAdvice = new();

    /// <summary>Feeds the meter this tick's running totals and redraws the bars if the verdict moved.</summary>
    private void UpdateQuality()
    {
        if (_pipeline is null || QualityIndicator.Visibility != Visibility.Visible) return;

        // Frames the display had no time for are left out: they are this PC's refresh rate
        // talking, not the network.
        var videoLost = _pipeline.DroppedSampleCount + _pipeline.SkippedSampleCount;
        var audioLost = _audio is { } audio ? audio.FilledGapCount + audio.DroppedPacketCount : 0;
        var level = _quality.Sample(TimeSpan.FromTicks(Stopwatch.GetTimestamp() * TimeSpan.TicksPerSecond / Stopwatch.Frequency),
            videoLost, audioLost);
        ShowQuality(level);

        // Sustained poor is worth naming once; the occasional hiccup is not.
        if (_connectionAdvice.ShouldAdvise(TimeSpan.FromTicks(Stopwatch.GetTimestamp() * TimeSpan.TicksPerSecond / Stopwatch.Frequency),
                level == ConnectionQualityLevel.Poor))
        {
            ShowToast("The connection has been poor for a while - moving closer to the router, or a 5 GHz network, helps most", "");
            _log.Warn("connection has been poor for some seconds; advice offered");
        }
    }

    private void ShowQuality(ConnectionQualityLevel level)
    {
        if (_qualityShown == level) return;
        _qualityShown = level;

        var (lit, brush) = level switch
        {
            ConnectionQualityLevel.Good => (3, "Success"),
            ConnectionQualityLevel.Fair => (2, "Warning"),
            ConnectionQualityLevel.Poor => (1, "Danger"),
            _ => (0, "TextTertiary"),
        };

        var bars = new[] { QualityBar1, QualityBar2, QualityBar3 };
        for (var i = 0; i < bars.Length; i++)
        {
            var on = i < lit;
            bars[i].SetResourceReference(System.Windows.Shapes.Shape.FillProperty, on ? brush : "TextTertiary");
            bars[i].Opacity = on || lit == 0 ? 1 : 0.35;
        }

        var description = ConnectionQualityMeter.Describe(level);
        QualityIndicator.ToolTip = description;
        System.Windows.Automation.AutomationProperties.SetHelpText(QualityIndicator, description);
    }

    /// <summary>
    /// Every few seconds of a recording, looks at the room left on the capture drive: a warning
    /// with the time left once it runs low, and the recording ended - while there is still room
    /// to write its index - before it runs out.
    /// </summary>
    private void CheckRecordingDiskSpace()
    {
        if (_pipeline is not { IsRecording: true } pipeline) return;
        if (DateTime.UtcNow - _diskCheckedUtc < TimeSpan.FromSeconds(5)) return;
        _diskCheckedUtc = DateTime.UtcNow;

        var free = FreeBytesFor(_settings.CaptureDirectory);
        switch (DiskSpace.Classify(free))
        {
            case DiskSpaceLevel.Critical:
                _log.Warn($"stopping the recording: {free} bytes left on the capture drive");
                RecordButton.IsChecked = false;
                ShowToast("Recording stopped - the capture drive is almost full", "\uE7BA");
                NotifyFromTray("Recording stopped", "The drive it was saving to is almost full. The recording up to now has been kept.");
                break;

            case DiskSpaceLevel.Low when !_diskLowWarned && free is { } bytes:
                _diskLowWarned = true;
                var seconds = pipeline.RecordingDuration.TotalSeconds;
                var rate = seconds > 2 ? pipeline.RecordingSizeBytes / seconds : 0;
                ShowToast(DiskSpace.TimeLeft(bytes, rate) is { } left
                    ? $"The capture drive is filling up - {DiskSpace.DescribeTimeLeft(left)} of recording left"
                    : "The capture drive is filling up", "\uE7BA");
                break;
        }
    }

    /// <summary>Free space on the drive holding <paramref name="directory"/>, or null when it
    /// cannot be told - a network share, or a drive that has gone.</summary>
    private static long? FreeBytesFor(string directory)
    {
        try
        {
            var root = System.IO.Path.GetPathRoot(System.IO.Path.GetFullPath(directory));
            if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\", StringComparison.Ordinal)) return null;
            var drive = new System.IO.DriveInfo(root);
            return drive.IsReady ? drive.AvailableFreeSpace : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void SetMargin(FrameworkElement element, Thickness margin)
    {
        if (element.Margin != margin) element.Margin = margin;
    }

    // ----------------------------------------------------------------- sparkline

    /// <summary>The last three minutes of frame rate, one sample per tick, for the graph
    /// under the statistics figures. Cleared with each session.</summary>
    private readonly MetricsHistory _fpsHistory = new(180);
    private const double SparklineWidth = 190;
    private const double SparklineHeight = 40;

    private void SampleMetricsHistory()
    {
        if (_pipeline is null || VideoHost.Visibility != Visibility.Visible)
        {
            if (_fpsHistory.Count > 0) _fpsHistory.Clear();
            return;
        }
        _fpsHistory.Add(_pipeline.FramesPerSecond);
    }

    /// <summary>Redraws the fps sparkline. A single stream geometry per tick - the cost is
    /// one small path, not a render-target rewrite, and only while it can be seen.</summary>
    private void UpdatePerformanceGraph()
    {
        if (PerformanceGraph is null) return;

        var wanted = _settings.ShowPerformanceGraph && StatsHud.Visibility == Visibility.Visible
                     && _fpsHistory.Count > 1;
        if (!wanted)
        {
            if (PerformanceGraph.Visibility != Visibility.Collapsed) PerformanceGraph.Visibility = Visibility.Collapsed;
            return;
        }

        var values = _fpsHistory.ToArray();
        var max = _fpsHistory.Max() ?? 0;
        if (max <= 0) max = 1;

        var step = SparklineWidth / (MetricsHistoryCapacity - 1);
        // The line starts at the left while the buffer fills, then scrolls right to left.
        var originX = _fpsHistory.IsFull ? 0 : SparklineWidth - (values.Length - 1) * step;

        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            Point? previous = null;
            for (var i = 0; i < values.Length; i++)
            {
                var x = originX + i * step;
                var y = SparklineHeight - 2 - Math.Clamp(values[i] / max, 0.0, 1.0) * (SparklineHeight - 4);
                var point = new Point(x, y);
                if (previous is null) context.BeginFigure(point, false, false);
                else context.LineTo(point, true, false);
                previous = point;
            }
        }
        geometry.Freeze();
        PerformanceGraphLine.Data = geometry;

        if (PerformanceGraph.Visibility != Visibility.Visible)
        {
            PerformanceGraph.Visibility = Visibility.Visible;
            PositionOverlays();
        }
    }

    private int MetricsHistoryCapacity => _fpsHistory.Capacity;

    private void UpdateSessionTimer()
    {
        if (_sessionStartedUtc is not { } started)
        {
            SessionTimer.Text = string.Empty;
            return;
        }
        var elapsed = DateTime.UtcNow - started;
        SessionTimer.Text = elapsed.TotalHours >= 1
            ? $"{(int)elapsed.TotalHours}:{elapsed.Minutes:00}:{elapsed.Seconds:00}"
            : $"{elapsed.Minutes:00}:{elapsed.Seconds:00}";
    }

    /// <summary>Encoded video bits per second, smoothed over about two seconds.</summary>
    private void UpdateBitrate()
    {
        if (_pipeline is null) return;
        var now = Stopwatch.GetTimestamp();
        var bytes = _pipeline.ReceivedByteCount;
        var seconds = (now - _ticksAtLastBitrate) / (double)Stopwatch.Frequency;
        if (seconds < 0.25) return;

        var instant = (bytes - _bytesAtLastTick) * 8 / seconds;
        _bitsPerSecond = _bitsPerSecond <= 0 ? instant : _bitsPerSecond + (instant - _bitsPerSecond) * 0.35;
        _bytesAtLastTick = bytes;
        _ticksAtLastBitrate = now;
    }

    private static string FormatBitrate(double bitsPerSecond) => bitsPerSecond switch
    {
        <= 0 => "-",
        < 1_000_000 => $"{bitsPerSecond / 1000:0} kb/s",
        _ => $"{bitsPerSecond / 1_000_000:0.0} Mb/s",
    };

    private void UpdateStatsHud(double refresh, double latency, long lost)
    {
        if (_pipeline is null) return;

        var device = ActiveSource?.Device;
        SetHud("Device", device is null ? "-" : device.Value.Model is null ? device.Value.Name : $"{device.Value.Name} ({device.Value.Model})");
        SetHud("Session", _sessionStartedUtc is { } started ? FormatDuration(DateTime.UtcNow - started) : "-");
        SetHud("Screenshots", _sessionTally.Screenshots == 0 ? "-" : $"{_sessionTally.Screenshots} taken");
        SetHud("Recordings", _sessionTally.Recordings == 0
            ? "-"
            : $"{_sessionTally.Recordings} made" + (_sessionTally.RecordedBytes > 0 ? $" · {_sessionTally.RecordedBytes / 1024.0 / 1024.0:0.#} MB" : ""));
        SetHud("Picture", Video.VideoSize.Width > 0
            ? $"{(int)Video.VideoSize.Width}x{(int)Video.VideoSize.Height}" + (_settings.Rotation != 0 ? $" ↻{_settings.Rotation}°" : "")
            : "-");
        SetHud("Source", _pipeline.FramesPerSecond > 0 ? $"{_pipeline.FramesPerSecond:0.#} fps" : "-");
        SetHud("Display", refresh > 0 ? $"{refresh:0} Hz" : "-");
        SetHud("Bit rate", FormatBitrate(_bitsPerSecond));
        SetHud("Latency", latency > 0 ? $"{latency:0} ms" : "-");
        SetHud("Buffer", $"{Video.BufferedFrameCount} frames / {Video.PresentationDelay.TotalMilliseconds:0} ms");
        // Broken out by stage: a frame lost to a decoder that could not keep up, to a
        // resynchronisation, or to the display are three different problems.
        SetHud("Skipped", lost == 0
            ? "0"
            : $"{lost}  ({_pipeline.DroppedSampleCount} late, {_pipeline.SkippedSampleCount} resync, {Video.SupersededFrameCount} paced out)");
        SetHud("Audio", _audio is { IsPlaying: true }
            ? (_audio.Muted ? "muted" : $"{_audio.BufferedDuration.TotalMilliseconds:0} ms ahead")
            : "off");
        SetHud("Recording", _pipeline.IsRecording
            ? (_pipeline.RecordingPath is null ? "waiting for a keyframe"
                : $"{(int)_pipeline.RecordingDuration.TotalMinutes:00}:{_pipeline.RecordingDuration.Seconds:00}" +
                  (_pipeline.RecordingHasAudio ? " with audio" : ""))
            : "-");
    }

    /// <summary>Writes one row of the overlay, adding the row the first time it is seen.</summary>
    private void SetHud(string label, string value)
    {
        if (!_hudValues.TryGetValue(label, out var block))
        {
            var row = StatsGrid.RowDefinitions.Count;
            StatsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var name = new TextBlock { Text = label, Style = (System.Windows.Style)StatsGrid.FindResource("HudLabel") };
            Grid.SetRow(name, row);
            StatsGrid.Children.Add(name);

            block = new TextBlock { Style = (System.Windows.Style)StatsGrid.FindResource("HudValue") };
            Grid.SetRow(block, row);
            Grid.SetColumn(block, 1);
            StatsGrid.Children.Add(block);
            _hudValues[label] = block;
        }

        if (block.Text != value) block.Text = value;
    }

    /// <summary>
    /// Warns when the display refresh rate is not a whole multiple of the rate the phone is
    /// sending.
    /// <para>
    /// At 100 Hz with a 60 fps source each picture has to be held for either one refresh or
    /// two, in a repeating but uneven pattern. The motion judders, and no amount of work in
    /// this application can prevent it - the only fix is to make the two rates divide. It is
    /// worth naming because every other number on screen looks healthy while it happens.
    /// </para>
    /// </summary>
    private void CheckCadence(double sourceRate, double refreshRate)
    {
        if (sourceRate < 5 || refreshRate < 5) return;

        var ratio = refreshRate / sourceRate;
        var nearestWhole = Math.Round(ratio);
        var mismatched = nearestWhole >= 1 && Math.Abs(ratio - nearestWhole) > 0.12;

        // Both rates are measured, and both wobble for a second or two after a session
        // starts or a window is dragged between monitors. A warning raised on one such
        // reading names rates that were never really in force, so a run of agreeing
        // readings is required before anything is said.
        _cadenceStreak = mismatched == _cadenceStreakMismatched ? _cadenceStreak + 1 : 1;
        _cadenceStreakMismatched = mismatched;
        if (_cadenceStreak < CadenceStreakNeeded) return;

        if (mismatched == _cadenceWarned) return;
        _cadenceWarned = mismatched;

        for (var i = _warnings.Count - 1; i >= 0; i--)
            if (_warnings[i].Kind == WarningKind.Cadence) _warnings.RemoveAt(i);

        if (!mismatched)
        {
            NoticeBar.Visibility = Visibility.Collapsed;
            PositionOverlays();
            return;
        }

        var sender = _demo is null ? "the phone is" : "the demo is";
        var title = $"Your display runs at {refreshRate:0} Hz and {sender} sending {sourceRate:0} fps";
        var detail = $"That is {ratio:0.00} refreshes per frame, so each one is held for an uneven number of " +
                     "them and the motion judders. Setting the display to 60 Hz while mirroring makes it one to one.";

        _warnings.Add(new WarningItem(title, detail, WarningKind.Cadence));

        // Filled in even when it cannot be shown yet, so the mini player can put it back up
        // with the right words when it closes.
        NoticeTitle.Text = title;
        NoticeDetail.Text = detail;

        // The idle panel is hidden while streaming, which is exactly when this matters.
        // Not in the mini player, though, which has no room for it.
        if (_noticeDismissed || _isMiniPlayer) return;
        NoticeBar.Visibility = Visibility.Visible;
        // The overlay shares this corner, and the bar has just changed height.
        Dispatcher.BeginInvoke(PositionOverlays, DispatcherPriority.Loaded);
    }

    private void OnDismissNotice(object sender, RoutedEventArgs e)
    {
        // Dismissed for this session only: the rate can change, and a fresh session should
        // say so again rather than stay silent about a problem the user may have forgotten.
        _noticeDismissed = true;
        NoticeBar.Visibility = Visibility.Collapsed;
        PositionOverlays();

        // If the session ended and took its warning with it, the dismiss must not be what
        // pins the flag; the next tick would otherwise leave the bar down for a warning
        // that no longer exists, or up again for one that does.
        if (!_warnings.Any(w => w.Kind == WarningKind.Cadence)) _cadenceWarned = false;
    }
}
