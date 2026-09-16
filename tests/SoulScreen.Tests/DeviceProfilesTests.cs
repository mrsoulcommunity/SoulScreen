using SoulScreen.App;
using SoulScreen.App.Logic;

namespace SoulScreen.Tests;

/// <summary>
/// Each known iPhone's own settings: accent, picture mapping and auto-record, layered onto
/// its history entry so a phone recognised again loads exactly what it left, and a phone
/// never seen before gets a fresh default rather than borrowing another phone's choices.
/// </summary>
public class DeviceProfilesTests
{
    private static List<RecentDevice> History() =>
    [
        new()
        {
            Name = "Kasra's iPhone", Model = "iPhone18,1",
            Accent = AccentColor.Purple, VideoFit = VideoFit.Fill, Rotation = 90,
            MirrorHorizontally = true, AutoRecord = true,
        },
    ];

    [Fact]
    public void AKnownPhoneLoadsExactlyWhatItLeft()
    {
        var profile = DeviceProfiles.Resolve(History(), "Kasra's iPhone", "iPhone18,1");
        Assert.Equal(AccentColor.Purple, profile.Accent);
        Assert.Equal(VideoFit.Fill, profile.VideoFit);
        Assert.Equal(90, profile.Rotation);
        Assert.True(profile.MirrorHorizontally);
        Assert.True(profile.AutoRecord);
    }

    /// <summary>A phone never seen before has nothing to remember, so it gets the
    /// defaults - not another phone's settings, and not a crash.</summary>
    [Fact]
    public void AnUnknownPhoneGetsTheDefaultProfile()
    {
        var profile = DeviceProfiles.Resolve(History(), "Guest's iPhone", "iPhone15,2");
        Assert.Equal(DeviceProfiles.Profile.Default, profile);
        Assert.False(DeviceProfiles.IsKnown(History(), "Guest's iPhone", "iPhone15,2"));
    }

    [Fact]
    public void AnotherModelWithTheSameNameIsAnotherPhonesProfile()
    {
        // Same identity rule DeviceTrust uses: name and model together are the phone.
        Assert.False(DeviceProfiles.IsKnown(History(), "Kasra's iPhone", "iPhone16,1"));
    }

    [Fact]
    public void SaveCreatesAnEntryForAPhoneNeverSeenBefore()
    {
        var history = new List<RecentDevice>();
        DeviceProfiles.Save(history, "New Phone", "iPhone14,5",
            new DeviceProfiles.Profile(AccentColor.Green, VideoFit.Actual, 180, false, true));

        Assert.Single(history);
        Assert.True(DeviceProfiles.IsKnown(history, "New Phone", "iPhone14,5"));
        var profile = DeviceProfiles.Resolve(history, "New Phone", "iPhone14,5");
        Assert.Equal(AccentColor.Green, profile.Accent);
        Assert.Equal(180, profile.Rotation);
        Assert.True(profile.AutoRecord);
    }

    [Fact]
    public void SaveOverwritesAnExistingPhonesProfileWithoutDuplicatingIt()
    {
        var history = History();
        DeviceProfiles.Save(history, "Kasra's iPhone", "iPhone18,1",
            DeviceProfiles.Profile.Default with { AutoRecord = false });

        Assert.Single(history);
        var profile = DeviceProfiles.Resolve(history, "Kasra's iPhone", "iPhone18,1");
        Assert.Equal(DeviceProfiles.Profile.Default.Accent, profile.Accent);
        Assert.False(profile.AutoRecord);
    }

    /// <summary>Saving does not disturb the session bookkeeping fields on the same entry -
    /// a device profile write and RememberDevice share one record, not two.</summary>
    [Fact]
    public void SavingAProfileLeavesTheSessionHistoryAlone()
    {
        var history = History();
        history[0].SessionCount = 7;
        history[0].TotalSeconds = 1234;

        DeviceProfiles.Save(history, "Kasra's iPhone", "iPhone18,1", DeviceProfiles.Profile.Default);

        Assert.Equal(7, history[0].SessionCount);
        Assert.Equal(1234, history[0].TotalSeconds);
    }

    [Fact]
    public void SaveIgnoresABlankName()
    {
        var history = new List<RecentDevice>();
        DeviceProfiles.Save(history, "   ", null, DeviceProfiles.Profile.Default);
        Assert.Empty(history);
    }
}
