using System.Diagnostics;
using SoulScreen.Media;
using Xunit;

namespace SoulScreen.Tests;

/// <summary>
/// Drives the pacer from a simulated clock rather than a real one, so the property that
/// matters - that unevenly arriving frames leave evenly spaced - can be measured instead of
/// watched.
/// <para>
/// Each test states the same thing two ways: what the pacer does, and what showing every
/// frame at the first composition pass after it arrives would have done instead. The second
/// is not a straw man; it is the obvious implementation, it is what this replaced, and on
/// the cases below it is visibly worse while every counter in the application still reads
/// healthy.
/// </para>
/// </summary>
public class FramePacerTests
{
    /// <summary>One millisecond of <see cref="Stopwatch"/> ticks.</summary>
    private static readonly long Tick = Stopwatch.Frequency / 1000;

    /// <summary>Pictures are one pixel: the pacer never looks at them, only at their timing.</summary>
    private static DecodedVideoFrame Frame() => DecodedVideoFrame.Rent(1, 1, 4, 0);

    [Fact]
    public void HoldsTheFirstFramesBackUntilTheCushionExists()
    {
        using var pacer = new FramePacer();
        var refresh = 16 * Tick;
        var now = 0L;

        // Fewer frames than the cushion, so none is released. Starting to play out
        // immediately would leave the session permanently one hiccup from empty.
        for (var i = 0; i < 5; i++)
        {
            pacer.Enqueue(Frame(), now);
            Assert.Null(pacer.TryDequeue(now, refresh));
            now += refresh;
        }

        pacer.Enqueue(Frame(), now);

        using var released = pacer.TryDequeue(now, refresh);
        Assert.NotNull(released);
        Assert.Equal(5, pacer.Depth);
    }

    /// <summary>
    /// The delay is the user's to choose - smoother against more immediate - so a shorter
    /// target must prime on fewer frames and a longer one on more, and the value is clamped
    /// to what the cushion can physically hold.
    /// </summary>
    [Theory]
    [InlineData(50, 3)]
    [InlineData(100, 6)]
    [InlineData(150, 8)]
    public void PrimesOnAsManyFramesAsTheTargetDelayHolds(int targetMs, int expectedDepth)
    {
        using var pacer = new FramePacer { TargetDelay = TimeSpan.FromMilliseconds(targetMs) };
        var refresh = 16 * Tick;
        var now = 0L;

        for (var i = 0; i < expectedDepth - 1; i++)
        {
            pacer.Enqueue(Frame(), now);
            Assert.Null(pacer.TryDequeue(now, refresh));
            now += refresh;
        }

        pacer.Enqueue(Frame(), now);
        using var released = pacer.TryDequeue(now, refresh);
        Assert.NotNull(released);
    }

    /// <summary>
    /// A display that cannot show every picture - thirty passes a second against sixty
    /// arriving - must not be met by letting the cushion fill. Filling it loses the same
    /// pictures and buys nothing but a mirror that runs a third of a second late, so the
    /// pacer skips past what the display could never have shown and keeps its delay.
    /// </summary>
    [Fact]
    public void SkipsAheadWhenTheDisplayCannotKeepUpWithTheSource()
    {
        using var pacer = new FramePacer();

        var sourceInterval = Tick * 1000 / 60;   // sixty pictures a second
        var passInterval = Tick * 1000 / 30;     // thirty composition passes a second
        var now = 0L;
        var presented = 0;

        // Five seconds of a sixty-a-second source against a thirty-a-second display.
        for (var pass = 0; pass < 150; pass++)
        {
            for (var i = 0; i < 2; i++)
            {
                pacer.Enqueue(Frame(), now + i * sourceInterval);
            }
            now += passInterval;

            using var shown = pacer.TryDequeue(now, passInterval);
            if (shown is not null) presented++;
        }

        // The cushion stays near its target rather than climbing to the ceiling and staying
        // there, which is what keeps the delay at the tenth of a second it was set to.
        Assert.InRange(pacer.Depth, 0, 8);
        // Roughly one picture per pass reaches the screen, and the rest are accounted for
        // rather than silently overflowing.
        Assert.InRange(presented, 120, 150);
        Assert.InRange(pacer.DroppedFrameCount, 100, 180);
    }

