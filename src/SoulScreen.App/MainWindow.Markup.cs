using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using SoulScreen.App.Logic;

namespace SoulScreen.App;

/// <summary>
/// Markup: a pen, a highlighter and a laser pointer over the mirrored picture, for pointing
/// something out during a demonstration or a lesson.
/// <para>
/// The drawing surface sits inside the zoomed viewport, so strokes zoom and pan with the
/// picture, and strokes are carried along whenever the picture itself moves or grows with the
/// window. A change that makes the drawing meaningless - a new fit, rotation or mirroring, or
/// the phone turning - clears it. Leaving markup clears it too: a drawing left behind over a
/// live picture would soon be pointing at something else.
/// </para>
/// <para>
/// Space still pauses the picture, which is the natural way to draw on something that would
/// otherwise scroll away; a screenshot taken while marking up carries the drawing.
/// </para>
/// </summary>
public partial class MainWindow
{
    private enum MarkupTool
    {
        Pen,
        Highlighter,
        Laser,
        Eraser,
    }

    private static readonly (string Name, Color Color)[] InkColors =
    [
        ("Red", Color.FromRgb(0xFF, 0x3B, 0x30)),
        ("Orange", Color.FromRgb(0xFF, 0x9F, 0x0A)),
        ("Yellow", Color.FromRgb(0xFF, 0xD6, 0x0A)),
        ("Green", Color.FromRgb(0x30, 0xD1, 0x58)),
        ("Blue", Color.FromRgb(0x0A, 0x84, 0xFF)),
        ("White", Colors.White),
    ];

    private static readonly (string Name, double Width)[] InkSizes = [("Fine", 2.5), ("Medium", 5), ("Bold", 10)];

    private static readonly Color LaserColor = Color.FromRgb(0xFF, 0x3B, 0x30);

    /// <summary>How long a laser stroke stays at full strength, and when it is gone.</summary>
    private static readonly TimeSpan LaserHold = TimeSpan.FromMilliseconds(450);
    private static readonly TimeSpan LaserLife = TimeSpan.FromMilliseconds(1100);

    private MarkupTool _markupTool = MarkupTool.Pen;

    /// <summary>Each change to the drawing, newest on top: what it added and what it took away.</summary>
    private readonly Stack<(StrokeCollection Added, StrokeCollection Removed)> _markupHistory = new();

    /// <summary>Laser strokes still fading, with when each was drawn.</summary>
    private readonly List<(Stroke Stroke, long Born)> _laserStrokes = [];

    /// <summary>Set while the code itself changes the strokes, so the change is not recorded as an edit.</summary>
    private bool _markupRewriting;

    private bool _syncingMarkupChoices;
    private DispatcherTimer? _laserTimer;

    /// <summary>Where the picture was when the strokes were last placed, in viewport coordinates.</summary>
    private Rect _markupPictureRect = Rect.Empty;

    private bool IsMarkupActive => MarkupBar.Visibility == Visibility.Visible;

    private void InitialiseMarkup()
    {
        BuildMarkupChoices();

        MarkupCanvas.Strokes.StrokesChanged += OnMarkupStrokesChanged;
        MarkupCanvas.StrokeCollected += OnMarkupStrokeCollected;
        VideoViewport.SizeChanged += (_, _) => KeepMarkupOnPicture();
        Video.SizeChanged += (_, _) => KeepMarkupOnPicture();

        // The same arrangement as the toolbar's overflow: a click on the button while its popup
        // is open should only close it, not close it and open it again.
        MarkupColorPopup.Opened += (_, _) => MarkupColorButton.IsHitTestVisible = false;
        MarkupColorPopup.Closed += (_, _) => MarkupColorButton.IsHitTestVisible = true;
        MarkupColorPopup.CustomPopupPlacementCallback = (popupSize, targetSize, _) =>
            [new CustomPopupPlacement(new Point((targetSize.Width - popupSize.Width) / 2, -popupSize.Height - 2), PopupPrimaryAxis.Vertical)];

        _syncingMarkupChoices = true;
        try { MarkupPen.IsChecked = true; }
        finally { _syncingMarkupChoices = false; }

        ApplyMarkupTool();
        UpdateMarkupButtons();
    }

