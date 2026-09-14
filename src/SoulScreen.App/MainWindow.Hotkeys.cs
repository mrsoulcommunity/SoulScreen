using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Shell;

namespace SoulScreen.App;

/// <summary>
/// Reaching the mirror without the window in front: shortcuts that work from any application,
/// and buttons on the taskbar thumbnail - screenshot, record, mute, mini player - with a red
/// badge on the taskbar button while a recording runs.
/// </summary>
public partial class MainWindow
{
    private const string GlobalHotkeysDetail =
        "Ctrl+Alt+Shift with S saves a screenshot, R records, M opens the mini player and O brings SoulScreen forward - whichever app is in front.";

    private GlobalHotkeys? _hotkeys;

    private ThumbButtonInfo? _thumbSnapshot;
    private ThumbButtonInfo? _thumbRecord;
    private ThumbButtonInfo? _thumbMute;
    private ThumbButtonInfo? _thumbMini;
    private ImageSource? _thumbRecordImage;
    private ImageSource? _thumbStopImage;
    private ImageSource? _thumbMuteImage;
    private ImageSource? _thumbUnmuteImage;
    private ImageSource? _recordingOverlay;

    private void InitialiseHotkeysAndTaskbar()
    {
        // Both need the window's handle.
        SourceInitialized += (_, _) =>
        {
            _hotkeys = new GlobalHotkeys(this);
            _hotkeys.Pressed += OnGlobalHotkey;
            ApplyGlobalHotkeys(announce: false);
            InitialiseTaskbar();
        };
    }

    // ------------------------------------------------------------------ global shortcuts

    private void ApplyGlobalHotkeys(bool announce)
    {
        if (_hotkeys is null) return;

        if (!_settings.GlobalHotkeys)
        {
            _hotkeys.Unregister();
            GlobalHotkeysRow.Detail = GlobalHotkeysDetail;
            return;
        }

        var failed = _hotkeys.Register();
        if (failed.Count == 0)
        {
            GlobalHotkeysRow.Detail = GlobalHotkeysDetail;
            if (announce) ShowToast("Shortcuts now work from any app", "");
            return;
        }

        var taken = string.Join(", ", failed.Select(action =>
            GlobalHotkeys.Gesture(GlobalHotkeys.Bindings.First(binding => binding.Action == action).Key)));
        GlobalHotkeysRow.Detail = $"{GlobalHotkeysDetail} {taken} {(failed.Count == 1 ? "is" : "are")} already taken by another app.";
        if (announce)
        {
            ShowToast(failed.Count == GlobalHotkeys.Bindings.Length
                ? "Another app already holds these shortcuts"
                : "Some of these shortcuts are taken by another app", "");
        }
    }

    private void OnGlobalHotkeysChanged(object sender, RoutedEventArgs e)
    {
        if (_populatingSettings) return;
        _settings.GlobalHotkeys = GlobalHotkeysCheck.IsChecked == true;
        _settings.Save();
        ApplyGlobalHotkeys(announce: true);
    }

    private void OnGlobalHotkey(HotkeyAction action)
    {
        if (_shuttingDown) return;

        switch (action)
        {
            case HotkeyAction.Screenshot:
                if (SaveSnapshot()) NotifyFromTray("Screenshot saved", "It is in Captures.");
                break;

            case HotkeyAction.Record:
                if (!RecordButton.IsEnabled)
                {
                    ShowToast("Recording needs a mirrored phone", "");
                    break;
                }
                RecordButton.IsChecked = RecordButton.IsChecked != true;
                NotifyFromTray(RecordButton.IsChecked == true ? "Recording" : "Recording stopped",
                    RecordButton.IsChecked == true ? "Press Ctrl+Alt+Shift+R again to stop." : "The file is being saved to Captures.");
                break;

            case HotkeyAction.MiniPlayer:
                if (_hiddenToTray || WindowState == WindowState.Minimized) RestoreFromTray();
                ToggleMiniPlayer();
                break;

            case HotkeyAction.ShowWindow:
                ActivateFromAnotherInstance();
                break;
        }
    }

    // ------------------------------------------------------------------ taskbar

