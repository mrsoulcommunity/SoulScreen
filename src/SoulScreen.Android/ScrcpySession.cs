using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using SoulScreen.Core.Logging;
using SoulScreen.Core.Media;

namespace SoulScreen.Android;

public sealed class ScrcpyOptions
{
    /// <summary>Longest captured edge, in pixels; scrcpy's own "max_size".</summary>
    public int MaxDimension { get; init; } = 1600;

    public int VideoBitRate { get; init; } = 12_000_000;
    public int MaxFps { get; init; } = 60;
    public bool EnableAudio { get; init; } = true;
}

/// <summary>
/// Pushes and drives one scrcpy server session on a device: the source of Android audio
/// and rotation-aware video, both of which "screenrecord" cannot provide - see
/// <see cref="AndroidMirrorSource"/>, which uses this when available and falls back to
/// screenrecord otherwise.
/// <para>
/// Runs entirely over adb: push the jar, forward a local TCP port to the abstract socket
/// the server listens on, launch it under <c>app_process</c>, then connect to that port
/// once for video and, if enabled, again for audio - scrcpy's own client does the same,
/// just written in C rather than C#. Wire format verified against scrcpy's client source
/// (app/src/demuxer.c) and doc/develop.md; see <see cref="ScrcpyProtocol"/>.
/// </para>
/// </summary>
public sealed class ScrcpySession : IAsyncDisposable
{
    private readonly ILogger _log = Log.For("scrcpy");
    private readonly string _adbPath;
    private readonly string _serial;
    private readonly ScrcpyOptions _options;

    private Process? _serverProcess;
    private TcpClient? _videoClient;
    private TcpClient? _audioClient;
    private int _forwardedPort;
    private bool _forwardActive;

    /// <summary>Set once the audio format has been raised, so the handshake and the audio
    /// loop cannot announce it twice - two announcements would restart the playback
    /// timeline for no reason.</summary>
    private bool _audioFormatAnnounced;

    public ScrcpySession(string adbPath, string serial, ScrcpyOptions? options = null)
    {
        _adbPath = adbPath;
        _serial = serial;
        _options = options ?? new ScrcpyOptions();
    }

    public event EventHandler<VideoFormat>? VideoFormatChanged;
    public event EventHandler<MediaSample>? VideoSampleReady;
    public event EventHandler<AudioFormat>? AudioFormatChanged;
    public event EventHandler<MediaSample>? AudioSampleReady;

    /// <summary>The phone's own name, read off the wire once the video socket connects.</summary>
    public string? DeviceName { get; private set; }

