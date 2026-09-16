using System.Globalization;

namespace SoulScreen.Core.Time;

/// <summary>
/// How to spell a timestamp in the UI.
/// </summary>
public enum TimestampMode
{
    /// <summary>Gregorian only, current culture. The default.</summary>
    Gregorian,
    /// <summary>Persian (Shamsi) only, with the current culture's time formatting.</summary>
    Shamsi,
    /// <summary>Shamsi primary, Gregorian in parentheses.</summary>
    Both,
}

/// <summary>
/// One formatter for every place SoulScreen shows a date or time. Centralised so the
/// Shamsi/Gregorian toggle changes them all at once.
/// </summary>
public static class TimestampFormatting
{
    /// <summary>
    /// Picks the display mode from settings and the UI culture, in priority order:
    /// <list type="number">
    /// <item>The explicit setting in <paramref name="settings"/>, when set.</item>
    /// <item>Otherwise on when <paramref name="uiCulture"/>'s two-letter ISO name is
    /// <c>fa</c> (Persian / Dari).</item>
    /// <item>Otherwise off (Gregorian only).</item>
    /// </list>
    /// </summary>
    public static TimestampMode Resolve(bool? useShamsiSetting, bool showGregorianAlongside, CultureInfo uiCulture)
    {
        var on = useShamsiSetting
                 ?? string.Equals(uiCulture.TwoLetterISOLanguageName, "fa", StringComparison.OrdinalIgnoreCase);
        if (!on) return TimestampMode.Gregorian;
        return showGregorianAlongside ? TimestampMode.Both : TimestampMode.Shamsi;
    }

    /// <summary>
    /// Formats a local date (the capture folder sorts by it). For a time-of-day use
    /// <see cref="FormatTime"/>.
    /// </summary>
    public static string FormatDate(DateTime local, TimestampMode mode)
    {
        return mode switch
        {
            TimestampMode.Shamsi => PersianDateTime.ToStringShamsi(local),
            TimestampMode.Both => PersianDateTime.ToStringShamsiWithGregorian(local),
            _ => local.ToString("yyyy-MM-dd", CultureInfo.CurrentCulture),
        };
    }

    /// <summary>
    /// Formats a local time-of-day. Always Gregorian numerals under the current culture —
    /// Shamsi doesn't replace the way clocks are written.
    /// </summary>
    public static string FormatTime(DateTime local) =>
        local.ToString("HH:mm:ss", CultureInfo.CurrentCulture);

    /// <summary>
    /// Combined date + time, used by surfaces that want both (gallery subtitle,
    /// session-summary card).
    /// </summary>
    public static string FormatDateTime(DateTime local, TimestampMode mode)
    {
        var date = FormatDate(local, mode);
        var time = FormatTime(local);
        return mode == TimestampMode.Both
            ? $"{date} {time}"
            : $"{date} {time}";
    }

    /// <summary>Short month-day, for the activity log rows.</summary>
    public static string FormatMonthDay(DateTime local, TimestampMode mode)
    {
        return mode switch
        {
            TimestampMode.Shamsi => ExtractMonthDay(PersianDateTime.ToStringShamsi(local)),
            TimestampMode.Both => ExtractMonthDay(PersianDateTime.ToStringShamsiWithGregorian(local)),
            _ => local.ToString("MM-dd", CultureInfo.CurrentCulture),
        };
    }

    private static string ExtractMonthDay(string shamsiShort)
    {
        // "1403/06/24" -> "06/24"; "[?] 2024-09-15" -> "09-15"
        var slash = shamsiShort.IndexOf('/');
        if (slash >= 0 && slash + 3 <= shamsiShort.Length)
            return shamsiShort.Substring(slash + 1, 5); // "MM/dd"
        var dash = shamsiShort.IndexOf('-');
        return dash >= 0 ? shamsiShort[(dash + 1)..] : shamsiShort;
    }
}
