using System.Buffers.Binary;
using SoulScreen.AirPlay.Discovery;
using SoulScreen.Core.Media;
using SoulScreen.Core.Buffers;
using Xunit;

namespace SoulScreen.Tests;

public class H264Tests
{
    // Produced by an independent exp-Golomb encoder. 1080 is not a multiple of 16, so it
    // is coded as 1088 rows with a bottom crop of 4 chroma units - the case a naive parser
    // gets wrong.
    private const string Sps1080 = "6742001ff403c0113f2a";
    private const string Sps720 = "6742001ff402802dc8";
    private const string Sps480 = "6742001ff40501ec80";

    [Theory]
    [InlineData(Sps1080, 1920, 1080)]
    [InlineData(Sps720, 1280, 720)]
    [InlineData(Sps480, 640, 480)]
    public void ReadsDimensionsFromTheSps(string spsHex, int expectedWidth, int expectedHeight)
    {
        var config = BuildConfiguration(Hex.Parse(spsHex));

        Assert.True(config.TryGetDimensions(out var width, out var height));
        Assert.Equal(expectedWidth, width);
        Assert.Equal(expectedHeight, height);
    }

    [Fact]
    public void ParsesAnAvcCRecordAndEmitsAnnexB()
    {
        var sps = Hex.Parse(Sps1080);
        byte[] pps = [0x68, 0xce, 0x3c, 0x80];
        var config = BuildConfiguration(sps, pps);

        Assert.Equal(4, config.NalLengthSize);
        Assert.Single(config.SequenceParameterSets);
        Assert.Single(config.PictureParameterSets);

        var annexB = config.ToAnnexB();
        Assert.Equal([0, 0, 0, 1], annexB[..4]);
        Assert.Equal(sps, annexB[4..(4 + sps.Length)]);
        Assert.Equal([0, 0, 0, 1], annexB[(4 + sps.Length)..(8 + sps.Length)]);
        Assert.Equal(pps, annexB[(8 + sps.Length)..]);
    }

    [Fact]
    public void RejectsATruncatedAvcCRecord()
    {
        // Announces one SPS of 0x0010 bytes but carries none of them.
        var record = Hex.Parse("0142001fffe10010");
        Assert.Throws<InvalidDataException>(() => AvcDecoderConfiguration.Parse(record));
    }

    [Fact]
    public void RejectsAnUnknownAvcCVersion()
    {
        var record = Hex.Parse("0942001fffe10004674200");
        Assert.Throws<InvalidDataException>(() => AvcDecoderConfiguration.Parse(record));
    }

    [Fact]
    public void RewritesLengthPrefixesAsStartCodes()
    {
        // Two NAL units: a 5-byte IDR slice and a 3-byte non-IDR slice.
        var buffer = new byte[4 + 5 + 4 + 3];
        BinaryPrimitives.WriteUInt32BigEndian(buffer, 5);
        buffer[4] = 0x65; // nal_unit_type 5 - IDR
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(9), 3);
        buffer[13] = 0x41; // nal_unit_type 1 - non-IDR

        Assert.True(H264.ConvertLengthPrefixedToAnnexB(buffer, out var isKeyFrame));

        Assert.True(isKeyFrame);
        Assert.Equal([0, 0, 0, 1], buffer[..4]);
        Assert.Equal([0, 0, 0, 1], buffer[9..13]);
    }

    [Fact]
    public void ReportsFramesWithNoKeyFrameNalUnit()
    {
        var buffer = new byte[4 + 3];
        BinaryPrimitives.WriteUInt32BigEndian(buffer, 3);
        buffer[4] = 0x41;

        Assert.True(H264.ConvertLengthPrefixedToAnnexB(buffer, out var isKeyFrame));
        Assert.False(isKeyFrame);
    }

    /// <summary>
    /// A wrong stream key produces bytes that do not parse as length-prefixed NAL units.
    /// The conversion has to say so rather than emit garbage to the decoder.
    /// </summary>
    [Theory]
    [InlineData("00000000")]                       // zero length
    [InlineData("7fffffff41")]                     // length past the end
    [InlineData("0000000541650000")]               // trailing bytes that are not a full NAL
    public void RejectsPayloadsThatAreNotLengthPrefixed(string hex)
    {
        var buffer = Hex.Parse(hex);
        Assert.False(H264.ConvertLengthPrefixedToAnnexB(buffer, out _));
    }

    private static AvcDecoderConfiguration BuildConfiguration(byte[] sps, byte[]? pps = null)
    {
        pps ??= [0x68, 0xce, 0x3c, 0x80];
        var record = new List<byte>
        {
            1,          // configurationVersion
            sps[1],     // AVCProfileIndication
            sps[2],     // profile_compatibility
            sps[3],     // AVCLevelIndication
            0xff,       // 6 reserved bits + lengthSizeMinusOne = 3
            0xe1,       // 3 reserved bits + numOfSequenceParameterSets = 1
        };
        record.AddRange([(byte)(sps.Length >> 8), (byte)sps.Length]);
        record.AddRange(sps);
        record.Add(1);  // numOfPictureParameterSets
        record.AddRange([(byte)(pps.Length >> 8), (byte)pps.Length]);
        record.AddRange(pps);

        return AvcDecoderConfiguration.Parse(record.ToArray());
    }
}

