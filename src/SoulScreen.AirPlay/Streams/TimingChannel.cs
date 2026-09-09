using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using SoulScreen.Core.Logging;

namespace SoulScreen.AirPlay.Streams;

/// <summary>
/// The AirPlay timing channel: an NTP-shaped exchange the receiver drives against the
/// sender's timing port.
/// <para>
/// It is the receiver that asks. Every few seconds we send a 32-byte probe stamped with
/// our clock; the phone answers with when it saw the probe and when it replied, which is
/// enough for the classic four-timestamp offset and round-trip estimate.
/// </para>
/// <para>
/// SoulScreen renders frames as they arrive rather than scheduling them against the
/// sender's clock, so the offset is not used for playback - it drives the latency readout
/// and keeps the session looking like a well-behaved receiver to the phone.
/// </para>
/// </summary>
public sealed class TimingChannel : IAsyncDisposable
{
    private const int PacketSize = 32;
    private static readonly TimeSpan ProbeInterval = TimeSpan.FromSeconds(3);

    private readonly ILogger _log = Log.For("timing");
    private readonly Socket _socket;
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private CancellationTokenSource? _cts;
    private Task? _loop;
    private IPEndPoint? _remote;

    public TimingChannel()
    {
        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        _socket.Bind(new IPEndPoint(IPAddress.Any, 0));
        Port = ((IPEndPoint)_socket.LocalEndPoint!).Port;
    }

    /// <summary>Local UDP port reported to the sender as "timingPort".</summary>
    public int Port { get; }

    /// <summary>Estimated offset between the sender's clock and ours, in microseconds.</summary>
    public long ClockOffsetMicroseconds { get; private set; }

    /// <summary>Most recent measured round-trip, in microseconds.</summary>
    public long RoundTripMicroseconds { get; private set; }

    /// <summary>True once at least one probe has been answered.</summary>
    public bool IsSynchronised { get; private set; }

    public void Start(IPAddress senderAddress, int senderTimingPort, CancellationToken cancellationToken = default)
    {
        if (_cts is not null) return;
        if (senderTimingPort <= 0)
        {
            _log.Debug("sender did not supply a timing port; skipping clock sync");
            return;
        }

        _remote = new IPEndPoint(senderAddress, senderTimingPort);
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _loop = Task.Run(() => ProbeLoopAsync(_cts.Token), CancellationToken.None);
        _log.Debug($"clock sync against {_remote} from local port {Port}");
    }

    private async Task ProbeLoopAsync(CancellationToken token)
    {
        var request = new byte[PacketSize];
        var response = new byte[128];

        // Carried from the previous exchange so the sender can match up the conversation.
        ulong lastClientReference = 0;
        ulong lastReceiveTime = 0;

        while (!token.IsCancellationRequested)
        {
            try
            {
                Array.Clear(request);
                request[0] = 0x80;
                request[1] = 0xd2; // timing request
                request[3] = 0x07;

                if (lastReceiveTime != 0)
                {
                    BinaryPrimitives.WriteUInt64BigEndian(request.AsSpan(8), lastClientReference);
                    BinaryPrimitives.WriteUInt64BigEndian(request.AsSpan(16), lastReceiveTime);
                }

                var sendTime = NowNtp();
                BinaryPrimitives.WriteUInt64BigEndian(request.AsSpan(24), sendTime);

                await _socket.SendToAsync(request, SocketFlags.None, _remote!, token).ConfigureAwait(false);

                using var replyTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                replyTimeout.CancelAfter(TimeSpan.FromMilliseconds(500));

                SocketReceiveFromResult result;
                try
                {
                    result = await _socket.ReceiveFromAsync(response, SocketFlags.None, _remote!, replyTimeout.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    _log.Trace("timing probe went unanswered");
                    await Task.Delay(ProbeInterval, token).ConfigureAwait(false);
                    continue;
                }

                var receiveTime = NowNtp();
                if (result.ReceivedBytes >= PacketSize)
                {
                    // t0 our send, t1 sender receive, t2 sender send, t3 our receive.
                    var t0 = NtpToMicroseconds(BinaryPrimitives.ReadUInt64BigEndian(response.AsSpan(8)));
                    var t1 = NtpToMicroseconds(BinaryPrimitives.ReadUInt64BigEndian(response.AsSpan(16)));
                    var t2 = NtpToMicroseconds(BinaryPrimitives.ReadUInt64BigEndian(response.AsSpan(24)));
                    var t3 = NtpToMicroseconds(receiveTime);

                    ClockOffsetMicroseconds = ((t1 - t0) + (t2 - t3)) / 2;
                    RoundTripMicroseconds = (t3 - t0) - (t2 - t1);
                    IsSynchronised = true;

                    lastClientReference = BinaryPrimitives.ReadUInt64BigEndian(response.AsSpan(24));
                    lastReceiveTime = receiveTime;

                    _log.Trace($"rtt {RoundTripMicroseconds / 1000.0:0.##} ms, offset {ClockOffsetMicroseconds / 1000.0:0.##} ms");
                }

                await Task.Delay(ProbeInterval, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException ex)
            {
                _log.Debug($"timing probe failed: {ex.SocketErrorCode}");
                try { await Task.Delay(ProbeInterval, token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    /// <summary>Our monotonic clock as a 32.32 fixed-point NTP timestamp.</summary>
    private ulong NowNtp()
    {
        var elapsed = _clock.Elapsed;
        var seconds = (ulong)elapsed.TotalSeconds;
        var fraction = (ulong)((elapsed.TotalSeconds - seconds) * 4294967296.0);
        return (seconds << 32) | (fraction & 0xFFFFFFFF);
    }

    private static long NtpToMicroseconds(ulong timestamp)
    {
        var seconds = timestamp >> 32;
        var fraction = timestamp & 0xFFFFFFFF;
        return (long)(seconds * 1_000_000 + ((fraction * 1_000_000) >> 32));
    }

    public async ValueTask DisposeAsync()
    {
        var cts = Interlocked.Exchange(ref _cts, null);
        if (cts is not null)
        {
            await cts.CancelAsync().ConfigureAwait(false);
            cts.Dispose();
        }

        _socket.Dispose();

        if (_loop is not null)
        {
            try { await _loop.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            _loop = null;
        }
    }
}
