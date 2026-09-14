using FFmpeg.AutoGen;
using SoulScreen.Core.Logging;

namespace SoulScreen.Media;

/// <summary>
/// Encodes interleaved 16-bit PCM to AAC-LC packets, for the audio track of a recording.
/// <para>
/// The phone's own audio is AAC-ELD, which almost nothing outside FFmpeg can play back, so a
/// recording that simply remuxed it would open silent in most players. Re-encoding to
/// AAC-LC costs a fraction of a core and produces a file that plays everywhere.
/// </para>
/// <para>
/// PCM is accepted in any chunk size and encoded in the fixed 1024-sample frames AAC uses,
/// with a FIFO in between. Packets come out with presentation times counted in samples from
/// the first one fed in.
/// </para>
/// <para>Not thread-safe; the recorder serialises calls.</para>
/// </summary>
internal sealed unsafe class AacEncoder : IDisposable
{
    private readonly ILogger _log = Log.For("aac-enc");

    private AVCodecContext* _context;
    private SwrContext* _resampler;
    private AVAudioFifo* _fifo;
    private AVFrame* _frame;
    private AVPacket* _packet;

    /// <summary>Presentation time, in samples, of the first sample still waiting in the FIFO.</summary>
    private long _fifoPts;

    private bool _flushed;

