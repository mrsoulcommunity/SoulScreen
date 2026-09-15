using SoulScreen.App.Logic;

namespace SoulScreen.Tests;

public class SessionSummaryTests
{
    [Fact]
    public void HeadlineNamesTheMirrorAndItsLength()
    {
        var summary = new SessionSummary("Kasra's iPhone", TimeSpan.FromMinutes(4) + TimeSpan.FromSeconds(12),
            0, 0, 0, HadDemo: false, SessionEndReason.PhoneEnded);
        Assert.Equal("Mirrored for 4m 12s", summary.Headline);
    }

    [Fact]
    public void DemoHeadlineSaysSo()
    {
        var summary = new SessionSummary("Demo", TimeSpan.FromSeconds(30),
            0, 0, 0, HadDemo: true, SessionEndReason.PhoneEnded);
        Assert.Equal("Demo ran for 30s", summary.Headline);
    }

    [Fact]
    public void EndingHereIsNamed()
    {
        var stopped = new SessionSummary("iPhone", TimeSpan.FromMinutes(10),
            0, 0, 0, HadDemo: false, SessionEndReason.StoppedByUser);
        var faulted = new SessionSummary("iPhone", TimeSpan.FromMinutes(10),
            0, 0, 0, HadDemo: false, SessionEndReason.Faulted);
        var phone = new SessionSummary("iPhone", TimeSpan.FromMinutes(10),
            0, 0, 0, HadDemo: false, SessionEndReason.PhoneEnded);
        var taken = new SessionSummary("iPhone", TimeSpan.FromMinutes(10),
            0, 0, 0, HadDemo: false, SessionEndReason.TakenOver);
        Assert.Contains("ended here", stopped.Headline);
        Assert.Contains("ended here", faulted.Headline);
        Assert.DoesNotContain("ended here", phone.Headline);
        Assert.DoesNotContain("ended here", taken.Headline);
    }

    [Fact]
    public void DetailCountsCaptures()
    {
        var summary = new SessionSummary("iPhone", TimeSpan.FromMinutes(5),
            2, 1, 90_000_000, HadDemo: false, SessionEndReason.PhoneEnded);
        Assert.Equal("2 screenshots · 1 recording · 85.8 MB", summary.Detail);
    }

    [Fact]
    public void DetailIgnoresSmallRecordings()
    {
        var summary = new SessionSummary("iPhone", TimeSpan.FromMinutes(5),
            0, 1, 500_000, HadDemo: false, SessionEndReason.PhoneEnded);
        Assert.Equal("1 recording", summary.Detail);
    }

    [Fact]
    public void DetailIsNullWhenNothingHappened()
    {
        var summary = new SessionSummary("iPhone", TimeSpan.FromMinutes(5),
            0, 0, 0, HadDemo: false, SessionEndReason.PhoneEnded);
        Assert.Null(summary.Detail);
    }

    [Fact]
    public void DeservesToastRules()
    {
        // A short look at the idle screen ends quietly.
        var short_ = new SessionSummary("iPhone", TimeSpan.FromSeconds(10),
            0, 0, 0, HadDemo: false, SessionEndReason.PhoneEnded);
        Assert.False(short_.DeservesToast);

        // A real session says something.
        var long_ = new SessionSummary("iPhone", TimeSpan.FromMinutes(3),
            0, 0, 0, HadDemo: false, SessionEndReason.PhoneEnded);
        Assert.True(long_.DeservesToast);

        // Anything produced says so, however briefly.
        var withCapture = new SessionSummary("iPhone", TimeSpan.FromSeconds(20),
            1, 0, 0, HadDemo: false, SessionEndReason.PhoneEnded);
        Assert.True(withCapture.DeservesToast);

        // A fault always gets a word in.
        var faulted = new SessionSummary("iPhone", TimeSpan.FromSeconds(5),
            0, 0, 0, HadDemo: false, SessionEndReason.Faulted);
        Assert.True(faulted.DeservesToast);
    }

    [Fact]
    public void SizesReadInMegabytesThenGigabytes()
    {
        var mb = 1024 * 1024;
        Assert.Equal("3 MB", SessionSummary.DescribeSize(3 * mb));
        Assert.Equal("3.4 MB", SessionSummary.DescribeSize((long)(3.4 * mb)));
        Assert.Equal("1.2 GB", SessionSummary.DescribeSize((long)(1.2 * 1024 * mb)));
    }
}

public class SessionTallyTests
{
    [Fact]
    public void CountsScreenshotsAndRecordings()
    {
        var tally = new SessionTally();
        tally.AddScreenshot();
        tally.AddScreenshot();
        tally.AddRecording(1_000_000);
        tally.AddRecording(2_500_000);
        Assert.Equal(2, tally.Screenshots);
        Assert.Equal(2, tally.Recordings);
        Assert.Equal(3_500_000, tally.RecordedBytes);
    }

    [Fact]
    public void NegativeBytesAreCountedAsNone()
    {
        var tally = new SessionTally();
        tally.AddRecording(-5);
        Assert.Equal(1, tally.Recordings);
        Assert.Equal(0, tally.RecordedBytes);
    }

