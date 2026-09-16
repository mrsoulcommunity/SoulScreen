using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace SoulScreen.App;

/// <summary>
/// The control bar: record, screenshot, markup, sound, pause, the mini player and fullscreen,
/// floating over the foot of the picture the way QuickTime's controls do.
/// <para>
/// It comes up whenever the pointer moves or the keyboard reaches it, and goes again once the
/// pointer has rested for a moment, so a picture being watched is left alone. It stays while it
/// is in use - the pointer on it, focus in it, its menu open - and while the picture is paused,
/// since a paused picture is one somebody is about to do something with.
/// </para>
/// </summary>
public partial class MainWindow
{
    private static readonly TimeSpan ControlBarLinger = TimeSpan.FromMilliseconds(2600);

    private DispatcherTimer? _controlBarTimer;
    private bool _controlBarShown;

    /// <summary>Where the pointer last was, so a mouse-move that WPF raises for a layout change
    /// under a pointer that has not moved does not count as the user reaching for the controls.</summary>
    private Point _lastPointer = new(double.NaN, double.NaN);

    private void InitialiseControlBar()
    {
        _controlBarTimer = new DispatcherTimer(DispatcherPriority.Input) { Interval = ControlBarLinger };
        _controlBarTimer.Tick += (_, _) => OnControlBarIdle();

        // Leaving the window is as clear a sign as resting: the controls go at once rather than
        // hanging over a picture nobody is pointing at.
        MouseLeave += (_, _) =>
        {
            _lastPointer = new Point(double.NaN, double.NaN);
            if (!IsControlBarInUse) HideControlBar();
        };

        ControlBar.IsKeyboardFocusWithinChanged += (_, _) =>
        {
            if (ControlBar.IsKeyboardFocusWithin) RevealControlBar();
        };

        ContentGrid.SizeChanged += (_, _) =>
        {
            LayoutControlBar();
            PositionFloatingControlBar();
        };

        // The picture's menu is opened from the bar's last button as well as by a right-click.
        // Placed against the button for the one, it must go back to the pointer for the other.
        VideoMenu.Closed += (_, _) =>
        {
            VideoMenu.ClearValue(ContextMenu.PlacementTargetProperty);
            VideoMenu.ClearValue(ContextMenu.PlacementProperty);
            if (ControlBar.Visibility == Visibility.Visible) RevealControlBar();
        };
    }

    private bool ControlBarWanted =>
        !_shuttingDown && VideoHost.Visibility == Visibility.Visible && !_isMiniPlayer && !IsMarkupActive;

    private bool IsControlBarInUse =>
        ControlBar.IsMouseOver || ControlBar.IsKeyboardFocusWithin || VideoMenu.IsOpen || Video.IsFrozen;

    /// <summary>Puts the bar in step with the window: there, and briefly shown, whenever a
    /// picture is on screen; gone in the mini player, which has controls of its own, and while
    /// marking up, whose own bar takes the same place.</summary>
    private void UpdateControlBar()
    {
        if (ControlBar is null) return;

        if (!ControlBarWanted)
        {
            _controlBarTimer?.Stop();
            _controlBarShown = false;
            ControlBar.BeginAnimation(OpacityProperty, null);
            ControlBar.Visibility = Visibility.Collapsed;
            PositionOverlays();
            return;
        }

        var appearing = ControlBar.Visibility != Visibility.Visible;
        ControlBar.Visibility = Visibility.Visible;
        // The bar is laid out against the picture by the placement logic, not the XAML's
        // default Stretch alignment: without this it would spread over the whole picture
        // on first show, until some other action applied the placement.
        ApplyControlBarPlacement();
        LayoutControlBar();
        PositionOverlays();

        // Shown when it first appears, so a new session announces where its controls are
        // before they fade.
        if (appearing)
        {
            _controlBarShown = false;
            RevealControlBar();
        }
    }