    private void BuildMarkupChoices()
    {
        var swatchStyle = (Style)FindResource("HudSwatch");
        for (var i = 0; i < InkColors.Length; i++)
        {
            var (name, color) = InkColors[i];
            var swatch = new RadioButton
            {
                Style = swatchStyle,
                GroupName = "MarkupColor",
                Tag = i,
                Background = Frozen(color),
                ToolTip = name,
            };
            AutomationProperties.SetName(swatch, $"{name} ink");
            swatch.Checked += OnMarkupColorChecked;
            MarkupColorChoices.Children.Add(swatch);
        }

        var sizeStyle = (Style)FindResource("HudSizeChoice");
        var dotBrush = (Brush)FindResource("HudText");
        for (var i = 0; i < InkSizes.Length; i++)
        {
            var (name, _) = InkSizes[i];
            var diameter = 5 + i * 4;
            var choice = new RadioButton
            {
                Style = sizeStyle,
                GroupName = "MarkupSize",
                Tag = i,
                Content = new Ellipse { Width = diameter, Height = diameter, Fill = dotBrush },
                ToolTip = name,
            };
            AutomationProperties.SetName(choice, $"{name} stroke");
            choice.Checked += OnMarkupSizeChecked;
            MarkupSizeChoices.Children.Add(choice);
        }

        SyncMarkupChoices();
    }

    private void SyncMarkupChoices()
    {
        _syncingMarkupChoices = true;
        try
        {
            foreach (var swatch in MarkupColorChoices.Children.OfType<RadioButton>())
                swatch.IsChecked = swatch.Tag is int index && index == _settings.MarkupColorIndex;
            foreach (var choice in MarkupSizeChoices.Children.OfType<RadioButton>())
                choice.IsChecked = choice.Tag is int index && index == _settings.MarkupSizeIndex;
        }
        finally
        {
            _syncingMarkupChoices = false;
        }
    }

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    // ------------------------------------------------------------------ entering and leaving

    private void OnMarkupToggled(object sender, RoutedEventArgs e)
    {
        if (MarkupButton.IsChecked == true) BeginMarkup();
        else EndMarkup();
    }

    private void OnMenuMarkup(object sender, RoutedEventArgs e) => ToggleMarkup();

    private void OnMarkupDone(object sender, RoutedEventArgs e) => MarkupButton.IsChecked = false;

    private void ToggleMarkup()
    {
        if (MarkupButton.IsChecked != true && VideoHost.Visibility != Visibility.Visible)
        {
            ShowToast("Markup needs a mirrored phone", "");
            return;
        }
        MarkupButton.IsChecked = MarkupButton.IsChecked != true;
    }

