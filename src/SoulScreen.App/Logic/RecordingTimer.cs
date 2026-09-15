namespace SoulScreen.App.Logic;

/// <summary>
/// A recording that is to stop itself after a set time.
/// <para>
/// The deadline is a moment in time rather than a countdown being ticked, so pausing the
/// metrics tick - or the window being hidden to the tray - cannot make the recording run
/// long: whenever the tick next runs, the recording is either past its deadline or it is
/// not. Deliberately free of WPF so it can be tested on its own.
/// </para>
/// </summary>
internal sealed class RecordingTimer
{
    /// <summary>Recording stops this long after the deadline, at the latest, so the file
    /// always has room to be finalised.</summary>
    public static readonly TimeSpan GracePeriod = TimeSpan.FromSeconds(5);

    private DateTime? _deadlineUtc;
    private DateTime _startedUtc;

    /// <summary>True while a timed stop is armed.</summary>
    public bool IsArmed => _deadlineUtc is not null;

    /// <summary>When the recording will stop, or null when no timed stop is armed.</summary>
    public DateTime? DeadlineUtc => _deadlineUtc;

    /// <summary>Arms a stop <paramref name="duration"/> from now. Arming twice re-arms from
    /// the later of the two, which is what "stop in 5 minutes" pressed twice means.</summary>
    public void Arm(TimeSpan duration, DateTime nowUtc)
    {
        if (duration <= TimeSpan.Zero) { Disarm(); return; }

        var deadline = nowUtc + duration;
        if (_deadlineUtc is { } existing && existing > deadline) deadline = existing;
        _startedUtc = nowUtc;
        _deadlineUtc = deadline;
    }

    /// <summary>Stops the timer without touching the recording itself.</summary>
    public void Disarm() => _deadlineUtc = null;

    /// <summary>True once the deadline has passed. Due inside the grace period, before the
    /// recording is actually stopped, so the user is warned rather than cut off.</summary>
    public bool IsDue(DateTime nowUtc) => _deadlineUtc is { } deadline && nowUtc >= deadline;

    /// <summary>True when the deadline has been passed by the whole grace period and the
    /// recording must be stopped now whether the UI has noticed or not.</summary>
    public bool IsOverdue(DateTime nowUtc) =>
        _deadlineUtc is { } deadline && nowUtc >= deadline + GracePeriod;

    /// <summary>Time left until the stop, or null when nothing is armed or it is already due.</summary>
    public TimeSpan? Remaining(DateTime nowUtc) =>
        _deadlineUtc is { } deadline && nowUtc < deadline ? deadline - nowUtc : null;

    /// <summary>"4:59", "48s": how the countdown reads on the recording pill.</summary>
    public static string FormatCountdown(TimeSpan remaining)
    {
        if (remaining <= TimeSpan.Zero) return "0s";
        if (remaining.TotalMinutes >= 1)
            return $"{(int)remaining.TotalMinutes}:{remaining.Seconds:00}";
        var seconds = Math.Ceiling(remaining.TotalSeconds);
        return seconds == 1 ? "1s" : $"{seconds.ToString("0", System.Globalization.CultureInfo.InvariantCulture)}s";
    }

    /// <summary>"Stops in 4:59" - what the pill says, and what a screen reader reads.</summary>
    public static string DescribeCountdown(TimeSpan remaining) => $"Stops in {FormatCountdown(remaining)}";
}
