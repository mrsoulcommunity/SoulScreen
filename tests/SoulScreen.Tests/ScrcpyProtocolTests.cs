using SoulScreen.Android;
using Xunit;

namespace SoulScreen.Tests;

/// <summary>
/// Exercises <see cref="ScrcpyPacketHeader"/> against byte layouts taken directly from
/// scrcpy's own client source (app/src/demuxer.c): SC_PACKET_FLAG_CONFIG = 1&lt;&lt;62,
/// SC_PACKET_FLAG_KEY_FRAME = 1&lt;&lt;61, and the PTS occupies the low 61 bits.
/// </summary>
public class ScrcpyProtocolTests
{
    private static byte[] Header(ulong ptsAndFlags, uint size)
    {
        var header = new byte[12];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(header, ptsAndFlags);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(8), size);
        return header;
    }

    [Fact]
    public void SessionPacket_TopBitSet_ParsesWidthAndHeight()
    {
        var header = new byte[12];
        header[0] = 0x80; // session flag
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), 1080);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(8), 2400);

        var parsed = ScrcpyPacketHeader.Parse(header);

        Assert.True(parsed.IsSessionPacket);
        Assert.Equal(1080, parsed.SessionWidth);
        Assert.Equal(2400, parsed.SessionHeight);
    }

    [Fact]
    public void ConfigPacket_ConfigBitSet_HasNoPts()
    {
        const ulong configFlag = 1UL << 62;
        var header = Header(configFlag, size: 37);

        var parsed = ScrcpyPacketHeader.Parse(header);

        Assert.False(parsed.IsSessionPacket);
        Assert.True(parsed.IsConfig);
        Assert.False(parsed.IsKeyFrame);
        Assert.Equal(0, parsed.PtsUs);
        Assert.Equal(37, parsed.PayloadSize);
    }

    [Fact]
    public void KeyFramePacket_KeyFrameBitSet_PtsIsMaskedCorrectly()
    {
        const ulong keyFrameFlag = 1UL << 61;
        const ulong pts = 123_456_789UL;
        var header = Header(keyFrameFlag | pts, size: 9000);

        var parsed = ScrcpyPacketHeader.Parse(header);

        Assert.False(parsed.IsSessionPacket);
        Assert.False(parsed.IsConfig);
        Assert.True(parsed.IsKeyFrame);
        Assert.Equal(123_456_789L, parsed.PtsUs);
        Assert.Equal(9000, parsed.PayloadSize);
    }

    [Fact]
    public void OrdinaryFramePacket_NoFlags_IsNeitherConfigNorKeyFrame()
    {
        var header = Header(ptsAndFlags: 42, size: 100);

        var parsed = ScrcpyPacketHeader.Parse(header);

        Assert.False(parsed.IsConfig);
        Assert.False(parsed.IsKeyFrame);
        Assert.Equal(42, parsed.PtsUs);
    }

    [Fact]
    public void ConfigAndKeyFrameFlags_DoNotLeakIntoThePtsValue()
    {
        const ulong configFlag = 1UL << 62;
        const ulong keyFrameFlag = 1UL << 61;
        const ulong pts = 999UL;
        var header = Header(configFlag | keyFrameFlag | pts, size: 1);

        var parsed = ScrcpyPacketHeader.Parse(header);

        Assert.True(parsed.IsConfig);
        Assert.True(parsed.IsKeyFrame);
        Assert.Equal(999L, parsed.PtsUs);
    }

    [Fact]
    public void WrongLength_Throws()
    {
        Assert.Throws<ArgumentException>(() => ScrcpyPacketHeader.Parse(new byte[11]));
        Assert.Throws<ArgumentException>(() => ScrcpyPacketHeader.Parse(new byte[13]));
    }

    [Theory]
    [InlineData(new byte[] { 0x68, 0x32, 0x36, 0x34 })] // "h264"
    public void VideoCodecId_H264_MatchesKnownBytes(byte[] expected) =>
        Assert.Equal(expected, ScrcpyCodecIds.VideoH264);

    [Fact]
    public void ThreeLetterCodecIds_AreFrontPadded()
    {
        // Verified against scrcpy's demuxer.c: SC_CODEC_ID_RAW = 0x00726177, i.e.
        // [0x00, 'r', 'a', 'w'] - the NUL pads the front, not the back.
        Assert.Equal(new byte[] { 0x00, (byte)'r', (byte)'a', (byte)'w' }, ScrcpyCodecIds.AudioRaw);
        Assert.Equal(new byte[] { 0x00, (byte)'a', (byte)'a', (byte)'c' }, ScrcpyCodecIds.AudioAac);
        Assert.Equal(new byte[] { 0x00, (byte)'a', (byte)'v', (byte)'1' }, ScrcpyCodecIds.VideoAv1);
    }

    [Fact]
    public void DeviceName_TrimsAtFirstNul_AndDecodesUtf8()
    {
        var field = new byte[64];
        var name = System.Text.Encoding.UTF8.GetBytes("Kasra's Pixel");
        name.CopyTo(field, 0);

        Assert.Equal("Kasra's Pixel", ScrcpyDeviceMeta.ParseDeviceName(field));
    }

    [Fact]
    public void DeviceName_AllNul_IsEmptyString() =>
        Assert.Equal(string.Empty, ScrcpyDeviceMeta.ParseDeviceName(new byte[64]));
}
