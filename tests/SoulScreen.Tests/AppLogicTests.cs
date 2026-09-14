using SoulScreen.App.Logic;

namespace SoulScreen.Tests;

/// <summary>
/// The window's decision-making that does not need WPF: palette ranking, capture naming,
/// accent shades and mini player placement. The source files are linked in from the app.
/// </summary>
public class CommandMatcherTests
{
    [Fact]
    public void EmptyQueryMatchesEverything()
    {
        Assert.Equal(0, CommandMatcher.Score("", "Save screenshot"));
        Assert.Equal(0, CommandMatcher.Score("   ", "Save screenshot"));
    }

    [Fact]
    public void NoMatchIsNull()
    {
        Assert.Null(CommandMatcher.Score("zebra", "Save screenshot"));
    }

    [Fact]
    public void EveryWordMustMatch()
    {
        Assert.NotNull(CommandMatcher.Score("save shot", "Save screenshot"));
        Assert.Null(CommandMatcher.Score("save zebra", "Save screenshot"));
    }

    [Fact]
    public void TitleStartBeatsWordStartBeatsInside()
    {
        var start = CommandMatcher.Score("full", "Fullscreen")!.Value;
        var word = CommandMatcher.Score("full", "Enter fullscreen")!.Value;
        var inside = CommandMatcher.Score("screen", "Fullscreen")!.Value;
        Assert.True(start > word, $"{start} should beat {word}");
        Assert.True(word > inside, $"{word} should beat {inside}");
    }

    [Fact]
    public void LettersInOrderStillMatch()
    {
        Assert.NotNull(CommandMatcher.Score("fscr", "Fullscreen"));
        Assert.Null(CommandMatcher.Score("rcsf", "Fullscreen"));
    }

    [Fact]
    public void KeywordsMatchButRankBelowTheTitle()
    {
        var viaKeyword = CommandMatcher.Score("pip", "Mini player", "picture in picture pip float")!.Value;
        var viaTitle = CommandMatcher.Score("pip", "Pip settings")!.Value;
        Assert.True(viaTitle > viaKeyword);
    }

    [Fact]
    public void MatchingIgnoresCase()
    {
        Assert.Equal(CommandMatcher.Score("MUTE", "Mute"), CommandMatcher.Score("mute", "Mute"));
    }
}

public class CaptureNamingTests
{
    private static readonly DateTime Moment = new(2026, 9, 14, 9, 5, 7);

    [Fact]
    public void NameCarriesTheTimeInInvariantDigits()
    {
        var path = CaptureNaming.NewPath("C:\\captures", Moment, ".png", _ => false);
        Assert.Equal(Path.Combine("C:\\captures", "SoulScreen-20260914-090507.png"), path);
    }

    [Fact]
    public void NameIgnoresACultureWithItsOwnCalendar()
    {
        var saved = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("fa-IR");
            var path = CaptureNaming.NewPath("C:\\captures", Moment, ".png", _ => false);
            Assert.EndsWith("SoulScreen-20260914-090507.png", path);
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = saved;
        }
    }

    [Fact]
    public void ACaptureInTheSameSecondDoesNotOverwrite()
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var first = CaptureNaming.NewPath("C:\\c", Moment, ".png", taken.Contains);
        taken.Add(first);
        var second = CaptureNaming.NewPath("C:\\c", Moment, ".png", taken.Contains);
        taken.Add(second);
        var third = CaptureNaming.NewPath("C:\\c", Moment, ".png", taken.Contains);

        Assert.EndsWith("SoulScreen-20260914-090507.png", first);
        Assert.EndsWith("SoulScreen-20260914-090507-2.png", second);
        Assert.EndsWith("SoulScreen-20260914-090507-3.png", third);
    }

    [Theory]
    [InlineData("SoulScreen-20260914-090507.png", CaptureKind.Screenshot)]
    [InlineData("soulscreen-20260914-090507-2.JPG", CaptureKind.Screenshot)]
    [InlineData("SoulScreen-20260914-090507.jpeg", CaptureKind.Screenshot)]
    [InlineData("SoulScreen-20260914-090507.mp4", CaptureKind.Recording)]
    public void RecognisesItsOwnFiles(string name, CaptureKind kind)
    {
        Assert.Equal(kind, CaptureNaming.KindOf(Path.Combine("C:\\c", name)));
    }

    [Theory]
    [InlineData("holiday.png")]
    [InlineData("SoulScreen-20260914-090507.txt")]
    [InlineData("SoulScreen-20260914-090507.mp4.tmp")]
    public void IgnoresEverythingElse(string name)
    {
        Assert.Null(CaptureNaming.KindOf(Path.Combine("C:\\c", name)));
    }

    [Theory]
    [InlineData(512, "512 B")]
    [InlineData(2048, "2 KB")]
    [InlineData(5 * 1024 * 1024 + 512 * 1024, "5.5 MB")]
    [InlineData(3L * 1024 * 1024 * 1024, "3 GB")]
    public void SizesReadNaturally(long bytes, string expected)
    {
        Assert.Equal(expected, CaptureNaming.FormatSize(bytes));
    }
}

