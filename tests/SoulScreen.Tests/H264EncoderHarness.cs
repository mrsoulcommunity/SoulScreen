using FFmpeg.AutoGen;
using SoulScreen.Media;

namespace SoulScreen.Tests;

/// <summary>
/// Produces a real H.264 elementary stream so the decoder can be tested against something
/// other than its own output.
/// <para>
/// libopenh264 is used because it ships in the LGPL FFmpeg build SoulScreen downloads and
/// is pure software, so the test behaves the same on a machine with no usable GPU encoder.
/// </para>
/// </summary>
internal static unsafe class H264EncoderHarness
{
    /// <summary>Luma value for black in the limited (TV) range iOS encodes with.</summary>
    public const byte LimitedBlack = 16;

    /// <summary>Luma value for white in the limited range.</summary>
    public const byte LimitedWhite = 235;

    public static bool IsAvailable
    {
        get
        {
            if (!FFmpegRuntime.IsAvailable) return false;
            return ffmpeg.avcodec_find_encoder_by_name("libopenh264") is not null;
        }
    }

    /// <summary>
    /// Encodes <paramref name="frameCount"/> frames of a split field - black on the left
    /// half, white on the right.
    /// </summary>
    /// <returns>
    /// One Annex-B buffer per access unit. They are kept separate on purpose: a decoder is
    /// fed one access unit at a time, which is also how the AirPlay transport delivers
    /// them - each framed payload is exactly one.
    /// </returns>
    public static IReadOnlyList<byte[]> EncodeSplitField(int width, int height, int frameCount)
    {
        FFmpegRuntime.ThrowIfUnavailable();

        var codec = ffmpeg.avcodec_find_encoder_by_name("libopenh264");
        if (codec is null) throw new InvalidOperationException("libopenh264 is not in this FFmpeg build.");

        var context = ffmpeg.avcodec_alloc_context3(codec);
        if (context is null) throw new InvalidOperationException("Could not allocate an encoder context.");

        AVFrame* frame = null;
        AVPacket* packet = null;
        var accessUnits = new List<byte[]>(frameCount);

        try
        {
            context->width = width;
            context->height = height;
            context->pix_fmt = AVPixelFormat.AV_PIX_FMT_YUV420P;
            context->time_base = new AVRational { num = 1, den = 30 };
            context->framerate = new AVRational { num = 30, den = 1 };
            context->bit_rate = 2_000_000;
            // A keyframe every frame keeps the test independent of reference handling.
            context->gop_size = 1;
            context->max_b_frames = 0;

            var opened = ffmpeg.avcodec_open2(context, codec, null);
            if (opened < 0)
                throw new InvalidOperationException($"Could not open libopenh264: {FFmpegRuntime.DescribeError(opened)}");

            frame = ffmpeg.av_frame_alloc();
            frame->format = (int)AVPixelFormat.AV_PIX_FMT_YUV420P;
            frame->width = width;
            frame->height = height;
            if (ffmpeg.av_frame_get_buffer(frame, 32) < 0)
                throw new InvalidOperationException("Could not allocate the source picture.");

            packet = ffmpeg.av_packet_alloc();

            for (var index = 0; index < frameCount; index++)
            {
                if (ffmpeg.av_frame_make_writable(frame) < 0)
                    throw new InvalidOperationException("Source picture is not writable.");

                FillSplitField(frame, width, height);
                frame->pts = index;

                Drain(context, packet, accessUnits, ffmpeg.avcodec_send_frame(context, frame));
            }

            Drain(context, packet, accessUnits, ffmpeg.avcodec_send_frame(context, null));
        }
        finally
        {
            if (packet is not null) ffmpeg.av_packet_free(&packet);
            if (frame is not null) ffmpeg.av_frame_free(&frame);
            ffmpeg.avcodec_free_context(&context);
        }

        return accessUnits;
    }

    private static void Drain(AVCodecContext* context, AVPacket* packet, List<byte[]> accessUnits, int sendResult)
    {
        if (sendResult < 0 && sendResult != ffmpeg.AVERROR_EOF)
            throw new InvalidOperationException($"send_frame failed: {FFmpegRuntime.DescribeError(sendResult)}");

        while (true)
        {
            var received = ffmpeg.avcodec_receive_packet(context, packet);
            if (received == ffmpeg.AVERROR(ffmpeg.EAGAIN) || received == ffmpeg.AVERROR_EOF) return;
            if (received < 0)
                throw new InvalidOperationException($"receive_packet failed: {FFmpegRuntime.DescribeError(received)}");

            accessUnits.Add(new ReadOnlySpan<byte>(packet->data, packet->size).ToArray());
            ffmpeg.av_packet_unref(packet);
        }
    }

    /// <summary>Black on the left half, white on the right, chroma neutral throughout.</summary>
    private static void FillSplitField(AVFrame* frame, int width, int height)
    {
        var luma = frame->data[0];
        var lumaStride = frame->linesize[0];
        for (var y = 0; y < height; y++)
        {
            var row = luma + (long)y * lumaStride;
            for (var x = 0; x < width; x++)
                row[x] = x < width / 2 ? LimitedBlack : LimitedWhite;
        }

        // 128 in both chroma planes is neutral, so the result is greyscale.
        for (var plane = 1; plane <= 2; plane++)
        {
            var data = frame->data[(uint)plane];
            var stride = frame->linesize[(uint)plane];
            for (var y = 0; y < height / 2; y++)
            {
                var row = data + (long)y * stride;
                for (var x = 0; x < width / 2; x++) row[x] = 128;
            }
        }
    }
}
