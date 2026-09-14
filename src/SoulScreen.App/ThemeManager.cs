using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Microsoft.Win32;
using SoulScreen.Core.Logging;

namespace SoulScreen.App;

/// <summary>
/// Recolours the application's brushes for the chosen theme.
/// <para>
/// Every colour in Theme.xaml is a <see cref="SolidColorBrush"/> resource, and every use
/// of one is a DynamicResource reference. Changing a brush's colour therefore changes every
/// control at once, live, and the change can be animated - which is what makes switching
/// look like the window fading to its new colours rather than being rebuilt. Should a brush
/// have been frozen, it is swapped for a new one instead, which the dynamic references pick
/// up without the fade.
/// </para>
/// </summary>
internal static class ThemeManager
{
    private static readonly ILogger Log_ = Log.For("theme");
    private static readonly Duration FadeDuration = new(TimeSpan.FromMilliseconds(220));

    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string AppsUseLightThemeValue = "AppsUseLightTheme";

    private static AppTheme _requested = AppTheme.System;
    private static bool _watchingSystem;

    /// <summary>The palette actually on screen: System resolved to Dark or Light.</summary>
    public static bool IsDark { get; private set; } = true;

    /// <summary>Raised on the UI thread after the palette changes, for chrome that is not a brush.</summary>
    public static event Action? Changed;

    /// <summary>Applies a theme. System follows the Windows setting and keeps following it.</summary>
    public static void Apply(AppTheme theme, bool animate)
    {
        _requested = theme;
        var dark = theme switch
        {
            AppTheme.Dark => true,
            AppTheme.Light => false,
            _ => SystemPrefersDark(),
        };

        if (theme == AppTheme.System) WatchSystem();

        SetPalette(dark, animate);
    }

    private static void SetPalette(bool dark, bool animate)
    {
        var resources = Application.Current.Resources;
        var palette = dark ? Dark : Light;

        foreach (var (key, color) in palette)
        {
            if (resources[key] is not SolidColorBrush brush)
            {
                Log_.Warn($"theme brush '{key}' is missing from the resources");
                continue;
            }

            if (brush.IsFrozen)
            {
                // A brush that something has frozen - WPF seals the values of a sealed
                // Style's setters, for one - cannot be recoloured, so it is replaced. Every
                // use is a DynamicResource reference, so the replacement reaches them all;
                // only the cross-fade is lost.
                resources[key] = new SolidColorBrush(color);
                continue;
            }

            if (animate && brush.Color != color)
            {
                brush.BeginAnimation(SolidColorBrush.ColorProperty,
                    new ColorAnimation(color, FadeDuration) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            }
            else
            {
                brush.BeginAnimation(SolidColorBrush.ColorProperty, null);
                brush.Color = color;
            }
        }

        IsDark = dark;
        Changed?.Invoke();
    }

    /// <summary>Reads the "choose your default app mode" setting.</summary>
    public static bool SystemPrefersDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            return key?.GetValue(AppsUseLightThemeValue) is not int light || light == 0;
        }
        catch (Exception)
        {
            return true;
        }
    }

    private static void WatchSystem()
    {
        if (_watchingSystem) return;
        _watchingSystem = true;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    private static void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category != UserPreferenceCategory.General) return;
        if (_requested != AppTheme.System) return;

        // Raised on a system thread; the brushes belong to the UI thread.
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            var dark = SystemPrefersDark();
            if (dark != IsDark) SetPalette(dark, animate: true);
        });
    }

    private static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex)!;

    /// <summary>
    /// A neutral dark palette. Surfaces are shades of near-black grey; the accent is the
    /// system blue; text steps from white through two greys.
    /// </summary>
    private static readonly (string Key, Color Color)[] Dark =
    [
        ("Surface", C("#FF121214")),
        ("SurfaceRaised", C("#FF1C1C1F")),
        ("SurfaceSunken", C("#FF0B0B0C")),
        ("SurfaceHover", C("#FF2A2A2F")),
        ("SurfacePressed", C("#FF35353B")),
        ("SegmentSelected", C("#FF3A3A40")),
        ("BorderBrush", C("#FF2C2C31")),
        ("BorderSubtle", C("#FF232327")),
        ("Accent", C("#FF0A84FF")),
        ("AccentHover", C("#FF3395FF")),
        ("AccentPressed", C("#FF0071E3")),
        ("AccentTint", C("#330A84FF")),
        ("AccentMuted", C("#FF1F4D80")),
        ("OnAccent", C("#FFFFFFFF")),
        ("TextPrimary", C("#FFF5F5F7")),
        ("TextSecondary", C("#FF9A9AA0")),
        ("TextTertiary", C("#FF636368")),
        ("Success", C("#FF30D158")),
        ("Warning", C("#FFFFD60A")),
        ("Danger", C("#FFFF453A")),
        ("DangerHover", C("#FFC42B1C")),
        ("WarningTint", C("#24FFD60A")),
        ("WarningBorder", C("#4DFFD60A")),
        ("NoticeBackground", C("#F2231E0C")),
        ("OverlayChrome", C("#E61C1C1F")),
        ("PanelBackground", C("#F5121214")),
        ("Shadow", C("#99000000")),
    ];

    /// <summary>The light palette: warm off-white ground, white cards, the same blue.</summary>
    private static readonly (string Key, Color Color)[] Light =
    [
        ("Surface", C("#FFF2F2F5")),
        ("SurfaceRaised", C("#FFFFFFFF")),
        ("SurfaceSunken", C("#FFEBEBEF")),
        ("SurfaceHover", C("#FFE4E4E9")),
        ("SurfacePressed", C("#FFD7D7DD")),
        ("SegmentSelected", C("#FFFFFFFF")),
        ("BorderBrush", C("#FFD6D6DB")),
        ("BorderSubtle", C("#FFE8E8EC")),
        ("Accent", C("#FF007AFF")),
        ("AccentHover", C("#FF2B8FFF")),
        ("AccentPressed", C("#FF0064D6")),
        ("AccentTint", C("#26007AFF")),
        ("AccentMuted", C("#FFB9D6FF")),
        ("OnAccent", C("#FFFFFFFF")),
        ("TextPrimary", C("#FF1D1D1F")),
        ("TextSecondary", C("#FF6E6E73")),
        ("TextTertiary", C("#FF9C9CA1")),
        ("Success", C("#FF28A745")),
        ("Warning", C("#FFD48A00")),
        ("Danger", C("#FFE0332A")),
        ("DangerHover", C("#FFC42B1C")),
        ("WarningTint", C("#24FF9F0A")),
        ("WarningBorder", C("#59FF9F0A")),
        ("NoticeBackground", C("#F5FFF5DA")),
        ("OverlayChrome", C("#EEF6F6F8")),
        ("PanelBackground", C("#F7F2F2F5")),
        ("Shadow", C("#40000000")),
    ];
}
