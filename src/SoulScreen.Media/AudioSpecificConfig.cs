using SoulScreen.Core.Media;

namespace SoulScreen.Media;

/// <summary>
/// Builds the MPEG-4 AudioSpecificConfig an AAC decoder needs before it can read a single
/// packet.
/// <para>
/// AirPlay never sends one. The stream description in SETUP names a compression type and a
/// samples-per-frame count and leaves the receiver to reconstruct the rest, so this encodes
/// what those fields imply. Getting it wrong is silent: the decoder opens happily and then
/// rejects every packet.
/// </para>
/// </summary>
public static class AudioSpecificConfig
{
    /// <summary>Object type for Error Resilient AAC Enhanced Low Delay.</summary>
    private const int AacEldObjectType = 39;

    /// <summary>Object type for AAC Low Complexity.</summary>
    private const int AacLcObjectType = 2;

    /// <summary>Object types at or above 31 use a 5-bit escape followed by a 6-bit value.</summary>
    private const int ObjectTypeEscape = 31;

    private static readonly int[] SampleRates =
    [
        96000, 88200, 64000, 48000, 44100, 32000,
        24000, 22050, 16000, 12000, 11025, 8000, 7350,
    ];

    /// <summary>
    /// Encodes a config for the given format, or an empty array for codecs that do not use
    /// one.
    /// </summary>
    public static byte[] Build(AudioFormat format) => format.Codec switch
    {
        AudioCodec.AacEld => BuildAacEld(format.SampleRate, format.Channels, format.FramesPerPacket),
        AudioCodec.AacLc => BuildAacLc(format.SampleRate, format.Channels),
        _ => [],
    };

    /// <summary>
    /// AAC-LC: object type, sample rate index, channel configuration, and a
    /// GASpecificConfig whose three flags are all zero.
    /// </summary>
    public static byte[] BuildAacLc(int sampleRate, int channels)
    {
        var writer = new BitWriter();
        writer.Write(AacLcObjectType, 5);
        writer.Write(SampleRateIndex(sampleRate), 4);
        writer.Write(channels, 4);
        writer.Write(0, 1); // frameLengthFlag: 1024 samples
        writer.Write(0, 1); // dependsOnCoreCoder
        writer.Write(0, 1); // extensionFlag
        return writer.ToArray();
    }

    /// <summary>
    /// AAC-ELD: the escaped object type, then an ELDSpecificConfig. The frame length flag
    /// is the field that matters most here - mirroring sends 480-sample frames, and a
    /// decoder told to expect 512 rejects all of them.
    /// </summary>
    public static byte[] BuildAacEld(int sampleRate, int channels, int framesPerPacket)
    {
        var writer = new BitWriter();
        writer.Write(ObjectTypeEscape, 5);
        writer.Write(AacEldObjectType - 32, 6);
        writer.Write(SampleRateIndex(sampleRate), 4);
        writer.Write(channels, 4);

        // ELDSpecificConfig
        writer.Write(framesPerPacket == 512 ? 0 : 1, 1); // frameLengthFlag: 1 selects 480
        writer.Write(0, 1); // aacSectionDataResilienceFlag
        writer.Write(0, 1); // aacScalefactorDataResilienceFlag
        writer.Write(0, 1); // aacSpectralDataResilienceFlag
        writer.Write(0, 1); // ldSbrPresentFlag
        writer.Write(0, 4); // ELDEXT_TERM - no extensions follow

        return writer.ToArray();
    }

    private static int SampleRateIndex(int sampleRate)
    {
        var index = Array.IndexOf(SampleRates, sampleRate);
        if (index >= 0) return index;
        // 15 is the escape that would introduce an explicit 24-bit rate, which none of the
        // AirPlay formats need; fall back to 44.1 kHz rather than emit a config we cannot
        // finish writing.
        return Array.IndexOf(SampleRates, 44100);
    }

    /// <summary>Most-significant-bit-first writer; AudioSpecificConfig is not byte aligned.</summary>
    private sealed class BitWriter
    {
        private readonly List<byte> _bytes = [];
        private int _current;
        private int _bitsUsed;

        public void Write(int value, int bitCount)
        {
            for (var i = bitCount - 1; i >= 0; i--)
            {
                _current = (_current << 1) | ((value >> i) & 1);
                if (++_bitsUsed != 8) continue;
                _bytes.Add((byte)_current);
                _current = 0;
                _bitsUsed = 0;
            }
        }

        public byte[] ToArray()
        {
            if (_bitsUsed == 0) return [.. _bytes];
            // Pad the final byte with zeros, which is what the specification requires.
            var padded = new List<byte>(_bytes) { (byte)(_current << (8 - _bitsUsed)) };
            return [.. padded];
        }
    }
}