    private void BeginMarkup()
    {
        if (_shuttingDown || VideoHost.Visibility != Visibility.Visible)
        {
            MarkupButton.IsChecked = false;
            return;
        }

        if (_isMiniPlayer) ExitMiniPlayer();
        // Everything that would cover the picture being drawn on goes.
        ClosePalette();
        CloseHelp();
        SettingsButton.IsChecked = false;
        CapturesButton.IsChecked = false;
        MoreButton.IsChecked = false;

        MarkupCanvas.Visibility = Visibility.Visible;
        MarkupBar.Visibility = Visibility.Visible;
        _markupPictureRect = PictureRect();
        ApplyMarkupTool();
        UpdateMarkupButtons();
        // The markup bar takes the control bar's place at the foot of the picture.
        UpdateControlBar();
        MarkupButton.ToolTip = "Leave markup (Esc)";

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        MarkupBar.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease });
        MarkupBarSlide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(14, 0, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
    }

    private void EndMarkup()
    {
        var hadFocus = MarkupBar.IsKeyboardFocusWithin;
        MarkupColorButton.IsChecked = false;
        _laserTimer?.Stop();

        ClearMarkupStrokes(recordHistory: false);
        _laserStrokes.Clear();
        _markupHistory.Clear();
        _markupPictureRect = Rect.Empty;

        MarkupCanvas.EditingMode = InkCanvasEditingMode.None;
        MarkupCanvas.Visibility = Visibility.Collapsed;
        MarkupBar.Visibility = Visibility.Collapsed;
        MarkupButton.ToolTip = "Markup (Ctrl+E)";

        UpdateMarkupButtons();
        UpdateControlBar();
        if (hadFocus) Focus();
    }

    // ------------------------------------------------------------------ tools

    private void OnMarkupToolChanged(object sender, RoutedEventArgs e)
    {
        if (_syncingMarkupChoices || MarkupCanvas is null) return;
        _markupTool = ReferenceEquals(sender, MarkupHighlighter) ? MarkupTool.Highlighter
            : ReferenceEquals(sender, MarkupLaser) ? MarkupTool.Laser
            : ReferenceEquals(sender, MarkupEraser) ? MarkupTool.Eraser
            : MarkupTool.Pen;
        ApplyMarkupTool();
    }

    private void SelectMarkupTool(MarkupTool tool)
    {
        var button = tool switch
        {
            MarkupTool.Highlighter => MarkupHighlighter,
            MarkupTool.Laser => MarkupLaser,
            MarkupTool.Eraser => MarkupEraser,
            _ => MarkupPen,
        };
        button.IsChecked = true;
    }

    private void OnMarkupColorChecked(object sender, RoutedEventArgs e)
    {
        if (_syncingMarkupChoices || sender is not RadioButton { Tag: int index }) return;
        if (_settings.MarkupColorIndex != index)
        {
            _settings.MarkupColorIndex = index;
            _settings.Save();
        }
        // Picking an ink means drawing with it.
        if (_markupTool is MarkupTool.Eraser or MarkupTool.Laser) SelectMarkupTool(MarkupTool.Pen);
        ApplyMarkupTool();
        MarkupColorButton.IsChecked = false;
    }

    private void OnMarkupSizeChecked(object sender, RoutedEventArgs e)
    {
        if (_syncingMarkupChoices || sender is not RadioButton { Tag: int index }) return;
        if (_settings.MarkupSizeIndex != index)
        {
            _settings.MarkupSizeIndex = index;
            _settings.Save();
        }
        if (_markupTool == MarkupTool.Eraser) SelectMarkupTool(MarkupTool.Pen);
        ApplyMarkupTool();
        MarkupColorButton.IsChecked = false;
    }

    /// <summary>Points the canvas at the tool, ink and width in force.</summary>
    private void ApplyMarkupTool()
    {
        if (MarkupColorDot is null) return;

        var color = InkColors[Math.Clamp(_settings.MarkupColorIndex, 0, InkColors.Length - 1)].Color;
        var width = InkSizes[Math.Clamp(_settings.MarkupSizeIndex, 0, InkSizes.Length - 1)].Width;
        MarkupColorDot.Fill = Frozen(color);

        MarkupCanvas.DefaultDrawingAttributes = _markupTool switch
        {
            // A chisel tip, taller than it is wide, as a real highlighter draws.
            MarkupTool.Highlighter => new DrawingAttributes
            {
                Color = color,
                IsHighlighter = true,
                StylusTip = StylusTip.Rectangle,
                Width = width * 1.6,
                Height = width * 4.5,
                FitToCurve = false,
                IgnorePressure = true,
            },
            MarkupTool.Laser => new DrawingAttributes
            {
                Color = LaserColor,
                Width = 7,
                Height = 7,
                FitToCurve = true,
                IgnorePressure = true,
            },
            _ => new DrawingAttributes
            {
                Color = color,
                Width = width,
                Height = width,
                FitToCurve = true,
            },
        };

        MarkupCanvas.EditingMode = !IsMarkupActive ? InkCanvasEditingMode.None
            : _markupTool == MarkupTool.Eraser ? InkCanvasEditingMode.EraseByStroke
            : InkCanvasEditingMode.Ink;
    }

    // ------------------------------------------------------------------ history

    private void OnMarkupStrokesChanged(object? sender, StrokeCollectionChangedEventArgs e)
    {
        if (_markupRewriting) return;

        // A laser stroke is gone in a second, and is never something to undo back into view.
        var removed = new StrokeCollection();
        foreach (var stroke in e.Removed)
        {
            if (ForgetLaserStroke(stroke)) continue;
            removed.Add(stroke);
        }
        var added = _markupTool == MarkupTool.Laser ? new StrokeCollection() : new StrokeCollection(e.Added);

        if (added.Count == 0 && removed.Count == 0) return;
        _markupHistory.Push((added, removed));
        UpdateMarkupButtons();
    }

    private void OnMarkupUndo(object sender, RoutedEventArgs e) => UndoMarkup();

    private void UndoMarkup()
    {
        if (_markupHistory.Count == 0) return;
        var (added, removed) = _markupHistory.Pop();

        _markupRewriting = true;
        try
        {
            foreach (var stroke in added)
                if (MarkupCanvas.Strokes.Contains(stroke)) MarkupCanvas.Strokes.Remove(stroke);
            foreach (var stroke in removed)
                if (!MarkupCanvas.Strokes.Contains(stroke)) MarkupCanvas.Strokes.Add(stroke);
        }
        finally
        {
            _markupRewriting = false;
        }

        UpdateMarkupButtons();
    }

    private void OnMarkupClear(object sender, RoutedEventArgs e) => ClearMarkupStrokes(recordHistory: true);

    /// <summary>Takes every stroke away; with <paramref name="recordHistory"/>, as one step Undo
    /// can put back.</summary>
    private void ClearMarkupStrokes(bool recordHistory)
    {
        if (MarkupCanvas.Strokes.Count == 0) return;

        var drawn = new StrokeCollection(MarkupCanvas.Strokes.Where(stroke => !IsLaserStroke(stroke)));
        _markupRewriting = true;
        try { MarkupCanvas.Strokes.Clear(); }
        finally { _markupRewriting = false; }
        _laserStrokes.Clear();

        if (recordHistory && drawn.Count > 0) _markupHistory.Push((new StrokeCollection(), drawn));
        UpdateMarkupButtons();
    }

    /// <summary>
    /// Clears the drawing when what it was drawn on has changed shape - a new fit, rotation or
    /// mirroring, or the phone turning. The picture's new place is taken once layout has settled,
    /// so the next resize carries the next drawing correctly.
    /// </summary>
    private void ResetMarkupForNewPicture()
    {
        if (!IsMarkupActive) return;
        ClearMarkupStrokes(recordHistory: false);
        _markupHistory.Clear();
        UpdateMarkupButtons();
        _markupPictureRect = Rect.Empty;
        Dispatcher.BeginInvoke(() =>
        {
            if (IsMarkupActive) _markupPictureRect = PictureRect();
        }, DispatcherPriority.Loaded);
    }

    private void UpdateMarkupButtons()
    {
        if (MarkupUndoButton is null) return;
        MarkupUndoButton.IsEnabled = _markupHistory.Count > 0;
        MarkupClearButton.IsEnabled = MarkupCanvas.Strokes.Any(stroke => !IsLaserStroke(stroke));
    }

    // ------------------------------------------------------------------ laser

    private void OnMarkupStrokeCollected(object sender, InkCanvasStrokeCollectedEventArgs e)
    {
        if (_markupTool != MarkupTool.Laser) return;
        _laserStrokes.Add((e.Stroke, Stopwatch.GetTimestamp()));
        _laserTimer ??= CreateLaserTimer();
        if (!_laserTimer.IsEnabled) _laserTimer.Start();
    }

    private DispatcherTimer CreateLaserTimer()
    {
        var timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(30) };
        timer.Tick += (_, _) => FadeLaserStrokes();
        return timer;
    }

    private void FadeLaserStrokes()
    {
        var now = Stopwatch.GetTimestamp();
        var fadeMilliseconds = (LaserLife - LaserHold).TotalMilliseconds;

        _markupRewriting = true;
        try
        {
            for (var i = _laserStrokes.Count - 1; i >= 0; i--)
            {
                var (stroke, born) = _laserStrokes[i];
                var age = TimeSpan.FromSeconds((now - born) / (double)Stopwatch.Frequency);

                if (age >= LaserLife)
                {
                    if (MarkupCanvas.Strokes.Contains(stroke)) MarkupCanvas.Strokes.Remove(stroke);
                    _laserStrokes.RemoveAt(i);
                    continue;
                }

                if (age <= LaserHold) continue;
                var strength = Math.Clamp(1 - (age - LaserHold).TotalMilliseconds / fadeMilliseconds, 0, 1);
                stroke.DrawingAttributes.Color = Color.FromArgb((byte)Math.Round(255 * strength), LaserColor.R, LaserColor.G, LaserColor.B);
            }
        }
        finally
        {
            _markupRewriting = false;
        }

        if (_laserStrokes.Count == 0) _laserTimer?.Stop();
    }

    private bool IsLaserStroke(Stroke stroke) => _laserStrokes.Any(entry => ReferenceEquals(entry.Stroke, stroke));

    private bool ForgetLaserStroke(Stroke stroke)
    {
        var index = _laserStrokes.FindIndex(entry => ReferenceEquals(entry.Stroke, stroke));
        if (index < 0) return false;
        _laserStrokes.RemoveAt(index);
        return true;
    }

    // ------------------------------------------------------------------ geometry

    /// <summary>Carries the strokes - and the ones Undo could bring back - from where the picture
    /// was to where it is now.</summary>
    private void KeepMarkupOnPicture()
    {
        if (!IsMarkupActive) return;

        var now = PictureRect();
        var previous = _markupPictureRect;
        _markupPictureRect = now;
        if (previous.IsEmpty) return;
        if (RectMapping.Between(ToBounds(previous), ToBounds(now)) is not { IsIdentity: false } map) return;

        var matrix = new Matrix(map.ScaleX, 0, 0, map.ScaleY, map.OffsetX, map.OffsetY);
        var moved = new HashSet<Stroke>(ReferenceEqualityComparer.Instance);
        foreach (var stroke in MarkupCanvas.Strokes)
        {
            if (moved.Add(stroke)) stroke.Transform(matrix, applyToStylusTip: false);
        }
        foreach (var (added, removed) in _markupHistory)
        {
            foreach (var stroke in added.Concat(removed))
                if (moved.Add(stroke)) stroke.Transform(matrix, applyToStylusTip: false);
        }
    }

    private static Bounds ToBounds(Rect rect) => new(rect.X, rect.Y, rect.Width, rect.Height);

    /// <summary>The frame with the drawing laid over it at the picture's own resolution, or the
    /// frame unchanged when nothing is drawn.</summary>
    private BitmapSource ComposeMarkup(BitmapSource frame)
    {
        if (!IsMarkupActive || MarkupCanvas.Strokes.Count == 0) return frame;

        var width = frame.PixelWidth;
        var height = frame.PixelHeight;
        if (RectMapping.Between(ToBounds(PictureRect()), new Bounds(0, 0, width, height)) is not { } map) return frame;

        try
        {
            var visual = new DrawingVisual();
            using (var context = visual.RenderOpen())
            {
                var area = new Rect(0, 0, width, height);
                context.DrawImage(frame, area);
                context.PushClip(new RectangleGeometry(area));
                context.PushTransform(new MatrixTransform(map.ScaleX, 0, 0, map.ScaleY, map.OffsetX, map.OffsetY));
                MarkupCanvas.Strokes.Draw(context);
                context.Pop();
                context.Pop();
            }

            var composed = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            composed.Render(visual);
            composed.Freeze();
            return composed;
        }
        catch (Exception ex)
        {
            // Better the picture without the drawing than no screenshot at all.
            _log.Warn("could not lay the drawing over the screenshot", ex);
            return frame;
        }
    }

    /// <summary>The frame with the watermark laid over it, or the frame unchanged when the
    /// watermark is off, has no image, or the image cannot be read. A screenshot must never
    /// fail to save because of the watermark - at worst it is skipped for that shot.</summary>
    private BitmapSource ComposeWatermark(BitmapSource frame)
    {
        var settings = _settings.Watermark;
        if (!settings.Enabled || string.IsNullOrWhiteSpace(settings.ImagePath)) return frame;

        var mark = LoadWatermarkImage(settings.ImagePath);
        if (mark is null) return frame;

        try
        {
            var (x, y, width, height) = WatermarkPlacement.Place(
                frame.PixelWidth, frame.PixelHeight, mark.PixelWidth, mark.PixelHeight,
                settings.Scale, settings.Corner, settings.MarginFraction);
            if (width <= 0 || height <= 0) return frame;

            var visual = new DrawingVisual();
            using (var context = visual.RenderOpen())
            {
                context.DrawImage(frame, new Rect(0, 0, frame.PixelWidth, frame.PixelHeight));
                context.PushOpacity(Math.Clamp(settings.Opacity, 0, 1));
                context.DrawImage(mark, new Rect(x, y, width, height));
                context.Pop();
            }

            var composed = new RenderTargetBitmap(frame.PixelWidth, frame.PixelHeight, 96, 96, PixelFormats.Pbgra32);
            composed.Render(visual);
            composed.Freeze();
            return composed;
        }
        catch (Exception ex)
        {
            _log.Warn("could not lay the watermark over the screenshot", ex);
            return frame;
        }
    }

    /// <summary>Cached decode of the watermark file, refreshed whenever its path or its own
    /// last-write time changes - a replaced logo takes effect on the very next screenshot,
    /// but an unchanged one is not re-read from disk on every single capture.</summary>
    private (string Path, DateTime WrittenUtc, BitmapImage Image)? _watermarkCache;

    private BitmapImage? LoadWatermarkImage(string path)
    {
        try
        {
            var writtenUtc = System.IO.File.GetLastWriteTimeUtc(path);
            if (_watermarkCache is { } cached && cached.Path == path && cached.WrittenUtc == writtenUtc)
                return cached.Image;

            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(path, UriKind.Absolute);
            image.EndInit();
            image.Freeze();

            _watermarkCache = (path, writtenUtc, image);
            return image;
        }
        catch (Exception ex)
        {
            // Moved, deleted, or not actually an image: the setting stays on, but this
            // screenshot - and every one after it until the file is fixed - goes out plain.
            _log.Warn($"could not read the watermark image at {path}", ex);
            _watermarkCache = null;
            return null;
        }
    }

    // ------------------------------------------------------------------ keyboard

    /// <summary>The markup keys: Ctrl+Z to undo, and a letter for each tool, as Preview's
    /// markup toolbar has them. Only while marking up and nothing covers the picture.</summary>
    private bool HandleMarkupKey(KeyEventArgs e)
    {
        if (!IsMarkupActive || IsPanelOpen() || Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase) return false;

        var modifiers = Keyboard.Modifiers;
        if (modifiers == ModifierKeys.Control && e.Key == Key.Z)
        {
            UndoMarkup();
            return true;
        }

        if (modifiers != ModifierKeys.None) return false;
        switch (e.Key)
        {
            case Key.P: SelectMarkupTool(MarkupTool.Pen); return true;
            case Key.H: SelectMarkupTool(MarkupTool.Highlighter); return true;
            case Key.L: SelectMarkupTool(MarkupTool.Laser); return true;
            case Key.E: SelectMarkupTool(MarkupTool.Eraser); return true;
            case Key.Delete or Key.Back: ClearMarkupStrokes(recordHistory: true); return true;
            default: return false;
        }
    }
}
