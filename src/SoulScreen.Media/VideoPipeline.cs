using System.Buffers;
using System.Diagnostics;
using System.Threading.Channels;
using SoulScreen.Core.Logging;
using SoulScreen.Core.Media;
using SoulScreen.Core.Sources;

namespace SoulScreen.Media;

/// <summary>
/// Connects a mirror source to the H.264 decoder and hands finished pictures to a renderer.
/// <para>
/// The source raises samples on its receive thread, and decoding there would stall the
/// socket. So samples are copied into a short bounded queue and decoded on a dedicated
/// thread. The queue is deliberately tiny and drops the oldest entry when full: for a
/// mirror, a frame that is already late is worth less than the one behind it, and letting
/// the queue grow would trade latency for frames nobody wants to see.
/// </para>
/// </summary>
public sealed class VideoPipeline : IAsyncDisposable
{
    /// <summary>
    /// Frames buffered between the network and the decoder. Three is enough to absorb a
    /// scheduling hiccup without letting the picture drift behind the phone.
    /// </summary>
    private const int QueueDepth = 3;

    private readonly ILogger _log = Log.For("pipeline");
    private readonly Channel<QueuedSample> _queue;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _decodeLoop;
    private readonly Stopwatch _rateClock = Stopwatch.StartNew();

    private IMirrorSource? _source;
    private H264Decoder? _decoder;
    private long _droppedSamples;
    private long _decodedFrames;
    private long _framesAtLastSample;
    private double _framesPerSecond;

    public VideoPipeline()
    {
        FFmpegRuntime.ThrowIfUnavailable();

        _queue = Channel.CreateBounded<QueuedSample>(
            new BoundedChannelOptions(QueueDepth)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
            },
            // Without this the pooled buffer behind a dropped sample would never come back.
            dropped =>
            {
                Interlocked.Increment(ref _droppedSamples);
                dropped.Dispose();
            });