    /// <param name="sampleRate">Rate of the PCM fed in, and of the encoded track.</param>
    /// <param name="channels">Channels in the PCM fed in.</param>
    /// <param name="globalHeader">True when the container keeps the codec configuration in
    /// its header rather than in each packet, which MP4 does.</param>
    /// <param name="bitRate">Target bit rate. 160 kb/s is transparent for stereo AAC-LC.</param>
    public AacEncoder(int sampleRate, int channels, bool globalHeader, int bitRate = 160_000)
    {
        FFmpegRuntime.ThrowIfUnavailable();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(channels);

        SampleRate = sampleRate;
        Channels = channels;

        var codec = ffmpeg.avcodec_find_encoder(AVCodecID.AV_CODEC_ID_AAC);
        if (codec is null) throw new FFmpegUnavailableException("This FFmpeg build has no AAC encoder.");

        _context = ffmpeg.avcodec_alloc_context3(codec);
        if (_context is null) throw new FFmpegUnavailableException("Could not allocate an AAC encoder context.");

        try
        {
            _context->sample_rate = sampleRate;
            _context->sample_fmt = AVSampleFormat.AV_SAMPLE_FMT_FLTP;
            _context->bit_rate = bitRate;
            _context->time_base = new AVRational { num = 1, den = sampleRate };
            ffmpeg.av_channel_layout_default(&_context->ch_layout, channels);
            if (globalHeader) _context->flags |= ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER;

            var result = ffmpeg.avcodec_open2(_context, codec, null);
            if (result < 0)
                throw new FFmpegUnavailableException($"Could not open the AAC encoder: {FFmpegRuntime.DescribeError(result)}");

            FrameSize = _context->frame_size > 0 ? _context->frame_size : 1024;

            // The encoder wants planar float; the decoder hands over interleaved shorts.
            AVChannelLayout layout;
            ffmpeg.av_channel_layout_default(&layout, channels);
            SwrContext* resampler = null;
            result = ffmpeg.swr_alloc_set_opts2(
                &resampler,
                &layout, AVSampleFormat.AV_SAMPLE_FMT_FLTP, sampleRate,
                &layout, AVSampleFormat.AV_SAMPLE_FMT_S16, sampleRate,
                0, null);
            if (result < 0 || resampler is null || ffmpeg.swr_init(resampler) < 0)
            {
                if (resampler is not null) ffmpeg.swr_free(&resampler);
                throw new FFmpegUnavailableException("Could not build the recording audio converter.");
            }
            _resampler = resampler;

            _fifo = ffmpeg.av_audio_fifo_alloc(AVSampleFormat.AV_SAMPLE_FMT_FLTP, channels, FrameSize * 4);
            if (_fifo is null) throw new FFmpegUnavailableException("Could not allocate the recording audio buffer.");

            _frame = ffmpeg.av_frame_alloc();
            _frame->nb_samples = FrameSize;
            _frame->format = (int)AVSampleFormat.AV_SAMPLE_FMT_FLTP;
            _frame->sample_rate = sampleRate;
            ffmpeg.av_channel_layout_copy(&_frame->ch_layout, &_context->ch_layout);
            result = ffmpeg.av_frame_get_buffer(_frame, 0);
            if (result < 0)
                throw new FFmpegUnavailableException($"Could not allocate an audio frame: {FFmpegRuntime.DescribeError(result)}");

            _packet = ffmpeg.av_packet_alloc();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public int SampleRate { get; }

    public int Channels { get; }

    /// <summary>Samples per encoded packet: 1024 for AAC-LC.</summary>
    public int FrameSize { get; }

    /// <summary>Time base the packets' timestamps are expressed in: one sample.</summary>
    public AVRational TimeBase => new() { num = 1, den = SampleRate };

    /// <summary>Samples fed in so far, which is also the presentation time of the next one.</summary>
    public long SamplesFed { get; private set; }

    /// <summary>Copies the encoder's configuration onto a container stream. Call once, before
    /// the container header is written.</summary>
    public void FillStreamParameters(AVStream* stream)
    {
        var result = ffmpeg.avcodec_parameters_from_context(stream->codecpar, _context);
        if (result < 0)
            throw new FFmpegUnavailableException($"Could not describe the audio track: {FFmpegRuntime.DescribeError(result)}");
        stream->time_base = TimeBase;
    }

    /// <summary>
    /// Feeds PCM in and hands every packet that becomes complete to <paramref name="sink"/>.
    /// The packet is only valid during the call; its timestamps are in <see cref="TimeBase"/>.
    /// </summary>
    /// <returns>False if the encoder has failed and should be abandoned.</returns>
    public bool Encode(ReadOnlySpan<byte> pcm16, Action<nint> sink)
    {
        if (_context is null || _flushed) return false;

        var bytesPerFrame = Channels * sizeof(short);
        var samples = pcm16.Length / bytesPerFrame;
        if (samples <= 0) return true;

        // Convert straight into the FIFO through a scratch frame sized to this chunk.
        var scratch = ffmpeg.av_frame_alloc();
        try
        {
            scratch->nb_samples = samples;
            scratch->format = (int)AVSampleFormat.AV_SAMPLE_FMT_FLTP;
            scratch->sample_rate = SampleRate;
            ffmpeg.av_channel_layout_copy(&scratch->ch_layout, &_context->ch_layout);
            var result = ffmpeg.av_frame_get_buffer(scratch, 0);
            if (result < 0)
            {
                _log.Error($"could not allocate a conversion frame: {FFmpegRuntime.DescribeError(result)}");
                return false;
            }

            fixed (byte* source = pcm16)
            {
                var input = source;
                var converted = ffmpeg.swr_convert(_resampler, scratch->extended_data, samples, &input, samples);
                if (converted < 0)
                {
                    _log.Error($"could not convert recording audio: {FFmpegRuntime.DescribeError(converted)}");
                    return false;
                }
                if (converted == 0) return true;

                var written = ffmpeg.av_audio_fifo_write(_fifo, (void**)scratch->extended_data, converted);
                if (written < converted)
                {
                    _log.Error("could not buffer recording audio");
                    return false;
                }
                SamplesFed += converted;
            }
        }
        finally
        {
            ffmpeg.av_frame_free(&scratch);
        }

        while (ffmpeg.av_audio_fifo_size(_fifo) >= FrameSize)
        {
            var result = ffmpeg.av_frame_make_writable(_frame);
            if (result < 0) return Fail("could not reuse the audio frame", result);

            var read = ffmpeg.av_audio_fifo_read(_fifo, (void**)_frame->extended_data, FrameSize);
            if (read < FrameSize) return Fail("could not read the buffered audio", read);

            _frame->nb_samples = FrameSize;
            _frame->pts = _fifoPts;
            _fifoPts += FrameSize;

            if (!SendAndDrain(_frame, sink)) return false;
        }

        return true;
    }

    /// <summary>
    /// Encodes whatever is left - a final partial frame padded with silence - and drains
    /// the encoder. Call once, at the end of the recording.
    /// </summary>
    public void Flush(Action<nint> sink)
    {
        if (_context is null || _flushed) return;
        _flushed = true;

        var remaining = ffmpeg.av_audio_fifo_size(_fifo);
        if (remaining > 0 && ffmpeg.av_frame_make_writable(_frame) >= 0)
        {
            // Zero the frame first so the tail past the real samples is silence, not
            // whatever the previous frame left there.
            for (uint plane = 0; plane < Channels; plane++)
            {
                var data = _frame->extended_data[plane];
                if (data is not null) new Span<byte>(data, FrameSize * sizeof(float)).Clear();
            }

            var read = ffmpeg.av_audio_fifo_read(_fifo, (void**)_frame->extended_data, remaining);
            if (read > 0)
            {
                _frame->nb_samples = read;
                _frame->pts = _fifoPts;
                _fifoPts += read;
                SendAndDrain(_frame, sink);
            }
        }

        SendAndDrain(null, sink);
    }

    private bool SendAndDrain(AVFrame* frame, Action<nint> sink)
    {
        var sent = ffmpeg.avcodec_send_frame(_context, frame);
        if (sent < 0 && sent != ffmpeg.AVERROR_EOF)
            return Fail("the AAC encoder rejected a frame", sent);

        while (true)
        {
            var received = ffmpeg.avcodec_receive_packet(_context, _packet);
            if (received == ffmpeg.AVERROR(ffmpeg.EAGAIN) || received == ffmpeg.AVERROR_EOF) return true;
            if (received < 0) return Fail("the AAC encoder failed", received);

            try { sink((nint)_packet); }
            finally { ffmpeg.av_packet_unref(_packet); }
        }
    }

    private bool Fail(string what, int code)
    {
        _log.Error($"{what}: {FFmpegRuntime.DescribeError(code)}");
        return false;
    }

    public void Dispose()
    {
        if (_packet is not null)
        {
            var packet = _packet;
            ffmpeg.av_packet_free(&packet);
            _packet = null;
        }

        if (_frame is not null)
        {
            var frame = _frame;
            ffmpeg.av_frame_free(&frame);
            _frame = null;
        }

        if (_fifo is not null)
        {
            ffmpeg.av_audio_fifo_free(_fifo);
            _fifo = null;
        }

        if (_resampler is not null)
        {
            var resampler = _resampler;
            ffmpeg.swr_free(&resampler);
            _resampler = null;
        }

        if (_context is not null)
        {
            var context = _context;
            ffmpeg.avcodec_free_context(&context);
            _context = null;
        }
    }
}