    /// <summary>
    /// Runs until the device is lost or <paramref name="token"/> is cancelled, then returns
    /// normally. Throws only for a handshake failure (push, tunnel, server start, or the
    /// initial preamble/codec exchange) - <see cref="AndroidMirrorSource"/> takes that as
    /// "scrcpy is not going to work for this device right now" and falls back to
    /// screenrecord for the attempt; a mid-session drop is not distinguished from a clean
    /// disconnect, since either way the right response is the same reconnect loop.
    /// </summary>
    public async Task RunAsync(CancellationToken token)
    {
        await AdbCommands.PushAsync(
            _adbPath, _serial, ScrcpyServerRuntime.LocalPath!, ScrcpyServerRuntime.RemoteJarPath, token).ConfigureAwait(false);

        _forwardedPort = ReserveLocalPort();
        await AdbCommands.ForwardAsync(_adbPath, _serial, _forwardedPort, "scrcpy", token).ConfigureAwait(false);
        _forwardActive = true;

        _serverProcess = StartServerProcess();

        // Both sockets are opened before anything is read. Two reasons, both verified against
        // scrcpy's own client (app/src/server.c) and the server's DesktopConnection.java:
        // the adb forward accepts a TCP connection even when nothing is listening behind it,
        // so a successful connect proves nothing - only the dummy byte does - and the server
        // accepts the audio socket *inside* DesktopConnection.open(), before it writes the
        // device name, so reading the name while waiting to open audio would deadlock.
        await ConnectSocketsAsync(_forwardedPort, token).ConfigureAwait(false);

        var videoStream = _videoClient!.GetStream();

        // The dummy byte was consumed as the connect probe; what follows on the first socket
        // is the fixed-width device name, then the video codec id.
        var deviceName = new byte[ScrcpyDeviceMeta.DeviceNameFieldLength];
        await videoStream.ReadExactlyAsync(deviceName, token).ConfigureAwait(false);
        DeviceName = ScrcpyDeviceMeta.ParseDeviceName(deviceName);

        var videoCodecId = new byte[4];
        await videoStream.ReadExactlyAsync(videoCodecId, token).ConfigureAwait(false);
        if (!videoCodecId.AsSpan().SequenceEqual(ScrcpyCodecIds.VideoH264))
        {
            throw new InvalidOperationException(
                $"scrcpy announced an unsupported video codec (0x{Convert.ToHexString(videoCodecId)}); expected h264.");
        }

        var audioTask = Task.CompletedTask;
        if (_options.EnableAudio)
        {
            try { audioTask = await StartAudioAsync(token).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _log.Warn($"audio unavailable for this session, continuing video-only: {ex.Message}");
            }
        }

        await RunVideoLoopAsync(videoStream, token).ConfigureAwait(false);

        try { await audioTask.ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { _log.Warn($"audio loop ended unexpectedly: {ex.Message}"); }
    }

    internal async Task RunVideoLoopAsync(Stream stream, CancellationToken token)
    {
        var headerBuffer = new byte[ScrcpyPacketHeader.Size];

        while (!token.IsCancellationRequested)
        {
            if (!await TryReadExactlyAsync(stream, headerBuffer, token).ConfigureAwait(false)) return;
            var header = ScrcpyPacketHeader.Parse(headerBuffer);

            if (header.IsSessionPacket)
            {
                // scrcpy sends this again after every rotation restart; the actual width and
                // height SoulScreen uses come from the SPS in the config packet that follows,
                // via the same AvcDecoderConfiguration path every other transport uses, so
                // this is purely informational.
                _log.Debug($"scrcpy capture session: {header.SessionWidth}x{header.SessionHeight}");
                continue;
            }

            if (header.PayloadSize <= 0) continue;

            if (header.IsConfig)
            {
                var configBytes = new byte[header.PayloadSize];
                if (!await TryReadExactlyAsync(stream, configBytes, token).ConfigureAwait(false)) return;

                var configuration = AvcDecoderConfiguration.FromAnnexB(configBytes);
                if (configuration is null) continue;

                configuration.TryGetDimensions(out var width, out var height);
                VideoFormatChanged?.Invoke(this, new VideoFormat(VideoCodec.H264, width, height, configBytes, 0));
                continue;
            }

            var pooled = ArrayPool<byte>.Shared.Rent(header.PayloadSize);
            if (!await TryReadExactlyAsync(stream, pooled.AsMemory(0, header.PayloadSize), token).ConfigureAwait(false))
            {
                ArrayPool<byte>.Shared.Return(pooled);
                return;
            }

            using var sample = MediaSample.AdoptPooled(pooled, header.PayloadSize, header.PtsUs, header.IsKeyFrame);
            VideoSampleReady?.Invoke(this, sample);
        }
    }

    internal async Task RunAudioLoopAsync(Stream stream, CancellationToken token)
    {
        var headerBuffer = new byte[ScrcpyPacketHeader.Size];

        while (!token.IsCancellationRequested)
        {
            if (!await TryReadExactlyAsync(stream, headerBuffer, token).ConfigureAwait(false)) return;
            var header = ScrcpyPacketHeader.Parse(headerBuffer);

            // "For the audio stream, there are no session packets" (doc/develop.md) - and raw
            // PCM has no setup blob to send as a config packet either - but both are read
            // defensively rather than assumed, in case a future server version differs.
            if (header.IsSessionPacket || header.PayloadSize <= 0) continue;

            // Normally already announced by StartAudioAsync; this covers a loop driven on its
            // own (and a future server that writes the codec id later).
            AnnounceAudioFormat();

            if (header.IsConfig)
            {
                var discard = ArrayPool<byte>.Shared.Rent(header.PayloadSize);
                var ok = await TryReadExactlyAsync(stream, discard.AsMemory(0, header.PayloadSize), token).ConfigureAwait(false);
                ArrayPool<byte>.Shared.Return(discard);
                if (!ok) return;
                continue;
            }

            var pooled = ArrayPool<byte>.Shared.Rent(header.PayloadSize);
            if (!await TryReadExactlyAsync(stream, pooled.AsMemory(0, header.PayloadSize), token).ConfigureAwait(false))
            {
                ArrayPool<byte>.Shared.Return(pooled);
                return;
            }

            using var sample = MediaSample.AdoptPooled(pooled, header.PayloadSize, header.PtsUs, isKeyFrame: false);
            AudioSampleReady?.Invoke(this, sample);
        }
    }

    /// <summary>
    /// Opens the video socket (and the audio one, when enabled) against the adb forward,
    /// retrying until the server on the phone is actually up.
    /// <para>
    /// <c>adb forward</c> completes the TCP connection itself and only then tries to reach
    /// the device side, so connecting succeeds long before <c>app_process</c> has booted the
    /// server - scrcpy documents this exact behaviour and probes with the dummy byte for the
    /// same reason ("the client connection does not fail as long as there is an adb forward
    /// redirection, even if nothing is listening on the device side"). Retrying the connect
    /// alone would therefore never retry at all; only a failed <i>read</i> means "not yet".
    /// </para>
    /// <para>The audio socket is opened here, immediately after the probe succeeds and before
    /// any metadata is read, because the server will not write the device name until it has
    /// accepted every socket it is expecting.</para>
    /// </summary>
    internal async Task ConnectSocketsAsync(int port, CancellationToken token)
    {
        // scrcpy's own numbers: 100 attempts at 100 ms, ten seconds for app_process to boot.
        const int maxAttempts = 100;
        var delay = TimeSpan.FromMilliseconds(100);

        Exception? last = null;
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            token.ThrowIfCancellationRequested();

            var video = new TcpClient();
            try
            {
                await video.ConnectAsync(IPAddress.Loopback, port, token).ConfigureAwait(false);
                video.NoDelay = true;

                var probe = new byte[ScrcpyDeviceMeta.DummyByteLength];
                if (!await TryReadExactlyAsync(video.GetStream(), probe, token).ConfigureAwait(false))
                {
                    last = new IOException("nothing is listening behind the adb forward yet");
                    video.Dispose();
                    await Task.Delay(delay, token).ConfigureAwait(false);
                    continue;
                }

                _videoClient = video;

                if (_options.EnableAudio)
                {
                    var audio = new TcpClient();
                    try
                    {
                        await audio.ConnectAsync(IPAddress.Loopback, port, token).ConfigureAwait(false);
                        audio.NoDelay = true;
                    }
                    catch
                    {
                        audio.Dispose();
                        throw;
                    }
                    _audioClient = audio;
                }

                return;
            }
            catch (SocketException ex)
            {
                last = ex;
                video.Dispose();
                await Task.Delay(delay, token).ConfigureAwait(false);
            }
        }

        throw new InvalidOperationException($"Could not connect to the scrcpy server on port {port}.", last);
    }

