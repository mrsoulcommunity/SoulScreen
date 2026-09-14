using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using SoulScreen.App.Logic;

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
        Video.SizeChanged += (_, _) => UpdatePictureCorners();
        VideoViewport.SizeChanged += (_, _) => UpdatePictureCorners();
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
        // Once the new fit has been laid out: the corners follow where the picture lands.
        Dispatcher.BeginInvoke(UpdatePictureCorners, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void SetVideoFit(VideoFit fit)
    {
        if (_settings.VideoFit == fit) return;
        _settings.VideoFit = fit;
        _settings.Save();
        ApplyPictureSettings();
        SyncPictureControls();
        ResetMarkupForNewPicture();
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
        ResetMarkupForNewPicture();
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
        ResetMarkupForNewPicture();
    }

    /// <summary>The picture's size as shown, with rotation applied.</summary>
    private Size RotatedVideoSize()
    {
        var size = Video.VideoSize;
        return _settings.Rotation is 90 or 270 ? new Size(size.Height, size.Width) : size;
    }

    /// <summary>
    /// Paints the phone's rounded screen corners over the picture in the window. Not in
    /// fullscreen, where the picture meets the screen's own edges, nor in the mini player, whose
    /// window Windows already rounds.
    /// </summary>
    private void UpdatePictureCorners()
    {
        if (PictureCorners is null) return;

        var wanted = _settings.RoundedCorners && !_isFullscreen && !_isMiniPlayer
                     && VideoHost.Visibility == Visibility.Visible && Video.VideoSize.Width > 0;
        var picture = wanted ? PictureRect() : Rect.Empty;
        if (!wanted || picture.IsEmpty || picture.Width < 8 || picture.Height < 8)
        {
            if (PictureCorners.Visibility != Visibility.Collapsed) PictureCorners.Visibility = Visibility.Collapsed;
            return;
        }

        // A phone's screen corners are about a ninth of its width; anything squarer - an iPad,
        // or a phone's picture letterboxed to a landscape stream - gets a gentler curve.
        var shortSide = Math.Min(picture.Width, picture.Height);
        var aspect = picture.Width / picture.Height;
        var phoneShaped = aspect < 0.62 || aspect > 1 / 0.62;
        var radius = Math.Round(shortSide * (phoneShaped ? 0.11 : 0.035), 1);

        // A pixel larger than the picture all round, so the outer edge's anti-aliasing falls on
        // the letterbox rather than darkening the picture's own edge.
        var margin = new Thickness(picture.X - 1, picture.Y - 1, 0, 0);
        var width = picture.Width + 2;
        var height = picture.Height + 2;
        if (PictureCorners.Visibility == Visibility.Visible && PictureCorners.Margin == margin
            && PictureCorners.Width == width && PictureCorners.Height == height
            && PictureCorners.Tag is double shown && shown == radius)
        {
            return;
        }

        var corners = new CombinedGeometry(GeometryCombineMode.Exclude,
            new RectangleGeometry(new Rect(0, 0, width, height)),
            new RectangleGeometry(new Rect(1, 1, picture.Width, picture.Height), radius, radius));
        corners.Freeze();

        PictureCorners.Data = corners;
        PictureCorners.Margin = margin;
        PictureCorners.Width = width;
        PictureCorners.Height = height;
        PictureCorners.Tag = radius;
        PictureCorners.Visibility = Visibility.Visible;
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
        // The phone turned, or a new session began: a drawing on the old picture means nothing now.
        ResetMarkupForNewPicture();

        if (_isMiniPlayer)
        {
            // The phone turned while the player was open: reshape it where it stands.
            if (size.Width > 0 && size.Height > 0) PlaceMiniPlayer(keepCurrentPlacement: true);
            return;
        }

        // Never fight the user's own sizing after the first fit.
        if (_adjustedForVideoSize || _isFullscreen || WindowState != WindowState.Normal) return;
        if (!_settings.FitWindowToVideo) return;
        if (size.Width <= 0 || size.Height <= 0) return;
        _adjustedForVideoSize = true;

        size = RotatedVideoSize();

        // The monitor the window is on, not the primary one: sized against the primary, a window
        // on a second screen was thrown back onto the first by the nudge below.
        var workArea = WindowFrame.WorkArea(this);
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
        else if (_audio is not null)
        {
            NudgeVolume(e.Delta > 0 ? 0.05 : -0.05);
            e.Handled = true;
        }
    }

    /// <summary>Double-click toggles fullscreen, which is what every video player does;
    /// a single press on a zoomed picture starts a drag.</summary>
    private void OnVideoMouseDown(object sender, MouseButtonEventArgs e)
    {
        // The pen has the pointer while marking up.
        if (IsMarkupActive) return;
        if (HandleMiniPlayerMouseDown(e)) return;

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
        MenuPin.IsChecked = PinButton.IsChecked == true;
        MenuFullscreen.Header = _isFullscreen ? "Leave fullscreen" : "Fullscreen";
        MenuMini.Header = _isMiniPlayer ? "Leave the mini player" : "Mini player";
        MenuPause.Header = Video.IsFrozen ? "Resume picture" : "Pause picture";
        MenuMarkup.Header = IsMarkupActive ? "Leave markup" : "Markup";
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

            // A drive that fills mid-recording leaves an MP4 with no index, which nothing can open.
            if (DiskSpace.Classify(FreeBytesFor(_settings.CaptureDirectory)) == DiskSpaceLevel.Critical)
            {
                ShowToast("The capture drive is almost full - free some space to record", "\uE7BA");
                RecordButton.IsChecked = false;
                return;
            }

            _diskLowWarned = false;
            var path = CaptureNaming.NewPath(_settings.CaptureDirectory, DateTime.Now, ".mp4");
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

        UpdateRecordingPill();
        UpdateTaskbar();
    }

    /// <summary>The toolbar's dot and the badge over the picture pulse together while recording.</summary>
    private void SetRecordDotPulsing(bool pulsing)
    {
        foreach (var dot in new UIElement[] { RecordDot, RecordingPillDot })
        {
            if (pulsing)
            {
                dot.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.3, TimeSpan.FromMilliseconds(650))
                {
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever,
                    EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
                });
            }
            else
            {
                dot.BeginAnimation(OpacityProperty, null);
                dot.Opacity = 1;
            }
        }
    }

    /// <summary>The recording badge over the picture: how long it has been going, and a way to
    /// stop it that is there even when the toolbar has been put away.</summary>
    private void UpdateRecordingPill()
    {
        var pipeline = _pipeline;
        if (pipeline is not { IsRecording: true } || VideoHost.Visibility != Visibility.Visible)
        {
            RecordingPill.Visibility = Visibility.Collapsed;
            return;
        }

        RecordingPill.Visibility = Visibility.Visible;
        if (pipeline.RecordingPath is null)
        {
            // Recording begins on the next keyframe, which can be a moment away.
            RecordingPillText.Text = "Starting…";
            return;
        }

        var elapsed = pipeline.RecordingDuration;
        RecordingPillText.Text = elapsed.TotalHours >= 1
            ? $"{(int)elapsed.TotalHours}:{elapsed.Minutes:00}:{elapsed.Seconds:00}"
            : $"{elapsed.Minutes:00}:{elapsed.Seconds:00}";
    }

    private void OnRecordingPillClick(object sender, RoutedEventArgs e) => RecordButton.IsChecked = false;

    private void OnRecordingFinished(object? sender, string path) =>
        Dispatcher.BeginInvoke(() =>
        {
            RecordButton.IsChecked = false;
            ShowTransientStatus($"Saved {Path.GetFileName(path)}", path);
            ShowToast("Recording saved", "\uE714", "View", ShowCaptures);
            _log.Info($"recording saved to {path}");
            RefreshCapturesIfOpen();
        });

    // ------------------------------------------------------------------ audio

    private void OnMuteChanged(object sender, RoutedEventArgs e)
    {
        var muted = MuteButton.IsChecked == true;
        MuteButton.Content = muted ? "" : "";
        MuteButton.ToolTip = muted ? "Unmute the phone's audio (Ctrl+M)" : "Mute the phone's audio (Ctrl+M)";
        ApplyAudioMute();
        // Muted reads as zero on the level, not as the level being forgotten.
        UpdateVolumeLabel();
        UpdateMiniControls();
        UpdateTaskbar();

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
        // window, before the settings are there to write to. Nothing needs doing - the first
        // real synchronisation with the persisted level happens in ShowVideo once the pipeline
        // exists.
        if (_settings is null || !IsInitialized || _suppressVolumeEvents) return;

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
        // The slider may have given up its place on a narrow control bar; the level it holds is
        // still the one in force.
        if (_audio is null) return;
        VolumeSlider.Value = Math.Clamp(Math.Round((VolumeSlider.Value + delta) * 20) / 20, 0, 1);
        if (MuteButton.IsChecked == true && delta > 0) MuteButton.IsChecked = false;
        ShowToast($"Volume {(int)Math.Round(VolumeSlider.Value * 100)}%", "");
    }

    /// <summary>The level in words, on the slider's tooltip and for a screen reader: on a short
    /// track, "35%" is more readable than a thumb position. Mute reads as zero.</summary>
    private void UpdateVolumeLabel()
    {
        // Callers can run while the window is still half-built.
        if (VolumeSlider is null || MuteButton is null) return;

        var muted = MuteButton.IsChecked == true;
        var percent = (int)Math.Round((muted ? 0 : VolumeSlider.Value) * 100);
        var text = muted ? "Volume: muted" : $"Volume: {percent}%";
        VolumeSlider.ToolTip = text;
        System.Windows.Automation.AutomationProperties.SetHelpText(VolumeSlider, text);
    }

    // ------------------------------------------------------------- screenshots

    private void OnSnapshot(object sender, RoutedEventArgs e) => SaveSnapshot();

    private void OnCopySnapshot(object sender, RoutedEventArgs e) => CopySnapshot();

    /// <summary>The picture as shown - rotated and flipped like the window - or null before
    /// the first frame. Screenshots follow the display, not the wire.</summary>
    private BitmapSource? CaptureFrame()
    {
        // Only what is on screen: a phone still waiting to be allowed is not captured, nor one
        // being turned away - including a phone that took over from one already on screen.
        if (VideoHost.Visibility != Visibility.Visible || _approvalPending || _sessionRejected) return null;

        var raw = Video.Snapshot();
        if (raw is null) return null;
        if (_settings.Rotation == 0 && !_settings.MirrorHorizontally) return ComposeMarkup(raw);

        var group = new TransformGroup();
        if (_settings.MirrorHorizontally) group.Children.Add(new ScaleTransform(-1, 1));
        if (_settings.Rotation != 0) group.Children.Add(new RotateTransform(_settings.Rotation));
        var transformed = new TransformedBitmap(raw, group);
        transformed.Freeze();
        return ComposeMarkup(transformed);
    }

    /// <returns>True if a screenshot was written.</returns>
    private bool SaveSnapshot()
    {
        var snapshot = CaptureFrame();
        if (snapshot is null)
        {
            ShowToast("Nothing to capture yet", "");
            return false;
        }

        try
        {
            Directory.CreateDirectory(_settings.CaptureDirectory);
            var jpeg = _settings.ScreenshotFormat == ScreenshotFormat.Jpeg;
            var path = CaptureNaming.NewPath(_settings.CaptureDirectory, DateTime.Now, jpeg ? ".jpg" : ".png");

            BitmapEncoder encoder = jpeg ? new JpegBitmapEncoder { QualityLevel = 92 } : new PngBitmapEncoder();
            // JPEG has no fourth channel to carry, and the encoder is given exactly what it takes.
            BitmapSource frame = jpeg ? new FormatConvertedBitmap(snapshot, PixelFormats.Bgr24, null, 0) : snapshot;
            encoder.Frames.Add(BitmapFrame.Create(frame));
            // CreateNew rather than Create: the name was picked because it was free, and should
            // anything have taken it since, failing is better than overwriting it.
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write)) encoder.Save(stream);

            _log.Info($"saved {path}");
            Flash();
            ShowTransientStatus($"Saved {Path.GetFileName(path)}", path);
            ShowToast("Screenshot saved", "", "View", ShowCaptures);
            RefreshCapturesIfOpen();

            // After the toast, so a clipboard that is busy is what the user is told about.
            if (_settings.CopyScreenshotToClipboard) TrySetClipboardImage(snapshot);
            return true;
        }
        catch (Exception ex)
        {
            _log.Error("could not save the screenshot", ex);
            ShowToast("The screenshot could not be saved", "");
            return false;
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
        var shown = _settings.ShowStats && VideoHost.Visibility == Visibility.Visible && !_isMiniPlayer;
        StatsHud.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
        if (shown) UpdateMetrics();
    }

    // ------------------------------------------------------------------- pause

    private void OnMenuPause(object sender, RoutedEventArgs e) => TogglePause();

    private void OnPausedPillClick(object sender, RoutedEventArgs e) => SetPaused(false);

    private void TogglePause() => SetPaused(!Video.IsFrozen);

    /// <summary>
    /// Holds the picture still while the phone carries on - to point at something during a
    /// presentation, or read a message before it scrolls away. Sound, recording and the
    /// connection are all left running; only what is drawn stops.
    /// </summary>
    private void SetPaused(bool paused, bool announce = true)
    {
        if (paused && VideoHost.Visibility != Visibility.Visible) return;

        var changed = Video.IsFrozen != paused;
        Video.IsFrozen = paused;
        PausedPill.Visibility = paused ? Visibility.Visible : Visibility.Collapsed;
        UpdateMiniControls();
        UpdatePauseButton();

        if (changed && announce)
            ShowToast(paused ? "Picture paused - the phone is still connected" : "Picture resumed", paused ? "" : "");
    }
}
