using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace SoulScreen.App;

/// <summary>
/// Window chrome: the toolbar that doubles as the caption and spills into a popup when the
/// window is narrow, the window controls, fullscreen with controls that fade in on pointer
/// movement, the aspect-ratio lock, keyboard shortcuts, and window placement.
/// </summary>
public partial class MainWindow
{
    /// <summary>Hides the pointer, and the overlaid controls, after a moment of stillness in fullscreen.</summary>
    private DispatcherTimer? _cursorTimer;

    private WindowAspectLock? _aspectLock;
    private WindowState _stateBeforeFullscreen = WindowState.Normal;
    private bool _isFullscreen;

    /// <summary>True while the fullscreen controls are on screen.</summary>
    private bool _chromeShown = true;

    private static readonly Duration ChromeFade = new(TimeSpan.FromMilliseconds(180));

    private void InitialiseChrome()
    {
        ConfigureMoreMenu();
        InitialiseControlBar();
        HideWhenCramped(ReceiverName);
        HideWhenCramped(StatusText);

        // Window-wide rather than on the video host alone: the log and the notice bar sit
        // over the picture in fullscreen, and a pointer resting on them would otherwise stay
        // hidden. Moving over the toolbar also keeps the pointer visible, which is right.
        MouseMove += OnPointerMoved;

        KeyDown += OnKeyDown;
        PreviewKeyDown += OnPreviewKeyDown;
        StateChanged += OnWindowStateChanged;
        SizeChanged += OnWindowSizeChanged;

        SourceInitialized += (_, _) =>
        {
            WindowFrame.SetDarkFrame(this, ThemeManager.IsDark);
            WindowFrame.RoundCorners(this);
            WindowFrame.UseCustomCaption(this);
            _aspectLock = new WindowAspectLock(this)
            {
                ChromeSize = () => new Size(
                    RootGrid.Margin.Left + RootGrid.Margin.Right,
                    RootGrid.Margin.Top + RootGrid.Margin.Bottom + ChromeHeight()),
            };
            UpdateAspectLock();
        };
    }

    /// <summary>Height of the toolbar and status bar as they take room from the picture.</summary>
    private double ChromeHeight() => _isFullscreen || _isMiniPlayer ? 0 : Toolbar.ActualHeight + StatusBar.ActualHeight;

    // ------------------------------------------------------------- placement

    /// <summary>Puts the window where it was last time, if that place is still on a screen.</summary>
    private void RestoreWindowBounds()
    {
        if (_settings.WindowWidth is not { } width || _settings.WindowHeight is not { } height) return;
        if (_settings.WindowLeft is not { } left || _settings.WindowTop is not { } top) return;

        var virtualScreen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        var proposed = new Rect(left, top, width, height);
        // Enough of the window must land on a screen to be grabbed: a monitor that has been
        // unplugged since must not leave the window somewhere it cannot be reached.
        var visible = Rect.Intersect(proposed, virtualScreen);
        if (visible.IsEmpty || visible.Width < 120 || visible.Height < 80) return;

        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = left;
        Top = top;
        Width = Math.Max(width, MinWidth);
        Height = Math.Max(height, MinHeight);
        if (_settings.WindowMaximized && !App.StartMinimised) WindowState = WindowState.Maximized;
    }

    private void SaveWindowBounds()
    {
        if (_isMiniPlayer)
        {
            RememberMiniPlayerPlacement();
            // The window to remember is the one the mini player was opened from.
            if (_boundsBeforeMini.Width > 0 && _boundsBeforeMini.Height > 0)
            {
                _settings.WindowLeft = _boundsBeforeMini.Left;
                _settings.WindowTop = _boundsBeforeMini.Top;
                _settings.WindowWidth = _boundsBeforeMini.Width;
                _settings.WindowHeight = _boundsBeforeMini.Height;
            }
            _settings.WindowMaximized = _stateBeforeMini == WindowState.Maximized;
            return;
        }

        if (_isFullscreen)
        {
            // The size to remember is the one fullscreen was entered from.
            _settings.WindowMaximized = _stateBeforeFullscreen == WindowState.Maximized;
            return;
        }

        var bounds = WindowState == WindowState.Normal
            ? new Rect(Left, Top, ActualWidth, ActualHeight)
            : RestoreBounds;
        if (bounds.Width <= 0 || bounds.Height <= 0) return;

        _settings.WindowLeft = bounds.Left;
        _settings.WindowTop = bounds.Top;
        _settings.WindowWidth = bounds.Width;
        _settings.WindowHeight = bounds.Height;
        _settings.WindowMaximized = WindowState == WindowState.Maximized;
    }

