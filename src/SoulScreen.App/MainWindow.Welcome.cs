using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace SoulScreen.App;

/// <summary>
/// The welcome sheet: what SoulScreen does and how to start, shown once on the first launch
/// and afterwards whenever it is asked for.
/// </summary>
public partial class MainWindow
{
    private bool IsWelcomeOpen => WelcomeOverlay.Visibility == Visibility.Visible;

    /// <summary>Not for a launch at sign-in: that opens out of sight, and the sheet would wait there.</summary>
    private void ShowWelcomeIfFirstRun()
    {
        if (_settings.HasSeenWelcome || App.StartMinimised) return;
        ShowWelcome();
    }

    private void OnShowWelcome(object sender, RoutedEventArgs e) => ShowWelcome();

    private void ShowWelcome()
    {
        if (_shuttingDown) return;
        if (_isMiniPlayer) ExitMiniPlayer();
        ClosePalette();
        SettingsButton.IsChecked = false;

        WelcomeOverlay.Visibility = Visibility.Visible;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(220);
        WelcomeScrim.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration));
        WelcomeCard.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration) { EasingFunction = ease });
        WelcomeScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.96, 1, duration) { EasingFunction = ease });
        WelcomeScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.96, 1, duration) { EasingFunction = ease });

        Dispatcher.BeginInvoke(() =>
        {
            if (IsWelcomeOpen) WelcomeContinue.Focus();
        }, DispatcherPriority.Input);
    }

    private void CloseWelcome()
    {
        if (!IsWelcomeOpen) return;
        var hadFocus = WelcomeOverlay.IsKeyboardFocusWithin;
        WelcomeOverlay.Visibility = Visibility.Collapsed;

        if (!_settings.HasSeenWelcome)
        {
            _settings.HasSeenWelcome = true;
            _settings.Save();
        }

        if (hadFocus) Focus();
    }

    private void OnWelcomeContinue(object sender, RoutedEventArgs e) => CloseWelcome();

    private void OnWelcomeDoctor(object sender, RoutedEventArgs e)
    {
        CloseWelcome();
        ShowDoctor();
    }
}
