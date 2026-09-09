using SoulScreen.AirPlay.Plist;
using Xunit;

namespace SoulScreen.Tests;

public class BinaryPlistTests
{
    [Fact]
    public void RoundTripsTheShapeOfARealSetupRequest()
    {
        var original = new PlistDictionary
        {
            ["et"] = 32,
            ["ekey"] = new byte[72],
            ["eiv"] = new byte[16],
            ["timingProtocol"] = "NTP",
            ["timingPort"] = 51234,
            ["name"] = "Test iPhone",
            ["model"] = "iPhone14,5",
            ["isScreenMirroringSession"] = true,
            ["latencyMin"] = 11025,
            ["streams"] = new PlistArray
            {
                new PlistDictionary
                {
                    ["type"] = 110,
                    ["streamConnectionID"] = 8123456789012345678L,
                },
            },
        };

        var round = BinaryPlist.Read(BinaryPlist.Write(original)).AsDictionary();

        Assert.Equal(32, round.GetInteger("et"));
        Assert.Equal(72, round.GetData("ekey")!.Length);
        Assert.Equal("NTP", round.GetString("timingProtocol"));
        Assert.Equal(51234, round.GetInteger("timingPort"));
        Assert.Equal("Test iPhone", round.GetString("name"));
        Assert.True(round.GetBoolean("isScreenMirroringSession"));

        var stream = round.GetArray("streams")!.OfType<PlistDictionary>().Single();
        Assert.Equal(110, stream.GetInteger("type"));
        Assert.Equal(8123456789012345678L, stream.GetInteger("streamConnectionID"));
    }

    [Fact]
    public void PreservesNegativeAndLargeIntegers()
    {
        var original = new PlistDictionary
        {
            ["negative"] = -12345L,
            ["byteBoundary"] = 255L,
            ["shortBoundary"] = 65535L,
            ["intBoundary"] = 4294967295L,
            ["large"] = long.MaxValue,
        };

        var round = BinaryPlist.Read(BinaryPlist.Write(original)).AsDictionary();

        Assert.Equal(-12345L, round.GetInteger("negative"));
        Assert.Equal(255L, round.GetInteger("byteBoundary"));
        Assert.Equal(65535L, round.GetInteger("shortBoundary"));
        Assert.Equal(4294967295L, round.GetInteger("intBoundary"));
        Assert.Equal(long.MaxValue, round.GetInteger("large"));
    }

    [Fact]
    public void HandlesCollectionsLongerThanTheInlineCountLimit()
    {
        // Counts of 15 or more switch from an inline nibble to a trailing integer object.
        var array = new PlistArray(Enumerable.Range(0, 40).Select(i => (PlistValue)(long)i));
        var round = BinaryPlist.Read(BinaryPlist.Write(array)).AsArray();

        Assert.Equal(40, round.Count);
        Assert.Equal(39L, round[39].AsInteger());
    }

    [Fact]
    public void RoundTripsNonAsciiStringsAsUtf16()
    {
        var original = new PlistDictionary { ["name"] = "کسری's iPhone" };
        var round = BinaryPlist.Read(BinaryPlist.Write(original)).AsDictionary();
        Assert.Equal("کسری's iPhone", round.GetString("name"));
    }

