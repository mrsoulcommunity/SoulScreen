using System.Globalization;
using SoulScreen.Core.Time;

namespace SoulScreen.Tests;

/// <summary>
/// Persian calendar conversion: known dates, leap-year edge, and the out-of-range fallback.
/// </summary>
public class PersianDateTimeTests
{
    [Fact]
    public void Nowruz2024_1403FirstDay()
    {
        // 2024-03-20 = 01 Farvardin 1403 (the first day of the Persian year)
        Assert.Equal("1403/01/01", PersianDateTime.ToStringShamsi(new DateTime(2024, 3, 20)));
    }

    [Fact]
    public void Nowruz2025_1404FirstDay()
    {
        Assert.Equal("1404/01/01", PersianDateTime.ToStringShamsi(new DateTime(2025, 3, 21)));
    }

    [Fact]
    public void September15_2024_IsShahrivar25()
    {
        Assert.Equal("1403/06/25", PersianDateTime.ToStringShamsi(new DateTime(2024, 9, 15)));
    }

    [Fact]
    public void LongForm_NamesMonthInLatin()
    {
        var s = PersianDateTime.ToStringShamsi(new DateTime(2024, 9, 15), PersianDateFormat.Long);
        Assert.Equal("1403 Shahrivar 25", s);
    }

    [Fact]
    public void LeapYear_1403_HasEsfand29()
    {
        // 1403 is a leap year in the Persian calendar: Esfand has 30 days.
        // 2025-03-20 is Esfand 29, 1404 (the day before Nowruz 1404 - leap day).
        Assert.Equal("1403/12/30", PersianDateTime.ToStringShamsi(new DateTime(2025, 3, 20)));
    }

    [Fact]
    public void Both_AppendsGregorianInParens()
    {
        var s = PersianDateTime.ToStringShamsiWithGregorian(new DateTime(2024, 9, 15));
        Assert.Equal("1403/06/25 (2024-09-15)", s);
    }

    [Fact]
    public void OutOfRange_FallsBackWithQuestionMark()
    {
        // PersianCalendar supports years 62..9999 (Gregorian ~2623..9999). 1800 is well
        // inside the supported range, so use a date that genuinely breaks: year 1.
        var ancient = new DateTime(1, 1, 1);
        Assert.False(PersianDateTime.IsSupported(ancient));
        Assert.StartsWith("[?]", PersianDateTime.ToStringShamsi(ancient));
    }

    [Fact]
    public void OutOfRange_Both_StillProducesSomething()
    {
        var ancient = new DateTime(1, 1, 1);
        var s = PersianDateTime.ToStringShamsiWithGregorian(ancient);
        Assert.Contains("0001-01-01", s);
    }

    [Fact]
    public void MonthName_ReturnsLatinTransliteration()
    {
        Assert.Equal("Shahrivar", PersianDateTime.MonthName(6));
        Assert.Equal("Esfand", PersianDateTime.MonthName(12));
    }

    [Fact]
    public void MonthName_OutOfRange_ReturnsQuestionMark()
    {
        Assert.Equal("?", PersianDateTime.MonthName(0));
        Assert.Equal("?", PersianDateTime.MonthName(13));
    }

    [Fact]
    public void LocaleResolution_ExplicitSettingOverridesCulture()
    {
        // fa-IR UI culture suggests Shamsi; explicit false wins.
        var ui = new CultureInfo("fa-IR");
        Assert.Equal(TimestampMode.Gregorian, TimestampFormatting.Resolve(useShamsiSetting: false, showGregorianAlongside: false, uiCulture: ui));
    }

    [Fact]
    public void LocaleResolution_FaCultureTurnsItOn()
    {
        var ui = new CultureInfo("fa-IR");
        Assert.Equal(TimestampMode.Shamsi, TimestampFormatting.Resolve(useShamsiSetting: null, showGregorianAlongside: false, uiCulture: ui));
    }

    [Fact]
    public void LocaleResolution_EnCultureStaysOff()
    {
        var ui = new CultureInfo("en-US");
        Assert.Equal(TimestampMode.Gregorian, TimestampFormatting.Resolve(useShamsiSetting: null, showGregorianAlongside: false, uiCulture: ui));
    }

    [Fact]
    public void LocaleResolution_BothWhenShowGregorianAlongside()
    {
        var ui = new CultureInfo("fa-IR");
        Assert.Equal(TimestampMode.Both, TimestampFormatting.Resolve(useShamsiSetting: null, showGregorianAlongside: true, uiCulture: ui));
    }
}
