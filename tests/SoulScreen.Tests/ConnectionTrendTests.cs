using SoulScreen.App.Logic;

namespace SoulScreen.Tests;

/// <summary>
/// The early-warning trend: a first-half-versus-second-half comparison over a short run of
/// samples, looking for frame rate sliding down or round-trip time climbing - worth a word
/// before the ordinary quality meter has anything to react to, and not fooled by ordinary
/// noise that never actually gets anywhere.
/// </summary>
public class ConnectionTrendTests
{
    [Fact]
    public void FewerThanTheMinimumSamplesIsNeverADecline()
    {
        // Five samples, unmistakably declining, but under the floor that makes a trend real.
        Assert.False(ConnectionTrend.IsDeclining([60, 55, 50, 45, 40], higherIsWorse: false, 0.1));
    }

    [Fact]
    public void AFallingFrameRateIsDetected()
    {
        // 60 -> ~30: a clean halving across the window.
        double[] samples = [60, 59, 58, 32, 31, 30];
        Assert.True(ConnectionTrend.IsDeclining(samples, higherIsWorse: false, 0.25));
    }

    [Fact]
    public void ARisingFrameRateIsNotADecline()
    {
        double[] samples = [30, 31, 32, 58, 59, 60];
        Assert.False(ConnectionTrend.IsDeclining(samples, higherIsWorse: false, 0.25));
    }

    /// <summary>Ordinary session noise - a frame rate that wobbles a little either way but
    /// never actually gets anywhere - must not read as a decline.</summary>
    [Fact]
    public void NoisySamplesWithNoRealTrendAreNotADecline()
    {
        double[] samples = [59, 61, 58, 60, 59, 61, 58, 60];
        Assert.False(ConnectionTrend.IsDeclining(samples, higherIsWorse: false, 0.25));
    }

    [Fact]
    public void ARisingRoundTripIsDetectedWhenHigherIsWorse()
    {
        double[] samples = [20, 21, 19, 55, 58, 60];
        Assert.True(ConnectionTrend.IsDeclining(samples, higherIsWorse: true, 0.5));
    }

    [Fact]
    public void AFallingRoundTripIsNotADeclineWhenHigherIsWorse()
    {
        double[] samples = [60, 58, 55, 21, 19, 20];
        Assert.False(ConnectionTrend.IsDeclining(samples, higherIsWorse: true, 0.5));
    }

    /// <summary>A decline smaller than the threshold is left alone - the whole point of the
    /// threshold is to ask for a clear signal, not any drop at all.</summary>
    [Fact]
    public void ADeclineSmallerThanTheThresholdIsIgnored()
    {
        double[] samples = [60, 60, 60, 56, 56, 56]; // ~6.7% drop
        Assert.False(ConnectionTrend.IsDeclining(samples, higherIsWorse: false, 0.25));
    }

    [Fact]
    public void AZeroBaselineWithAnyRiseCountsAsDeclineWhenHigherIsWorse()
    {
        double[] samples = [0, 0, 0, 5, 5, 5];
        Assert.True(ConnectionTrend.IsDeclining(samples, higherIsWorse: true, 0.5));
    }

    [Fact]
    public void AllZerosIsNeverADecline()
    {
        double[] samples = [0, 0, 0, 0, 0, 0];
        Assert.False(ConnectionTrend.IsDeclining(samples, higherIsWorse: false, 0.25));
        Assert.False(ConnectionTrend.IsDeclining(samples, higherIsWorse: true, 0.25));
    }
}

/// <summary>The watcher that turns a decline into advice offered once per stretch, the same
/// hush-and-recover shape <see cref="ConnectionAdvice"/> uses.</summary>
public class ConnectionTrendWatcherTests
{
    private static readonly TimeSpan Step = TimeSpan.FromSeconds(1);

    /// <summary>Feeds a steady frame rate for a while, so the window has enough samples to
    /// judge, without itself producing a trend.</summary>
    private static TimeSpan Warm(ConnectionTrendWatcher watcher, TimeSpan t, double frameRate, int ticks)
    {
        for (var i = 0; i < ticks; i++)
        {
            t += Step;
            watcher.Sample(t, frameRate, null);
        }
        return t;
    }

    [Fact]
    public void TooFewSamplesNeverAdvises()
    {
        var watcher = new ConnectionTrendWatcher();
        var t = TimeSpan.Zero;
        double[] falling = [60, 50, 40];
        foreach (var rate in falling)
        {
            t += Step;
            Assert.False(watcher.Sample(t, rate, null));
        }
    }

    [Fact]
    public void ASustainedFallInFrameRateIsAdvisedOnce()
    {
        var watcher = new ConnectionTrendWatcher();
        var t = Warm(watcher, TimeSpan.Zero, 60, 3);

        var advised = false;
        foreach (var rate in new double[] { 45, 40, 35, 30, 28 })
        {
            t += Step;
            advised |= watcher.Sample(t, rate, null);
        }
        Assert.True(advised);

        // Still declining, same stretch: not repeated.
        t += Step;
        Assert.False(watcher.Sample(t, 25, null));
    }

    [Fact]
    public void ARisingRoundTripAloneIsAdvised()
    {
        var watcher = new ConnectionTrendWatcher();
        var t = TimeSpan.Zero;
        // Steady frame rate throughout; only the round-trip climbs.
        foreach (var rtt in new double?[] { 20, 20, 20 })
        {
            t += Step;
            watcher.Sample(t, 60, rtt);
        }

        var advised = false;
        foreach (var rtt in new double?[] { 40, 60, 80, 100, 120 })
        {
            t += Step;
            advised |= watcher.Sample(t, 60, rtt);
        }
        Assert.True(advised);
    }

    [Fact]
    public void RecoveryClearsTheHushForFreshAdvice()
    {
        var watcher = new ConnectionTrendWatcher();
        var t = Warm(watcher, TimeSpan.Zero, 60, 3);

        var advised = false;
        foreach (var rate in new double[] { 45, 40, 35, 30, 28 })
        {
            t += Step;
            advised |= watcher.Sample(t, rate, null);
        }
        Assert.True(advised);

        // A long steady stretch at the lower rate: recovered, even though it never went
        // back up - "stopped getting worse for a while" is what clears the hush.
        t = Warm(watcher, t, 28, 40);

        // Declining again: worth a fresh word.
        var advisedAgain = false;
        foreach (var rate in new double[] { 20, 16, 12, 8, 5 })
        {
            t += Step;
            advisedAgain |= watcher.Sample(t, rate, null);
        }
        Assert.True(advisedAgain);
    }

    [Fact]
    public void ResetForgetsEverything()
    {
        var watcher = new ConnectionTrendWatcher();
        var t = Warm(watcher, TimeSpan.Zero, 60, 3);
        foreach (var rate in new double[] { 45, 40, 35, 30, 28 })
        {
            t += Step;
            watcher.Sample(t, rate, null);
        }

        watcher.Reset();
        Assert.False(watcher.Sample(TimeSpan.Zero, 10, null));
    }
}
