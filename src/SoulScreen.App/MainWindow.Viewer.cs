using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SoulScreen.App.Logic;

namespace SoulScreen.App;

/// <summary>
/// Quick Look for captures: a screenshot or recording opened over the gallery, without leaving
/// the app, with the arrow keys stepping through the rest.
/// <para>
/// Screenshots are decoded off the UI thread through a stream the file is not held by, so the
/// one on screen can still be deleted. Recordings play in place; the player is closed before
/// anything else touches the file, since Windows' media stack keeps it open while it plays.
/// </para>
/// </summary>
public partial class MainWindow
{
    /// <summary>Longest side a screenshot is decoded at: sharp on a 4K screen, and bounded in memory.</summary>
    private const int MaxViewerImageSide = 3200;

    private List<CaptureItem> _viewerItems = [];
    private int _viewerIndex = -1;

    /// <summary>Bumped for every item shown, so a slow decode for one never lands on the next.</summary>
    private int _viewerGeneration;

    private DispatcherTimer? _viewerTimer;
    private bool _viewerMediaReady;
    private bool _viewerPlaying;
    private bool _viewerUpdatingScrubber;

    private bool IsViewerOpen => ViewerPanel.Visibility == Visibility.Visible;

    private CaptureItem? CurrentViewerItem() =>
        _viewerIndex >= 0 && _viewerIndex < _viewerItems.Count ? _viewerItems[_viewerIndex] : null;

    // ------------------------------------------------------------------ opening and closing

    private void OpenViewer(CaptureItem item)
    {
        if (!CaptureStillExists(item)) return;

        _viewerItems = (CaptureList.ItemsSource as IEnumerable<CaptureItem>)?.ToList() ?? [];
        _viewerIndex = _viewerItems.IndexOf(item);
        if (_viewerIndex < 0)
        {
            _viewerItems = [item];
            _viewerIndex = 0;
        }

        ViewerPanel.Visibility = Visibility.Visible;
        FadeContentIn(ViewerPanel);
        ShowViewerItem();

        // Off the tile behind, so the arrow keys and Delete are the viewer's.
        Dispatcher.BeginInvoke(() =>
        {
            if (IsViewerOpen) ViewerPanel.Focus();
        }, DispatcherPriority.Input);
    }

    private void CloseViewer()
    {
        if (!IsViewerOpen) return;
        _viewerGeneration++;
        StopViewerMedia();
        ViewerImage.Source = null;
        ViewerPanel.Visibility = Visibility.Collapsed;
        _viewerItems = [];
        _viewerIndex = -1;
        Focus();
    }

    private void OnViewerClose(object sender, RoutedEventArgs e) => CloseViewer();

    private void OnViewerPrevious(object sender, RoutedEventArgs e) => StepViewer(-1);

    private void OnViewerNext(object sender, RoutedEventArgs e) => StepViewer(+1);

    private void StepViewer(int delta)
    {
        var target = _viewerIndex + delta;
        if (target < 0 || target >= _viewerItems.Count) return;
        _viewerIndex = target;
        ShowViewerItem();
    }

    // ------------------------------------------------------------------ showing an item

    private async void ShowViewerItem()
    {
        StopViewerMedia();
        var generation = ++_viewerGeneration;

        if (CurrentViewerItem() is not { } item)
        {
            CloseViewer();
            return;
        }

        var position = $"{_viewerIndex + 1} of {_viewerItems.Count}";
        ViewerTitle.Text = item.Title;
        ViewerDetail.Text = $"{item.Detail} · {position}";
        ViewerPrevious.Visibility = _viewerIndex > 0 ? Visibility.Visible : Visibility.Hidden;
        ViewerNext.Visibility = _viewerIndex < _viewerItems.Count - 1 ? Visibility.Visible : Visibility.Hidden;
        ViewerMessage.Visibility = Visibility.Collapsed;
        ViewerImage.Source = null;

        if (item.Kind == CaptureKind.Screenshot)
        {
            ViewerMedia.Visibility = Visibility.Collapsed;
            ViewerTransport.Visibility = Visibility.Collapsed;
            ViewerImage.Visibility = Visibility.Visible;

            var decoded = await Task.Run(() => DecodeForViewer(item.Path));
            if (generation != _viewerGeneration || !IsViewerOpen) return;

            if (decoded is not { } picture)
            {
                ShowViewerMessage("This screenshot could not be opened. It may have been moved, or still be being written.");
                return;
            }

            ViewerImage.Source = picture.Image;
            ViewerDetail.Text = $"{item.Detail} · {picture.Width}×{picture.Height} · {position}";
            return;
        }

        ViewerImage.Visibility = Visibility.Collapsed;
        ViewerMedia.Visibility = Visibility.Visible;
        ViewerTransport.Visibility = Visibility.Visible;
        ViewerPlayButton.IsEnabled = false;
        SetViewerScrubber(0, 1);
        ViewerPosition.Text = FormatClock(TimeSpan.Zero);
        ViewerDuration.Text = "-:--";

        try
        {
            ViewerMedia.Source = new Uri(item.Path);
            ViewerMedia.Play();
            _viewerPlaying = true;
        }
        catch (Exception ex)
        {
            _log.Warn($"could not play {item.Path}", ex);
            ShowRecordingUnplayable();
        }
        UpdateViewerPlayButton();
    }

