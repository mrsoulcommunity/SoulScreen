using System.Globalization;

namespace SoulScreen.Core.Time;

/// <summary>
/// How a Persian date is spelled.
/// </summary>
public enum PersianDateFormat
{
    /// <summary>Numbers with slashes: <c>1403/06/24</c>.</summary>
    Short,
    /// <summary>Number followed by the month name: <c>1403 Shahrivar 24</c>. Latin
    /// transliteration of the month is used so the output is plain ASCII.</summary>
    Long,
}

/// <summary>
/// A date or date-time formatted for someone whose calendar is the Persian (Shamsi) one.
/// <para>
/// Uses <see cref="System.Globalization.PersianCalendar"/> from the BCL: no third-party
/// dependency. PersianCalendar throws on dates before Persian year 62 (roughly Gregorian
/// 2683 March), and accepts only the Gregorian dates it was designed for, so the formatter
/// falls back to Gregorian with a <c>[?]</c> marker rather than crashing.
/// </para>
/// <para>
/// <b>Parse anchor:</b> a Shamsi date like <c>1403/06/24</c> is the year, month and day in
/// the Persian calendar. Concatenating the Gregorian anchor <c>2024-09-15</c> at the same
/// calendar position (when both are shown) gives an unambiguous pair a
/// <see cref="DateTime.Parse(string, IFormatProvider)"/> with <see cref="CultureInfo.InvariantCulture"/>
/// will accept as <c>2024-09-15</c>.
/// </para>
/// </summary>
public static class PersianDateTime
{
    private static readonly string[] PersianMonthsLatin =
    [
        "Farvardin", "Ordibehesht", "Khordad", "Tir", "Mordad", "Shahrivar",
        "Mehr", "Aban", "Azar", "Dey", "Bahman", "Esfand",
    ];

    /// <summary>
    /// True when <paramref name="utc"/> falls inside the range <see cref="PersianCalendar"/>
    /// can represent. Out-of-range dates get a <c>[?]</c> marker instead of an exception.
    /// </summary>
    public static bool IsSupported(DateTime utc)
    {
        // PersianCalendar's range, expressed as DateTime bounds rather than year numbers
        // (GetYear itself throws when the year is outside [62, 9999]).
        return utc >= PersianCalendarMinDate && utc <= PersianCalendarMaxDate;
    }

    private static readonly DateTime PersianCalendarMinDate = new(622, 3, 22);
    private static readonly DateTime PersianCalendarMaxDate = new(9999, 12, 31);

    /// <summary>
    /// Formats <paramref name="utc"/> as a Persian (Shamsi) date.
    /// </summary>
    /// <param name="utc">An instant. Treated as UTC; the caller has already converted.</param>
    /// <param name="fmt">Short <c>1403/06/24</c> or Long <c>1403 Shahrivar 24</c>.</param>
    /// <returns>The formatted string, or <c>[?] yyyy-MM-dd</c> when out of range.</returns>
    public static string ToStringShamsi(DateTime utc, PersianDateFormat fmt = PersianDateFormat.Short)
    {
        if (!IsSupported(utc)) return $"[?] {utc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}";
        var pc = new PersianCalendar();
        var y = pc.GetYear(utc);
        var m = pc.GetMonth(utc);
        var d = pc.GetDayOfMonth(utc);
        return fmt == PersianDateFormat.Long
            ? $"{y} {PersianMonthsLatin[m - 1]} {d:00}"
            : $"{y:0000}/{m:00}/{d:00}";
    }

    /// <summary>
    /// Short Shamsi date with the Gregorian in parentheses, e.g.
    /// <c>1403/06/24 (2024-09-15)</c>. Gregorian is omitted entirely when the converter is
    /// out of range for <see cref="PersianCalendar"/>.
    /// </summary>
    public static string ToStringShamsiWithGregorian(DateTime utc)
    {
        var shamsi = ToStringShamsi(utc, PersianDateFormat.Short);
        if (shamsi.StartsWith("[?]", StringComparison.Ordinal))
            return $"{shamsi} (Gregorian out of range)";
        var gregorian = utc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return $"{shamsi} ({gregorian})";
    }

    /// <summary>
    /// The Persian month name in Latin transliteration, indexed 1–12.
    /// </summary>
    public static string MonthName(int month) =>
        month is >= 1 and <= 12 ? PersianMonthsLatin[month - 1] : "?";
}
