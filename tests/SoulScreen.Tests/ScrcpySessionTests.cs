using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using SoulScreen.Android;
using SoulScreen.Core.Media;
using Xunit;

namespace SoulScreen.Tests;

/// <summary>
/// Drives <see cref="ScrcpySession"/>'s socket-reading loops over a real loopback TCP
/// connection - not a MemoryStream - so the test exercises the actual async read path
/// against a server that writes in arbitrary, network-realistic chunks that share no
/// boundary with the 12-byte headers or their payloads. adb, the pushed jar and the phone
/// itself cannot be exercised this way (there is no substitute for real hardware there),
/// but everything downstream of "bytes arrive on a socket" - the part most likely to hide a
/// framing bug - is fully exercised here.
/// </summary>
public class ScrcpySessionTests
{
    private const ulong ConfigFlag = 1UL << 62;
    private const ulong KeyFrameFlag = 1UL << 61;

    private static byte[] FrameHeader(ulong ptsAndFlags, int size)
    {
        var header = new byte[12];
        BinaryPrimitives.WriteUInt64BigEndian(header, ptsAndFlags);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(8), (uint)size);
        return header;
    }

    private static byte[] SessionHeader(int width, int height)
    {
        var header = new byte[12];
        header[0] = 0x80;
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(8), (uint)height);
        return header;
    }

    /// <summary>Writes one byte at a time with a tiny stagger, plus one packet split
    /// mid-header and mid-payload - the two places a framing bug would actually show up
    /// (a header read that returns fewer bytes than asked, requiring a real loop rather
    /// than a single Read call, which is exactly what ReadExactlyAsync is there for).</summary>
    private static async Task WriteAwkwardlyAsync(NetworkStream stream, byte[] data)
    {
        var i = 0;
        var rnd = new Random(12345);
        while (i < data.Length)
        {
            var chunk = Math.Min(1 + rnd.Next(3), data.Length - i);
            await stream.WriteAsync(data.AsMemory(i, chunk));
            await stream.FlushAsync();
            i += chunk;
        }
    }

    private static async Task<(TcpListener Listener, TcpClient Client, NetworkStream ServerSide)> LoopbackPairAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var acceptTask = listener.AcceptTcpClientAsync();
        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        var server = await acceptTask;

        return (listener, client, server.GetStream());
    }

    private const string SkipReason =
        "FFmpeg is not installed; run tools/fetch-ffmpeg.ps1 to enable the real-encoder test.";

    [SkippableFact]
    public async Task RunVideoLoopAsync_AwkwardlyChunkedRealSocket_ProducesFormatAndSamplesInOrder()
    {
        Skip.IfNot(H264EncoderHarness.IsAvailable, SkipReason);

        var (listener, client, serverStream) = await LoopbackPairAsync();
        using var _l = client;
        listener.Stop();

        // A real, encoder-produced SPS/PPS pair (320x240): bit-exact enough for
        // AvcDecoderConfiguration.FromAnnexB and TryGetDimensions to actually parse real
        // dimensions out of it, not just accept well-formed NAL types. Reused from the first
        // encoded access unit, split out with the same SplitAnnexB every transport uses.
        var accessUnit = H264EncoderHarness.EncodeSplitField(320, 240, frameCount: 1)[0];
        var configPayload = ExtractParameterSets(accessUnit);
        var keyFramePayload = new byte[] { 0x65, 0xAA, 0xBB, 0xCC };
        var interFramePayload = new byte[] { 0x41, 0x01, 0x02 };

        var wireBytes = Concat(
            SessionHeader(320, 240),
            FrameHeader(ConfigFlag, configPayload.Length), configPayload,
            FrameHeader(KeyFrameFlag | 1000UL, keyFramePayload.Length), keyFramePayload,
            FrameHeader(2000UL, interFramePayload.Length), interFramePayload);

        var writerTask = Task.Run(async () =>
        {
            await using var s = serverStream;
            await WriteAwkwardlyAsync(s, wireBytes);
            // Half-close so the client's read loop sees a clean EOF and returns, rather
            // than hanging forever waiting for a 13th byte of a header that never comes.
        });

        var session = new ScrcpySession("unused-adb-path", "unused-serial");
        var formats = new List<VideoFormat>();
        var samples = new List<(byte[] Payload, long Pts, bool KeyFrame)>();
        session.VideoFormatChanged += (_, f) => formats.Add(f);
        session.VideoSampleReady += (_, s) => samples.Add((s.ToArray(), s.TimestampUs, s.IsKeyFrame));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await session.RunVideoLoopAsync(client.GetStream(), cts.Token);
        await writerTask;

        var format = Assert.Single(formats);
        Assert.Equal(VideoCodec.H264, format.Codec);
        Assert.Equal(320, format.Width);
        Assert.Equal(240, format.Height);

        Assert.Equal(2, samples.Count);
        Assert.Equal(keyFramePayload, samples[0].Payload);
        Assert.Equal(1000, samples[0].Pts);
        Assert.True(samples[0].KeyFrame);
        Assert.Equal(interFramePayload, samples[1].Payload);
        Assert.Equal(2000, samples[1].Pts);
        Assert.False(samples[1].KeyFrame);
    }

    [Fact]
    public async Task RunAudioLoopAsync_RawPcmPackets_AnnouncesFormatOnceAndDeliversEachPacket()
    {
        var (listener, client, serverStream) = await LoopbackPairAsync();
        using var _l = client;
        listener.Stop();

        var chunk1 = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        var chunk2 = new byte[] { 9, 10, 11, 12 };
        var wireBytes = Concat(
            FrameHeader(500UL, chunk1.Length), chunk1,
            FrameHeader(1500UL, chunk2.Length), chunk2);

        var writerTask = Task.Run(async () =>
        {
            await using var s = serverStream;
            await WriteAwkwardlyAsync(s, wireBytes);
        });

        var session = new ScrcpySession("unused-adb-path", "unused-serial");
        var formats = new List<AudioFormat>();
        var samples = new List<byte[]>();
        session.AudioFormatChanged += (_, f) => formats.Add(f);
        session.AudioSampleReady += (_, s) => samples.Add(s.ToArray());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await session.RunAudioLoopAsync(client.GetStream(), cts.Token);
        await writerTask;

        var format = Assert.Single(formats);
        Assert.Equal(AudioCodec.Pcm16, format.Codec);
        Assert.Equal(48000, format.SampleRate);
        Assert.Equal(2, format.Channels);

        Assert.Equal(2, samples.Count);
        Assert.Equal(chunk1, samples[0]);
        Assert.Equal(chunk2, samples[1]);
    }

    [Fact]
    public async Task TryReadExactlyAsync_StreamClosedMidHeader_ReturnsFalseRatherThanThrowing()
    {
        var (listener, client, serverStream) = await LoopbackPairAsync();
        using var _l = client;
        listener.Stop();

        var writerTask = Task.Run(async () =>
        {
            await serverStream.WriteAsync(new byte[] { 1, 2, 3 }); // fewer than 12 bytes
            await serverStream.FlushAsync();
            serverStream.Close();
        });

        var buffer = new byte[12];
        var ok = await ScrcpySession.TryReadExactlyAsync(client.GetStream(), buffer, CancellationToken.None);

        Assert.False(ok);
        await writerTask;
    }

    private static byte[] Concat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    /// <summary>
    /// The handshake against a server that behaves exactly like scrcpy's DesktopConnection
    /// does over an adb forward, in the two ways a wrong implementation dies on real
    /// hardware and no in-memory stream test can show:
    /// <list type="number">
    /// <item>adb forward completes the TCP connection even when nothing is listening behind
    /// it yet, so the connect succeeds and the first read hits EOF - the probe has to treat
    /// that as "not ready yet" and retry rather than fail the handshake (the first two
    /// accepted connections below are closed unanswered, exactly like that case).</item>
    /// <item>the server accepts the audio socket before it writes the device name, so the
    /// client must open the audio socket before reading - otherwise both sides wait on the
    /// other forever. The server here holds the name and codec id back until the audio
    /// connection arrives and fails the test if it never does.</item>
    /// </list>
    /// </summary>
    [Fact]
    public async Task ConnectSockets_RetriesPastAForwardWithNothingBehindIt_AndOpensAudioBeforeReading()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var serverTask = Task.Run(async () =>
        {
            // Two connections accepted and closed unanswered - what the forward looks like
            // while app_process is still booting on the phone.
            for (var i = 0; i < 2; i++)
            {
                var dead = await listener.AcceptTcpClientAsync();
                dead.Dispose();
            }

            // Third time the server is up: accept video, send the dummy byte immediately
            // (DesktopConnection writes it on accept), then wait for the audio socket before
            // writing the device name and codec id - name first would deadlock a client that
            // has not opened audio yet.
            var video = await listener.AcceptTcpClientAsync();
            await using var videoStream = video.GetStream();
            await videoStream.WriteAsync(new byte[] { 0 });

            using var audio = await listener.AcceptTcpClientAsync()
                .WaitAsync(TimeSpan.FromSeconds(5));

            var name = new byte[ScrcpyDeviceMeta.DeviceNameFieldLength];
            System.Text.Encoding.UTF8.GetBytes("Kasra's Pixel").CopyTo(name, 0);
            await videoStream.WriteAsync(name);
            await videoStream.WriteAsync(ScrcpyCodecIds.VideoH264);
            await videoStream.FlushAsync();
        });

        var session = new ScrcpySession("unused-adb-path", "unused-serial");
        try
        {
            await session.ConnectSocketsAsync(port, CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(10));
            await serverTask.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            await session.DisposeAsync();
            listener.Stop();
        }

        // The handshake completed only because both waits above were met: two dead
        // forwards were probed past, and the audio socket was opened before the server
        // would hand over the device name. Either regression fails on serverTask instead
        // of hanging the suite.
    }

    /// <summary>Pulls the SPS/PPS NALs out of a real encoded access unit and rebuilds them
    /// as the Annex-B blob a scrcpy config packet carries.</summary>
    private static byte[] ExtractParameterSets(byte[] accessUnit)
    {
        var sets = new List<ReadOnlyMemory<byte>>();
        foreach (var range in H264.SplitAnnexB(accessUnit))
        {
            var (offset, length) = range.GetOffsetAndLength(accessUnit.Length);
            if (length == 0) continue;
            var nalType = accessUnit[offset] & 0x1f;
            if (nalType is H264.NalTypeSps or H264.NalTypePps)
                sets.Add(accessUnit.AsMemory(offset, length));
        }
        return H264.ToAnnexB([.. sets]);
    }
}