    // ---------------------------------------------------------------- toolbar

    private void OnPinChanged(object sender, RoutedEventArgs e)
    {
        var pinned = PinButton.IsChecked == true;
        // The mini player floats whatever the setting; the setting is what the window returns to.
        Topmost = pinned || _isMiniPlayer;
        if (_settings is null) return;
        _settings.AlwaysOnTop = pinned;
        _settings.Save();
        if (OnTopCheck is not null) OnTopCheck.IsChecked = pinned;
    }

    private void OnMinimise(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximise(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    /// <summary>Wires up the menu behind the toolbar's last button.</summary>
    private void ConfigureMoreMenu()
    {
        // Right edges aligned, just below the button: the menu opens back over the window
        // rather than past the edge of one that is only as wide as a phone.
        MorePopup.CustomPopupPlacementCallback = (popupSize, targetSize, _) =>
            [new CustomPopupPlacement(new Point(targetSize.Width - popupSize.Width + 10, targetSize.Height - 4), PopupPrimaryAxis.Horizontal)];

        // StaysOpen="False" closes the popup on any click outside it - including one on the
        // button, which would then toggle it straight back open. While it is open the button
        // is taken out of hit testing, so that click only closes it.
        MorePopup.Opened += (_, _) =>
        {
            MoreButton.IsHitTestVisible = false;
            // Keyboard users arrive in the menu, not left on a button behind it.
            Dispatcher.BeginInvoke(() => MoreMenu.Children.OfType<ButtonBase>().FirstOrDefault()?.Focus(), DispatcherPriority.Input);
        };
        MorePopup.Closed += (_, _) => MoreButton.IsHitTestVisible = true;

        // Every row has done its job once clicked.
        MoreMenu.AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler((_, _) => MoreButton.IsChecked = false));
        MoreMenu.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            MoreButton.IsChecked = false;
            MoreButton.Focus();
            e.Handled = true;
        };
    }

    /// <summary>
    /// Stops drawing a label once it is squeezed narrower than a short word. Trimmed to a few
    /// pixels, a name or status line shows as a stray dot or a sliver of a letter, which reads
    /// as a rendering fault rather than as text that did not fit.
    /// </summary>
    private static void HideWhenCramped(TextBlock label) =>
        label.SizeChanged += (_, _) => label.Opacity = label.ActualWidth < 24 ? 0 : 1;

