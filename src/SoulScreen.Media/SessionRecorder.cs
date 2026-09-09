using System.Buffers;
using FFmpeg.AutoGen;
using SoulScreen.Core.Logging;
using SoulScreen.Core.Media;

namespace SoulScreen.Media;

/// <summary>
/// Records a mirroring session to an MP4 file.
/// <para>
/// The phone already sends H.264, so nothing is re-encoded: the frames are remuxed exactly
/// as they arrive. That means no quality loss and almost no CPU cost, which matters because
/// recording runs alongside decoding for the live view.
/// </para>
/// <para>
/// The one transformation is framing. AirPlay delivers length-prefixed NAL units, the live
/// path converts them to Annex-B for the decoder, and MP4 wants them length-prefixed again
/// with the parameter sets held in the track header rather than repeated in every sample.
/// </para>
/// <para>Not thread-safe; the pipeline drives it from the decode thread.</para>
/// </summary>
public sealed unsafe class SessionRecorder : IDisposable
{
    private readonly ILogger _log = Log.For("record");

    private AVFormatContext* _format;
    private AVStream* _stream;
    private AVPacket* _packet;
    private long _firstTimestampUs = -1;
    private long _lastTimestampUs = -1;
    private bool _headerWritten;
    private bool _faulted;

    private SessionRecorder(string path) => Path = path;

    /// <summary>File being written.</summary>
    public string Path { get; }

    /// <summary>Frames written so far.</summary>
    public long FrameCount { get; private set; }

    /// <summary>Length of the recording so far.</summary>
    public TimeSpan Duration => _firstTimestampUs < 0 || _lastTimestampUs < 0
        ? TimeSpan.Zero
        : TimeSpan.FromMilliseconds((_lastTimestampUs - _firstTimestampUs) / 1000.0);

    /// <summary>Bytes on disk, or 0 before anything has been flushed.</summary>
    public long FileSizeBytes
    {
        get
        {
            try { return File.Exists(Path) ? new FileInfo(Path).Length : 0; }
            catch (IOException) { return 0; }
        }
    }

    /// <summary>
    /// Opens a recording for a stream of the given format.
    /// </summary>
    /// <exception cref="FFmpegUnavailableException">FFmpeg is missing, or the file cannot be written.</exception>
    public static SessionRecorder Create(string path, VideoFormat format)
    {
        FFmpegRuntime.ThrowIfUnavailable();

        if (format.Codec != VideoCodec.H264)
            throw new FFmpegUnavailableException($"Recording only handles H.264, not {format.Codec}.");

        var configuration = AvcDecoderConfiguration.FromAnnexB(format.ParameterSets)
            ?? throw new FFmpegUnavailableException(
                "Cannot start recording: the stream has not sent its codec configuration yet.");

        configuration.TryGetDimensions(out var width, out var height);
        if (width <= 0 || height <= 0)
        {
            width = format.Width;
            height = format.Height;
        }
        if (width <= 0 || height <= 0)
            throw new FFmpegUnavailableException("Cannot start recording: the picture size is unknown.");

        var recorder = new SessionRecorder(path);
        try
        {
            recorder.Open(configuration, width, height);
            return recorder;
        }
        catch
        {
            recorder.Dispose();
            throw;
        }
    }

