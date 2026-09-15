using SoulScreen.App.Logic;

namespace SoulScreen.Tests;

public class DisplayLayoutHardeningTests
{
    [Fact]
    public void InvalidDisplayGeometryDoesNotProduceAMove()
    {
        var invalid = new DisplayChoice(0, "bad", true, new RectBounds(0, 0, double.NaN, 500));
        Assert.Null(DisplayLayout.Resolve(DisplayLayout.PrimaryDisplay, [invalid], new RectBounds(20, 20, 400, 300)));
    }

    [Fact]
    public void OversizedWindowIsClampedToTheReachableOriginRange()
    {
        var desktop = new RectBounds(0, 0, 1920, 1080);
        var (left, top) = DisplayLayout.ClampToDisplays(-9000, 9000, 3000, 2000, desktop);
        Assert.Equal(-1080, left, 3);
        Assert.Equal(-920, top, 3);
    }

    [Fact]
    public void NonFinitePlacementFallsBackWithoutInventingCoordinates()
    {
        var display = new DisplayChoice(0, "display", true, new RectBounds(0, 0, 1920, 1080));
        var result = DisplayLayout.PlacementFor(display, new RectBounds(double.NaN, 10, 800, 600));
        Assert.True(double.IsNaN(result.Left));
        Assert.Equal(10, result.Top);
    }
}

public class UiMotionTests
{
    [Fact]
    public void AlwaysOffIsInstant()
    {
        var motion = UiMotion.Resolve(MotionPreference.AlwaysOff, windowsAllows: true);
        Assert.False(motion.Enabled);
        Assert.Equal(TimeSpan.Zero, motion.Duration);
        Assert.Equal(1, UiMotion.Opacity(motion, 0));
    }

    [Fact]
    public void AlwaysOnUsesTheFullMotionBudget()
    {
        var motion = UiMotion.Resolve(MotionPreference.AlwaysOn, windowsAllows: false);
        Assert.True(motion.Enabled);
        Assert.Equal(UiMotion.NormalDuration, motion.Duration);
    }

    [Fact]
    public void FollowWindowsUsesInstantTransitionsWhenWindowsRequestsReducedMotion()
    {
        var motion = UiMotion.Resolve(MotionPreference.FollowWindows, windowsAllows: false);
        Assert.False(motion.Enabled);
        Assert.Equal(TimeSpan.Zero, motion.Duration);
    }
}
