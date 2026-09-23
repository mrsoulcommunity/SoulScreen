using System.Windows;
using SoulScreen.Android;
using SoulScreen.Core.Sources;
using SoulScreen.Media;

namespace SoulScreen.App;

/// <summary>
/// Mirrors an Android phone over adb instead of AirPlay: USB debugging, or wireless
/// debugging once paired once over USB - adb treats both the same, and so does this.
/// <para>
/// Mirrors <see cref="StartDemoAsync"/>/<see cref="EndDemoAsync"/> in MainWindow.xaml.cs:
/// the receiver is stopped rather than left running beside it, for the same reason - a
/// second live source next to the one already on screen is exactly the concurrent-source
/// complexity multi-device grid mirroring turned out not to be worth keeping. Only one of
/// <c>_receiver</c>, <c>_demo</c> and <c>_android</c> is ever non-null at a time.
/// </para>
/// </summary>
public partial class MainWindow
{
    /// <summary>Whether the receiver was running when Android mirroring replaced it.</summary>
    private bool _resumeReceiverAfterAndroid;

    private async Task StartAndroidAsync()
    {
        if (!AdbRuntime.IsAvailable)
        {
            SetIdleState("adb was not found", AdbRuntime.UnavailableReason ?? "", MirrorSourceState.Faulted);
            return;
        }

        _resumeReceiverAfterAndroid = _receiver is not null;
        await StopReceiverAsync();

        try
        {
            // Whether audio actually arrives depends on which of Android's two capture
            // paths ends up running for whatever phone connects (see AndroidMirrorSource) -
            // scrcpy carries it, the screenrecord fallback never does. Recording is enabled
            // the same way the AirPlay receiver does it either way; a session with no audio
            // format ever announced just records video-only, the same as it would if an
            // AirPlay sender chose not to send sound.
            _pipeline = new VideoPipeline { RecordAudio = _settings.RecordAudio && _settings.EnableAudio };
            _pipeline.FrameDecoded += OnFrameDecoded;
            _pipeline.RecordingFinished += OnRecordingFinished;
        }
        catch (FFmpegUnavailableException ex)
        {
            _log.Warn(ex.Message);
            _pipeline = null;
        }

        if (_settings.EnableAudio && FFmpegRuntime.IsAvailable)
        {
            _audio = new AudioPipeline
            {
                Muted = MuteButton.IsChecked == true,
                Volume = (float)_settings.Volume,
                OutputDeviceId = _settings.AudioOutputDeviceId,
                Reserve = AppSettings.AudioReserveFor(AppSettings.PresentationDelayFor(_settings.Latency)),
            };
            _audio.FormatChanged += (_, format) => Dispatcher.BeginInvoke(() => _log.Info($"audio: {format}"));
        }

        _android = new AndroidMirrorSource();
        _android.StateChanged += OnReceiverStateChanged;
        _android.VideoFormatChanged += OnVideoFormatChanged;
        _pipeline?.Attach(_android);
        _audio?.Attach(_android);

        try
        {
            await _android.StartAsync();
            SetReceiverToggle(running: false);
            UpdateTray();
        }
        catch (Exception ex)
        {
            _log.Error("could not start Android mirroring", ex);
            SetIdleState("Android mirroring could not start", ex.Message, MirrorSourceState.Faulted);
            await StopAndroidAsync();
        }
    }

    private async Task StopAndroidAsync()
    {
        var android = _android;
        _android = null;
        if (android is not null)
        {
            android.StateChanged -= OnReceiverStateChanged;
            android.VideoFormatChanged -= OnVideoFormatChanged;
            await android.DisposeAsync();
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
    }

    private async void OnAndroidRequested()
    {
        if (!TryBeginReceiverWork()) return;
        try { await StartAndroidAsync(); }
        finally { EndReceiverWork(); }
    }

    /// <summary>Fire-and-forget wrapper, matching <see cref="DisconnectDemoAsync"/>.</summary>
    private async void DisconnectAndroidAsync()
    {
        try { await EndAndroidAsync(); }
        catch (Exception ex) { _log.Warn("Android disconnect failed", ex); }
    }

    /// <summary>Ends Android mirroring and brings the receiver back, if it was running before.</summary>
    private async Task EndAndroidAsync()
    {
        if (!TryBeginReceiverWork()) return;
        try
        {
            await StopAndroidAsync();
            if (_resumeReceiverAfterAndroid)
            {
                await StartReceiverAsync();
            }
            else
            {
                ShowIdle();
                SetReceiverToggle(running: false);
                SetIdleState("Receiver stopped", "Start it when you are ready to mirror.", MirrorSourceState.Stopped);
                UpdateTray();
            }
        }
        finally
        {
            EndReceiverWork();
        }
    }
}