    private static (BitmapSource Image, int Width, int Height)? DecodeForViewer(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            var width = decoder.Frames[0].PixelWidth;
            var height = decoder.Frames[0].PixelHeight;
            stream.Position = 0;

            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            if (Math.Max(width, height) > MaxViewerImageSide)
            {
                if (width >= height) image.DecodePixelWidth = MaxViewerImageSide;
                else image.DecodePixelHeight = MaxViewerImageSide;
            }
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return (image, width, height);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void ShowViewerMessage(string message)
    {
        ViewerMessage.Text = message;
        ViewerMessage.Visibility = Visibility.Visible;
    }

    private void ShowRecordingUnplayable()
    {
        StopViewerMedia();
        ViewerMedia.Visibility = Visibility.Collapsed;
        ViewerTransport.Visibility = Visibility.Collapsed;
        ShowViewerMessage("This recording can't be played here. “Open” plays it in your usual video app.");
    }

    // ------------------------------------------------------------------ playback

    private void OnViewerMediaOpened(object sender, RoutedEventArgs e)
    {
        _viewerMediaReady = true;
        ViewerPlayButton.IsEnabled = true;

        if (ViewerMedia.NaturalDuration.HasTimeSpan)
        {
            var total = ViewerMedia.NaturalDuration.TimeSpan;
            SetViewerScrubber(0, Math.Max(total.TotalSeconds, 0.1));
            ViewerDuration.Text = FormatClock(total);
        }

        if (CurrentViewerItem() is { } item && ViewerMedia.NaturalVideoWidth > 0)
            ViewerDetail.Text = $"{item.Detail} · {ViewerMedia.NaturalVideoWidth}×{ViewerMedia.NaturalVideoHeight} · {_viewerIndex + 1} of {_viewerItems.Count}";

        _viewerTimer ??= CreateViewerTimer();
        _viewerTimer.Start();
    }

    private void OnViewerMediaEnded(object sender, RoutedEventArgs e)
    {
        ViewerMedia.Pause();
        _viewerPlaying = false;
        UpdateViewerPlayButton();
        UpdateViewerClock();
    }

    private void OnViewerMediaFailed(object sender, ExceptionRoutedEventArgs e)
    {
        _log.Warn("a recording could not be played in the viewer", e.ErrorException);
        ShowRecordingUnplayable();
    }

    private void OnViewerPlayPause(object sender, RoutedEventArgs e) => ToggleViewerPlayback();

    private void ToggleViewerPlayback()
    {
        if (!_viewerMediaReady) return;

        if (_viewerPlaying)
        {
            ViewerMedia.Pause();
            _viewerPlaying = false;
        }
        else
        {
            // Played to the end, Play starts it again rather than doing nothing.
            if (ViewerMedia.NaturalDuration.HasTimeSpan
                && ViewerMedia.Position >= ViewerMedia.NaturalDuration.TimeSpan - TimeSpan.FromMilliseconds(150))
            {
                ViewerMedia.Position = TimeSpan.Zero;
            }
            ViewerMedia.Play();
            _viewerPlaying = true;
        }

        UpdateViewerPlayButton();
    }

    private void UpdateViewerPlayButton()
    {
        ViewerPlayButton.Content = _viewerPlaying ? "" : "";
        ViewerPlayButton.ToolTip = _viewerPlaying ? "Pause (Space)" : "Play (Space)";
        System.Windows.Automation.AutomationProperties.SetName(ViewerPlayButton, _viewerPlaying ? "Pause" : "Play");
    }

    private DispatcherTimer CreateViewerTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        timer.Tick += (_, _) => UpdateViewerClock();
        return timer;
    }

