namespace SoulScreen.Core.Media;

/// <summary>Audio codecs an iOS device can hand us.</summary>
public enum AudioCodec
{
    Unknown = 0,
    /// <summary>AAC Enhanced Low Delay - what AirPlay screen mirroring uses.</summary>
    AacEld = 1,
    /// <summary>AAC-LC, used by some AirPlay audio-only senders.</summary>
    AacLc = 2,
    /// <summary>Apple Lossless, the classic AirPlay (RAOP) audio codec.</summary>
    Alac = 3,
    /// <summary>Uncompressed little-endian 16-bit PCM (USB transport, and ALAC once decoded).</summary>
    Pcm16 = 4,
}

/// <param name="Codec">Codec of the audio payloads that follow.</param>
/// <param name="SampleRate">Sample rate in Hz.</param>
/// <param name="Channels">Channel count.</param>
/// <param name="FramesPerPacket">Samples per channel in one compressed packet (480 for AAC-ELD).</param>
/// <param name="MagicCookie">Codec-specific setup blob (AudioSpecificConfig for AAC), may be empty.</param>
public readonly record struct AudioFormat(
    AudioCodec Codec,
    int SampleRate,
    int Channels,
    int FramesPerPacket,
    byte[] MagicCookie)
{
    public static readonly AudioFormat None = new(AudioCodec.Unknown, 0, 0, 0, []);

    public bool IsValid => Codec != AudioCodec.Unknown && SampleRate > 0 && Channels > 0;

    public override string ToString() => $"{Codec} {SampleRate}Hz x{Channels}";
}
