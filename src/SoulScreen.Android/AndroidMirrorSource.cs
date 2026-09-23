using System.Diagnostics;
using SoulScreen.Core.Logging;
using SoulScreen.Core.Media;
using SoulScreen.Core.Sources;

namespace SoulScreen.Android;

public sealed class AndroidMirrorOptions
{
    /// <summary>Longest edge captured, in pixels. Full native resolution on a modern phone
    /// (1440x3200 or more) costs decode time for no visible gain in a mirrored window; this
    /// keeps things smooth without a visible drop in sharpness.</summary>
    public int MaxDimension { get; init; } = 1600;

    public int BitRate { get; init; } = 12_000_000;

    /// <summary>"screenrecord --output-format=h264 -" needs Android 12 (API 31): earlier
    /// releases only ever write an mp4 container, which cannot be streamed frame-by-frame.</summary>
    public int MinimumSdkVersion { get; init; } = 31;
}

/// <summary>
/// A wired or wireless Android mirroring source built on adb.
/// <para>
/// Unlike the iPhone USB transport (<see cref="SoulScreen.Usb.UsbTransport"/>), this needs
/// no driver swap: Android's standard adb interface is exactly what the phone shows up as
/// once "USB debugging" is on, over the cable or, after the first pairing, over
/// Wi-Fi ("wireless debugging") - adb treats both the same way, so this source does too.
/// </para>
/// <para>
/// Two ways of getting the picture, tried in order. When <see cref="ScrcpyServerRuntime"/>
/// has the scrcpy server available, <see cref="ScrcpySession"/> pushes and drives it: audio,
/// and rotation handled by the phone re-announcing its capture session rather than the
/// picture staying letterboxed. Failing that - the jar was never fetched, or the handshake
/// does not complete - this falls back to driving <c>screenrecord</c> directly: video only,
/// fixed orientation for the life of the connection, but needs nothing pushed to the phone
/// and works down to whatever screenrecord's own <c>--output-format=h264</c> requires
/// (Android 12). Both hand the render pipeline the exact same shape of events, so neither
/// the pipeline nor the UI knows which one is actually running.
/// </para>
/// </summary>
public sealed class AndroidMirrorSource : IMirrorSource
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1.5);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(3);

    private readonly ILogger _log = Log.For("android");
    private readonly AndroidMirrorOptions _options;

    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private MirrorSourceState _state = MirrorSourceState.Stopped;

    /// <summary>Where the screenrecord fallback's picture timestamps come from. Started once
    /// per source, not per capture attempt, so a restarted capture keeps moving forward on
    /// the same clock - see the comment where it is read.</summary>
    private readonly Stopwatch _captureClock = Stopwatch.StartNew();

    /// <summary>Set when this phone's screenrecord rejected <c>--time-limit 0</c>; remembered
    /// for the rest of this mirroring session rather than probed again on every reconnect.</summary>
    private bool _screenRecordRejectsUnlimitedTimeLimit;

    /// <summary>The state, device and message last announced, so an unchanged re-detection
    /// raises nothing - see <see cref="SetState"/>.</summary>
    private SourceDeviceInfo? _lastAnnouncedDevice;
    private string? _lastAnnouncedMessage;

    public AndroidMirrorSource(AndroidMirrorOptions? options = null)
    {
        _options = options ?? new AndroidMirrorOptions();
    }

    public string Id => "android";

    public string DisplayName => "Android (ADB)";

    public MirrorSourceState State => _state;

    public SourceDeviceInfo? Device { get; private set; }

    public event EventHandler<MirrorSourceStateChangedEventArgs>? StateChanged;
    public event EventHandler<VideoFormat>? VideoFormatChanged;
    public event EventHandler<MediaSample>? VideoSampleReady;

    /// <summary>Raised only via the scrcpy path; never fires when the screenrecord fallback
    /// is in use, since screenrecord carries no audio track.</summary>
    public event EventHandler<AudioFormat>? AudioFormatChanged;

    /// <summary>See <see cref="AudioFormatChanged"/>.</summary>
    public event EventHandler<MediaSample>? AudioSampleReady;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_cts is not null) return Task.CompletedTask;

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        SetState(MirrorSourceState.Ready);
        _loopTask = RunAsync(_cts.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        var cts = Interlocked.Exchange(ref _cts, null);
        if (cts is null) return;

        await cts.CancelAsync().ConfigureAwait(false);

        var loop = _loopTask;
        _loopTask = null;
        if (loop is not null)
        {
            try { await loop.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }

        cts.Dispose();
        Device = null;
        SetState(MirrorSourceState.Stopped);
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    /// <summary>Watches for a device, streams it until it is lost, then watches again -
    /// a replug or a Wi-Fi drop recovers on its own rather than needing the receiver
    /// toggled off and back on.</summary>
    private async Task RunAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var device = await WaitForDeviceAsync(token).ConfigureAwait(false);
                if (device is null) continue;

                var streamed = await StreamDeviceAsync(device.Value, token).ConfigureAwait(false);

                // An attempt that produced no picture at all - the capture refused to start,
                // screenrecord rejected an argument, the screen is locked - ends instantly
                // while the device stays plugged in, so WaitForDeviceAsync would hand it
                // straight back. Without this the loop would spawn adb and screenrecord
                // again in an unbroken cycle.
                if (!streamed) await DelayOrCancel(RetryDelay, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Cooperative shutdown or a retry delay cut short by StopAsync.
            }
            catch (Exception ex)
            {
                _log.Error("android capture loop failed", ex);
                SetState(MirrorSourceState.Faulted, message: ex.Message);
                await DelayOrCancel(RetryDelay, token).ConfigureAwait(false);
            }
        }
    }

    private async Task<AdbDevice?> WaitForDeviceAsync(CancellationToken token)
    {
        var adb = AdbRuntime.ExecutablePath;
        if (adb is null)
        {
            SetState(MirrorSourceState.Faulted, message: AdbRuntime.UnavailableReason);
            await DelayOrCancel(TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
            return null;
        }

        SetState(MirrorSourceState.Ready);

        while (!token.IsCancellationRequested)
        {
            IReadOnlyList<AdbDevice> devices;
            try { devices = await AdbCommands.ListDevicesAsync(adb, token).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.Warn("could not list adb devices", ex);
                devices = [];
            }

            var online = devices.FirstOrDefault(d => d.State == AdbDeviceState.Online);
            if (online.Serial is not null) return online;

            var unauthorized = devices.FirstOrDefault(d => d.State == AdbDeviceState.Unauthorized);
            SetState(
                unauthorized.Serial is not null ? MirrorSourceState.Connecting : MirrorSourceState.Ready,
                unauthorized.Serial is not null ? new SourceDeviceInfo(unauthorized.DisplayModel, Identifier: unauthorized.Serial) : null,
                unauthorized.Serial is not null ? "On the phone, tap Allow on the \"Allow USB debugging?\" prompt." : null);

            await DelayOrCancel(PollInterval, token).ConfigureAwait(false);
        }

        return null;
    }

    private async Task<bool> StreamDeviceAsync(AdbDevice device, CancellationToken token)
    {
        var adb = AdbRuntime.ExecutablePath!;
        var deviceInfo = new SourceDeviceInfo(device.DisplayModel, device.DisplayModel, device.Serial);

        Device = deviceInfo;
        SetState(MirrorSourceState.Connecting, deviceInfo);

        if (ScrcpyServerRuntime.IsAvailable)
        {
            try
            {
                // Ran to a clean end (device lost, or cancelled) - reconnect will try scrcpy again.
                return await StreamViaScrcpyAsync(adb, device, deviceInfo, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _log.Warn($"scrcpy could not be used for {deviceInfo} ({ex.Message}); falling back to screenrecord.");
            }
        }

        return await StreamViaScreenRecordAsync(adb, device, deviceInfo, token).ConfigureAwait(false);
    }

    private async Task<bool> StreamViaScrcpyAsync(string adb, AdbDevice device, SourceDeviceInfo deviceInfo, CancellationToken token)
    {
        await using var session = new ScrcpySession(adb, device.Serial, new ScrcpyOptions
        {
            MaxDimension = _options.MaxDimension,
            VideoBitRate = _options.BitRate,
        });

        var sawSample = false;
        session.VideoFormatChanged += (_, format) => VideoFormatChanged?.Invoke(this, format);
        session.AudioFormatChanged += (_, format) => AudioFormatChanged?.Invoke(this, format);
        session.AudioSampleReady += (_, sample) => AudioSampleReady?.Invoke(this, sample);
        session.VideoSampleReady += (_, sample) =>
        {
            if (!sawSample)
            {
                sawSample = true;
                _log.Info($"streaming {deviceInfo} via scrcpy" + (session.DeviceName is { Length: > 0 } n ? $" ({n})" : ""));
                SetState(MirrorSourceState.Streaming, deviceInfo);
            }
            VideoSampleReady?.Invoke(this, sample);
        };

        try
        {
            await session.RunAsync(token).ConfigureAwait(false);
        }
        finally
        {
            Device = null;
            if (!token.IsCancellationRequested) SetState(MirrorSourceState.Ready);
        }

        return sawSample;
    }

    private async Task<bool> StreamViaScreenRecordAsync(string adb, AdbDevice device, SourceDeviceInfo deviceInfo, CancellationToken token)
    {
        var sdk = await AdbCommands.GetSdkVersionAsync(adb, device.Serial, token).ConfigureAwait(false);
        if (sdk is null || sdk < _options.MinimumSdkVersion)
        {
            SetState(MirrorSourceState.Faulted, deviceInfo,
                $"This phone reports Android SDK {sdk?.ToString() ?? "unknown"}; SoulScreen's " +
                $"cable link needs Android 12 (SDK {_options.MinimumSdkVersion}) or newer, or scrcpy-server " +
                "(run 'pwsh tools/fetch-scrcpy-server.ps1') for older phones.");
            await DelayOrCancel(TimeSpan.FromSeconds(10), token).ConfigureAwait(false);
            return false;
        }

        var size = await AdbCommands.GetScreenSizeAsync(adb, device.Serial, token).ConfigureAwait(false);
        if (size is null)
        {
            SetState(MirrorSourceState.Faulted, deviceInfo, "Could not read the phone's screen size over adb.");
            await DelayOrCancel(RetryDelay, token).ConfigureAwait(false);
            return false;
        }

        var (width, height) = FitCaptureSize(size.Value.Width, size.Value.Height, _options.MaxDimension);

        _log.Info($"streaming {deviceInfo} via screenrecord at {width}x{height}, {_options.BitRate / 1_000_000.0:0.#} Mbps");

        try
        {
            // "--time-limit 0" means "no limit" on recent Android builds, but older ones -
            // Android 12 among them, which is exactly the floor this fallback supports -
            // reject 0 outright (their accepted range is [1,180] seconds) and exit before a
            // single frame is written, which would leave that phone with no picture at all.
            // So 0 is tried first and a run that produced nothing falls back to 180 s,
            // recycling the capture when it ends; a rejection named in stderr is remembered
            // so later attempts do not pay for the failed probe again.
            var timeLimit = _screenRecordRejectsUnlimitedTimeLimit ? "180" : "0";
            var streamed = await RunScreenRecordAttemptAsync(adb, device, deviceInfo, width, height, timeLimit, token).ConfigureAwait(false);
            if (!streamed && !token.IsCancellationRequested && timeLimit == "0")
            {
                _log.Info("screenrecord produced nothing with --time-limit 0; retrying with the 180 s limit every build accepts");
                streamed = await RunScreenRecordAttemptAsync(adb, device, deviceInfo, width, height, "180", token).ConfigureAwait(false);
            }

            return streamed;
        }
        finally
        {
            Device = null;
            if (!token.IsCancellationRequested) SetState(MirrorSourceState.Ready);
        }
    }

    private async Task<bool> RunScreenRecordAttemptAsync(
        string adb, AdbDevice device, SourceDeviceInfo deviceInfo, int width, int height, string timeLimit, CancellationToken token)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(adb)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };
        foreach (var arg in new[]
                 {
                     "-s", device.Serial, "exec-out", "screenrecord", "--output-format=h264",
                     "--time-limit", timeLimit, "--size", $"{width}x{height}",
                     "--bit-rate", _options.BitRate.ToString(),
                     "-",
                 })
        {
            process.StartInfo.ArgumentList.Add(arg);
        }

        if (!process.Start())
            throw new InvalidOperationException("adb.exe did not start.");

        var stderrTask = DrainStderrAsync(process, device.Serial, token);
        var assembler = new AndroidH264Assembler();
        var sawSample = false;

        try
        {
            var buffer = new byte[64 * 1024];
            var stdout = process.StandardOutput.BaseStream;

            while (!token.IsCancellationRequested)
            {
                int read;
                try { read = await stdout.ReadAsync(buffer, token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                catch (IOException) { break; } // process died mid-read: device unplugged.

                if (read <= 0) break; // adb exited - device lost, or the phone locked capture off.

                foreach (var unit in assembler.Feed(buffer.AsSpan(0, read)))
                {
                    switch (unit)
                    {
                        case AndroidFormatUnit format:
                            VideoFormatChanged?.Invoke(this, format.Format);
                            break;

                        case AndroidSampleUnit sample:
                            if (!sawSample)
                            {
                                sawSample = true;
                                SetState(MirrorSourceState.Streaming, deviceInfo);
                            }

                            // One clock for the whole mirroring session rather than one per
                            // attempt: a capture that ends (screenrecord's 180 s limit,
                            // phone locked and back) and restarts must keep handing the
                            // recorder timestamps that move forward, or the recording's
                            // timeline stops where the previous attempt left it.
                            using (var media = MediaSample.Copy(sample.Payload, (long)_captureClock.Elapsed.TotalMicroseconds, sample.IsKeyFrame))
                                VideoSampleReady?.Invoke(this, media);
                            break;
                    }
                }
            }
        }
        finally
        {
            TryKill(process);
            await stderrTask.ConfigureAwait(false);
        }

        return sawSample;
    }

    /// <summary>screenrecord prints nothing on success; on failure ("Unable to acquire
    /// screen") it goes to stderr. Reading it keeps the pipe from filling and stalling the
    /// child, and puts the reason in the log when a phone refuses to stream. It also carries
    /// the verdict on --time-limit 0, which older builds reject before writing anything.</summary>
    private async Task DrainStderrAsync(Process process, string serial, CancellationToken token)
    {
        try
        {
            string? line;
            while ((line = await process.StandardError.ReadLineAsync(token).ConfigureAwait(false)) is not null)
            {
                if (line.Length == 0) continue;
                _log.Warn($"screenrecord ({serial}): {line}");

                // Android 12's own wording: "Time limit 0s outside acceptable range [1,180]".
                if (line.Contains("Time limit", StringComparison.OrdinalIgnoreCase)
                    && line.Contains("outside acceptable range", StringComparison.OrdinalIgnoreCase))
                {
                    _screenRecordRejectsUnlimitedTimeLimit = true;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
    }

    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { } // already exited between the check and the call.
    }

    /// <summary>Scales to at most <paramref name="maxDimension"/> on the longest edge,
    /// keeping aspect ratio, and rounds down to even numbers - H.264 requires it.</summary>
    private static (int Width, int Height) FitCaptureSize(int width, int height, int maxDimension)
    {
        var longest = Math.Max(width, height);
        if (longest <= maxDimension) return (EvenDown(width), EvenDown(height));

        var scale = maxDimension / (double)longest;
        return (EvenDown((int)Math.Round(width * scale)), EvenDown((int)Math.Round(height * scale)));

        static int EvenDown(int v) => v - (v % 2);
    }

    private static Task DelayOrCancel(TimeSpan delay, CancellationToken token)
        => Task.Delay(delay, token).ContinueWith(_ => { }, CancellationToken.None);

    private void SetState(MirrorSourceState state, SourceDeviceInfo? device = null, string? message = null)
    {
        if (device is not null) Device = device;

        // The poll loop re-detects the same situation over and over - a phone sitting on its
        // "Allow USB debugging?" prompt, or adb still missing - with an identical state,
        // device and message each time. Forwarding those on would redraw the idle screen
        // every poll interval as though something had changed.
        if (_state == state
            && Equals(Device, _lastAnnouncedDevice)
            && string.Equals(message, _lastAnnouncedMessage, StringComparison.Ordinal))
        {
            return;
        }

        _state = state;
        _lastAnnouncedDevice = Device;
        _lastAnnouncedMessage = message;
        StateChanged?.Invoke(this, new MirrorSourceStateChangedEventArgs(state, Device, message));
    }
}
