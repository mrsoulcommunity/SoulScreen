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
/// socket, so samples are copied into a queue and decoded on a dedicated thread.
/// </para>
/// <para>
/// Nothing is dropped here that the decoder could have used. H.264 frames are not
/// independent: every inter frame is described as a difference from the one before it, so
/// removing one from the middle of the queue does not cost a single picture - it corrupts
/// every picture after it, until the phone sends another keyframe. In mirroring that can be
/// several seconds, which is exactly the drifting, blocky picture that made this queue look
/// like the cheap place to save time. It is the most expensive.
/// </para>
/// <para>
/// So the queue is deep enough to ride out anything short of the machine genuinely being
/// unable to decode in real time - two seconds of frames, which is only a few megabytes
/// because these are still compressed. Overrunning even that means the backlog is useless
/// anyway, and the queue is cut cleanly back to the next keyframe rather than left with
/// holes in it.
/// </para>
/// <para>
/// Being behind is a separate problem from being corrupt, and it is solved downstream: the
/// renderer decides which decoded pictures to show. Frames are dropped there, where dropping
/// one costs exactly one picture.
/// </para>
/// </summary>
public sealed class VideoPipeline : IAsyncDisposable
{
    /// <summary>
    /// Frames buffered between the network and the decoder: two seconds at sixty a second.
    /// These are still compressed, a few tens of kilobytes each, so the whole queue is a
    /// handful of megabytes - far cheaper than the corruption that a shallower one causes.
    /// </summary>
    private const int QueueDepth = 120;

    private readonly ILogger _log = Log.For("pipeline");
    private readonly Queue<QueuedSample> _queue = new(QueueDepth);
    private readonly object _queueLock = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Thread _decodeThread;
    private readonly Stopwatch _rateClock = Stopwatch.StartNew();

    private IMirrorSource? _source;
    private H264Decoder? _decoder;

    /// <summary>
    /// Guards the recorder and everything feeding it. Video reaches it from the decode
    /// thread and audio from the receive thread, and stopping swaps it out from the UI
    /// thread; the lock is what keeps a write from landing on a recorder being closed.
    /// </summary>
    private readonly object _recordLock = new();
    private SessionRecorder? _recorder;
    private string? _pendingRecordingPath;
    private Task _recordingFinalisation = Task.CompletedTask;

    /// <summary>
    /// The most recent keyframe access unit - parameter sets plus an IDR picture, exactly
    /// as fed to the decoder - kept on hand so a new recording can open immediately instead
    /// of waiting for the next one. It is self-contained by construction, so it is exactly
    /// as good as the next keyframe for opening a file, and on a poor connection the next
    /// one can be a long time coming or never arrive before the session ends. Cleared the
    /// moment the codec configuration actually changes, since it would then describe a
    /// picture the track header no longer matches.
    /// </summary>
    private byte[]? _lastKeyFrame;
    private long _lastKeyFrameTimestampUs;

    /// <summary>Decodes the phone's audio for the recording only. The live audio pipeline
    /// has a decoder of its own; sharing one would tie recording to whether sound is on.</summary>
    private AudioDecoder? _recordAudioDecoder;
    private AudioFormat _audioFormat = AudioFormat.None;

    /// <summary>The rate and channel count of the open recording's audio track, so what the
    /// decoder produces is compared against the track actually declared rather than against
    /// a hard-coded assumption about the sender.</summary>
    private RecordingAudioTrack _recordingAudioTrack = RecordingAudioTrack.AirPlay;
    private long _receivedBytes;

    /// <summary>SPS and PPS in Annex-B, prepended to each keyframe so the decoder can start
    /// from any of them.</summary>
    private byte[] _parameterSets = [];

    /// <summary>
    /// True while the decoder has no usable reference at all, so inter frames would decode
    /// to noise. Set at the start and on a format change - deliberately not after a drop.
    /// </summary>
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

