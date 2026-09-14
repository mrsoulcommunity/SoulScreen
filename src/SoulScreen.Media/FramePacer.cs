using System.Diagnostics;

namespace SoulScreen.Media;

/// <summary>
/// Holds decoded pictures between the decode thread and the screen, and decides when each
/// one is shown.
/// <para>
/// Frames leave the phone at an even sixty a second and arrive here in clumps: TCP over
/// Wi-Fi delivers two or three at once and then nothing for fifty milliseconds. Showing each
/// one as it turns up puts that clumping straight onto the screen, which is what the eye
/// reads as judder even when every single frame was displayed. The picture is not dropping
/// frames; it is showing them at the wrong times.
/// </para>
/// <para>
/// So arrival and presentation are decoupled. A cushion of frames is built up first, and
/// from then on one is released every <see cref="_intervalTicks"/> - the measured average
/// period of the source, not the arrival of any particular frame. Clumps land in the
/// cushion and drain out evenly.
/// </para>
/// <para>
/// Nothing keeps the two rates equal on their own, so the cushion would slowly fill or empty
/// and eventually stutter either way. The release interval is therefore nudged by up to
/// three percent in whichever direction returns the cushion to <see cref="TargetDelay"/>,
/// which is far too small a change in speed to see and removes the drift entirely.
/// </para>
/// </summary>
public sealed class FramePacer : IDisposable
{
    /// <summary>
    /// Delay the cushion aims for, and the length of stall it can hide. Wi-Fi pauses of that
    /// order are ordinary, and a mirror is worth more smooth and slightly behind than immediate
    /// and stuttering.
    /// <para>
    /// Set as a time rather than a number of frames, because iOS sends fewer frames when less
    /// is changing on screen. Four frames of cushion were seventy milliseconds at sixty a second
    /// and twice that at thirty, which slid the picture against the sound every time the rate
    /// changed; a tenth of a second stays a tenth of a second.
    /// </para>
    /// </summary>
    public static readonly TimeSpan DefaultTargetDelay = TimeSpan.FromMilliseconds(100);

    /// <summary>Least delay worth holding: below this the cushion is one frame and hides nothing.</summary>
    public static readonly TimeSpan MinimumTargetDelay = TimeSpan.FromMilliseconds(30);

    /// <summary>Most delay allowed, bounded by <see cref="MaximumDepth"/> at thirty frames a second.</summary>
    public static readonly TimeSpan MaximumTargetDelay = TimeSpan.FromMilliseconds(250);

    private TimeSpan _targetDelay = DefaultTargetDelay;

    /// <summary>Cushion to fill before the source rate has been measured: the target delay at
    /// sixty frames a second.</summary>
    private const int InitialDepth = 6;

    /// <summary>Least cushion however slow the source, so one late frame is still hidden.</summary>
    private const int MinimumDepth = 2;

    /// <summary>
    /// Hard ceiling, and the size of burst that can arrive without anything being thrown
    /// away. Reaching it does not add delay - the release rate below pulls the cushion back
    /// down to its target - so this is set by how much memory a backlog of full pictures is
    /// worth, not by latency. Ten is under a hundred megabytes at 1080p.
    /// </summary>
    private const int MaximumDepth = 10;

    /// <summary>
    /// Arrivals averaged to estimate the source period. Half a second at sixty a second:
    /// long enough that a clump of four averages away, short enough to follow the phone
    /// changing frame rate.
    /// </summary>
    private const int RateWindow = 32;

    /// <summary>How far the release interval may be stretched or shortened, as a
    /// thirty-secondth - a little over three percent.</summary>
    private const long TrimTicks = 32;

    private readonly Queue<DecodedVideoFrame> _frames = new(MaximumDepth + 1);
    private readonly long[] _arrivals = new long[RateWindow];
    private readonly object _lock = new();

    private int _arrivalCount;
    private int _arrivalIndex;

    /// <summary>Estimated period of the source, in stopwatch ticks. Zero until measured.</summary>
    private long _intervalTicks;

    /// <summary>When the next picture is due on screen, in stopwatch ticks.</summary>
    private long _nextDueTicks;

    /// <summary>False until the cushion has filled once, which is the only time we wait.</summary>
    private bool _primed;

    private long _droppedFrames;