    /// <summary>Reads the audio socket's codec id and, if it is the raw PCM we asked for,
    /// starts the audio loop. Never loses the session over audio: a phone that refuses to
    /// capture sound (scrcpy writes codec id 0 - see Streamer.writeDisableStream), an
    /// unexpected codec, or a manufacturer that blocks capture all degrade to video-only.
    /// The returned task completes when the audio loop ends.</summary>
    private async Task<Task> StartAudioAsync(CancellationToken token)
    {
        var stream = _audioClient!.GetStream();

        var codecId = new byte[4];
        await stream.ReadExactlyAsync(codecId, token).ConfigureAwait(false);
        if (!codecId.AsSpan().SequenceEqual(ScrcpyCodecIds.AudioRaw))
        {
            _log.Warn($"scrcpy announced an unexpected audio codec (0x{Convert.ToHexString(codecId)}); expected raw. Continuing without audio.");
            _audioClient.Dispose();
            _audioClient = null;
            return Task.CompletedTask;
        }

        // Known the moment the codec id arrives rather than on the first packet: the session
        // recording declares its audio track from this, and a track declared at the wrong
        // rate is a recording that silently comes out without sound.
        AnnounceAudioFormat();
        return RunAudioLoopAsync(stream, token);
    }

    private void AnnounceAudioFormat()
    {
        if (_audioFormatAnnounced) return;
        _audioFormatAnnounced = true;
        AudioFormatChanged?.Invoke(this, new AudioFormat(AudioCodec.Pcm16, 48000, 2, 0, []));
    }

