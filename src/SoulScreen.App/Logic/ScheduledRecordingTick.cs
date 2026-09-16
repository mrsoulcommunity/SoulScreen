namespace SoulScreen.App.Logic;

/// <summary>What a metrics tick should do about an armed scheduled recording start.</summary>
public enum ScheduledStartAction
{
    /// <summary>Not due yet: the schedule stays armed and the tick does nothing.</summary>
    Wait,

    /// <summary>The schedule came due and a recording can begin: the caller toggles its
    /// record control on - which starts the recording - and arms the automatic stop when
    /// the schedule carried a duration.</summary>
    Start,

    /// <summary>The schedule came due while a recording was already running, by hand or by
    /// another path: drop it silently, starting a second recording would be wrong.</summary>
    DropAlreadyRecording,

    /// <summary>The schedule came due with nothing connected to record into: announce the
    /// miss and drop it. It is never left armed for whatever connects next, which nobody
    /// asked for.</summary>
    DropNothingConnected,
}

/// <summary>
/// The metrics tick's decision about an armed scheduled start, split out of the window so
/// that "a due schedule actually begins a recording" can be tested without WPF. The window
/// keeps the side effects - toggling the record control, arming the timed stop, the toasts -
/// and this class keeps the branching, which is the part a regression hides.
/// <para>
/// Pure: it reads its arguments and changes nothing, and a due schedule is consumed on
/// every outcome but <see cref="ScheduledStartAction.Wait"/> - a missed start is announced,
/// not retried forever.
/// </para>
/// </summary>
public static class ScheduledRecordingTick
{
    /// <summary>Decides what the tick owes an armed schedule at <paramref name="nowUtc"/>.
    /// <paramref name="autoStop"/> carries the schedule's duration when the answer is
    /// <see cref="ScheduledStartAction.Start"/>, for the caller to arm as a timed stop.</summary>
    public static ScheduledStartAction Decide(
        RecordingSchedule? schedule,
        DateTime nowUtc,
        bool alreadyRecording,
        bool canRecord,
        out TimeSpan? autoStop)
    {
        autoStop = null;
        if (schedule is not { } armed || !armed.IsDue(nowUtc)) return ScheduledStartAction.Wait;
        if (alreadyRecording) return ScheduledStartAction.DropAlreadyRecording;
        if (!canRecord) return ScheduledStartAction.DropNothingConnected;
        autoStop = armed.Duration;
        return ScheduledStartAction.Start;
    }
}
