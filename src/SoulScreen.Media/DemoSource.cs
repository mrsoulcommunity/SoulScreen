using System.Diagnostics;
using FFmpeg.AutoGen;
using SoulScreen.Core.Logging;
using SoulScreen.Core.Media;
using SoulScreen.Core.Sources;

namespace SoulScreen.Media;

/// <summary>Which test pattern the demo paints.</summary>
public enum DemoPattern
{
    /// <summary>A bouncing shape and a moving bar, so a dropped or repeated frame stands out.</summary>
    MotionTest,

    /// <summary>Reference colour bars and a grey-scale step wedge, for checking colour and tone.</summary>
    ColorBars,

    /// <summary>A hand sweeping past a fixed ring of ticks, so the display holding or skipping a
    /// frame is visible even though the demo itself never drops one.</summary>
    RefreshRateChecker,
}

/// <summary>
/// A mirror source that needs no phone: it encodes one of a few test patterns with the H.264
/// encoder in the FFmpeg build and delivers it exactly as the AirPlay transport would.
/// <para>
/// It exists so the whole path after the network - decoding, pacing, the picture controls,
/// screenshots, recording - can be tried and checked without an iPhone in the room, both by
/// a user wondering whether their PC is up to it and by anyone working on the app. The pattern
/// can be switched while it runs, so one session can move from checking motion to checking
/// colour without a restart.
/// </para>
/// </summary>
public sealed class DemoSource : IMirrorSource
{
    private const string EncoderName = "libopenh264";

    private readonly ILogger _log = Log.For("demo");
    private readonly int _width;
    private readonly int _height;
    private readonly int _fps;

    private CancellationTokenSource? _cts;
    private Thread? _thread;
    private MirrorSourceState _state = MirrorSourceState.Stopped;
    private long _frames;
    private volatile DemoPattern _pattern;

    /// <param name="width">Picture width; even, as H.264 4:2:0 requires.</param>
    /// <param name="height">Picture height; even.</param>
    /// <param name="fps">Frames a second to produce.</param>
    /// <param name="pattern">The pattern to start with; change it later through <see cref="Pattern"/>.</param>
    public DemoSource(int width = 720, int height = 1280, int fps = 30, DemoPattern pattern = DemoPattern.MotionTest)
    {
        _width = Math.Max(width & ~1, 64);
        _height = Math.Max(height & ~1, 64);
        _fps = Math.Clamp(fps, 5, 60);
        _pattern = pattern;
    }

    /// <summary>True when FFmpeg is present and carries the encoder the pattern needs.</summary>
    public static bool IsAvailable
    {
        get
        {
            if (!FFmpegRuntime.IsAvailable) return false;
            try { return FindEncoder(); }
            catch (Exception) { return false; }
        }
    }

    private static unsafe bool FindEncoder() => ffmpeg.avcodec_find_encoder_by_name(EncoderName) is not null;

    public string Id => "demo";

    public string DisplayName => "Demo pattern";

    public MirrorSourceState State => _state;

    public SourceDeviceInfo? Device { get; private set; }

    /// <summary>Which pattern is painted. Settable while streaming - the next frame picks it up,
    /// and <see cref="Device"/>'s model updates with it so the stats HUD names it too.</summary>
    public DemoPattern Pattern
    {
        get => _pattern;
        set
        {
            _pattern = value;
            if (Device is { } device) Device = device with { Model = PatternLabel(value) };
        }
    }

    /// <summary>Frames delivered so far.</summary>
    public long FrameCount => Interlocked.Read(ref _frames);

