using SoulScreen.Core.Buffers;
using SoulScreen.Core.Media;
using SoulScreen.Media;
using Xunit;

namespace SoulScreen.Tests;

public class AudioSpecificConfigTests
{
    /// <summary>
    /// The config mirroring needs: ER AAC ELD, 44.1 kHz, stereo, 480-sample frames.
    /// <para>
    /// Laid out bit by bit: escape 11111, object type 39-32 as 000111, sample rate index 4
    /// as 0100, channel configuration 2 as 0010, frameLengthFlag 1 for 480 samples, four
    /// zero resilience and SBR flags, then ELDEXT_TERM. Twenty-eight bits, zero padded.
    /// </para>
    /// </summary>
    [Fact]
    public void EncodesAacEldForMirroring()
    {
        var config = AudioSpecificConfig.BuildAacEld(44100, 2, framesPerPacket: 480);
        Assert.Equal(Hex.Parse("f8e85000"), config);
    }

    [Fact]
    public void FrameLengthFlagFollowsThePacketSize()
    {
        var shortFrames = AudioSpecificConfig.BuildAacEld(44100, 2, framesPerPacket: 480);
        var longFrames = AudioSpecificConfig.BuildAacEld(44100, 2, framesPerPacket: 512);

        // Only the frameLengthFlag differs, and it is the first bit of the fourth nibble.
        Assert.NotEqual(shortFrames, longFrames);
        Assert.Equal(shortFrames[..2], longFrames[..2]);
    }

    /// <summary>AAC-LC 44.1 kHz stereo is the canonical two-byte 0x12 0x10.</summary>
    [Fact]
    public void EncodesAacLc()
    {
        Assert.Equal(Hex.Parse("1210"), AudioSpecificConfig.BuildAacLc(44100, 2));
    }

    /// <summary>
    /// The rate index lands in bits 6 to 9, so it shifts the second nibble of the first
    /// byte and the top of the second. 0x1190 for 48 kHz stereo and 0x1210 for 44.1 kHz are
    /// the values every AAC encoder emits for those settings.
    /// </summary>
    [Theory]
    [InlineData(48000, "1190")]
    [InlineData(44100, "1210")]
    [InlineData(32000, "1290")]
    public void PicksTheRightSampleRateIndex(int sampleRate, string expected)
    {
        Assert.Equal(Hex.Parse(expected), AudioSpecificConfig.BuildAacLc(sampleRate, 2));
    }

    [Fact]
    public void FallsBackWhenTheRateHasNoIndex()
    {
        // 44.1 kHz stands in rather than emitting the escape form, which would need a
        // 24-bit rate this writer does not produce.
        Assert.Equal(AudioSpecificConfig.BuildAacLc(44100, 2), AudioSpecificConfig.BuildAacLc(1234, 2));
    }

    [Fact]
    public void ReturnsNothingForCodecsWithoutAConfig()
    {
        Assert.Empty(AudioSpecificConfig.Build(new AudioFormat(AudioCodec.Alac, 44100, 2, 352, [])));
        Assert.Empty(AudioSpecificConfig.Build(new AudioFormat(AudioCodec.Pcm16, 44100, 2, 0, [])));
    }
}

public class AudioDecoderTests
{
    private const string SkipReason =
        "FFmpeg is not installed; run tools/fetch-ffmpeg.ps1 to enable the audio tests.";

    /// <summary>
    /// Opening the decoder is the real test of the config: FFmpeg parses the
    /// AudioSpecificConfig during avcodec_open2 and fails there if it does not describe a
    /// profile it can decode. A malformed config is otherwise silent - the decoder opens
    /// and then rejects every packet, which sounds exactly like no audio at all.
    /// </summary>
    [SkippableFact]
    public void OpensAnAacEldDecoderFromTheGeneratedConfig()
    {
        Skip.IfNot(FFmpegRuntime.IsAvailable, SkipReason);

        var format = new AudioFormat(AudioCodec.AacEld, 44100, 2, 480, []);
        using var decoder = new AudioDecoder(format);

        Assert.Equal(44100, decoder.OutputSampleRate);
        Assert.Equal(2, decoder.OutputChannels);
    }

    /// <summary>
    /// ALAC carries no in-band configuration, so it cannot be opened without the sender's
    /// magic cookie. Mirroring never asks for ALAC, so this is a documented gap rather than
    /// a defect - but the failure has to name the cause instead of surfacing an FFmpeg
    /// error code.
    /// </summary>
    [SkippableFact]
    public void ExplainsWhyAlacCannotOpenWithoutItsConfiguration()
    {
        Skip.IfNot(FFmpegRuntime.IsAvailable, SkipReason);

        var failure = Assert.Throws<FFmpegUnavailableException>(
            () => new AudioDecoder(new AudioFormat(AudioCodec.Alac, 44100, 2, 352, [])));

        Assert.Contains("codec configuration", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// iOS fills quiet stretches of a mirroring session with four-byte packets, 00 68 34 00,
    /// short enough to go out unencrypted. They are real AAC-ELD frames of silence a whole
    /// packet long - which matters, because playback keeps its timeline by what each packet
    /// decodes to, and a frame that decoded to nothing would read as a lost packet.
    /// </summary>
    [SkippableFact]
    public void DecodesTheSilenceFramesIosSendsToAWholePacketOfSilence()
    {
        Skip.IfNot(FFmpegRuntime.IsAvailable, SkipReason);

        using var decoder = new AudioDecoder(new AudioFormat(AudioCodec.AacEld, 44100, 2, 480, []));
        byte[] silence = [0x00, 0x68, 0x34, 0x00];

        for (var i = 0; i < 4; i++)
        {
            var pcm = decoder.Decode(silence);
            Assert.Equal(480 * 2 * sizeof(short), pcm.Length);
            Assert.True(pcm.IndexOfAnyExcept((byte)0) < 0, "a silence frame decoded to something audible");
        }

        Assert.Equal(0, decoder.ErrorCount);
    }

    [SkippableFact]
    public void RejectsNoiseWithoutThrowing()
    {
        Skip.IfNot(FFmpegRuntime.IsAvailable, SkipReason);

        using var decoder = new AudioDecoder(new AudioFormat(AudioCodec.AacEld, 44100, 2, 480, []));

        var noise = new byte[256];
        Random.Shared.NextBytes(noise);

        // A corrupt or undecryptable packet must not take the stream down.
        Assert.True(decoder.Decode(noise).IsEmpty);
        Assert.True(decoder.Decode([]).IsEmpty);
    }
}
