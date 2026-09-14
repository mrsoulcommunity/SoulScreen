using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace SoulScreen.App;

/// <summary>
/// The picture: how it is fitted, rotated and flipped, zoom and pan, the context menu,
/// screenshots and recording, the phone's audio level, and the statistics overlay.
/// </summary>
public partial class MainWindow
{
    private const double MinZoom = 1.0;
    private const double MaxZoom = 8.0;

    private bool _adjustedForVideoSize;
    private bool _suppressVolumeEvents;

    private Point _panStart;
    private Point _panOrigin;
    private bool _panning;

    private void InitialiseVideo()
    {
        Video.VideoSizeChanged += OnVideoSizeChanged;
        VideoHost.MouseLeftButtonDown += OnVideoMouseDown;
        VideoHost.MouseLeftButtonUp += OnVideoMouseUp;
        VideoHost.MouseMove += OnVideoMouseMove;
        VideoHost.MouseWheel += OnVideoWheel;
        VideoHost.SizeChanged += (_, _) => ClampPan();
        DpiChanged += (_, _) => ApplyPictureSettings();
    }

    // ------------------------------------------------------------ fit & rotate

    /// <summary>Pushes fit, rotation, flip and pacing onto the surface. Idempotent.</summary>
    private void ApplyPictureSettings()
    {
        Video.Stretch = _settings.VideoFit switch
        {
            VideoFit.Fill => Stretch.UniformToFill,
            VideoFit.Stretch => Stretch.Fill,
            VideoFit.Actual => Stretch.None,
            _ => Stretch.Uniform,
        };

        // "Actual size" means one phone pixel per screen pixel, which at 150 % scaling is a
        // 0.667 layout scale: WPF lays a 96-dpi bitmap out at one DIP per pixel otherwise.
        var dpi = VisualTreeHelper.GetDpi(this);
        var actualScale = _settings.VideoFit == VideoFit.Actual ? 1.0 / dpi.DpiScaleX : 1.0;
        FlipTransform.ScaleX = (_settings.MirrorHorizontally ? -1 : 1) * actualScale;
        FlipTransform.ScaleY = actualScale;
        RotationTransform.Angle = _settings.Rotation;

        Video.PresentationDelay = AppSettings.PresentationDelayFor(_settings.Latency);
        if (_audio is not null) _audio.Reserve = AppSettings.AudioReserveFor(Video.PresentationDelay);

        ClampPan();
        UpdateAspectLock();
    }

    private void SetVideoFit(VideoFit fit)
    {
        if (_settings.VideoFit == fit) return;
        _settings.VideoFit = fit;
        _settings.Save();
        ApplyPictureSettings();
        SyncPictureControls();
        ShowToast(fit switch
        {
            VideoFit.Fill => "Fill the window",
            VideoFit.Stretch => "Stretch to the window",
            VideoFit.Actual => "Actual size",
            _ => "Fit to the window",
        }, "");
    }

    private void SetRotation(int degrees)
    {
        degrees = AppSettings.NormaliseRotation(degrees);
        if (_settings.Rotation == degrees) return;
        _settings.Rotation = degrees;
        _settings.Save();
        ApplyPictureSettings();
        SyncPictureControls();
        // The window was shaped for the other orientation; let it be reshaped once.
        _adjustedForVideoSize = false;
        if (Video.VideoSize.Width > 0) OnVideoSizeChanged(this, Video.VideoSize);
    }

    private void RotateBy(int degrees) => SetRotation(_settings.Rotation + degrees);

    private void SetMirror(bool mirrored)
    {
        if (_settings.MirrorHorizontally == mirrored) return;
        _settings.MirrorHorizontally = mirrored;
        _settings.Save();
        ApplyPictureSettings();
        SyncPictureControls();
    }