    private void Open(AvcDecoderConfiguration configuration, int width, int height)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path) ?? ".");

        AVFormatContext* format = null;
        var result = ffmpeg.avformat_alloc_output_context2(&format, null, null, Path);
        if (result < 0 || format is null)
            throw new FFmpegUnavailableException($"Could not create an MP4 writer: {FFmpegRuntime.DescribeError(result)}");
        _format = format;

        _stream = ffmpeg.avformat_new_stream(_format, null);
        if (_stream is null) throw new FFmpegUnavailableException("Could not add a video track.");

        var parameters = _stream->codecpar;
        parameters->codec_type = AVMediaType.AVMEDIA_TYPE_VIDEO;
        parameters->codec_id = AVCodecID.AV_CODEC_ID_H264;
        parameters->width = width;
        parameters->height = height;
        parameters->format = (int)AVPixelFormat.AV_PIX_FMT_YUV420P;

        // Microsecond timestamps, matching what the transport reports, so no rescaling is
        // needed and a variable frame rate records faithfully.
        _stream->time_base = new AVRational { num = 1, den = 1_000_000 };

        var avcC = configuration.ToAvcC();
        parameters->extradata = (byte*)ffmpeg.av_mallocz((ulong)avcC.Length + ffmpeg.AV_INPUT_BUFFER_PADDING_SIZE);
        parameters->extradata_size = avcC.Length;
        fixed (byte* source = avcC)
            Buffer.MemoryCopy(source, parameters->extradata, avcC.Length, avcC.Length);

        result = ffmpeg.avio_open(&_format->pb, Path, ffmpeg.AVIO_FLAG_WRITE);
        if (result < 0)
            throw new FFmpegUnavailableException($"Could not open {Path}: {FFmpegRuntime.DescribeError(result)}");

        // faststart moves the index to the front when the trailer is written, so a
        // recording plays immediately instead of after a full download.
        AVDictionary* options = null;
        ffmpeg.av_dict_set(&options, "movflags", "+faststart", 0);
        result = ffmpeg.avformat_write_header(_format, &options);
        ffmpeg.av_dict_free(&options);

        if (result < 0)
            throw new FFmpegUnavailableException($"Could not write the MP4 header: {FFmpegRuntime.DescribeError(result)}");

        _headerWritten = true;
        _packet = ffmpeg.av_packet_alloc();
        _log.Info($"recording {width}x{height} to {Path}");
    }

    /// <summary>
    /// Writes one access unit. Frames before the first keyframe are ignored so the file
    /// always opens on something decodable.
    /// </summary>
    public void Write(ReadOnlySpan<byte> annexB, long timestampUs, bool isKeyFrame)
    {
        if (_faulted || _format is null || _packet is null) return;
        if (_firstTimestampUs < 0 && !isKeyFrame) return;
        if (annexB.IsEmpty) return;

        var buffer = ArrayPool<byte>.Shared.Rent(annexB.Length);
        try
        {
            // Parameter sets are kept in the samples as well as the track header. They are
            // redundant for a stream that never changes, but the phone re-sends them when it
            // rotates, and an MP4 whose header describes only the first orientation still
            // plays through the change if every keyframe carries its own.
            var length = H264.ConvertAnnexBToLengthPrefixed(annexB, buffer, dropParameterSets: false);
            if (length <= 0) return;

            if (_firstTimestampUs < 0) _firstTimestampUs = timestampUs;
            // Timestamps must not go backwards or repeat, or the muxer rejects the packet.
            var presentation = Math.Max(timestampUs - _firstTimestampUs, _lastTimestampUs - _firstTimestampUs + 1);
            _lastTimestampUs = _firstTimestampUs + presentation;

            fixed (byte* data = buffer)
            {
                _packet->data = data;
                _packet->size = length;
                _packet->stream_index = _stream->index;
                _packet->pts = presentation;
                _packet->dts = presentation;
                _packet->duration = 0;
                _packet->flags = isKeyFrame ? ffmpeg.AV_PKT_FLAG_KEY : 0;

                var result = ffmpeg.av_interleaved_write_frame(_format, _packet);
                _packet->data = null;
                _packet->size = 0;

                if (result < 0)
                {
                    _faulted = true;
                    _log.Error($"recording stopped: {FFmpegRuntime.DescribeError(result)}");
                    return;
                }
            }

            FrameCount++;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public void Dispose()
    {
        if (_format is not null)
        {
            // Without the trailer the file has no index and will not play.
            if (_headerWritten && !_faulted)
            {
                var result = ffmpeg.av_write_trailer(_format);
                if (result < 0) _log.Warn($"could not finalise {Path}: {FFmpegRuntime.DescribeError(result)}");
            }

            if (_format->pb is not null) ffmpeg.avio_closep(&_format->pb);

            var format = _format;
            ffmpeg.avformat_free_context(format);
            _format = null;
            _stream = null;
        }

        if (_packet is not null)
        {
            var packet = _packet;
            ffmpeg.av_packet_free(&packet);
            _packet = null;
        }

        if (FrameCount > 0)
            _log.Info($"recorded {FrameCount} frames, {Duration.TotalSeconds:0.#} s, " +
                      $"{FileSizeBytes / 1024.0 / 1024.0:0.#} MB to {Path}");
    }
}