    /// <summary>
    /// Reads a container produced by an independent implementation (Python's plistlib,
    /// which follows Apple's CFBinaryPlist layout). Without this the reader and writer
    /// could agree on an encoding Apple would never emit.
    /// </summary>
    [Fact]
    public void ReadsAForeignEncoderOutput()
    {
        var reference = Convert.FromBase64String(
            "YnBsaXN0MDDaAQIDBAUGBwgJCgsMDQ4PEBMnKCtRYVFiU2JpZ1RibG9iVGZsYWdUbGlzdFRtYW55" +
            "U25lZ1ZuZXN0ZWRXdW5pY29kZRABUXgTAAAAAQAAAABEAAECAwmiERJTb25lU3R3b68QFBQLFRYX" +
            "GBkaGxwdHh8gISIjJCUmEAAQAhADEAQQBRAGEAcQCBAJEAoQCxAMEA0QDhAPEBAQERASEBMT////" +
            "//////vRKSpUZGVlcCNADAAAAAAAAGQGqQYzBjEGzAgdHyElKi80OT1ETE5QWV5fYmZqgYOFh4mL" +
            "jY+Rk5WXmZudn6Gjpaews7jBAAAAAAAAAQEAAAAAAAAALAAAAAAAAAAAAAAAAAAAAMo=");

        var dict = BinaryPlist.Read(reference).AsDictionary();

        Assert.Equal(1, dict.GetInteger("a"));
        Assert.Equal("x", dict.GetString("b"));
        Assert.Equal(4294967296L, dict.GetInteger("big"));
        Assert.Equal(-5L, dict.GetInteger("neg"));
        Assert.True(dict.GetBoolean("flag"));
        Assert.Equal([0, 1, 2, 3], dict.GetData("blob"));
        Assert.Equal("کسری", dict.GetString("unicode"));
        Assert.Equal(["one", "two"], dict.GetArray("list")!.Select(v => v.AsString()));
        Assert.Equal(3.5, dict.GetDictionary("nested")!["deep"].AsReal());

        // A 20-element array exercises the extended object-count encoding.
        var many = dict.GetArray("many")!;
        Assert.Equal(20, many.Count);
        Assert.Equal(19L, many[19].AsInteger());
    }

    [Fact]
    public void RejectsATrailerThatDoesNotMatchThePayload()
    {
        var valid = BinaryPlist.Write(new PlistDictionary { ["a"] = 1 });
        var corrupted = (byte[])valid.Clone();
        // Claim far more objects than the payload can hold.
        corrupted[^16] = 0xFF;
        corrupted[^15] = 0xFF;

        Assert.Throws<InvalidDataException>(() => BinaryPlist.Read(corrupted));
    }

    [Fact]
    public void SnifferPicksTheRightDecoder()
    {
        var binary = BinaryPlist.Write(new PlistDictionary { ["k"] = "v" });
        var xml = XmlPlist.Write(new PlistDictionary { ["k"] = "v" });

        Assert.Equal("v", PlistSerializer.Read(binary).AsDictionary().GetString("k"));
        Assert.Equal("v", PlistSerializer.Read(xml).AsDictionary().GetString("k"));
        Assert.False(PlistSerializer.TryRead("not a plist"u8, out _));
    }
}

public class XmlPlistTests
{
    [Fact]
    public void RoundTripsThroughTheTextualForm()
    {
        var original = new PlistDictionary
        {
            ["name"] = "SoulScreen",
            ["port"] = 7000,
            ["enabled"] = true,
            ["ratio"] = 0.5,
            ["key"] = new byte[] { 1, 2, 3, 4 },
            ["list"] = new PlistArray { "a", "b" },
        };

        var round = XmlPlist.Read(XmlPlist.WriteToString(original)).AsDictionary();

        Assert.Equal("SoulScreen", round.GetString("name"));
        Assert.Equal(7000, round.GetInteger("port"));
        Assert.True(round.GetBoolean("enabled"));
        Assert.Equal(0.5, round["ratio"].AsReal());
        Assert.Equal([1, 2, 3, 4], round.GetData("key"));
        Assert.Equal(2, round.GetArray("list")!.Count);
    }

    [Fact]
    public void EscapesMarkupInValues()
    {
        var xml = XmlPlist.WriteToString(new PlistDictionary { ["name"] = "a<b>&c" });
        Assert.Contains("a&lt;b&gt;&amp;c", xml);
        Assert.Equal("a<b>&c", XmlPlist.Read(xml).AsDictionary().GetString("name"));
    }
}