    /// <summary>
    /// Fits the bar to the width it has. The volume level gives way first - the wheel over the
    /// picture and Ctrl+Up and Down still set it - then pause, which Space still does, so every
    /// control that has no other way to reach it keeps its place down to the narrowest window.
    /// </summary>
    private void LayoutControlBar()
    {
        if (ControlBar is null || ControlBar.Visibility != Visibility.Visible) return;

        var room = ContentGrid.ActualWidth - ControlBar.Margin.Left - ControlBar.Margin.Right;
        if (room <= 0) return;

        var hasAudio = _audio is not null;
        MuteButton.Visibility = hasAudio ? Visibility.Visible : Visibility.Collapsed;
        ControlBarAudioDivider.Visibility = hasAudio ? Visibility.Visible : Visibility.Collapsed;
        VolumeSlider.Visibility = hasAudio ? Visibility.Visible : Visibility.Collapsed;
        PauseButton.Visibility = Visibility.Visible;

        var unbounded = new Size(double.PositiveInfinity, double.PositiveInfinity);
        foreach (var optional in new FrameworkElement[] { VolumeSlider, PauseButton })
        {
            ControlBar.Measure(unbounded);
            if (ControlBar.DesiredSize.Width <= room) break;
            optional.Visibility = Visibility.Collapsed;
        }

        // Collapsing items above changes the bar's size; the corners park it by that size,
        // so re-run the placement once the bar has been through a layout pass.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, PositionFloatingControlBar);
    }

    /// <summary>A real movement of the pointer over the window.</summary>
    private void OnPointerMovedForControlBar(MouseEventArgs e)
    {
        var position = e.GetPosition(this);
        if (!double.IsNaN(_lastPointer.X) && (position - _lastPointer).Length < 3) return;
        _lastPointer = position;
        RevealControlBar();
    }

    private void RevealControlBar()
    {
        if (ControlBar is null || ControlBar.Visibility != Visibility.Visible) return;

        _controlBarTimer?.Stop();
        _controlBarTimer?.Start();
        if (_controlBarShown) return;

        _controlBarShown = true;
        ControlBar.IsHitTestVisible = true;
        FadeControlBar(1, TimeSpan.FromMilliseconds(180), EasingMode.EaseOut);
    }

    private void HideControlBar()
    {
        if (!_controlBarShown) return;
        _controlBarShown = false;
        _controlBarTimer?.Stop();
        ControlBar.IsHitTestVisible = false;
        FadeControlBar(0, TimeSpan.FromMilliseconds(320), EasingMode.EaseIn);
    }

    private void FadeControlBar(double to, TimeSpan duration, EasingMode easing)
    {
        if (!Motion.Enabled)
        {
            ControlBar.BeginAnimation(OpacityProperty, null);
            ControlBar.Opacity = to;
            return;
        }

        ControlBar.BeginAnimation(OpacityProperty, new DoubleAnimation(to, duration)
        {
            EasingFunction = new CubicEase { EasingMode = easing },
        });
    }

    private void OnControlBarIdle()
    {
        _controlBarTimer?.Stop();
        if (IsControlBarInUse)
        {
            _controlBarTimer?.Start();
            return;
        }
        HideControlBar();
    }

    private void OnControlBarMore(object sender, RoutedEventArgs e)
    {
        VideoMenu.PlacementTarget = ControlBarMore;
        VideoMenu.Placement = PlacementMode.Top;
        VideoMenu.IsOpen = true;
    }

    /// <summary>The pause button follows the picture, as the mini player's does.</summary>
    private void UpdatePauseButton()
    {
        if (PauseButton is null) return;
        var frozen = Video.IsFrozen;
        PauseButton.Content = frozen ? "" : "";
        PauseButton.ToolTip = frozen ? "Resume the picture (Space)" : "Pause the picture (Space)";
        System.Windows.Automation.AutomationProperties.SetName(PauseButton, frozen ? "Resume the picture" : "Pause the picture");
        if (frozen) RevealControlBar();
    }
}