public class AccentPaletteTests
{
    [Fact]
    public void BlueKeepsTheShippedShades()
    {
        var dark = AccentPalette.For(AccentColor.Blue, dark: true);
        Assert.Equal(Rgb.Parse("#0A84FF"), dark.Accent);
        Assert.Equal(Rgb.Parse("#3395FF"), dark.Hover);
        Assert.Equal(Rgb.Parse("#0071E3"), dark.Pressed);

        var light = AccentPalette.For(AccentColor.Blue, dark: false);
        Assert.Equal(Rgb.Parse("#007AFF"), light.Accent);
    }

    [Fact]
    public void EveryAccentIsDistinctInBothThemes()
    {
        foreach (var dark in new[] { true, false })
        {
            var colours = Enum.GetValues<AccentColor>().Select(a => AccentPalette.For(a, dark).Accent).ToList();
            Assert.Equal(colours.Count, colours.Distinct().Count());
        }
    }

    [Fact]
    public void HoverIsLighterAndPressedDarker()
    {
        foreach (var accent in Enum.GetValues<AccentColor>())
        foreach (var dark in new[] { true, false })
        {
            var shades = AccentPalette.For(accent, dark);
            Assert.True(shades.Hover.Luminance >= shades.Accent.Luminance, $"{accent} hover");
            Assert.True(shades.Pressed.Luminance <= shades.Accent.Luminance, $"{accent} pressed");
        }
    }

    [Fact]
    public void TextOnALightAccentIsDark()
    {
        Assert.NotEqual(Rgb.White, AccentPalette.For(AccentColor.Yellow, dark: true).OnAccent);
        Assert.Equal(Rgb.White, AccentPalette.For(AccentColor.Blue, dark: true).OnAccent);
        Assert.Equal(Rgb.White, AccentPalette.For(AccentColor.Purple, dark: false).OnAccent);
    }

    [Fact]
    public void MixReachesItsEnds()
    {
        var red = new Rgb(255, 0, 0);
        Assert.Equal(red, red.Mix(Rgb.Black, 0));
        Assert.Equal(Rgb.Black, red.Mix(Rgb.Black, 1));
        Assert.Equal(new Rgb(128, 0, 0), red.Mix(Rgb.Black, 0.5));
    }

    [Fact]
    public void ParseRejectsNonsense()
    {
        Assert.Throws<FormatException>(() => Rgb.Parse("#12345"));
    }
}

public class MiniPlayerGeometryTests
{
    private static readonly Bounds Screen = new(0, 0, 1920, 1040);

    private static void AssertInside(Bounds inner, Bounds outer)
    {
        Assert.True(inner.Left >= outer.Left - 0.001, $"left {inner.Left}");
        Assert.True(inner.Top >= outer.Top - 0.001, $"top {inner.Top}");
        Assert.True(inner.Right <= outer.Right + 0.001, $"right {inner.Right}");
        Assert.True(inner.Bottom <= outer.Bottom + 0.001, $"bottom {inner.Bottom}");
    }

    [Fact]
    public void DefaultsToTheBottomRightInThePicturesShape()
    {
        var aspect = 9.0 / 19.5;
        var placed = MiniPlayerGeometry.Place(aspect, Screen, null, null, null);

        Assert.Equal(aspect, placed.Width / placed.Height, 3);
        Assert.Equal(Screen.Right - MiniPlayerGeometry.Margin, placed.Right, 3);
        Assert.Equal(Screen.Bottom - MiniPlayerGeometry.Margin, placed.Bottom, 3);
        AssertInside(placed, Screen);
    }

    [Fact]
    public void LandscapeUsesTheWidthForItsSize()
    {
        var placed = MiniPlayerGeometry.Place(16.0 / 9.0, Screen, null, null, null);
        Assert.True(placed.Width > placed.Height);
        AssertInside(placed, Screen);
    }

    [Fact]
    public void RememberedSizeSurvivesThePhoneTurning()
    {
        var portrait = MiniPlayerGeometry.Place(9.0 / 16.0, Screen, 400, null, null);
        var landscape = MiniPlayerGeometry.Place(16.0 / 9.0, Screen, 400, null, null);
        Assert.Equal(400, MiniPlayerGeometry.LongSide(portrait.Width, portrait.Height), 3);
        Assert.Equal(400, MiniPlayerGeometry.LongSide(landscape.Width, landscape.Height), 3);
    }

    [Fact]
    public void ARememberedPlaceOffTheScreenIsPulledBack()
    {
        var placed = MiniPlayerGeometry.Place(9.0 / 16.0, Screen, 400, 5000, -300);
        AssertInside(placed, Screen);
    }

    [Fact]
    public void NeverSmallerThanTheControlsNeed()
    {
        var placed = MiniPlayerGeometry.Place(9.0 / 16.0, Screen, 50, null, null);
        Assert.True(Math.Min(placed.Width, placed.Height) >= MiniPlayerGeometry.MinimumShortSide - 0.001);
    }

    [Fact]
    public void NeverLargerThanTheScreen()
    {
        var small = new Bounds(100, 50, 640, 480);
        var placed = MiniPlayerGeometry.Place(9.0 / 16.0, small, 5000, null, null);
        AssertInside(placed, small);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void ANonsenseAspectFallsBackToAPhone(double aspect)
    {
        var placed = MiniPlayerGeometry.Place(aspect, Screen, null, null, null);
        Assert.True(placed.Height > placed.Width);
        AssertInside(placed, Screen);
    }
}
