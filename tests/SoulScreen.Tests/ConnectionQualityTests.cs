using SoulScreen.App.Logic;

namespace SoulScreen.Tests;

/// <summary>
/// The toolbar's connection bars: judged from losses a person would notice, counted once per
/// moment however large, forgotten after the window, and silent until a session has been seen.
/// </summary>
public class ConnectionQualityTests
{
    private static TimeSpan At(double seconds) => TimeSpan.FromSeconds(seconds);

    /// <summary>Feeds half-second ticks from <paramref name="from"/> to <paramref name="to"/>
    /// with unchanged totals, returning the last verdict.</summary>
    private static ConnectionQualityLevel Idle(ConnectionQualityMeter meter, double from, double to, long video, long audio)
    {
        var level = ConnectionQualityLevel.Unknown;
        for (var t = from; t <= to + 1e-9; t += 0.5) level = meter.Sample(At(t), video, audio);
        return level;
    }

    [Fact]
    public void NothingIsSaidBeforeTheWarmup()
    {
        var meter = new ConnectionQualityMeter();
        Assert.Equal(ConnectionQualityLevel.Unknown, meter.Sample(At(0), 0, 0));
        Assert.Equal(ConnectionQualityLevel.Unknown, meter.Sample(At(1.5), 0, 0));
        Assert.Equal(ConnectionQualityLevel.Good, meter.Sample(At(2), 0, 0));
    }

    [Fact]
    public void ASessionThatLosesNothingIsGood()
    {
        var meter = new ConnectionQualityMeter();
        Assert.Equal(ConnectionQualityLevel.Good, Idle(meter, 0, 30, 0, 0));
    }

    [Fact]
    public void TotalsCarriedInFromBeforeTheSessionAreNotLosses()
    {
        // The first sample is only a baseline, whatever it holds.
        var meter = new ConnectionQualityMeter();
        meter.Sample(At(0), 500, 40);
        Assert.Equal(ConnectionQualityLevel.Good, Idle(meter, 0.5, 5, 500, 40));
    }

    [Fact]
    public void OneFreezeIsFairHoweverManyFramesItCost()
    {
        var meter = new ConnectionQualityMeter();
        Idle(meter, 0, 5, 0, 0);
        Assert.Equal(ConnectionQualityLevel.Fair, meter.Sample(At(5.5), 90, 0));
        Assert.Equal(1, meter.VideoEvents);
    }

    [Fact]
    public void TwoFreezesInTheWindowArePoor()
    {
        var meter = new ConnectionQualityMeter();
        Idle(meter, 0, 5, 0, 0);
        meter.Sample(At(5.5), 30, 0);
        Idle(meter, 6, 8, 30, 0);
        Assert.Equal(ConnectionQualityLevel.Poor, meter.Sample(At(8.5), 60, 0));
    }

    [Fact]
    public void ALossIsForgottenOnceTheWindowHasPassed()
    {
        var meter = new ConnectionQualityMeter();
        Idle(meter, 0, 5, 0, 0);
        meter.Sample(At(5.5), 30, 0);
        Assert.Equal(ConnectionQualityLevel.Fair, Idle(meter, 6, 15, 30, 0));
        Assert.Equal(ConnectionQualityLevel.Good, Idle(meter, 15.5, 16, 30, 0));
    }

    [Fact]
    public void SoundBreakingUpRepeatedlyIsPoorButAClickIsNot()
    {
        var meter = new ConnectionQualityMeter();
        Idle(meter, 0, 3, 0, 0);
        Assert.Equal(ConnectionQualityLevel.Good, meter.Sample(At(3.5), 0, 1));

        var audio = 1L;
        var level = ConnectionQualityLevel.Unknown;
        for (var t = 4.0; t <= 8; t += 1) level = meter.Sample(At(t), 0, ++audio);
        Assert.Equal(ConnectionQualityLevel.Poor, level);
    }

    [Fact]
    public void AFallingTotalIsANewPipelineNotALoss()
    {
        var meter = new ConnectionQualityMeter();
        Idle(meter, 0, 3, 200, 10);
        Assert.Equal(ConnectionQualityLevel.Good, meter.Sample(At(3.5), 0, 0));
        Assert.Equal(0, meter.VideoEvents);
    }

    [Fact]
    public void ResetStartsTheWarmupAgain()
    {
        var meter = new ConnectionQualityMeter();
        Idle(meter, 0, 5, 0, 0);
        meter.Sample(At(5.5), 50, 0);
        meter.Reset();
        Assert.Equal(ConnectionQualityLevel.Unknown, meter.Sample(At(6), 50, 0));
        Assert.Equal(ConnectionQualityLevel.Good, Idle(meter, 6.5, 8, 50, 0));
    }

    [Theory]
    [InlineData(0, 0, ConnectionQualityLevel.Good)]
    [InlineData(0, 1, ConnectionQualityLevel.Good)]
    [InlineData(0, 2, ConnectionQualityLevel.Fair)]
    [InlineData(1, 0, ConnectionQualityLevel.Fair)]
    [InlineData(2, 0, ConnectionQualityLevel.Poor)]
    [InlineData(0, 5, ConnectionQualityLevel.Poor)]
    public void TheThresholdsAreTheOnesDescribed(int video, int audio, ConnectionQualityLevel expected)
    {
        Assert.Equal(expected, ConnectionQualityMeter.Judge(video, audio));
    }

    [Fact]
    public void EveryLevelHasWords()
    {
        foreach (var level in Enum.GetValues<ConnectionQualityLevel>())
            Assert.False(string.IsNullOrWhiteSpace(ConnectionQualityMeter.Describe(level)));
    }
}
