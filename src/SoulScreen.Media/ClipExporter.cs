using FFmpeg.AutoGen;
using SoulScreen.Core.Logging;

namespace SoulScreen.Media;

/// <summary>What a clip is saved as.</summary>
public enum ClipFormat
{
    /// <summary>An animated GIF: plays anywhere, pastes into chats, and cannot carry sound.</summary>
    Gif,

    /// <summary>A WebM: a fraction of the size of the same moment as a GIF, and needs a modern player.</summary>
    WebM,
}

/// <summary>A moment to cut out of a recording and save as an animation.</summary>
/// <param name="SourcePath">The recording to read.</param>
/// <param name="OutputPath">Where the clip goes. Its extension should match the format.</param>
/// <param name="Start">Where in the recording the clip opens.</param>
/// <param name="Duration">How long it runs, clamped to what the exporter allows.</param>
/// <param name="Format">GIF or WebM.</param>
public readonly record struct ClipRequest(
    string SourcePath,
    string OutputPath,
    TimeSpan Start,
    TimeSpan Duration,
    ClipFormat Format);

/// <summary>What was written.</summary>
public sealed record ClipExportResult(
    string Path,
    ClipFormat Format,
    int Width,
    int Height,
    int FrameCount,
    TimeSpan Duration,
    long Bytes);

/// <summary>
/// Cuts a moment out of a recording and saves it as an animation, so a few seconds of a
/// mirrored phone can be sent to someone without a video editor, a converter or a website
/// in between.
/// <para>
/// The recording is decoded and re-encoded rather than remuxed: an MP4's H.264 is not
/// something a GIF or a WebM can carry. The clip is written at a steady frame rate - 12 a
/// second for a GIF, whose format cannot do anything else, and 30 for a WebM - by holding
/// the newest picture that is not past each moment, which also settles the phone's variable
/// frame rate into something every player agrees on.
/// </para>
/// <para>
/// Size is what makes an animation shareable, so a GIF is written at most 480 pixels on its
/// longest side and a WebM at most 1280; neither is ever scaled up. Colour in a GIF is
/// quantised by libavcodec to a 256-entry palette per frame, which is what a GIF is.
/// </para>
/// <para>
/// Call it off the UI thread: a few seconds of a phone screen takes a second or two of one.
/// </para>
/// </summary>
public static unsafe class ClipExporter
{
    private static readonly ILogger Log_ = Log.For("clip");

    /// <summary>Shortest clip worth writing; anything less is a still picture.</summary>
    public static readonly TimeSpan MinimumLength = TimeSpan.FromMilliseconds(400);

    /// <summary>Longest clip the exporter will write, whatever it is asked for.</summary>
    public static readonly TimeSpan MaximumLength = TimeSpan.FromSeconds(30);

    /// <summary>Longest side of a GIF. Past this the file is too big to send.</summary>
    private const int GifMaximumSide = 480;

    /// <summary>Longest side of a WebM: a phone screen at 720p is still legible.</summary>
    private const int WebMMaximumSide = 1280;

    /// <summary>A GIF cannot vary its frame rate, and 12 a second is what the format suits.</summary>
    private const double GifFrameRate = 12;

    private const double WebMFrameRate = 30;

    private const int WebMBitRate = 4_000_000;

    /// <summary>
    /// swscale's bicubic kernel, for a picture being resized. The binding exposes neither
    /// this nor the rest of the algorithm flags, so it is repeated from
    /// libswscale/swscale.h, as <see cref="ColourSpace.SwsPoint"/> is.
    /// </summary>
    private const int SwsBicubic = 4;

    /// <summary>Whether this FFmpeg build can write clips in <paramref name="format"/>.</summary>
    public static bool Supports(ClipFormat format)
    {
        if (!FFmpegRuntime.IsAvailable) return false;
        return format == ClipFormat.Gif
            ? ffmpeg.avcodec_find_encoder(AVCodecID.AV_CODEC_ID_GIF) is not null
            : ffmpeg.avcodec_find_encoder(AVCodecID.AV_CODEC_ID_VP9) is not null
              || ffmpeg.avcodec_find_encoder(AVCodecID.AV_CODEC_ID_VP8) is not null;
    }

