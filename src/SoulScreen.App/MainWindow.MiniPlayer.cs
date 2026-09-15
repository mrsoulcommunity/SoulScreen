using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using SoulScreen.App.Logic;

namespace SoulScreen.App;

/// <summary>
/// The mini player: the phone's screen alone, in a small window that stays above everything
/// else, for keeping an eye on the phone while working in another application.
/// <para>
/// It is the same window, not a second one. The toolbar and status bar are put away, the
/// caption strip is given up so the picture itself can be dragged, and a few controls fade in
/// over the picture while the pointer is on it. Leaving puts the window back exactly where and
/// how it was.
/// </para>
/// </summary>
public partial class MainWindow
{
    private bool _isMiniPlayer;
    private Rect _boundsBeforeMini;
    private WindowState _stateBeforeMini = WindowState.Normal;
    private double _minWidthBeforeMini;
    private double _minHeightBeforeMini;
    private bool _miniControlsShown;

    private static readonly Duration MiniControlsFade = new(TimeSpan.FromMilliseconds(160));

    private void InitialiseMiniPlayer()
    {
        MouseEnter += (_, _) => { if (_isMiniPlayer) ShowMiniControls(); };
        MouseLeave += (_, _) => { if (_isMiniPlayer) HideMiniControls(); };
    }

    private void OnToggleMiniPlayer(object sender, RoutedEventArgs e) => ToggleMiniPlayer();

    private void ToggleMiniPlayer()
    {
        if (_isMiniPlayer) ExitMiniPlayer();
        else EnterMiniPlayer();
    }

    private void EnterMiniPlayer()
    {
        if (_isMiniPlayer || _shuttingDown || _hiddenToTray) return;
        if (VideoHost.Visibility != Visibility.Visible)
        {
            ShowToast("The mini player needs a mirrored phone", "");
            return;
        }

        if (_isFullscreen) ToggleFullscreen();

        // Everything that covers the picture goes: none of it fits in a window this size.
        ClosePalette();
        SettingsButton.IsChecked = false;
        CapturesButton.IsChecked = false;
        LogButton.IsChecked = false;
        MoreButton.IsChecked = false;
        CloseHelp();
        ResetZoom();
        MarkupButton.IsChecked = false;

        _stateBeforeMini = WindowState;
        _boundsBeforeMini = WindowState == WindowState.Normal
            ? new Rect(Left, Top, ActualWidth, ActualHeight)
            : RestoreBounds;
        _minWidthBeforeMini = MinWidth;
        _minHeightBeforeMini = MinHeight;

        _isMiniPlayer = true;
        if (WindowState != WindowState.Normal) WindowState = WindowState.Normal;

        Toolbar.Visibility = Visibility.Collapsed;
        StatusBar.Visibility = Visibility.Collapsed;
        NoticeBar.Visibility = Visibility.Collapsed;
        ApplyStatsVisibility();

        WindowFrame.UseCustomCaption(this, captionHeight: 0);
        MinWidth = MiniPlayerGeometry.MinimumShortSide;
        MinHeight = MiniPlayerGeometry.MinimumShortSide;
        Topmost = true;
        ApplyChromePadding();

        PlaceMiniPlayer(keepCurrentPlacement: false);
        UpdateControlBar();

        MiniControls.Visibility = Visibility.Visible;
        MiniControls.BeginAnimation(OpacityProperty, null);
        MiniControls.Opacity = 0;
        _miniControlsShown = false;
        UpdateMiniControls();
        // The pointer is usually still over the window it just clicked in.
        if (IsMouseOver) ShowMiniControls();

        UpdateAspectLock();
        PositionOverlays();
        UpdatePictureCorners();
        UpdateTaskbar();
    }

    private void ExitMiniPlayer()
    {
        if (!_isMiniPlayer) return;
        _snapTimer?.Stop();
        RememberMiniPlayerPlacement();
        _isMiniPlayer = false;

        MiniControls.BeginAnimation(OpacityProperty, null);
        MiniControls.Visibility = Visibility.Collapsed;
        _miniControlsShown = false;

        // Focus mode asked for the strips to be gone; the mini player only borrowed the idea.
        var chrome = _focusMode ? Visibility.Collapsed : Visibility.Visible;
        Toolbar.Visibility = chrome;
        StatusBar.Visibility = chrome;

        // A refresh-rate notice put away with the toolbar comes back with it.
        if (!_noticeDismissed && VideoHost.Visibility == Visibility.Visible
            && _warnings.Any(warning => warning.Kind == WarningKind.Cadence))
        {
            NoticeBar.Visibility = Visibility.Visible;
        }

        WindowFrame.UseCustomCaption(this);
        MinWidth = _minWidthBeforeMini;
        MinHeight = _minHeightBeforeMini;
        Topmost = _settings.AlwaysOnTop;

        if (_boundsBeforeMini.Width > 0 && _boundsBeforeMini.Height > 0)
        {
            Left = _boundsBeforeMini.Left;
            Top = _boundsBeforeMini.Top;
            Width = Math.Max(_boundsBeforeMini.Width, MinWidth);
            Height = Math.Max(_boundsBeforeMini.Height, MinHeight);
        }
        if (_stateBeforeMini == WindowState.Maximized) WindowState = WindowState.Maximized;

        ApplyChromePadding();
        ApplyStatsVisibility();
        UpdateAspectLock();
        PositionOverlays();
        UpdatePictureCorners();
        UpdateTaskbar();
        // The control bar comes back at a different width; fit it once the layout has settled.
        Dispatcher.InvokeAsync(UpdateControlBar, DispatcherPriority.Loaded);
    }

