using SoulScreen.App.Logic;

namespace SoulScreen.Tests;

/// <summary>
/// The decisions behind the newer window features: who may mirror, where drawings go when the
/// picture moves, when a recording is stopped for lack of room, what the firewall will let
/// through, and where a dropped mini player settles.
/// </summary>
public class DeviceTrustTests
{
    private static List<DeviceKey> Keys(params (string Name, string? Model)[] phones) =>
        phones.Select(p => new DeviceKey { Name = p.Name, Model = p.Model }).ToList();

    [Fact]
    public void WithoutAskingEveryoneIsAllowed()
    {
        Assert.Equal(ConnectDecision.Allow, DeviceTrust.Decide(false, [], [], "Guest's iPhone", "iPhone15,2"));
    }

    [Fact]
    public void AskingHoldsBackAPhoneNotSeenBefore()
    {
        Assert.Equal(ConnectDecision.Ask, DeviceTrust.Decide(true, [], [], "Guest's iPhone", "iPhone15,2"));
    }

    [Fact]
    public void AnAllowedPhoneIsNotAskedAgain()
    {
        var allowed = Keys(("Kasra's iPhone", "iPhone18,1"));
        Assert.Equal(ConnectDecision.Allow, DeviceTrust.Decide(true, allowed, [], "Kasra's iPhone", "iPhone18,1"));
    }

    [Fact]
    public void ABlockHoldsEvenWhenAskingIsOff()
    {
        var blocked = Keys(("Guest's iPhone", null));
        Assert.Equal(ConnectDecision.Block, DeviceTrust.Decide(false, [], blocked, "Guest's iPhone", null));
    }

    [Fact]
    public void ABlockBeatsAnAllow()
    {
        var both = Keys(("Phone", "iPhone1,1"));
        Assert.Equal(ConnectDecision.Block, DeviceTrust.Decide(true, both, both, "Phone", "iPhone1,1"));
    }

    [Fact]
    public void AnotherModelWithTheSameNameIsAnotherPhone()
    {
        var allowed = Keys(("iPhone", "iPhone15,2"));
        Assert.Equal(ConnectDecision.Ask, DeviceTrust.Decide(true, allowed, [], "iPhone", "iPhone16,1"));
    }

    [Fact]
    public void ModelsIgnoreCaseAndBlanksMatchMissing()
    {
        Assert.True(DeviceTrust.Matches(new DeviceKey { Name = "Phone", Model = "IPHONE15,2" }, "Phone", "iphone15,2"));
        Assert.True(DeviceTrust.Matches(new DeviceKey { Name = "Phone", Model = "  " }, "Phone", null));
        Assert.False(DeviceTrust.Matches(new DeviceKey { Name = "Phone", Model = null }, "Phone", "iPhone15,2"));
    }

    [Fact]
    public void NamesAreComparedExactlyButTrimmed()
    {
        Assert.True(DeviceTrust.Matches(new DeviceKey { Name = " Phone " }, "Phone", null));
        Assert.False(DeviceTrust.Matches(new DeviceKey { Name = "phone" }, "Phone", null));
    }

    [Fact]
    public void AddingTwiceKeepsOneEntry()
    {
        var list = new List<DeviceKey>();
        Assert.True(DeviceTrust.Add(list, "Phone", "iPhone15,2"));
        Assert.False(DeviceTrust.Add(list, "Phone", "iphone15,2"));
        Assert.False(DeviceTrust.Add(list, "   ", null));
        Assert.Single(list);
    }

    [Fact]
    public void RemoveTakesEveryCopy()
    {
        var list = Keys(("Phone", null), ("Phone", null), ("Other", null));
        Assert.Equal(2, DeviceTrust.Remove(list, "Phone", null));
        Assert.Single(list);
    }

    [Fact]
    public void SanitiseDropsBlanksNullsAndRepeats()
    {
        var dirty = new List<DeviceKey>
        {
            new() { Name = "Phone", Model = "A" },
            null!,
            new() { Name = "  " },
            new() { Name = null! },
            new() { Name = "Phone", Model = "a" },
        };
        var clean = DeviceTrust.Sanitise(dirty);
        Assert.Single(clean);
        Assert.Empty(DeviceTrust.Sanitise(null));
    }
}

public class RectMappingTests
{
    [Fact]
    public void CarriesCornersOntoCorners()
    {
        var map = RectMapping.Between(new Bounds(10, 20, 100, 200), new Bounds(50, 0, 300, 100))!.Value;
        Assert.Equal((50.0, 0.0), map.Apply(10, 20));
        Assert.Equal((350.0, 100.0), map.Apply(110, 220));
        Assert.Equal((200.0, 50.0), map.Apply(60, 120));
    }

    [Fact]
    public void TheSameRectangleIsTheIdentity()
    {
        var rect = new Bounds(3, 4, 50, 60);
        Assert.True(RectMapping.Between(rect, rect)!.Value.IsIdentity);
        Assert.False(RectMapping.Between(rect, rect with { Left = 5 })!.Value.IsIdentity);
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(10, 0)]
    [InlineData(double.NaN, 10)]
    [InlineData(-5, 10)]
    public void AnEmptyRectangleHasNoMapping(double width, double height)
    {
        Assert.Null(RectMapping.Between(new Bounds(0, 0, width, height), new Bounds(0, 0, 10, 10)));
        Assert.Null(RectMapping.Between(new Bounds(0, 0, 10, 10), new Bounds(0, 0, width, height)));
    }
}

