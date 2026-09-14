using System.Buffers;
using System.Diagnostics;
using FFmpeg.AutoGen;
using SoulScreen.Core.Logging;
using SoulScreen.Core.Media;

namespace SoulScreen.Media;

/// <summary>Describes the audio track a recording should carry.</summary>
/// <param name="SampleRate">Rate of the PCM that will be written, and of the track.</param>
/// <param name="Channels">Channels in the PCM that will be written.</param>
public readonly record struct RecordingAudioTrack(int SampleRate, int Channels)
{
    /// <summary>What AirPlay mirroring always sends: 44.1 kHz stereo.</summary>
    public static RecordingAudioTrack AirPlay { get; } = new(44100, 2);
}

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
/// <para>
/// Audio, when asked for, is a second track. The phone's AAC-ELD is decoded to PCM on the
/// way in and re-encoded as AAC-LC, because that is what every player can open; see
/// <see cref="AacEncoder"/>. The two tracks are lined up by arrival: the audio's first packet
/// is placed at the moment it turned up relative to the first video frame, and from then on
/// the sender's own audio clock spaces the packets. Gaps in that clock become silence, so
/// the audio never drifts ahead of the picture over a long recording.
/// </para>
/// <para>
/// Video is written from the decode thread and audio from the receive thread, so every
/// call is serialised on one lock.
/// </para>
/// </summary>
public sealed unsafe class SessionRecorder : IDisposable
{
    /// <summary>
    /// Longest run of missing audio filled with silence. Past this the audio clock has jumped
    /// - a stream torn down and set up again - and the track is re-anchored on arrival time.
    /// </summary>
    private static readonly TimeSpan MaximumAudioGapFill = TimeSpan.FromSeconds(2);

    /// <summary>
    /// How far the audio track may sit from where arrival time says it should be before it is
    /// pulled back into line. Wide enough that ordinary Wi-Fi jitter never trips it, tight
    /// enough that a slipped clock is corrected long before anyone would notice lips drifting.
    /// </summary>
    private static readonly TimeSpan AudioDriftTolerance = TimeSpan.FromMilliseconds(400);

    private readonly ILogger _log = Log.For("record");
    private readonly object _sync = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private AVFormatContext* _format;
    private AVStream* _videoStream;
    private AVStream* _audioStream;
    private AVPacket* _packet;
    private AacEncoder? _encoder;

    private long _firstTimestampUs = -1;
    private long _lastTimestampUs = -1;
    private bool _headerWritten;
    private bool _faulted;
    private bool _audioFaulted;
    private bool _disposed;

    /// <summary>Where the next audio packet is expected on the sender's clock.</summary>
    private long _audioExpectedTimestampUs;
    private bool _audioAnchored;
    private int _audioDriftCorrections;

    private SessionRecorder(string path) => Path = path;

    /// <summary>File being written.</summary>
    public string Path { get; }

    /// <summary>Frames written so far.</summary>
    public long FrameCount { get; private set; }

    /// <summary>Audio packets accepted so far, for the log.</summary>
    public long AudioPacketCount { get; private set; }

