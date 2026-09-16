using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace SoulScreen.App;

/// <summary>
/// Where the picture controls live. Four placements are offered:
/// <list type="bullet">
/// <item><see cref="ControlBarPlacement.Floating"/> - the original over-the-foot bar, fading away
/// until the pointer moves.</item>
/// <item><see cref="ControlBarPlacement.Corner"/> - parked in one of the four picture corners
/// (or centred over the foot), still fading.</item>
/// <item><see cref="ControlBarPlacement.Free"/> - wherever the user dragged it; on release it
/// snaps to the nearest corner so the bar's edges line up with the picture.</item>
/// <item><see cref="ControlBarPlacement.Docked"/> - moved into the top caption strip beside the
/// window controls, where it sits alongside the rest of the chrome and is never hidden.</item>
/// </list>
/// The user can pick the placement from the settings panel, from a popup menu on the bar's
/// last button, or by double-clicking the grip on the floating bar.
/// </summary>
public partial class MainWindow
{
    private Point _controlBarDragStart;
    private Point _controlBarDragOrigin;
    private bool _suppressDockedEvents;

    /// <summary>
    /// Picks the right margin, alignment and visibility for the floating <see cref="ControlBar"/>
    /// and shows or hides its docked twin based on the chosen placement.
    /// </summary>
    private void ApplyControlBarPlacement()
    {
        var placement = _settings?.ControlBarPlacement ?? ControlBarPlacement.Floating;

        if (placement == ControlBarPlacement.Docked)
        {
            ControlBar.Visibility = Visibility.Collapsed;
            ControlBarDocked.Visibility = VideoHost.Visibility == Visibility.Visible && !_isMiniPlayer
                ? Visibility.Visible : Visibility.Collapsed;
            SyncDockedBar();
            return;
        }

        ControlBarDocked.Visibility = Visibility.Collapsed;

        if (ControlBar.Visibility != Visibility.Visible) return;
        // Floating, corner and free are all the same bar - just parked differently.
        PositionFloatingControlBar();
        ApplyCompactMode();
    }