    /// <summary>The extension a clip of this format is saved with, dot included.</summary>
    public static string ExtensionOf(ClipFormat format) => format == ClipFormat.Gif ? ".gif" : ".webm";

    public static string Describe(ClipFormat format) => format == ClipFormat.Gif ? "GIF" : "WebM";

    /// <summary>
    /// Writes the clip.
    /// </summary>
    /// <param name="progress">Fraction complete, 0 to 1. Reported from the calling thread.</param>
    /// <exception cref="OperationCanceledException">Cancelled. A partial file may be left behind.</exception>
    /// <exception cref="FFmpegUnavailableException">FFmpeg is missing, or the format cannot be written.</exception>
    /// <exception cref="IOException">The recording is gone, or the clip cannot be created.</exception>
    public static ClipExportResult Export(ClipRequest request, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        FFmpegRuntime.ThrowIfUnavailable();

        var name = Path.GetFileName(request.SourcePath);
        if (!File.Exists(request.SourcePath))
            throw new IOException($"{name} is not there any more.");

        var format = request.Format;
        if (!Supports(format))
            throw new FFmpegUnavailableException($"This FFmpeg build cannot write {Describe(format)} clips.");

        var start = request.Start < TimeSpan.Zero ? TimeSpan.Zero : request.Start;
        var wanted = TimeSpan.FromSeconds(Math.Clamp(request.Duration.TotalSeconds, MinimumLength.TotalSeconds, MaximumLength.TotalSeconds));

        AVFormatContext* input = null;
        AVCodecContext* decoder = null;
        AVFrame* current = null;
        AVFrame* lookahead = null;
        AVPacket* packet = null;
        ClipWriter? writer = null;

        try
        {
            var result = ffmpeg.avformat_open_input(&input, request.SourcePath, null, null);
            if (result < 0)
                throw new FFmpegUnavailableException($"Could not open {name}: {FFmpegRuntime.DescribeError(result)}");

            result = ffmpeg.avformat_find_stream_info(input, null);
            if (result < 0)
                throw new FFmpegUnavailableException($"Could not read {name}: {FFmpegRuntime.DescribeError(result)}");

            var videoIndex = ffmpeg.av_find_best_stream(input, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, null, 0);
            if (videoIndex < 0)
                throw new FFmpegUnavailableException($"{name} holds no picture to clip.");

            var stream = input->streams[videoIndex];
            var codec = ffmpeg.avcodec_find_decoder(stream->codecpar->codec_id);
            if (codec is null)
                throw new FFmpegUnavailableException($"This FFmpeg build has no decoder for {stream->codecpar->codec_id}.");

            decoder = ffmpeg.avcodec_alloc_context3(codec);
            if (decoder is null) throw new FFmpegUnavailableException("Could not allocate a decoder context.");
            result = ffmpeg.avcodec_parameters_to_context(decoder, stream->codecpar);
            if (result < 0)
                throw new FFmpegUnavailableException($"Could not describe the recording's picture: {FFmpegRuntime.DescribeError(result)}");

            // A file can be decoded with every thread the machine has; the live mirror cannot,
            // which is why the decoder it uses asks for slicing instead.
            decoder->thread_count = 0;

            result = ffmpeg.avcodec_open2(decoder, codec, null);
            if (result < 0)
                throw new FFmpegUnavailableException($"Could not open the decoder: {FFmpegRuntime.DescribeError(result)}");

            packet = ffmpeg.av_packet_alloc();
            current = ffmpeg.av_frame_alloc();
            lookahead = ffmpeg.av_frame_alloc();

            var length = wanted;
            var sourceSeconds = input->duration > 0 ? input->duration / (double)ffmpeg.AV_TIME_BASE : 0;
            if (sourceSeconds > 0)
                length = TimeSpan.FromSeconds(Math.Min(wanted.TotalSeconds, Math.Max(sourceSeconds - start.TotalSeconds, 0)));

            if (start > TimeSpan.Zero)
            {
                // Backwards to the keyframe before the moment asked for: the pictures between
                // it and the start are decoded and skipped, which is what makes the first
                // frame of a clip a whole picture rather than a smear.
                var target = ffmpeg.av_rescale_q(
                    (long)(start.TotalSeconds * 1_000_000),
                    new AVRational { num = 1, den = 1_000_000 },
                    stream->time_base);

                result = ffmpeg.av_seek_frame(input, videoIndex, target, ffmpeg.AVSEEK_FLAG_BACKWARD);
                if (result < 0)
                    Log_.Warn($"could not seek to {start.TotalSeconds:0.#} s in {name}; the clip starts at the beginning");

                ffmpeg.avcodec_flush_buffers(decoder);
            }

            if (!ReadFrame(input, decoder, packet, videoIndex, current))
                throw new FFmpegUnavailableException($"{name} holds no picture that could be decoded.");

            var frameRate = FrameRateOf(stream, format);
            var (width, height) = Fit(current->width, current->height,
                format == ClipFormat.Gif ? GifMaximumSide : WebMMaximumSide);

            writer = new ClipWriter(request.OutputPath, format, width, height, frameRate);

            var interval = 1.0 / frameRate;
            var frameCount = Math.Max((int)Math.Round(length.TotalSeconds * frameRate), 1);
            var haveLookahead = false;
            var endOfFile = false;
            var written = 0;

            for (var index = 0; index < frameCount; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var moment = start.TotalSeconds + index * interval;

                // Hold the newest picture that is not past this moment.
                while (!endOfFile)
                {
                    if (!haveLookahead)
                    {
                        if (!ReadFrame(input, decoder, packet, videoIndex, lookahead))
                        {
                            endOfFile = true;
                            break;
                        }
                        haveLookahead = true;
                    }

                    if (TimestampSeconds(lookahead, stream) > moment) break;
                    // A reference move, not a copy: the picture the clip is holding stays
                    // valid until the next one takes its place.
                    ffmpeg.av_frame_move_ref(current, lookahead);
                    haveLookahead = false;
                }

                // The recording ran out before the window did: the clip is as long as the
                // recording rather than padded with a frozen picture.
                if (endOfFile && TimestampSeconds(current, stream) + interval < moment) break;

                if (!writer.Write(current, index)) break;
                written++;
                progress?.Report(Math.Min((index + 1) / (double)frameCount, 1));
            }

            writer.Finish();

            if (written == 0) throw new FFmpegUnavailableException("No pictures fell inside that moment.");

            var lengthSeconds = written / frameRate;
            var bytes = writer.BytesOnDisk;
            Log_.Info($"wrote a {Describe(format)} clip of {lengthSeconds:0.#} s " +
                      $"({width}x{height}, {written} pictures, {bytes / 1024.0:0} KB) to {request.OutputPath}");

            progress?.Report(1);
            return new ClipExportResult(request.OutputPath, format, width, height, written,
                TimeSpan.FromSeconds(lengthSeconds), bytes);
        }
        finally
        {
            writer?.Dispose();

            if (packet is not null)
            {
                var value = packet;
                ffmpeg.av_packet_free(&value);
            }

            if (lookahead is not null)
            {
                var value = lookahead;
                ffmpeg.av_frame_free(&value);
            }

            if (current is not null)
            {
                var value = current;
                ffmpeg.av_frame_free(&value);
            }

            if (decoder is not null)
            {
                var value = decoder;
                ffmpeg.avcodec_free_context(&value);
            }

            if (input is not null) ffmpeg.avformat_close_input(&input);
        }
    }

