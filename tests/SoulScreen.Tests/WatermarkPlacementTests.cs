using SoulScreen.App.Logic;

namespace SoulScreen.Tests;

/// <summary>
/// Watermark placement arithmetic: given a canvas and a mark, where does the mark go.
/// </summary>
public class WatermarkPlacementTests
{
    [Fact]
    public void BottomRightSitsAgainstTheBottomAndRightEdgesLessTheMargin()
    {
        var (x, y, width, height) = WatermarkPlacement.Place(
            1000, 500, 200, 100, scale: 0.2, WatermarkCorner.BottomRight, marginFraction: 0.05);

        // shortest side is 500; scale 0.2 -> longest mark side 100; mark aspect 2:1 -> 100x50.
        Assert.Equal(100, width, 3);
        Assert.Equal(50, height, 3);

        var margin = 500 * 0.05; // 25
        Assert.Equal(1000 - 100 - margin, x, 3);
        Assert.Equal(500 - 50 - margin, y, 3);
    }

    [Fact]
    public void TopLeftSitsAtTheMarginFromBothEdges()
    {
        var (x, y, _, _) = WatermarkPlacement.Place(
            800, 800, 100, 100, scale: 0.1, WatermarkCorner.TopLeft, marginFraction: 0.02);

        Assert.Equal(16, x, 3); // 800 * 0.02
        Assert.Equal(16, y, 3);
    }

    [Fact]
    public void CenterIgnoresMarginAndSitsInTheMiddle()
    {
        var (x, y, width, height) = WatermarkPlacement.Place(
            800, 400, 100, 100, scale: 0.25, WatermarkCorner.Center, marginFraction: 0.5);

        Assert.Equal((800 - width) / 2, x, 3);
        Assert.Equal((400 - height) / 2, y, 3);
    }

    [Fact]
    public void KeepsTheMarksAspectRatio()
    {
        var (_, _, width, height) = WatermarkPlacement.Place(
            1920, 1080, 300, 100, scale: 0.3, WatermarkCorner.TopRight, marginFraction: 0.03);

        Assert.Equal(300.0 / 100.0, width / height, 3);
    }

    [Fact]
    public void ScaleIsClampedToTheAllowedRange()
    {
        var (_, _, tooSmallWidth, _) = WatermarkPlacement.Place(
            1000, 1000, 100, 100, scale: -5, WatermarkCorner.TopLeft, marginFraction: 0);
        var (_, _, minWidth, _) = WatermarkPlacement.Place(
            1000, 1000, 100, 100, scale: WatermarkPlacement.MinScale, WatermarkCorner.TopLeft, marginFraction: 0);
        Assert.Equal(minWidth, tooSmallWidth, 3);

        var (_, _, tooBigWidth, _) = WatermarkPlacement.Place(
            1000, 1000, 100, 100, scale: 50, WatermarkCorner.TopLeft, marginFraction: 0);
        var (_, _, maxWidth, _) = WatermarkPlacement.Place(
            1000, 1000, 100, 100, scale: WatermarkPlacement.MaxScale, WatermarkCorner.TopLeft, marginFraction: 0);
        Assert.Equal(maxWidth, tooBigWidth, 3);
    }

    [Fact]
    public void ANonPositiveCanvasOrMarkProducesAnEmptyRectangleRatherThanThrowing()
    {
        Assert.Equal((0, 0, 0, 0), WatermarkPlacement.Place(0, 500, 100, 100, 0.2, WatermarkCorner.Center, 0.05));
        Assert.Equal((0, 0, 0, 0), WatermarkPlacement.Place(500, -1, 100, 100, 0.2, WatermarkCorner.Center, 0.05));
        Assert.Equal((0, 0, 0, 0), WatermarkPlacement.Place(500, 500, 0, 100, 0.2, WatermarkCorner.Center, 0.05));
        Assert.Equal((0, 0, 0, 0), WatermarkPlacement.Place(500, 500, 100, 0, 0.2, WatermarkCorner.Center, 0.05));
    }

    [Fact]
    public void AnOversizedMarkOrMarginNeverPlacesTheRectangleOffCanvas()
    {
        // Ask for an enormous margin relative to a huge scale; the result must still fit.
        var (x, y, width, height) = WatermarkPlacement.Place(
            200, 200, 100, 100, scale: WatermarkPlacement.MaxScale, WatermarkCorner.BottomRight, marginFraction: 0.5);

        Assert.True(x >= 0);
        Assert.True(y >= 0);
        Assert.True(x + width <= 200 + 0.001);
        Assert.True(y + height <= 200 + 0.001);
    }

    [Theory]
    [InlineData(WatermarkCorner.TopLeft)]
    [InlineData(WatermarkCorner.TopRight)]
    [InlineData(WatermarkCorner.BottomLeft)]
    [InlineData(WatermarkCorner.BottomRight)]
    [InlineData(WatermarkCorner.Center)]
    public void EveryCornerStaysFullyInsideAnOrdinaryCanvas(WatermarkCorner corner)
    {
        var (x, y, width, height) = WatermarkPlacement.Place(
            1920, 1080, 400, 150, WatermarkPlacement.DefaultScale, corner, WatermarkPlacement.DefaultMarginFraction);

        Assert.True(x >= 0);
        Assert.True(y >= 0);
        Assert.True(x + width <= 1920 + 0.001);
        Assert.True(y + height <= 1080 + 0.001);
    }
}
