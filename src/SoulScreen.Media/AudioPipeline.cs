using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using SoulScreen.Core.Logging;
using SoulScreen.Core.Media;
using SoulScreen.Core.Sources;

namespace SoulScreen.Media;

/// <summary>
/// Plays the audio that accompanies a mirrored screen.
/// <para>
/// Packets are decoded on the thread that receives them rather than handed to a decode
/// thread. AAC-ELD frames are 480 samples - about 11 ms - and decoding one costs a fraction
/// of that, so a queue would add latency and a thread hand-off without buying anything.
/// </para>
/// <para>
/// Everything else is about when each packet is heard, and that comes down to how much
/// decoded audio is waiting in front of the sound card: too little and a late packet leaves
/// the card playing silence, too much and the voice trails the lips. How much it should be is
/// <see cref="PlayoutController"/>'s decision. This class carries those decisions out on the
/// audio itself - priming silence, a frame spliced in or out, a run of packets skipped behind
/// a fade - and owns the decoder and the device.
/// </para>
/// </summary>
public sealed class AudioPipeline : IAsyncDisposable
{
    /// <summary>
    /// Capacity of the playback buffer. Far above anything the controller lets it hold, on
    /// purpose: an overflow discards silently, and every discard should be a decision made here.
    /// </summary>
    private static readonly TimeSpan Capacity = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Longest run of missing packets that is filled with silence rather than treated as the
    /// stream having paused. A real pause is better met by starting the timeline over than by
    /// queueing its whole length of silence ahead of the sound that follows it.
    /// </summary>
    private static readonly TimeSpan MaximumGapFill = TimeSpan.FromMilliseconds(120);

    /// <summary>
    /// Ramp laid over the audio either side of a jump - a skip, a filled gap, the card running
    /// dry. Three milliseconds turns the step in the waveform, which is heard as a click, into
    /// a dip, which mostly is not heard at all.
    /// </summary>
    private static readonly TimeSpan FadeDuration = TimeSpan.FromMilliseconds(3);

    /// <summary>
    /// The device's own buffer, which is delay on top of everything the controller holds.
    /// Twenty-five milliseconds is what shared-mode event sync reliably honours on Windows 11
    /// audio drivers; below that some drivers start to glitch, and a glitch costs far more than
    /// the few milliseconds saved.
    /// </summary>
    private const int DeviceLatencyMilliseconds = 25;

    private readonly ILogger _log = Log.For("audio-out");

    /// <summary>
    /// Guards the decoder, the buffer and the controller. Packets are handled under it, and so
    /// is replacing or releasing any of them: a stream being torn down can still be delivering
    /// its last packet while the device is reconfigured, and the decoder is native code that
    /// must never see two threads or be freed under one.
    /// </summary>
    private readonly object _lock = new();

    private IMirrorSource? _source;
    private AudioDecoder? _decoder;
    private BufferedWaveProvider? _buffer;
    private StarvationMeter? _meter;
    private WasapiOut? _output;
    private VolumeSampleProvider? _volume;
    private PlayoutController? _controller;
    private AudioFormat _format = AudioFormat.None;
    private float _requestedVolume = 1f;
    private bool _muted;
    private string? _outputDeviceId;
    private TimeSpan _reserve = PlayoutController.Reserve;

    /// <summary>Bumped each time the sender starts an audio stream; the packet path starts its
    /// timeline over when it sees a new value.</summary>
    private int _streamGeneration;
    private int _handledGeneration = -1;

    private long _playedPackets;
    private long _skippedPackets;
    private long _filledGaps;

    /// <summary>Controller statistics carried over from devices already closed, for the final log.</summary>
    private long _skips, _stretchedFrames, _shrunkFrames, _stalls, _starvedFrames;

    /// <summary>
    /// Reused across packets. NAudio takes an array rather than a span, and allocating one
    /// ninety times a second only to throw it away is pure garbage-collector pressure on the
    /// thread that also receives the audio.
    /// </summary>
    private byte[] _transfer = new byte[8192];

    /// <summary>Zeroed PCM, for priming and filling gaps. Never written to.</summary>
    private byte[] _silence = [];

    /// <summary>Where the next packet is expected on the sender's clock, for spotting gaps.</summary>
    private long _expectedTimestampUs;
    private bool _haveTimestamp;

    /// <summary>How long the last packet played for, which sets the scale of a real gap.</summary>
    private long _packetDurationUs;

    /// <summary>Set when silence was just queued for missing packets, so the next packet fades in.</summary>
    private bool _fadeInAfterGap;

    /// <summary>Format currently being played, if any.</summary>
    public AudioFormat Format => _format;

    public long PlayedPacketCount => Interlocked.Read(ref _playedPackets);

    /// <summary>Packets skipped to bring the sound back in step with the picture after a stall.</summary>
    public long DroppedPacketCount => Interlocked.Read(ref _skippedPackets);