    /// <summary>True when the file carries an audio track.</summary>
    public bool HasAudio => _encoder is not null;

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
    /// Opens a recording for a stream of the given format, with an audio track when
    /// <paramref name="audio"/> is given.
    /// </summary>
    /// <exception cref="FFmpegUnavailableException">FFmpeg is missing, or the file cannot be written.</exception>
    public static SessionRecorder Create(string path, VideoFormat format, RecordingAudioTrack? audio = null)
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
            recorder.Open(configuration, width, height, audio);
            return recorder;
        }
        catch
        {
            recorder.Dispose();
            throw;
        }
    }

    private void Open(AvcDecoderConfiguration configuration, int width, int height, RecordingAudioTrack? audio)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path) ?? ".");

        AVFormatContext* format = null;
        var result = ffmpeg.avformat_alloc_output_context2(&format, null, null, Path);
        if (result < 0 || format is null)
            throw new FFmpegUnavailableException($"Could not create an MP4 writer: {FFmpegRuntime.DescribeError(result)}");
        _format = format;

        _videoStream = ffmpeg.avformat_new_stream(_format, null);
        if (_videoStream is null) throw new FFmpegUnavailableException("Could not add a video track.");

        var parameters = _videoStream->codecpar;
        parameters->codec_type = AVMediaType.AVMEDIA_TYPE_VIDEO;
        parameters->codec_id = AVCodecID.AV_CODEC_ID_H264;
        parameters->width = width;
        parameters->height = height;
        parameters->format = (int)AVPixelFormat.AV_PIX_FMT_YUV420P;

        // Microsecond timestamps, matching what the transport reports, so no rescaling is
        // needed and a variable frame rate records faithfully.
        _videoStream->time_base = new AVRational { num = 1, den = 1_000_000 };

        var avcC = configuration.ToAvcC();
        parameters->extradata = (byte*)ffmpeg.av_mallocz((ulong)avcC.Length + ffmpeg.AV_INPUT_BUFFER_PADDING_SIZE);
        parameters->extradata_size = avcC.Length;
        fixed (byte* source = avcC)
            Buffer.MemoryCopy(source, parameters->extradata, avcC.Length, avcC.Length);

        if (audio is { } track)
        {
            // An audio track that cannot be opened - an FFmpeg build without the encoder -
            // downgrades the recording to video only rather than refusing to record at all.
            try
            {
                var globalHeader = (_format->oformat->flags & ffmpeg.AVFMT_GLOBALHEADER) != 0;
                _encoder = new AacEncoder(track.SampleRate, track.Channels, globalHeader);
                _audioStream = ffmpeg.avformat_new_stream(_format, null);
                if (_audioStream is null) throw new FFmpegUnavailableException("Could not add an audio track.");
                _encoder.FillStreamParameters(_audioStream);
            }
            catch (Exception ex)
            {
                _log.Warn($"recording without audio: {ex.Message}");
                _encoder?.Dispose();
                _encoder = null;
                _audioStream = null;
            }
        }

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
        _clock.Restart();
        _log.Info($"recording {width}x{height}{(_encoder is null ? "" : " with audio")} to {Path}");
    }

    /// <summary>
    /// Writes one access unit. Frames before the first keyframe are ignored so the file
    /// always opens on something decodable.
    /// </summary>
    public void Write(ReadOnlySpan<byte> annexB, long timestampUs, bool isKeyFrame)
    {
        lock (_sync)
        {
            if (_disposed || _faulted || _format is null || _packet is null) return;
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

                if (_firstTimestampUs < 0)
                {
                    _firstTimestampUs = timestampUs;
                    // Audio arrival is measured from the first picture, not from the file being
                    // opened, so the two tracks share a zero.
                    _clock.Restart();
                }

                // Timestamps must not go backwards or repeat, or the muxer rejects the packet.
                var presentation = Math.Max(timestampUs - _firstTimestampUs, _lastTimestampUs - _firstTimestampUs + 1);
                _lastTimestampUs = _firstTimestampUs + presentation;

                fixed (byte* data = buffer)
                {
                    _packet->data = data;
                    _packet->size = length;
                    _packet->stream_index = _videoStream->index;
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
    }

    /// <summary>
    /// Writes one packet's worth of decoded audio: interleaved 16-bit PCM at the rate and
    /// channel count the track was opened with. Ignored, harmlessly, on a video-only file
    /// and before the first picture has been written.
    /// </summary>
    /// <param name="pcm16">The decoded packet.</param>
    /// <param name="timestampUs">Its presentation time on the sender's audio clock.</param>
    public void WriteAudio(ReadOnlySpan<byte> pcm16, long timestampUs)
    {
        lock (_sync)
        {
            var encoder = _encoder;
            if (_disposed || _faulted || _audioFaulted || encoder is null || _format is null) return;
            if (_firstTimestampUs < 0) return;

            var bytesPerFrame = encoder.Channels * sizeof(short);
            var samples = pcm16.Length / bytesPerFrame;
            if (samples <= 0) return;

            var durationUs = (long)samples * 1_000_000 / encoder.SampleRate;

            if (!_audioAnchored)
            {
                // The first packet lands where it arrived, measured against the first picture.
                // Everything after it is spaced by the sender's clock.
                AnchorAudioToArrival(encoder);
                _audioAnchored = true;
            }
            else
            {
                var gapUs = timestampUs - _audioExpectedTimestampUs;
                if (gapUs > durationUs / 2 && gapUs <= (long)MaximumAudioGapFill.TotalMicroseconds)
                {
                    // Packets lost on the way: silence keeps what follows at its proper time.
                    if (!WriteSilence(encoder, (int)(gapUs * encoder.SampleRate / 1_000_000))) return;
                }
                else if (gapUs < -durationUs / 2 || gapUs > (long)MaximumAudioGapFill.TotalMicroseconds)
                {
                    // The clock jumped - iOS tore the audio stream down and set up a new one,
                    // whose timestamps start from nothing. Re-anchor on arrival.
                    AnchorAudioToArrival(encoder);
                }
                else
                {
                    CorrectDrift(encoder);
                }
            }

            _audioExpectedTimestampUs = timestampUs + durationUs;

            if (!encoder.Encode(pcm16, WritePacket))
            {
                _audioFaulted = true;
                _log.Warn("the audio track stopped; the recording continues without sound from here");
                return;
            }

            AudioPacketCount++;
        }
    }

    /// <summary>
    /// Places the audio track's write position at the current arrival time: silence up to
    /// it if the track is behind, nothing if it is already ahead. Caller must hold <see cref="_sync"/>.
    /// </summary>
    private void AnchorAudioToArrival(AacEncoder encoder)
    {
        var target = (long)(_clock.Elapsed.TotalSeconds * encoder.SampleRate);
        var behind = target - encoder.SamplesFed;
        if (behind > 0) WriteSilence(encoder, (int)Math.Min(behind, int.MaxValue));
    }

    /// <summary>
    /// Compares where the track has got to against arrival time, and pulls it back into
    /// line if the two have parted by more than the tolerance. The sender's audio clock and
    /// this PC's differ by a few parts per million, which is invisible over any recording a
    /// person makes; this guards against a clock that slips outright.
    /// </summary>
    private void CorrectDrift(AacEncoder encoder)
    {
        var target = (long)(_clock.Elapsed.TotalSeconds * encoder.SampleRate);
        var behind = target - encoder.SamplesFed;
        var tolerance = (long)(AudioDriftTolerance.TotalSeconds * encoder.SampleRate);
        if (Math.Abs(behind) <= tolerance) return;

        _audioDriftCorrections++;
        if (behind > 0)
        {
            WriteSilence(encoder, (int)Math.Min(behind, int.MaxValue));
        }
        // Ahead of arrival time cannot be corrected without discarding sound already
        // written, and it only happens if the sender's clock runs fast; the next re-anchor
        // takes care of it.
    }

    private bool WriteSilence(AacEncoder encoder, int samples)
    {
        if (samples <= 0) return true;

        var bytesPerFrame = encoder.Channels * sizeof(short);
        var chunkSamples = Math.Min(samples, encoder.SampleRate / 4);
        var silence = ArrayPool<byte>.Shared.Rent(chunkSamples * bytesPerFrame);
        try
        {
            Array.Clear(silence, 0, chunkSamples * bytesPerFrame);
            while (samples > 0)
            {
                var now = Math.Min(samples, chunkSamples);
                if (!encoder.Encode(silence.AsSpan(0, now * bytesPerFrame), WritePacket))
                {
                    _audioFaulted = true;
                    return false;
                }
                samples -= now;
            }
            return true;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(silence);
        }
    }

    /// <summary>Muxes one encoded audio packet. Caller must hold <see cref="_sync"/>.</summary>
    private void WritePacket(nint packetPointer)
    {
        if (_format is null || _audioStream is null || _encoder is null || _faulted) return;

        var packet = (AVPacket*)packetPointer;
        packet->stream_index = _audioStream->index;
        ffmpeg.av_packet_rescale_ts(packet, _encoder.TimeBase, _audioStream->time_base);

        var result = ffmpeg.av_interleaved_write_frame(_format, packet);
        if (result < 0)
        {
            _audioFaulted = true;
            _log.Error($"audio track stopped: {FFmpegRuntime.DescribeError(result)}");
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;

            if (_encoder is { } encoder)
            {
                if (_headerWritten && !_faulted && !_audioFaulted)
                {
                    // Bring the track up to the picture's length - covering the case where no
                    // audio ever arrived, which would otherwise leave an empty track some
                    // players refuse - then flush the encoder's tail.
                    var videoSamples = (long)(Duration.TotalSeconds * encoder.SampleRate);
                    var behind = videoSamples - encoder.SamplesFed;
                    if (behind > 0) WriteSilence(encoder, (int)Math.Min(behind, int.MaxValue));
                    encoder.Flush(WritePacket);
                }
                encoder.Dispose();
                _encoder = null;
            }

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
                _videoStream = null;
                _audioStream = null;
            }

            if (_packet is not null)
            {
                var packet = _packet;
                ffmpeg.av_packet_free(&packet);
                _packet = null;
            }

            if (FrameCount > 0)
                _log.Info($"recorded {FrameCount} frames, {Duration.TotalSeconds:0.#} s, " +
                          $"{FileSizeBytes / 1024.0 / 1024.0:0.#} MB to {Path}" +
                          (AudioPacketCount > 0 ? $" with {AudioPacketCount} audio packets" : "") +
                          (_audioDriftCorrections > 0 ? $" ({_audioDriftCorrections} drift corrections)" : ""));
        }
    }
}