    /// <summary>
    /// The little "lift" animation that plays when the pointer enters the floating bar:
    /// 2 px upward with a soft spring, so the bar feels alive. Reverses on leave. The
    /// transform is on the bar itself, not its content, so the shadow goes with it.
    /// </summary>
    private void AnimateBarLift(bool lifted)
    {
        if (ControlBar is null || !Motion.Enabled) return;
        var y = lifted ? -2.0 : 0.0;
        var rt = ControlBar.RenderTransform as TranslateTransform;
        if (rt is null)
        {
            rt = new TranslateTransform();
            ControlBar.RenderTransform = rt;
        }
        rt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(y, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut },
        });
    }

    private void OnControlBarMouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        AnimateBarLift(true);
    }

    private void OnControlBarMouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        // Don't drop the lift if the pointer is still on the bar (a child button or the
        // grip). The leave event fires whenever a child is entered, so the guard is the
        // same as the existing IsMouseOver check used by the rest of the bar logic.
        if (ControlBar.IsMouseOver) return;
        AnimateBarLift(false);
    }

    /// <summary>
    /// The bar is icon-only by design. The setting kept its name for the sake of saved
    /// profiles, but its meaning has flipped: it now asks for the older, taller bar with
    /// the shortcut labels under each icon. Nobody needs to find it - the icons and their
    /// tooltips carry the meaning - so the setting is hidden from the settings panel and
    /// the placement menu, and only honoured if a profile already has it.
    /// </summary>
    private void ApplyCompactMode()
    {
        if (ControlBarItems is null) return;
        var showLabels = _settings?.CompactControlBar != true;
        foreach (var child in ControlBarItems.Children.OfType<FrameworkElement>())
        {
            if (child is TextBlock tb && tb.Name.EndsWith("Hint", StringComparison.Ordinal))
            {
                tb.Visibility = showLabels ? Visibility.Visible : Visibility.Collapsed;
            }
        }
    }

    /// <summary>Flips between the icon-only bar and the labelled one.</summary>
    /// <summary>Sets the labelled-bar mode explicitly.</summary>
    public void SetCompactControlBar(bool compact)
    {
        if (_settings is null) return;
        _settings.CompactControlBar = compact;
        _settings.Save();
        ApplyCompactMode();
        RevealControlBar();
    }    /// <summary>
    /// Lays the floating bar against the chosen edge of the picture: aligned to a corner with a
    /// small breathing margin, or at the dragged-to position in free mode.
    /// </summary>
    private void PositionFloatingControlBar()
    {
        var room = ContentGrid.ActualWidth;
        var height = ContentGrid.ActualHeight;
        if (room <= 0 || height <= 0) return;

        var placement = _settings.ControlBarPlacement;
        var corner = placement == ControlBarPlacement.Corner
            ? _settings.ControlBarCorner
            : placement == ControlBarPlacement.Free && _settings.ControlBarFreeX is { } fx && _settings.ControlBarFreeY is { } fy
                ? ClosestCorner(fx, fy)
                : ControlBarCorner.BottomCentre;

        // The bar measures itself only on layout; reading its width here is the post-Measure
        // value so the centre and corner positions land where the eye expects them.
        var barWidth = ControlBar.ActualWidth > 0 ? ControlBar.ActualWidth : ControlBar.DesiredSize.Width;
        var barHeight = ControlBar.ActualHeight > 0 ? ControlBar.ActualHeight : ControlBar.DesiredSize.Height;

        // 12 px from the picture edge so the bar doesn't sit on the seam between picture and
        // chrome; the same inset the bar used when it was bottom-centred.
        const double inset = 12;
        const double bottomLift = 16;

        Thickness margin;
        HorizontalAlignment hAlign;
        VerticalAlignment vAlign;

        switch (corner)
        {
            case ControlBarCorner.TopLeft:
                hAlign = HorizontalAlignment.Left;
                vAlign = VerticalAlignment.Top;
                margin = new Thickness(inset, inset, inset, 0);
                break;
            case ControlBarCorner.TopRight:
                hAlign = HorizontalAlignment.Right;
                vAlign = VerticalAlignment.Top;
                margin = new Thickness(inset, inset, inset, 0);
                break;
            case ControlBarCorner.BottomLeft:
                hAlign = HorizontalAlignment.Left;
                vAlign = VerticalAlignment.Bottom;
                margin = new Thickness(inset, 0, inset, bottomLift);
                break;
            case ControlBarCorner.BottomRight:
                hAlign = HorizontalAlignment.Right;
                vAlign = VerticalAlignment.Bottom;
                margin = new Thickness(inset, 0, inset, bottomLift);
                break;
            default: // BottomCentre
                hAlign = HorizontalAlignment.Center;
                vAlign = VerticalAlignment.Bottom;
                margin = new Thickness(inset, 0, inset, bottomLift);
                break;
        }

        ControlBar.HorizontalAlignment = hAlign;
        ControlBar.VerticalAlignment = vAlign;
        ControlBar.Margin = margin;

        // Free mode remembers the dragged position as a fraction so the bar survives a window
        // resize. The corner-pick is purely visual - the free fractions stay where they were
        // dropped.
        if (placement == ControlBarPlacement.Free && !_controlBarDragging)
        {
            if (_settings.ControlBarFreeX is { } freeX && _settings.ControlBarFreeY is { } freeY)
            {
                // The free position is stored as the centre of the bar in 0..1 against the
                // picture's width and height. Translate that into the same alignment + margin
                // model the corner placements use.
                var centreX = freeX * room;
                var centreY = freeY * height;
                var left = Math.Clamp(centreX - barWidth / 2, inset, Math.Max(inset, room - barWidth - inset));
                var top = Math.Clamp(centreY - barHeight / 2, inset, Math.Max(inset, height - barHeight - inset));
                ControlBar.HorizontalAlignment = HorizontalAlignment.Left;
                ControlBar.VerticalAlignment = VerticalAlignment.Top;
                ControlBar.Margin = new Thickness(left, top, 0, 0);
            }
        }
    }

    /// <summary>Which corner a 0..1 picture-relative point is closest to.</summary>
    private static ControlBarCorner ClosestCorner(double fx, double fy)
    {
        var left = fx < 0.5;
        var top = fy < 0.5;
        if (top && left) return ControlBarCorner.TopLeft;
        if (top) return ControlBarCorner.TopRight;
        if (left) return ControlBarCorner.BottomLeft;
        return ControlBarCorner.BottomRight;
    }

    /// <summary>
    /// Keeps the docked bar's visible buttons in step with the floating bar's state: the
    /// record and mute toggles, the volume slider's value, and whether record, markup and
    /// audio are even available right now.
    /// </summary>
    private void SyncDockedBar()
    {
        if (ControlBarDockedItems is null) return;
        _suppressDockedEvents = true;
        try
        {
            RecordButtonDocked.Visibility = RecordButton.Visibility;
            RecordButtonDocked.IsChecked = RecordButton.IsChecked == true;

            MarkupButtonDocked.Visibility = MarkupButton.Visibility;
            MarkupButtonDocked.IsChecked = MarkupButton.IsChecked == true;

            var hasAudio = MuteButton.Visibility == Visibility.Visible;
            MuteButtonDocked.Visibility = hasAudio ? Visibility.Visible : Visibility.Collapsed;
            MuteButtonDocked.IsChecked = MuteButton.IsChecked == true;

            VolumeSliderDocked.Visibility = hasAudio ? Visibility.Visible : Visibility.Collapsed;
            if (VolumeSlider is not null && VolumeSlider.Value != VolumeSliderDocked.Value) VolumeSliderDocked.Value = VolumeSlider.Value;

            // Keep the docked bar's "stop recording" tooltip in step with the live state.
            var recording = RecordButton.IsChecked == true;
            RecordButtonDocked.Content = recording ? "\uE71A" : "\uE7C8";
            RecordButtonDocked.ToolTip = recording ? "Stop recording (Ctrl+R)" : "Record to MP4 (Ctrl+R)";

            SnapshotButtonDocked.IsEnabled = SnapshotButton.IsEnabled;
        }
        finally
        {
            _suppressDockedEvents = false;
        }
    }

    /// <summary>Updates the docked bar from the floating one whenever the latter's state changes.</summary>
    private void OnControlBarStateChanged()
    {
        if (ControlBarDocked.Visibility == Visibility.Visible) SyncDockedBar();
    }

    // ---- docked bar button handlers (mirror the floating ones) -----------------

    private void OnRecordDockedClick(object sender, RoutedEventArgs e)
    {
        RecordButton.IsChecked = RecordButton.IsChecked != true;
        RecordButtonDocked.IsChecked = RecordButton.IsChecked == true;
    }

    private void OnMarkupDockedToggled(object sender, RoutedEventArgs e)
    {
        MarkupButton.IsChecked = MarkupButtonDocked.IsChecked == true;
    }

    private void OnMuteDockedClick(object sender, RoutedEventArgs e)
    {
        MuteButton.IsChecked = MuteButton.IsChecked != true;
        MuteButtonDocked.IsChecked = MuteButton.IsChecked == true;
    }

    private void OnVolumeDockedChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // VolumeSlider is the floating bar's slider; during XAML init it may not yet be
        // wired up. Ignore early events rather than dereference null.
        if (_suppressDockedEvents) return;
        if (VolumeSlider is null) return;
        VolumeSlider.Value = e.NewValue;
    }

    // ---- placement menu on the bar's More button --------------------------------------

    private void OnControlBarPlacementMenu(object sender, RoutedEventArgs e)
    {
        var popup = new ContextMenu();
        AddPlacementItem(popup, ControlBarPlacement.Floating, "Over the picture", "Ctrl+Shift+F");
        AddPlacementItem(popup, ControlBarPlacement.Corner, "Parked in a corner", "Ctrl+Shift+C");
        AddPlacementItem(popup, ControlBarPlacement.Free, "Wherever I drag it", "Ctrl+Shift+D");
        AddPlacementItem(popup, ControlBarPlacement.Docked, "In the title bar", "Ctrl+Shift+B");

        // Sub-menu for the corner choice, only meaningful in Corner mode.
        var cornerMenu = new MenuItem { Header = "Parked corner" };
        foreach (ControlBarCorner corner in Enum.GetValues(typeof(ControlBarCorner)))
        {
            var label = corner switch
            {
                ControlBarCorner.TopLeft => "Top left",
                ControlBarCorner.TopRight => "Top right",
                ControlBarCorner.BottomLeft => "Bottom left",
                ControlBarCorner.BottomRight => "Bottom right",
                _ => "Bottom centre",
            };
            var item = new MenuItem { Header = label };
            item.Click += (_, _) => SetCornerPlacement(corner);
            cornerMenu.Items.Add(item);
        }
        popup.Items.Add(cornerMenu);
        popup.Items.Add(new Separator());
        var reset = new MenuItem { Header = "Reset to default (over the foot)" };
        reset.Click += (_, _) => ResetControlBarPlacement();
        popup.Items.Add(reset);

        popup.PlacementTarget = ControlBarMore;
        popup.Placement = PlacementMode.Top;
        popup.IsOpen = true;
    }

    private void AddPlacementItem(ContextMenu menu, ControlBarPlacement placement, string label, string hint)
    {
        var item = new MenuItem { Header = $"{label}    {hint}" };
        item.Click += (_, _) => SetControlBarPlacement(placement);
        menu.Items.Add(item);
    }

    /// <summary>Sets the placement, persists it, and repositions the bar so the change is visible.</summary>
    private void SetControlBarPlacement(ControlBarPlacement placement)
    {
        if (_settings is null) return;
        _settings.ControlBarPlacement = placement;
        if (placement == ControlBarPlacement.Floating)
        {
            _settings.ControlBarCorner = ControlBarCorner.BottomCentre;
            _settings.ControlBarFreeX = null;
            _settings.ControlBarFreeY = null;
        }
        _settings.Save();
        ApplyControlBarPlacement();
        RevealControlBar();
        _log.Info($"control bar placement set to {placement}");
    }

    private void SetCornerPlacement(ControlBarCorner corner)
    {
        if (_settings is null) return;
        _settings.ControlBarPlacement = ControlBarPlacement.Corner;
        _settings.ControlBarCorner = corner;
        _settings.Save();
        ApplyControlBarPlacement();
        RevealControlBar();
    }

    private void ResetControlBarPlacement()
    {
        if (_settings is null) return;
        _settings.ControlBarPlacement = ControlBarPlacement.Floating;
        _settings.ControlBarCorner = ControlBarCorner.BottomCentre;
        _settings.ControlBarFreeX = null;
        _settings.ControlBarFreeY = null;
        _settings.Save();
        ApplyControlBarPlacement();
        RevealControlBar();
    }

    /// <summary>The press a drag is armed from. The bar itself does not move on a mere
    /// press - that would fight every click - but once the pointer actually travels while
    /// the button is down, the drag takes over. Cleared on release.</summary>
    private bool _controlBarPressArmed;

    /// <summary>A drag in progress. Only one exists at a time, and it captures the pointer.</summary>
    private bool _controlBarDragging;

    /// <summary>Arms a drag on a press on the bar's own surface - the padding, the dividers,
    /// the grip - never on a button, which keeps its click. Nothing is captured yet, so a
    /// click that goes nowhere can never strand the pointer.</summary>
    private void OnControlBarGrab(object sender, MouseButtonEventArgs e)
    {
        if (_settings is null) return;
        // A double-click (and not the start of a drag) cycles the placement. Detected here
        // because Rectangle inherits from Shape, not Control, so it has no MouseDoubleClick
        // event to bind in XAML.
        if (e.ClickCount >= 2)
        {
            _controlBarPressArmed = false;
            OnControlBarGrabDoubleClick(sender, e);
            e.Handled = true;
            return;
        }
        // A press on a real control leaves the control its click: dragging begins from the
        // bar's own surface only.
        if (e.OriginalSource is not DependencyObject source || IsControlBarInteractive(source))
        {
            _controlBarPressArmed = false;
            return;
        }

        _controlBarPressArmed = true;
        _controlBarDragStart = e.GetPosition(this);
        _controlBarDragOrigin = new Point(ControlBar.Margin.Left, ControlBar.Margin.Top);
        RevealControlBar();
        e.Handled = true;
    }

    /// <summary>True when the press landed on something that must keep its own click: a
    /// button, a toggle, the volume. Everything else on the bar is grab surface.</summary>
    private bool IsControlBarInteractive(DependencyObject source)
    {
        var bar = (DependencyObject)ControlBar!;
        for (var node = source; node is not null; node = LogicalTreeHelper.GetParent(node) ?? VisualTreeHelper.GetParent(node))
        {
            if (node is System.Windows.Controls.Primitives.ButtonBase or ToggleButton or System.Windows.Controls.Slider)
                return true;
            if (node == bar) break;
        }
        return false;
    }

    /// <summary>Takes over once the pointer truly travels with the button held on armed
    /// surface: captures the mouse and pulls the bar. Until that first real movement the
    /// bar stays put, so an ordinary press - or a click that lands nowhere - is harmless.</summary>
    private void OnControlBarGrabMove(object sender, MouseEventArgs e)
    {
        if (!_controlBarPressArmed || e.LeftButton != MouseButtonState.Pressed)
        {
            // The button came up somewhere else, or drifted off the armed surface; a drag
            // was never or no longer possible. Stand down so the next press starts clean.
            if (_controlBarPressArmed && e.LeftButton != MouseButtonState.Pressed) _controlBarPressArmed = false;
            if (!_controlBarDragging) return;
        }
        var now = e.GetPosition(this);
        var delta = now - _controlBarDragStart;

        if (!_controlBarDragging)
        {
            // A couple of pixels of tremor are not a drag. Past that, this is the real
            // gesture - and only now is the pointer captured, so a mis-click never traps it.
            if (delta.Length < 4) return;
            if (_settings!.ControlBarPlacement == ControlBarPlacement.Docked)
            {
                // A pull on the docked bar frees it, so the user can drag it out of the chrome.
                SetControlBarPlacement(ControlBarPlacement.Free);
                _controlBarDragOrigin = new Point(ControlBar.Margin.Left, ControlBar.Margin.Top);
            }
            else if (_settings.ControlBarPlacement != ControlBarPlacement.Free)
            {
                // Switch to free mode on first drag, so the dropped position is remembered.
                _settings.ControlBarPlacement = ControlBarPlacement.Free;
                _settings.ControlBarCorner = ClosestCorner(
                    ControlBar.Margin.Left / Math.Max(1, ContentGrid.ActualWidth),
                    ControlBar.Margin.Top / Math.Max(1, ContentGrid.ActualHeight));
                ApplyControlBarPlacement();
                _controlBarDragOrigin = new Point(ControlBar.Margin.Left, ControlBar.Margin.Top);
            }
            _controlBarDragging = true;
            _controlBarDragStart = now;
            _controlBarDragOrigin = new Point(ControlBar.Margin.Left, ControlBar.Margin.Top);
            ControlBar.CaptureMouse();
        }

        var newLeft = _controlBarDragOrigin.X + (now.X - _controlBarDragStart.X);
        var newTop = _controlBarDragOrigin.Y + (now.Y - _controlBarDragStart.Y);

        // Clamp inside the picture's content area so the bar can't be dragged off-screen.
        var room = ContentGrid.ActualWidth;
        var height = ContentGrid.ActualHeight;
        var barWidth = ControlBar.ActualWidth;
        var barHeight = ControlBar.ActualHeight;
        const double inset = 12;
        newLeft = Math.Clamp(newLeft, inset, Math.Max(inset, room - barWidth - inset));
        newTop = Math.Clamp(newTop, inset, Math.Max(inset, height - barHeight - inset));

        ControlBar.HorizontalAlignment = HorizontalAlignment.Left;
        ControlBar.VerticalAlignment = VerticalAlignment.Top;
        ControlBar.Margin = new Thickness(newLeft, newTop, 0, 0);
    }

    /// <summary>Ends an armed press or a drag: releases the pointer, remembers the dropped
    /// spot, and snaps to the nearest corner when the bar landed almost exactly on one.</summary>
    private void OnControlBarGrabReleased(object sender, MouseButtonEventArgs? e)
    {
        _controlBarPressArmed = false;
        if (!_controlBarDragging) return;
        _controlBarDragging = false;
        if (ControlBar.IsMouseCaptured) ControlBar.ReleaseMouseCapture();

        if (_settings is not null)
        {
            // Persist the dropped position as fractions so the bar returns to the same spot
            // after a window resize or a relaunch.
            var room = ContentGrid.ActualWidth;
            var height = ContentGrid.ActualHeight;
            var barWidth = ControlBar.ActualWidth;
            var barHeight = ControlBar.ActualHeight;
            if (room > 0 && height > 0 && barWidth > 0 && barHeight > 0)
            {
                _settings.ControlBarFreeX = (ControlBar.Margin.Left + barWidth / 2) / room;
                _settings.ControlBarFreeY = (ControlBar.Margin.Top + barHeight / 2) / height;
            }

            // Snap to the nearest corner if it ended close enough that the user almost
            // certainly meant to park it there - 32 px is about a thumb's reach with the bar.
            var centreX = ControlBar.Margin.Left + barWidth / 2;
            var centreY = ControlBar.Margin.Top + barHeight / 2;
            var nearestX = centreX < room / 2 ? 12.0 : room - barWidth - 12.0;
            var nearestY = centreY < height / 2 ? 12.0 : height - barHeight - 12.0;
            if (Math.Abs(ControlBar.Margin.Left - nearestX) < 32
                && Math.Abs(ControlBar.Margin.Top - nearestY) < 32)
            {
                _settings.ControlBarPlacement = ControlBarPlacement.Corner;
                _settings.ControlBarCorner = ClosestCorner(centreX / room, centreY / height);
            }

            _settings.Save();
        }

        ApplyControlBarPlacement();
    }

    private void OnControlBarGrabDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // Cycle: floating -> corner -> free -> docked -> floating.
        var next = _settings?.ControlBarPlacement switch
        {
            ControlBarPlacement.Floating => ControlBarPlacement.Corner,
            ControlBarPlacement.Corner => ControlBarPlacement.Free,
            ControlBarPlacement.Free => ControlBarPlacement.Docked,
            _ => ControlBarPlacement.Floating,
        };
        SetControlBarPlacement(next);
        e.Handled = true;
    }

    /// <summary>Whatever takes the capture away - a menu opening, the window losing focus -
    /// ends the drag rather than leaving the pointer held by a bar nobody is dragging.</summary>
    private void OnControlBarLostMouseCapture(object sender, MouseEventArgs e)
    {
        if (_controlBarDragging)
        {
            _controlBarDragging = false;
            _controlBarPressArmed = false;
            ApplyControlBarPlacement();
        }
    }
}
