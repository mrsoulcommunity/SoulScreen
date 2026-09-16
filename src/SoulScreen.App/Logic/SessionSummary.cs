using System.Globalization;
using SoulScreen.Core.Time;

namespace SoulScreen.App.Logic;

/// <summary>
/// How a session ended, which decides the summary's opening words.
/// </summary>
public enum SessionEndReason
{
    /// <summary>The phone closed the mirror, or walked out of range.</summary>
    PhoneEnded,
    /// <summary>Disconnect was pressed, or the receiver was stopped.</summary>
    StoppedByUser,
    /// <summary>Another iPhone took the receiver over.</summary>
    TakenOver,
    /// <summary>The receiver faulted; the summary is still worth showing.</summary>
    Faulted,
}

/// <summary>
/// What a finished mirroring session amounted to, for the summary toast.
/// <para>
/// Every figure is read at the moment the session ends, so the words can be built from a plain
/// snapshot rather than from live objects that are about to be torn down. Deliberately free of
/// WPF so the wording can be tested on its own.
/// </para>
/// </summary>
public sealed record SessionSummary(
    string DeviceName,
    TimeSpan Duration,
    int Screenshots,
    int Recordings,
    long RecordedBytes,
    bool HadDemo,
    SessionEndReason Reason)
{
    /// <summary>A 1080p session moves roughly two megabytes a second, so anything under a
    /// couple of minutes is noise on the sentence; below this the figure is left out.</summary>
    public const long MinBytesForSize = 2_000_000;

    /// <summary>The toast's one line: "Mirrored for 4m 12s · 2 screenshots · 1 recording".</summary>
    public string Headline
    {
        get
        {
            var what = HadDemo ? "Demo ran" : "Mirrored";
            var sentence = $"{what} for {FormatDuration(Duration)}";
            if (Reason is SessionEndReason.StoppedByUser or SessionEndReason.Faulted) sentence += " · ended here";
            return sentence;
        }
    }

    /// <summary>
    /// Headline with a leading timestamp, locale-aware: when Shamsi is on the start time
    /// is in Persian, with the Gregorian in parentheses when both are configured.
    /// </summary>
    public string HeadlineWithStart(DateTime startedAtLocal, TimestampSettings timestamps)
    {
        var mode = TimestampFormatting.Resolve(
            timestamps.UseShamsi, timestamps.ShowGregorianAlongside, CultureInfo.CurrentCulture);
        var stamp = TimestampFormatting.FormatDateTime(startedAtLocal, mode);
        return $"{stamp} · {Headline}";
    }

    /// <summary>The toast's second line, or null when there is nothing to count.</summary>
    public string? Detail
    {
        get
        {
            var parts = new List<string>(3);
            if (Screenshots > 0) parts.Add(Screenshots == 1 ? "1 screenshot" : $"{Screenshots} screenshots");
            if (Recordings > 0) parts.Add(Recordings == 1 ? "1 recording" : $"{Recordings} recordings");
            if (RecordedBytes >= MinBytesForSize) parts.Add(DescribeSize(RecordedBytes));
            return parts.Count == 0 ? null : string.Join(" · ", parts);
        }
    }

    /// <summary>True when the end deserves a toast: a session with something in it, or one that
    /// ended unexpectedly. A five-second look at the idle screen ends quietly.</summary>
    public bool DeservesToast =>
        Reason is SessionEndReason.Faulted
        || Duration >= TimeSpan.FromSeconds(45)
        || Screenshots > 0
        || Recordings > 0;

    /// <summary>"3.4 MB", "1.2 GB" - for the summary's second line.</summary>
    public static string DescribeSize(long bytes) => (bytes / (1024.0 * 1024.0)) switch
    {
        < 1024 => $"{bytes / (1024.0 * 1024.0):0.#} MB",
        _ => $"{bytes / (1024.0 * 1024.0 * 1024.0):0.0#} GB",
    };

    /// <summary>"4m 12s", "1h 03m", "48s" - the same words the window uses elsewhere.</summary>
    public static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalHours >= 1) return $"{(int)duration.TotalHours}h {duration.Minutes:00}m";
        if (duration.TotalMinutes >= 1) return $"{duration.Minutes}m {duration.Seconds:00}s";
        return $"{Math.Max(duration.Seconds, 0)}s";
    }
}