    private void UpdateViewerClock()
    {
        if (!IsViewerOpen || !_viewerMediaReady) return;
        // The thumb is being dragged: the pointer decides where the scrubber is.
        if (ViewerScrubber.IsMouseCaptureWithin) return;

        var position = ViewerMedia.Position;
        _viewerUpdatingScrubber = true;
        try { ViewerScrubber.Value = Math.Min(position.TotalSeconds, ViewerScrubber.Maximum); }
        finally { _viewerUpdatingScrubber = false; }
        ViewerPosition.Text = FormatClock(position);
    }

    private void OnViewerScrubberChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (ViewerPosition is null || _viewerUpdatingScrubber || !_viewerMediaReady) return;
        var position = TimeSpan.FromSeconds(Math.Max(e.NewValue, 0));
        ViewerMedia.Position = position;
        ViewerPosition.Text = FormatClock(position);
    }

    private void SetViewerScrubber(double value, double maximum)
    {
        _viewerUpdatingScrubber = true;
        try
        {
            ViewerScrubber.Maximum = maximum;
            ViewerScrubber.Value = value;
        }
        finally
        {
            _viewerUpdatingScrubber = false;
        }
    }

    /// <summary>Stops and closes the player, which lets go of the file.</summary>
    private void StopViewerMedia()
    {
        _viewerTimer?.Stop();
        _viewerMediaReady = false;
        _viewerPlaying = false;
        if (ViewerMedia.Source is null) return;
        ViewerMedia.Stop();
        ViewerMedia.Close();
        ViewerMedia.Source = null;
    }

    internal static string FormatClock(TimeSpan time)
    {
        if (time < TimeSpan.Zero) time = TimeSpan.Zero;
        return time.TotalHours >= 1
            ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}"
            : $"{(int)time.TotalMinutes}:{time.Seconds:00}";
    }

    // ------------------------------------------------------------------ actions

    private void OnViewerCopy(object sender, RoutedEventArgs e)
    {
        if (CurrentViewerItem() is { } item) CopyCapture(item);
    }

    private void OnViewerReveal(object sender, RoutedEventArgs e)
    {
        if (CurrentViewerItem() is { } item && CaptureStillExists(item)) RevealFile(item.Path);
    }

    private void OnViewerOpenExternal(object sender, RoutedEventArgs e)
    {
        if (CurrentViewerItem() is not { } item) return;
        if (_viewerPlaying) ToggleViewerPlayback();
        OpenCapture(item);
    }

    private async void OnViewerDelete(object sender, RoutedEventArgs e) => await DeleteViewerItemAsync();

    private async Task DeleteViewerItemAsync()
    {
        if (CurrentViewerItem() is not { } item) return;

        if (item.Kind == CaptureKind.Recording)
        {
            // The media stack lets go of the file a moment after it is closed.
            var generation = _viewerGeneration;
            StopViewerMedia();
            await Task.Delay(200);
            if (generation != _viewerGeneration || !IsViewerOpen) return;
        }

        if (!DeleteCapture(item))
        {
            if (item.Kind == CaptureKind.Recording) ShowViewerItem();
            return;
        }

        _viewerItems.Remove(item);
        if (_viewerItems.Count == 0)
        {
            CloseViewer();
            return;
        }

        _viewerIndex = Math.Min(_viewerIndex, _viewerItems.Count - 1);
        ShowViewerItem();
    }

    /// <summary>The viewer's keys, taken before anything behind it sees them.</summary>
    private bool HandleViewerKey(KeyEventArgs e)
    {
        // Only while nothing is laid over the viewer: its keys must never act on a capture that
        // cannot be seen - Delete least of all.
        if (!IsViewerOpen || PaletteOverlay.Visibility == Visibility.Visible || IsWelcomeOpen
            || HelpPanel.Visibility == Visibility.Visible)
        {
            return false;
        }

        var modifiers = Keyboard.Modifiers;
        if (modifiers == ModifierKeys.Control && e.Key == Key.C)
        {
            if (CurrentViewerItem() is { } item) CopyCapture(item);
            return true;
        }

        if (modifiers != ModifierKeys.None) return false;
        switch (e.Key)
        {
            case Key.Left: StepViewer(-1); return true;
            case Key.Right: StepViewer(+1); return true;
            case Key.Home: StepViewer(-_viewerIndex); return true;
            case Key.End: StepViewer(_viewerItems.Count - 1 - _viewerIndex); return true;
            case Key.Space: ToggleViewerPlayback(); return true;
            // Once per press: held down, the key's auto-repeat recycled a whole run of captures.
            case Key.Delete:
                if (!e.IsRepeat) _ = DeleteViewerItemAsync();
                return true;
            default: return false;
        }
    }
}
