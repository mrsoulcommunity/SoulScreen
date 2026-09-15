using FFmpeg.AutoGen;
using SoulScreen.Core.Logging;

namespace SoulScreen.Media;

/// <summary>
/// Decodes the Annex-B H.264 stream an iPhone mirrors into BGRA pictures.
/// <para>
/// Tuned for a mirror rather than a media player: the decoder is configured for low delay,
/// so a picture comes out for every picture that goes in instead of being buffered a few
/// frames deep. That costs some throughput and buys the responsiveness that makes a mirrored
/// screen usable.
/// </para>
/// <para>Not thread-safe; drive it from a single decode thread.</para>
/// </summary>
public sealed unsafe class H264Decoder : IDisposable
{
    private readonly ILogger _log = Log.For("decode");

    // Reused across frames: sws_scale takes managed arrays, and allocating them per frame
    // would add avoidable garbage at 60 fps.
    private readonly byte*[] _sourcePlanes = new byte*[8];
    private readonly int[] _sourceStrides = new int[8];
    private readonly byte*[] _destinationPlanes = new byte*[4];
    private readonly int[] _destinationStrides = new int[4];

    private AVCodecContext* _context;
    private AVFrame* _frame;
    private AVPacket* _packet;
    private SwsContext* _scaler;

    private int _scalerWidth;
    private int _scalerHeight;
    private AVPixelFormat _scalerFormat = AVPixelFormat.AV_PIX_FMT_NONE;
    private bool _scalerFullRange;
    private AVColorSpace _scalerColorspace = AVColorSpace.AVCOL_SPC_UNSPECIFIED;

    public H264Decoder()
    {
        FFmpegRuntime.ThrowIfUnavailable();

        var codec = ffmpeg.avcodec_find_decoder(AVCodecID.AV_CODEC_ID_H264);
        if (codec is null) throw new FFmpegUnavailableException("This FFmpeg build has no H.264 decoder.");

        _context = ffmpeg.avcodec_alloc_context3(codec);
        if (_context is null) throw new FFmpegUnavailableException("Could not allocate a decoder context.");

        // Emit each picture as soon as it is decoded. Frame-level threading would buffer
        // several frames before releasing the first, which is exactly the latency a mirror
        // must not have; slice threading still parallelises when the sender uses slices.
        _context->flags |= ffmpeg.AV_CODEC_FLAG_LOW_DELAY;
        _context->thread_type = ffmpeg.FF_THREAD_SLICE;
        _context->thread_count = Math.Min(Environment.ProcessorCount, 4);

        // Deliberately not AV_CODEC_FLAG2_FAST. It buys a few percent by allowing
        // interpolation that does not match the specification bit for bit, and on any
        // ordinary video that is invisible. Mirroring is the case where it is not: the phone
        // sends keyframes seconds apart, so every picture in between is predicted from our
        // reconstruction rather than the phone's. Each small mismatch is carried into the
        // next frame and the next, and by the end of a long run of inter frames the drift is
        // plain - blocks that no longer match their surroundings, drifting green where the
        // chroma has wandered furthest. The keyframe clears it, and it starts again.

        var result = ffmpeg.avcodec_open2(_context, codec, null);
        if (result < 0)
        {
            var context = _context;
            ffmpeg.avcodec_free_context(&context);
            _context = null;
            throw new FFmpegUnavailableException($"Could not open the H.264 decoder: {FFmpegRuntime.DescribeError(result)}");
        }

        _frame = ffmpeg.av_frame_alloc();
        _packet = ffmpeg.av_packet_alloc();

        _log.Debug($"H.264 decoder ready ({_context->thread_count} slice threads)");
    }

    /// <summary>Pictures successfully decoded.</summary>
    public long FrameCount { get; private set; }

    /// <summary>Packets the decoder rejected, usually a corrupt or undecryptable frame.</summary>
    public long ErrorCount { get; private set; }

    /// <summary>
    /// Decodes one access unit. A single packet normally yields one picture, but the API
    /// permits zero or several, so the result is a list.
    /// </summary>
    /// <param name="annexB">Complete access unit with start codes, SPS/PPS included when present.</param>
    /// <param name="timestampUs">Presentation timestamp to carry through to the output.</param>
    public IReadOnlyList<DecodedVideoFrame> Decode(ReadOnlySpan<byte> annexB, long timestampUs)
    {
        if (_context is null) throw new ObjectDisposedException(nameof(H264Decoder));
        if (annexB.IsEmpty) return [];

        fixed (byte* data = annexB)
        {
            _packet->data = data;
            _packet->size = annexB.Length;
            _packet->pts = timestampUs;
            _packet->dts = timestampUs;

            var sent = ffmpeg.avcodec_send_packet(_context, _packet);
            _packet->data = null;
            _packet->size = 0;

            if (sent < 0)
            {
                ErrorCount++;
                // EAGAIN would mean the decoder is full, which cannot happen while we drain
                // after every packet; anything else is a genuinely bad access unit.
                _log.Debug($"decoder rejected a packet: {FFmpegRuntime.DescribeError(sent)}");
                return [];
            }
        }

        return DrainFrames();
    }