public class DiskSpaceTests
{
    [Fact]
    public void LevelsFollowTheThresholds()
    {
        Assert.Equal(DiskSpaceLevel.Plenty, DiskSpace.Classify(DiskSpace.LowBytes));
        Assert.Equal(DiskSpaceLevel.Low, DiskSpace.Classify(DiskSpace.LowBytes - 1));
        Assert.Equal(DiskSpaceLevel.Low, DiskSpace.Classify(DiskSpace.CriticalBytes));
        Assert.Equal(DiskSpaceLevel.Critical, DiskSpace.Classify(DiskSpace.CriticalBytes - 1));
        Assert.Equal(DiskSpaceLevel.Critical, DiskSpace.Classify(0));
    }

    [Fact]
    public void UnknownSpaceNeverStopsARecording()
    {
        Assert.Equal(DiskSpaceLevel.Plenty, DiskSpace.Classify(null));
        Assert.Equal(DiskSpaceLevel.Plenty, DiskSpace.Classify(-1));
    }

    [Fact]
    public void TimeLeftCountsDownToTheStopPoint()
    {
        var left = DiskSpace.TimeLeft(DiskSpace.CriticalBytes + 60_000_000, 1_000_000)!.Value;
        Assert.Equal(60, left.TotalSeconds, 3);
        Assert.Equal(TimeSpan.Zero, DiskSpace.TimeLeft(0, 1_000_000));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void AnUnknownRateHasNoEstimate(double rate)
    {
        Assert.Null(DiskSpace.TimeLeft(DiskSpace.LowBytes, rate));
    }

    [Theory]
    [InlineData(20, "less than a minute")]
    [InlineData(65, "about a minute")]
    [InlineData(40 * 60, "about 40 minutes")]
    [InlineData(3 * 3600 + 100, "about 3 hours")]
    public void TimeLeftReadsNaturally(double seconds, string expected)
    {
        Assert.Equal(expected, DiskSpace.DescribeTimeLeft(TimeSpan.FromSeconds(seconds)));
    }
}

public class FirewallRulesTests
{
    private const string App = @"C:\Apps\SoulScreen\SoulScreen.App.exe";
    private const int Private = FirewallRules.ProfilePrivate;
    private const int Public = FirewallRules.ProfilePublic;

    private static FirewallRule AppRule(bool allow, int protocol, int profiles = Private, bool enabled = true, string path = App) =>
        new("rule", Inbound: true, allow, enabled, profiles, protocol, "*", path);

    private static FirewallVerdict Evaluate(int active, params FirewallRule[] rules) =>
        FirewallRules.Evaluate(rules, active, enabledOnActiveProfiles: true, App, 7000);

    [Fact]
    public void TcpAndUdpForTheAppIsAllowed()
    {
        Assert.Equal(FirewallVerdict.Allowed,
            Evaluate(Private, AppRule(true, FirewallRules.ProtocolTcp), AppRule(true, FirewallRules.ProtocolUdp)));
        Assert.Equal(FirewallVerdict.Allowed, Evaluate(Private, AppRule(true, FirewallRules.ProtocolAny)));
    }

    [Fact]
    public void OnlyOneProtocolIsPartlyAllowed()
    {
        Assert.Equal(FirewallVerdict.PartlyAllowed, Evaluate(Private, AppRule(true, FirewallRules.ProtocolTcp)));
    }

    [Fact]
    public void TheBlockWindowsWritesOnADismissedPromptWins()
    {
        Assert.Equal(FirewallVerdict.Blocked,
            Evaluate(Private, AppRule(true, FirewallRules.ProtocolAny), AppRule(false, FirewallRules.ProtocolUdp)));
    }

    [Fact]
    public void RulesForAnotherNetworkProfileDoNotCount()
    {
        Assert.Equal(FirewallVerdict.NoRule, Evaluate(Public, AppRule(true, FirewallRules.ProtocolAny, profiles: Private)));
        Assert.Equal(FirewallVerdict.NoRule, Evaluate(Public, AppRule(false, FirewallRules.ProtocolAny, profiles: Private)));
    }

    [Fact]
    public void DisabledRulesAndOtherProgramsDoNotCount()
    {
        Assert.Equal(FirewallVerdict.NoRule, Evaluate(Private, AppRule(true, FirewallRules.ProtocolAny, enabled: false)));
        Assert.Equal(FirewallVerdict.NoRule, Evaluate(Private, AppRule(false, FirewallRules.ProtocolAny, path: @"C:\Other\other.exe")));
    }

    [Fact]
    public void OutboundRulesDoNotCount()
    {
        var outbound = AppRule(false, FirewallRules.ProtocolAny) with { Inbound = false };
        Assert.Equal(FirewallVerdict.NoRule, Evaluate(Private, outbound));
    }

