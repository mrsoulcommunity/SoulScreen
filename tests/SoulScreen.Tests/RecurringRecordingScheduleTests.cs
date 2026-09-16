using SoulScreen.App.Logic;

namespace SoulScreen.Tests;

/// <summary>
/// The recurring-recording deadline: computed fresh from the clock every time, on the same
/// "moment in time, not a countdown" principle as <see cref="RecordingSchedule"/> and
/// <see cref="RecordingTimer"/>, so a rule edited mid-week or a paused tick cannot drift it.
/// </summary>
public class RecurringRecordingScheduleTests
{
    private static readonly DateTime Monday900 = new(2026, 1, 5, 9, 0, 0, DateTimeKind.Utc); // a Monday

    [Fact]
    public void NoDaysNeverFires()
    {
        Assert.Null(RecurringRecordingSchedule.NextOccurrence(RecordingDays.None, new TimeOnly(9, 0), Monday900));
    }

    [Fact]
    public void TheSameDayLaterTodayIsTheNextOccurrence()
    {
        var after = Monday900; // Monday 09:00:00
        var next = RecurringRecordingSchedule.NextOccurrence(RecordingDays.Monday, new TimeOnly(14, 30), after);
        Assert.Equal(new DateTime(2026, 1, 5, 14, 30, 0, DateTimeKind.Utc), next);
    }

    [Fact]
    public void ASingleDayAlreadyPassedTodayWaitsAFullWeek()
    {
        // Monday 09:00:00 exactly: the slot is not strictly after "now", so it must not fire
        // again until next Monday.
        var next = RecurringRecordingSchedule.NextOccurrence(RecordingDays.Monday, new TimeOnly(9, 0), Monday900);
        Assert.Equal(new DateTime(2026, 1, 12, 9, 0, 0, DateTimeKind.Utc), next);
    }

    [Fact]
    public void PicksTheNearestOfSeveralDays()
    {
        // Monday 09:00:00; Wed and Fri at 08:00 - Wednesday is nearer than Friday.
        var next = RecurringRecordingSchedule.NextOccurrence(
            RecordingDays.Wednesday | RecordingDays.Friday, new TimeOnly(8, 0), Monday900);
        Assert.Equal(new DateTime(2026, 1, 7, 8, 0, 0, DateTimeKind.Utc), next);
    }

    [Fact]
    public void WeekdaysSkipsTheWeekend()
    {
        // Friday 09:00:00 - the next weekday slot at 09:00 is the following Monday.
        var friday = new DateTime(2026, 1, 9, 9, 0, 0, DateTimeKind.Utc);
        var next = RecurringRecordingSchedule.NextOccurrence(RecordingDays.Weekdays, new TimeOnly(9, 0), friday);
        Assert.Equal(new DateTime(2026, 1, 12, 9, 0, 0, DateTimeKind.Utc), next);
    }

    [Fact]
    public void EveryDayFiresTomorrowWhenTodaysSlotHasPassed()
    {
        var after = new DateTime(2026, 1, 5, 23, 0, 0, DateTimeKind.Utc);
        var next = RecurringRecordingSchedule.NextOccurrence(RecordingDays.All, new TimeOnly(9, 0), after);
        Assert.Equal(new DateTime(2026, 1, 6, 9, 0, 0, DateTimeKind.Utc), next);
    }

    [Fact]
    public void IncludesReadsEachDaysFlag()
    {
        Assert.True(RecurringRecordingSchedule.Includes(RecordingDays.Weekends, DayOfWeek.Saturday));
        Assert.True(RecurringRecordingSchedule.Includes(RecordingDays.Weekends, DayOfWeek.Sunday));
        Assert.False(RecurringRecordingSchedule.Includes(RecordingDays.Weekends, DayOfWeek.Monday));
    }

    [Fact]
    public void WithTogglesOneDayWithoutTouchingTheRest()
    {
        var days = RecurringRecordingSchedule.With(RecordingDays.None, DayOfWeek.Tuesday, true);
        Assert.Equal(RecordingDays.Tuesday, days);

        days = RecurringRecordingSchedule.With(days, DayOfWeek.Friday, true);
        Assert.True(RecurringRecordingSchedule.Includes(days, DayOfWeek.Tuesday));
        Assert.True(RecurringRecordingSchedule.Includes(days, DayOfWeek.Friday));

        days = RecurringRecordingSchedule.With(days, DayOfWeek.Tuesday, false);
        Assert.False(RecurringRecordingSchedule.Includes(days, DayOfWeek.Tuesday));
        Assert.True(RecurringRecordingSchedule.Includes(days, DayOfWeek.Friday));
    }

    [Theory]
    [InlineData(RecordingDays.None, "No days chosen")]
    [InlineData(RecordingDays.All, "Every day")]
    [InlineData(RecordingDays.Weekdays, "Weekdays")]
    [InlineData(RecordingDays.Weekends, "Weekends")]
    public void DescribeNamesTheCommonCases(RecordingDays days, string expected)
    {
        Assert.Equal(expected, RecurringRecordingSchedule.Describe(days));
    }

    [Fact]
    public void DescribeListsIndividualDaysInWeekOrder()
    {
        var days = RecordingDays.Friday | RecordingDays.Monday | RecordingDays.Wednesday;
        Assert.Equal("Mon, Wed, Fri", RecurringRecordingSchedule.Describe(days));
    }
}
