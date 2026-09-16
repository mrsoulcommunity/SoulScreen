using System.Collections.Specialized;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SoulScreen.App.Logic;
using SoulScreen.Core.Time;
using SoulScreen.Media;

namespace SoulScreen.App;

/// <summary>
/// Making a clip: a moment of a recording saved as an animated GIF or a short WebM, so
/// something that happened on a mirrored phone can be sent to somebody without a video
/// editor, a converter or a website in between.
/// <para>
/// The panel is opened from the viewer, and the moment is wherever the player is when it is
/// opened - so the player stops there, and the picture cannot drift while the length and
/// format are chosen. The clip is encoded off the UI thread and saved into the capture
/// folder, where it joins the gallery as any other capture does.
/// </para>
/// </summary>
public partial class MainWindow
{
    /// <summary>The export in flight, or null. Its token is what stops it.</summary>
    private CancellationTokenSource? _clipExport;

    /// <summary>The clip length the panel opens with, before the in/out points are touched.</summary>
    private static readonly TimeSpan DefaultClipLength = TimeSpan.FromSeconds(5);

    /// <summary>Set while an in/out slider is pushing the other one out of its way, so that
    /// cross-clamp does not recurse into itself.</summary>
    private bool _updatingClipRange;

    /// <summary>Whether this FFmpeg build can write the chosen format at all - independent of
    /// whether the in/out selection is itself valid, since the two disable the Save button
    /// for different reasons and each deserves its own words.</summary>
    private bool _clipFormatAvailable;

    private bool IsClipPanelOpen => ViewerClipPanel.Visibility == Visibility.Visible;

    // --------------------------------------------------------------- the panel

    private void OnViewerClip(object sender, RoutedEventArgs e) => OpenClipPanel();

    private void OpenClipPanel()
    {
        if (CurrentViewerItem() is not { Kind: CaptureKind.Recording }) return;

        // The moment is the player's position, so it must not move while it is being chosen:
        // the clip is made from exactly the picture that is on screen.
        if (_viewerPlaying) ToggleViewerPlayback();

        ViewerClipError.Visibility = Visibility.Collapsed;
        ViewerClipProgress.Visibility = Visibility.Collapsed;
        ViewerClipPanel.Visibility = Visibility.Visible;

        // A build without a GIF or a WebM encoder still offers the other one, and whatever
        // was chosen last stays chosen unless this build cannot write it.
        var gif = ClipExporter.Supports(ClipFormat.Gif);
        var webM = ClipExporter.Supports(ClipFormat.WebM);
        var wanted = ViewerClipWebM.IsChecked == true ? ClipFormat.WebM : ClipFormat.Gif;
        if (wanted == ClipFormat.Gif && !gif) wanted = ClipFormat.WebM;
        if (wanted == ClipFormat.WebM && !webM) wanted = ClipFormat.Gif;
        ViewerClipGif.IsEnabled = gif;
        ViewerClipWebM.IsEnabled = webM;
        ViewerClipGif.IsChecked = wanted == ClipFormat.Gif;
        ViewerClipWebM.IsChecked = wanted == ClipFormat.WebM;

        _clipFormatAvailable = gif || webM;
        if (!_clipFormatAvailable) ShowClipError("This PC's FFmpeg build cannot write an animation. See the activity log.");

        // The bounds a clip can be cut from: the whole recording, when its length is known
        // yet - an unknown one still lets the in/out points move, just without an upper limit.
        var totalDuration = ClipTotalDuration();
        var maxSeconds = totalDuration?.TotalSeconds ?? Math.Max(ViewerMedia.Position.TotalSeconds + ClipExporter.MaximumLength.TotalSeconds, 30);
        ViewerClipIn.Maximum = maxSeconds;
        ViewerClipOut.Maximum = maxSeconds;

        var (start, end) = ClipRange.DefaultRange(ViewerMedia.Position, totalDuration, DefaultClipLength);
        SetClipRange(start, end);

        UpdateViewerClipSummary();
        Dispatcher.BeginInvoke(() =>
        {
            if (IsClipPanelOpen) ViewerClipSave.Focus();
        }, DispatcherPriority.Input);
    }

    /// <summary>
    /// Closes the panel. An export already running is left to finish: it is writing a file
    /// that was asked for, and closing the panel is not a reason to lose it.
    /// </summary>
    private void CloseClipPanel()
    {
        if (!IsClipPanelOpen) return;
        ViewerClipPanel.Visibility = Visibility.Collapsed;
        ViewerClipError.Visibility = Visibility.Collapsed;
        if (IsViewerOpen) ViewerPanel.Focus();
    }

    private void OnViewerClipCancel(object sender, RoutedEventArgs e)
    {
        if (_clipExport is { } export)
        {
            export.Cancel();
            return;
        }
        CloseClipPanel();
    }

    /// <summary>Format changing changes what the panel says about the moment.</summary>
    private void OnViewerClipChanged(object sender, RoutedEventArgs e)
    {
        // Runs while the XAML is being read too, before the rest of the panel exists.
        if (ViewerClipLengthText is null) return;
        if (IsClipPanelOpen) UpdateViewerClipSummary();
    }

