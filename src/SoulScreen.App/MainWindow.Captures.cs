using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SoulScreen.App.Logic;
using SoulScreen.Core.Time;

namespace SoulScreen.App;

/// <summary>
/// The captures gallery: every screenshot and recording SoulScreen has saved to the capture
/// folder, newest first, to open, copy, find in Explorer or throw away without leaving the app.
/// <para>
/// Only files SoulScreen named are shown - the folder may well be the user's whole Pictures
/// folder. Thumbnails are decoded small, one at a time, off the UI thread, and let go of when
/// the panel closes, so an open gallery cannot put the picture behind.
/// </para>
/// </summary>
public partial class MainWindow
{
    /// <summary>The most captures listed at once, which bounds the thumbnails held in memory.</summary>
    private const int MaxCapturesShown = 120;

    /// <summary>Narrowest a tile may be before the gallery drops a column.</summary>
    private const double MinCaptureTileWidth = 150;

    /// <summary>Bumped whenever the list is rebuilt, so a slow listing or thumbnail for an old
    /// list never lands in a new one.</summary>
    private int _captureGeneration;

    private List<CaptureItem> _allCaptures = [];

    /// <summary>Most files OCR'd in one pass of the gallery. Already-indexed files are
    /// instant (a cache lookup), so this only bounds how much work a *new* pile of
    /// screenshots costs before the rest catch up on a later refresh.</summary>
    private const int MaxOcrPerPass = 200;

    private OcrCache? _ocrCache;

    private sealed class CaptureItem : INotifyPropertyChanged
    {
        private Brush? _thumbnail;

        public required string Path { get; init; }
        public required CaptureKind Kind { get; init; }
        public required DateTime ModifiedLocal { get; init; }
        public required long Size { get; init; }

        /// <summary>Text OCR found in the screenshot, or null before it has been indexed
        /// (or for anything that is not a screenshot). Empty string, not null, once indexed
        /// with nothing recognised - the distinction is "not looked at yet" vs "looked at,
        /// nothing there", which matters so an item is never re-OCR'd forever for having
        /// no text.</summary>
        public string? OcrText { get; set; }

        public string FileName => System.IO.Path.GetFileName(Path);

        public string Title => FormatCaptureTime(ModifiedLocal);

        public string Detail => Kind == CaptureKind.Recording
            ? $"Recording · {CaptureNaming.FormatSize(Size)}"
            : $"{System.IO.Path.GetExtension(Path).TrimStart('.').ToUpperInvariant()} · {CaptureNaming.FormatSize(Size)}";

        public Visibility PlayBadge => Kind == CaptureKind.Recording ? Visibility.Visible : Visibility.Collapsed;

        public string AccessibleName
        {
            get
            {
                // The title already carries both calendars when both are configured (the
                // formatter appends the Gregorian in parentheses). Exposing both is required
                // for screen readers, so do not collapse "Both" down to a single calendar.
                var stamp = Title;
                var kind = Kind == CaptureKind.Recording ? "Recording" : "Screenshot";
                return $"{kind}, {stamp}";
            }
        }

