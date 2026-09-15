using System.Windows;
using SoulScreen.App.Logic;

namespace SoulScreen.App;

/// <summary>
/// The one place the app asks whether a transition may be animated.
/// <para>
/// Replaces the scattered <c>SystemParameters.ClientAreaAnimation</c> checks: the
/// preference in settings now has the final word, and Windows' own setting is only the
/// default it starts from.
/// </para>
/// </summary>
internal static class Motion
{
    /// <summary>True when transitions should play. Reads the live settings; before the
    /// window has built them, Windows' own setting answers.</summary>
    public static bool Enabled => ShouldAnimate(App.Settings);

    /// <summary>The same question, answered for an explicit settings object - which is
    /// what the test path and the settings reset both use.</summary>
    public static bool ShouldAnimate(AppSettings? settings) =>
        MotionPreferences.ShouldAnimate(settings?.Animations ?? MotionPreference.FollowWindows,
            SystemParameters.ClientAreaAnimation);
}
