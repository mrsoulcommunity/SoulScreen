using SoulScreen.App.Logic;

namespace SoulScreen.Tests;

/// <summary>
/// Validates a precise in/out clip selection before it reaches the exporter: unlike a single
/// length slider, an in/out pair can describe something genuinely wrong - in after out, or
/// either point outside the recording - and that deserves to be said before the export runs.
/// </summary>
public class ClipRangeTests
{
    private static readonly TimeSpan MinLength = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan MaxLength = TimeSpan.FromSeconds(30);

    private static ClipRangeResult Validate(double startSeconds, double endSeconds, double? totalSeconds = 60) =>
        ClipRange.Validate(
            TimeSpan.FromSeconds(startSeconds), TimeSpan.FromSeconds(endSeconds),
            totalSeconds is { } t ? TimeSpan.FromSeconds(t) : null, MinLength, MaxLength);

    [Fact]
    public void AValidRangeInsideTheClipIsAccepted()
    {
        var result = Validate(5, 12);
        Assert.True(result.IsValid);
        Assert.Null(result.Error);
        Assert.Equal(TimeSpan.FromSeconds(5), result.Start);
        Assert.Equal(TimeSpan.FromSeconds(7), result.Duration);
    }

    [Fact]
    public void InAfterOutIsRejected()
    {
        var result = Validate(10, 5);
        Assert.False(result.IsValid);
        Assert.Contains("out point", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InEqualToOutIsRejected()
    {
        Assert.False(Validate(5, 5).IsValid);
    }

    [Fact]
    public void ANegativeInPointIsRejected()
    {
        var result = Validate(-1, 5);
        Assert.False(result.IsValid);
        Assert.Contains("in point", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnOutPointPastTheEndOfTheClipIsRejected()
    {
        var result = Validate(5, 65, totalSeconds: 60);
        Assert.False(result.IsValid);
        Assert.Contains("out point", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnInPointPastTheEndOfTheClipIsRejected()
    {
        var result = Validate(65, 70, totalSeconds: 60);
        Assert.False(result.IsValid);
        Assert.Contains("in point", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Without a known total duration - the player has not reported one yet -
    /// the bound is not enforced rather than wrongly rejecting everything.</summary>
    [Fact]
    public void AnUnknownTotalDurationDoesNotBoundTheRange()
    {
        var result = Validate(5, 20, totalSeconds: null);
        Assert.True(result.IsValid);
    }

    [Fact]
    public void ARangeShorterThanTheMinimumIsRejected()
    {
        var result = Validate(5, 5.2, totalSeconds: 60);
        Assert.False(result.IsValid);
        Assert.Contains("at least", result.Error);
    }

    [Fact]
    public void ARangeLongerThanTheMaximumIsRejected()
    {
        var result = Validate(0, 45, totalSeconds: 60);
        Assert.False(result.IsValid);
        Assert.Contains("at most", result.Error);
    }

    // ------------------------------------------------------------ default range

    [Fact]
    public void TheDefaultRangeStartsWherePlaybackIs()
    {
        var (start, end) = ClipRange.DefaultRange(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(5));
        Assert.Equal(TimeSpan.FromSeconds(10), start);
        Assert.Equal(TimeSpan.FromSeconds(15), end);
    }

    /// <summary>Near the end of a known recording, the default range is pulled back so
    /// it still fits rather than running past the end.</summary>
    [Fact]
    public void TheDefaultRangeIsPulledBackNearTheEnd()
    {
        var (start, end) = ClipRange.DefaultRange(TimeSpan.FromSeconds(58), TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(5));
        Assert.Equal(TimeSpan.FromSeconds(58), start);
        Assert.Equal(TimeSpan.FromSeconds(60), end);
    }

    [Fact]
    public void TheDefaultRangeClampsAPositionPastTheEnd()
    {
        var (start, end) = ClipRange.DefaultRange(TimeSpan.FromSeconds(90), TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(5));
        Assert.Equal(TimeSpan.FromSeconds(60), start);
        Assert.Equal(TimeSpan.FromSeconds(60), end);
    }

    [Fact]
    public void TheDefaultRangeWithNoKnownTotalJustAddsTheLength()
    {
        var (start, end) = ClipRange.DefaultRange(TimeSpan.FromSeconds(3), null, TimeSpan.FromSeconds(5));
        Assert.Equal(TimeSpan.FromSeconds(3), start);
        Assert.Equal(TimeSpan.FromSeconds(8), end);
    }

    [Fact]
    public void ANegativePositionClampsToZero()
    {
        var (start, _) = ClipRange.DefaultRange(TimeSpan.FromSeconds(-5), TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(5));
        Assert.Equal(TimeSpan.Zero, start);
    }
}
