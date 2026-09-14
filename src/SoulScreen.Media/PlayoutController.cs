namespace SoulScreen.Media;

/// <summary>What to do with one packet of decoded audio before it is queued for the device.</summary>
/// <param name="SilenceFrames">Silence to queue ahead of the packet.</param>
/// <param name="AdjustFrames">Frames to splice into the packet (positive) or out of it (negative).</param>
/// <param name="Skip">Discard the packet instead of queueing it.</param>
/// <param name="FadeIn">Ramp the start of the packet up from silence.</param>
/// <param name="FadeOut">Ramp the end of the packet down to silence.</param>
internal readonly record struct PlayoutStep(
    int SilenceFrames = 0,
    int AdjustFrames = 0,
    bool Skip = false,
    bool FadeIn = false,
    bool FadeOut = false);

/// <summary>
/// Decides, a packet at a time, how the phone's audio is laid into the playback buffer so
/// that it is heard without gaps and without falling behind the picture.
/// <para>
/// The number it watches is how much audio is still waiting at the moment each packet
/// arrives. Measured before the packet is added, that is the lowest the buffer gets on the
/// way to that arrival, so the lowest such reading over a second is exactly the margin by
/// which the sound card avoided running dry in that second. Packets that arrive in clumps,
/// packets that arrive late, and the sound card taking its audio a period at a time all show
/// up in it, which a buffer level sampled at arbitrary moments does not. That margin is held
/// at <see cref="Reserve"/>, and everything above it is delay nobody needs.
/// </para>
/// <para>
/// Corrections are gentle when they can be and quick when they must be. Clock drift between
/// the phone and the sound card amounts to a few milliseconds a minute, and a single frame
/// spliced into or out of a packet every so often - a forty-fourth of a millisecond - takes
/// it out inaudibly. A Wi-Fi stall that delivers a fifth of a second of audio at once is
/// another matter: splicing that away a frame at a time would leave the sound behind the
/// picture for a minute, so whole packets are skipped instead, in one go, behind a fade.
/// </para>
/// <para>
/// When the card does run dry, the margin was too small for this network. Running dry has
/// already put the margin back - everything after it plays later by exactly the silence -
/// so nothing is added then. Instead the reserve itself is raised by what the stall cost, and
/// held there for a while before being let back down, so the next stall like it goes unheard.
/// A stall that only came close to emptying the buffer raises it too, by the distance it came.
/// </para>
/// <para>Not thread-safe; drive it from the thread that handles packets.</para>
/// </summary>
internal sealed class PlayoutController
{
    /// <summary>
    /// The margin held on a network that has not stalled: audio still waiting, at worst, when
    /// the next packet arrives, so a Wi-Fi pause shorter than this is never heard. It is also
    /// most of the delay this adds, and it is set against the picture's rather than on its own:
    /// <see cref="FramePacer"/> holds a tenth of a second of frames, and this plus the sound
    /// card's own buffer puts the voice level with the lips.
    /// </summary>
    internal static readonly TimeSpan Reserve = TimeSpan.FromMilliseconds(80);

    /// <summary>How far the margin may sit from its target before it is corrected at all.</summary>
    internal static readonly TimeSpan Tolerance = TimeSpan.FromMilliseconds(5);

    /// <summary>
    /// Excess margin beyond which it is skipped away rather than spliced away: past this, the
    /// splices would take longer to catch up than anyone would put up with the sound trailing.
    /// </summary>
    internal static readonly TimeSpan SkipThreshold = TimeSpan.FromMilliseconds(60);

    /// <summary>
    /// How close to empty a window may come before it counts against the reserve even though
    /// nothing was heard - and the headroom added on top of what a stall that was heard cost,
    /// so that the same stall next time clears the raised reserve rather than only just failing to.
    /// </summary>
    internal static readonly TimeSpan CloseCall = TimeSpan.FromMilliseconds(15);

    /// <summary>Most that stalls may raise the reserve by, however badly the network behaves.</summary>
    internal static readonly TimeSpan MaximumRaise = TimeSpan.FromMilliseconds(60);

    /// <summary>How long a raised reserve is kept after the last shortfall before it starts down.</summary>
    internal static readonly TimeSpan RaiseHold = TimeSpan.FromSeconds(30);

    /// <summary>How quickly a raised reserve comes back down once its hold has passed.</summary>
    internal static readonly TimeSpan RaiseDecayPerSecond = TimeSpan.FromMilliseconds(1);