        _decodeLoop = Task.Run(() => DecodeLoopAsync(_cts.Token), CancellationToken.None);
    }

    /// <summary>Codec configuration currently in force, once the sender has announced one.</summary>
    public VideoFormat? Format { get; private set; }

    /// <summary>Pictures decoded since the pipeline started.</summary>
    public long DecodedFrameCount => Interlocked.Read(ref _decodedFrames);

    /// <summary>Samples dropped because the decoder could not keep up.</summary>
    public long DroppedSampleCount => Interlocked.Read(ref _droppedSamples);

    /// <summary>Rolling decode rate, updated about once a second.</summary>
    public double FramesPerSecond => _framesPerSecond;

    /// <summary>Raised when the picture geometry is known or changes.</summary>
    public event EventHandler<VideoFormat>? FormatChanged;

    /// <summary>
    /// Raised per decoded picture, on the decode thread. The frame is recycled as soon as
    /// the handler returns, so a renderer must copy or upload the pixels inline.
    /// </summary>
    public event EventHandler<DecodedVideoFrame>? FrameDecoded;

    /// <summary>Subscribes to a source. Replaces any source attached before.</summary>
    public void Attach(IMirrorSource source)
    {
        Detach();
        _source = source;
        source.VideoFormatChanged += OnFormatChanged;
        source.VideoSampleReady += OnSampleReady;
        _log.Debug($"attached to {source.Id}");
    }

    public void Detach()
    {
        var source = Interlocked.Exchange(ref _source, null);
        if (source is null) return;
        source.VideoFormatChanged -= OnFormatChanged;
        source.VideoSampleReady -= OnSampleReady;
    }

    private void OnFormatChanged(object? sender, VideoFormat format)
    {
        Format = format;
        FormatChanged?.Invoke(this, format);

        // Queued as a sample of its own so the parameter sets reach the decoder ahead of
        // the frames they describe, in the order the sender sent them.
        if (format.ParameterSets.Length > 0)
            Enqueue(format.ParameterSets, 0, isConfiguration: true);
    }

    private void OnSampleReady(object? sender, MediaSample sample)
    {
        // The sample's buffer is recycled the moment this returns, so copy before queueing.
        Enqueue(sample.Span, sample.TimestampUs, isConfiguration: false);
    }

    private void Enqueue(ReadOnlySpan<byte> payload, long timestampUs, bool isConfiguration)
    {
        var queued = QueuedSample.Copy(payload, timestampUs, isConfiguration);
        if (_queue.Writer.TryWrite(queued)) return;

        // Only reachable once the channel is completed, i.e. during shutdown.
        queued.Dispose();
    }

    private async Task DecodeLoopAsync(CancellationToken token)
    {
        try
        {
            _decoder = new H264Decoder();
        }
        catch (Exception ex)
        {
            _log.Error("could not start the H.264 decoder", ex);
            return;
        }

        try
        {
            await foreach (var queued in _queue.Reader.ReadAllAsync(token).ConfigureAwait(false))
            {
                using (queued)
                {
                    DecodeOne(queued);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _log.Error("decode loop failed", ex);
        }
        finally
        {
            var decoder = _decoder;
            _decoder = null;
            if (decoder is not null)
            {
                foreach (var frame in decoder.Flush()) frame.Dispose();
                decoder.Dispose();
            }
        }
    }

    private void DecodeOne(QueuedSample queued)
    {
        var decoder = _decoder;
        if (decoder is null) return;

        IReadOnlyList<DecodedVideoFrame> frames;
        try
        {
            frames = decoder.Decode(queued.Span, queued.TimestampUs);
        }
        catch (Exception ex)
        {
            _log.Warn("a frame failed to decode", ex);
            return;
        }

        foreach (var frame in frames)
        {
            using (frame)
            {
                Interlocked.Increment(ref _decodedFrames);
                try
                {
                    FrameDecoded?.Invoke(this, frame);
                }
                catch (Exception ex)
                {
                    // A renderer that throws must not kill the decode loop.
                    _log.Warn("frame handler threw", ex);
                }
            }
        }

        UpdateFrameRate();
    }

    private void UpdateFrameRate()
    {
        var elapsed = _rateClock.Elapsed;
        if (elapsed.TotalSeconds < 1) return;

        var decoded = Interlocked.Read(ref _decodedFrames);
        _framesPerSecond = (decoded - _framesAtLastSample) / elapsed.TotalSeconds;
        _framesAtLastSample = decoded;
        _rateClock.Restart();
    }

    public async ValueTask DisposeAsync()
    {
        Detach();
        _queue.Writer.TryComplete();
        await _cts.CancelAsync().ConfigureAwait(false);

        try { await _decodeLoop.ConfigureAwait(false); }
        catch (OperationCanceledException) { }

        // Anything still queued when the reader stopped still owns a pooled buffer.
        while (_queue.Reader.TryRead(out var leftover)) leftover.Dispose();

        _cts.Dispose();
        _log.Info($"pipeline closed: {DecodedFrameCount} frames decoded, {DroppedSampleCount} dropped");
    }

    /// <summary>A copy of one access unit, owned by the queue until the decoder consumes it.</summary>
    private sealed class QueuedSample : IDisposable
    {
        private byte[]? _buffer;

        private QueuedSample(byte[] buffer, int length, long timestampUs, bool isConfiguration)
        {
            _buffer = buffer;
            Length = length;
            TimestampUs = timestampUs;
            IsConfiguration = isConfiguration;
        }

        public int Length { get; }
        public long TimestampUs { get; }

        /// <summary>True for an SPS/PPS record rather than a picture.</summary>
        public bool IsConfiguration { get; }

        public ReadOnlySpan<byte> Span => _buffer is null
            ? throw new ObjectDisposedException(nameof(QueuedSample))
            : _buffer.AsSpan(0, Length);

        public static QueuedSample Copy(ReadOnlySpan<byte> payload, long timestampUs, bool isConfiguration)
        {
            var buffer = ArrayPool<byte>.Shared.Rent(Math.Max(payload.Length, 1));
            payload.CopyTo(buffer);
            return new QueuedSample(buffer, payload.Length, timestampUs, isConfiguration);
        }

        public void Dispose()
        {
            var buffer = Interlocked.Exchange(ref _buffer, null);
            if (buffer is not null) ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
