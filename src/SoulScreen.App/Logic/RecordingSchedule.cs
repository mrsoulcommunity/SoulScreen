namespace SoulScreen.App.Logic;

/// <summary>
/// A recording that is to start itself later, and optionally stop itself again.
/// <para>
/// Built the same way <see cref="RecordingTimer"/> is: a deadline computed once from a delay
/// and a clock reading, not a countdown ticked down, so a paused metrics tick or a window
/// hidden to the tray cannot make the start early or late - whenever the tick next runs, the
/// moment has either come or it has not. Deliberately free of WPF so it can be tested alone.
/// </para>
/// </summary>
/// <param name="StartAtUtc">When the recording is to begin.</param>
/// <param name="Duration">How long it is to run once started, or null to run until it is
/// stopped by hand.</param>
public readonly record struct RecordingSchedule(DateTime StartAtUtc, TimeSpan? Duration)
{
    /// <summary>
    /// Builds a schedule <paramref name="delay"/> from now, running for
    /// <paramref name="duration"/>.
    /// <para>
    /// A delay of zero or less starts as soon as the schedule is checked, which is what
    /// "start now" means rather than a moment already missed. A duration of zero or less
    /// means no automatic stop - the recording runs until it is stopped by hand, the same as
    /// one started from the toolbar.
    /// </para>
    /// </summary>
    public static RecordingSchedule Compute(TimeSpan delay, TimeSpan duration, DateTime nowUtc)
    {
        var startAt = nowUtc + (delay > TimeSpan.Zero ? delay : TimeSpan.Zero);
        return new RecordingSchedule(startAt, duration > TimeSpan.Zero ? duration : null);
    }

    /// <summary>True once the start deadline has passed.</summary>
    public bool IsDue(DateTime nowUtc) => nowUtc >= StartAtUtc;

    /// <summary>
    /// How long from now until a wall-clock time of day, for a "start at 14:30" field: if
    /// that time has already gone today, it means tomorrow rather than a moment already
    /// missed; the exact moment itself counts as now, giving a delay of zero. Pure
    /// arithmetic on whatever clock <paramref name="now"/> is in - local or UTC - so the
    /// caller need not convert either value, and the result never depends on the machine's
    /// time zone.
    /// </summary>
    public static TimeSpan DelayUntil(TimeOnly timeOfDay, DateTime now)
    {
        var target = now.Date + timeOfDay.ToTimeSpan();
        if (target < now) target = target.AddDays(1);
        return target - now;
    }
}