    /// <summary>Audio over which each lowest reading is taken and each decision made.</summary>
    internal static readonly TimeSpan Window = TimeSpan.FromSeconds(1);

    /// <summary>Slow-down while building the margin back up: 0.2%, one frame in 500.</summary>
    private const int StretchPartsPerMillion = 2_000;

    /// <summary>Speed-up while draining excess margin, per <see cref="ShrinkStep"/> of excess.</summary>
    private const int ShrinkPartsPerMillion = 2_000;

    private const int MaximumShrinkSteps = 2;

    private static readonly TimeSpan ShrinkStep = TimeSpan.FromMilliseconds(20);

    private readonly int _reserveFrames;
    private readonly int _toleranceFrames;
    private readonly int _skipThresholdFrames;
    private readonly int _closeCallFrames;
    private readonly int _maximumRaiseFrames;
    private readonly int _raiseDecayFrames;
    private readonly int _raiseHoldWindows;
    private readonly int _windowFrames;
    private readonly int _shrinkStepFrames;

    private bool _primed;
    private bool _warmingUp;
    private bool _fadeInNext;
    private int _skipRemaining;

    private int _windowLowest = int.MaxValue;
    private long _windowLength;
    private long _starvedAtWindowStart;

    private int _raiseFrames;
    private int _windowsSinceShortfall = int.MaxValue;

    private int _ratePartsPerMillion;
    private long _rateRemainder;

    public PlayoutController(int sampleRate) : this(sampleRate, Reserve) { }

    /// <param name="sampleRate">Sample rate of the audio being played.</param>
    /// <param name="reserve">The margin to hold on a network that has not stalled. Chosen
    /// against the picture's own delay so that voice and lips stay level.</param>
    public PlayoutController(int sampleRate, TimeSpan reserve)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        ArgumentOutOfRangeException.ThrowIfNegative(reserve.Ticks);
        SampleRate = sampleRate;

        _reserveFrames = Frames(reserve);
        _toleranceFrames = Frames(Tolerance);
        _skipThresholdFrames = Frames(SkipThreshold);
        _closeCallFrames = Frames(CloseCall);
        _maximumRaiseFrames = Frames(MaximumRaise);
        _windowFrames = Frames(Window);
        _raiseDecayFrames = (int)(Frames(RaiseDecayPerSecond) * Window.TotalSeconds);
        _raiseHoldWindows = (int)Math.Ceiling(RaiseHold / Window);
        _shrinkStepFrames = Frames(ShrinkStep);

