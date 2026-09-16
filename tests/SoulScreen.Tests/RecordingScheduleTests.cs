using SoulScreen.App.Logic;

namespace SoulScreen.Tests;

/// <summary>
/// The scheduled-recording deadline: computed once from a delay and a clock reading, the
/// same way <see cref="RecordingTimer"/> computes a stop, so a paused tick cannot drift it.
/// </summary>
public class RecordingScheduleTests
{
    [Fact]
    public void ComputesAStartDelayFromNow()
    {
        var now = DateTime.UtcNow;
        var schedule = RecordingSchedule.Compute(TimeSpan.FromMinutes(15), TimeSpan.Zero, now);

        Assert.Equal(now + TimeSpan.FromMinutes(15), schedule.StartAtUtc);
        Assert.Null(schedule.Duration);
        Assert.False(schedule.IsDue(now + TimeSpan.FromMinutes(14)));
        Assert.True(schedule.IsDue(now + TimeSpan.FromMinutes(15)));
    }

    [Fact]
    public void CarriesAnOptionalDurationForTheAutomaticStop()
    {
        var now = DateTime.UtcNow;
        var schedule = RecordingSchedule.Compute(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(30), now);
        Assert.Equal(TimeSpan.FromMinutes(30), schedule.Duration);
    }

    /// <summary>A delay of zero or less is "start now", not a moment already missed.</summary>
    [Fact]
    public void ANonPositiveDelayStartsImmediately()
    {
        var now = DateTime.UtcNow;
        Assert.True(RecordingSchedule.Compute(TimeSpan.Zero, TimeSpan.Zero, now).IsDue(now));
        Assert.True(RecordingSchedule.Compute(TimeSpan.FromMinutes(-5), TimeSpan.Zero, now).IsDue(now));
    }

    /// <summary>A duration of zero or less means no automatic stop, the same as a recording
    /// started from the toolbar.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-10)]
    public void ANonPositiveDurationMeansNoAutomaticStop(double minutes)
    {
        var schedule = RecordingSchedule.Compute(TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(minutes), DateTime.UtcNow);
        Assert.Null(schedule.Duration);
    }

    [Fact]
    public void DelayUntilATimeOfDayLaterTodayIsTheDifference()
    {
        var now = new DateTime(2026, 1, 1, 9, 0, 0, DateTimeKind.Utc);
        var delay = RecordingSchedule.DelayUntil(new TimeOnly(14, 30), now);
        Assert.Equal(TimeSpan.FromHours(5.5), delay);
    }

    /// <summary>A time of day already passed today means tomorrow, not a start already missed.</summary>
    [Fact]
    public void ATimeOfDayAlreadyPassedMeansTomorrow()
    {
        var now = new DateTime(2026, 1, 1, 20, 0, 0, DateTimeKind.Utc);
        var delay = RecordingSchedule.DelayUntil(new TimeOnly(9, 0), now);
        Assert.Equal(TimeSpan.FromHours(13), delay);
    }

    /// <summary>The exact moment of day counts as "now", not "already gone" - arming at
    /// 09:00:00 for 09:00 must not wait a full day.</summary>
    [Fact]
    public void TheExactMomentCountsAsNow()
    {
        var now = new DateTime(2026, 1, 1, 9, 0, 0, DateTimeKind.Utc);
        Assert.Equal(TimeSpan.Zero, RecordingSchedule.DelayUntil(new TimeOnly(9, 0), now));
    }
}
