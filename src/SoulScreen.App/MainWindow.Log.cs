using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using Microsoft.Win32;
using SoulScreen.App.Logic;
using SoulScreen.Core.Logging;
using SoulScreen.Core.Time;

namespace SoulScreen.App;

/// <summary>The activity panel: a live tail of the log with a level filter and a search box.</summary>
public partial class MainWindow
{
    /// <summary>Lines kept in the activity panel. Enough to cover a whole session handshake.</summary>
    private const int MaxLogLines = 600;

    private readonly Queue<(LogLevel Level, string Line)> _logLines = new();
    private readonly object _logLock = new();

    /// <summary>Set when new log lines have arrived but the panel has not been redrawn.</summary>
    private bool _logDirty;

    /// <summary>How wide the level filter wants to be before it has to fold, measured once -
    /// its labels never change, and re-measuring on every resize would fight the layout pass
    /// that asked for the figure.</summary>
    private double _logFilterWanted;

    private void InitialiseLog()
    {
        Log.Entry += OnLogEntry;
        LogPanel.SizeChanged += (_, _) => UpdateLogHeaderLayout();
        Loaded += (_, _) =>
        {
            var previous = LogFilterTrack.MaxWidth;
            LogFilterTrack.MaxWidth = double.PositiveInfinity;
            LogFilterTrack.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            _logFilterWanted = LogFilterTrack.DesiredSize.Width;
            LogFilterTrack.MaxWidth = previous;
            UpdateLogHeaderLayout();
        };
    }

    /// <summary>
    /// Fits the log's header to the panel it is docked in, so that nothing up there is ever
    /// drawn over anything else. The level filter and the search box together want more room
    /// than a narrow window has: the search box takes what is left once the filter has its
    /// width, and stands down when that would not show a few characters - a sliver of a text
    /// field is worse than none, since it takes the pointer from the button beside it and
    /// gives nothing back. The filter folds onto a second line inside its capsule when the
    /// row is narrower still, which keeps every level reachable at any width.
    /// </summary>
    private void UpdateLogHeaderLayout()
    {
        if (LogPanel is null || LogPanel.ActualWidth <= 0) return;

        // The header's own furniture: its margins, the "Activity" label, the middle group's
        // margin, and the four icon buttons at the end of the row.
        const double furniture = 22 + 48 + 18 + 136;
        const double searchFloor = 60;
        const double searchCap = 170;
        const double gap = 8;

        var filter = _logFilterWanted > 0 ? _logFilterWanted : 145;
        var room = LogPanel.ActualWidth - furniture;
        var spare = room - filter - gap;

        if (spare >= searchFloor)
        {
            LogSearchBox.Visibility = Visibility.Visible;
            LogSearchBox.MaxWidth = Math.Min(spare, searchCap);
            LogFilterTrack.MaxWidth = filter;
        }
        else
        {
            LogSearchBox.Visibility = Visibility.Collapsed;
            // What the search box would have used goes to the filter, which may still fold.
            LogFilterTrack.MaxWidth = Math.Max(96, room);
        }
    }

    private void OnLogToggled(object sender, RoutedEventArgs e)
    {
        if (LogButton.IsChecked == true && _isMiniPlayer) ExitMiniPlayer();

        LogPanel.Visibility = LogButton.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        // The control bar and toasts sit above the docked log; they move with it.
        PositionOverlays();
        if (LogButton.IsChecked != true) return;

        // Reopening the panel must show what arrived while it was hidden: the refresh
        // below skips unchanged text, and without this it would come back stale.
        lock (_logLock) _logDirty = true;
        RefreshLogPanel();
    }

    private void OnCloseLog(object sender, RoutedEventArgs e) => LogButton.IsChecked = false;

    private void OnLogFilterChanged(object sender, RoutedEventArgs e)
    {
        lock (_logLock) _logDirty = true;
        RefreshLogPanel();
    }

    private void OnCopyLog(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(LogText.Text);
            ShowToast("Log copied", "");
        }
        catch (Exception ex) { _log.Warn("could not copy the log to the clipboard", ex); }
    }

    private void OnSaveLog(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Save the activity log",
            FileName = $"soulscreen-log-{CaptureTimestampFormatter.BuildStem(DateTime.Now, useShamsi: false)}.txt",
            Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*",
            DefaultExt = ".txt",
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            File.WriteAllText(dialog.FileName, BuildLogText(applyFilter: false), Encoding.UTF8);
            ShowTransientStatus($"Saved {Path.GetFileName(dialog.FileName)}", dialog.FileName);
        }
        catch (Exception ex)
        {
            _log.Error("could not save the log", ex);
            ShowToast("The log could not be saved", "");
        }
    }

    private void OnClearLog(object sender, RoutedEventArgs e)
    {
        lock (_logLock)
        {
            _logLines.Clear();
            _logDirty = false;
        }
        LogText.Text = string.Empty;
    }

    /// <summary>
    /// Runs on whatever thread logged. Deliberately does no dispatcher work: with protocol
    /// tracing on this is called often, and posting a redraw per line put enough on the UI
    /// thread to be visible in the picture. The panel is redrawn on the metrics tick instead.
    /// </summary>
    private void OnLogEntry(LogEntry entry)
    {
        if (entry.Level < LogLevel.Debug) return;

        var line = entry.Exception is null
            ? entry.ToString()
            : $"{entry.TimestampUtc.ToLocalTime():HH:mm:ss.fff} {entry.Level.ToString().ToUpperInvariant(),-5} [{entry.Category}] {entry.Message}: {entry.Exception.Message}";

        lock (_logLock)
        {
            _logLines.Enqueue((entry.Level, line));
            while (_logLines.Count > MaxLogLines) _logLines.Dequeue();
            _logDirty = true;
        }
    }

    private LogLevel LogFilterLevel =>
        LogProblems?.IsChecked == true ? LogLevel.Warn
        : LogInfo?.IsChecked == true ? LogLevel.Info
        : LogLevel.Debug;

    private string BuildLogText(bool applyFilter)
    {
        var level = applyFilter ? LogFilterLevel : LogLevel.Trace;
        var search = applyFilter ? LogSearchBox?.Text.Trim() ?? string.Empty : string.Empty;

        lock (_logLock)
        {
            var builder = new StringBuilder(_logLines.Count * 80);
            foreach (var (entryLevel, line) in _logLines)
            {
                if (entryLevel < level) continue;
                if (search.Length > 0 && !line.Contains(search, StringComparison.OrdinalIgnoreCase)) continue;
                builder.AppendLine(line);
            }
            return builder.ToString();
        }
    }

    /// <summary>Redraws the activity panel at the metrics cadence, and only while visible.</summary>
    private void RefreshLogPanel()
    {
        if (LogPanel.Visibility != Visibility.Visible) return;

        lock (_logLock)
        {
            if (!_logDirty) return;
            _logDirty = false;
        }

        var text = BuildLogText(applyFilter: true);

        // Follow the tail only when the reader is already there. Scrolling up to study a
        // line should not be undone half a second later by the next batch of entries.
        var pinnedToTail = LogScroller.VerticalOffset + LogScroller.ViewportHeight
            >= LogScroller.ExtentHeight - 1;
        LogText.Text = text;
        if (pinnedToTail) LogScroller.ScrollToEnd();
    }
}
