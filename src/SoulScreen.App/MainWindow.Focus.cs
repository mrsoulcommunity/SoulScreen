using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SoulScreen.App;

/// <summary>
/// The mini player's playback controls, and the focus mode that hides every strip of chrome so
/// only the picture is on the window.
/// <para>
/// The mini player is small by design, so the controls it cannot fit live under a "More"
/// button: everything the player can do is on one menu, rebuilt each time it opens from the
/// window's present state, so there is never a "Pause" against a picture that is already
/// paused. Focus mode is a persistent view, not a fade: the toolbar and status bar are
/// collapsed rather than faded, so the layout underneath simply grows, and leaving it puts
/// back whatever was there before - including a fullscreen or mini player it was asked over.
/// </para>
/// </summary>
public partial class MainWindow
{
    // ------------------------------------------------------------------ mini player menu

    private void InitialiseMiniMenu()
    {
        // Set as the mini grid's context menu in the XAML, so a right-click on the player
        // opens it as well as the More button; only where it opens differs.
        MiniMenu.Opened += (_, _) => RebuildMiniMenu();
    }

    private void OnMiniMenu(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        MiniMenu.PlacementTarget = button;
        MiniMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Top;
        MiniMenu.IsOpen = true;
    }

    /// <summary>Rebuilds the mini menu from the window's present state, so there is never a
    /// "Pause" against a picture that is already paused, and nothing is offered that cannot
    /// be done from a window this small.</summary>
    private void RebuildMiniMenu()
    {
        MiniMenu.Items.Clear();

        if (VideoHost.Visibility == Visibility.Visible)
        {
            AddMiniItem(Video.IsFrozen ? "Resume the picture" : "Pause the picture", "\uE769",
                "Space", TogglePause);

            if (_pipeline is not null)
            {
                var recording = RecordButton.IsChecked == true;
                AddMiniItem(recording ? "Stop recording" : "Record to MP4", recording ? "\uE71A" : "\uE70F",
                    "Ctrl+R", () => RecordButton.IsChecked = !recording);
            }

            AddMiniItem("Save a screenshot", "\uE722", "Ctrl+S", () => SaveSnapshot());

            if (_audio is not null)
            {
                var muted = MuteButton.IsChecked == true;
                AddMiniItem(muted ? "Unmute the audio" : "Mute the audio", muted ? "\uE74F" : "\uE767",
                    "Ctrl+M", () => MuteButton.IsChecked = !muted);
            }
        }

        if (_pipeline is not null || _demo is not null)
        {
            var label = _demo is null ? "Disconnect the iPhone" : "End the demo";
            AddMiniItem(label, "\uE8F4", "Ctrl+D", DisconnectDevice);
            MiniMenu.Items.Add(new Separator());
        }

        if (VideoHost.Visibility == Visibility.Visible)
            AddMiniItem("Show the window", "\uE8A7", "Esc", ExitMiniPlayer);
        AddMiniItem("Quit SoulScreen", "\uE711", null, () =>
        {
            _quitRequested = true;
            Close();
        });
    }

    private void AddMiniItem(string header, string glyph, string? shortcut, Action run)
    {
        var item = new MenuItem
        {
            Header = header,
            Icon = new TextBlock
            {
                Text = glyph,
                FontFamily = (FontFamily)FindResource("IconFont"),
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
            },
            InputGestureText = shortcut ?? string.Empty,
        };
        item.Click += (_, _) => run();
        MiniMenu.Items.Add(item);
    }

    // --------------------------------------------------------------------- presentation mode

    /// <summary>True while presentation mode owns the fullscreen and focus transitions.</summary>
    private bool _presentationMode;
    private bool _presentationEnteredFullscreen;
    private bool _presentationEnteredFocus;

    /// <summary>Combines fullscreen and focus mode for presenting or teaching. It remembers
    /// which transitions it made, so leaving never undoes a state the user had already chosen.</summary>
    private void OnPresentationMode(object sender, RoutedEventArgs e) => TogglePresentationMode();

    private void TogglePresentationMode()
    {
        if (VideoHost.Visibility != Visibility.Visible)
        {
            ShowToast("Presentation mode is for when an iPhone is on screen", "\uE7B3");
            return;
        }

        if (_presentationMode)
        {
            _presentationMode = false;
            if (_presentationEnteredFocus && _focusMode) SetFocusMode(false);
            if (_presentationEnteredFullscreen && _isFullscreen) ToggleFullscreen();
            ShowToast("Presentation mode ended", "\uE7B3");
            return;
        }

        _presentationMode = true;
        _presentationEnteredFullscreen = !_isFullscreen;
        _presentationEnteredFocus = !_focusMode;
        if (_presentationEnteredFullscreen) ToggleFullscreen();
        if (_presentationEnteredFocus) SetFocusMode(true);
        ShowToast("Presentation mode · Ctrl+Shift+P to leave", "\uE7B3");
    }

    // --------------------------------------------------------------------- focus mode

    /// <summary>True while only the picture is showing: no toolbar, no status bar.</summary>
    private bool _focusMode;

    private void SetFocusMode(bool on)
    {
        if (_focusMode == on) return;
        _focusMode = on;

        // Focus mode only ever hides the strips; it leaves fullscreen and the mini player's
        // own state alone, so leaving it puts back whatever view it was asked over. The mini
        // player keeps its strips hidden in its own right, and fullscreen brings them back
        // with its auto-hide on the next pointer move.
        var visible = !on && !_isMiniPlayer;
        Toolbar.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        StatusBar.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        ApplyChromePadding();
        PositionOverlays();
        if (!on && _isFullscreen) ShowChrome();
        if (on) ShowToast("Focus mode - press Ctrl+H to bring the chrome back", "\uE7B3");
    }

    private void ToggleFocusMode()
    {
        // Focus mode is for watching a session; the idle screen has nothing to hide.
        if (VideoHost.Visibility != Visibility.Visible)
        {
            ShowToast("Focus mode is for when an iPhone is on screen", "\uE7B3");
            return;
        }
        SetFocusMode(!_focusMode);
    }

    /// <summary>Sessions end; focus mode would leave an empty black window behind one, and the
    /// fullscreen presentation mode entered would still be running.</summary>
    private void LeaveFocusModeForSessionEnd()
    {
        // Clear presentation mode first so a session end mid-presentation does not leave the
        // window in a state the user can no longer describe or undo through Ctrl+Shift+P.
        _presentationMode = false;
        _presentationEnteredFullscreen = false;
        _presentationEnteredFocus = false;
        if (!_shuttingDown && _focusMode) SetFocusMode(false);
    }
}