    /// <summary>Pictures discarded without being shown, because the cushion overflowed.</summary>
    public long DroppedFrameCount => Interlocked.Read(ref _droppedFrames);

    /// <summary>
    /// Delay the cushion aims for. Can be changed mid-session: the release rate drifts the
    /// cushion to the new depth over a few seconds rather than dropping or repeating frames.
    /// </summary>
    public TimeSpan TargetDelay
    {
        get { lock (_lock) return _targetDelay; }
        set
        {
            var clamped = value < MinimumTargetDelay ? MinimumTargetDelay
                : value > MaximumTargetDelay ? MaximumTargetDelay : value;
            lock (_lock) _targetDelay = clamped;
        }
    }

    /// <summary>Pictures waiting to be shown.</summary>
    public int Depth
    {
        get { lock (_lock) return _frames.Count; }
    }

    /// <summary>
    /// Frames a second the source is being measured at, or zero before enough have arrived.
    /// This is the rate presentation is paced to.
    /// </summary>
    public double SourceRate
    {
        get
        {
            var interval = Interlocked.Read(ref _intervalTicks);
            return interval <= 0 ? 0 : Stopwatch.Frequency / (double)interval;
        }
    }

    /// <summary>
    /// Takes ownership of a decoded picture. Safe from any thread; returns immediately.
    /// </summary>
    /// <param name="frame">The picture. It is disposed here if the cushion overflows.</param>
    /// <param name="arrivalTicks">Current <see cref="Stopwatch.GetTimestamp"/> reading.</param>
    public void Enqueue(DecodedVideoFrame frame, long arrivalTicks)
    {
        DecodedVideoFrame? stale = null;

        lock (_lock)
        {
            RecordArrivalLocked(arrivalTicks);
            _frames.Enqueue(frame);

            if (_frames.Count > MaximumDepth)
            {
                stale = _frames.Dequeue();
                Interlocked.Increment(ref _droppedFrames);
            }
        }

        stale?.Dispose();
    }

    /// <summary>
    /// Returns the picture due on screen now, or null when the next one is not due yet, the
    /// cushion is still filling, or nothing has arrived. The caller owns what it gets back.
    /// </summary>
    /// <param name="now">Current <see cref="Stopwatch.GetTimestamp"/> reading.</param>
    /// <param name="passIntervalTicks">
    /// Measured gap between composition passes. A frame due part way between two passes is
    /// shown at whichever is closer rather than always the later one, which halves the error
    /// between when a picture was meant to appear and when it did.
    /// </param>
    public DecodedVideoFrame? TryDequeue(long now, long passIntervalTicks)
    {
        DecodedVideoFrame? frame;
        List<DecodedVideoFrame>? stale = null;

        lock (_lock)
        {
            if (!_primed)
            {
                // Releasing before the cushion exists would mean spending the whole session
                // one frame from empty, which is the state this class is here to avoid.
                if (_frames.Count < TargetDepthLocked()) return null;
                _primed = true;
                _nextDueTicks = now;
            }

            var interval = _intervalTicks;
            if (interval <= 0)
            {
                // Too early to have measured the source. One per pass until we have.
                return _frames.Count == 0 ? null : _frames.Dequeue();
            }

            if (now + passIntervalTicks / 2 < _nextDueTicks) return null;

            if (_frames.Count == 0)
            {
                // Nothing arrived in time. The screen keeps the last picture; re-phase so the
                // next one is a full interval away rather than instantly overdue.
                _nextDueTicks = now + interval;
                return null;
            }

            frame = _frames.Dequeue();
            _nextDueTicks += AdjustedIntervalLocked(interval);

            // The display cannot always show every picture. A sixty-hertz panel with a phone
            // sending sixty is the happy case; a thirty-hertz one, or a window WPF composites
            // at a reduced rate because something else covers it, gets fewer passes a second
            // than pictures arrive. One picture per pass is then a permanent shortfall: the
            // cushion fills to its ceiling and stays there, so the same pictures are lost as
            // would be lost anyway - but only after the mirror has fallen a third of a second
            // behind and stayed there for the rest of the session.
            //
            // So a cushion that has grown well past its target sheds one extra picture per
            // pass. The oldest goes and the newer one is shown, which costs a frame nobody
            // could have seen and walks the delay back to where it was set. One per pass,
            // never a handful at once: a single skipped frame is invisible where a jump of
            // six is a stutter.
            //
            // Above target alone is not enough to act on - a cushion above its target is
            // ordinary jitter being absorbed, which is what this class is for, and the
            // interval trim walks that back down without dropping anything.
            if (_frames.Count > TargetDepthLocked() + 2)
            {
                stale = [frame];
                frame = _frames.Dequeue();
                _nextDueTicks += interval;
                Interlocked.Increment(ref _droppedFrames);
            }

            // A stall longer than the cushion - a suspended machine, a window dragged for a
            // while - leaves the schedule in the past with nothing left to skip. Start it
            // again a full interval from now: releasing from "now" would put the next
            // picture on the very next pass, which is the burst of stale pictures this
            // guard exists to prevent.
            if (now - _nextDueTicks > 4 * interval) _nextDueTicks = now + interval;
        }

        // Outside the lock, as everywhere else here: returning a pooled buffer is cheap, but
        // the decode thread may be waiting on the lock to hand over the next picture.
        if (stale is not null) foreach (var discarded in stale) discarded.Dispose();
        return frame;
    }

