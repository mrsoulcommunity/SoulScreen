namespace SoulScreen.App.Logic;

/// <summary>
/// One phone's own settings, independent of every other phone's: accent, picture mapping,
/// and whether it always records. Layered onto <see cref="RecentDevice"/> rather than kept
/// apart, so a phone's profile follows the same history entry its session count does.
/// <para>
/// A phone connecting for the first time gets a fresh, default profile - not because a
/// blank profile means anything, but because there is nothing yet to remember. A phone
/// recognised from before loads exactly what it left: the profile is read, never guessed at.
/// </para>
/// Deliberately free of WPF so the lookup and the merge can both be tested on their own.
/// </summary>
public static class DeviceProfiles
{
    /// <summary>The profile fields, apart from what identifies the phone or counts its
    /// sessions - what a session start actually applies to the window.</summary>
    public readonly record struct Profile(
        AccentColor Accent, VideoFit VideoFit, int Rotation, bool MirrorHorizontally, bool AutoRecord)
    {
        public static Profile Default { get; } = new(AccentColor.Blue, VideoFit.Fit, 0, false, false);
    }

    /// <summary>
    /// The profile for a phone by name and model - the same identity <see cref="DeviceTrust"/>
    /// matches with. A phone never seen before gets <see cref="Profile.Default"/>: there is
    /// no history entry yet to have opinions of its own.
    /// </summary>
    public static Profile Resolve(IEnumerable<RecentDevice> history, string name, string? model)
    {
        var device = Find(history, name, model);
        return device is null
            ? Profile.Default
            : new Profile(device.Accent, device.VideoFit, device.Rotation, device.MirrorHorizontally, device.AutoRecord);
    }

    /// <summary>True when this phone has mirrored before and so already has a profile of
    /// its own, as opposed to one just built as the default.</summary>
    public static bool IsKnown(IEnumerable<RecentDevice> history, string name, string? model) =>
        Find(history, name, model) is not null;

    /// <summary>Writes <paramref name="profile"/> onto this phone's history entry, creating
    /// one first if this is a phone never seen before. Mirrors how <see
    /// cref="AppSettings.RememberDevice"/> finds or creates an entry, so the two never
    /// disagree about which entry is "this phone's".</summary>
    public static void Save(List<RecentDevice> history, string name, string? model, Profile profile)
    {
        if (string.IsNullOrWhiteSpace(name)) return;

        var device = Find(history, name, model);
        if (device is null)
        {
            device = new RecentDevice { Name = name, Model = model };
            history.Add(device);
        }

        device.Accent = profile.Accent;
        device.VideoFit = profile.VideoFit;
        device.Rotation = profile.Rotation;
        device.MirrorHorizontally = profile.MirrorHorizontally;
        device.AutoRecord = profile.AutoRecord;
    }

    private static RecentDevice? Find(IEnumerable<RecentDevice> history, string name, string? model) =>
        history.FirstOrDefault(d =>
            string.Equals(d.Name, name, StringComparison.Ordinal)
            && string.Equals(NormaliseModel(d.Model), NormaliseModel(model), StringComparison.OrdinalIgnoreCase));

    private static string NormaliseModel(string? model) => string.IsNullOrWhiteSpace(model) ? string.Empty : model.Trim();
}
