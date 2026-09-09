using System.Buffers;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using SoulScreen.Core.Logging;
using SoulScreen.Core.Media;

namespace SoulScreen.AirPlay.Streams;

/// <summary>
/// Receives the audio half of a mirroring session.
/// <para>
/// Unlike video, audio arrives as RTP over UDP: a 12-byte header followed by a payload
/// encrypted with AES-128-CBC under the key SETUP delivered. Only whole 16-byte blocks are
/// encrypted; any tail is sent in the clear.
/// </para>
/// <para>
/// Packets can arrive out of order or not at all. Rather than build a full jitter buffer,
/// this emits in arrival order and reports gaps, which suits mirroring - a late audio
/// packet is worth less than a low-latency one.
/// </para>
/// </summary>
public sealed class AudioStream : IAsyncDisposable
{
    private const int RtpHeaderSize = 12;

    private readonly ILogger _log = Log.For("audio");
    private readonly byte[] _aesKey;
    private readonly byte[] _aesIv;
    private readonly Socket _dataSocket;
    private readonly Socket _controlSocket;
    private readonly string? _dumpPath;

    private CancellationTokenSource? _cts;
    private Task? _dataLoop;
    private Task? _controlLoop;
    private FileStream? _dumpStream;
    private ushort _lastSequence;
    private bool _haveSequence;
    private long _packetCount;
    private long _lostCount;

    public AudioStream(ReadOnlySpan<byte> aesKey, ReadOnlySpan<byte> aesIv, AudioFormat format, string? dumpDirectory = null)
    {
        if (aesKey.Length < 16) throw new ArgumentException("Audio AES key must be 16 bytes.", nameof(aesKey));
        if (aesIv.Length < 16) throw new ArgumentException("Audio AES IV must be 16 bytes.", nameof(aesIv));

        _aesKey = aesKey[..16].ToArray();
        _aesIv = aesIv[..16].ToArray();
        Format = format;

        _dataSocket = BindEphemeralUdp();
        _controlSocket = BindEphemeralUdp();
        DataPort = ((IPEndPoint)_dataSocket.LocalEndPoint!).Port;
        ControlPort = ((IPEndPoint)_controlSocket.LocalEndPoint!).Port;

        if (!string.IsNullOrEmpty(dumpDirectory))
        {
            Directory.CreateDirectory(dumpDirectory);
            _dumpPath = Path.Combine(dumpDirectory, $"mirror-{DateTime.Now:yyyyMMdd-HHmmss}.aac");
        }
    }

    public int DataPort { get; }
    public int ControlPort { get; }
    public AudioFormat Format { get; }
    public long PacketCount => Interlocked.Read(ref _packetCount);
    public long LostPacketCount => Interlocked.Read(ref _lostCount);

    /// <summary>Raised per audio packet. The sample is recycled once the handler returns.</summary>
    public event EventHandler<MediaSample>? SampleReady;

    private static Socket BindEphemeralUdp()
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        // A burst of audio while the UI thread is busy must not cost us packets.
        socket.ReceiveBufferSize = 1 << 20;
        socket.Bind(new IPEndPoint(IPAddress.Any, 0));
        return socket;
    }

    public void Start(CancellationToken cancellationToken = default)
    {
        if (_cts is not null) return;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        if (_dumpPath is not null)
        {
            _dumpStream = new FileStream(_dumpPath, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16);
            _log.Info($"dumping audio packets to {_dumpPath}");
        }

        _dataLoop = Task.Run(() => ReceiveDataAsync(_cts.Token), CancellationToken.None);
        _controlLoop = Task.Run(() => DrainControlAsync(_cts.Token), CancellationToken.None);
        _log.Info($"audio stream on data {DataPort} / control {ControlPort} as {Format}");
    }

    private async Task ReceiveDataAsync(CancellationToken token)
    {
        var buffer = new byte[2048];
        using var aes = Aes.Create();
        aes.Key = _aesKey;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.None;

        while (!token.IsCancellationRequested)
        {
            int received;
            try
            {
                received = await _dataSocket.ReceiveAsync(buffer, SocketFlags.None, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException ex)
            {
                // ConnectionReset on a UDP socket just means an ICMP port-unreachable came
                // back for an earlier send; it is not fatal.
                if (ex.SocketErrorCode == SocketError.ConnectionReset) continue;
                _log.Debug($"audio receive failed: {ex.SocketErrorCode}");
                continue;
            }

            if (received <= RtpHeaderSize) continue;

            var sequence = BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(2));
            var timestamp = BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(4));
            TrackSequence(sequence);

            var payloadLength = received - RtpHeaderSize;
            var encryptedLength = payloadLength & ~0xf;

            var payload = ArrayPool<byte>.Shared.Rent(payloadLength);
            try
            {
                buffer.AsSpan(RtpHeaderSize, payloadLength).CopyTo(payload);

                // Every packet is its own CBC message: the IV never chains between packets.
                if (encryptedLength > 0)
                {
                    using var decryptor = aes.CreateDecryptor(_aesKey, _aesIv);
                    decryptor.TransformBlock(payload, 0, encryptedLength, payload, 0);
                }

                Interlocked.Increment(ref _packetCount);
                _dumpStream?.Write(payload, 0, payloadLength);

                var handler = SampleReady;
                if (handler is null) continue;

                // The RTP clock runs at the audio sample rate.
                var timestampUs = Format.SampleRate > 0
                    ? (long)timestamp * 1_000_000 / Format.SampleRate
                    : 0;

                using var sample = MediaSample.Copy(payload.AsSpan(0, payloadLength), timestampUs);
                handler(this, sample);
            }
            catch (CryptographicException ex)
            {
                _log.Warn($"audio packet {sequence} would not decrypt: {ex.Message}");
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(payload);
            }
        }
    }

    /// <summary>
    /// The control port carries retransmit requests and sync packets. SoulScreen never asks
    /// for a resend, but the socket has to exist and be drained or the sender's queue backs up.
    /// </summary>
    private async Task DrainControlAsync(CancellationToken token)
    {
        var buffer = new byte[2048];
        while (!token.IsCancellationRequested)
        {
            try
            {
                await _controlSocket.ReceiveAsync(buffer, SocketFlags.None, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset) { }
            catch (SocketException) { break; }
        }
    }

    private void TrackSequence(ushort sequence)
    {
        if (_haveSequence)
        {
            var expected = (ushort)(_lastSequence + 1);
            if (sequence != expected)
            {
                // Wrap-safe distance; anything large is reordering rather than loss.
                var gap = (ushort)(sequence - expected);
                if (gap is > 0 and < 1000) Interlocked.Add(ref _lostCount, gap);
            }
        }

        _lastSequence = sequence;
        _haveSequence = true;
    }

    public async ValueTask DisposeAsync()
    {
        var cts = Interlocked.Exchange(ref _cts, null);
        if (cts is not null)
        {
            await cts.CancelAsync().ConfigureAwait(false);
            cts.Dispose();
        }

        _dataSocket.Dispose();
        _controlSocket.Dispose();

        foreach (var task in new[] { _dataLoop, _controlLoop })
        {
            if (task is null) continue;
            try { await task.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }

        _dataLoop = null;
        _controlLoop = null;

        if (_dumpStream is not null) await _dumpStream.DisposeAsync().ConfigureAwait(false);
        CryptographicOperations.ZeroMemory(_aesKey);
        CryptographicOperations.ZeroMemory(_aesIv);

        if (PacketCount > 0)
            _log.Info($"audio stream closed after {PacketCount} packets ({LostPacketCount} lost)");
    }
}
