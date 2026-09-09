using System.Buffers;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using SoulScreen.Core.Buffers;
using SoulScreen.Core.Logging;
using SoulScreen.Core.Media;

namespace SoulScreen.AirPlay.Streams;

/// <summary>
/// Receives the mirrored screen.
/// <para>
/// After SETUP hands the phone a port, it opens a plain TCP connection and pushes a
/// continuous sequence of [128-byte header][payload] records. The header is little-endian:
/// the payload length at offset 0, a payload type at offset 4, and the sender's NTP clock
/// at offset 8. Type 0 is encrypted H.264 with 4-byte length prefixes; type 1 is an
/// unencrypted avcC configuration record, resent on every rotation.
/// </para>
/// </summary>
public sealed class MirrorVideoStream : IAsyncDisposable
{
    private const int HeaderSize = 128;
    private const int MaxPayloadSize = 16 * 1024 * 1024;

    private const int PayloadTypeVideo = 0;
    private const int PayloadTypeCodecConfig = 1;

    private readonly ILogger _log = Log.For("mirror");
    private readonly MirrorStreamCipher _cipher;
    private readonly TcpListener _listener;
    private readonly string? _dumpPath;

    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;
    private FileStream? _dumpStream;
    private AvcDecoderConfiguration? _configuration;
    private long _frameCount;

