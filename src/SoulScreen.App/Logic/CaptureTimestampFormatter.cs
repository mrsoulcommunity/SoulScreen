using System.Globalization;
using System.IO;
using SoulScreen.Core.Time;

namespace SoulScreen.App.Logic;

/// <summary>
/// Builds capture filenames in the configured timestamp style. Wraps <see cref="CaptureNaming"/>
/// so callers stay the same shape, and the Shamsi / Gregorian switch is in one place.
/// </summary>
internal static class CaptureTimestampFormatter
{
    /// <summary>The local time used as the basis for the log file's timestamp in its name.
    /// Returned as <see cref="DateTime.Now"/>: logs name themselves after the user's clock,
    /// so a file picked up off the desk is named after when it was created.</summary>
    public static DateTime LogFileTimestamp() => DateTime.Now;

    /// <summary>
    /// A path in <paramref name="directory"/> that does not exist yet. The stem is the
    /// configured format (<c>SoulScreen-yyyyMMdd-HHmmss</c> under Gregorian, or
    /// <c>SoulScreen-yyyyMMdd-HHmmss</c> with Shamsi numerals under Shamsi) plus an optional
    /// sanitised device name. A numbered suffix is appended when the file already exists.
    /// </summary>
    public static string NewPath(
        string directory,
        DateTime localTime,
        string extension,
        bool useShamsi,
        string deviceName = "",
        Func<string, bool>? exists = null)
    {
        exists ??= File.Exists;
        var stem = BuildStem(localTime, useShamsi, deviceName);
        var path = Path.Combine(directory, stem + extension);
        for (var suffix = 2; exists(path); suffix++)
            path = Path.Combine(directory, $"{stem}-{suffix.ToString(CultureInfo.InvariantCulture)}{extension}");
        return path;
    }

    /// <summary>
    /// Overload that resolves the calendar choice from the app settings: explicit
    /// <see cref="TimestampSettings.UseShamsi"/> wins; otherwise the caller's
    /// <paramref name="uiCulture"/> decides. Used by every capture-filename site so the
    /// three lines of conditionals don't have to live at each one.
    /// </summary>
    public static string NewPath(
        string directory,
        DateTime localTime,
        string extension,
        TimestampSettings settings,
        System.Globalization.CultureInfo uiCulture,
        string deviceName = "",
        Func<string, bool>? exists = null)
    {
        var useShamsi = TimestampFormatting.Resolve(settings.UseShamsi, settings.ShowGregorianAlongside, uiCulture) != TimestampMode.Gregorian;
        return NewPath(directory, localTime, extension, useShamsi, deviceName, exists);
    }

    /// <summary>The stem portion, exposed so tests can round-trip parse.</summary>
    public static string BuildStem(DateTime localTime, bool useShamsi, string deviceName = "")
    {
        // Shamsi stems carry a "SH" calendar marker so the parser knows which calendar the
        // 14-digit numeric block is in. Gregorian stems stay plain, matching today's format
        // for files written before this feature existed.
        var timePart = useShamsi
            ? ToShamsiCompact(localTime)
            : localTime.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var calendarMarker = useShamsi ? "-SH" : "";
        var device = SanitiseDevice(deviceName);
        var body = string.IsNullOrEmpty(device)
            ? timePart + calendarMarker
            : timePart + calendarMarker + "-" + device;
        return CaptureNaming.Prefix + body;
    }

    /// <summary>
    /// Reverse of <see cref="BuildStem"/>: pulls the local <see cref="DateTime"/> back out of
    /// a file name so a freshly-formatted file can be matched against its mtime.
    /// </summary>
    public static DateTime? ParseStem(string stem)
    {
        if (stem is null) return null;
        var trimmed = stem.StartsWith(CaptureNaming.Prefix, StringComparison.OrdinalIgnoreCase)
            ? stem[CaptureNaming.Prefix.Length..]
            : stem;
        var shamsi = false;
        // "-SH" is the calendar marker; strip it before pulling the time block.
        var shamsiMarker = trimmed.IndexOf("-SH", StringComparison.Ordinal);
        if (shamsiMarker >= 0)
        {
            shamsi = true;
            trimmed = trimmed[..shamsiMarker] + trimmed[(shamsiMarker + 3)..];
        }
        var dash = trimmed.IndexOf('-');
        if (dash < 0 || dash + 7 > trimmed.Length) return null;
        var datePart = trimmed[..dash];     // yyyyMMdd
        var timePart = trimmed[(dash + 1)..];
        var secondDash = timePart.IndexOf('-');
        var timeOnly = secondDash >= 0 && LooksLikeCollisionSuffix(timePart[(secondDash + 1)..])
            ? timePart[..secondDash]
            : timePart;
        var maybeDevice = timeOnly.IndexOf('-');
        var hhMMss = maybeDevice >= 0 ? timeOnly[..maybeDevice] : timeOnly;
        if (hhMMss.Length != 6) return null;
        return TryParseCompact(datePart + hhMMss, shamsi);
    }

    private static string ToShamsiCompact(DateTime utc)
    {
        // yyyyMMdd-HHmmss in Shamsi numerals, so a file sort by name is still chronological.
        if (!PersianDateTime.IsSupported(utc))
            return utc.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-?";
        var pc = new PersianCalendar();
        var y = pc.GetYear(utc).ToString("0000", CultureInfo.InvariantCulture);
        var m = pc.GetMonth(utc).ToString("00", CultureInfo.InvariantCulture);
        var d = pc.GetDayOfMonth(utc).ToString("00", CultureInfo.InvariantCulture);
        var h = utc.ToString("HH", CultureInfo.InvariantCulture);
        var mi = utc.ToString("mm", CultureInfo.InvariantCulture);
        var s = utc.ToString("ss", CultureInfo.InvariantCulture);
        return $"{y}{m}{d}-{h}{mi}{s}";
    }

    private static DateTime? TryParseCompact(string s, bool shamsi)
    {
        if (s.Length != 14) return null;
        if (!long.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out _)) return null;
        var yyyy = int.Parse(s[..4], CultureInfo.InvariantCulture);
        var mm = int.Parse(s.Substring(4, 2), CultureInfo.InvariantCulture);
        var dd = int.Parse(s.Substring(6, 2), CultureInfo.InvariantCulture);
        var hh = int.Parse(s.Substring(8, 2), CultureInfo.InvariantCulture);
        var mi = int.Parse(s.Substring(10, 2), CultureInfo.InvariantCulture);
        var ss = int.Parse(s.Substring(12, 2), CultureInfo.InvariantCulture);
        try
        {
            if (shamsi)
            {
                var pc = new PersianCalendar();
                return pc.ToDateTime(yyyy, mm, dd, hh, mi, ss, 0);
            }
        }
        catch (ArgumentOutOfRangeException) { return null; }
        try { return new DateTime(yyyy, mm, dd, hh, mi, ss, DateTimeKind.Local); }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    private static bool LooksLikeCollisionSuffix(string tail) =>
        !tail.Contains('-') && tail.All(char.IsDigit);

    /// <summary>
    /// Path-invalid characters and whitespace collapsed to a single dash, capped at 32 chars.
    /// Empty input returns an empty string.
    /// </summary>
    public static string SanitiseDevice(string deviceName)
    {
        if (string.IsNullOrWhiteSpace(deviceName)) return string.Empty;
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(deviceName.Select(c => Array.IndexOf(invalid, c) >= 0 ? '-' : c).ToArray());
        var collapsed = string.Join('-', cleaned.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return collapsed.Length > 32 ? collapsed[..32] : collapsed;
    }
}