    [Fact]
    public void ClearEmptiesEverything()
    {
        var tally = new SessionTally();
        tally.AddScreenshot();
        tally.AddRecording(1_000_000);
        tally.Clear();
        Assert.Equal(0, tally.Screenshots);
        Assert.Equal(0, tally.Recordings);
        Assert.Equal(0, tally.RecordedBytes);
    }
}

public class RepeatRateTests
{
    [Fact]
    public void FirstRepeatWaitsOutTheQuietPeriod()
    {
        // Just after the press: not yet repeating.
        Assert.Null(RepeatRate.NextDelay(TimeSpan.FromMilliseconds(100)));
        Assert.Null(RepeatRate.NextDelay(TimeSpan.Zero));
    }

    [Fact]
    public void NegativeHeldTimeIsTreatedAsTheStart()
    {
        Assert.Equal(RepeatRate.SlowPeriod, RepeatRate.NextDelay(TimeSpan.FromMilliseconds(-50)));
    }

    [Fact]
    public void RepeatsBeginSlowThenQuick()
    {
        var first = RepeatRate.NextDelay(RepeatRate.SlowPeriod);
        Assert.Equal(TimeSpan.FromMilliseconds(RepeatRate.SlowStepMilliseconds), first);

        var stillSlow = RepeatRate.NextDelay(RepeatRate.SlowPeriod + TimeSpan.FromMilliseconds(50));
        Assert.Equal(TimeSpan.FromMilliseconds(RepeatRate.SlowStepMilliseconds), stillSlow);

        var fast = RepeatRate.NextDelay(RepeatRate.SlowPeriod + TimeSpan.FromSeconds(2));
        Assert.Equal(TimeSpan.FromMilliseconds(RepeatRate.FastStepMilliseconds), fast);
    }
}

public class ConnectionAdviceTests
{
    private static readonly TimeSpan Step = TimeSpan.FromSeconds(1);

    [Fact]
    public void OnePoorTickIsNotAdvice()
    {
        var advice = new ConnectionAdvice();
        Assert.False(advice.ShouldAdvise(TimeSpan.Zero, poor: true));
        Assert.False(advice.ShouldAdvise(Step, poor: true));
    }

    [Fact]
    public void SustainedPoorIsAdvisedOnce()
    {
        var advice = new ConnectionAdvice();
        var t = TimeSpan.Zero;

        // Poor from the first tick: the streak starts there.
        while (t < ConnectionAdvice.PoorStreakNeeded)
        {
            t += Step;
            Assert.False(advice.ShouldAdvise(t, poor: true));
        }

        // One more tick and the window has fully elapsed since the first poor sample.
        t += Step;
        Assert.True(advice.ShouldAdvise(t, poor: true));

        // Still poor: nothing more, this stretch having had its word.
        t += Step;
        Assert.False(advice.ShouldAdvise(t, poor: true));
        t += TimeSpan.FromMinutes(1);
        Assert.False(advice.ShouldAdvise(t, poor: true));
    }

    [Fact]
    public void BriefGoodBreakDoesNotRepeatTheAdvice()
    {
        var advice = new ConnectionAdvice();
        var t = TimeSpan.Zero;
        while (t < ConnectionAdvice.PoorStreakNeeded) { t += Step; advice.ShouldAdvise(t, poor: true); }
        t += Step;
        Assert.True(advice.ShouldAdvise(t, poor: true));

        // Recovered briefly.
        t += Step;
        advice.ShouldAdvise(t, poor: false);
        t += TimeSpan.FromSeconds(10);
        advice.ShouldAdvise(t, poor: false);

        // Poor again: still inside the hush, and the recovery was too short to clear it.
        t += Step;
        Assert.False(advice.ShouldAdvise(t, poor: true));
        t += TimeSpan.FromMinutes(2);
        Assert.False(advice.ShouldAdvise(t, poor: true));
    }

    [Fact]
    public void LongRecoveryFindsFreshAdviceAgain()
    {
        var advice = new ConnectionAdvice();
        var t = TimeSpan.Zero;
        while (t < ConnectionAdvice.PoorStreakNeeded) { t += Step; advice.ShouldAdvise(t, poor: true); }
        t += Step;
        Assert.True(advice.ShouldAdvise(t, poor: true));

        // A good stretch long enough to count as recovered.
        t += Step;
        advice.ShouldAdvise(t, poor: false);
        t += ConnectionAdvice.Recovery + TimeSpan.FromSeconds(10);
        advice.ShouldAdvise(t, poor: false);

        // Trouble again, for long enough: worth naming once more.
        var before = t;
        while (t < before + ConnectionAdvice.PoorStreakNeeded) { t += Step; advice.ShouldAdvise(t, poor: true); }
        t += Step;
        Assert.True(advice.ShouldAdvise(t, poor: true));
    }

    [Fact]
    public void ResetStartsOver()
    {
        var advice = new ConnectionAdvice();
        var t = TimeSpan.Zero;
        while (t < ConnectionAdvice.PoorStreakNeeded) { t += Step; advice.ShouldAdvise(t, poor: true); }
        t += Step;
        Assert.True(advice.ShouldAdvise(t, poor: true));

        advice.Reset();
        Assert.False(advice.ShouldAdvise(TimeSpan.Zero, poor: true));
        Assert.False(advice.ShouldAdvise(Step, poor: true));
    }
}
