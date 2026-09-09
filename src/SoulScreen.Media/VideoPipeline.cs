using System.Buffers;
using System.Diagnostics;
using SoulScreen.Core.Logging;
using SoulScreen.Core.Media;
using SoulScreen.Core.Sources;

namespace SoulScreen.Media;

/// <summary>
/// Connects a mirror source to the H.264 decoder and hands finished pictures to a renderer.
/// <para>
/// The source raises samples on its receive thread and decoding there would stall the
/// socket, so samples are copied into a short queue and decoded on a dedicated thread.
/// </para>
/// <para>
/// What the queue drops matters more than how deep it is. H.264 frames are not independent:
/// discard one keyframe and every frame after it decodes to nothing until the phone happens
/// to send another, which in mirroring can be seconds - long enough to look like a black
/// screen rather than a dropped frame. So the queue drops the oldest <em>inter</em> frame,
/// keeps keyframes, and after any drop skips ahead to the next keyframe instead of feeding
/// the decoder frames whose references are gone.
/// </para>
/// </summary>
public sealed class VideoPipeline : IAsyncDisposable
{
    /// <summary>
    /// Frames buffered between the network and the decoder. Deep enough to absorb decoder
    /// start-up and a scheduling hiccup, shallow enough that the picture cannot drift far
    /// behind the phone.
    /// </summary>
    private const int QueueDepth = 8;

    private readonly ILogger _log = Log.For("pipeline");
    private readonly Queue<QueuedSample> _queue = new(QueueDepth);
    private readonly object _queueLock = new();
    private readonly SemaphoreSlim _queued = new(0);
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _decodeLoop;
    private readonly Stopwatch _rateClock = Stopwatch.StartNew();

    private IMirrorSource? _source;
    private H264Decoder? _decoder;
    private SessionRecorder? _recorder;
    private string? _pendingRecordingPath;

    /// <summary>SPS and PPS in Annex-B, prepended to each keyframe so the decoder can start
    /// from any of them.</summary>
    private byte[] _parameterSets = [];

    /// <summary>Set after a drop: inter frames are skipped until the next keyframe.</summary>
    private bool _needKeyFrame = true;

    private bool _completed;
    private long _droppedSamples;
    private long _skippedSamples;
    private long _decodedFrames;
    private long _framesAtLastSample;
    private double _framesPerSecond;

    public VideoPipeline()
    {
        FFmpegRuntime.ThrowIfUnavailable();
        _decodeLoop = Task.Run(() => DecodeLoopAsync(_cts.Token), CancellationToken.None);
    }

    /// <summary>Codec configuration currently in force, once the sender has announced one.</summary>
    public VideoFormat? Format { get; private set; }

    /// <summary>Pictures decoded since the pipeline started.</summary>
    public long DecodedFrameCount => Interlocked.Read(ref _decodedFrames);

    /// <summary>Samples discarded because the decoder could not keep up.</summary>
    public long DroppedSampleCount => Interlocked.Read(ref _droppedSamples);

    /// <summary>Inter frames skipped while waiting to resynchronise on a keyframe.</summary>
    public long SkippedSampleCount => Interlocked.Read(ref _skippedSamples);

    /// <summary>Rolling decode rate, updated about once a second.</summary>
    public double FramesPerSecond => _framesPerSecond;

    /// <summary>True while a recording is open, or waiting for the keyframe that starts one.</summary>
    public bool IsRecording => _recorder is not null || _pendingRecordingPath is not null;

    /// <summary>File being recorded, once recording has actually started.</summary>
    public string? RecordingPath => _recorder?.Path;

    public long RecordedFrameCount => _recorder?.FrameCount ?? 0;

    public TimeSpan RecordingDuration => _recorder?.Duration ?? TimeSpan.Zero;

    public long RecordingSizeBytes => _recorder?.FileSizeBytes ?? 0;

    /// <summary>Raised when a recording finishes, with the file it produced.</summary>
    public event EventHandler<string>? RecordingFinished;

    /// <summary>
    /// Begins recording to <paramref name="path"/>. The file is opened lazily on the first
    /// keyframe: the codec configuration has to be known before an MP4 header can be
    /// written, and starting mid-GOP would produce a file that opens on corruption.
    /// </summary>
    public void StartRecording(string path)
    {
        lock (_queueLock)
        {
            if (IsRecording) return;
            _pendingRecordingPath = path;
            // Recording from the next keyframe rather than the next frame.
            _log.Info($"recording will start on the next keyframe: {path}");
        }
    }

    public void StopRecording()
    {
        SessionRecorder? recorder;
        lock (_queueLock)
        {
            recorder = _recorder;
            _recorder = null;
            _pendingRecordingPath = null;
        }

        if (recorder is null) return;
        var path = recorder.Path;
        recorder.Dispose();
        RecordingFinished?.Invoke(this, path);
    }

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

        // Held rather than queued as a packet of its own: a packet carrying only SPS and
        // PPS makes the decoder report "no frame" and fail, so the sets ride along with the
        // next keyframe instead.
        if (format.ParameterSets.Length > 0)
        {
            lock (_queueLock)
            {
                _parameterSets = format.ParameterSets;
                // New geometry invalidates whatever the decoder was tracking.
                _needKeyFrame = true;
            }
        }