public class DnsWireTests
{
    [Fact]
    public void RoundTripsAServiceAnnouncement()
    {
        var message = DnsMessage.Response();
        message.Answers.Add(new PtrRecord("_airplay._tcp.local", "SoulScreen._airplay._tcp.local"));
        message.Answers.Add(new SrvRecord("SoulScreen._airplay._tcp.local", "SoulScreen.local", 7000));
        message.Answers.Add(new TxtRecord("SoulScreen._airplay._tcp.local",
            ["deviceid=AA:BB:CC:DD:EE:FF", "features=0x48FFCBBF,0x0", "model=AppleTV3,2"]));
        message.Answers.Add(new ARecord("SoulScreen.local", System.Net.IPAddress.Parse("192.168.1.20")));

        var parsed = DnsMessage.Parse(message.ToArray());

        Assert.True(parsed.IsResponse);
        Assert.Equal(4, parsed.Answers.Count);
        Assert.Equal(DnsRecordType.Ptr, parsed.Answers[0].Type);
        Assert.Equal("_airplay._tcp.local", parsed.Answers[0].Name);
        Assert.Equal("SoulScreen._airplay._tcp.local", parsed.Answers[1].Name);
        Assert.Equal(DnsRecordType.Srv, parsed.Answers[1].Type);
        Assert.Equal(DnsRecordType.A, parsed.Answers[3].Type);
    }

    [Fact]
    public void CompressesRepeatedSuffixes()
    {
        var message = DnsMessage.Response();
        for (var i = 0; i < 8; i++)
            message.Answers.Add(new PtrRecord("_airplay._tcp.local", $"Receiver{i}._airplay._tcp.local"));

        var bytes = message.ToArray();

        // Without compression each record would repeat "_airplay._tcp.local" twice.
        var uncompressedEstimate = 12 + 8 * (21 + 10 + 11 + 21);
        Assert.True(bytes.Length < uncompressedEstimate,
            $"expected compression to help, got {bytes.Length} vs {uncompressedEstimate}");

        Assert.Equal(8, DnsMessage.Parse(bytes).Answers.Count);
    }

    [Fact]
    public void ParsesAQuestionWithTheUnicastResponseBit()
    {
        var query = new DnsMessage();
        query.Questions.Add(new DnsQuestion("_raop._tcp.local", DnsRecordType.Ptr, WantsUnicastReply: true));

        var parsed = DnsMessage.Parse(query.ToArray());

        Assert.True(parsed.IsQuery);
        var question = Assert.Single(parsed.Questions);
        Assert.Equal("_raop._tcp.local", question.Name);
        Assert.True(question.WantsUnicastReply);
    }

    [Fact]
    public void SurvivesAPointerLoop()
    {
        // A name whose compression pointer refers to itself must terminate, not hang.
        byte[] hostile =
        [
            0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0xC0, 0x0C,             // pointer to itself
            0x00, 0x0C, 0x00, 0x01,
        ];

        var parsed = DnsMessage.Parse(hostile);
        Assert.Single(parsed.Questions);
    }

    [Fact]
    public void TxtEntriesSurviveTheirLengthPrefixes()
    {
        var entries = new[] { "a=1", new string('x', 200), "pk=" + new string('0', 64) };
        var message = DnsMessage.Response();
        message.Answers.Add(new TxtRecord("x._airplay._tcp.local", entries));

        var record = (RawRecord)DnsMessage.Parse(message.ToArray()).Answers[0];

        var decoded = new List<string>();
        var offset = 0;
        while (offset < record.RData.Length)
        {
            int length = record.RData[offset++];
            decoded.Add(System.Text.Encoding.UTF8.GetString(record.RData, offset, length));
            offset += length;
        }

        Assert.Equal(entries, decoded);
    }
}