    /// <summary>The picture's size as shown, with rotation applied.</summary>
    private Size RotatedVideoSize()
    {
        var size = Video.VideoSize;
        return _settings.Rotation is 90 or 270 ? new Size(size.Height, size.Width) : size;
    }

    /// <summary>
    /// Reshapes the window to the phone's aspect ratio, once per session, so a portrait
    /// screen fills the window instead of sitting between black bars.
    /// <para>
    /// Both dimensions move, not just the height: matching a 9:19.5 phone by growing the
    /// height alone produces a window taller than the screen, which then gets clamped and
    /// leaves the bars it was meant to remove.
    /// </para>
    /// </summary>
    private void OnVideoSizeChanged(object? sender, Size size)
    {
        UpdateAspectLock();

        // Never fight the user's own sizing after the first fit.
        if (_adjustedForVideoSize || _isFullscreen || WindowState != WindowState.Normal) return;
        if (!_settings.FitWindowToVideo) return;
        if (size.Width <= 0 || size.Height <= 0) return;
        _adjustedForVideoSize = true;

        size = RotatedVideoSize();

        var workArea = SystemParameters.WorkArea;
        var chromeHeight = Toolbar.ActualHeight + StatusBar.ActualHeight;
        // Border and caption sit outside the client area the layout measured.
        var borderWidth = Math.Max(ActualWidth - VideoHost.ActualWidth, 0);

        // Leave a margin so the window never sits flush against the screen edges.
        var maxWidth = workArea.Width * 0.9;
        var maxHeight = workArea.Height * 0.9;

        // Start from the current width and let the aspect ratio decide the height, then fall
        // back to fitting the height when that would not fit.
        var contentWidth = Math.Max(VideoHost.ActualWidth, MinWidth - borderWidth);
        var contentHeight = contentWidth * size.Height / size.Width;

        if (contentHeight + chromeHeight > maxHeight)
        {
            contentHeight = maxHeight - chromeHeight;
            contentWidth = contentHeight * size.Width / size.Height;
        }

        if (contentWidth + borderWidth > maxWidth)
        {
            contentWidth = maxWidth - borderWidth;
            contentHeight = contentWidth * size.Height / size.Width;
        }

        Width = Math.Max(contentWidth + borderWidth, MinWidth);
        Height = Math.Max(contentHeight + chromeHeight, MinHeight);

        // Nudge back on screen if the new size pushed an edge past the work area.
        if (Left + Width > workArea.Right) Left = Math.Max(workArea.Left, workArea.Right - Width - 8);
        if (Top + Height > workArea.Bottom) Top = Math.Max(workArea.Top, workArea.Bottom - Height - 8);
    }

    // ------------------------------------------------------------- zoom & pan

    private bool IsZoomed => ZoomScale.ScaleX > 1.001;

    /// <summary>Where the picture actually is inside the viewport, before zoom: the
    /// letterboxed rectangle for Fit, the overflowing one for Fill, and so on.</summary>
    private Rect PictureRect()
    {
        if (Video.ActualWidth <= 0 || Video.ActualHeight <= 0)
            return new Rect(0, 0, VideoViewport.ActualWidth, VideoViewport.ActualHeight);
        try
        {
            return Video.TransformToAncestor(VideoViewport)
                .TransformBounds(new Rect(0, 0, Video.ActualWidth, Video.ActualHeight));
        }
        catch (InvalidOperationException)
        {
            return new Rect(0, 0, VideoViewport.ActualWidth, VideoViewport.ActualHeight);
        }
    }

