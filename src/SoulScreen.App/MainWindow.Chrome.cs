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
    /// <summary>The toolbar's actions, in the order the toolbar shows them.</summary>
    private FrameworkElement[] _toolbarActions = [];

    /// <summary>The order in which actions give up their place when the toolbar is too narrow
    /// for all of them.</summary>
    private FrameworkElement[] _overflowOrder = [];

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
        _toolbarActions =
        [
            RecordButton, MuteButton, VolumeGroup, SnapshotButton, DisconnectButton, StatsButton,
            PinButton, FullscreenButton, LogButton, SettingsButton,
        ];
        // Least used first; the controls a mirror is actually driven with - mute and
        // fullscreen - hold their place longest.
        _overflowOrder =
        [
            StatsButton, PinButton, LogButton, DisconnectButton, VolumeGroup, RecordButton,
            SnapshotButton, SettingsButton, FullscreenButton, MuteButton,
        ];
        ConfigureOverflow();
        HideWhenCramped(ReceiverName);
        HideWhenCramped(StatusText);
        // The window's size, not the toolbar's: a toolbar whose contents overflow is laid out
        // at the width of its contents, so it would stop reporting changes exactly when the
        // window got too narrow for it.
        SizeChanged += (_, _) => LayoutToolbar();

        // Window-wide rather than on the video host alone: the log and the notice bar sit
        // over the picture in fullscreen, and a pointer resting on them would otherwise stay
        // hidden. Moving over the toolbar also keeps the pointer visible, which is right.
        MouseMove += OnPointerMoved;

        KeyDown += OnKeyDown;
        StateChanged += OnWindowStateChanged;

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
    private double ChromeHeight() => _isFullscreen ? 0 : Toolbar.ActualHeight + StatusBar.ActualHeight;

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
        Topmost = PinButton.IsChecked == true;
        if (_settings is null) return;
        _settings.AlwaysOnTop = Topmost;
        _settings.Save();
        if (OnTopCheck is not null) OnTopCheck.IsChecked = Topmost;
    }

    private void OnMinimise(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximise(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// Fits the toolbar to the window. The window controls always keep their place, and the
    /// actions that do not fit beside them move, least used first, into the popup behind
    /// <see cref="MoreButton"/>.
    /// <para>
    /// A mirrored phone makes the window about as narrow as a phone, and the full row of
    /// actions is wider than that. Laid out as one strip, it pushed minimise, maximise and
    /// close off the edge of the window the moment a session started.
    /// </para>
    /// </summary>
    private void LayoutToolbar()
    {
        // Measured from the room the window gives its content rather than from the toolbar's
        // own ActualWidth, which grows to fit its contents whenever they overflow.
        var width = LayoutInformation.GetLayoutSlot(RootGrid).Width
                    - RootGrid.Margin.Left - RootGrid.Margin.Right
                    - ToolbarLayout.Margin.Left - ToolbarLayout.Margin.Right;
        if (width <= 0) return;

        var unbounded = new Size(double.PositiveInfinity, double.PositiveInfinity);
        foreach (var action in _toolbarActions) action.Measure(unbounded);
        CaptionButtons.Measure(unbounded);

        // The status dot stays, however little room that leaves the name beside it.
        var room = width - CaptionButtons.DesiredSize.Width - StatusDot.Width - ToolbarIdentity.Margin.Right;
        // Collapsed actions measure to nothing, so this is only what the current state shows.
        var needed = _toolbarActions.Sum(action => action.DesiredSize.Width);

        var overflow = new HashSet<FrameworkElement>();
        if (needed > room)
        {
            room -= MoreButton.Width;
            foreach (var action in _overflowOrder)
            {
                if (needed <= room) break;
                if (action.DesiredSize.Width <= 0) continue;
                overflow.Add(action);
                needed -= action.DesiredSize.Width;
            }
        }

        foreach (var action in _toolbarActions)
            MoveAction(action, overflow.Contains(action) ? OverflowActions : ToolbarActions);

        MoreButton.Visibility = overflow.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (overflow.Count == 0) MoreButton.IsChecked = false;
    }

    /// <summary>Moves an action into <paramref name="panel"/>, placed in toolbar order among the
    /// actions already there. Does nothing if it is there already.</summary>
    private void MoveAction(FrameworkElement action, Panel panel)
    {
        if (ReferenceEquals(action.Parent, panel)) return;
        (action.Parent as Panel)?.Children.Remove(action);

        var order = Array.IndexOf(_toolbarActions, action);
        var index = 0;
        while (index < panel.Children.Count
               && Array.IndexOf(_toolbarActions, panel.Children[index] as FrameworkElement) < order)
        {
            index++;
        }

        panel.Children.Insert(index, action);
    }

    /// <summary>Wires up the popup the toolbar spills into when the window is narrow.</summary>
    private void ConfigureOverflow()
    {
        // Right edges aligned, just below the button: the popup opens back over the toolbar
        // rather than past the edge of a window that is narrow whenever it is needed.
        OverflowPopup.CustomPopupPlacementCallback = (popupSize, targetSize, _) =>
            [new CustomPopupPlacement(new Point(targetSize.Width - popupSize.Width + 8, targetSize.Height - 4), PopupPrimaryAxis.Horizontal)];

        // StaysOpen="False" closes the popup on any click outside it - including one on the
        // button, which would then toggle it straight back open. While it is open the button
        // is taken out of hit testing, so that click only closes it.
        OverflowPopup.Opened += (_, _) => MoreButton.IsHitTestVisible = false;
        OverflowPopup.Closed += (_, _) => MoreButton.IsHitTestVisible = true;

        // A button has done its job once clicked. The volume slider is not one, and keeps the
        // popup open while it is dragged.
        OverflowActions.AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler((_, e) =>
        {
            if (e.Source is ButtonBase) MoreButton.IsChecked = false;
        }));
    }

    /// <summary>
    /// Stops drawing a label once it is squeezed narrower than a short word. Trimmed to a few
    /// pixels, a name or status line shows as a stray dot or a sliver of a letter, which reads
    /// as a rendering fault rather than as text that did not fit.
    /// </summary>
    private static void HideWhenCramped(TextBlock label) =>
        label.SizeChanged += (_, _) => label.Opacity = label.ActualWidth < 24 ? 0 : 1;

    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
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
    private void ApplyChromePadding() =>
        RootGrid.Margin = _isFullscreen ? new Thickness(0) : WindowFrame.MaximisedPadding(this);

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
        _stateBeforeFullscreen = WindowState;
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

        _cursorTimer ??= CreateCursorTimer();
        _cursorTimer.Start();
        Dispatcher.InvokeAsync(LayoutToolbar, DispatcherPriority.Loaded);
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

        // The toolbar was skipped while hidden, and the window may come back at the size it
        // left, which raises no SizeChanged; fit it once the layout has settled.
        Dispatcher.InvokeAsync(LayoutToolbar, DispatcherPriority.Loaded);

        _cursorTimer?.Stop();
        Cursor = null;
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
        if (Toolbar.IsMouseOver || StatusBar.IsMouseOver || MoreButton.IsChecked == true) return;
        if (SettingsPanel.Visibility == Visibility.Visible || HelpPanel.Visibility == Visibility.Visible) return;
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

        if (inTextEntry || !ctrl) return;

        var handled = true;
        switch (e.Key)
        {
            case Key.S when !shift: SaveSnapshot(); break;
            case Key.C when shift: CopySnapshot(); break;
            case Key.M: MuteButton.IsChecked = MuteButton.IsChecked != true; break;
            case Key.R when shift: RotateBy(90); break;
            case Key.R when RecordButton.IsEnabled: RecordButton.IsChecked = RecordButton.IsChecked != true; break;
            case Key.I: ToggleStats(); break;
            case Key.T: PinButton.IsChecked = PinButton.IsChecked != true; break;
            case Key.L: LogButton.IsChecked = LogButton.IsChecked != true; break;
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

    /// <summary>Escape peels back one layer at a time: help, then settings, then fullscreen,
    /// then zoom, then the log.</summary>
    private bool HandleEscape()
    {
        if (HelpPanel.Visibility == Visibility.Visible) { CloseHelp(); return true; }
        if (SettingsPanel.Visibility == Visibility.Visible) { SettingsButton.IsChecked = false; return true; }
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
            HelpPanel.Visibility = Visibility.Visible;
            FadeContentIn(HelpPanel);
        }
    }

    private void CloseHelp() => HelpPanel.Visibility = Visibility.Collapsed;

    private static readonly (string Keys, string Action)[] Shortcuts =
    [
        ("F11", "Fullscreen; Esc leaves"),
        ("Ctrl+S", "Save a screenshot"),
        ("Ctrl+Shift+C", "Copy a screenshot to the clipboard"),
        ("Ctrl+R", "Start or stop recording"),
        ("Ctrl+M", "Mute the phone's audio"),
        ("Ctrl+↑ / ↓", "Volume up and down"),
        ("Ctrl+D", "Disconnect the iPhone"),
        ("Ctrl+1 … 4", "Fit, fill, stretch, actual size"),
        ("Ctrl+Shift+R", "Rotate 90° clockwise"),
        ("Ctrl+wheel", "Zoom in and out; drag to pan"),
        ("Ctrl+0", "Reset the zoom"),
        ("Ctrl+I", "Statistics overlay"),
        ("Ctrl+T", "Keep the window on top"),
        ("Ctrl+L", "Activity log"),
        ("Ctrl+,", "Settings"),
        ("F1", "This list"),
    ];

    private void InitialiseShortcutList()
    {
        foreach (var (keys, action) in Shortcuts)
        {
            var row = new Grid { Margin = new Thickness(0, 5, 0, 5) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
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