    private Process StartServerProcess()
    {
        string[] args =
        [
            "-s", _serial, "shell",
            $"CLASSPATH={ScrcpyServerRuntime.RemoteJarPath}", "app_process", "/",
            "com.genymobile.scrcpy.Server", ScrcpyServerRuntime.ServerVersion,
            "tunnel_forward=true",
            $"audio={(_options.EnableAudio ? "true" : "false")}",
            "audio_codec=raw",
            "control=false",
            "send_frame_meta=true",
            "send_device_meta=true",
            // Named send_stream_meta since scrcpy 3.x (it covers the codec id and the
            // session packets); scrcpy only warns on an option it does not know, but
            // passing the old send_codec_meta name would silently disable nothing while
            // leaving the warning in the server's log on every session.
            "send_stream_meta=true",
            "cleanup=true",
            $"max_size={_options.MaxDimension}",
            $"video_bit_rate={_options.VideoBitRate}",
            $"max_fps={_options.MaxFps}",
            "log_level=info",
        ];

        var process = new Process
        {
            StartInfo = new ProcessStartInfo(_adbPath)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };
        foreach (var arg in args) process.StartInfo.ArgumentList.Add(arg);
        process.Start();

        _ = DrainAsync(process.StandardOutput, "stdout");
        _ = DrainAsync(process.StandardError, "stderr");
        return process;

        async Task DrainAsync(StreamReader reader, string label)
        {
            try
            {
                string? line;
                while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) is not null)
                    if (line.Length > 0) _log.Debug($"scrcpy-server {label}: {line}");
            }
            catch (IOException) { }
        }
    }

    private static int ReserveLocalPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }

    /// <summary>False on a clean EOF or a dropped connection (device unplugged, process
    /// killed) - the ordinary ways a session ends - so callers can tell that apart from a
    /// genuine cancellation, which still propagates.</summary>
    internal static async Task<bool> TryReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken token)
    {
        try
        {
            await stream.ReadExactlyAsync(buffer, token).ConfigureAwait(false);
            return true;
        }
        catch (EndOfStreamException) { return false; }
        catch (IOException) { return false; }
        catch (ObjectDisposedException) { return false; }
    }

    public async ValueTask DisposeAsync()
    {
        _videoClient?.Dispose();
        _videoClient = null;
        _audioClient?.Dispose();
        _audioClient = null;

        var process = _serverProcess;
        _serverProcess = null;
        if (process is not null)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            process.Dispose();
        }

        if (_forwardActive)
        {
            _forwardActive = false;
            await AdbCommands.RemoveForwardAsync(_adbPath, _serial, _forwardedPort, CancellationToken.None).ConfigureAwait(false);
        }
    }
}