        public Brush? Thumbnail
        {
            get => _thumbnail;
            set
            {
                _thumbnail = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Thumbnail)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private void InitialiseCaptures()
    {
        CaptureList.Tag = 170.0;
        CapturesScroller.SizeChanged += (_, _) => UpdateCaptureTileWidth();
        CapturesToolbar.SizeChanged += (_, _) => UpdateCapturesToolbarLayout();
        // A gallery opened from the toolbar, the palette or the tray starts from everything,
        // so a search left behind does not hide captures from a fresh look.
        CapturesPanel.IsVisibleChanged += (_, _) =>
        {
            if (!CapturesPanel.IsVisible && CapturesSearchBox.Text.Length > 0) CapturesSearchBox.Text = string.Empty;
        };
    }

    // ------------------------------------------------------------------ opening

    private void OnCapturesToggled(object sender, RoutedEventArgs e)
    {
        if (CapturesButton.IsChecked == true)
        {
            if (_isMiniPlayer) ExitMiniPlayer();
            SettingsButton.IsChecked = false;
            CloseHelp();
            CapturesPanel.Visibility = Visibility.Visible;
            FadeContentIn(CapturesPanel);
            UpdateCaptureTileWidth();
            RefreshCaptures();
        }
        else
        {
            CloseViewer();
            CapturesPanel.Visibility = Visibility.Collapsed;
            // Let the thumbnails go: they are only worth their memory while they can be seen.
            _captureGeneration++;
            CaptureList.ItemsSource = null;
            _allCaptures = [];
        }
    }

    private void ShowCaptures()
    {
        if (CapturesButton.IsChecked == true) RefreshCaptures();
        else CapturesButton.IsChecked = true;
    }

    private void OnCloseCaptures(object sender, RoutedEventArgs e) => CapturesButton.IsChecked = false;

    private void OnMenuCaptures(object sender, RoutedEventArgs e) => ShowCaptures();

    private void OnRefreshCaptures(object sender, RoutedEventArgs e) => RefreshCaptures();

    private void OnOcrSearchChanged(object sender, RoutedEventArgs e)
    {
        _settings.EnableOcrSearch = OcrSearchCheck.IsChecked == true;
        _settings.Save();

        if (!_settings.EnableOcrSearch)
        {
            // Turned off: cached text stops being searchable immediately, without waiting
            // for a refresh, and the in-memory engine handle is let go.
            foreach (var item in _allCaptures) item.OcrText = null;
            ApplyCaptureFilter();
            return;
        }

        if (!ScreenshotOcr.IsAvailable)
        {
            ShowToast("No OCR language is installed for Windows to read text with", "");
            return;
        }

        RefreshCapturesIfOpen();
    }

    private void OnCaptureFilterChanged(object sender, RoutedEventArgs e)
    {
        if (CaptureList is null) return;
        ApplyCaptureFilter();
    }

    private void OnCapturesSearchChanged(object sender, TextChangedEventArgs e)
    {
        if (CaptureList is null) return; // still building the window
        ApplyCaptureFilter();
    }

    private void OnCaptureSortChanged(object sender, RoutedEventArgs e)
    {
        if (CaptureList is null) return;
        ApplyCaptureFilter();
    }

    /// <summary>The order the sort control is showing.</summary>
    private CaptureSortOrder CaptureSort =>
        CapturesSortOldest.IsChecked == true ? CaptureSortOrder.OldestFirst
        : CapturesSortLargest.IsChecked == true ? CaptureSortOrder.LargestFirst
        : CaptureSortOrder.NewestFirst;

    /// <summary>Refreshes the gallery if it is open; called whenever a capture is saved.</summary>
    private void RefreshCapturesIfOpen()
    {
        if (CapturesPanel.Visibility == Visibility.Visible) RefreshCaptures();
    }

    // ------------------------------------------------------------------ listing

    private async void RefreshCaptures()
    {
        // Snapshot the timestamp mode at the moment of refresh - FormatCaptureTime reads
        // these statics when it builds each item's title, so the next render after a toggle
        // change uses the new mode.
        SetTimestampMode(_settings.Timestamps.UseShamsi, _settings.Timestamps.ShowGregorianAlongside);

        var generation = ++_captureGeneration;
        var directory = _settings.CaptureDirectory;

        List<CaptureItem> items;
        try
        {
            items = await Task.Run(() => ListCaptures(directory));
        }
        catch (Exception ex)
        {
            _log.Warn($"could not list the captures in {directory}", ex);
            items = [];
        }

        if (generation != _captureGeneration || _shuttingDown || CapturesPanel.Visibility != Visibility.Visible) return;

        _allCaptures = items;
        ApplyCaptureFilter();
        await LoadThumbnailsAsync(items, generation);
        await IndexOcrTextAsync(items, generation);
    }

    private static List<CaptureItem> ListCaptures(string directory)
    {
        if (!Directory.Exists(directory)) return [];

        var found = new List<CaptureItem>();
        foreach (var path in Directory.EnumerateFiles(directory, CaptureNaming.Prefix + "*"))
        {
            if (CaptureNaming.KindOf(path) is not { } kind) continue;
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists) continue;
                found.Add(new CaptureItem { Path = path, Kind = kind, ModifiedLocal = info.LastWriteTime, Size = info.Length });
            }
            catch (IOException) { /* gone, or locked, between listing and reading */ }
            catch (UnauthorizedAccessException) { }
        }

