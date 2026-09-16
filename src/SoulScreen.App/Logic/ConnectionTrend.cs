namespace SoulScreen.App.Logic;

/// <summary>
/// Detects a worsening trend in a short run of samples - frame rate falling, or round-trip
/// time rising - rather than judging only the most recent reading.
/// <para>
/// A first-half-versus-second-half comparison, not a full regression: a receiver's samples
/// are noisy enough that a least-squares slope reacts to one spike near either end of the
/// window. Comparing two averages against each other is steadier, and easy to reason about -
/// "the second half of what has just been seen is meaningfully worse than the first half".
/// </para>
/// Deliberately free of WPF so the judgement can be tested on its own.
/// </summary>
public static class ConnectionTrend
{
    /// <summary>Fewer samples than this and there is nothing to call a trend - a blip and a
    /// decline look identical with three points.</summary>
    public const int MinimumSamples = 6;

    /// <summary>
    /// True when the second half of <paramref name="samples"/>, oldest first, is worse than
    /// the first half by at least <paramref name="relativeThreshold"/> of the first half's
    /// average. An odd sample in the middle is left out of both halves, rather than tipping
    /// into whichever side would make the comparison notice it more.
    /// </summary>
    /// <param name="higherIsWorse">False for frame rate, where falling is the concern; true
    /// for round-trip time, where rising is.</param>
    public static bool IsDeclining(IReadOnlyList<double> samples, bool higherIsWorse, double relativeThreshold)
    {
        if (samples.Count < MinimumSamples) return false;

        var half = samples.Count / 2;
        var firstAverage = Average(samples, 0, half);
        var secondAverage = Average(samples, samples.Count - half, half);

        var change = higherIsWorse ? secondAverage - firstAverage : firstAverage - secondAverage;
        if (change <= 0) return false;

        var baseline = Math.Abs(firstAverage);
        // A baseline of (near) zero has no meaningful percentage to fall by; any move away
        // from it is reported directly rather than divided into something enormous.
        if (baseline < 1e-6) return change > 1e-6;

        return change / baseline >= relativeThreshold;
    }

    private static double Average(IReadOnlyList<double> samples, int start, int count)
    {
        double sum = 0;
        for (var i = start; i < start + count; i++) sum += samples[i];
        return sum / count;
    }
}

/// <summary>
/// Watches a session's recent frame rate and round-trip time for a worsening trend, and
/// offers advice about it once per stretch of trouble - the same hush-and-recover shape
/// <see cref="ConnectionAdvice"/> uses for a connection that has already gone poor, applied
/// here to one that is still heading that way.
/// <para>
/// This is the early warning: <see cref="ConnectionTrend.IsDeclining"/> can fire while the
/// three-bar meter still reads Good, because a session that has lost nothing yet can still be
/// getting steadily worse. Reacting only once packets are actually lost is what the ordinary
/// quality meter already does; this is what notices the slide before that.
/// </para>
/// Deliberately free of WPF so it can be tested on its own.
/// </summary>
public sealed class ConnectionTrendWatcher
{
    /// <summary>Samples kept per metric - enough recent history to call a trend without
    /// reacting to ancient readings from earlier in a long session.</summary>
    public const int WindowSize = 8;

    /// <summary>A quarter drop in frame rate, hollowed out over the window, is worth a word.</summary>
    public const double FrameRateDeclineThreshold = 0.25;

    /// <summary>Round-trip time is noisier than frame rate by nature; half again as long
    /// asks for a clearer signal before it is named.</summary>
    public const double RoundTripIncreaseThreshold = 0.5;

    /// <summary>How long after advice is given it will not be given again for the same
    /// stretch of decline.</summary>
    public static readonly TimeSpan Hush = TimeSpan.FromMinutes(3);

    /// <summary>A stretch without decline longer than this clears the hush, so a fresh
    /// slide later in the session is worth naming again.</summary>
    public static readonly TimeSpan Recovery = TimeSpan.FromSeconds(30);

    private readonly Queue<double> _frameRates = new();
    private readonly Queue<double> _roundTrips = new();
    private TimeSpan? _warnedAt;
    private TimeSpan? _goodSince;

    /// <summary>
    /// Feeds one tick's reading in. <paramref name="roundTripMs"/> is null before the timing
    /// channel has synchronised, in which case the frame-rate trend alone decides.
    /// </summary>
    /// <returns>True the moment a decline is worth mentioning - once per stretch of it.</returns>
    public bool Sample(TimeSpan now, double frameRate, double? roundTripMs)
    {
        Enqueue(_frameRates, frameRate);
        if (roundTripMs is { } rtt) Enqueue(_roundTrips, rtt);

        var declining =
            ConnectionTrend.IsDeclining(_frameRates.ToArray(), higherIsWorse: false, FrameRateDeclineThreshold)
            || (roundTripMs is not null
                && ConnectionTrend.IsDeclining(_roundTrips.ToArray(), higherIsWorse: true, RoundTripIncreaseThreshold));

        if (declining)
        {
            _goodSince = null;
            var hushed = _warnedAt is { } at && now - at < Hush;
            if (hushed) return false;
            _warnedAt = now;
            return true;
        }

        _goodSince ??= now;
        if (_goodSince is { } good && now - good >= Recovery) _warnedAt = null;
        return false;
    }

    /// <summary>Forgets the session measured so far; the next sample starts a new one.</summary>
    public void Reset()
    {
        _frameRates.Clear();
        _roundTrips.Clear();
        _warnedAt = null;
        _goodSince = null;
    }

    private static void Enqueue(Queue<double> queue, double value)
    {
        queue.Enqueue(value);
        while (queue.Count > WindowSize) queue.Dequeue();
    }
}
