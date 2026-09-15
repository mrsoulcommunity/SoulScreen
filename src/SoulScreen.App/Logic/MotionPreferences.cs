namespace SoulScreen.App.Logic;

/// <summary>How the app treats its own animations.</summary>
public enum MotionPreference
{
    /// <summary>Animate, unless Windows has been asked to keep animation to a minimum.</summary>
    FollowWindows,
    /// <summary>Animate whatever Windows says - for screenshots, videos and the like.</summary>
    AlwaysOn,
    /// <summary>Never animate: every transition lands on its end state at once.</summary>
    AlwaysOff,
}

/// <summary>
/// The single gate for the app's own motion.
/// <para>
/// Every fade, slide and ripple used to ask <see cref="SystemParameters.ClientAreaAnimation"/>
/// for itself. That answered Windows' setting, but gave the user no say of their own: a
/// screen recorder wants the ripples gone whatever Windows does, and a demonstration wants
/// them kept on a machine Windows has muted. One preference now answers for all of them.
/// </para>
/// Deliberately free of WPF so the decision can be tested on its own.
/// </summary>
public static class MotionPreferences
{
    /// <summary>
    /// True when a transition may be animated. <paramref name="windowsAllows"/> is
    /// <c>SystemParameters.ClientAreaAnimation</c>, passed in so the test can drive it.
    /// </summary>
    public static bool ShouldAnimate(MotionPreference preference, bool windowsAllows) => preference switch
    {
        MotionPreference.AlwaysOn => true,
        MotionPreference.AlwaysOff => false,
        _ => windowsAllows,
    };
}
