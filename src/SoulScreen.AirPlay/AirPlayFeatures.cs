namespace SoulScreen.AirPlay;

/// <summary>
/// The AirPlay "features" bitmask advertised over mDNS. iOS decides what a receiver is
/// allowed to do purely from these bits, so getting them wrong is the difference between
/// appearing under Screen Mirroring and not appearing at all.
/// </summary>
/// <remarks>
/// The bit meanings come from the publicly reverse-engineered AirPlay documentation used
/// by RPiPlay, UxPlay and shairport-sync. Apple has never published them, so treat the
/// named bits as well-supported convention rather than specification. If a device refuses
/// to list this receiver, <see cref="AirPlayOptions.Features"/> is the first knob to turn.
/// </remarks>
[Flags]
public enum AirPlayFeatures : ulong
{
    None = 0,

    Video = 1UL << 0,
    Photo = 1UL << 1,
    VideoFairPlay = 1UL << 2,
    VideoVolumeControl = 1UL << 3,
    VideoHttpLiveStreams = 1UL << 4,
    Slideshow = 1UL << 5,

    /// <summary>Screen mirroring. Without this bit iOS will not offer the receiver at all.</summary>
    Screen = 1UL << 7,
    ScreenRotate = 1UL << 8,

    /// <summary>RAOP audio streaming.</summary>
    Audio = 1UL << 9,
    AudioRedundant = 1UL << 11,

    /// <summary>FairPlay SAP v2.5 with AES-GCM - what mirroring negotiates over /fp-setup.</summary>
    FairPlaySapV2p5 = 1UL << 14,
    PhotoCaching = 1UL << 15,

    Authentication4 = 1UL << 16,
    MetadataFeature1 = 1UL << 17,
    MetadataFeature2 = 1UL << 18,
    MetadataFeature0 = 1UL << 19,
    AudioFormat1 = 1UL << 20,
    AudioFormat2 = 1UL << 21,
    AudioFormat3 = 1UL << 22,
    AudioFormat4 = 1UL << 23,

    /// <summary>The classic (non-HomeKit) pair-setup / pair-verify exchange this receiver implements.</summary>
    LegacyPairing = 1UL << 27,

    RaopSupported = 1UL << 30,

    SupportsBufferedAudio = 1UL << 40,
    SupportsPtp = 1UL << 41,
    SupportsScreenMultiCodec = 1UL << 42,
    SupportsSystemPairing = 1UL << 43,

    /// <summary>
    /// Advertising HomeKit pairing would make iOS demand the HAP handshake instead of the
    /// legacy one. Listed for completeness; deliberately not part of any preset here.
    /// </summary>
    SupportsHomeKitPairingAndAccessControl = 1UL << 46,

    SupportsCoreUtilsPairingAndEncryption = 1UL << 48,
    SupportsUnifiedPairSetupAndMfi = 1UL << 51,
}

public static class AirPlayFeaturePresets
{
    /// <summary>
    /// Mirroring plus audio over the legacy pairing path. This is the profile modelled on
    /// what an Apple TV 3 advertises, which is the combination iOS still serves with a
    /// plain FairPlay handshake instead of HomeKit pairing.
    /// </summary>
    public const ulong MirroringWithAudio =
        (ulong)(AirPlayFeatures.Video
              | AirPlayFeatures.Photo
              | AirPlayFeatures.VideoFairPlay
              | AirPlayFeatures.VideoVolumeControl
              | AirPlayFeatures.VideoHttpLiveStreams
              | AirPlayFeatures.Slideshow
              | AirPlayFeatures.Screen
              | AirPlayFeatures.ScreenRotate
              | AirPlayFeatures.Audio
              | AirPlayFeatures.AudioRedundant
              | AirPlayFeatures.FairPlaySapV2p5
              | AirPlayFeatures.PhotoCaching
              | AirPlayFeatures.Authentication4
              | AirPlayFeatures.MetadataFeature0
              | AirPlayFeatures.MetadataFeature1
              | AirPlayFeatures.MetadataFeature2
              | AirPlayFeatures.AudioFormat1
              | AirPlayFeatures.AudioFormat2
              | AirPlayFeatures.AudioFormat3
              | AirPlayFeatures.AudioFormat4
              | AirPlayFeatures.LegacyPairing
              | AirPlayFeatures.RaopSupported);

    /// <summary>Same as <see cref="MirroringWithAudio"/> minus the audio bits, for when
    /// only the picture is wanted and the phone should keep playing sound locally.</summary>
    public const ulong MirroringVideoOnly =
        MirroringWithAudio & ~(ulong)(AirPlayFeatures.Audio | AirPlayFeatures.AudioRedundant);

    /// <summary>
    /// Formats the mask the way DNS-SD expects it: two 32-bit halves, low word first,
    /// e.g. <c>0x527FFEE6,0x0</c>.
    /// </summary>
    public static string Format(ulong features)
    {
        var low = (uint)(features & 0xFFFFFFFF);
        var high = (uint)(features >> 32);
        return $"0x{low:X},0x{high:X}";
    }

    /// <summary>Parses either a single "0x..." value or the two-word DNS-SD form.</summary>
    public static ulong Parse(string text)
    {
        var parts = text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return 0;
        var low = ParseWord(parts[0]);
        var high = parts.Length > 1 ? ParseWord(parts[1]) : 0;
        return ((ulong)high << 32) | low;

        static uint ParseWord(string word) => word.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? Convert.ToUInt32(word[2..], 16)
            : uint.Parse(word);
    }
}