    /// <summary>
    /// Sizes and places the player. On entering, from what was remembered last time; when the
    /// phone turns while it is open, from where it is now, so it stays where it was put.
    /// </summary>
    private void PlaceMiniPlayer(bool keepCurrentPlacement)
    {
        var size = RotatedVideoSize();
        var aspect = size.Width > 0 && size.Height > 0 ? size.Width / size.Height : 9.0 / 19.5;
        var area = WindowFrame.WorkArea(this);
        var workArea = new Bounds(area.Left, area.Top, area.Width, area.Height);

        double? longSide, left, top;
        if (keepCurrentPlacement)
        {
            longSide = MiniPlayerGeometry.LongSide(ActualWidth, ActualHeight);
            left = Left;
            top = Top;
        }
        else
        {
            longSide = _settings.MiniPlayerLongSide;
            left = _settings.MiniPlayerLeft;
            top = _settings.MiniPlayerTop;

            // A place remembered on another monitor is not dragged onto this one's edge; the
            // player opens in this monitor's corner instead.
            if (left is { } l && top is { } t && !area.Contains(new Point(l + 40, t + 40)))
            {
                left = null;
                top = null;
            }
        }

        var placed = MiniPlayerGeometry.Place(aspect, workArea, longSide, left, top);
        Left = placed.Left;
        Top = placed.Top;
        Width = placed.Width;
        Height = placed.Height;
    }

    private void RememberMiniPlayerPlacement()
    {
        if (!_isMiniPlayer || WindowState != WindowState.Normal || ActualWidth <= 0 || ActualHeight <= 0) return;
        _settings.MiniPlayerLongSide = MiniPlayerGeometry.LongSide(ActualWidth, ActualHeight);
        _settings.MiniPlayerLeft = Left;
        _settings.MiniPlayerTop = Top;
        _settings.Save();
    }

    /// <summary>A press on the picture moves the player; a double-click puts the window back.</summary>
    private bool HandleMiniPlayerMouseDown(MouseButtonEventArgs e)
    {
        if (!_isMiniPlayer) return false;

        if (e.ClickCount == 2)
        {
            ExitMiniPlayer();
            e.Handled = true;
            return true;
        }

        if (IsZoomed) return false;

        _snapTimer?.Stop();
        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // The button was already released by the time the drag could begin.
        }

        SnapMiniPlayer();
        e.Handled = true;
        return true;
    }

    private DispatcherTimer? _snapTimer;

    /// <summary>
    /// Settles a dropped player: lined up with an edge it was left near, or brought back onto a
    /// screen it was dragged partly off, with a short glide - or at once, when Windows has been
    /// asked to keep animation to a minimum. Remembered where it comes to rest.
    /// </summary>
    private void SnapMiniPlayer()
    {
        if (!_isMiniPlayer || WindowState != WindowState.Normal)
        {
            RememberMiniPlayerPlacement();
            return;
        }

        var area = WindowFrame.WorkArea(this);
        var from = new Bounds(Left, Top, ActualWidth, ActualHeight);
        var to = MiniPlayerGeometry.Snap(from, new Bounds(area.Left, area.Top, area.Width, area.Height));

        var alreadyThere = Math.Abs(to.Left - from.Left) < 0.5 && Math.Abs(to.Top - from.Top) < 0.5;
        if (alreadyThere || !Motion.Enabled)
        {
            Left = to.Left;
            Top = to.Top;
            RememberMiniPlayerPlacement();
            return;
        }

        var clock = Stopwatch.StartNew();
        const double durationMs = 200;
        var timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(8) };
        timer.Tick += (_, _) =>
        {
            if (!_isMiniPlayer)
            {
                timer.Stop();
                return;
            }

            var progress = Math.Min(clock.Elapsed.TotalMilliseconds / durationMs, 1);
            var eased = 1 - Math.Pow(1 - progress, 3);
            Left = from.Left + (to.Left - from.Left) * eased;
            Top = from.Top + (to.Top - from.Top) * eased;
            if (progress < 1) return;

            timer.Stop();
            RememberMiniPlayerPlacement();
        };
        _snapTimer = timer;
        timer.Start();
    }

    private void ShowMiniControls()
    {
        if (!_isMiniPlayer || _miniControlsShown) return;
        _miniControlsShown = true;
        MiniControls.BeginAnimation(OpacityProperty, new DoubleAnimation(1, MiniControlsFade)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });
    }

    private void HideMiniControls()
    {
        if (!_miniControlsShown) return;
        _miniControlsShown = false;
        MiniControls.BeginAnimation(OpacityProperty, new DoubleAnimation(0, new Duration(TimeSpan.FromMilliseconds(260)))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
        });
    }

    /// <summary>Keeps the player's buttons in step with mute and pause.</summary>
    private void UpdateMiniControls()
    {
        if (MiniMuteButton is null) return;

        var hasAudio = _audio is not null;
        MiniMuteButton.Visibility = hasAudio ? Visibility.Visible : Visibility.Collapsed;
        var muted = MuteButton.IsChecked == true;
        MiniMuteButton.Content = muted ? "" : "";
        MiniMuteButton.ToolTip = muted ? "Unmute (Ctrl+M)" : "Mute (Ctrl+M)";

        MiniPauseButton.Content = Video.IsFrozen ? "" : "";
        MiniPauseButton.ToolTip = Video.IsFrozen ? "Resume the picture (Space)" : "Pause the picture (Space)";
    }

    private void OnMiniMute(object sender, RoutedEventArgs e) => MuteButton.IsChecked = MuteButton.IsChecked != true;
}
