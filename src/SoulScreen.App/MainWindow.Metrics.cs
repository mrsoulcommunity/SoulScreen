using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
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
        if (source.State != MirrorSourceState.Streaming)
        {
            MetricsText.Text = string.Empty;
            return;
        }

        UpdateBitrate();

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
        PositionStatsHud();

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
        UpdateTray();
    }

    /// <summary>Keeps the overlay clear of the notice bar, which shares the same corner.</summary>
    private void PositionStatsHud()
    {
        var top = NoticeBar.Visibility == Visibility.Visible ? NoticeBar.ActualHeight + 14 : 14;
        if (Math.Abs(StatsHud.Margin.Top - top) > 0.5) StatsHud.Margin = new Thickness(14, top, 0, 0);
    }

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
            PositionStatsHud();
            return;
        }

        var sender = _demo is null ? "the phone is" : "the demo is";
        var title = $"Your display runs at {refreshRate:0} Hz and {sender} sending {sourceRate:0} fps";
        var detail = $"That is {ratio:0.00} refreshes per frame, so each one is held for an uneven number of " +
                     "them and the motion judders. Setting the display to 60 Hz while mirroring makes it one to one.";

        _warnings.Add(new WarningItem(title, detail, WarningKind.Cadence));

        // The idle panel is hidden while streaming, which is exactly when this matters.
        if (_noticeDismissed) return;
        NoticeTitle.Text = title;
        NoticeDetail.Text = detail;
        NoticeBar.Visibility = Visibility.Visible;
        // The overlay shares this corner, and the bar has just changed height.
        Dispatcher.BeginInvoke(PositionStatsHud, DispatcherPriority.Loaded);
    }

    private void OnDismissNotice(object sender, RoutedEventArgs e)
    {
        // Dismissed for this session only: the rate can change, and a fresh session should
        // say so again rather than stay silent about a problem the user may have forgotten.
        _noticeDismissed = true;
        NoticeBar.Visibility = Visibility.Collapsed;
        PositionStatsHud();

        // If the session ended and took its warning with it, the dismiss must not be what
        // pins the flag; the next tick would otherwise leave the bar down for a warning
        // that no longer exists, or up again for one that does.
        if (!_warnings.Any(w => w.Kind == WarningKind.Cadence)) _cadenceWarned = false;
    }
}
