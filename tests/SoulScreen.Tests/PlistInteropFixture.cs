using SoulScreen.AirPlay.Plist;
using Xunit;

namespace SoulScreen.Tests;

/// <summary>
/// Writes a container to disk so an independent implementation can read it back.
/// <para>
/// The foreign-decoder test proves SoulScreen can <em>read</em> Apple's layout. This one
/// closes the other half: run <c>python tools/check-plist-interop.py</c> after the suite
/// and Python's plistlib will verify what SoulScreen <em>wrote</em>. A receiver whose
/// /info body iOS cannot parse fails in a way that is very hard to diagnose from the phone.
/// </para>
/// </summary>
public class PlistInteropFixture
{
    public static string OutputPath => Path.Combine(Path.GetTempPath(), "soulscreen-interop.plist");

    [Fact]
    public void WritesAFileForTheExternalChecker()
    {
        var document = new PlistDictionary
        {
            ["deviceID"] = "AA:BB:CC:DD:EE:FF",
            ["features"] = 1224129983L,
            ["pk"] = new byte[] { 0xde, 0xad, 0xbe, 0xef },
            ["name"] = "SoulScreen کسری",
            ["statusFlags"] = 68,
            ["initialVolume"] = -30.5,
            ["keepAliveSendStatsAsBody"] = true,
            ["negative"] = -1L,
            ["displays"] = new PlistArray
            {
                new PlistDictionary
                {
                    ["width"] = 1920,
                    ["height"] = 1080,
                    ["refreshRate"] = 1.0 / 60,
                    ["overscanned"] = false,
                },
            },
            ["many"] = new PlistArray(Enumerable.Range(0, 32).Select(i => (PlistValue)(long)i)),
        };

        File.WriteAllBytes(OutputPath, BinaryPlist.Write(document));

        // Sanity check our own reader too, so a failure here points at the writer.
        var round = BinaryPlist.Read(File.ReadAllBytes(OutputPath)).AsDictionary();
        Assert.Equal("SoulScreen کسری", round.GetString("name"));
        Assert.Equal(32, round.GetArray("many")!.Count);
    }
}
