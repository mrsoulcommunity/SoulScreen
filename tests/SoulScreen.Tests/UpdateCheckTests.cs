using SoulScreen.App.Logic;

namespace SoulScreen.Tests;

/// <summary>
/// The decisions behind checking GitHub for a newer SoulScreen release: parsing a tag into a
/// comparable version, deciding whether a background check is due, whether a release counts
/// as "available" given what has been skipped, and which asset in a release is the Windows
/// build. None of this touches the network; <c>UpdateService</c> is the half that does.
/// </summary>
public class UpdateVersionTests
{
    [Theory]
    [InlineData("v1.2.0", 1, 2, 0)]
    [InlineData("1.2.0", 1, 2, 0)]
    [InlineData("V2.0.0", 2, 0, 0)]
    [InlineData("1.2", 1, 2, 0)]
    [InlineData("3", 3, 0, 0)]
    [InlineData("v1.2.0-beta.1", 1, 2, 0)]
    [InlineData("v1.2.0+abcdef", 1, 2, 0)]
    public void ParsesTheNumericPrefix(string text, int major, int minor, int patch)
    {
        Assert.True(UpdateVersion.TryParse(text, out var version));
        Assert.Equal(new UpdateVersion(major, minor, patch), version);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("latest")]
    [InlineData("vNext")]
    public void RejectsAnythingWithNoLeadingNumber(string? text)
    {
        Assert.False(UpdateVersion.TryParse(text, out _));
    }

    [Fact]
    public void ComparesByMajorThenMinorThenPatch()
    {
        Assert.True(new UpdateVersion(2, 0, 0).IsNewerThan(new UpdateVersion(1, 9, 9)));
        Assert.True(new UpdateVersion(1, 2, 0).IsNewerThan(new UpdateVersion(1, 1, 9)));
        Assert.True(new UpdateVersion(1, 1, 2).IsNewerThan(new UpdateVersion(1, 1, 1)));
        Assert.False(new UpdateVersion(1, 1, 1).IsNewerThan(new UpdateVersion(1, 1, 1)));
    }

    [Fact]
    public void FromAssemblyVersionClampsAnUnsetField()
    {
        var version = UpdateVersion.FromAssemblyVersion(new Version(1, 1, 0));
        Assert.Equal(new UpdateVersion(1, 1, 0), version);

        // Version(1,1) leaves Build unset, which reads back as -1.
        var partial = UpdateVersion.FromAssemblyVersion(new Version(1, 1));
        Assert.Equal(new UpdateVersion(1, 1, 0), partial);

        Assert.Equal(default, UpdateVersion.FromAssemblyVersion(null));
    }

    [Fact]
    public void ToStringIsAlwaysThreeParts()
    {
        Assert.Equal("1.2.0", new UpdateVersion(1, 2, 0).ToString());
    }
}

public class UpdatePolicyTests
{
    [Fact]
    public void ACheckIsDueWhenNeverRunBefore()
    {
        Assert.True(UpdatePolicy.IsCheckDue(null, DateTime.UtcNow, TimeSpan.FromHours(6)));
    }

    [Fact]
    public void ACheckIsNotDueBeforeTheInterval()
    {
        var last = DateTime.UtcNow.AddHours(-1);
        Assert.False(UpdatePolicy.IsCheckDue(last, DateTime.UtcNow, TimeSpan.FromHours(6)));
    }

    [Fact]
    public void ACheckIsDueOnceTheIntervalHasPassed()
    {
        var last = DateTime.UtcNow.AddHours(-7);
        Assert.True(UpdatePolicy.IsCheckDue(last, DateTime.UtcNow, TimeSpan.FromHours(6)));
    }

    [Fact]
    public void ANewerReleaseIsAvailable()
    {
        Assert.True(UpdatePolicy.IsUpdateAvailable(
            new UpdateVersion(1, 1, 0), new UpdateVersion(1, 2, 0), "v1.2.0", null));
    }

    [Fact]
    public void TheSameOrOlderReleaseIsNotAvailable()
    {
        Assert.False(UpdatePolicy.IsUpdateAvailable(
            new UpdateVersion(1, 1, 0), new UpdateVersion(1, 1, 0), "v1.1.0", null));
        Assert.False(UpdatePolicy.IsUpdateAvailable(
            new UpdateVersion(1, 2, 0), new UpdateVersion(1, 1, 0), "v1.1.0", null));
    }

    [Fact]
    public void ASkippedTagIsNotOfferedAgain()
    {
        Assert.False(UpdatePolicy.IsUpdateAvailable(
            new UpdateVersion(1, 1, 0), new UpdateVersion(1, 2, 0), "v1.2.0", "v1.2.0"));
    }

    [Fact]
    public void SkippingOneVersionDoesNotSilenceTheNext()
    {
        Assert.True(UpdatePolicy.IsUpdateAvailable(
            new UpdateVersion(1, 1, 0), new UpdateVersion(1, 3, 0), "v1.3.0", "v1.2.0"));
    }

    [Fact]
    public void PicksTheWindowsAssetByName()
    {
        var names = new[] { "SoulScreen-1.2.0-win-x64.zip", "source-code.tar.gz" };
        Assert.Equal("SoulScreen-1.2.0-win-x64.zip", UpdatePolicy.SelectWindowsAssetName(names));
    }

    [Fact]
    public void FallsBackToTheOnlyZipWhenNothingNamesThePlatform()
    {
        var names = new[] { "SoulScreen-1.2.0.zip" };
        Assert.Equal("SoulScreen-1.2.0.zip", UpdatePolicy.SelectWindowsAssetName(names));
    }

    [Fact]
    public void PicksNothingWhenSeveralZipsAreAmbiguous()
    {
        var names = new[] { "SoulScreen-mac.zip", "SoulScreen-linux.zip" };
        Assert.Null(UpdatePolicy.SelectWindowsAssetName(names));
    }

    [Theory]
    [InlineData("sha256:ABCDEF", "sha256", "ABCDEF")]
    [InlineData("SHA256:1234", "sha256", "1234")]
    public void ParsesADigest(string digest, string algorithm, string hex)
    {
        var parsed = UpdatePolicy.ParseDigest(digest);
        Assert.NotNull(parsed);
        Assert.Equal(algorithm, parsed!.Value.Algorithm);
        Assert.Equal(hex, parsed.Value.Hex);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("nocolon")]
    [InlineData("sha256:")]
    public void RejectsAMalformedDigest(string? digest)
    {
        Assert.Null(UpdatePolicy.ParseDigest(digest));
    }
}