    /// <summary>Gaps left by lost packets that were filled with silence.</summary>
    public long FilledGapCount => Interlocked.Read(ref _filledGaps);

    public bool IsPlaying => _output is not null;

    /// <summary>How much audio is waiting to be played.</summary>
    public TimeSpan BufferedDuration => _buffer?.BufferedDuration ?? TimeSpan.Zero;

    /// <summary>Playback volume from 0 to 1.</summary>
    public float Volume
    {
        get => _requestedVolume;
        set
        {
            _requestedVolume = Math.Clamp(value, 0f, 1f);
            ApplyVolume();
        }
    }

    public bool Muted
    {
        get => _muted;
        set
        {
            _muted = value;
            ApplyVolume();
        }
    }

    /// <summary>
    /// Endpoint id of the playback device, or null for the system default. Changing it while
    /// audio is playing reopens the device on the new endpoint; the stream's timeline starts
    /// over, which costs a fraction of a second of sound.
    /// </summary>
    public string? OutputDeviceId
    {
        get => _outputDeviceId;
        set
        {
            lock (_lock)
            {
                if (string.Equals(_outputDeviceId, value, StringComparison.Ordinal)) return;
                _outputDeviceId = value;
                Reopen();
            }
        }
    }

    /// <summary>
    /// The margin of decoded audio held ahead of the sound card. Set against the picture's
    /// pacing delay so the two stay level. Changing it mid-stream reopens the device.
    /// </summary>
    public TimeSpan Reserve
    {
        get => _reserve;
        set
        {
            var clamped = value < TimeSpan.FromMilliseconds(20) ? TimeSpan.FromMilliseconds(20)
                : value > TimeSpan.FromMilliseconds(240) ? TimeSpan.FromMilliseconds(240) : value;
            lock (_lock)
            {
                if (_reserve == clamped) return;
                _reserve = clamped;
                Reopen();
            }
        }
    }

    /// <summary>Reopens the device with the current settings, if one is open. Caller must
    /// hold <see cref="_lock"/>. The stream generation is bumped so the next packet primes a
    /// fresh timeline rather than continuing one measured against the old device.</summary>
    private void Reopen()
    {
        if (_output is null) return;
        var format = _format;
        StopCore();
        _streamGeneration++;
        Open(format);
    }

    /// <summary>Raised when playback starts or the format changes.</summary>
    public event EventHandler<AudioFormat>? FormatChanged;

    public void Attach(IMirrorSource source)
    {
        Detach();
        _source = source;
        source.AudioFormatChanged += OnFormatChanged;
        source.AudioSampleReady += OnSampleReady;
        _log.Debug($"attached to {source.Id}");
    }

    public void Detach()
    {
        var source = Interlocked.Exchange(ref _source, null);
        if (source is null) return;
        source.AudioFormatChanged -= OnFormatChanged;
        source.AudioSampleReady -= OnSampleReady;
        Stop();
    }

    private void OnFormatChanged(object? sender, AudioFormat format)
    {
        lock (_lock)
        {
            // Raised for every audio SETUP, and iOS sets up a fresh stream each time it brings
            // sound back after dropping the last one. The device can stay open across that, but
            // the new stream's timeline starts from nothing.
            _streamGeneration++;

            if (_format.Equals(format) && _output is not null) return;
            StopCore();

            if (!format.IsValid)
            {
                _log.Debug($"ignoring an unusable audio format: {format}");
                return;
            }

            Open(format);
        }
    }

    /// <summary>Opens the decoder and the device for one format. Caller must hold <see cref="_lock"/>.</summary>
    private void Open(AudioFormat format)
    {
        try
        {
            _decoder = new AudioDecoder(format);
            var waveFormat = new WaveFormat(_decoder.OutputSampleRate, 16, _decoder.OutputChannels);

            _buffer = new BufferedWaveProvider(waveFormat)
            {
                BufferDuration = Capacity,
                // Only ever reached if the controller fails to keep the level down, and a
                // late packet is worth less than blocking the receive thread.
                DiscardOnBufferOverflow = true,
                // Running dry is measured by the meter, which has to see the shortfall
                // rather than silence already padded in.
                ReadFully = false,
            };

            _meter = new StarvationMeter(_buffer, (int)(waveFormat.SampleRate * FadeDuration.TotalSeconds));
            _controller = new PlayoutController(waveFormat.SampleRate, _reserve);
            _silence = new byte[waveFormat.AverageBytesPerSecond / 4];

            _volume = new VolumeSampleProvider(_meter.ToSampleProvider());
            ApplyVolume();

            // Shared mode: SoulScreen should mix with everything else on the PC rather
            // than seize the device. A chosen endpoint that has gone away falls back to
            // the default rather than leaving the session silent.
            var device = AudioDevices.TryOpen(_outputDeviceId);
            _output = device is null
                ? new WasapiOut(NAudio.CoreAudioApi.AudioClientShareMode.Shared, useEventSync: true,
                    latency: DeviceLatencyMilliseconds)
                : new WasapiOut(device, NAudio.CoreAudioApi.AudioClientShareMode.Shared, useEventSync: true,
                    latency: DeviceLatencyMilliseconds);
            _output.Init(_volume);
            _output.Play();

            _format = format;
            _log.Info($"playing {format} through {_output.OutputWaveFormat}" +
                      (device is null ? "" : $" on {device.FriendlyName}") +
                      $", holding {_reserve.TotalMilliseconds:0} ms in reserve");
            FormatChanged?.Invoke(this, format);
        }
        catch (Exception ex)
        {
            // No speakers, no decoder, or a device in exclusive use: the picture is
            // still worth showing, so audio simply stays off.
            _log.Warn($"audio playback is unavailable: {ex.Message}", ex);
            StopCore();
        }
    }

