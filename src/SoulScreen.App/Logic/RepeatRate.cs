namespace SoulScreen.App.Logic;

/// <summary>
/// How fast repeated key presses should act, for press-and-hold.
/// <para>
/// Media players all answer a held arrow key: the first press steps at once, a pause follows so
/// a quick tap is never mistaken for a hold, and only a key that stays down begins to step fast.
/// Firing at one speed from the first repeat would skip straight past the item that was wanted.
/// </para>
/// Deliberately free of WPF so the schedule can be tested on its own.
/// </summary>
public static class RepeatRate
{
    /// <summary>How long the key must have been down before the repeats quicken.</summary>
    public static readonly TimeSpan SlowPeriod = TimeSpan.FromMilliseconds(450);

    /// <summary>Milliseconds between steps while the hold is still young.</summary>
    public const int SlowStepMilliseconds = 180;

    /// <summary>Milliseconds between steps once the hold is established.</summary>
    public const int FastStepMilliseconds = 60;

    /// <summary>
    /// The delay until the next step for a key held for <paramref name="heldFor"/>, or null
    /// while the key is not yet due to repeat at all - the first press having acted already.
    /// </summary>
    public static TimeSpan? NextDelay(TimeSpan heldFor)
    {
        if (heldFor < SlowPeriod) return heldFor < TimeSpan.Zero ? SlowPeriod : null;
        return TimeSpan.FromMilliseconds(heldFor < SlowPeriod + TimeSpan.FromMilliseconds(SlowStepMilliseconds)
            ? SlowStepMilliseconds
            : FastStepMilliseconds);
    }
}