    /// <summary>
    /// Decodes the next picture of the video stream into <paramref name="destination"/>.
    /// Packets from any other stream are dropped, and the end of the file flushes whatever
    /// the decoder is still holding.
    /// </summary>
    /// <returns>False once there is nothing left to decode.</returns>
    private static bool ReadFrame(AVFormatContext* input, AVCodecContext* decoder, AVPacket* packet, int videoIndex, AVFrame* destination)
    {
        var draining = false;

        while (true)
        {
            var received = ffmpeg.avcodec_receive_frame(decoder, destination);
            if (received >= 0) return true;
            if (received == ffmpeg.AVERROR_EOF) return false;
            if (received != ffmpeg.AVERROR(ffmpeg.EAGAIN))
                throw new FFmpegUnavailableException($"The recording could not be decoded: {FFmpegRuntime.DescribeError(received)}");

            // The decoder wants more input. Once it has been told the stream has ended and
            // still hands nothing over, there is no more picture in the file.
            if (draining) return false;

            if (ffmpeg.av_read_frame(input, packet) < 0)
            {
                draining = true;
                ffmpeg.avcodec_send_packet(decoder, null);
                continue;
            }

            if (packet->stream_index == videoIndex
                && ffmpeg.avcodec_send_packet(decoder, packet) < 0)
            {
                Log_.Debug("the decoder refused a packet; carrying on with the rest of the recording");
            }

            ffmpeg.av_packet_unref(packet);
        }
    }

