using SoulScreen.App.Logic;

namespace SoulScreen.Tests;

/// <summary>
/// When a session that has just lost its phone is held open, and which phone counts as the
/// same one coming back. The rules are pure, so they are checked here rather than by watching
/// a phone drop out of range.
/// </summary>
public class ReconnectPolicyTests
{
    private static readonly DateTime Moment = new(2026, 9, 15, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void WaitsForThePhoneThatNamedItself()
    {
        var policy = new ReconnectPolicy();

        Assert.True(policy.Hold("Kasra's iPhone", "iPhone18,1", Moment));
        Assert.True(policy.IsHolding);
        Assert.Equal("Kasra's iPhone", policy.DeviceName);
        Assert.Equal(Moment + ReconnectPolicy.DefaultGrace, policy.DeadlineUtc);
        Assert.Equal(TimeSpan.FromMinutes(1), ReconnectPolicy.DefaultGrace);
    }

    [Fact]
    public void RefusesToWaitForAPhoneThatNeverNamedItself()
    {
        var policy = new ReconnectPolicy();

        Assert.False(policy.Hold("  ", null, Moment));
        Assert.False(policy.IsHolding);
    }

    [Fact]
    public void TheSamePhoneIsRecognisedComingBack()
    {
        var policy = new ReconnectPolicy();
        policy.Hold("Kasra's iPhone", "iPhone18,1", Moment);

        Assert.True(policy.IsReturning("Kasra's iPhone", "iPhone18,1"));
        // The phone spells its own name; case is the sender's business, not the match's.
        Assert.True(policy.IsReturning("KASRA'S IPHONE", "iphone18,1"));
    }

    [Fact]
    public void APhoneThatStayedQuietAboutItsModelStillMatches()
    {
        var holding = new ReconnectPolicy();
        holding.Hold("Kasra's iPhone", null, Moment);
        Assert.True(holding.IsReturning("Kasra's iPhone", "iPhone18,1"));

        var returning = new ReconnectPolicy();
        returning.Hold("Kasra's iPhone", "iPhone18,1", Moment);
        Assert.True(returning.IsReturning("Kasra's iPhone", null));
    }

    [Fact]
    public void AnotherPhoneIsNotTheOneBeingWaitedFor()
    {
        var policy = new ReconnectPolicy();
        policy.Hold("Kasra's iPhone", "iPhone18,1", Moment);

        Assert.False(policy.IsReturning("The other iPhone", "iPhone18,1"));
        // The same name, a different device: two phones can be called the same thing.
        Assert.False(policy.IsReturning("Kasra's iPhone", "iPad14,1"));
        Assert.False(policy.IsReturning(null, null));
    }

    [Fact]
    public void NothingIsAReconnectionWhileNothingIsHeld()
    {
        var policy = new ReconnectPolicy();

        Assert.False(policy.IsReturning("Kasra's iPhone", "iPhone18,1"));
        Assert.False(policy.HasExpired(Moment + TimeSpan.FromHours(1)));
    }

    [Fact]
    public void TheWaitRunsOutAfterTheGrace()
    {
        var policy = new ReconnectPolicy(TimeSpan.FromSeconds(30));
        policy.Hold("Kasra's iPhone", "iPhone18,1", Moment);

        Assert.False(policy.HasExpired(Moment + TimeSpan.FromSeconds(29)));
        Assert.True(policy.HasExpired(Moment + TimeSpan.FromSeconds(30)));
        Assert.True(policy.HasExpired(Moment + TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void ComingBackInsideTheGraceStillCountsAfterTheWaitWouldHaveEnded()
    {
        // The window is what ends a hold; a phone that arrived in time is matched on identity
        // alone, so a late tick can never turn a reconnection into a stranger.
        var policy = new ReconnectPolicy(TimeSpan.FromSeconds(30));
        policy.Hold("Kasra's iPhone", "iPhone18,1", Moment);

        Assert.True(policy.IsReturning("Kasra's iPhone", "iPhone18,1"));
        Assert.True(policy.HasExpired(Moment + TimeSpan.FromSeconds(31)));
    }

    [Fact]
    public void ClearingForgetsThePhoneAndTheDeadline()
    {
        var policy = new ReconnectPolicy();
        policy.Hold("Kasra's iPhone", "iPhone18,1", Moment);

        policy.Clear();

        Assert.False(policy.IsHolding);
        Assert.Null(policy.DeviceName);
        Assert.Equal(default, policy.DeadlineUtc);
        Assert.False(policy.IsReturning("Kasra's iPhone", "iPhone18,1"));
    }

    [Fact]
    public void HoldingAgainMovesTheDeadline()
    {
        var policy = new ReconnectPolicy(TimeSpan.FromSeconds(30));
        policy.Hold("Kasra's iPhone", "iPhone18,1", Moment);
        var later = Moment + TimeSpan.FromSeconds(10);

        policy.Hold("Kasra's iPhone", "iPhone18,1", later);

        Assert.Equal(later + TimeSpan.FromSeconds(30), policy.DeadlineUtc);
        Assert.False(policy.HasExpired(later + TimeSpan.FromSeconds(20)));
    }

    [Fact]
    public void TheAddressIsNotWhatIdentifiesAPhone()
    {
        // Nothing about matching takes an address, which is the point: a phone that dropped and
        // joined another network comes back from a different one, and that is usually why.
        Assert.True(ReconnectPolicy.SameDevice("Kasra's iPhone", "iPhone18,1", "kasra's iphone", "iPhone18,1"));
        Assert.False(ReconnectPolicy.SameDevice(null, null, "Kasra's iPhone", "iPhone18,1"));
        Assert.False(ReconnectPolicy.SameDevice("Kasra's iPhone", null, null, null));
    }
}