    /// <summary>
    /// The same path must leave a display that does keep up entirely alone: nothing is
    /// dropped when every picture can be shown.
    /// </summary>
    [Fact]
    public void DropsNothingWhenTheDisplayKeepsUp()
    {
        using var pacer = new FramePacer();

        var interval = Tick * 1000 / 60;
        var now = 0L;

        // A second of a sixty-a-second source against a sixty-hertz display.
        for (var pass = 0; pass < 60; pass++)
        {
            pacer.Enqueue(Frame(), now);
            now += interval;
            pacer.TryDequeue(now, interval)?.Dispose();
        }

        Assert.Equal(0, pacer.DroppedFrameCount);
    }

    [Fact]
    public void ClampsTheTargetDelayToWhatTheCushionCanHold()
    {
        using var pacer = new FramePacer();
        pacer.TargetDelay = TimeSpan.FromSeconds(5);
        Assert.Equal(FramePacer.MaximumTargetDelay, pacer.TargetDelay);
        pacer.TargetDelay = TimeSpan.Zero;
        Assert.Equal(FramePacer.MinimumTargetDelay, pacer.TargetDelay);
    }

    /// <summary>
    /// iOS sends fewer frames when less is changing on screen. A cushion counted in frames is
    /// twice as long at thirty a second as at sixty, sliding the picture against the sound
    /// whenever the rate changes; counted in time, the delay stays where it is.
    /// </summary>
    [Theory]
    [InlineData(60)]
    [InlineData(30)]
    public void HoldsTheSameDelayWhateverTheFrameRate(int framesPerSecond)
    {
        using var pacer = new FramePacer();
        var refresh = Stopwatch.Frequency / 60;
        var period = Stopwatch.Frequency / framesPerSecond;
        var nextArrival = 0L;
        var depths = new List<int>();

        for (var now = 0L; now < 20 * Stopwatch.Frequency; now += refresh)
        {
            for (; nextArrival <= now; nextArrival += period) pacer.Enqueue(Frame(), nextArrival);
            pacer.TryDequeue(now, refresh)?.Dispose();
            if (now >= 15 * Stopwatch.Frequency) depths.Add(pacer.Depth);
        }

        var delayMs = depths.Average() * 1000.0 / framesPerSecond;
        Assert.InRange(delayMs, 70, 130);
    }

    /// <summary>
    /// Thirty frames a second arriving two at a time - what iOS sends for a screen that is
    /// mostly still - onto an ordinary sixty hertz display.
    /// </summary>
    [Fact]
    public void SpreadsClumpedArrivalsBackOut()
    {
        var arrivals = new List<long>();
        for (var pair = 0; pair < 120; pair++)
        {
            arrivals.Add(pair * 64L);
            arrivals.Add(pair * 64L + 1);
        }

        var paced = Simulate(arrivals, refreshMs: 16, paced: true);
        var naive = Simulate(arrivals, refreshMs: 16, paced: false);

        // Every picture 32 ms after the last, which is the rate the phone sent them at.
        Assert.Equal(32, paced.Shortest);
        Assert.Equal(32, paced.Longest);

        // Showing each frame as it turns up puts the clumping on the screen: the two frames
        // of a pair are drawn one refresh apart and the next pair is three refreshes later.
        Assert.Equal(16, naive.Shortest);
        Assert.Equal(48, naive.Longest);
    }

    /// <summary>
    /// Sixty frames a second with the arrival times scattered by up to twelve milliseconds,
    /// which is an ordinary Wi-Fi link.
    /// </summary>
    [Fact]
    public void AbsorbsArrivalJitterRatherThanPassingItOn()
    {
        var random = new Random(7);
        var arrivals = new List<long>();
        for (int i = 0, at = 0; i < 300; i++, at += 16) arrivals.Add(at + random.Next(0, 25));
        arrivals.Sort();

        var paced = Simulate(arrivals, refreshMs: 16, paced: true);
        var naive = Simulate(arrivals, refreshMs: 16, paced: false);

        Assert.Equal(0, paced.Spread);
        Assert.True(naive.Spread >= 16, $"the unpaced baseline was not uneven enough to be worth comparing against ({naive.Spread} ms)");

        // And it is not evenness bought by throwing frames away: pacing shows more of them,
        // because the cushion holds the ones a clump would otherwise have overwritten.
        Assert.True(paced.Shown >= naive.Shown,
            $"pacing showed {paced.Shown} pictures against the baseline's {naive.Shown}");
    }