    /// <summary>A picture's presentation time in seconds, on the stream's own clock.</summary>
    private static double TimestampSeconds(AVFrame* frame, AVStream* stream)
    {
        var timestamp = frame->best_effort_timestamp != ffmpeg.AV_NOPTS_VALUE
            ? frame->best_effort_timestamp
            : frame->pts;
        return timestamp == ffmpeg.AV_NOPTS_VALUE ? 0 : timestamp * ffmpeg.av_q2d(stream->time_base);
    }

    /// <summary>
    /// The rate a clip runs at. A recording slower than the format's own rate keeps its own
    /// pace rather than being padded out with repeats.
    /// </summary>
    private static double FrameRateOf(AVStream* stream, ClipFormat format)
    {
        var wanted = format == ClipFormat.Gif ? GifFrameRate : WebMFrameRate;

        var rate = ffmpeg.av_q2d(stream->avg_frame_rate);
        if (double.IsNaN(rate) || rate < 1) rate = ffmpeg.av_q2d(stream->r_frame_rate);
        if (double.IsNaN(rate) || rate <= 0) return wanted;
        return Math.Clamp(rate, 5, wanted);
    }

    /// <summary>The size a clip is written at: at most <paramref name="maximumSide"/> on its
    /// longest side, never enlarged, and always even.</summary>
    private static (int Width, int Height) Fit(int width, int height, int maximumSide)
    {
        var longest = Math.Max(width, height);
        var scale = longest > maximumSide ? maximumSide / (double)longest : 1.0;
        return (Even((int)Math.Round(width * scale)), Even((int)Math.Round(height * scale)));
    }

    private static int Even(int value) => Math.Max(2, value & ~1);

    /// <summary>
    /// The encoder and container one clip is written through. Not thread-safe; dispose it
    /// once, after <see cref="Finish"/>.
    /// </summary>
    private sealed unsafe class ClipWriter : IDisposable
    {
        private readonly ILogger _log = Log.For("clip");
        private readonly ClipFormat _clip;
        private readonly double _frameRate;
        private readonly int _width;
        private readonly int _height;

        // sws_scale takes managed arrays; they are reused for every picture.
        private readonly byte*[] _sourcePlanes = new byte*[8];
        private readonly int[] _sourceStrides = new int[8];
        private readonly byte*[] _destinationPlanes = new byte*[4];
        private readonly int[] _destinationStrides = new int[4];

        private AVCodecContext* _encoder;
        private AVFormatContext* _output;
        private AVStream* _stream;
        private AVFrame* _frame;
        private AVPacket* _packet;
        private SwsContext* _scaler;

        private int _scalerWidth;
        private int _scalerHeight;
        private AVPixelFormat _scalerFormat = AVPixelFormat.AV_PIX_FMT_NONE;
        private bool _scalerFullRange;
        private AVColorSpace _scalerColorspace = AVColorSpace.AVCOL_SPC_UNSPECIFIED;

        private bool _headerWritten;
        private bool _faulted;

        public ClipWriter(string path, ClipFormat format, int width, int height, double frameRate)
        {
            _clip = format;
            _width = width;
            _height = height;
            _frameRate = frameRate;
            Path = path;

            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path) ?? ".");
                Open();
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        /// <summary>File being written.</summary>
        public string Path { get; }

        /// <summary>Pictures written so far.</summary>
        public long FrameCount { get; private set; }

        /// <summary>Microsecond-based timestamps for WebM, hundredths for GIF, which counts
        /// its delays in centiseconds and nothing finer.</summary>
        private AVRational TimeBase => _clip == ClipFormat.Gif
            ? new AVRational { num = 1, den = 100 }
            : new AVRational { num = 1, den = 1000 };

