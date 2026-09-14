using System.Globalization;

namespace SoulScreen.App.Logic;

/// <summary>How much room is left on the drive a recording is going to.</summary>
public enum DiskSpaceLevel
{
    Plenty,
    /// <summary>Worth a warning: a long recording could fill it.</summary>
    Low,
    /// <summary>The recording is stopped, so the file can still be finalised and played.</summary>
    Critical,
}

/// <summary>
/// Watches the capture drive while recording.
/// <para>
/// An MP4 is only playable once its index has been written at the end. A drive that fills up
/// mid-recording leaves a file nothing can open, so the recording is ended while there is
/// still room to finish it properly.
/// </para>
/// Deliberately free of WPF so it can be tested on its own.
/// </summary>
internal static class DiskSpace
{
    public const long LowBytes = 2L * 1024 * 1024 * 1024;
    public const long CriticalBytes = 500L * 1024 * 1024;

    /// <param name="freeBytes">Free space, or null when it could not be read - which is not
    /// a reason to stop anyone recording.</param>
    public static DiskSpaceLevel Classify(long? freeBytes) => freeBytes switch
    {
        null or < 0 => DiskSpaceLevel.Plenty,
        < CriticalBytes => DiskSpaceLevel.Critical,
        < LowBytes => DiskSpaceLevel.Low,
        _ => DiskSpaceLevel.Plenty,
    };

    /// <summary>Roughly how long recording can go on at <paramref name="bytesPerSecond"/> before
    /// it would be stopped, or null when the rate is not known yet.</summary>
    public static TimeSpan? TimeLeft(long freeBytes, double bytesPerSecond)
    {
        if (!(bytesPerSecond > 0) || double.IsInfinity(bytesPerSecond)) return null;
        var seconds = Math.Max(0, freeBytes - CriticalBytes) / bytesPerSecond;
        return TimeSpan.FromSeconds(Math.Min(seconds, TimeSpan.MaxValue.TotalSeconds / 2));
    }

    /// <summary>"about 40 minutes", "about 3 hours".</summary>
    public static string DescribeTimeLeft(TimeSpan time)
    {
        if (time.TotalMinutes < 1) return "less than a minute";
        if (time.TotalMinutes < 90)
        {
            var minutes = (int)Math.Round(time.TotalMinutes);
            return minutes == 1 ? "about a minute" : $"about {minutes.ToString(CultureInfo.InvariantCulture)} minutes";
        }
        var hours = (int)Math.Round(time.TotalHours);
        return $"about {hours.ToString(CultureInfo.InvariantCulture)} hours";
    }
}
