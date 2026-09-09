namespace SoulScreen.Core.Media;

/// <summary>Video codec carried by a mirror source. Both AirPlay and the iOS USB
/// (QuickTime) transport deliver H.264; HEVC is reserved for future AirPlay 2 senders.</summary>
public enum VideoCodec
{
    Unknown = 0,
    H264 = 1,
    Hevc = 2,
}

/// <summary>
/// Stream-level video description. Emitted once when the sender announces its codec
/// configuration and again whenever the phone rotates or changes resolution.
/// </summary>
/// <param name="Codec">Codec of the elementary stream.</param>
/// <param name="Width">Coded width in pixels, or 0 when the sender did not announce one.</param>
/// <param name="Height">Coded height in pixels, or 0 when the sender did not announce one.</param>
/// <param name="ParameterSets">
/// Codec configuration in Annex-B form (SPS/PPS prefixed with 00 00 00 01 start codes),
/// ready to be prepended to the first keyframe handed to a decoder.
/// </param>
/// <param name="FrameRate">Nominal frame rate hint, 0 when unknown.</param>
public readonly record struct VideoFormat(
    VideoCodec Codec,
    int Width,
    int Height,
    byte[] ParameterSets,
    double FrameRate)
{
    public bool IsValid => Codec != VideoCodec.Unknown && ParameterSets.Length > 0;

    public override string ToString() =>
        $"{Codec} {Width}x{Height}{(FrameRate > 0 ? $"@{FrameRate:0.##}" : "")} (cfg {ParameterSets.Length}B)";
}