    /// <summary>
    /// One in/out slider moved. Each is kept from crossing the other - dragging "in" past
    /// "out" pushes "out" along with it, rather than letting the range invert - so the pair
    /// can never describe a clip that runs backwards while it is only half-adjusted.
    /// </summary>
    private void OnViewerClipRangeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingClipRange || ViewerClipLengthText is null) return;
        _updatingClipRange = true;
        try
        {
            const double minGap = 0.1;
            if (ReferenceEquals(sender, ViewerClipIn) && ViewerClipIn.Value > ViewerClipOut.Value - minGap)
                ViewerClipOut.Value = Math.Min(ViewerClipIn.Value + minGap, ViewerClipOut.Maximum);
            else if (ReferenceEquals(sender, ViewerClipOut) && ViewerClipOut.Value < ViewerClipIn.Value + minGap)
                ViewerClipIn.Value = Math.Max(ViewerClipOut.Value - minGap, 0);
        }
        finally
        {
            _updatingClipRange = false;
        }

        if (IsClipPanelOpen) UpdateViewerClipSummary();
    }

    private void SetClipRange(TimeSpan start, TimeSpan end)
    {
        _updatingClipRange = true;
        try
        {
            ViewerClipIn.Value = Math.Max(start.TotalSeconds, 0);
            ViewerClipOut.Value = Math.Max(end.TotalSeconds, ViewerClipIn.Value + 0.1);
        }
        finally
        {
            _updatingClipRange = false;
        }
    }

    private void UpdateViewerClipSummary()
    {
        ViewerClipInText.Text = FormatClock(ClipStart());
        ViewerClipOutText.Text = FormatClock(ClipEnd());

        var range = CurrentClipRange();
        if (!range.IsValid)
        {
            ViewerClipLengthText.Text = range.Error;
            ViewerClipSave.IsEnabled = false;
            return;
        }

        ViewerClipLengthText.Text = $"{range.Duration.TotalSeconds:0.#} s · {FormatClock(range.Start)} to {FormatClock(range.Start + range.Duration)}";
        ViewerClipSave.IsEnabled = _clipFormatAvailable;
    }

    private TimeSpan ClipStart() => TimeSpan.FromSeconds(Math.Max(ViewerClipIn.Value, 0));

    private TimeSpan ClipEnd() => TimeSpan.FromSeconds(Math.Max(ViewerClipOut.Value, 0));

    /// <summary>The recording's own length, once the player has reported it - null before
    /// then, which is when the in/out bounds are widest rather than wrong.</summary>
    private TimeSpan? ClipTotalDuration() =>
        ViewerMedia.NaturalDuration.HasTimeSpan ? ViewerMedia.NaturalDuration.TimeSpan : null;

    /// <summary>Checks the in/out selection against the clip it is cut from, in one place
    /// the panel's summary and the export both read from - so what the summary says and what
    /// Save is willing to do can never disagree.</summary>
    private ClipRangeResult CurrentClipRange() =>
        ClipRange.Validate(ClipStart(), ClipEnd(), ClipTotalDuration(), ClipExporter.MinimumLength, ClipExporter.MaximumLength);

    // -------------------------------------------------------------- making one

    private void OnViewerClipSave(object sender, RoutedEventArgs e) => _clipExportTask = SaveViewerClipAsync();

    /// <summary>The export task in flight, so shutdown can wait for it to actually stop
    /// rather than just requesting cancellation and hoping.</summary>
    private Task? _clipExportTask;

    private async Task SaveViewerClipAsync()
    {
        if (_clipExport is not null) return;
        if (CurrentViewerItem() is not { Kind: CaptureKind.Recording } item)
        {
            CloseClipPanel();
            return;
        }

        var format = ViewerClipWebM.IsChecked == true ? ClipFormat.WebM : ClipFormat.Gif;
        if (!ClipExporter.Supports(format))
        {
            ShowClipError($"This PC's FFmpeg build cannot write {ClipExporter.Describe(format)} clips.");
            return;
        }

        var range = CurrentClipRange();
        if (!range.IsValid)
        {
            ShowClipError(range.Error ?? "That in/out selection is not valid.");
            return;
        }

        string path;
        try
        {
            Directory.CreateDirectory(_settings.CaptureDirectory);
            path = CaptureTimestampFormatter.NewPath(
                _settings.CaptureDirectory, DateTime.Now, ClipExporter.ExtensionOf(format),
                _settings.Timestamps, CultureInfo.CurrentCulture,
                deviceName: _sessionDevice?.Name ?? "");
        }
        catch (Exception ex)
        {
            _log.Error("could not name a clip", ex);
            ShowClipError("The clip could not be saved there. Is the capture folder still there?");
            return;
        }

        var request = new ClipRequest(item.Path, path, range.Start, range.Duration, format);
        var cancellation = new CancellationTokenSource();
        _clipExport = cancellation;
        SetClipBusy(true);

        // Progress arrives on the UI thread: the panel is the only thing that reports it.
        var progress = new Progress<double>(fraction =>
            ViewerClipProgress.Text = $"Making the clip… {(int)Math.Round(Math.Clamp(fraction, 0, 1) * 100)}%");

        try
        {
            var result = await Task.Run(() => ClipExporter.Export(request, progress, cancellation.Token));
            SetClipBusy(false);
            CloseClipPanel();
            OnClipSaved(result);
        }
        catch (OperationCanceledException)
        {
            SetClipBusy(false);
            DiscardClipFile(path);
            CloseClipPanel();
            ShowToast("The clip was cancelled", "\uE711");
        }
        catch (Exception ex)
        {
            SetClipBusy(false);
            DiscardClipFile(path);
            _log.Error($"could not make a clip of {item.Path}", ex);
            ShowClipError(DescribeClipFailure(ex));
        }
        finally
        {
            cancellation.Dispose();
            _clipExport = null;
            _clipExportTask = null;
        }
    }

    private void SetClipBusy(bool busy)
    {
        ViewerClipSave.IsEnabled = !busy && CurrentClipRange().IsValid && _clipFormatAvailable;
        ViewerClipIn.IsEnabled = !busy;
        ViewerClipOut.IsEnabled = !busy;
        ViewerClipCopy.IsEnabled = !busy;
        ViewerClipGif.IsEnabled = !busy && ClipExporter.Supports(ClipFormat.Gif);
        ViewerClipWebM.IsEnabled = !busy && ClipExporter.Supports(ClipFormat.WebM);
        // The one button reads as what it will do next: stop the clip being made, or leave.
        ViewerClipCancel.Content = busy ? "Stop" : "Cancel";
        ViewerClipProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        if (busy) ViewerClipProgress.Text = "Making the clip…";
    }

    private void ShowClipError(string message)
    {
        ViewerClipError.Text = message;
        ViewerClipError.Visibility = Visibility.Visible;
    }

    private static string DescribeClipFailure(Exception failure) => failure switch
    {
        FFmpegUnavailableException => "This PC's FFmpeg build could not write that clip. See the activity log.",
        IOException => "The clip could not be written. Is there room on the drive?",
        _ => "The clip could not be made. See the activity log for what went wrong.",
    };

    /// <summary>Removes a half-written clip: without its index it would never open anyway.</summary>
    private static void DiscardClipFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception)
        {
            // A partial file left behind is clutter, not something to report a second failure about.
        }
    }

    /// <summary>
    /// The clip joins the gallery like any other capture. A copy on the clipboard is usually
    /// what is wanted next, so it is pasted straight into a chat rather than found first.
    /// </summary>
    private void OnClipSaved(ClipExportResult result)
    {
        var name = Path.GetFileName(result.Path);
        _log.Info($"saved a clip of {name}");
        RefreshCapturesIfOpen();

        var copied = ViewerClipCopy.IsChecked == true && TryCopyClipToClipboard(result);
        var size = CaptureNaming.FormatSize(result.Bytes);
        ShowToast(copied ? $"Clip saved and copied · {size}" : $"Clip saved · {size}", "\uE8C6", "View",
            () =>
            {
                CloseViewer();
                ShowCaptures();
            });
    }

    private bool TryCopyClipToClipboard(ClipExportResult result)
    {
        try
        {
            var data = new DataObject();
            data.SetFileDropList(new StringCollection { result.Path });
            // A GIF goes over as a picture as well, which is what a chat or a document takes
            // when it is pasted rather than attached.
            if (result.Format == ClipFormat.Gif)
            {
                using var stream = new FileStream(result.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.StreamSource = stream;
                image.EndInit();
                image.Freeze();
                data.SetImage(image);
            }

            Clipboard.SetDataObject(data, copy: true);
            return true;
        }
        catch (Exception ex)
        {
            // The file itself is safe in the capture folder; only the convenience copy failed.
            _log.Warn("could not copy the clip to the clipboard", ex);
            return false;
        }
    }

    // ------------------------------------------------------------------ the keys

    /// <summary>
    /// The clip panel's keys. Everything is swallowed while it is up, so a keystroke meant
    /// for the panel cannot act on the capture behind it - Delete least of all. Tab is left
    /// alone, so the panel can still be walked with the keyboard.
    /// </summary>
    private bool HandleClipPanelKey(KeyEventArgs e)
    {
        if (e.Key == Key.Tab) return false;

        if (e.Key == Key.Escape)
        {
            OnViewerClipCancel(ViewerClipCancel, new RoutedEventArgs());
            return true;
        }

        return true;
    }

    /// <summary>True for a still picture that animates when it is opened elsewhere. The
    /// viewer decodes one frame of a GIF, which without saying so looks like a failed clip.</summary>
    internal static bool IsAnimatedImage(string path) =>
        Path.GetExtension(path).Equals(".gif", StringComparison.OrdinalIgnoreCase);
}
