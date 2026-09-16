namespace SoulScreen.App.Logic;

/// <summary>
/// How long the PIN entry waits after a run of wrong guesses.
/// <para>
/// Every attempt above three doubles the wait, capped at five minutes, so a script trying
/// PINs costs more with every failure while a person who mistyped their own PIN twice is
/// never made to wait for it. The count resets the moment a correct PIN is entered - a wrong
/// guess is only ever held against the attempts made since the last success.
/// </para>
/// Deliberately free of WPF, and free of any real clock, so the escalation can be tested
/// without a timer or a wall clock.
/// </summary>
public static class LockoutPolicy
{
    /// <summary>Wrong attempts allowed before any wait is imposed.</summary>
    public const int FreeAttempts = 3;

    public static readonly TimeSpan MaxDelay = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The wait imposed after <paramref name="consecutiveFailures"/> wrong attempts in a row.
    /// Zero for the first <see cref="FreeAttempts"/>; doubling from there: 5 s, 10 s, 20 s, …
    /// up to <see cref="MaxDelay"/>.
    /// </summary>
    public static TimeSpan DelayAfter(int consecutiveFailures)
    {
        if (consecutiveFailures <= FreeAttempts) return TimeSpan.Zero;

        var exponent = consecutiveFailures - FreeAttempts - 1;
        // Capped before the shift so a very large failure count cannot overflow the double.
        if (exponent > 10) return MaxDelay;

        var seconds = 5 * Math.Pow(2, exponent);
        return TimeSpan.FromSeconds(Math.Min(seconds, MaxDelay.TotalSeconds));
    }

    /// <summary>True once <paramref name="now"/> has reached the moment a lockout that began
    /// at <paramref name="lockedAtUtc"/> for <paramref name="delay"/> is over.</summary>
    public static bool HasElapsed(DateTime lockedAtUtc, TimeSpan delay, DateTime now) =>
        now >= lockedAtUtc + delay;

    /// <summary>Time left in a lockout, or <see cref="TimeSpan.Zero"/> once it has elapsed.</summary>
    public static TimeSpan Remaining(DateTime lockedAtUtc, TimeSpan delay, DateTime now)
    {
        var end = lockedAtUtc + delay;
        return end > now ? end - now : TimeSpan.Zero;
    }
}
