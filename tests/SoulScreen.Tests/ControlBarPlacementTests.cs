using SoulScreen.App;

namespace SoulScreen.Tests;

/// <summary>
/// Tests for the picture-control placement settings: the four-way placement enum, the
/// five-way corner enum, and the way AppSettings.Normalise rejects hand-edited values that
/// would put the bar somewhere it could not be reached.
/// </summary>
public class ControlBarPlacementTests
{
    [Fact]
    public void DefaultsAreFloatingBottomCentre()
    {
        var settings = new AppSettings();
        Assert.Equal(ControlBarPlacement.Floating, settings.ControlBarPlacement);
        Assert.Equal(ControlBarCorner.BottomCentre, settings.ControlBarCorner);
        Assert.Null(settings.ControlBarFreeX);
        Assert.Null(settings.ControlBarFreeY);
    }

    [Fact]
    public void UnknownPlacementFallsBackToFloating()
    {
        var settings = new AppSettings
        {
            ControlBarPlacement = (ControlBarPlacement)999,
        };
        settings.Normalise();
        Assert.Equal(ControlBarPlacement.Floating, settings.ControlBarPlacement);
    }

    [Fact]
    public void UnknownCornerFallsBackToBottomCentre()
    {
        var settings = new AppSettings
        {
            ControlBarCorner = (ControlBarCorner)999,
        };
        settings.Normalise();
        Assert.Equal(ControlBarCorner.BottomCentre, settings.ControlBarCorner);
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(1.1)]
    [InlineData(double.NaN)]
    public void OffPictureFractionsAreRejected(double fraction)
    {
        var settings = new AppSettings
        {
            ControlBarFreeX = fraction,
            ControlBarFreeY = fraction,
        };
        settings.Normalise();
        Assert.Null(settings.ControlBarFreeX);
        Assert.Null(settings.ControlBarFreeY);
    }

    [Fact]
    public void InPictureFractionsAreKept()
    {
        var settings = new AppSettings
        {
            ControlBarFreeX = 0.25,
            ControlBarFreeY = 0.75,
        };
        settings.Normalise();
        Assert.Equal(0.25, settings.ControlBarFreeX);
        Assert.Equal(0.75, settings.ControlBarFreeY);
    }
}