    [Fact]
    public void APortRuleForAnyProgramOpensTheControlChannel()
    {
        var port = new FirewallRule("port", true, true, true, Private, FirewallRules.ProtocolTcp, "5000-5010, 7000", null);
        Assert.Equal(FirewallVerdict.PartlyAllowed, Evaluate(Private, port));
    }

    [Fact]
    public void ABlanketBlockOnAnotherPortIsNotReadAsBlockingTheApp()
    {
        var other = new FirewallRule("port", true, false, true, Private, FirewallRules.ProtocolTcp, "445", null);
        Assert.Equal(FirewallVerdict.Allowed, Evaluate(Private, other, AppRule(true, FirewallRules.ProtocolAny)));
    }

    [Fact]
    public void ARuleOpeningEveryPortForEveryProgramIsNotAboutTheApp()
    {
        // Windows carries many of these for Store apps; they made a PC with no SoulScreen rule
        // look half open.
        var blanket = new FirewallRule("store app", true, true, true, Private, FirewallRules.ProtocolAny, "*", null);
        Assert.Equal(FirewallVerdict.NoRule, Evaluate(Private, blanket));
        Assert.Equal(FirewallVerdict.NoRule, Evaluate(Private, blanket with { Allow = false }));
    }

    [Fact]
    public void RulesForAStoreAppOrAServiceAreIgnored()
    {
        var scoped = new FirewallRule("service", true, false, true, Private, FirewallRules.ProtocolTcp, "7000", null, ScopedToPackageOrService: true);
        Assert.Equal(FirewallVerdict.Allowed, Evaluate(Private, scoped, AppRule(true, FirewallRules.ProtocolAny)));
        Assert.Equal(FirewallVerdict.NoRule, Evaluate(Private, scoped with { Allow = true }));
    }

    [Fact]
    public void AFirewallThatIsOffLetsEverythingIn()
    {
        Assert.Equal(FirewallVerdict.Off,
            FirewallRules.Evaluate([AppRule(false, FirewallRules.ProtocolAny)], Private, enabledOnActiveProfiles: false, App, 7000));
    }

    [Fact]
    public void PathsCompareLikeWindowsDoes()
    {
        Assert.True(FirewallRules.SamePath(@"c:\apps\soulscreen\soulscreen.app.exe", App));
        Assert.True(FirewallRules.SamePath("\"C:\\Apps\\SoulScreen\\SoulScreen.App.exe\"", App));
        Assert.False(FirewallRules.SamePath(null, App));
    }

    [Theory]
    [InlineData("*", 7000, true)]
    [InlineData("", 7000, true)]
    [InlineData("7000", 7000, true)]
    [InlineData("6999-7001", 7000, true)]
    [InlineData("80,443", 7000, false)]
    [InlineData("RPC", 7000, false)]
    public void PortListsAreRead(string ports, int port, bool listed)
    {
        Assert.Equal(listed, FirewallRules.PortListed(ports, port));
    }

    [Theory]
    [InlineData(2, "private")]
    [InlineData(4, "public")]
    [InlineData(6, "private,public")]
    [InlineData(7, "domain,private,public")]
    [InlineData(0, "private")]
    public void ProfileNamesNameTheActiveNetworks(int mask, string expected)
    {
        Assert.Equal(expected, FirewallRules.ProfileNames(mask));
    }
}

public class MiniPlayerSnapTests
{
    private static readonly Bounds Screen = new(0, 0, 1920, 1040);
    private const double M = MiniPlayerGeometry.Margin;

    [Fact]
    public void NearAnEdgeItLinesUpWithTheMargin()
    {
        var snapped = MiniPlayerGeometry.Snap(new Bounds(30, 900 - 40, 200, 400), Screen);
        Assert.Equal(M, snapped.Left, 3);
        Assert.Equal(Screen.Bottom - M - 400, snapped.Top, 3);
    }

    [Fact]
    public void InTheMiddleItStaysWhereItWasPut()
    {
        var put = new Bounds(700, 300, 200, 400);
        Assert.Equal(put, MiniPlayerGeometry.Snap(put, Screen));
    }

    [Fact]
    public void PartlyOffTheScreenItIsBroughtBack()
    {
        var snapped = MiniPlayerGeometry.Snap(new Bounds(1850, -120, 200, 400), Screen);
        Assert.Equal(Screen.Right - M - 200, snapped.Left, 3);
        Assert.Equal(M, snapped.Top, 3);
    }

    [Fact]
    public void KeepsItsSize()
    {
        var snapped = MiniPlayerGeometry.Snap(new Bounds(5, 5, 321, 654), Screen);
        Assert.Equal(321, snapped.Width);
        Assert.Equal(654, snapped.Height);
    }

    [Fact]
    public void TooBigForTheMarginsItOnlyStaysOnScreen()
    {
        var small = new Bounds(0, 0, 300, 1040);
        var snapped = MiniPlayerGeometry.Snap(new Bounds(-50, 100, 290, 400), small);
        Assert.Equal(0, snapped.Left, 3);
    }
}
