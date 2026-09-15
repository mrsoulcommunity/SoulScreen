using FFmpeg.AutoGen;
using SoulScreen.Core.Logging;
using SoulScreen.Core.Media;

namespace SoulScreen.Media;

/// <summary>
/// Decodes the audio an iPhone sends alongside its screen into interleaved 16-bit PCM.
/// <para>
/// Mirroring uses AAC-ELD, which no Windows codec decodes - Media Foundation stops at
/// AAC-LC - so FFmpeg is not merely convenient here, it is the only option.
/// </para>
/// <para>Not thread-safe; drive it from a single decode thread.</para>
/// </summary>
public sealed unsafe class AudioDecoder : IDisposable
{
    private readonly ILogger _log = Log.For("audio-dec");

    private AVCodecContext* _context;
    private AVFrame* _frame;
    private AVPacket* _packet;
    private SwrContext* _resampler;
    private byte[] _pcm = new byte[16384];

    public AudioDecoder(AudioFormat format)
    {
        FFmpegRuntime.ThrowIfUnavailable();

        Format = format;
        OutputSampleRate = format.SampleRate > 0 ? format.SampleRate : 44100;
        OutputChannels = format.Channels > 0 ? format.Channels : 2;

        var codecId = format.Codec switch
        {
            AudioCodec.AacEld or AudioCodec.AacLc => AVCodecID.AV_CODEC_ID_AAC,
            AudioCodec.Alac => AVCodecID.AV_CODEC_ID_ALAC,
            AudioCodec.Pcm16 => AVCodecID.AV_CODEC_ID_PCM_S16LE,
            _ => AVCodecID.AV_CODEC_ID_AAC,
        };

        var codec = ffmpeg.avcodec_find_decoder(codecId);
        if (codec is null) throw new FFmpegUnavailableException($"This FFmpeg build cannot decode {format.Codec}.");

        _context = ffmpeg.avcodec_alloc_context3(codec);
        if (_context is null) throw new FFmpegUnavailableException("Could not allocate an audio decoder context.");

        _context->sample_rate = OutputSampleRate;
        ffmpeg.av_channel_layout_default(&_context->ch_layout, OutputChannels);

        // AAC packets carry no format description of their own; the decoder learns the
        // profile, rate and channel count from the AudioSpecificConfig handed to it here.
        var extradata = format.MagicCookie.Length > 0
            ? format.MagicCookie
            : AudioSpecificConfig.Build(format);

        if (extradata.Length > 0 && codecId == AVCodecID.AV_CODEC_ID_AAC)
        {
            // FFmpeg frees extradata itself, so it has to own the allocation.
            _context->extradata = (byte*)ffmpeg.av_mallocz((ulong)extradata.Length + ffmpeg.AV_INPUT_BUFFER_PADDING_SIZE);
            _context->extradata_size = extradata.Length;
            fixed (byte* source = extradata)
                Buffer.MemoryCopy(source, _context->extradata, extradata.Length, extradata.Length);
        }

        var result = ffmpeg.avcodec_open2(_context, codec, null);
        if (result < 0)
        {
            var context = _context;
            ffmpeg.avcodec_free_context(&context);
            _context = null;

            // ALAC is the case worth naming: it cannot be decoded without the magic cookie
            // the sender supplies, and SoulScreen only reconstructs configs for AAC. Nothing
            // requests ALAC for mirroring, so this is a gap rather than a defect - but a
            // silent failure here would be very hard to place.
            var detail = format.Codec == AudioCodec.Alac && format.MagicCookie.Length == 0
                ? "ALAC needs the codec configuration from the sender, which this stream did not provide."
                : FFmpegRuntime.DescribeError(result);

            throw new FFmpegUnavailableException($"Could not open the {format.Codec} decoder: {detail}");
        }

        _frame = ffmpeg.av_frame_alloc();
        _packet = ffmpeg.av_packet_alloc();

        _log.Info($"audio decoder ready: {format.Codec} {OutputSampleRate} Hz x{OutputChannels}" +
                  (extradata.Length > 0 ? $", config {Convert.ToHexString(extradata)}" : ""));
    }

    public AudioFormat Format { get; }

    /// <summary>Sample rate of the PCM this produces.</summary>
    public int OutputSampleRate { get; }

    /// <summary>Channel count of the PCM this produces.</summary>
    public int OutputChannels { get; }