    public MirrorVideoStream(
        ReadOnlySpan<byte> fairPlayKey,
        ReadOnlySpan<byte> ecdhSecret,
        ulong streamConnectionId,
        string? dumpDirectory = null)
    {
        _cipher = new MirrorStreamCipher(fairPlayKey, ecdhSecret, streamConnectionId);
        _listener = new TcpListener(IPAddress.Any, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;

        if (!string.IsNullOrEmpty(dumpDirectory))
        {
            Directory.CreateDirectory(dumpDirectory);
            _dumpPath = Path.Combine(dumpDirectory, $"mirror-{DateTime.Now:yyyyMMdd-HHmmss}.h264");
        }
    }

    /// <summary>Port handed back to the sender in the SETUP response.</summary>
    public int Port { get; }

    /// <summary>Frames delivered since the stream opened.</summary>
    public long FrameCount => Interlocked.Read(ref _frameCount);

    /// <summary>Raised when the codec configuration arrives or changes (rotation, resolution).</summary>
    public event EventHandler<VideoFormat>? FormatChanged;

    /// <summary>Raised per frame with an Annex-B payload. The handler owns the sample and must dispose it.</summary>
    public event EventHandler<MediaSample>? SampleReady;

    /// <summary>Raised once the sender disconnects the data channel.</summary>
    public event EventHandler? Ended;

    public void Start(CancellationToken cancellationToken = default)
    {
        if (_cts is not null) return;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _acceptLoop = Task.Run(() => AcceptAsync(_cts.Token), CancellationToken.None);
        _log.Info($"video stream waiting on port {Port}");
    }

    private async Task AcceptAsync(CancellationToken token)
    {
        try
        {
            using var client = await _listener.AcceptTcpClientAsync(token).ConfigureAwait(false);
            client.NoDelay = true;
            _log.Info($"video stream connected from {client.Client.RemoteEndPoint}");

            if (_dumpPath is not null)
            {
                _dumpStream = new FileStream(_dumpPath, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16);
                _log.Info($"dumping elementary stream to {_dumpPath}");
            }

            await using var stream = client.GetStream();
            await ReadLoopAsync(stream, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (IOException ex)
        {
            _log.Debug($"video stream closed: {ex.Message}");
        }
        catch (Exception ex)
        {
            _log.Error("video stream failed", ex);
        }
        finally
        {
            if (_dumpStream is not null)
            {
                await _dumpStream.DisposeAsync().ConfigureAwait(false);
                _dumpStream = null;
            }
            Ended?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task ReadLoopAsync(NetworkStream stream, CancellationToken token)
    {
        var header = new byte[HeaderSize];

        while (!token.IsCancellationRequested)
        {
            if (!await ReadExactlyAsync(stream, header, token).ConfigureAwait(false))
            {
                _log.Debug("sender closed the video stream");
                return;
            }

            var payloadSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(header);
            var payloadType = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(4)) & 0xff;
            var ntpTimestamp = BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(8));

            if (payloadSize is < 0 or > MaxPayloadSize)
            {
                _log.Error($"implausible payload size {payloadSize}; dropping the stream. Header:\n{Hex.Dump(header, 32)}");
                return;
            }

            if (payloadSize == 0) continue;

            var payload = ArrayPool<byte>.Shared.Rent(payloadSize);
            try
            {
                if (!await ReadExactlyAsync(stream, payload.AsMemory(0, payloadSize), token).ConfigureAwait(false))
                {
                    _log.Debug("sender closed mid-payload");
                    return;
                }

                switch (payloadType)
                {
                    case PayloadTypeVideo:
                        HandleVideo(payload, payloadSize, NtpToMicroseconds(ntpTimestamp));
                        break;
                    case PayloadTypeCodecConfig:
                        HandleCodecConfig(payload.AsSpan(0, payloadSize));
                        break;
                    default:
                        // Senders emit occasional keep-alive and telemetry records here.
                        _log.Trace($"ignoring payload type {payloadType} ({payloadSize} bytes)");
                        break;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(payload);
            }
        }
    }

    private void HandleVideo(byte[] payload, int length, long timestampUs)
    {
        // Decryption must see every byte of every packet in order, including packets we
        // would otherwise skip, or the keystream desynchronises for good.
        _cipher.Decrypt(payload.AsSpan(0, length));

        if (!H264.ConvertLengthPrefixedToAnnexB(payload.AsSpan(0, length), out var isKeyFrame))
        {
            // Almost always a sign the key derivation is wrong, so say so loudly once.
            _log.Warn($"frame {FrameCount} did not parse as length-prefixed H.264; " +
                      "this usually means the stream key is wrong (was pair-verify completed?)");
            return;
        }

        _dumpStream?.Write(payload, 0, length);
        Interlocked.Increment(ref _frameCount);

        var handler = SampleReady;
        if (handler is null) return;

        using var sample = MediaSample.Copy(payload.AsSpan(0, length), timestampUs, isKeyFrame);
        handler(this, sample);
    }

    private void HandleCodecConfig(ReadOnlySpan<byte> payload)
    {
        AvcDecoderConfiguration configuration;
        try
        {
            configuration = AvcDecoderConfiguration.Parse(payload);
        }
        catch (InvalidDataException ex)
        {
            _log.Warn($"could not parse the codec configuration: {ex.Message}\n{Hex.Dump(payload, 64)}");
            return;
        }

        _configuration = configuration;
        var parameterSets = configuration.ToAnnexB();
        _dumpStream?.Write(parameterSets);

        configuration.TryGetDimensions(out var width, out var height);
        var format = new VideoFormat(VideoCodec.H264, width, height, parameterSets, 0);
        _log.Info($"codec configuration: {format}");
        FormatChanged?.Invoke(this, format);
    }

    /// <summary>
    /// Converts the sender's 32.32 fixed-point NTP clock to microseconds. The epoch is
    /// left alone deliberately: the value is only ever used as a relative presentation
    /// timestamp, never as a wall clock.
    /// </summary>
    private static long NtpToMicroseconds(ulong ntpTimestamp)
    {
        var seconds = ntpTimestamp >> 32;
        var fraction = ntpTimestamp & 0xFFFFFFFF;
        return (long)(seconds * 1_000_000 + ((fraction * 1_000_000) >> 32));
    }

    private static async Task<bool> ReadExactlyAsync(NetworkStream stream, Memory<byte> buffer, CancellationToken token)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[offset..], token).ConfigureAwait(false);
            if (read == 0) return false;
            offset += read;
        }
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        var cts = Interlocked.Exchange(ref _cts, null);
        if (cts is not null)
        {
            await cts.CancelAsync().ConfigureAwait(false);
            cts.Dispose();
        }

        try { _listener.Stop(); } catch (Exception) { /* already torn down */ }

        if (_acceptLoop is not null)
        {
            try { await _acceptLoop.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            _acceptLoop = null;
        }

        _cipher.Dispose();
        if (_dumpStream is not null) await _dumpStream.DisposeAsync().ConfigureAwait(false);
        _log.Info($"video stream closed after {FrameCount} frames");
    }
}
