namespace SoulScreen.App.Logic;

/// <summary>The days of the week a recurring recording runs on, as flags so a rule can name
/// any combination - "weekdays", "weekends", a single day, or every day.</summary>
[Flags]
public enum RecordingDays
{
    None = 0,
    Sunday = 1 << 0,
    Monday = 1 << 1,
    Tuesday = 1 << 2,
    Wednesday = 1 << 3,
    Thursday = 1 << 4,
    Friday = 1 << 5,
    Saturday = 1 << 6,

    Weekdays = Monday | Tuesday | Wednesday | Thursday | Friday,
    Weekends = Saturday | Sunday,
    All = Weekdays | Weekends,
}

/// <summary>
/// One "record every Mon/Wed/Fri at 09:00 for 30 minutes" rule.
/// <para>
/// Kept apart from the one-shot <see cref="RecordingSchedule"/>, which fires once. A rule
/// itself carries no notion of the next time it fires - that is computed fresh from the
/// clock whenever it is needed, by <see cref="RecurringRecordingSchedule"/>, so a rule edited
/// or the system clock changed cannot leave a stale deadline lying around.
/// </para>
/// </summary>
public sealed class RecurringRecordingRule
{
    /// <summary>Stable identity, since a rule is found again by it rather than by position
    /// in the list - edited, toggled or removed without disturbing the others.</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>Off without being removed, for a schedule kept for later.</summary>
    public bool Enabled { get; set; } = true;

    public RecordingDays Days { get; set; } = RecordingDays.Weekdays;

    /// <summary>Local wall-clock time the recording starts.</summary>
    public TimeOnly StartTime { get; set; }

    /// <summary>How long it runs once started; zero or less means no automatic stop.</summary>
    public int DurationMinutes { get; set; }
}

/// <summary>
/// Works out when a recurring rule next fires. Pure arithmetic on whatever clock the caller
/// passes in - local time, since "every Monday at 09:00" is inherently a wall-clock idea -
/// so it can be tested without a clock, a timer or a phone.
/// </summary>
public static class RecurringRecordingSchedule
{
    /// <summary>
    /// The next moment strictly after <paramref name="after"/> that a rule running on
    /// <paramref name="days"/> at <paramref name="timeOfDay"/> should fire, in the same
    /// clock <paramref name="after"/> is in. Strictly after, not at or after, so a rule that
    /// just fired this instant is not read as due again for the same slot.
    /// </summary>
    /// <returns>Null when <paramref name="days"/> names no day at all.</returns>
    public static DateTime? NextOccurrence(RecordingDays days, TimeOnly timeOfDay, DateTime after)
    {
        if (days == RecordingDays.None) return null;

        // Today first, then each day out to a full week later - which guarantees landing on
        // every day named at least once, including "today" again if it is the only day and
        // today's slot has already gone.
        for (var offset = 0; offset <= 7; offset++)
        {
            var date = after.Date.AddDays(offset);
            if (!Includes(days, date.DayOfWeek)) continue;

            var candidate = date + timeOfDay.ToTimeSpan();
            if (candidate > after) return DateTime.SpecifyKind(candidate, after.Kind);
        }

        // Unreachable: every day of the week appears within 7 days of any start point, and
        // days is not None. Kept as a safety net rather than an assert people could hit.
        return null;
    }

    /// <summary>True when <paramref name="days"/> names <paramref name="day"/>.</summary>
    public static bool Includes(RecordingDays days, DayOfWeek day) => (days & ToFlag(day)) != 0;

    /// <summary>Sets or clears one day's bit, for a day-picker toggled one day at a time.</summary>
    public static RecordingDays With(RecordingDays days, DayOfWeek day, bool included) =>
        included ? days | ToFlag(day) : days & ~ToFlag(day);

    public static RecordingDays ToFlag(DayOfWeek day) => day switch
    {
        DayOfWeek.Sunday => RecordingDays.Sunday,
        DayOfWeek.Monday => RecordingDays.Monday,
        DayOfWeek.Tuesday => RecordingDays.Tuesday,
        DayOfWeek.Wednesday => RecordingDays.Wednesday,
        DayOfWeek.Thursday => RecordingDays.Thursday,
        DayOfWeek.Friday => RecordingDays.Friday,
        _ => RecordingDays.Saturday,
    };

    /// <summary>"Mon, Wed, Fri", "Weekdays", "Weekends", "Every day", or "No days chosen".</summary>
    public static string Describe(RecordingDays days)
    {
        if (days == RecordingDays.None) return "No days chosen";
        if (days == RecordingDays.All) return "Every day";
        if (days == RecordingDays.Weekdays) return "Weekdays";
        if (days == RecordingDays.Weekends) return "Weekends";

        var order = new[]
        {
            (RecordingDays.Monday, "Mon"), (RecordingDays.Tuesday, "Tue"), (RecordingDays.Wednesday, "Wed"),
            (RecordingDays.Thursday, "Thu"), (RecordingDays.Friday, "Fri"), (RecordingDays.Saturday, "Sat"),
            (RecordingDays.Sunday, "Sun"),
        };
        return string.Join(", ", order.Where(pair => Includes(days, ToDayOfWeek(pair.Item1))).Select(pair => pair.Item2));
    }

    private static DayOfWeek ToDayOfWeek(RecordingDays single) => single switch
    {
        RecordingDays.Monday => DayOfWeek.Monday,
        RecordingDays.Tuesday => DayOfWeek.Tuesday,
        RecordingDays.Wednesday => DayOfWeek.Wednesday,
        RecordingDays.Thursday => DayOfWeek.Thursday,
        RecordingDays.Friday => DayOfWeek.Friday,
        RecordingDays.Saturday => DayOfWeek.Saturday,
        _ => DayOfWeek.Sunday,
    };
}
