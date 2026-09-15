namespace SoulScreen.App.Logic;

/// <summary>Describes the motion budget for a single UI transition.</summary>
public readonly record struct MotionSpec(bool Enabled, TimeSpan Duration)
{
    public static MotionSpec Instant => new(false, TimeSpan.Zero);
}

/// <summary>Resolves the app's motion preference into one consistent, testable policy.</summary>
public static class UiMotion
{
    public static readonly TimeSpan NormalDuration = TimeSpan.FromMilliseconds(180);
    public static readonly TimeSpan ReducedDuration = TimeSpan.FromMilliseconds(90);

    public static MotionSpec Resolve(MotionPreference preference, bool windowsAllows)
    {
        // FollowWindows is the accessibility-safe default: when Windows requests reduced
        // motion, transitions should land immediately rather than merely becoming shorter.
        if (!MotionPreferences.ShouldAnimate(preference, windowsAllows)) return MotionSpec.Instant;
        return new MotionSpec(true, NormalDuration);
    }

    public static double Opacity(MotionSpec motion, double target) => motion.Enabled ? target : 1;
}