    private void InitialiseTaskbar()
    {
        try
        {
            var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            _thumbRecordImage = ShapeImage(new EllipseGeometry(new Point(8, 8), 5.5, 5.5), Color.FromRgb(0xFF, 0x45, 0x3A));
            _thumbStopImage = ShapeImage(new RectangleGeometry(new Rect(3.5, 3.5, 9, 9), 1.5, 1.5), Colors.White);
            _thumbMuteImage = GlyphImage("", pixelsPerDip);
            _thumbUnmuteImage = GlyphImage("", pixelsPerDip);
            _recordingOverlay = ShapeImage(new EllipseGeometry(new Point(8, 8), 6, 6), Color.FromRgb(0xFF, 0x3B, 0x30), Colors.White);

            _thumbSnapshot = CreateThumb(GlyphImage("", pixelsPerDip), "Save a screenshot", () => SaveSnapshot());
            _thumbRecord = CreateThumb(_thumbRecordImage, "Start recording", () =>
            {
                if (RecordButton.IsEnabled) RecordButton.IsChecked = RecordButton.IsChecked != true;
            });
            _thumbMute = CreateThumb(_thumbMuteImage, "Mute the phone", () => MuteButton.IsChecked = MuteButton.IsChecked != true);
            _thumbMini = CreateThumb(GlyphImage("", pixelsPerDip), "Mini player", ToggleMiniPlayer);

            TaskbarItemInfo = new TaskbarItemInfo
            {
                ThumbButtonInfos = new ThumbButtonInfoCollection { _thumbSnapshot, _thumbRecord, _thumbMute, _thumbMini },
            };
            UpdateTaskbar();
        }
        catch (Exception ex)
        {
            // Explorer restarting, or a shell with no taskbar: the window works without it.
            _log.Warn("the taskbar buttons are unavailable", ex);
            TaskbarItemInfo = null;
            _thumbSnapshot = null;
        }
    }

    private ThumbButtonInfo CreateThumb(ImageSource image, string description, Action run)
    {
        var button = new ThumbButtonInfo
        {
            ImageSource = image,
            Description = description,
            DismissWhenClicked = false,
            Visibility = Visibility.Collapsed,
        };
        button.Click += (_, _) =>
        {
            try { run(); }
            catch (Exception ex) { _log.Warn($"the taskbar button \"{button.Description}\" failed", ex); }
        };
        return button;
    }

    /// <summary>Keeps the taskbar thumbnail's buttons and badge in step with the session.</summary>
    private void UpdateTaskbar()
    {
        if (TaskbarItemInfo is not { } info || _thumbSnapshot is null || _thumbRecord is null || _thumbMute is null || _thumbMini is null)
            return;

        var streaming = !_shuttingDown && VideoHost.Visibility == Visibility.Visible;
        var recording = RecordButton.IsChecked == true;
        var muted = MuteButton.IsChecked == true;

        SetThumb(_thumbSnapshot, streaming, "Save a screenshot", null);
        SetThumb(_thumbRecord, streaming && RecordButton.IsEnabled, recording ? "Stop recording" : "Start recording",
            recording ? _thumbStopImage : _thumbRecordImage);
        SetThumb(_thumbMute, streaming && _audio is not null, muted ? "Unmute the phone" : "Mute the phone",
            muted ? _thumbUnmuteImage : _thumbMuteImage);
        SetThumb(_thumbMini, streaming, _isMiniPlayer ? "Leave the mini player" : "Mini player", null);

        var overlay = recording ? _recordingOverlay : null;
        if (!ReferenceEquals(info.Overlay, overlay))
        {
            info.Overlay = overlay;
            info.Description = recording ? "Recording" : string.Empty;
        }
    }

    private static void SetThumb(ThumbButtonInfo button, bool visible, string description, ImageSource? image)
    {
        var visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (button.Visibility != visibility) button.Visibility = visibility;
        if (button.Description != description) button.Description = description;
        if (image is not null && !ReferenceEquals(button.ImageSource, image)) button.ImageSource = image;
    }

    /// <summary>A shape on a transparent 16-unit square, which Windows scales to the icon size it wants.</summary>
    private static ImageSource ShapeImage(Geometry shape, Color fill, Color? outline = null)
    {
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, 16, 16))));
        group.Children.Add(new GeometryDrawing(Frozen(fill), outline is { } edge ? new Pen(Frozen(edge), 1.5) : null, shape));
        var image = new DrawingImage(group);
        image.Freeze();
        return image;
    }

    /// <summary>An icon-font glyph as a white vector image, centred on the square.</summary>
    private ImageSource GlyphImage(string glyph, double pixelsPerDip)
    {
        var typeface = new Typeface((FontFamily)FindResource("IconFont"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var text = new FormattedText(glyph, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, 14, Brushes.White, pixelsPerDip);
        var geometry = text.BuildGeometry(new Point(0, 0));
        var bounds = geometry.Bounds;
        if (!bounds.IsEmpty)
            geometry.Transform = new TranslateTransform(8 - bounds.X - bounds.Width / 2, 8 - bounds.Y - bounds.Height / 2);
        return ShapeImage(geometry, Colors.White);
    }
}
