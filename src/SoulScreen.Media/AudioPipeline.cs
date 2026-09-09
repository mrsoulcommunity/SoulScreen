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
/// Latency is bounded by discarding audio rather than letting it accumulate: sound that
/// arrives late is worse than sound that is missing, because a growing buffer drifts
/// permanently out of step with the picture.
/// </para>
/// </summary>
public sealed class AudioPipeline : IAsyncDisposable
{
    /// <summary>Playback buffer ceiling. Beyond this the oldest audio is dropped.</summary>
    private static readonly TimeSpan MaxBuffer = TimeSpan.FromMilliseconds(300);

    /// <summary>Buffer level that triggers a catch-up flush.</summary>
    private static readonly TimeSpan CatchUpThreshold = TimeSpan.FromMilliseconds(220);

    private readonly ILogger _log = Log.For("audio-out");
    private readonly object _deviceLock = new();

    private IMirrorSource? _source;
    private AudioDecoder? _decoder;
    private BufferedWaveProvider? _buffer;
    private WasapiOut? _output;
    private VolumeSampleProvider? _volume;
    private AudioFormat _format = AudioFormat.None;
    private long _playedPackets;
    private long _droppedPackets;
    private float _requestedVolume = 1f;
    private bool _muted;

    /// <summary>
    /// Reused across packets. NAudio takes an array rather than a span, and allocating one
    /// ninety times a second only to throw it away is pure garbage-collector pressure on the
    /// thread that also receives the audio.
    /// </summary>
    private byte[] _transfer = new byte[8192];

    /// <summary>Format currently being played, if any.</summary>
    public AudioFormat Format => _format;

    public long PlayedPacketCount => Interlocked.Read(ref _playedPackets);

    /// <summary>Packets discarded to keep playback from drifting behind the picture.</summary>
    public long DroppedPacketCount => Interlocked.Read(ref _droppedPackets);

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
        lock (_deviceLock)
        {
            if (_format.Equals(format) && _output is not null) return;
            StopCore();

            if (!format.IsValid)
            {
                _log.Debug($"ignoring an unusable audio format: {format}");
                return;
            }

            try
            {
                _decoder = new AudioDecoder(format);
                var waveFormat = new WaveFormat(_decoder.OutputSampleRate, 16, _decoder.OutputChannels);

                _buffer = new BufferedWaveProvider(waveFormat)
                {
                    BufferDuration = MaxBuffer,
                    // A late packet is worth less than a low-latency stream; drop rather
                    // than block the receive thread.
                    DiscardOnBufferOverflow = true,
                };

                _volume = new VolumeSampleProvider(_buffer.ToSampleProvider());
                ApplyVolume();

                // Shared mode: SoulScreen should mix with everything else on the PC rather
                // than seize the device.
                _output = new WasapiOut(NAudio.CoreAudioApi.AudioClientShareMode.Shared, useEventSync: true, latency: 60);
                _output.Init(_volume);
                _output.Play();

                _format = format;
                _log.Info($"playing {format} through {_output.OutputWaveFormat}");
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
    }

    private void OnSampleReady(object? sender, MediaSample sample)
    {
        AudioDecoder? decoder;
        BufferedWaveProvider? buffer;

        lock (_deviceLock)
        {
            decoder = _decoder;
            buffer = _buffer;
        }

        if (decoder is null || buffer is null) return;

        try
        {
            // Catching up by flushing is deliberate: skipping decode would desynchronise the
            // codec's internal state, and playing the backlog would keep the delay forever.
            if (buffer.BufferedDuration > CatchUpThreshold)
            {
                buffer.ClearBuffer();
                Interlocked.Increment(ref _droppedPackets);
            }

            var pcm = decoder.Decode(sample.Span);
            if (pcm.IsEmpty) return;

            if (_transfer.Length < pcm.Length) _transfer = new byte[pcm.Length];
            pcm.CopyTo(_transfer);
            buffer.AddSamples(_transfer, 0, pcm.Length);
            Interlocked.Increment(ref _playedPackets);
        }
        catch (Exception ex)
        {
            _log.Warn("an audio packet could not be played", ex);
        }
    }

    private void ApplyVolume()
    {
        var volume = _volume;
        if (volume is not null) volume.Volume = _muted ? 0f : _requestedVolume;
    }

    public void Stop()
    {
        lock (_deviceLock) StopCore();
    }

    private void StopCore()
    {
        try { _output?.Stop(); }
        catch (Exception ex) { _log.Debug($"stopping the audio device failed: {ex.Message}"); }

        _output?.Dispose();
        _output = null;
        _volume = null;
        _buffer = null;

        _decoder?.Dispose();
        _decoder = null;
        _format = AudioFormat.None;
    }

    public ValueTask DisposeAsync()
    {
        Detach();
        Stop();
        if (PlayedPacketCount > 0)
            _log.Info($"audio closed after {PlayedPacketCount} packets ({DroppedPacketCount} catch-up flushes)");
        return ValueTask.CompletedTask;
    }
}