        /// <summary>Bytes the clip has reached on disk, or 0 before it has been flushed.</summary>
        public long BytesOnDisk
        {
            get
            {
                try { return File.Exists(Path) ? new FileInfo(Path).Length : 0; }
                catch (IOException) { return 0; }
            }
        }

        private void Open()
        {
            var container = _clip == ClipFormat.Gif ? "gif" : "webm";

            // Through a local: the address of a pointer held in a field cannot be taken.
            AVFormatContext* output = null;
            var result = ffmpeg.avformat_alloc_output_context2(&output, null, container, Path);
            _output = output;
            if (result < 0 || _output is null)
                throw new FFmpegUnavailableException($"Could not create a {Describe(_clip)} writer: {FFmpegRuntime.DescribeError(result)}");

            _stream = ffmpeg.avformat_new_stream(_output, null);
            if (_stream is null)
                throw new FFmpegUnavailableException($"Could not add a track to the {Describe(_clip)} file.");

            OpenEncoder();

            _stream->time_base = TimeBase;
            result = ffmpeg.avcodec_parameters_from_context(_stream->codecpar, _encoder);
            if (result < 0)
                throw new FFmpegUnavailableException($"Could not describe the clip's track: {FFmpegRuntime.DescribeError(result)}");

            result = ffmpeg.avio_open(&_output->pb, Path, ffmpeg.AVIO_FLAG_WRITE);
            if (result < 0)
                throw new FFmpegUnavailableException($"Could not create {System.IO.Path.GetFileName(Path)}: {FFmpegRuntime.DescribeError(result)}");

            result = ffmpeg.avformat_write_header(_output, null);
            if (result < 0)
                throw new FFmpegUnavailableException($"Could not write the header: {FFmpegRuntime.DescribeError(result)}");
            _headerWritten = true;

            _frame = ffmpeg.av_frame_alloc();
            _frame->format = (int)_encoder->pix_fmt;
            _frame->width = _width;
            _frame->height = _height;
            result = ffmpeg.av_frame_get_buffer(_frame, 32);
            if (result < 0)
                throw new FFmpegUnavailableException($"Could not allocate a clip picture: {FFmpegRuntime.DescribeError(result)}");

            _packet = ffmpeg.av_packet_alloc();
            _log.Info($"encoding a {Describe(_clip)} clip: {_width}x{_height} at {_frameRate:0.#} fps to {Path}");
        }

        /// <summary>
        /// Chooses and opens the encoder. The candidates are tried in turn - VP9 before VP8,
        /// the packed formats a GIF encoder prefers before the rest - because opening is the
        /// only honest test of what a build will take, and a refusal is worth falling back on
        /// rather than failing the clip for.
        /// </summary>
        private void OpenEncoder()
        {
            var failure = 0;

            foreach (var (codecId, pixelFormat) in EncoderAttempts())
            {
                var codec = ffmpeg.avcodec_find_encoder(codecId);
                if (codec is null) continue;

                var context = ffmpeg.avcodec_alloc_context3(codec);
                if (context is null) throw new FFmpegUnavailableException("Could not allocate an encoder context.");

                context->width = _width;
                context->height = _height;
                context->pix_fmt = pixelFormat;
                context->time_base = TimeBase;
                context->framerate = new AVRational { num = Math.Max((int)Math.Round(_frameRate), 1), den = 1 };
                if ((_output->oformat->flags & ffmpeg.AVFMT_GLOBALHEADER) != 0)
                    context->flags |= ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER;

                AVDictionary* options = null;
                if (_clip == ClipFormat.WebM)
                {
                    context->bit_rate = WebMBitRate;
                    context->gop_size = Math.Max((int)Math.Round(_frameRate * 2), 1);
                    context->max_b_frames = 0;

                    // A clip is made while somebody waits for it, so libvpx is asked for its
                    // fastest mode: a second or two of the phone's screen, not a master copy.
                    ffmpeg.av_dict_set(&options, "deadline", "realtime", 0);
                    ffmpeg.av_dict_set(&options, "cpu-used", "8", 0);
                    ffmpeg.av_dict_set(&options, "lag-in-frames", "0", 0);
                    ffmpeg.av_dict_set(&options, "auto-alt-ref", "0", 0);
                }

                var result = ffmpeg.avcodec_open2(context, codec, &options);
                ffmpeg.av_dict_free(&options);

                _encoder = context;
                if (result >= 0)
                {
                    _log.Info($"clip encoder: {ffmpeg.avcodec_get_name(codecId)} at {_width}x{_height}, " +
                              $"{pixelFormat}, {_frameRate:0.#} fps");
                    return;
                }

                failure = result;
                var value = _encoder;
                ffmpeg.avcodec_free_context(&value);
                _encoder = null;
            }

            throw new FFmpegUnavailableException(
                $"Could not open a {Describe(_clip)} encoder: {FFmpegRuntime.DescribeError(failure)}");
        }