    /// <summary>Pushes out any pictures still held inside the decoder. Call at end of stream.</summary>
    public IReadOnlyList<DecodedVideoFrame> Flush()
    {
        if (_context is null) return [];
        ffmpeg.avcodec_send_packet(_context, null);
        var frames = DrainFrames();
        ffmpeg.avcodec_flush_buffers(_context);
        return frames;
    }

    private List<DecodedVideoFrame> DrainFrames()
    {
        var frames = new List<DecodedVideoFrame>(1);

        while (true)
        {
            var received = ffmpeg.avcodec_receive_frame(_context, _frame);
            if (received == ffmpeg.AVERROR(ffmpeg.EAGAIN) || received == ffmpeg.AVERROR_EOF) break;
            if (received < 0)
            {
                ErrorCount++;
                _log.Debug($"receive_frame failed: {FFmpegRuntime.DescribeError(received)}");
                break;
            }

            try
            {
                var converted = ConvertToBgra(_frame);
                if (converted is not null)
                {
                    frames.Add(converted);
                    FrameCount++;
                }
            }
            finally
            {
                ffmpeg.av_frame_unref(_frame);
            }
        }

        return frames;
    }

    private DecodedVideoFrame? ConvertToBgra(AVFrame* frame)
    {
        var width = frame->width;
        var height = frame->height;
        if (width <= 0 || height <= 0) return null;

        var (sourceFormat, formatIsFullRange) = ColourSpace.Normalise((AVPixelFormat)frame->format);
        var fullRange = ColourSpace.IsFullRange(frame, formatIsFullRange);
        var colorspace = ColourSpace.Of(frame);

        EnsureScaler(width, height, sourceFormat, fullRange, colorspace);
        if (_scaler is null) return null;

        // Four bytes per pixel, rows aligned to 4 bytes - already satisfied by BGRA.
        var stride = width * 4;
        var output = DecodedVideoFrame.Rent(width, height, stride, frame->pts);

        try
        {
            for (uint plane = 0; plane < 8; plane++)
            {
                _sourcePlanes[plane] = frame->data[plane];
                _sourceStrides[plane] = frame->linesize[plane];
            }

            fixed (byte* destination = output.Buffer)
            {
                _destinationPlanes[0] = destination;
                _destinationStrides[0] = stride;
                ffmpeg.sws_scale(_scaler, _sourcePlanes, _sourceStrides, 0, height,
                    _destinationPlanes, _destinationStrides);
            }

            return output;
        }
        catch
        {
            output.Dispose();
            throw;
        }
    }

    /// <summary>
    /// (Re)builds the colour converter. iOS changes resolution whenever the phone rotates,
    /// so this has to notice a new geometry mid-stream rather than assume one forever.
    /// </summary>
    private void EnsureScaler(int width, int height, AVPixelFormat sourceFormat, bool fullRange, AVColorSpace colorspace)
    {
        if (_scaler is not null && width == _scalerWidth && height == _scalerHeight &&
            sourceFormat == _scalerFormat && fullRange == _scalerFullRange && colorspace == _scalerColorspace)
            return;

        if (_scaler is not null)
        {
            ffmpeg.sws_freeContext(_scaler);
            _scaler = null;
        }

        // The geometry does not change here - this is a colour conversion, not a resize -
        // so the cheapest kernel is also the exact one.
        _scaler = ffmpeg.sws_getContext(
            width, height, sourceFormat,
            width, height, AVPixelFormat.AV_PIX_FMT_BGRA,
            ColourSpace.SwsPoint, null, null, null);

        if (_scaler is null)
        {
            _log.Error($"could not build a {sourceFormat} to BGRA converter for {width}x{height}");
            return;
        }

        if (ColourSpace.Apply(_scaler, fullRange, colorspace) < 0)
            _log.Debug("this pixel format does not accept colour-space overrides; using swscale defaults");

        _scalerWidth = width;
        _scalerHeight = height;
        _scalerFormat = sourceFormat;
        _scalerFullRange = fullRange;
        _scalerColorspace = colorspace;
        _log.Info($"colour converter: {sourceFormat} {width}x{height} " +
                  $"{(fullRange ? "full" : "limited")} range, {ColourSpace.Describe(colorspace)} to BGRA");
    }

    public void Dispose()
    {
        if (_scaler is not null)
        {
            ffmpeg.sws_freeContext(_scaler);
            _scaler = null;
        }

        if (_frame is not null)
        {
            var frame = _frame;
            ffmpeg.av_frame_free(&frame);
            _frame = null;
        }

        if (_packet is not null)
        {
            var packet = _packet;
            ffmpeg.av_packet_free(&packet);
            _packet = null;
        }

        if (_context is not null)
        {
            var context = _context;
            ffmpeg.avcodec_free_context(&context);
            _context = null;
        }
    }
}