    [Fact]
    public void DiscardsTheOldestRatherThanGrowingWithoutBound()
    {
        using var pacer = new FramePacer();

        // Nothing is ever dequeued here, so every frame past the ceiling has to go.
        for (var i = 0; i < 50; i++) pacer.Enqueue(Frame(), i * 16 * Tick);

        Assert.True(pacer.Depth <= 10, $"the cushion grew to {pacer.Depth}");
        Assert.Equal(50 - pacer.Depth, pacer.DroppedFrameCount);
    }

    [Fact]
    public void MeasuresTheSourceRateThroughTheClumping()
    {
        using var pacer = new FramePacer();

        for (var burst = 0; burst < 30; burst++)
            for (var inBurst = 0; inBurst < 3; inBurst++)
                pacer.Enqueue(Frame(), (burst * 48L + inBurst) * Tick);

        // Three frames per 48 ms is 62.5 a second. Averaging the gaps between consecutive
        // arrivals would report several hundred instead, two thirds of them being the 1 ms
        // inside a burst.
        Assert.InRange(pacer.SourceRate, 58, 67);
    }

    [Fact]
    public void StartsAgainAfterALongStallInsteadOfDumpingTheBacklog()
    {
        using var pacer = new FramePacer();
        var refresh = 16 * Tick;
        var now = 0L;

        for (var i = 0; i < 40; i++)
        {
            pacer.Enqueue(Frame(), now);
            pacer.TryDequeue(now, refresh)?.Dispose();
            now += refresh;
        }

        // The window was dragged, or the machine slept: two seconds pass with no composition
        // at all. Walking the schedule forward one interval at a time would then release
        // every stale picture as fast as the passes came.
        now += 2000 * Tick;
        for (var i = 0; i < 8; i++) pacer.Enqueue(Frame(), now);

        var burst = 0;
        for (var pass = 0; pass < 3; pass++)
        {
            if (pacer.TryDequeue(now, refresh) is not { } frame) continue;
            frame.Dispose();
            burst++;
        }

        Assert.True(burst <= 1, $"{burst} frames were released in three consecutive passes");
    }

    /// <summary>What a run produced: how many pictures were shown, and how evenly.</summary>
    private readonly record struct Result(int Shown, long Shortest, long Longest)
    {
        public long Spread => Longest - Shortest;
    }

    /// <summary>
    /// Steps a composition clock over a list of arrival times and reports when pictures
    /// reached the screen.
    /// </summary>
    /// <param name="arrivalsMs">When each decoded picture was handed over.</param>
    /// <param name="refreshMs">Composition period.</param>
    /// <param name="paced">
    /// False for the baseline: a two-deep hand-off queue drained one picture per composition
    /// pass, which is the shape of every renderer that has no clock of its own.
    /// </param>
    private static Result Simulate(IReadOnlyList<long> arrivalsMs, long refreshMs, bool paced)
    {
        using var pacer = new FramePacer();
        var baseline = new Queue<DecodedVideoFrame>();
        var presented = new List<long>();
        var refresh = refreshMs * Tick;
        var next = 0;

        for (var now = 0L; now <= arrivalsMs[^1] * Tick + 200 * Tick; now += refresh)
        {
            while (next < arrivalsMs.Count && arrivalsMs[next] * Tick <= now)
            {
                var frame = Frame();
                if (paced)
                {
                    pacer.Enqueue(frame, arrivalsMs[next] * Tick);
                }
                else
                {
                    baseline.Enqueue(frame);
                    while (baseline.Count > 2) baseline.Dequeue().Dispose();
                }
                next++;
            }

            if (paced)
            {
                if (pacer.TryDequeue(now, refresh) is not { } frame) continue;
                frame.Dispose();
            }
            else
            {
                if (baseline.Count == 0) continue;
                baseline.Dequeue().Dispose();
            }

            presented.Add(now);
        }

        while (baseline.Count > 0) baseline.Dequeue().Dispose();

        // The cushion has to fill and the source rate has to be measured before pacing means
        // anything, so the steady state is what is judged, not the first two seconds.
        var steady = presented.Where(at => at >= 2000 * Tick).ToList();
        Assert.True(steady.Count > 20, $"the simulation did not reach a steady state ({steady.Count} presentations)");

        var gaps = Enumerable.Range(1, steady.Count - 1)
            .Select(i => (steady[i] - steady[i - 1]) / Tick)
            .ToList();

        return new Result(steady.Count, gaps.Min(), gaps.Max());
    }
}