        /// <summary>What to try, best first: the codec and pixel format each attempt opens with.</summary>
        private List<(AVCodecID Codec, AVPixelFormat PixelFormat)> EncoderAttempts()
        {
            var attempts = new List<(AVCodecID, AVPixelFormat)>();

            if (_clip == ClipFormat.Gif)
            {
                // The GIF encoder quantises packed pixels itself; PAL8 would need a palette
                // this exporter has no way to choose.
                attempts.Add((AVCodecID.AV_CODEC_ID_GIF, AVPixelFormat.AV_PIX_FMT_RGB8));
                attempts.Add((AVCodecID.AV_CODEC_ID_GIF, AVPixelFormat.AV_PIX_FMT_BGR8));
                attempts.Add((AVCodecID.AV_CODEC_ID_GIF, AVPixelFormat.AV_PIX_FMT_GRAY8));
                return attempts;
            }

            if (ffmpeg.avcodec_find_encoder(AVCodecID.AV_CODEC_ID_VP9) is not null)
                attempts.Add((AVCodecID.AV_CODEC_ID_VP9, AVPixelFormat.AV_PIX_FMT_YUV420P));
            attempts.Add((AVCodecID.AV_CODEC_ID_VP8, AVPixelFormat.AV_PIX_FMT_YUV420P));
            return attempts;
        }

        /// <summary>
        /// Converts one decoded picture into the encoder's frame and encodes it. The clip's
        /// own clock counts the pictures, not the phone's timestamps.
        /// </summary>
        /// <returns>False if encoding has failed and the clip should be abandoned.</returns>
        public bool Write(AVFrame* source, long index)
        {
            if (_faulted || _encoder is null) return false;

            EnsureScaler(source);
            if (_scaler is null) return false;

            if (ffmpeg.av_frame_make_writable(_frame) < 0)
            {
                _log.Error("the clip's picture is not writable");
                return false;
            }

            for (uint plane = 0; plane < 8; plane++)
            {
                _sourcePlanes[plane] = source->data[plane];
                _sourceStrides[plane] = source->linesize[plane];
            }

            for (uint plane = 0; plane < 4; plane++)
            {
                _destinationPlanes[plane] = _frame->data[plane];
                _destinationStrides[plane] = _frame->linesize[plane];
            }

            var scaled = ffmpeg.sws_scale(_scaler, _sourcePlanes, _sourceStrides, 0, source->height,
                _destinationPlanes, _destinationStrides);
            if (scaled <= 0)
            {
                _log.Error("a picture could not be scaled for the clip");
                return false;
            }

            _frame->pts = (long)Math.Round(index * TimeBase.den / _frameRate);
            _frame->duration = Math.Max((long)Math.Round(TimeBase.den / _frameRate), 1);

            if (!SendAndDrain(_frame)) return false;
            FrameCount++;
            return true;
        }

        /// <summary>Flushes the encoder and writes the container's trailer, so the clip has an
        /// index and plays. Call once, at the end.</summary>
        public void Finish()
        {
            if (_encoder is null || _faulted) return;
            SendAndDrain(null);

            if (_headerWritten)
            {
                var result = ffmpeg.av_write_trailer(_output);
                if (result < 0) _log.Warn($"could not finalise the clip: {FFmpegRuntime.DescribeError(result)}");
                _headerWritten = false;
            }
        }