    /// <summary>
    /// The release interval, leaned on slightly in whichever direction returns the cushion to
    /// its target depth. Caller must hold <see cref="_lock"/>.
    /// </summary>
    private long AdjustedIntervalLocked(long interval)
    {
        var trim = interval / TrimTicks;
        var target = TargetDepthLocked();
        if (_frames.Count > target) return interval - trim;
        if (_frames.Count < target) return interval + trim;
        return interval;
    }

    /// <summary>
    /// Frames of cushion that make up <see cref="TargetDelay"/> at the measured source rate.
    /// Rounded rather than rounded up: sixty a second measures a hair either side of 16.7 ms,
    /// and rounding up would flip the target between six and seven from one frame to the next.
    /// Caller must hold <see cref="_lock"/>.
    /// </summary>
    private int TargetDepthLocked()
    {
        var interval = _intervalTicks;
        if (interval <= 0)
            return Math.Clamp((int)Math.Round(_targetDelay.TotalSeconds * 60), MinimumDepth, MaximumDepth - 2);

        var frames = (int)Math.Round(_targetDelay.TotalSeconds * Stopwatch.Frequency / interval);
        return Math.Clamp(frames, MinimumDepth, MaximumDepth - 2);
    }

    /// <summary>
    /// Folds one arrival into the rate estimate. Caller must hold <see cref="_lock"/>.
    /// <para>
    /// The whole window is divided by the time it spans rather than each gap being averaged
    /// individually, because individual gaps are exactly what is unreliable here: a clump of
    /// three frames one millisecond apart says nothing about the source rate, but the span
    /// they sit in does.
    /// </para>
    /// </summary>
    private void RecordArrivalLocked(long now)
    {
        _arrivals[_arrivalIndex] = now;
        _arrivalIndex = (_arrivalIndex + 1) % RateWindow;
        if (_arrivalCount < RateWindow) _arrivalCount++;

        if (_arrivalCount < RateWindow) return;

        var oldest = _arrivals[_arrivalIndex];
        var span = now - oldest;
        if (span <= 0) return;

        var interval = span / (RateWindow - 1);

        // Between about four hundred frames a second and five. Anything outside that is a
        // measurement artefact - a stalled link, a burst after a pause - not a frame rate.
        var fastest = Stopwatch.Frequency / 400;
        var slowest = Stopwatch.Frequency / 5;
        if (interval < fastest || interval > slowest) return;

        Interlocked.Exchange(ref _intervalTicks, interval);
    }

    /// <summary>Drops everything held and starts the cushion again.</summary>
    public void Reset()
    {
        List<DecodedVideoFrame> discarded;
        lock (_lock)
        {
            discarded = [.. _frames];
            _frames.Clear();
            _primed = false;
            _nextDueTicks = 0;
            _arrivalCount = 0;
            _arrivalIndex = 0;
            Interlocked.Exchange(ref _intervalTicks, 0);
        }

        foreach (var frame in discarded) frame.Dispose();
    }

    /// <summary>Forgets the drop count without disturbing the frames in flight.</summary>
    public void ResetStatistics() => Interlocked.Exchange(ref _droppedFrames, 0);

    public void Dispose() => Reset();
}