    private void OnSampleReady(object? sender, MediaSample sample)
    {
        lock (_lock)
        {
            var decoder = _decoder;
            var buffer = _buffer;
            var meter = _meter;
            var controller = _controller;
            if (decoder is null || buffer is null || meter is null || controller is null) return;

            try
            {
                if (_handledGeneration != _streamGeneration)
                {
                    _handledGeneration = _streamGeneration;
                    StartTimelineOver(controller);
                }

                var waveFormat = buffer.WaveFormat;
                var align = waveFormat.BlockAlign;

                FillGapBefore(buffer, controller, sample.TimestampUs);

                // Decoded even if the controller then skips it: the decoder carries state from
                // one frame into the next, and starving it of one would corrupt the ones after.
                var pcm = decoder.Decode(sample.Span);
                if (pcm.IsEmpty) return;

                var packetFrames = pcm.Length / align;
                AdvanceTimeline(waveFormat, sample.TimestampUs, packetFrames);

                var step = controller.Next(buffer.BufferedBytes / align, packetFrames, meter.StarvedFrames);

                if (step.SilenceFrames > 0) AddSilence(buffer, step.SilenceFrames * align);

                if (step.Skip)
                {
                    Interlocked.Increment(ref _skippedPackets);
                    return;
                }

                var capacity = pcm.Length + Math.Max(step.AdjustFrames, 0) * align;
                if (_transfer.Length < capacity) _transfer = new byte[capacity];
                pcm.CopyTo(_transfer);

                var length = pcm.Length;
                var channels = waveFormat.Channels;
                if (step.AdjustFrames > 0) length = PcmSplice.Stretch(_transfer, length, channels, step.AdjustFrames);
                else if (step.AdjustFrames < 0) length = PcmSplice.Shrink(_transfer, length, channels, -step.AdjustFrames);

                var fadeFrames = (int)(waveFormat.SampleRate * FadeDuration.TotalSeconds);
                if (step.FadeIn || _fadeInAfterGap) PcmSplice.FadeIn(_transfer.AsSpan(0, length), channels, fadeFrames);
                if (step.FadeOut) PcmSplice.FadeOut(_transfer.AsSpan(0, length), channels, fadeFrames);
                _fadeInAfterGap = false;

                buffer.AddSamples(_transfer, 0, length);
                Interlocked.Increment(ref _playedPackets);
            }
            catch (Exception ex)
            {
                _log.Warn("an audio packet could not be played", ex);
            }
        }
    }

    private void StartTimelineOver(PlayoutController controller)
    {
        _haveTimestamp = false;
        _packetDurationUs = 0;
        _fadeInAfterGap = false;
        controller.Reset();
    }

    /// <summary>
    /// Inserts silence for packets that never arrived, so what follows plays at the moment it
    /// was recorded rather than as soon as there is room for it.
    /// <para>
    /// Simply playing the next packet early looks harmless one packet at a time - eleven
    /// milliseconds - but the time comes out of the margin, and a few losses in quick
    /// succession is the whole margin gone.
    /// </para>
    /// </summary>
    private void FillGapBefore(BufferedWaveProvider buffer, PlayoutController controller, long timestampUs)
    {
        if (!_haveTimestamp || _packetDurationUs <= 0) return;

        var gapUs = timestampUs - _expectedTimestampUs;

        // Half a packet, not a microsecond. What the decoder produces and what the sender's
        // clock says will not agree to the sample, and treating that disagreement as loss
        // would splice silence into every packet of a stream that never lost one. Each
        // expectation is measured from its own packet's timestamp, so the small error does
        // not accumulate into a real one.
        if (gapUs < _packetDurationUs / 2) return;

        var gap = TimeSpan.FromMicroseconds(gapUs);
        if (gap > MaximumGapFill)
        {
            // Longer than any run of loss worth papering over: the stream paused, or the
            // sender's clock jumped. Start the timeline again from this packet.
            StartTimelineOver(controller);
            return;
        }

        var waveFormat = buffer.WaveFormat;
        var bytes = (int)(waveFormat.AverageBytesPerSecond * gap.TotalSeconds);
        bytes -= bytes % waveFormat.BlockAlign;
        if (bytes <= 0) return;

        AddSilence(buffer, bytes);
        _fadeInAfterGap = true;
        Interlocked.Increment(ref _filledGaps);
    }