        /// <summary>
        /// Hands one picture to the encoder and muxes everything it produces. A null frame
        /// finishes the stream. Caller must not rely on <see cref="_frame"/> afterwards.
        /// </summary>
        private bool SendAndDrain(AVFrame* frame)
        {
            var sent = ffmpeg.avcodec_send_frame(_encoder, frame);
            if (sent < 0 && sent != ffmpeg.AVERROR_EOF) return Fail("the encoder rejected a picture", sent);

            while (true)
            {
                var received = ffmpeg.avcodec_receive_packet(_encoder, _packet);
                if (received == ffmpeg.AVERROR(ffmpeg.EAGAIN) || received == ffmpeg.AVERROR_EOF) return true;
                if (received < 0) return Fail("the encoder failed", received);

                try
                {
                    if (!Mux()) return false;
                }
                finally
                {
                    ffmpeg.av_packet_unref(_packet);
                }
            }
        }

        private bool Mux()
        {
            _packet->stream_index = _stream->index;
            ffmpeg.av_packet_rescale_ts(_packet, _encoder->time_base, _stream->time_base);

            var result = ffmpeg.av_interleaved_write_frame(_output, _packet);
            if (result >= 0) return true;

            _faulted = true;
            _log.Error($"the clip could not be written: {FFmpegRuntime.DescribeError(result)}");
            return false;
        }

        /// <summary>
        /// (Re)builds the converter. A phone that rotated part way through the recording
        /// changes the picture's size mid-file; the clip keeps the size it opened with and
        /// scales whatever arrives to it, because an encoder cannot change size mid-stream.
        /// </summary>
        private void EnsureScaler(AVFrame* source)
        {
            var width = source->width;
            var height = source->height;
            var (format, formatIsFullRange) = ColourSpace.Normalise((AVPixelFormat)source->format);
            var fullRange = ColourSpace.IsFullRange(source, formatIsFullRange);
            var colorspace = ColourSpace.Of(source);

            if (_scaler is not null && width == _scalerWidth && height == _scalerHeight
                && format == _scalerFormat && fullRange == _scalerFullRange && colorspace == _scalerColorspace)
            {
                return;
            }

            if (_scaler is not null)
            {
                ffmpeg.sws_freeContext(_scaler);
                _scaler = null;
            }

            var flags = format == _encoder->pix_fmt && width == _width && height == _height
                // A pure colour conversion: the cheapest kernel is also the exact one.
                ? ColourSpace.SwsPoint
                : SwsBicubic;

            _scaler = ffmpeg.sws_getContext(width, height, format, _width, _height, _encoder->pix_fmt,
                flags, null, null, null);

            if (_scaler is null)
            {
                _log.Error($"could not build a {format} to {_encoder->pix_fmt} converter for {width}x{height}");
                return;
            }

            if (ColourSpace.Apply(_scaler, fullRange, colorspace) < 0)
                _log.Debug("this pixel format does not accept colour-space overrides; using swscale defaults");

            _scalerWidth = width;
            _scalerHeight = height;
            _scalerFormat = format;
            _scalerFullRange = fullRange;
            _scalerColorspace = colorspace;
        }

        private bool Fail(string what, int code)
        {
            _faulted = true;
            _log.Error($"{what}: {FFmpegRuntime.DescribeError(code)}");
            return false;
        }

        /// <summary>
        /// Lets go of everything. A clip that was never finished keeps no trailer and will
        /// not open; the caller is expected to delete it, as <see cref="Export"/> does.
        /// </summary>
        public void Dispose()
        {
            if (_scaler is not null)
            {
                ffmpeg.sws_freeContext(_scaler);
                _scaler = null;
            }

            if (_packet is not null)
            {
                var value = _packet;
                ffmpeg.av_packet_free(&value);
                _packet = null;
            }

            if (_frame is not null)
            {
                var value = _frame;
                ffmpeg.av_frame_free(&value);
                _frame = null;
            }

            if (_encoder is not null)
            {
                var value = _encoder;
                ffmpeg.avcodec_free_context(&value);
                _encoder = null;
            }

            if (_output is not null)
            {
                if (_output->pb is not null) ffmpeg.avio_closep(&_output->pb);
                var value = _output;
                ffmpeg.avformat_free_context(value);
                _output = null;
                _stream = null;
            }
        }
    }
}