    /// <summary>Scales the picture by <paramref name="factor"/> about <paramref name="anchor"/>,
    /// or about the centre of the viewport when null.</summary>
    private void ZoomBy(double factor, Point? anchor)
    {
        if (VideoHost.Visibility != Visibility.Visible) return;

        var current = ZoomScale.ScaleX;
        var target = Math.Clamp(current * factor, MinZoom, MaxZoom);
        if (Math.Abs(target - current) < 0.0001) return;

        var point = anchor ?? new Point(VideoViewport.ActualWidth / 2, VideoViewport.ActualHeight / 2);

        // Keep the point under the pointer where it is: s'p + t' = sp + t.
        ZoomPan.X = current * point.X + ZoomPan.X - target * point.X;
        ZoomPan.Y = current * point.Y + ZoomPan.Y - target * point.Y;
        ZoomScale.ScaleX = target;
        ZoomScale.ScaleY = target;

        ClampPan();
        UpdateZoomBadge();
    }

    private void ResetZoom()
    {
        ZoomScale.ScaleX = 1;
        ZoomScale.ScaleY = 1;
        ZoomPan.X = 0;
        ZoomPan.Y = 0;
        _panning = false;
        if (VideoHost.IsMouseCaptured) VideoHost.ReleaseMouseCapture();
        UpdateZoomBadge();
    }

    /// <summary>Keeps the zoomed picture covering the viewport: no black revealed past an
    /// edge that the picture would have covered at this scale.</summary>
    private void ClampPan()
    {
        var scale = ZoomScale.ScaleX;
        if (scale <= 1.001)
        {
            ZoomPan.X = 0;
            ZoomPan.Y = 0;
            return;
        }

        var viewport = new Size(VideoViewport.ActualWidth, VideoViewport.ActualHeight);
        var picture = PictureRect();
        ZoomPan.X = ClampAxis(ZoomPan.X, picture.X, picture.Width, viewport.Width, scale);
        ZoomPan.Y = ClampAxis(ZoomPan.Y, picture.Y, picture.Height, viewport.Height, scale);
    }

    private static double ClampAxis(double pan, double origin, double length, double viewport, double scale)
    {
        var scaledLength = length * scale;
        if (scaledLength <= viewport)
        {
            // Smaller than the viewport on this axis even when zoomed: keep it centred.
            return (viewport - scaledLength) / 2 - origin * scale;
        }
        var min = viewport - scale * (origin + length);
        var max = -scale * origin;
        return Math.Clamp(pan, min, max);
    }

    private void UpdateZoomBadge()
    {
        if (!IsZoomed)
        {
            ZoomBadge.Visibility = Visibility.Collapsed;
            VideoHost.Cursor = null;
            return;
        }
        ZoomText.Text = $"{ZoomScale.ScaleX * 100:0}%";
        ZoomBadge.Visibility = Visibility.Visible;
        VideoHost.Cursor = _panning ? Cursors.ScrollAll : Cursors.Hand;
    }