    private void OnWindowSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_shuttingDown) return;
        UpdateAspectLock();
        UpdatePictureCorners();
        PositionOverlays();
        if (SettingsPanel.Visibility == Visibility.Visible) UpdateSettingsLayout();
    }

    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        if (_isMiniPlayer && WindowState == WindowState.Maximized)
        {
            // Win+Up or a snap: a maximised mini player would just be a window with no toolbar.
            Dispatcher.BeginInvoke(() =>
            {
                if (_isMiniPlayer && WindowState == WindowState.Maximized) WindowState = WindowState.Normal;
            });
            return;
        }

        UpdateRipple();

        // Restore glyph while maximised, maximise glyph otherwise.
        MaximiseButton.Content = WindowState == WindowState.Maximized ? "" : "";
        MaximiseButton.ToolTip = WindowState == WindowState.Maximized ? "Restore" : "Maximise";
        ApplyChromePadding();

        if (WindowState == WindowState.Minimized && _settings.MinimizeToTray && !_shuttingDown)
            HideToTray();
    }

    /// <summary>
    /// Recomputes the content margin from the current chrome. Always called after both the
    /// fullscreen flag and the window state have settled: reading them separately left one
    /// maximise pass computing padding for a fullscreen that had just ended.
    /// </summary>
    private void ApplyChromePadding()
    {
        RootGrid.Margin = _isFullscreen ? new Thickness(0) : WindowFrame.MaximisedPadding(this);
        ApplyOverlayInsets();
    }

    // ------------------------------------------------------------- fullscreen

    private void OnToggleFullscreen(object sender, RoutedEventArgs e) => ToggleFullscreen();

    private void ToggleFullscreen()
    {
        if (_isFullscreen) ExitFullscreen();
        else EnterFullscreen();
        UpdateAspectLock();
    }

    private void EnterFullscreen()
    {
        if (_isMiniPlayer) ExitMiniPlayer();
        // Minimised is never a state to come back to from fullscreen.
        _stateBeforeFullscreen = WindowState == WindowState.Minimized ? WindowState.Normal : WindowState;
        _isFullscreen = true;
        // The custom chrome reserves a resize border that would show as a seam against
        // the screen edge, so fullscreen drops it entirely.
        WindowFrame.RemoveCustomCaption(this);
        // Normal first: going straight from Maximized to fullscreen leaves the taskbar
        // drawn over the window.
        WindowState = WindowState.Normal;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        WindowState = WindowState.Maximized;
        MoreButton.IsChecked = false;
        FullscreenButton.Content = "";
        FullscreenButton.ToolTip = "Leave fullscreen (Esc)";
        ApplyChromePadding();

        // The toolbar and status bar move over the picture and appear on pointer movement.
        Grid.SetRow(Toolbar, 1);
        Toolbar.VerticalAlignment = VerticalAlignment.Top;
        Toolbar.SetResourceReference(BackgroundProperty, "OverlayChrome");
        Grid.SetRow(StatusBar, 1);
        StatusBar.VerticalAlignment = VerticalAlignment.Bottom;
        StatusBar.SetResourceReference(BackgroundProperty, "OverlayChrome");
        _chromeShown = true;
        ShowChrome();
        ApplyOverlayInsets();
        UpdatePictureCorners();

        _cursorTimer ??= CreateCursorTimer();
        _cursorTimer.Start();
        Dispatcher.InvokeAsync(UpdateControlBar, DispatcherPriority.Loaded);
        // The picture arrives as the chrome leaves: one eased move, not a hard swap.
        Dispatcher.InvokeAsync(() => TransitionContentForModeChange(entering: true), DispatcherPriority.Loaded);
    }

    private void ExitFullscreen()
    {
        _isFullscreen = false;
        WindowFrame.UseCustomCaption(this);
        WindowStyle = WindowStyle.SingleBorderWindow;
        ResizeMode = ResizeMode.CanResize;
        WindowState = _stateBeforeFullscreen;
        FullscreenButton.Content = "";
        FullscreenButton.ToolTip = "Fullscreen (F11)";
        ApplyChromePadding();

        Grid.SetRow(Toolbar, 0);
        Toolbar.VerticalAlignment = VerticalAlignment.Stretch;
        Toolbar.SetResourceReference(BackgroundProperty, "SurfaceRaised");
        Grid.SetRow(StatusBar, 2);
        StatusBar.VerticalAlignment = VerticalAlignment.Stretch;
        StatusBar.SetResourceReference(BackgroundProperty, "SurfaceRaised");
        foreach (var strip in new[] { Toolbar, StatusBar })
        {
            strip.BeginAnimation(OpacityProperty, null);
            strip.Opacity = 1;
            strip.IsHitTestVisible = true;
        }
        _chromeShown = true;
        ApplyOverlayInsets();
        UpdatePictureCorners();

        // The window may come back at the size it left, which raises no SizeChanged; fit the
        // control bar once the layout has settled.
        Dispatcher.InvokeAsync(UpdateControlBar, DispatcherPriority.Loaded);
        Dispatcher.InvokeAsync(() => TransitionContentForModeChange(entering: false), DispatcherPriority.Loaded);

        _cursorTimer?.Stop();
        Cursor = null;
    }

    /// <summary>
    /// In fullscreen the toolbar and status bar float over the content, where they would cover
    /// the head of every panel and the foot of the log; the panels are inset to clear them.
    /// </summary>
    private void ApplyOverlayInsets()
    {
        var top = _isFullscreen ? Toolbar.Height : 0;
        var bottom = _isFullscreen ? StatusBar.ActualHeight : 0;
        var inset = new Thickness(0, top, 0, bottom);
        SettingsPanel.Padding = inset;
        CapturesPanel.Padding = inset;
        HelpPanel.Padding = inset;
        DoctorPanel.Padding = inset;
        ApprovalPanel.Padding = inset;
        ViewerPanel.Padding = inset;
        LogPanel.Margin = new Thickness(0, 0, 0, bottom);
        PositionOverlays();
    }

    /// <summary>
    /// The slide-and-fade that tells the eye the chrome has changed modes, the same 180 ms
    /// language the captures toolbar speaks: content eases eight pixels up from below when
    /// the picture takes the whole window, and settles down into place when the chrome
    /// returns. Skipped entirely when Windows has been asked to keep animation to a minimum.
    /// </summary>
    private void TransitionContentForModeChange(bool entering)
    {
        if (!Motion.Enabled)
        {
            VideoHost.BeginAnimation(OpacityProperty, null);
            VideoHost.Opacity = 1;
            var rt = VideoHost.RenderTransform as TranslateTransform;
            if (rt is not null)
            {
                rt.BeginAnimation(TranslateTransform.YProperty, null);
                rt.Y = 0;
            }
            return;
        }

        var y = entering ? 10.0 : -10.0;
        if (VideoHost.RenderTransform is not TranslateTransform transform)
        {
            transform = new TranslateTransform();
            VideoHost.RenderTransform = transform;
        }
        transform.BeginAnimation(TranslateTransform.YProperty, null);
        transform.Y = y;
        transform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });

        VideoHost.BeginAnimation(OpacityProperty, null);
        VideoHost.Opacity = 0.5;
        VideoHost.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });
    }

    private DispatcherTimer CreateCursorTimer()
    {
        var timer = new DispatcherTimer(DispatcherPriority.Input)
        {
            Interval = TimeSpan.FromMilliseconds(2400),
        };
        timer.Tick += (_, _) => OnStillnessInFullscreen();
        return timer;
    }

    /// <summary>
    /// Brings the pointer and the overlaid controls back on movement and restarts the
    /// countdown that hides them again. Only in fullscreen: hiding the pointer over a
    /// windowed picture would strand the user with no way to reach the toolbar.
    /// </summary>
    private void OnPointerMoved(object sender, MouseEventArgs e)
    {
        OnPointerMovedForControlBar(e);
        if (!_isFullscreen) return;
        if (Cursor == Cursors.None) Cursor = null;
        ShowChrome();
        _cursorTimer?.Stop();
        _cursorTimer?.Start();
    }

    private void OnStillnessInFullscreen()
    {
        _cursorTimer?.Stop();
        if (!_isFullscreen) return;
        // A pointer resting on the controls, or a menu open from them, means they are in use.
        if (Toolbar.IsMouseOver || StatusBar.IsMouseOver || MoreButton.IsChecked == true || IsControlBarInUse) return;
        if (IsPanelOpen()) return;
        HideChrome();
        Cursor = Cursors.None;
    }

    private void ShowChrome()
    {
        if (!_isFullscreen || _chromeShown) return;
        _chromeShown = true;
        foreach (var strip in new[] { Toolbar, StatusBar })
        {
            strip.IsHitTestVisible = true;
            strip.BeginAnimation(OpacityProperty, new DoubleAnimation(1, ChromeFade)
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            });
        }
    }

    private void HideChrome()
    {
        if (!_chromeShown) return;
        _chromeShown = false;
        foreach (var strip in new[] { Toolbar, StatusBar })
        {
            strip.IsHitTestVisible = false;
            strip.BeginAnimation(OpacityProperty, new DoubleAnimation(0, new Duration(TimeSpan.FromMilliseconds(320)))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
            });
        }
    }

    // ------------------------------------------------------------ aspect lock

    /// <summary>Points the resize hook at the picture's shape, or switches it off when there
    /// is no picture, the fit mode ignores shape, or the user has turned it off.</summary>
    private void UpdateAspectLock()
    {
        if (_aspectLock is null) return;
        var size = RotatedVideoSize();
        var active = _settings.LockAspectRatio
                     && VideoHost.Visibility == Visibility.Visible
                     && _settings.VideoFit == VideoFit.Fit
                     && size.Width > 0 && size.Height > 0
                     && !_isFullscreen;
        _aspectLock.Enabled = active;
        _aspectLock.Aspect = active ? size.Width / size.Height : 0;
    }

    // ---------------------------------------------------------------- keyboard

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        // The lock screen owns the keyboard while it is up: nothing behind it - the palette,
        // fullscreen, a shortcut - may act while SoulScreen is locked.
        if (IsLocked) return;

        var inTextEntry = Keyboard.FocusedElement is TextBoxBase;
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);

        // Escape always works, and so do the function keys; the Ctrl chords belong to the
        // mirror, not to whatever word the user is halfway through typing.
        if (e.Key == Key.Escape)
        {
            if (HandleEscape()) e.Handled = true;
            return;
        }

        if (e.Key == Key.F11)
        {
            ToggleFullscreen();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.F1)
        {
            ToggleHelp();
            e.Handled = true;
            return;
        }

        // The palette answers from anywhere, a text field included: it is how people look for
        // a command they cannot find, and that is not the moment to be told to click elsewhere.
        if (ctrl && !shift && e.Key == Key.K)
        {
            ShowPalette();
            e.Handled = true;
            return;
        }

        if (ctrl && !shift && e.Key == Key.F && SettingsPanel.Visibility == Visibility.Visible)
        {
            SettingsSearchBox.Focus();
            SettingsSearchBox.SelectAll();
            e.Handled = true;
            return;
        }

        if (HandleMarkupKey(e))
        {
            e.Handled = true;
            return;
        }

        if (inTextEntry || !ctrl) return;

        var handled = true;
        switch (e.Key)
        {
            case Key.S when !shift: SaveSnapshot(); break;
            case Key.E when !shift: ToggleMarkup(); break;
            case Key.C when shift: CopySnapshot(); break;
            case Key.M when shift: ToggleMiniPlayer(); break;
            case Key.P when shift: TogglePresentationMode(); break;
            case Key.M: MuteButton.IsChecked = MuteButton.IsChecked != true; break;
            case Key.G: CapturesButton.IsChecked = CapturesButton.IsChecked != true; break;
            case Key.R when shift: RotateBy(90); break;
            case Key.R when RecordButton.IsEnabled: RecordButton.IsChecked = RecordButton.IsChecked != true; break;
            case Key.I: ToggleStats(); break;
            case Key.T: PinButton.IsChecked = PinButton.IsChecked != true; break;
            case Key.L: LogButton.IsChecked = LogButton.IsChecked != true; break;
            case Key.H: ToggleFocusMode(); break;
            case Key.OemComma: SettingsButton.IsChecked = SettingsButton.IsChecked != true; break;
            case Key.D: DisconnectDevice(); break;
            case Key.OemPlus or Key.Add: ZoomBy(1.25, null); break;
            case Key.OemMinus or Key.Subtract: ZoomBy(1 / 1.25, null); break;
            case Key.D0 or Key.NumPad0: ResetZoom(); break;
            case Key.D1: SetVideoFit(VideoFit.Fit); break;
            case Key.D2: SetVideoFit(VideoFit.Fill); break;
            case Key.D3: SetVideoFit(VideoFit.Stretch); break;
            case Key.D4: SetVideoFit(VideoFit.Actual); break;
            case Key.Up: NudgeVolume(+0.05); break;
            case Key.Down: NudgeVolume(-0.05); break;
            default: handled = false; break;
        }

        e.Handled = handled;
    }

    /// <summary>
    /// Space pauses the picture, as it does in every video player. Taken in the preview pass,
    /// so a toolbar button that kept focus after being clicked does not swallow the key and
    /// press itself a second time; Enter still activates a focused button.
    /// </summary>
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (IsLocked) return;

        // The viewer's keys come first: nothing under it should also act on them.
        if (HandleViewerKey(e))
        {
            e.Handled = true;
            return;
        }

        if (e.Key != Key.Space || Keyboard.Modifiers != ModifierKeys.None) return;
        if (VideoHost.Visibility != Visibility.Visible || IsPanelOpen()) return;
        if (Keyboard.FocusedElement is TextBoxBase or ComboBox or ComboBoxItem or ListBoxItem or MenuItem) return;

        TogglePause();
        e.Handled = true;
    }

    /// <summary>True while something covers the picture that the keyboard belongs to.</summary>
    private bool IsPanelOpen() =>
        PaletteOverlay.Visibility == Visibility.Visible
        || SettingsPanel.Visibility == Visibility.Visible
        || CapturesPanel.Visibility == Visibility.Visible
        || HelpPanel.Visibility == Visibility.Visible
        || ViewerPanel.Visibility == Visibility.Visible
        || DoctorPanel.Visibility == Visibility.Visible
        || WelcomeOverlay.Visibility == Visibility.Visible
        || LockOverlay.Visibility == Visibility.Visible;

    /// <summary>Escape peels back one layer at a time: the palette, the welcome sheet, help, the
    /// viewer, the connection check, captures, settings, markup, the mini player, fullscreen, zoom,
    /// and last the log.</summary>
    private bool HandleEscape()
    {
        if (PaletteOverlay.Visibility == Visibility.Visible) { ClosePalette(); return true; }
        if (IsWelcomeOpen) { CloseWelcome(); return true; }
        if (HelpPanel.Visibility == Visibility.Visible) { CloseHelp(); return true; }
        if (IsViewerOpen) { CloseViewer(); return true; }
        if (IsDoctorOpen) { CloseDoctor(); return true; }
        if (CapturesPanel.Visibility == Visibility.Visible) { CapturesButton.IsChecked = false; return true; }
        if (SettingsPanel.Visibility == Visibility.Visible) { SettingsButton.IsChecked = false; return true; }
        if (MarkupColorButton.IsChecked == true) { MarkupColorButton.IsChecked = false; return true; }
        if (IsMarkupActive) { MarkupButton.IsChecked = false; return true; }
        if (_presentationMode) { TogglePresentationMode(); return true; }
        if (_focusMode) { SetFocusMode(false); return true; }
        if (_isMiniPlayer) { ExitMiniPlayer(); return true; }
        if (_isFullscreen) { ToggleFullscreen(); return true; }
        if (IsZoomed) { ResetZoom(); return true; }
        if (LogPanel.Visibility == Visibility.Visible) { LogButton.IsChecked = false; return true; }
        return false;
    }

    // ------------------------------------------------------------------- help

    private void OnShowHelp(object sender, RoutedEventArgs e) => ToggleHelp();

    private void OnCloseHelp(object sender, RoutedEventArgs e) => CloseHelp();

    private void ToggleHelp()
    {
        if (HelpPanel.Visibility == Visibility.Visible) CloseHelp();
        else
        {
            if (_isMiniPlayer) ExitMiniPlayer();
            HelpPanel.Visibility = Visibility.Visible;
            FadeContentIn(HelpPanel);
        }
    }

    private void CloseHelp() => HelpPanel.Visibility = Visibility.Collapsed;

    private static readonly (string Keys, string Action)[] Shortcuts =
    [
        ("Ctrl+K", "Find any command"),
        ("F11", "Fullscreen; Esc leaves"),
        ("Ctrl+Shift+M", "Mini player; double-click returns"),
        ("Space", "Pause or resume the picture"),
        ("Ctrl+S", "Save a screenshot"),
        ("Ctrl+Shift+C", "Copy a screenshot to the clipboard"),
        ("Ctrl+R", "Start or stop recording"),
        ("Ctrl+E", "Markup: draw over the picture"),
        ("Ctrl+Z", "Undo the last stroke, while marking up"),
        ("Ctrl+G", "Captures"),
        ("Ctrl+M", "Mute the phone's audio"),
        ("Ctrl+↑ / ↓", "Volume up and down"),
        ("Ctrl+D", "Disconnect the iPhone"),
        ("Ctrl+1 … 4", "Fit, fill, stretch, actual size"),
        ("Ctrl+Shift+R", "Rotate 90° clockwise"),
        ("Ctrl+wheel", "Zoom in and out; drag to pan"),
        ("Ctrl+0", "Reset the zoom"),
        ("Ctrl+I", "Statistics overlay"),
        ("Ctrl+T", "Keep the window on top"),
        ("Ctrl+H", "Focus mode: hide the chrome; Esc or Ctrl+H brings it back"),
        ("Ctrl+Shift+P", "Presentation mode: fullscreen and focus mode together"),
        ("Ctrl+L", "Activity log"),
        ("Ctrl+,", "Settings"),
        ("Ctrl+Alt+Shift+S", "Screenshot from any app, once switched on"),
        ("F1", "This list"),
    ];

    private void InitialiseShortcutList()
    {
        foreach (var (keys, action) in Shortcuts)
        {
            var row = new Grid { Margin = new Thickness(0, 5, 0, 5) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(176) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var caps = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            foreach (var part in keys.Split('+'))
            {
                var cap = new TextBlock
                {
                    Text = part,
                    FontSize = 11.5,
                    FontFamily = (FontFamily)FindResource("TextFont"),
                };
                cap.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
                caps.Children.Add(new Border { Style = (Style)FindResource("KeyCap"), Child = cap });
            }
            row.Children.Add(caps);

            var label = new TextBlock
            {
                Text = action,
                Style = (Style)FindResource("Subheading"),
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(label, 1);
            row.Children.Add(label);

            ShortcutList.Children.Add(row);
        }
    }
}