    public event EventHandler<MirrorSourceStateChangedEventArgs>? StateChanged;
    public event EventHandler<VideoFormat>? VideoFormatChanged;
    public event EventHandler<MediaSample>? VideoSampleReady;
    public event EventHandler<AudioFormat>? AudioFormatChanged;
    public event EventHandler<MediaSample>? AudioSampleReady;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_cts is not null) return Task.CompletedTask;
        FFmpegRuntime.ThrowIfUnavailable();
        if (!FindEncoder())
            throw new FFmpegUnavailableException("This FFmpeg build has no H.264 encoder, so the demo cannot run.");

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Device = new SourceDeviceInfo("Demo", PatternLabel(_pattern));
        SetState(MirrorSourceState.Connecting);

        _thread = new Thread(() => Run(_cts.Token))
        {
            Name = "SoulScreen demo",
            IsBackground = true,
        };
        _thread.Start();
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        var cts = Interlocked.Exchange(ref _cts, null);
        if (cts is null) return;

        await cts.CancelAsync().ConfigureAwait(false);
        var thread = _thread;
        _thread = null;
        if (thread is not null)
        {
            // Off the caller's thread: the UI thread is what usually asks, and the encoder
            // thread may be a frame away from noticing the token.
            await Task.Run(() => thread.Join(TimeSpan.FromSeconds(3))).ConfigureAwait(false);
        }
        cts.Dispose();

        Device = null;
        SetState(MirrorSourceState.Stopped);
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private void SetState(MirrorSourceState state, string? message = null)
    {
        if (_state == state && message is null) return;
        _state = state;
        StateChanged?.Invoke(this, new MirrorSourceStateChangedEventArgs(state, Device, message));
    }

    // ------------------------------------------------------------------ encoding

    private unsafe void Run(CancellationToken token)
    {
        AVCodecContext* context = null;
        AVFrame* frame = null;
        AVPacket* packet = null;

        try
        {
            var codec = ffmpeg.avcodec_find_encoder_by_name(EncoderName);
            context = ffmpeg.avcodec_alloc_context3(codec);
            if (context is null) throw new FFmpegUnavailableException("Could not allocate the demo encoder.");

            context->width = _width;
            context->height = _height;
            context->pix_fmt = AVPixelFormat.AV_PIX_FMT_YUV420P;
            context->time_base = new AVRational { num = 1, den = _fps };
            context->framerate = new AVRational { num = _fps, den = 1 };
            // Around what an iPhone sends for a portrait screen, so the figures on screen mean
            // something; a keyframe every two seconds, as the phone does.
            context->bit_rate = 6_000_000;
            context->gop_size = _fps * 2;
            context->max_b_frames = 0;

            var opened = ffmpeg.avcodec_open2(context, codec, null);
            if (opened < 0)
                throw new FFmpegUnavailableException($"Could not open the demo encoder: {FFmpegRuntime.DescribeError(opened)}");

            frame = ffmpeg.av_frame_alloc();
            frame->format = (int)AVPixelFormat.AV_PIX_FMT_YUV420P;
            frame->width = _width;
            frame->height = _height;
            if (ffmpeg.av_frame_get_buffer(frame, 32) < 0)
                throw new FFmpegUnavailableException("Could not allocate the demo picture.");
            packet = ffmpeg.av_packet_alloc();

            var announced = false;
            var clock = Stopwatch.StartNew();
            var interval = TimeSpan.FromSeconds(1.0 / _fps);
            var index = 0L;

            while (!token.IsCancellationRequested)
            {
                if (ffmpeg.av_frame_make_writable(frame) < 0) break;
                Paint(frame, index);
                frame->pts = index;

                var sent = ffmpeg.avcodec_send_frame(context, frame);
                if (sent < 0)
                {
                    _log.Error($"the demo encoder rejected a frame: {FFmpegRuntime.DescribeError(sent)}");
                    break;
                }

                while (true)
                {
                    var received = ffmpeg.avcodec_receive_packet(context, packet);
                    if (received == ffmpeg.AVERROR(ffmpeg.EAGAIN) || received == ffmpeg.AVERROR_EOF) break;
                    if (received < 0)
                    {
                        _log.Error($"the demo encoder failed: {FFmpegRuntime.DescribeError(received)}");
                        return;
                    }

                    var annexB = new ReadOnlySpan<byte>(packet->data, packet->size);
                    var keyFrame = (packet->flags & ffmpeg.AV_PKT_FLAG_KEY) != 0;
                    var timestampUs = packet->pts * 1_000_000 / _fps;

                    if (!announced)
                    {
                        var parameterSets = ExtractParameterSets(annexB);
                        if (parameterSets.Length == 0)
                        {
                            ffmpeg.av_packet_unref(packet);
                            continue;
                        }
                        announced = true;
                        VideoFormatChanged?.Invoke(this, new VideoFormat(VideoCodec.H264, _width, _height, parameterSets, _fps));
                        SetState(MirrorSourceState.Streaming);
                    }

                    using (var sample = MediaSample.Copy(annexB, timestampUs, keyFrame))
                        VideoSampleReady?.Invoke(this, sample);
                    Interlocked.Increment(ref _frames);
                    ffmpeg.av_packet_unref(packet);
                }

                index++;

                // Paced to the frame rate against a stopwatch rather than by sleeping the
                // interval, so encode time does not accumulate into a slower rate.
                var due = interval * index;
                var wait = due - clock.Elapsed;
                if (wait > TimeSpan.Zero) token.WaitHandle.WaitOne(wait);
            }
        }
        catch (Exception ex)
        {
            _log.Error("the demo stopped", ex);
            SetState(MirrorSourceState.Faulted, ex.Message);
        }
        finally
        {
            if (packet is not null) ffmpeg.av_packet_free(&packet);
            if (frame is not null) ffmpeg.av_frame_free(&frame);
            if (context is not null) ffmpeg.avcodec_free_context(&context);
            _log.Info($"demo ended after {FrameCount} frames");
        }
    }

    /// <summary>The SPS and PPS from a keyframe, in Annex-B form, or empty if it carries none.</summary>
    private unsafe static byte[] ExtractParameterSets(ReadOnlySpan<byte> annexB)
    {
        var result = new List<byte>();
        foreach (var range in H264.SplitAnnexB(annexB))
        {
            var (offset, length) = range.GetOffsetAndLength(annexB.Length);
            if (length == 0) continue;
            var nalType = annexB[offset] & 0x1f;
            if (nalType is not (H264.NalTypeSps or H264.NalTypePps)) continue;
            result.AddRange([0, 0, 0, 1]);
            result.AddRange(annexB.Slice(offset, length).ToArray());
        }
        return [.. result];
    }

    // ------------------------------------------------------------------- pattern

    /// <summary>The name shown for each pattern, in the stats HUD and in the app's picker.</summary>
    public static string PatternLabel(DemoPattern pattern) => pattern switch
    {
        DemoPattern.ColorBars => "Color bars",
        DemoPattern.RefreshRateChecker => "Refresh-rate checker",
        _ => "Motion test",
    };

    /// <summary>Draws one frame of whichever pattern is selected, then the frame-count ladder
    /// every pattern carries in the corner, so a frame the pipeline dropped or repeated is
    /// always visible no matter what else is on screen.</summary>
    private unsafe void Paint(AVFrame* frame, long index)
    {
        var t = index / (double)_fps;
        switch (_pattern)
        {
            case DemoPattern.ColorBars:
                PaintColorBars(frame);
                break;
            case DemoPattern.RefreshRateChecker:
                PaintRefreshChecker(frame, t);
                break;
            default:
                PaintMotionTest(frame, index, t);
                break;
        }
        PaintFrameLadder(frame, index);
    }

    /// <summary>A slowly turning colour gradient, a bouncing disc and a sweeping bar, so dropped
    /// or repeated frames would be visible.</summary>
    private unsafe void PaintMotionTest(AVFrame* frame, long index, double t)
    {
        var luma = frame->data[0];
        var cb = frame->data[1];
        var cr = frame->data[2];
        var lumaStride = frame->linesize[0];
        var chromaStride = frame->linesize[1];

        // Background: hue drifts with time and shifts down the picture.
        for (var y = 0; y < _height; y++)
        {
            var hue = (t * 12 + y * 180.0 / _height) % 360;
            var (r, g, b) = HsvToRgb(hue, 0.55, 0.32);
            var (yy, u, v) = ToYuv(r, g, b);
            new Span<byte>(luma + (long)y * lumaStride, _width).Fill(yy);
            if ((y & 1) == 0)
            {
                new Span<byte>(cb + (long)(y / 2) * chromaStride, _width / 2).Fill(u);
                new Span<byte>(cr + (long)(y / 2) * chromaStride, _width / 2).Fill(v);
            }
        }

        // A disc bouncing around the picture.
        var radius = _width / 7;
        var cx = radius + (int)(Bounce(t * 0.31) * (_width - 2 * radius));
        var cy = radius + (int)(Bounce(t * 0.23) * (_height - 2 * radius));
        FillDisc(frame, cx, cy, radius, 235, 128, 128);
        FillDisc(frame, cx, cy, radius * 3 / 4, 90, 180, 60);

        // A bar sweeping down, so motion is visible even when the disc is slow.
        var barY = (int)((t * 0.5 % 1.0) * _height);
        var barHeight = Math.Max(_height / 60, 4);
        for (var y = barY; y < Math.Min(barY + barHeight, _height); y++)
            new Span<byte>(luma + (long)y * lumaStride, _width).Fill(240);
    }

    private static readonly (byte Y, byte U, byte V)[] ColorBarSwatches =
    [
        ToYuv(1, 1, 1), // white
        ToYuv(1, 1, 0), // yellow
        ToYuv(0, 1, 1), // cyan
        ToYuv(0, 1, 0), // green
        ToYuv(1, 0, 1), // magenta
        ToYuv(1, 0, 0), // red
        ToYuv(0, 0, 1), // blue
    ];

    /// <summary>Reference colour bars over a grey-scale step wedge: a still picture, on purpose,
    /// so colour and tone are what stand out rather than motion.</summary>
    private unsafe void PaintColorBars(AVFrame* frame)
    {
        var barsHeight = _height * 5 / 6;
        var barWidth = _width / ColorBarSwatches.Length;
        for (var i = 0; i < ColorBarSwatches.Length; i++)
        {
            var (yy, u, v) = ColorBarSwatches[i];
            var x0 = i * barWidth;
            var width = i == ColorBarSwatches.Length - 1 ? _width - x0 : barWidth;
            FillArea(frame, x0, 0, width, barsHeight, yy, u, v);
        }

        // A grey-scale step wedge along the foot: colour is for the bars above, this strip is
        // for a lifted black level or banding in the panel's tone curve.
        const int steps = 8;
        var stepWidth = _width / steps;
        for (var i = 0; i < steps; i++)
        {
            var level = (byte)(16 + i * (235 - 16) / (steps - 1));
            var x0 = i * stepWidth;
            var width = i == steps - 1 ? _width - x0 : stepWidth;
            FillArea(frame, x0, barsHeight, width, _height - barsHeight, level, 128, 128);
        }
    }

    /// <summary>A hand sweeping past a fixed ring of ticks. The demo advances the hand by the
    /// same angle every source frame, so any unevenness in its sweep past the ticks is the
    /// display holding or dropping a composition - not the network or the decoder, which the
    /// frame ladder in the corner accounts for separately.</summary>
    private unsafe void PaintRefreshChecker(AVFrame* frame, double t)
    {
        FillArea(frame, 0, 0, _width, _height, 30, 128, 128);

        var cx = _width / 2;
        var cy = _height / 2;
        var radius = Math.Min(_width, _height) * 2 / 5;

        const int ticks = 12;
        var tickSize = Math.Max(_width / 60, 6);
        for (var i = 0; i < ticks; i++)
        {
            var angle = i * 2 * Math.PI / ticks;
            var x = cx + (int)(Math.Cos(angle) * radius) - tickSize / 2;
            var y = cy + (int)(Math.Sin(angle) * radius) - tickSize / 2;
            FillRect(frame, x, y, tickSize, tickSize, 200);
        }

        // One turn every two seconds - slow enough to watch the hand cross each tick in turn.
        var handAngle = t * Math.PI;
        var stamp = Math.Max(_width / 90, 4);
        for (var r = 0; r < radius; r += Math.Max(stamp / 2, 2))
        {
            var x = cx + (int)(Math.Cos(handAngle) * r) - stamp / 2;
            var y = cy + (int)(Math.Sin(handAngle) * r) - stamp / 2;
            FillRect(frame, x, y, stamp, stamp, 245);
        }
    }

    /// <summary>The frame counter as a binary ladder in the top-left corner: one square per bit.</summary>
    private unsafe void PaintFrameLadder(AVFrame* frame, long index)
    {
        var square = Math.Max(_width / 36, 8);
        for (var bit = 0; bit < 10; bit++)
        {
            var on = ((index >> bit) & 1) == 1;
            var x0 = square / 2 + bit * (square + square / 3);
            FillRect(frame, x0, square / 2, square, square, (byte)(on ? 235 : 30));
        }
    }

    private static double Bounce(double phase)
    {
        var p = phase % 2.0;
        return p < 1.0 ? p : 2.0 - p;
    }

    private unsafe void FillDisc(AVFrame* frame, int cx, int cy, int radius, byte y, byte u, byte v)
    {
        var luma = frame->data[0];
        var cb = frame->data[1];
        var cr = frame->data[2];
        var lumaStride = frame->linesize[0];
        var chromaStride = frame->linesize[1];

        for (var row = Math.Max(cy - radius, 0); row < Math.Min(cy + radius, _height); row++)
        {
            var dy = row - cy;
            var half = (int)Math.Sqrt(Math.Max(radius * radius - dy * dy, 0));
            var x0 = Math.Max(cx - half, 0);
            var x1 = Math.Min(cx + half, _width);
            if (x1 <= x0) continue;

            new Span<byte>(luma + (long)row * lumaStride + x0, x1 - x0).Fill(y);
            if ((row & 1) == 0)
            {
                new Span<byte>(cb + (long)(row / 2) * chromaStride + x0 / 2, (x1 - x0) / 2).Fill(u);
                new Span<byte>(cr + (long)(row / 2) * chromaStride + x0 / 2, (x1 - x0) / 2).Fill(v);
            }
        }
    }

    private unsafe void FillRect(AVFrame* frame, int x, int y, int width, int height, byte luma)
    {
        var plane = frame->data[0];
        var stride = frame->linesize[0];
        var x1 = Math.Min(x + width, _width);
        for (var row = Math.Max(y, 0); row < Math.Min(y + height, _height); row++)
            new Span<byte>(plane + (long)row * stride + x, x1 - x).Fill(luma);
    }

    /// <summary>Like <see cref="FillRect"/>, but paints the chroma too - for a solid block of
    /// colour rather than a grey one.</summary>
    private unsafe void FillArea(AVFrame* frame, int x, int y, int width, int height, byte luma, byte u, byte v)
    {
        var lumaPlane = frame->data[0];
        var cb = frame->data[1];
        var cr = frame->data[2];
        var lumaStride = frame->linesize[0];
        var chromaStride = frame->linesize[1];

        var x0 = Math.Clamp(x, 0, _width);
        var x1 = Math.Clamp(x + width, 0, _width);
        if (x1 <= x0) return;

        for (var row = Math.Max(y, 0); row < Math.Min(y + height, _height); row++)
        {
            new Span<byte>(lumaPlane + (long)row * lumaStride + x0, x1 - x0).Fill(luma);
            if ((row & 1) == 0)
            {
                var cx0 = x0 / 2;
                var cx1 = x1 / 2;
                if (cx1 > cx0)
                {
                    new Span<byte>(cb + (long)(row / 2) * chromaStride + cx0, cx1 - cx0).Fill(u);
                    new Span<byte>(cr + (long)(row / 2) * chromaStride + cx0, cx1 - cx0).Fill(v);
                }
            }
        }
    }

    private static (double R, double G, double B) HsvToRgb(double hue, double saturation, double value)
    {
        var c = value * saturation;
        var h = hue / 60.0;
        var x = c * (1 - Math.Abs(h % 2 - 1));
        var (r, g, b) = (int)h switch
        {
            0 => (c, x, 0.0),
            1 => (x, c, 0.0),
            2 => (0.0, c, x),
            3 => (0.0, x, c),
            4 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        var m = value - c;
        return (r + m, g + m, b + m);
    }

    /// <summary>BT.601 limited range, which is what the decoder assumes for a picture this size.</summary>
    private static (byte Y, byte U, byte V) ToYuv(double r, double g, double b)
    {
        var y = 16 + (65.481 * r + 128.553 * g + 24.966 * b);
        var u = 128 + (-37.797 * r - 74.203 * g + 112.0 * b);
        var v = 128 + (112.0 * r - 93.786 * g - 18.214 * b);
        return ((byte)Math.Clamp(y, 16, 235), (byte)Math.Clamp(u, 16, 240), (byte)Math.Clamp(v, 16, 240));
    }
}