    private void OnVideoWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            ZoomBy(e.Delta > 0 ? 1.15 : 1 / 1.15, e.GetPosition(VideoViewport));
            e.Handled = true;
        }
        else if (_audio is not null && VolumeSlider.Visibility == Visibility.Visible)
        {
            NudgeVolume(e.Delta > 0 ? 0.05 : -0.05);
            e.Handled = true;
        }
    }

    /// <summary>Double-click toggles fullscreen, which is what every video player does;
    /// a single press on a zoomed picture starts a drag.</summary>
    private void OnVideoMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleFullscreen();
            e.Handled = true;
            return;
        }

        if (!IsZoomed) return;
        _panning = true;
        _panStart = e.GetPosition(VideoHost);
        _panOrigin = new Point(ZoomPan.X, ZoomPan.Y);
        VideoHost.CaptureMouse();
        UpdateZoomBadge();
    }

    private void OnVideoMouseMove(object sender, MouseEventArgs e)
    {
        if (!_panning) return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            EndPan();
            return;
        }
        var now = e.GetPosition(VideoHost);
        ZoomPan.X = _panOrigin.X + (now.X - _panStart.X);
        ZoomPan.Y = _panOrigin.Y + (now.Y - _panStart.Y);
        ClampPan();
    }

    private void OnVideoMouseUp(object sender, MouseButtonEventArgs e) => EndPan();

    private void EndPan()
    {
        if (!_panning) return;
        _panning = false;
        if (VideoHost.IsMouseCaptured) VideoHost.ReleaseMouseCapture();
        UpdateZoomBadge();
    }

    // ------------------------------------------------------------ context menu

    private void OnVideoMenuOpened(object sender, RoutedEventArgs e)
    {
        MenuFit.IsChecked = _settings.VideoFit == VideoFit.Fit;
        MenuFill.IsChecked = _settings.VideoFit == VideoFit.Fill;
        MenuStretch.IsChecked = _settings.VideoFit == VideoFit.Stretch;
        MenuActual.IsChecked = _settings.VideoFit == VideoFit.Actual;
        MenuRotate0.IsChecked = _settings.Rotation == 0;
        MenuRotate90.IsChecked = _settings.Rotation == 90;
        MenuRotate180.IsChecked = _settings.Rotation == 180;
        MenuRotate270.IsChecked = _settings.Rotation == 270;
        MenuMirror.IsChecked = _settings.MirrorHorizontally;
        MenuRecord.IsEnabled = RecordButton.IsEnabled;
        MenuRecord.Header = RecordButton.IsChecked == true ? "Stop recording" : "Record to MP4";
        MenuMute.IsEnabled = _audio is not null;
        MenuMute.IsChecked = MuteButton.IsChecked == true;
        MenuStats.IsChecked = StatsHud.Visibility == Visibility.Visible;
        MenuPin.IsChecked = Topmost;
    }

    private void OnMenuFit(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string tag } && Enum.TryParse<VideoFit>(tag, out var fit)) SetVideoFit(fit);
    }

    private void OnMenuRotate(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string tag } && int.TryParse(tag, NumberStyles.Integer, CultureInfo.InvariantCulture, out var degrees))
            SetRotation(degrees);
    }

    private void OnMenuMirror(object sender, RoutedEventArgs e) => SetMirror(!_settings.MirrorHorizontally);
    private void OnMenuZoomIn(object sender, RoutedEventArgs e) => ZoomBy(1.25, null);
    private void OnMenuZoomOut(object sender, RoutedEventArgs e) => ZoomBy(1 / 1.25, null);
    private void OnMenuZoomReset(object sender, RoutedEventArgs e) => ResetZoom();
    private void OnMenuRecord(object sender, RoutedEventArgs e)
    {
        if (RecordButton.IsEnabled) RecordButton.IsChecked = RecordButton.IsChecked != true;
    }
    private void OnMenuMute(object sender, RoutedEventArgs e) => MuteButton.IsChecked = MuteButton.IsChecked != true;
    private void OnMenuStats(object sender, RoutedEventArgs e) => ToggleStats();
    private void OnMenuPin(object sender, RoutedEventArgs e) => PinButton.IsChecked = PinButton.IsChecked != true;

    // --------------------------------------------------------------- recording

    private void OnRecordChanged(object sender, RoutedEventArgs e)
    {
        var recording = RecordButton.IsChecked == true;
        RecordDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, recording ? "Danger" : "TextSecondary");
        SetRecordDotPulsing(recording);

        if (_pipeline is null)
        {
            RecordButton.IsChecked = false;
            return;
        }

        if (recording)
        {
            try
            {
                Directory.CreateDirectory(_settings.CaptureDirectory);
            }
            catch (Exception ex)
            {
                _log.Error($"could not create {_settings.CaptureDirectory}", ex);
                ShowToast("The capture folder cannot be created", "");
                RecordButton.IsChecked = false;
                return;
            }

            var path = Path.Combine(_settings.CaptureDirectory, $"SoulScreen-{DateTime.Now:yyyyMMdd-HHmmss}.mp4");
            _pipeline.RecordAudio = _settings.RecordAudio && _audio is not null;
            _pipeline.StartRecording(path);
            RecordButton.ToolTip = "Stop recording (Ctrl+R)";
            ShowTransientStatus($"Recording to {Path.GetFileName(path)}", null);
            ShowToast("Recording", "");
        }
        else
        {
            RecordButton.ToolTip = "Record to MP4 (Ctrl+R)";
            _pipeline.StopRecording();
        }
    }

    private void SetRecordDotPulsing(bool pulsing)
    {
        if (pulsing)
        {
            RecordDot.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.3, TimeSpan.FromMilliseconds(650))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            });
        }
        else
        {
            RecordDot.BeginAnimation(OpacityProperty, null);
            RecordDot.Opacity = 1;
        }
    }

    private void OnRecordingFinished(object? sender, string path) =>
        Dispatcher.BeginInvoke(() =>
        {
            RecordButton.IsChecked = false;
            ShowTransientStatus($"Saved {Path.GetFileName(path)}", path);
            ShowToast("Recording saved", "");
            _log.Info($"recording saved to {path}");
        });

    // ------------------------------------------------------------------ audio

    private void OnMuteChanged(object sender, RoutedEventArgs e)
    {
        var muted = MuteButton.IsChecked == true;
        MuteButton.Content = muted ? "" : "";
        MuteButton.ToolTip = muted ? "Unmute the phone's audio (Ctrl+M)" : "Mute the phone's audio (Ctrl+M)";
        if (_audio is not null) _audio.Muted = muted;
        // Muted reads as zero on the level, not as the level being forgotten.
        UpdateVolumeLabel();

        // Mute and level are both properties of this stream, not of the PC's mixer, and
        // both are the kind of thing people expect to survive a restart.
        if (_settings is null) return;
        if (_settings.Muted != muted)
        {
            _settings.Muted = muted;
            _settings.Save();
        }
    }

    private void OnVolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // The XAML's own Value raises this while InitializeComponent is still building the
        // window: the label showing the percentage is declared after the slider and does
        // not exist yet. Nothing needs doing - the first real synchronisation with the
        // persisted level happens in ShowVideo once the pipeline exists.
        if (VolumeLabel is null || _suppressVolumeEvents) return;

        var volume = e.NewValue;
        // Keyboard arrows can walk a double slightly past an endpoint; a NaN from a
        // hand-edited settings file would poison every later comparison.
        if (double.IsNaN(volume)) volume = _settings?.Volume ?? 1.0;
        volume = Math.Clamp(volume, 0, 1);

        if (_audio is not null) _audio.Volume = (float)volume;
        UpdateVolumeLabel();

        if (_settings is null) return;
        if (_settings.Volume == volume) return;
        _settings.Volume = volume;
        _settings.Save();
    }

    private void NudgeVolume(double delta)
    {
        if (VolumeSlider.Visibility != Visibility.Visible) return;
        VolumeSlider.Value = Math.Clamp(Math.Round((VolumeSlider.Value + delta) * 20) / 20, 0, 1);
        if (MuteButton.IsChecked == true && delta > 0) MuteButton.IsChecked = false;
        ShowToast($"Volume {(int)Math.Round(VolumeSlider.Value * 100)}%", "");
    }

    /// <summary>The percentage beside the slider: with a 72-pixel track, "35%" is a more
    /// readable level than a thumb position. Mute reads as zero.</summary>
    private void UpdateVolumeLabel()
    {
        // Same mid-construction guard as OnVolumeChanged: callers can run while the
        // window is still half-built.
        if (VolumeLabel is null) return;

        var muted = MuteButton.IsChecked == true;
        var percent = (int)Math.Round((muted ? 0 : VolumeSlider.Value) * 100);
        VolumeLabel.Text = $"{percent}%";
    }

    // ------------------------------------------------------------- screenshots

    private void OnSnapshot(object sender, RoutedEventArgs e) => SaveSnapshot();

    private void OnCopySnapshot(object sender, RoutedEventArgs e) => CopySnapshot();

    /// <summary>The picture as shown - rotated and flipped like the window - or null before
    /// the first frame. Screenshots follow the display, not the wire.</summary>
    private BitmapSource? CaptureFrame()
    {
        var raw = Video.Snapshot();
        if (raw is null) return null;
        if (_settings.Rotation == 0 && !_settings.MirrorHorizontally) return raw;

        var group = new TransformGroup();
        if (_settings.MirrorHorizontally) group.Children.Add(new ScaleTransform(-1, 1));
        if (_settings.Rotation != 0) group.Children.Add(new RotateTransform(_settings.Rotation));
        var transformed = new TransformedBitmap(raw, group);
        transformed.Freeze();
        return transformed;
    }

    private void SaveSnapshot()
    {
        var snapshot = CaptureFrame();
        if (snapshot is null)
        {
            ShowToast("Nothing to capture yet", "");
            return;
        }

        try
        {
            Directory.CreateDirectory(_settings.CaptureDirectory);
            var path = Path.Combine(_settings.CaptureDirectory, $"SoulScreen-{DateTime.Now:yyyyMMdd-HHmmss}.png");

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(snapshot));
            using (var stream = File.Create(path)) encoder.Save(stream);

            if (_settings.CopyScreenshotToClipboard) TrySetClipboardImage(snapshot);

            _log.Info($"saved {path}");
            Flash();
            ShowTransientStatus($"Saved {Path.GetFileName(path)}", path);
        }
        catch (Exception ex)
        {
            _log.Error("could not save the screenshot", ex);
            ShowToast("The screenshot could not be saved", "");
        }
    }

    private void CopySnapshot()
    {
        var snapshot = CaptureFrame();
        if (snapshot is null)
        {
            ShowToast("Nothing to capture yet", "");
            return;
        }

        if (TrySetClipboardImage(snapshot))
        {
            Flash();
            ShowToast("Screenshot copied", "");
        }
    }

    private bool TrySetClipboardImage(BitmapSource image)
    {
        try
        {
            Clipboard.SetImage(image);
            return true;
        }
        catch (Exception ex)
        {
            // Another application can hold the clipboard open for a moment; not worth a dialog.
            _log.Warn("could not copy the screenshot to the clipboard", ex);
            ShowToast("The clipboard is busy; try again", "");
            return false;
        }
    }

    /// <summary>A camera-style flash: white over the picture, gone in a third of a second.</summary>
    private void Flash()
    {
        FlashOverlay.BeginAnimation(OpacityProperty, null);
        FlashOverlay.Opacity = 0.55;
        FlashOverlay.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(320))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });
    }

    private void OnOpenCaptureFolder(object sender, RoutedEventArgs e) => OpenFolder(_settings.CaptureDirectory);

    private void OnStatusLink(object sender, RoutedEventArgs e)
    {
        var file = _statusFile;
        if (file is not null && File.Exists(file)) RevealFile(file);
        else OpenFolder(_settings.CaptureDirectory);
    }

    private void OpenFolder(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{directory}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _log.Warn($"could not open {directory}", ex);
        }
    }

    private void RevealFile(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _log.Warn($"could not reveal {path}", ex);
        }
    }

    // ------------------------------------------------------------------- stats

    private void OnStatsToggled(object sender, RoutedEventArgs e)
    {
        var shown = StatsButton.IsChecked == true;
        if (_settings.ShowStats != shown)
        {
            _settings.ShowStats = shown;
            _settings.Save();
        }
        if (StatsCheck is not null) StatsCheck.IsChecked = shown;
        ApplyStatsVisibility();
    }

    private void ToggleStats() => StatsButton.IsChecked = StatsButton.IsChecked != true;

    private void ApplyStatsVisibility()
    {
        var shown = _settings.ShowStats && VideoHost.Visibility == Visibility.Visible;
        StatsHud.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
        if (shown) UpdateMetrics();
    }
}