        int Frames(TimeSpan duration) => (int)(duration.Ticks * sampleRate / TimeSpan.TicksPerSecond);
    }

    public int SampleRate { get; }

    /// <summary>The margin currently aimed for: the reserve, plus what stalls have raised it by.</summary>
    public int TargetFrames => _reserveFrames + _raiseFrames;

    /// <summary>Times the audio was skipped back into step after a stall.</summary>
    public long SkipCount { get; private set; }

    /// <summary>Packets discarded by those skips.</summary>
    public long SkippedPacketCount { get; private set; }

    /// <summary>Frames spliced in to build the margin up.</summary>
    public long StretchedFrames { get; private set; }

    /// <summary>Frames spliced out to bring the margin down.</summary>
    public long ShrunkFrames { get; private set; }

    /// <summary>Windows in which the sound card ran dry while a stream was playing.</summary>
    public long StallCount { get; private set; }

    /// <summary>Silence the sound card was given in those windows, in frames.</summary>
    public long StarvedFrames { get; private set; }

    /// <summary>
    /// Starts the timeline over, for a new stream or one resuming after a pause. The next
    /// packet is primed with the reserve. What stalls have taught about the network is kept:
    /// it is the same network.
    /// </summary>
    public void Reset()
    {
        _primed = false;
        _fadeInNext = false;
        _skipRemaining = 0;
        SetRate(0);
    }

    /// <summary>Decides what to do with the next packet.</summary>
    /// <param name="queuedFrames">Audio waiting to be played, measured before this packet is added.</param>
    /// <param name="packetFrames">Length of the packet.</param>
    /// <param name="starvedFrames">
    /// Running total of silence the sound card has been given because nothing was waiting.
    /// Only its increase between calls is used.
    /// </param>
    public PlayoutStep Next(int queuedFrames, int packetFrames, long starvedFrames)
    {
        if (packetFrames <= 0) return default;

        if (!_primed)
        {
            // Nothing is waiting, and the card may have been fed silence for a while. The
            // reserve goes in front of the first packet, so the stream starts with its margin
            // rather than spending its first second earning it through dropouts.
            _primed = true;
            _warmingUp = true;
            BeginWindow(starvedFrames);
            return new PlayoutStep(SilenceFrames: Math.Max(0, TargetFrames - queuedFrames), FadeIn: true);
        }

        if (_skipRemaining > 0)
        {
            SkippedPacketCount++;
            if (--_skipRemaining == 0)
            {
                _fadeInNext = true;
                // What was measured before the skip describes a buffer that no longer exists.
                BeginWindow(starvedFrames);
            }
            return new PlayoutStep(Skip: true);
        }

        _windowLowest = Math.Min(_windowLowest, queuedFrames);
        _windowLength += packetFrames;

        var step = new PlayoutStep(AdjustFrames: TakeAdjustment(packetFrames), FadeIn: _fadeInNext);
        _fadeInNext = false;

        return _windowLength >= _windowFrames ? CloseWindow(step, packetFrames, starvedFrames) : step;
    }

    private PlayoutStep CloseWindow(PlayoutStep step, int packetFrames, long starvedFrames)
    {
        var lowest = _windowLowest;
        var starved = starvedFrames - _starvedAtWindowStart;
        BeginWindow(starvedFrames);

        if (_warmingUp)
        {
            // The first window after a start measures the start - the device's first fill and
            // the prime going out - rather than the network.
            _warmingUp = false;
            return step;
        }

        // How far this window fell short of a safe margin: all the silence plus the close-call
        // distance if it ran dry, or the distance still missing if it only came near.
        var shortfall = starved > 0 ? starved + _closeCallFrames : Math.Max(0, _closeCallFrames - lowest);

        if (shortfall > 0)
        {
            _raiseFrames = (int)Math.Min(_raiseFrames + shortfall, _maximumRaiseFrames);
            _windowsSinceShortfall = 0;
        }
        else if (_windowsSinceShortfall < _raiseHoldWindows)
        {
            _windowsSinceShortfall++;
        }
        else
        {
            _raiseFrames = Math.Max(0, _raiseFrames - _raiseDecayFrames);
        }

        if (starved > 0)
        {
            // Running dry has already put the margin back; correcting on top of that would
            // count the stall twice.
            StallCount++;
            StarvedFrames += starved;
            SetRate(0);
            return step;
        }

        var excess = lowest - TargetFrames;

        if (excess > _skipThresholdFrames && excess / packetFrames > 0)
        {
            // Whole packets, rounded down, so the margin left afterwards is never below target.
            // This packet is the last one heard before the skip, and fades out into it.
            _skipRemaining = excess / packetFrames;
            SkipCount++;
            SetRate(0);
            return step with { FadeOut = true };
        }

        if (excess > _toleranceFrames)
            SetRate(-ShrinkPartsPerMillion * Math.Min(1 + excess / _shrinkStepFrames, MaximumShrinkSteps));
        else if (excess < -_toleranceFrames)
            SetRate(StretchPartsPerMillion);
        else
            SetRate(0);

        return step;
    }

    private void BeginWindow(long starvedFrames)
    {
        _windowLowest = int.MaxValue;
        _windowLength = 0;
        _starvedAtWindowStart = starvedFrames;
    }

    /// <summary>
    /// Frames to splice into this packet at the current rate. The remainder carries between
    /// packets, so a rate of one frame in five hundred is exactly that over time rather than
    /// rounding to nothing on every 480-frame packet.
    /// </summary>
    private int TakeAdjustment(int packetFrames)
    {
        if (_ratePartsPerMillion == 0) return 0;

        _rateRemainder += (long)packetFrames * _ratePartsPerMillion;
        var frames = (int)(_rateRemainder / 1_000_000);
        _rateRemainder -= frames * 1_000_000L;

        if (frames > 0) StretchedFrames += frames;
        else ShrunkFrames -= frames;
        return frames;
    }

    private void SetRate(int partsPerMillion)
    {
        if (_ratePartsPerMillion == partsPerMillion) return;
        _ratePartsPerMillion = partsPerMillion;
        _rateRemainder = 0;
    }
}