    /// <summary>Notes where the next packet should sit on the sender's clock.</summary>
    private void AdvanceTimeline(WaveFormat waveFormat, long timestampUs, int frames)
    {
        var durationUs = (long)frames * 1_000_000 / Math.Max(waveFormat.SampleRate, 1);
        _packetDurationUs = durationUs;
        _expectedTimestampUs = timestampUs + durationUs;
        _haveTimestamp = true;
    }

    private void AddSilence(BufferedWaveProvider buffer, int bytes)
    {
        while (bytes > 0)
        {
            var chunk = Math.Min(bytes, _silence.Length);
            if (chunk <= 0) return;
            buffer.AddSamples(_silence, 0, chunk);
            bytes -= chunk;
        }
    }

    private void ApplyVolume()
    {
        var volume = _volume;
        if (volume is not null) volume.Volume = _muted ? 0f : _requestedVolume;
    }

    public void Stop()
    {
        lock (_lock) StopCore();
    }

    private void StopCore()
    {
        try { _output?.Stop(); }
        catch (Exception ex) { _log.Debug($"stopping the audio device failed: {ex.Message}"); }

        _output?.Dispose();
        _output = null;
        _volume = null;
        _meter = null;
        _buffer = null;
        _silence = [];
        _haveTimestamp = false;
        _packetDurationUs = 0;
        _fadeInAfterGap = false;

        if (_controller is { } controller)
        {
            _skips += controller.SkipCount;
            _stretchedFrames += controller.StretchedFrames;
            _shrunkFrames += controller.ShrunkFrames;
            _stalls += controller.StallCount;
            _starvedFrames += controller.StarvedFrames;
            _controller = null;
        }

        _decoder?.Dispose();
        _decoder = null;
        _format = AudioFormat.None;
    }

    public ValueTask DisposeAsync()
    {
        Detach();
        Stop();
        if (PlayedPacketCount > 0)
            _log.Info($"audio closed after {PlayedPacketCount} packets: " +
                      $"{_stalls} stalls ({_starvedFrames} frames of silence), " +
                      $"{_skips} skips ({DroppedPacketCount} packets), {FilledGapCount} gaps filled, " +
                      $"{_stretchedFrames} frames spliced in and {_shrunkFrames} out");
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Stands between the playback buffer and the device, counting the silence the device has
    /// to be given when nothing is waiting, and softening the edges of it.
    /// <para>
    /// Runs on NAudio's playback thread, and never takes the pipeline's lock: stopping the
    /// device waits for that thread, and it must not be waiting on the thread that is stopping it.
    /// </para>
    /// </summary>
    private sealed class StarvationMeter(BufferedWaveProvider source, int fadeFrames) : IWaveProvider
    {
        private long _starvedFrames;
        private bool _starved;
        private bool _priorityRaised;

        public WaveFormat WaveFormat => source.WaveFormat;

        /// <summary>Running total of frames of silence handed to the device.</summary>
        public long StarvedFrames => Interlocked.Read(ref _starvedFrames);

        public int Read(byte[] buffer, int offset, int count)
        {
            if (!_priorityRaised)
            {
                // NAudio starts its playback thread at normal priority, below the video decode
                // thread and level with FFmpeg's slice threads. Falling behind by one device
                // period is an audible gap, and nothing else in the process has a deadline
                // that short.
                _priorityRaised = true;
                try { Thread.CurrentThread.Priority = ThreadPriority.Highest; }
                catch (Exception) { /* keep the priority it had */ }
            }

            var channels = source.WaveFormat.Channels;
            var read = source.Read(buffer, offset, count);

            if (read > 0 && _starved) PcmSplice.FadeIn(buffer.AsSpan(offset, read), channels, fadeFrames);

            if (read >= count)
            {
                _starved = false;
                return read;
            }

            // Ran dry part way. What did arrive fades out rather than stopping dead, and the
            // rest of the request is silence, counted.
            if (read > 0) PcmSplice.FadeOut(buffer.AsSpan(offset, read), channels, fadeFrames);
            Array.Clear(buffer, offset + read, count - read);
            Interlocked.Add(ref _starvedFrames, (count - read) / Math.Max(source.WaveFormat.BlockAlign, 1));
            _starved = true;
            return count;
        }
    }
}
