using SoulScreen.App.Logic;

namespace SoulScreen.Tests;

/// <summary>The PIN lock: hashing (never storing the PIN itself) and the backoff that
/// discourages guessing without punishing an honest mistake.</summary>
public class AppLockTests
{
    [Theory]
    [InlineData("1234", true)]
    [InlineData("12345678", true)]
    [InlineData("123", false)] // too short
    [InlineData("123456789", false)] // too long
    [InlineData("12a4", false)] // not all digits
    [InlineData("", false)]
    [InlineData(null, false)]
    public void ValidatesPinShape(string? pin, bool expected)
    {
        Assert.Equal(expected, AppLock.IsValidPin(pin));
    }

    [Fact]
    public void ARoundTrippedPinVerifies()
    {
        var (salt, hash) = AppLock.Hash("4269", iterations: 1000);
        Assert.True(AppLock.Verify("4269", salt, hash, 1000));
    }

    [Fact]
    public void AWrongPinDoesNotVerify()
    {
        var (salt, hash) = AppLock.Hash("4269", iterations: 1000);
        Assert.False(AppLock.Verify("1111", salt, hash, 1000));
    }

    [Fact]
    public void TwoHashesOfTheSamePinDifferByTheirSalt()
    {
        var (saltA, hashA) = AppLock.Hash("4269", iterations: 1000);
        var (saltB, hashB) = AppLock.Hash("4269", iterations: 1000);
        Assert.NotEqual(saltA, saltB);
        Assert.NotEqual(hashA, hashB);
    }

    [Fact]
    public void ACorruptedSettingsFileFailsClosedRatherThanThrowing()
    {
        Assert.False(AppLock.Verify("4269", "not valid base64!!", "also not base64!!", 1000));
    }

    [Fact]
    public void AZeroOrNegativeIterationCountFallsBackToTheDefault()
    {
        var (salt, hash) = AppLock.Hash("4269", AppLock.DefaultIterations);
        Assert.True(AppLock.Verify("4269", salt, hash, 0));
        Assert.True(AppLock.Verify("4269", salt, hash, -1));
    }
}

public class LockoutPolicyTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void TheFirstThreeFailuresImposeNoWait(int failures)
    {
        Assert.Equal(TimeSpan.Zero, LockoutPolicy.DelayAfter(failures));
    }

    [Theory]
    [InlineData(4, 5)]
    [InlineData(5, 10)]
    [InlineData(6, 20)]
    [InlineData(7, 40)]
    public void EachFailureAboveThreeDoublesTheWait(int failures, double expectedSeconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), LockoutPolicy.DelayAfter(failures));
    }

    [Fact]
    public void TheWaitNeverExceedsTheCap()
    {
        Assert.Equal(LockoutPolicy.MaxDelay, LockoutPolicy.DelayAfter(100));
    }

    [Fact]
    public void HasElapsedFollowsTheDeadline()
    {
        var lockedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var delay = TimeSpan.FromSeconds(10);
        Assert.False(LockoutPolicy.HasElapsed(lockedAt, delay, lockedAt + TimeSpan.FromSeconds(9)));
        Assert.True(LockoutPolicy.HasElapsed(lockedAt, delay, lockedAt + TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void RemainingCountsDownToZeroAndNoFurther()
    {
        var lockedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var delay = TimeSpan.FromSeconds(10);
        Assert.Equal(TimeSpan.FromSeconds(4), LockoutPolicy.Remaining(lockedAt, delay, lockedAt + TimeSpan.FromSeconds(6)));
        Assert.Equal(TimeSpan.Zero, LockoutPolicy.Remaining(lockedAt, delay, lockedAt + TimeSpan.FromSeconds(30)));
    }
}