        _decodeThread = new Thread(() => DecodeLoop(_cts.Token))
        {
            Name = "SoulScreen decode",
            IsBackground = true,
            // Above normal, not highest: decoding must beat ordinary background work but
            // never starve the UI thread that has to present what it produces.
            Priority = ThreadPriority.AboveNormal,
        };
        _decodeThread.Start();
    }

    /// <summary>Codec configuration currently in force, once the sender has announced one.</summary>
    public VideoFormat? Format { get; private set; }

    /// <summary>Pictures decoded since the pipeline started.</summary>
    public long DecodedFrameCount => Interlocked.Read(ref _decodedFrames);

    /// <summary>Encoded video bytes received since the pipeline started, for the bit rate.</summary>
    public long ReceivedByteCount => Interlocked.Read(ref _receivedBytes);

    /// <summary>
    /// Put the phone's audio into recordings as an AAC track. Read when a recording starts;
    /// changing it mid-recording affects the next one.
    /// </summary>
    public bool RecordAudio { get; set; } = true;

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

    /// <summary>True while the open recording carries an audio track.</summary>
    public bool RecordingHasAudio => _recorder?.HasAudio == true;

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
        SessionRecorder? recorder = null;

        lock (_recordLock)
        {
            if (IsRecording) return;

            // A keyframe already on hand is exactly as good as the next one - it is
            // self-contained by construction - so there is no reason to wait when one is
            // available.
            if (_lastKeyFrame is { } cached && Format is { } format && TryOpenRecorderLocked(path, format))
            {
                recorder = _recorder;
                try
                {
                    // Written here, still under the lock, and not after it. The decode
                    // thread hands frames over through the same lock, so a later frame can
                    // only be written once this call returns; writing the keyframe outside
                    // the lock instead let a frame that was already on the decode thread slip
                    // in first, and the file then opened - and stayed - on an inter frame,
                    // which is exactly what keeping a keyframe on hand is meant to avoid.
                    recorder!.Write(cached, _lastKeyFrameTimestampUs, isKeyFrame: true);
                }
                catch (Exception ex)
                {
                    _log.Error("recording failed", ex);
                    recorder = null;
                }
            }
            else
            {
                _pendingRecordingPath = path;
            }
        }

        if (recorder is null)
        {
            // Either no keyframe is on hand yet (the recorder opens on the next one), or the
            // file opened but its first sample could not be written - a recording that has
            // written nothing is closed again rather than left looking live.
            if (_pendingRecordingPath is null) StopRecording();
            else _log.Info($"recording will start on the next keyframe: {path}");
            return;
        }

        _log.Info($"recording started immediately from a keyframe already on hand: {path}");
    }

    /// <summary>
    /// Ends the recording. Returns at once; the file is finalised in the background - the
    /// audio track's tail has to be encoded and the index written - and
    /// <see cref="RecordingFinished"/> is raised when it is playable.
    /// </summary>
    public void StopRecording()
    {
        SessionRecorder? recorder;
        AudioDecoder? audioDecoder;
        lock (_recordLock)
        {
            recorder = _recorder;
            _recorder = null;
            _pendingRecordingPath = null;
            audioDecoder = _recordAudioDecoder;
            _recordAudioDecoder = null;
        }

        audioDecoder?.Dispose();
        if (recorder is null) return;

        var path = recorder.Path;
        var finalisation = Task.Run(() =>
        {
            try { recorder.Dispose(); }
            catch (Exception ex) { _log.Error($"could not finalise {path}", ex); }
            RecordingFinished?.Invoke(this, path);
        });

        lock (_recordLock) _recordingFinalisation = finalisation;
    }

    /// <summary>Raised when the picture geometry is known or changes.</summary>
    public event EventHandler<VideoFormat>? FormatChanged;

    /// <summary>
    /// Raised per decoded picture, on the decode thread.
    /// <para>
    /// The handler takes ownership of the frame and must dispose it when done, which lets a
    /// renderer queue the picture instead of copying it out before returning. At 1080p that
    /// copy was eight megabytes on the decode thread for every frame, sixty times a second,
    /// and it was enough on its own to put the decoder behind.
    /// </para>
    /// </summary>
    public event EventHandler<DecodedVideoFrame>? FrameDecoded;

    /// <summary>Subscribes to a source. Replaces any source attached before.</summary>
    public void Attach(IMirrorSource source)
    {
        Detach();
        _source = source;
        source.VideoFormatChanged += OnFormatChanged;
        source.VideoSampleReady += OnSampleReady;
        source.AudioFormatChanged += OnAudioFormatChanged;
        source.AudioSampleReady += OnAudioSampleReady;
        _log.Debug($"attached to {source.Id}");
    }

    public void Detach()
    {
        var source = Interlocked.Exchange(ref _source, null);
        if (source is null) return;
        source.VideoFormatChanged -= OnFormatChanged;
        source.VideoSampleReady -= OnSampleReady;
        source.AudioFormatChanged -= OnAudioFormatChanged;
        source.AudioSampleReady -= OnAudioSampleReady;
    }

    private void OnAudioFormatChanged(object? sender, AudioFormat format)
    {
        AudioDecoder? stale;
        lock (_recordLock)
        {
            _audioFormat = format;
            // A recording in progress keeps its track; only the decoder feeding it has to
            // follow the new stream, and it is rebuilt on the next packet.
            stale = _recordAudioDecoder;
            _recordAudioDecoder = null;
        }
        stale?.Dispose();
    }

    /// <summary>
    /// Runs on the audio receive thread. Cheap when nothing is being recorded - one lock and
    /// a null check - which is the state it is in almost all the time.
    /// </summary>
    private void OnAudioSampleReady(object? sender, MediaSample sample)
    {
        lock (_recordLock)
        {
            var recorder = _recorder;
            if (recorder is null || !recorder.HasAudio) return;

            try
            {
                if (_recordAudioDecoder is null)
                {
                    if (!_audioFormat.IsValid) return;
                    _recordAudioDecoder = new AudioDecoder(_audioFormat);
                    if (_recordAudioDecoder.OutputSampleRate != _recordingAudioTrack.SampleRate
                        || _recordAudioDecoder.OutputChannels != _recordingAudioTrack.Channels)
                    {
                        _log.Warn($"audio is {_audioFormat}, which the recording's track does not match; recording without it");
                        _recordAudioDecoder.Dispose();
                        _recordAudioDecoder = null;
                        return;
                    }
                }

                var pcm = _recordAudioDecoder.Decode(sample.Span);
                if (!pcm.IsEmpty) recorder.WriteAudio(pcm, sample.TimestampUs);
            }
            catch (Exception ex)
            {
                _log.Warn("an audio packet could not be recorded", ex);
            }
        }
    }

    private void OnFormatChanged(object? sender, VideoFormat format)
    {
        Format = format;

        // Held rather than queued as a packet of its own: a packet carrying only SPS and
        // PPS makes the decoder report "no frame" and fail, so the sets ride along with the
        // next keyframe instead.
        if (format.ParameterSets.Length > 0)
        {
            bool changed;
            lock (_queueLock)
            {
                // Only a configuration that actually differs invalidates what the decoder is
                // tracking. Resynchronising on a repeat would stall the picture until the
                // next keyframe for no reason at all.
                changed = !_parameterSets.AsSpan().SequenceEqual(format.ParameterSets);
                if (changed)
                {
                    _parameterSets = format.ParameterSets;
                    _needKeyFrame = true;
                }
            }

            if (changed)
            {
                // The cached keyframe belongs to the configuration that just changed: a
                // decoder opening the file on it would see the new dimensions in the track
                // header but old-configuration bytes in the sample.
                lock (_recordLock) _lastKeyFrame = null;
            }
        }

        FormatChanged?.Invoke(this, format);
    }

    private void OnSampleReady(object? sender, MediaSample sample)
    {
        Interlocked.Add(ref _receivedBytes, sample.Length);

        // The sample's buffer is recycled the moment this returns, so copy before queueing.
        var queued = QueuedSample.Copy(sample.Span, sample.TimestampUs, sample.IsKeyFrame);

        List<QueuedSample>? abandoned = null;
        lock (_queueLock)
        {
            if (_completed)
            {
                queued.Dispose();
                return;
            }

            if (_queue.Count >= QueueDepth) abandoned = AbandonBacklogLocked();

            _queue.Enqueue(queued);
            Monitor.Pulse(_queueLock);
        }

        if (abandoned is null) return;
        foreach (var discarded in abandoned) discarded.Dispose();

        // Outside the lock: the decode thread takes it for every frame, and writing a log
        // line while holding it would stall the decode this is already struggling to keep up
        // with.
        _log.Warn($"the decoder fell {abandoned.Count} frames behind; resynchronising on the next keyframe");
    }

    /// <summary>
    /// Empties a queue that has overrun, and arranges for decoding to resume on the next
    /// keyframe. Caller must hold <see cref="_queueLock"/>.
    /// <para>
    /// Two seconds behind means the machine cannot decode this stream in real time, and no
    /// choice made here will produce a good picture. It can still produce an honest one.
    /// Thinning the queue by pulling out individual inter frames keeps the count down while
    /// handing the decoder a stream with holes in it, and a hole is not one lost picture but
    /// every picture until the next keyframe, drifting further from the phone as it goes.
    /// Cutting cleanly costs a visible pause and then recovers exactly.
    /// </para>
    /// </summary>
    private List<QueuedSample> AbandonBacklogLocked()
    {
        var abandoned = new List<QueuedSample>(_queue.Count);
        while (_queue.Count > 0) abandoned.Add(_queue.Dequeue());

        Interlocked.Add(ref _droppedSamples, abandoned.Count);
        _needKeyFrame = true;
        return abandoned;
    }

    /// <summary>
    /// Runs on a dedicated thread rather than the thread pool. Decoding happens sixty times
    /// a second and must not queue behind unrelated pool work; a thread of its own, slightly
    /// above normal priority, is what keeps the cadence even under load.
    /// </summary>
    private void DecodeLoop(CancellationToken token)
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
            // Cancellation has to wake a thread that is blocked on the queue, not only be
            // observed by one that is running.
            using var wake = token.Register(() => { lock (_queueLock) Monitor.PulseAll(_queueLock); });

            while (!token.IsCancellationRequested)
            {
                QueuedSample? next;
                byte[] parameterSets;
                lock (_queueLock)
                {
                    while (_queue.Count == 0 && !token.IsCancellationRequested)
                        Monitor.Wait(_queueLock);
                    if (token.IsCancellationRequested) break;

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
            Interlocked.Increment(ref _decodedFrames);

            var handler = FrameDecoded;
            if (handler is null)
            {
                frame.Dispose();
                continue;
            }

            try
            {
                // Ownership passes to the handler here; it disposes the frame once it has
                // finished with the pixels, which may be several composition passes later.
                handler(this, frame);
            }
            catch (Exception ex)
            {
                // A renderer that throws must not kill the decode loop. The frame is left
                // alone rather than disposed: the handler may already have queued it, and
                // recycling a buffer that is still referenced is the worse of the two.
                _log.Warn("frame handler threw", ex);
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
        SessionRecorder? recorder;
        lock (_recordLock)
        {
            if (isKeyFrame)
            {
                _lastKeyFrame = annexB.ToArray();
                _lastKeyFrameTimestampUs = timestampUs;
            }

            if (_recorder is null)
            {
                if (_pendingRecordingPath is null || !isKeyFrame) return;

                var path = _pendingRecordingPath;
                var format = Format;
                if (format is null) return;

                if (!TryOpenRecorderLocked(path, format.Value)) { _pendingRecordingPath = null; return; }
                _pendingRecordingPath = null;
            }

            recorder = _recorder;
        }

        if (recorder is null) return;

        try
        {
            recorder.Write(annexB, timestampUs, isKeyFrame);
        }
        catch (Exception ex)
        {
            _log.Error("recording failed", ex);
            StopRecording();
        }
    }

    /// <summary>
    /// Creates the recorder for <paramref name="format"/>, honouring <see cref="RecordAudio"/>,
    /// and assigns it to <see cref="_recorder"/> on success. Caller must hold
    /// <see cref="_recordLock"/>. The track is declared up front because MP4 cannot grow one
    /// later, and iOS often sets its audio stream up a moment after the picture starts.
    /// </summary>
    private bool TryOpenRecorderLocked(string path, VideoFormat format)
    {
        try
        {
            var audio = RecordAudio ? TrackForAudioFormat() : (RecordingAudioTrack?)null;
            if (audio is { } declared) _recordingAudioTrack = declared;
            _recorder = SessionRecorder.Create(path, format, audio);
            return true;
        }
        catch (Exception ex)
        {
            _log.Error($"could not start recording to {path}", ex);
            return false;
        }
    }

    /// <summary>The track the open recording should declare: the stream's own rate and
    /// channel count when they are already known - scrcpy hands over 48 kHz stereo PCM where
    /// AirPlay sends 44.1 kHz AAC-ELD - falling back to AirPlay's shape when nothing has
    /// announced an audio format yet. Declaring the wrong rate is a recording whose audio is
    /// silently dropped the moment the first packet arrives, because the decoded output can
    /// never match the track.</summary>
    private RecordingAudioTrack TrackForAudioFormat()
    {
        var format = _audioFormat;
        return format.IsValid
            ? new RecordingAudioTrack(format.SampleRate, format.Channels)
            : RecordingAudioTrack.AirPlay;
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

        Task finalisation;
        lock (_recordLock) finalisation = _recordingFinalisation;
        // A recording still being finalised must finish before the process can go: the
        // trailer is what makes the file playable.
        try { await finalisation.WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false); }
        catch (TimeoutException) { _log.Warn("a recording did not finalise in time"); }

        lock (_queueLock)
        {
            _completed = true;
            while (_queue.Count > 0) _queue.Dequeue().Dispose();
        }

        await _cts.CancelAsync().ConfigureAwait(false);

        // Cancellation pulses the queue, which is what the decode thread is waiting on; give
        // it a bounded moment to unwind rather than blocking shutdown on it forever.
        if (!_decodeThread.Join(TimeSpan.FromSeconds(2)))
            _log.Warn("the decode thread did not stop in time");

        _cts.Dispose();
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