        return found.OrderByDescending(item => item.ModifiedLocal).ToList();
    }

    private void ApplyCaptureFilter()
    {
        // A recording still being written is not a capture yet: it would open as a broken file.
        var inProgress = _pipeline?.RecordingPath;
        var complete = _allCaptures
            .Where(item => !string.Equals(item.Path, inProgress, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var screenshots = complete.Count(item => item.Kind == CaptureKind.Screenshot);
        var recordings = complete.Count - screenshots;

        var shown = CapturesShots.IsChecked == true ? complete.Where(item => item.Kind == CaptureKind.Screenshot)
            : CapturesVideos.IsChecked == true ? complete.Where(item => item.Kind == CaptureKind.Recording)
            : complete;

        var query = CapturesSearchBox.Text;
        var matching = string.IsNullOrWhiteSpace(query) ? shown.ToList()
            : shown.Where(item => CaptureFilter.Matches(item.FileName, query) || OcrIndex.Matches(item.OcrText, query)).ToList();
        CaptureFilter.Sort(matching, item => item.ModifiedLocal, item => item.Size, CaptureSort);
        var list = matching.Take(MaxCapturesShown).ToList();

        var recordingTile = (Brush)FindResource("RecordingTile");
        foreach (var item in list)
            if (item.Kind == CaptureKind.Recording) item.Thumbnail ??= recordingTile;

        CaptureList.ItemsSource = list;

        // What the folder holds, what the search found, and where it all is.
        var totalBytes = complete.Sum(item => item.Size);
        var foundText = query.Length == 0
            ? null
            : $" · {Plural(list.Count, "match")} for “{query.Trim()}”";
        CapturesSummary.Text = complete.Count == 0
            ? _settings.CaptureDirectory
            : $"{Plural(screenshots, "screenshot")} · {Plural(recordings, "recording")} · {CaptureFilter.DescribeCount(complete.Count, totalBytes)}{foundText} · {_settings.CaptureDirectory}";
        CapturesSummary.ToolTip = _settings.CaptureDirectory;

        CapturesEmpty.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        CapturesEmptyTitle.Text = query.Length > 0 && complete.Count > 0 ? "Nothing matches that search"
            : CapturesShots.IsChecked == true ? "No screenshots yet"
            : CapturesVideos.IsChecked == true ? "No recordings yet"
            : "No captures yet";
        CapturesEmptyDetail.Text = query.Length > 0 && complete.Count > 0
            ? "Try fewer words, or clear the search to see everything."
            : "Screenshots (Ctrl+S) and recordings (Ctrl+R) of a mirrored phone are kept here.";
    }

    private static string Plural(int count, string noun) => $"{count} {noun}{(count == 1 ? "" : "s")}";

    /// <summary>Decodes the screenshots' thumbnails one after another, newest first, stopping
    /// as soon as the list they belong to is replaced or the panel closes.</summary>
    private async Task LoadThumbnailsAsync(IReadOnlyList<CaptureItem> items, int generation)
    {
        foreach (var item in items.Take(MaxCapturesShown))
        {
            if (generation != _captureGeneration || _shuttingDown) return;
            if (item.Kind != CaptureKind.Screenshot || item.Thumbnail is not null) continue;

            var thumbnail = await Task.Run(() => DecodeThumbnail(item.Path));
            if (generation != _captureGeneration) return;
            if (thumbnail is not null) item.Thumbnail = thumbnail;
        }
    }

    /// <summary>
    /// OCRs any screenshot in <paramref name="items"/> whose cached text is missing or
    /// stale, so the search box's next keystroke can find it - not just this refresh's, if
    /// the pass takes long enough that the generation moves on first.
    /// <para>
    /// Off unless the setting is on, and silently gives up the whole pass (not just one
    /// file) the first time Windows reports no OCR language installed, so a search box
    /// that will never find anything does not retry every screenshot on every refresh.
    /// </para>
    /// </summary>
    private async Task IndexOcrTextAsync(IReadOnlyList<CaptureItem> items, int generation)
    {
        if (!_settings.EnableOcrSearch) return;

        var cache = _ocrCache ??= OcrCache.Load();
        var screenshots = items.Where(item => item.Kind == CaptureKind.Screenshot).ToList();

        // Anything already cached for its current on-disk state is filled in immediately,
        // with no OCR pass at all - this covers every refresh after the first.
        var pending = new List<CaptureItem>();
        foreach (var item in screenshots)
        {
            if (cache.Entries.TryGetValue(item.Path, out var entry) && OcrIndex.IsFresh(entry, item.ModifiedLocal))
                item.OcrText = entry.Text;
            else
                pending.Add(item);
        }
        if (generation != _captureGeneration) return;
        if (pending.Count > 0) ApplyCaptureFilter();
        if (pending.Count == 0) return;

        var indexed = 0;
        foreach (var item in pending.Take(MaxOcrPerPass))
        {
            if (generation != _captureGeneration || _shuttingDown) return;

            string text;
            try
            {
                text = await Task.Run(() => ScreenshotOcr.RecognizeAsync(item.Path));
            }
            catch (InvalidOperationException)
            {
                // No OCR language installed: nothing later in this pass will succeed either.
                return;
            }
            catch (Exception ex)
            {
                // Moved, deleted, or not really an image: skip it, do not retry it forever -
                // caching "" is what stops it being retried every single refresh.
                _log.Warn($"could not read text from {item.Path}", ex);
                text = "";
            }

            if (generation != _captureGeneration) return;

            item.OcrText = text;
            cache.Set(item.Path, item.ModifiedLocal, text);
            indexed++;
        }

        if (indexed > 0)
        {
            cache.PruneAndSave(screenshots.Select(item => item.Path).ToHashSet(StringComparer.OrdinalIgnoreCase));
            if (generation == _captureGeneration) ApplyCaptureFilter();
        }
    }

    private static Brush? DecodeThumbnail(string path)
    {
        try
        {
            // Read through a stream the file is not held by: a capture can then still be
            // deleted, or replaced, while its thumbnail is on screen.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            var portrait = decoder.Frames[0].PixelHeight >= decoder.Frames[0].PixelWidth;
            stream.Position = 0;

            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            // Decoded at about the size a tile shows it, on whichever side fills the tile.
            if (portrait) image.DecodePixelWidth = 240;
            else image.DecodePixelHeight = 180;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();

            var brush = new ImageBrush(image) { Stretch = Stretch.UniformToFill };
            brush.Freeze();
            return brush;
        }
        catch (Exception)
        {
            // Not an image after all, still being written, or gone: the tile keeps its blank face.
            return null;
        }
    }

    /// <summary>
    /// Keeps the four parts of the captures toolbar on one line while the window is wide
    /// enough, and moves the search + sort pair onto its own row when it is not. Moving the
    /// element between hosts rather than swapping templates, so its state - the text being
    /// typed, the sort chosen - rides along untouched. The move itself is faded and eased
    /// so a resize reads as the toolbar folding, not controls teleporting.
    /// </summary>
    private void UpdateCapturesToolbarLayout()
    {
        if (CapturesToolbar.ActualWidth <= 0) return;

        // How wide one line needs to be: the filter segments and the two icon buttons take
        // what they take; the search + sort pair keeps a sensible floor even when squeezed.
        const double searchAndSortFloor = 350;
        const double iconButtons = 76;
        var wanted = CapturesAll.ActualWidth + CapturesShots.ActualWidth + CapturesVideos.ActualWidth
                     + searchAndSortFloor + iconButtons + 40; // margins and breathing room
        var wrapped = CapturesToolsHost.Parent == CapturesToolsWrap;
        var shouldWrap = CapturesToolbar.ActualWidth < wanted;

        if (shouldWrap == wrapped) return;

        if (shouldWrap)
        {
            CapturesToolsWrap.Children.Add(CapturesToolsHost);
            CapturesToolsWrap.Visibility = Visibility.Visible;
            CapturesToolsHost.Margin = new Thickness(0, 0, 0, 0);
            CapturesToolsHost.HorizontalAlignment = HorizontalAlignment.Left;
        }
        else
        {
            CapturesToolsHost.Margin = new Thickness(10, 0, 10, 0);
            CapturesToolsHost.HorizontalAlignment = HorizontalAlignment.Center;
            CapturesToolbar.Children.Add(CapturesToolsHost);
            Grid.SetRow(CapturesToolsHost, 0);
            Grid.SetColumn(CapturesToolsHost, 1);
            CapturesToolsWrap.Visibility = Visibility.Collapsed;
        }

        AnimateCapturesToolsMove();
    }

    /// <summary>The short slide-and-fade that plays after the search + sort pair has moved
    /// rows: eight pixels from the direction it came, over 180 ms. Skipped when the user
    /// has asked Windows to keep animation to a minimum.</summary>
    private void AnimateCapturesToolsMove()
    {
        if (!Motion.Enabled)
        {
            CapturesToolsHost.BeginAnimation(OpacityProperty, null);
            CapturesToolsHost.Opacity = 1;
            CapturesToolsSlide.BeginAnimation(TranslateTransform.YProperty, null);
            CapturesToolsSlide.Y = 0;
            return;
        }

        var slidingDown = CapturesToolsHost.Parent == CapturesToolsWrap;
        CapturesToolsSlide.BeginAnimation(TranslateTransform.YProperty, null);
        CapturesToolsSlide.Y = slidingDown ? -8 : 8;
        CapturesToolsSlide.BeginAnimation(TranslateTransform.YProperty,
            new System.Windows.Media.Animation.DoubleAnimation(0, TimeSpan.FromMilliseconds(180))
            {
                EasingFunction = new System.Windows.Media.Animation.CubicEase
                {
                    EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut,
                },
            });

        CapturesToolsHost.BeginAnimation(OpacityProperty, null);
        CapturesToolsHost.Opacity = 0.35;
        CapturesToolsHost.BeginAnimation(OpacityProperty,
            new System.Windows.Media.Animation.DoubleAnimation(1, TimeSpan.FromMilliseconds(180))
            {
                EasingFunction = new System.Windows.Media.Animation.CubicEase
                {
                    EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut,
                },
            });
    }

    private void UpdateCaptureTileWidth()
    {
        // The scroller's own width less a fixed allowance for its scroll bar, rather than the
        // viewport: the viewport narrows when the bar appears, which could change the column
        // count, which could take the bar away again.
        var available = CapturesScroller.ActualWidth - CaptureList.Margin.Left - CaptureList.Margin.Right - 12;
        if (available <= 0) return;

        var columns = Math.Max(1, (int)(available / MinCaptureTileWidth));
        var width = Math.Floor(available / columns);
        if (CaptureList.Tag is not double current || Math.Abs(current - width) > 0.5) CaptureList.Tag = width;
    }

    internal static string FormatCaptureTime(DateTime local)
    {
        // Capture the resolved mode at the moment we format - the captures panel re-renders
        // when the user changes the toggle, so the next render picks up the new mode.
        var mode = TimestampFormatting.Resolve(
            _currentUseShamsi, _currentShowGregorian, CultureInfo.CurrentCulture);
        var time = TimestampFormatting.FormatTime(local);
        if (mode == TimestampMode.Gregorian)
        {
            if (local.Date == DateTime.Today) return $"Today, {time}";
            if (local.Date == DateTime.Today.AddDays(-1)) return $"Yesterday, {time}";
            return local.ToString("d MMM yyyy", CultureInfo.CurrentCulture);
        }
        // Shamsi: same "Today/Yesterday" idea, but spelled in Persian with the time underneath.
        var shamsi = TimestampFormatting.FormatDate(local, mode);
        if (local.Date == DateTime.Today) return $"Today, {shamsi} {time}";
        if (local.Date == DateTime.Today.AddDays(-1)) return $"Yesterday, {shamsi} {time}";
        return $"{shamsi} {time}";
    }

    // The settings reference at the moment of formatting - set on every RefreshCaptures.
    // Stays null when nothing has set it; the formatter falls back to locale-based default.
    private static bool? _currentUseShamsi;
    private static bool _currentShowGregorian;

    internal static void SetTimestampMode(bool? useShamsi, bool showGregorian)
    {
        _currentUseShamsi = useShamsi;
        _currentShowGregorian = showGregorian;
    }

    // ------------------------------------------------------------------ actions

    private static CaptureItem? CaptureFrom(object sender) => (sender as FrameworkElement)?.DataContext as CaptureItem;

    private void OnCaptureTileClick(object sender, RoutedEventArgs e)
    {
        if (CaptureFrom(sender) is { } item) OpenViewer(item);
    }

    private void OnCaptureOpen(object sender, RoutedEventArgs e)
    {
        if (CaptureFrom(sender) is { } item) OpenViewer(item);
    }

    private void OnCaptureOpenExternal(object sender, RoutedEventArgs e)
    {
        if (CaptureFrom(sender) is { } item) OpenCapture(item);
    }

    /// <summary>Where a press on a tile began, so moving far enough from it drags the file out.</summary>
    private Point? _tileDragStart;

    private void OnCaptureTilePointerDown(object sender, MouseButtonEventArgs e) => _tileDragStart = e.GetPosition(this);

    /// <summary>A tile dragged out of the gallery carries its file, into Explorer, a chat or a
    /// document, as a file from Explorer itself would.</summary>
    private void OnCaptureTilePointerMove(object sender, MouseEventArgs e)
    {
        if (_tileDragStart is not { } start) return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            _tileDragStart = null;
            return;
        }

        var now = e.GetPosition(this);
        if (Math.Abs(now.X - start.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(now.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        _tileDragStart = null;
        if (CaptureFrom(sender) is not { } item || !File.Exists(item.Path)) return;

        try
        {
            var data = new DataObject(DataFormats.FileDrop, new[] { item.Path });
            DragDrop.DoDragDrop((DependencyObject)sender, data, DragDropEffects.Copy);
        }
        catch (Exception ex)
        {
            _log.Warn($"could not drag {item.Path}", ex);
        }
        e.Handled = true;
    }

    private void OnCaptureReveal(object sender, RoutedEventArgs e)
    {
        if (CaptureFrom(sender) is { } item && CaptureStillExists(item)) RevealFile(item.Path);
    }

    private void OnCaptureCopy(object sender, RoutedEventArgs e)
    {
        if (CaptureFrom(sender) is { } item) CopyCapture(item);
    }

    private void OnCaptureDelete(object sender, RoutedEventArgs e)
    {
        if (CaptureFrom(sender) is { } item) DeleteCapture(item);
    }

    private void OnCaptureTileKeyDown(object sender, KeyEventArgs e)
    {
        if (CaptureFrom(sender) is not { } item) return;

        if (e.Key == Key.Delete)
        {
            DeleteCapture(item);
            e.Handled = true;
        }
        else if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control)
        {
            CopyCapture(item);
            e.Handled = true;
        }
    }

    /// <summary>True if the file is still there; otherwise says so and refreshes the list.</summary>
    private bool CaptureStillExists(CaptureItem item)
    {
        if (File.Exists(item.Path)) return true;
        ShowToast("That file has been moved or deleted", "");
        RefreshCaptures();
        return false;
    }

    private void OpenCapture(CaptureItem item)
    {
        if (!CaptureStillExists(item)) return;
        try
        {
            Process.Start(new ProcessStartInfo(item.Path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _log.Warn($"could not open {item.Path}", ex);
            ShowToast("No app on this PC opens that file", "");
        }
    }

    private void CopyCapture(CaptureItem item)
    {
        if (!CaptureStillExists(item)) return;
        try
        {
            // The file itself, so it pastes into Explorer or a chat as an attachment, and for a
            // screenshot the picture as well, so it pastes into an image editor or a document.
            var data = new DataObject();
            data.SetFileDropList(new StringCollection { item.Path });
            if (item.Kind == CaptureKind.Screenshot)
            {
                using var stream = new FileStream(item.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.StreamSource = stream;
                image.EndInit();
                image.Freeze();
                data.SetImage(image);
            }

            Clipboard.SetDataObject(data, copy: true);
            ShowToast(item.Kind == CaptureKind.Screenshot ? "Screenshot copied" : "Recording copied", "");
        }
        catch (Exception ex)
        {
            _log.Warn($"could not copy {item.Path}", ex);
            ShowToast("The clipboard is busy; try again", "");
        }
    }

    /// <returns>True if the file went to the Recycle Bin.</returns>
    private bool DeleteCapture(CaptureItem item)
    {
        if (!CaptureStillExists(item)) return false;
        try
        {
            // The Recycle Bin rather than a delete: one press of a key should never be the end
            // of a recording.
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(item.Path,
                Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            _log.Warn($"could not delete {item.Path}", ex);
            ShowToast("That file could not be deleted - is it open elsewhere?", "");
            return false;
        }

        _allCaptures.Remove(item);
        ApplyCaptureFilter();
        ShowToast("Moved to the Recycle Bin", "");
        return true;
    }

    // ------------------------------------------------------------------ budget

    /// <summary>Runs after each capture is saved: if the folder has grown past the budget,
    /// the oldest captures go to the Recycle Bin to bring it back under.</summary>
    private void EnforceCaptureBudget()
    {
        var budget = _settings.CaptureBudgetBytes;
        if (budget <= 0) return;

        try
        {
            var candidates = new List<CaptureBudget.Candidate>();
            foreach (var path in Directory.EnumerateFiles(_settings.CaptureDirectory, CaptureNaming.Prefix + "*"))
            {
                if (CaptureNaming.KindOf(path) is not { }) continue;
                try
                {
                    var info = new FileInfo(path);
                    if (!info.Exists) continue;
                    candidates.Add(new CaptureBudget.Candidate(path, info.Length, info.LastWriteTimeUtc,
                        IsBeingRecorded: string.Equals(path, _pipeline?.RecordingPath, StringComparison.OrdinalIgnoreCase)));
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }

            var plan = CaptureBudget.PlanRemoval(candidates, budget);
            if (plan.Remove.Count == 0) return;

            var removed = 0;
            foreach (var path in plan.Remove)
            {
                try
                {
                    Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path,
                        Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                        Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                    removed++;
                }
                catch (Exception ex)
                {
                    // One unremovable file - open elsewhere, or a permission problem - must
                    // not stop the rest of the plan from running.
                    _log.Warn($"could not prune {path}", ex);
                }
            }

            if (removed == 0) return;
            _log.Info($"capture budget: moved {removed} capture{(removed == 1 ? "" : "s")} to the Recycle Bin");
            ShowToast($"Kept the capture folder under {CaptureNaming.FormatSize(budget)} - {CaptureBudget.DescribeFreed(plan.FreedBytes)}", "");
            RefreshCapturesIfOpen();
        }
        catch (Exception ex)
        {
            _log.Warn("could not run the capture budget", ex);
        }
    }
}