        FormatChanged?.Invoke(this, format);
    }

    private void OnSampleReady(object? sender, MediaSample sample)
    {
        // The sample's buffer is recycled the moment this returns, so copy before queueing.
        var queued = QueuedSample.Copy(sample.Span, sample.TimestampUs, sample.IsKeyFrame);

        QueuedSample? evicted = null;
        lock (_queueLock)
        {
            if (_completed)
            {
                queued.Dispose();
                return;
            }

            if (_queue.Count >= QueueDepth)
            {
                evicted = Evict();
                // Everything still queued now has a hole in front of it, so resynchronise.
                _needKeyFrame = true;
                Interlocked.Increment(ref _droppedSamples);
            }

            _queue.Enqueue(queued);
        }

        evicted?.Dispose();
        _queued.Release();
    }

    /// <summary>
    /// Removes one sample to make room, preferring the oldest inter frame. Only if the queue
    /// is nothing but keyframes does the oldest keyframe go, which cannot make things worse.
    /// </summary>
    private QueuedSample? Evict()
    {
        var retained = new List<QueuedSample>(_queue.Count);
        QueuedSample? evicted = null;

        while (_queue.Count > 0)
        {
            var candidate = _queue.Dequeue();
            if (evicted is null && !candidate.IsKeyFrame) evicted = candidate;
            else retained.Add(candidate);
        }

        if (evicted is null && retained.Count > 0)
        {
            evicted = retained[0];
            retained.RemoveAt(0);
        }

        foreach (var sample in retained) _queue.Enqueue(sample);
        return evicted;
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
            while (!token.IsCancellationRequested)
            {
                await _queued.WaitAsync(token).ConfigureAwait(false);

                QueuedSample? next;
                byte[] parameterSets;
                lock (_queueLock)
                {
                    if (_queue.Count == 0) continue;
                    next = _queue.Dequeue();
                    parameterSets = _parameterSets;

                    if (_needKeyFrame)
                    {
                        if (!next.IsKeyFrame)
                        {
                            // Its references are gone; decoding it would only produce noise.
                            Interlocked.Increment(ref _skippedSamples);
                            var skipped = next;
                            next = null;
                            skipped.Dispose();
                        }
                        else
                        {
                            _needKeyFrame = false;
                        }
                    }
                }

                if (next is null) continue;
                using (next) DecodeOne(next, parameterSets);
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

    private void DecodeOne(QueuedSample queued, byte[] parameterSets)
    {
        var decoder = _decoder;
        if (decoder is null) return;

        IReadOnlyList<DecodedVideoFrame> frames;
        try
        {
            if (queued.IsKeyFrame && parameterSets.Length > 0)
            {
                // Prefix the keyframe with SPS and PPS so a decoder that has just started,
                // or just been resynchronised, has everything it needs in one packet. The
                // recorder wants the same self-contained access unit.
                var combined = ArrayPool<byte>.Shared.Rent(parameterSets.Length + queued.Length);
                try
                {
                    parameterSets.CopyTo(combined, 0);
                    queued.Span.CopyTo(combined.AsSpan(parameterSets.Length));
                    var access = combined.AsSpan(0, parameterSets.Length + queued.Length);
                    Record(access, queued.TimestampUs, isKeyFrame: true);
                    frames = decoder.Decode(access, queued.TimestampUs);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(combined);
                }
            }
            else
            {
                Record(queued.Span, queued.TimestampUs, isKeyFrame: false);
                frames = decoder.Decode(queued.Span, queued.TimestampUs);
            }
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

    /// <summary>
    /// Hands one access unit to the recorder, opening the file when the first keyframe
    /// arrives. Recording failures never stop the live view.
    /// </summary>
    private void Record(ReadOnlySpan<byte> annexB, long timestampUs, bool isKeyFrame)
    {
        if (_recorder is null)
        {
            if (_pendingRecordingPath is null || !isKeyFrame) return;

            var path = _pendingRecordingPath;
            var format = Format;
            if (format is null) return;

            try
            {
                _recorder = SessionRecorder.Create(path, format.Value);
                _pendingRecordingPath = null;
            }
            catch (Exception ex)
            {
                _pendingRecordingPath = null;
                _log.Error($"could not start recording to {path}", ex);
                return;
            }
        }

        try
        {
            _recorder.Write(annexB, timestampUs, isKeyFrame);
        }
        catch (Exception ex)
        {
            _log.Error("recording failed", ex);
            StopRecording();
        }
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
        StopRecording();

        lock (_queueLock)
        {
            _completed = true;
            while (_queue.Count > 0) _queue.Dequeue().Dispose();
        }

        await _cts.CancelAsync().ConfigureAwait(false);

        try { await _decodeLoop.ConfigureAwait(false); }
        catch (OperationCanceledException) { }

        _cts.Dispose();
        _queued.Dispose();
        _log.Info($"pipeline closed: {DecodedFrameCount} decoded, {DroppedSampleCount} dropped, {SkippedSampleCount} skipped");
    }

    /// <summary>A copy of one access unit, owned by the queue until the decoder consumes it.</summary>
    private sealed class QueuedSample : IDisposable
    {
        private byte[]? _buffer;

        private QueuedSample(byte[] buffer, int length, long timestampUs, bool isKeyFrame)
        {
            _buffer = buffer;
            Length = length;
            TimestampUs = timestampUs;
            IsKeyFrame = isKeyFrame;
        }

        public int Length { get; }
        public long TimestampUs { get; }
        public bool IsKeyFrame { get; }

        public ReadOnlySpan<byte> Span => _buffer is null
            ? throw new ObjectDisposedException(nameof(QueuedSample))
            : _buffer.AsSpan(0, Length);

        public static QueuedSample Copy(ReadOnlySpan<byte> payload, long timestampUs, bool isKeyFrame)
        {
            var buffer = ArrayPool<byte>.Shared.Rent(Math.Max(payload.Length, 1));
            payload.CopyTo(buffer);
            return new QueuedSample(buffer, payload.Length, timestampUs, isKeyFrame);
        }

        public void Dispose()
        {
            var buffer = Interlocked.Exchange(ref _buffer, null);
            if (buffer is not null) ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
