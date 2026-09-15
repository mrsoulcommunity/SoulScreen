using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using SoulScreen.App.Logic;
using SoulScreen.Media;

namespace SoulScreen.App;

/// <summary>
/// The command palette: one search field that reaches everything the window can do, the way
/// Spotlight reaches everything on a Mac. It lists only what can be done right now - there is
/// no "Stop recording" while nothing is recording - and each row carries its shortcut, so the
/// palette also teaches the keyboard.
/// </summary>
public partial class MainWindow
{
    /// <summary>One row of the palette. Built fresh each time it opens, so titles such as
    /// "Mute" or "Unmute" describe the state the window is actually in.</summary>
    private sealed record PaletteCommand(string Title, string Group, string Glyph, string? Shortcut, Action Run, string? Keywords = null)
    {
        public Visibility ShortcutVisibility => Shortcut is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private List<PaletteCommand> _paletteCommands = [];

    private static readonly Duration PaletteFade = new(TimeSpan.FromMilliseconds(150));

    private void InitialisePalette()
    {
        // Rows never take focus, and neither does the list: typing has to stay in the field
        // while the arrow keys move the selection.
        PaletteList.Focusable = false;
    }

    private void OnShowPalette(object sender, RoutedEventArgs e) => ShowPalette();

    private void ShowPalette()
    {
        if (_shuttingDown) return;

        if (PaletteOverlay.Visibility == Visibility.Visible)
        {
            FocusPaletteSearch();
            return;
        }

        // The mini player is too small to hold the palette, and the palette is how people get
        // back to everything else anyway.
        if (_isMiniPlayer) ExitMiniPlayer();
        MoreButton.IsChecked = false;

        _paletteCommands = AvailableCommands().ToList();
        PaletteSearch.Text = string.Empty;
        FilterPalette();

        PaletteOverlay.Visibility = Visibility.Visible;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        PaletteScrim.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, PaletteFade));
        PaletteCard.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, PaletteFade));
        PaletteScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.97, 1, PaletteFade) { EasingFunction = ease });
        PaletteScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.97, 1, PaletteFade) { EasingFunction = ease });

        FocusPaletteSearch();
    }

    private void FocusPaletteSearch() =>
        // After layout: a field that has only just been made visible cannot take focus yet.
        Dispatcher.BeginInvoke(() =>
        {
            if (PaletteOverlay.Visibility != Visibility.Visible) return;
            PaletteSearch.Focus();
            Keyboard.Focus(PaletteSearch);
            PaletteSearch.SelectAll();
        }, DispatcherPriority.Input);

    private void ClosePalette()
    {
        if (PaletteOverlay.Visibility != Visibility.Visible) return;
        var hadFocus = PaletteOverlay.IsKeyboardFocusWithin;
        PaletteOverlay.Visibility = Visibility.Collapsed;
        PaletteList.ItemsSource = null;
        _paletteCommands = [];
        // Focus left in a collapsed field would swallow the next shortcut.
        if (hadFocus) Focus();
    }

    private void OnPaletteScrimClicked(object sender, MouseButtonEventArgs e)
    {
        ClosePalette();
        e.Handled = true;
    }

    private void OnPaletteQueryChanged(object sender, TextChangedEventArgs e) => FilterPalette();

    private void FilterPalette()
    {
        var query = PaletteSearch.Text;
        var ranked = _paletteCommands
            .Select((command, index) => (Command: command, Index: index,
                Score: CommandMatcher.Score(query, command.Title, $"{command.Group} {command.Keywords}")))
            .Where(entry => entry.Score is not null)
            .OrderByDescending(entry => entry.Score)
            .ThenBy(entry => entry.Index)
            .Select(entry => entry.Command)
            .ToList();

        PaletteList.ItemsSource = ranked;
        PaletteList.SelectedIndex = ranked.Count > 0 ? 0 : -1;
        if (ranked.Count > 0) PaletteList.ScrollIntoView(ranked[0]);

        PaletteList.Visibility = ranked.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        PaletteEmpty.Visibility = ranked.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
        PaletteHintText.Visibility = query.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnPaletteKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                ClosePalette();
                break;
            case Key.Down:
                MovePaletteSelection(+1, wrap: true);
                break;
            case Key.Up:
                MovePaletteSelection(-1, wrap: true);
                break;
            case Key.PageDown:
                MovePaletteSelection(+8, wrap: false);
                break;
            case Key.PageUp:
                MovePaletteSelection(-8, wrap: false);
                break;
            case Key.Enter:
                if (PaletteList.SelectedItem is PaletteCommand command) RunPaletteCommand(command);
                break;
            case Key.Tab:
                // Nowhere else in the palette to go.
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    private void MovePaletteSelection(int delta, bool wrap)
    {
        var count = PaletteList.Items.Count;
        if (count == 0) return;

        var index = PaletteList.SelectedIndex + delta;
        index = wrap ? ((index % count) + count) % count : Math.Clamp(index, 0, count - 1);
        PaletteList.SelectedIndex = index;
        PaletteList.ScrollIntoView(PaletteList.SelectedItem);
    }

    private void OnPaletteListClicked(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source) return;
        if (ItemsControl.ContainerFromElement(PaletteList, source) is not ListBoxItem { DataContext: PaletteCommand command }) return;
        RunPaletteCommand(command);
        e.Handled = true;
    }

    private void RunPaletteCommand(PaletteCommand command)
    {
        ClosePalette();
        try
        {
            command.Run();
        }
        catch (Exception ex)
        {
            _log.Error($"the command \"{command.Title}\" failed", ex);
            ShowToast("That did not work - the activity log has the details", "");
        }
    }

    /// <summary>Everything that can be done in the window's present state, in a sensible order.</summary>
    private IEnumerable<PaletteCommand> AvailableCommands()
    {
        var streaming = VideoHost.Visibility == Visibility.Visible;

        if (streaming)
        {
            yield return new(_isFullscreen ? "Leave fullscreen" : "Enter fullscreen", "Picture",
                _isFullscreen ? "" : "", "F11", ToggleFullscreen, "full screen maximise");
            yield return new("Mini player", "Picture", "", "Ctrl+Shift+M", ToggleMiniPlayer,
                "picture in picture pip float small compact");
            yield return new(Video.IsFrozen ? "Resume the picture" : "Pause the picture", "Picture",
                Video.IsFrozen ? "" : "", "Space", TogglePause, "freeze hold still stop play");
            yield return new(IsMarkupActive ? "Leave markup" : "Markup", "Picture", "\uEDFB", "Ctrl+E", ToggleMarkup,
                "draw pen annotate highlight highlighter laser pointer sketch presentation");
            if (IsMarkupActive && MarkupClearButton.IsEnabled)
                yield return new("Clear the drawing", "Picture", "\uE74D", "Delete", () => ClearMarkupStrokes(recordHistory: true),
                    "erase markup strokes remove");

            yield return new("Save screenshot", "Capture", "", "Ctrl+S", () => SaveSnapshot(), "image picture photo png jpeg");
            yield return new("Copy screenshot", "Capture", "", "Ctrl+Shift+C", CopySnapshot, "clipboard image");
            if (RecordButton.IsEnabled)
            {
                var recording = RecordButton.IsChecked == true;
                yield return new(recording ? "Stop recording" : "Start recording", "Capture",
                    recording ? "" : "", "Ctrl+R", () => RecordButton.IsChecked = !recording, "video mp4 record");
                var armed = _recordingTimer is { IsArmed: true };
                yield return new(armed ? "Cancel the timed stop" : "Stop recording in 5 minutes", "Capture",
                    "", null, () =>
                    {
                        if (armed) OnMenuCancelTimedStop(this, new RoutedEventArgs());
                        else OnMenuRecordTimed(new System.Windows.Controls.MenuItem { Tag = "5" }, new RoutedEventArgs());
                    }, "timed stop countdown minutes limit");
            }

            if (_settings.VideoFit != VideoFit.Fit)
                yield return new("Fit the picture to the window", "Picture", "", "Ctrl+1", () => SetVideoFit(VideoFit.Fit), "letterbox");
            if (_settings.VideoFit != VideoFit.Fill)
                yield return new("Fill the window", "Picture", "", "Ctrl+2", () => SetVideoFit(VideoFit.Fill), "crop");
            if (_settings.VideoFit != VideoFit.Stretch)
                yield return new("Stretch to the window", "Picture", "", "Ctrl+3", () => SetVideoFit(VideoFit.Stretch), "distort");
            if (_settings.VideoFit != VideoFit.Actual)
                yield return new("Actual size", "Picture", "", "Ctrl+4", () => SetVideoFit(VideoFit.Actual), "1:1 pixel");

            yield return new("Rotate 90° clockwise", "Picture", "", "Ctrl+Shift+R", () => RotateBy(90), "turn landscape portrait");
            if (_settings.Rotation != 0)
                yield return new("Turn the picture upright", "Picture", "", null, () => SetRotation(0), "rotate reset");
            yield return new(_settings.MirrorHorizontally ? "Stop mirroring horizontally" : "Mirror horizontally", "Picture",
                "", null, () => SetMirror(!_settings.MirrorHorizontally), "flip");

            yield return new("Zoom in", "Picture", "", "Ctrl++", () => ZoomBy(1.25, null), "magnify larger");
            if (IsZoomed)
            {
                yield return new("Zoom out", "Picture", "", "Ctrl+-", () => ZoomBy(1 / 1.25, null), "smaller");
                yield return new("Reset the zoom", "Picture", "", "Ctrl+0", ResetZoom, "actual");
            }

            yield return new(_settings.ShowStats ? "Hide statistics" : "Show statistics", "Picture", "", "Ctrl+I",
                ToggleStats, "fps latency bitrate overlay hud");

            if (_audio is not null)
            {
                var muted = MuteButton.IsChecked == true;
                yield return new(muted ? "Unmute the phone" : "Mute the phone", "Audio", muted ? "" : "", "Ctrl+M",
                    () => MuteButton.IsChecked = !muted, "sound silence");
                yield return new("Volume up", "Audio", "", "Ctrl+↑", () => NudgeVolume(+0.1), "louder");
                yield return new("Volume down", "Audio", "", "Ctrl+↓", () => NudgeVolume(-0.1), "quieter");
            }

            yield return new(_demo is null ? "Disconnect the iPhone" : "End the demo", "Receiver", "", "Ctrl+D",
                DisconnectDevice, "stop end close session");
        }

        if (!_receiverBusy)
        {
            if (_receiver is not null)
                yield return new("Stop the receiver", "Receiver", "", null, async () => await ToggleReceiverAsync(), "airplay advertise off");
            else if (_demo is null)
                yield return new("Start the receiver", "Receiver", "", null, async () => await ToggleReceiverAsync(), "airplay advertise on");

            if (_demo is null && DemoSource.IsAvailable)
                yield return new("Try the demo", "Receiver", "", null, OnDemoRequested, "test pattern sample no phone");
        }

        yield return new("Captures", "Go to", "", "Ctrl+G", ShowCaptures, "gallery screenshots recordings photos videos library");
        yield return new("Settings", "Go to", "", "Ctrl+,", () => SettingsButton.IsChecked = true, "preferences options");
        yield return new(LogPanel.Visibility == Visibility.Visible ? "Hide the activity log" : "Activity log", "Go to", "", "Ctrl+L",
            () => LogButton.IsChecked = LogButton.IsChecked != true, "log debug trace diagnostics");
        yield return new("Keyboard shortcuts", "Go to", "", "F1", ToggleHelp, "keys help");
        yield return new(_focusMode ? "Leave focus mode" : "Focus mode", "Window", "", "Ctrl+H", ToggleFocusMode,
            "focus hide chrome clean view presentation only the picture nothing else distraction");
        yield return new(_settings.ShowPerformanceGraph ? "Hide the performance graph" : "Show the performance graph", "Go to", "",
            null, () => SetShowPerformanceGraph(!_settings.ShowPerformanceGraph), "sparkline fps graph overlay statistics performance");

        if (_displayChoices.Count > 1)
        {
            for (var i = 0; i < _displayChoices.Count; i++)
            {
                var display = _displayChoices[i];
                yield return new($"Move to display {i + 1}{(display.IsPrimary ? " (primary)" : "")}", "Window", "", null,
                    () =>
                    {
                        _settings.TargetDisplay = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
                        _settings.Save();
                        DisplayService.MoveTo(this, display);
                        if (SettingsPanel.Visibility == Visibility.Visible) PopulateDisplayChoices();
                    }, "display monitor screen second");
            }
        }
        yield return new("Check the connection", "Go to", "\uE930", null, ShowDoctor,
            "troubleshoot doctor diagnose firewall network help cannot find missing not showing");
        yield return new("Welcome screen", "Go to", "\uE95A", null, ShowWelcome, "introduction tour getting started help");
        yield return new("Open the capture folder", "Go to", "", null, () => OpenFolder(_settings.CaptureDirectory), "explorer files");
        yield return new("Open the log folder", "Go to", "", null,
            () => OpenFolder(App.LogDirectory ?? System.IO.Path.Combine(AppSettings.Directory, "logs")), "explorer files");

        yield return new(Topmost && !_isMiniPlayer ? "Stop keeping on top" : "Keep on top", "Window", "", "Ctrl+T",
            () => PinButton.IsChecked = PinButton.IsChecked != true, "pin always above");
        if (_tray is not null)
            yield return new("Hide to the notification area", "Window", "", null, HideToTray, "tray background minimise");

        yield return new(_settings.RoundedCorners ? "Square the picture's corners" : "Round the picture's corners", "Picture", "\uF16B", null,
            () => SetRoundedCorners(!_settings.RoundedCorners), "rounded corners radius screen shape");
        yield return new(_settings.AskBeforeMirroring ? "Stop asking before an iPhone mirrors" : "Ask before an iPhone mirrors", "Privacy",
            "\uEA18", null, () => SetAskBeforeMirroring(!_settings.AskBeforeMirroring), "privacy approve allow permission trust block security");
        if (_settings.AllowedDevices.Count + _settings.BlockedDevices.Count > 0)
            yield return new("Allowed and blocked iPhones", "Privacy", "\uEA18", null, () => ShowSettingsSection("PRIVACY"),
                "privacy unblock remove trust list devices");
        yield return new(_settings.GlobalHotkeys ? "Stop the shortcuts in other apps" : "Use shortcuts from any app", "Window", "\uE765", null,
            () => SetGlobalHotkeys(!_settings.GlobalHotkeys), "global hotkeys keyboard system wide background");

        yield return new(_settings.Animations switch
        {
            MotionPreference.AlwaysOn => "Stop always animating",
            MotionPreference.AlwaysOff => "Turn animations back on",
            _ => "Switch animations off",
        }, "Appearance", "\uE785", null, () =>
        {
            _settings.Animations = _settings.Animations switch
            {
                MotionPreference.AlwaysOn => MotionPreference.FollowWindows,
                MotionPreference.AlwaysOff => MotionPreference.AlwaysOn,
                _ => MotionPreference.AlwaysOff,
            };
            _settings.Save();
            if (SettingsPanel.Visibility == Visibility.Visible) PopulateSettingsForm();
        }, "animations motion reduce transitions still");

        foreach (var theme in Enum.GetValues<AppTheme>())
        {
            if (theme == _settings.Theme) continue;
            yield return new($"Use the {theme.ToString().ToLowerInvariant()} theme", "Appearance",
                theme switch { AppTheme.Dark => "", AppTheme.Light => "", _ => "" },
                null, () => SetTheme(theme), "appearance mode colours dark light system");
        }

        foreach (var accent in Enum.GetValues<AccentColor>())
        {
            if (accent == _settings.Accent) continue;
            yield return new($"Accent colour: {accent}", "Appearance", "", null, () => SetAccent(accent), "color tint highlight");
        }

        yield return new("Quit SoulScreen", "App", "", null, () =>
        {
            _quitRequested = true;
            Close();
        }, "exit close");
    }

    private async void OnDemoRequested()
    {
        if (!TryBeginReceiverWork()) return;
        try { await StartDemoAsync(); }
        finally { EndReceiverWork(); }
    }
}
