using SoulScreen.App;
using SoulScreen.App.Logic;

namespace SoulScreen.Tests;

/// <summary>Defensive clamping in <see cref="AppSettings.Normalise"/> for the watermark
/// section - a hand-edited or corrupted settings.json must never crash a capture.</summary>
public class WatermarkSettingsNormaliseTests
{
    private static AppSettings New()
    {
        var settings = new AppSettings();
        settings.Normalise();
        return settings;
    }

    [Fact]
    public void DefaultsAreOffWithASensibleCornerScaleAndOpacity()
    {
        var settings = New();
        Assert.False(settings.Watermark.Enabled);
        Assert.Equal(WatermarkCorner.BottomRight, settings.Watermark.Corner);
        Assert.InRange(settings.Watermark.Scale, WatermarkPlacement.MinScale, WatermarkPlacement.MaxScale);
        Assert.InRange(settings.Watermark.Opacity, 0, 1);
    }

    [Fact]
    public void EnabledWithNoImagePathIsForcedOff()
    {
        var settings = new AppSettings
        {
            Watermark = new WatermarkSettings { Enabled = true, ImagePath = null },
        };
        settings.Normalise();
        Assert.False(settings.Watermark.Enabled);
    }

    [Fact]
    public void EnabledWithABlankImagePathIsForcedOff()
    {
        var settings = new AppSettings
        {
            Watermark = new WatermarkSettings { Enabled = true, ImagePath = "   " },
        };
        settings.Normalise();
        Assert.False(settings.Watermark.Enabled);
    }

    [Fact]
    public void EnabledWithARealPathStaysEnabled()
    {
        var settings = new AppSettings
        {
            Watermark = new WatermarkSettings { Enabled = true, ImagePath = "C:/logo.png" },
        };
        settings.Normalise();
        Assert.True(settings.Watermark.Enabled);
    }

    [Fact]
    public void OutOfRangeScaleOpacityAndMarginAreClamped()
    {
        var settings = new AppSettings
        {
            Watermark = new WatermarkSettings
            {
                Enabled = true,
                ImagePath = "C:/logo.png",
                Scale = 99,
                Opacity = -5,
                MarginFraction = 999,
            },
        };
        settings.Normalise();
        Assert.Equal(WatermarkPlacement.MaxScale, settings.Watermark.Scale);
        Assert.Equal(0, settings.Watermark.Opacity);
        Assert.Equal(0.5, settings.Watermark.MarginFraction);
    }

    [Fact]
    public void AnUndefinedCornerFallsBackToBottomRight()
    {
        var settings = new AppSettings
        {
            Watermark = new WatermarkSettings { Corner = (WatermarkCorner)999 },
        };
        settings.Normalise();
        Assert.Equal(WatermarkCorner.BottomRight, settings.Watermark.Corner);
    }

    [Fact]
    public void ANullWatermarkSectionIsReplacedRatherThanLeftNull()
    {
        var settings = new AppSettings { Watermark = null! };
        settings.Normalise();
        Assert.NotNull(settings.Watermark);
    }
}