    public long DecodedPacketCount { get; private set; }

    public long ErrorCount { get; private set; }

    /// <summary>
    /// Decodes one packet into interleaved little-endian 16-bit PCM.
    /// </summary>
    /// <returns>
    /// A span over an internal buffer, valid until the next call. Empty when the packet
    /// produced no audio.
    /// </returns>
    public ReadOnlySpan<byte> Decode(ReadOnlySpan<byte> packet)
    {
        if (_context is null) throw new ObjectDisposedException(nameof(AudioDecoder));
        if (packet.IsEmpty) return [];

        fixed (byte* data = packet)
        {
            _packet->data = data;
            _packet->size = packet.Length;

            var sent = ffmpeg.avcodec_send_packet(_context, _packet);
            _packet->data = null;
            _packet->size = 0;

            if (sent < 0)
            {
                ErrorCount++;
                _log.Trace($"audio packet rejected: {FFmpegRuntime.DescribeError(sent)}");
                // A malformed packet can leave FFmpeg's input queue in a non-progressing
                // state. Flush only that decoder state so the next valid packet can still
                // produce audio; the receiver must not lose the whole session to one bad
                // encrypted datagram.
                ffmpeg.avcodec_flush_buffers(_context);
                return [];
            }
        }

        var written = 0;
        while (true)
        {
            var received = ffmpeg.avcodec_receive_frame(_context, _frame);
            if (received == ffmpeg.AVERROR(ffmpeg.EAGAIN) || received == ffmpeg.AVERROR_EOF) break;
            if (received < 0)
            {
                ErrorCount++;
                _log.Trace($"audio frame rejected: {FFmpegRuntime.DescribeError(received)}");
                ffmpeg.avcodec_flush_buffers(_context);
                break;
            }

            try
            {
                written += Resample(_frame, written);
            }
            finally
            {
                ffmpeg.av_frame_unref(_frame);
            }
        }

        if (written > 0) DecodedPacketCount++;
        return _pcm.AsSpan(0, written);
    }

    /// <summary>
    /// Converts a decoded frame to interleaved 16-bit PCM, appending at
    /// <paramref name="offset"/>. AAC decodes to planar float, which no audio device takes
    /// directly.
    /// </summary>
    private int Resample(AVFrame* frame, int offset)
    {
        EnsureResampler(frame);
        if (_resampler is null) return 0;

        var maxSamples = (int)ffmpeg.swr_get_out_samples(_resampler, frame->nb_samples);
        var maxBytes = maxSamples * OutputChannels * sizeof(short);
        if (offset + maxBytes > _pcm.Length)
            Array.Resize(ref _pcm, Math.Max(_pcm.Length * 2, offset + maxBytes));

        fixed (byte* destination = &_pcm[offset])
        {
            var output = destination;
            var converted = ffmpeg.swr_convert(_resampler, &output, maxSamples, frame->extended_data, frame->nb_samples);
            if (converted < 0)
            {
                ErrorCount++;
                return 0;
            }
            return converted * OutputChannels * sizeof(short);
        }
    }

    private void EnsureResampler(AVFrame* frame)
    {
        if (_resampler is not null) return;

        AVChannelLayout outputLayout;
        ffmpeg.av_channel_layout_default(&outputLayout, OutputChannels);

        SwrContext* resampler = null;
        var result = ffmpeg.swr_alloc_set_opts2(
            &resampler,
            &outputLayout, AVSampleFormat.AV_SAMPLE_FMT_S16, OutputSampleRate,
            &frame->ch_layout, (AVSampleFormat)frame->format, frame->sample_rate,
            0, null);

        if (result < 0 || resampler is null)
        {
            _log.Error($"could not build the audio resampler: {FFmpegRuntime.DescribeError(result)}");
            return;
        }

        if (ffmpeg.swr_init(resampler) < 0)
        {
            ffmpeg.swr_free(&resampler);
            _log.Error("could not initialise the audio resampler");
            return;
        }

        _resampler = resampler;
        _log.Debug($"resampling {(AVSampleFormat)frame->format} {frame->sample_rate} Hz " +
                   $"to S16 {OutputSampleRate} Hz x{OutputChannels}");
    }

    public void Dispose()
    {
        if (_resampler is not null)
        {
            var resampler = _resampler;
            ffmpeg.swr_free(&resampler);
            _resampler = null;
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
