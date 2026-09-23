using SoulScreen.Android;
using SoulScreen.Core.Media;
using Xunit;

namespace SoulScreen.Tests;

/// <summary>
/// Exercises <see cref="AndroidH264Assembler"/> against hand-built Annex-B streams.
/// <para>
/// This is where a bug would actually bite: adb's pipe delivers bytes in whatever chunks
/// the OS feels like, sharing no boundary with NAL units, so the assembler has to be
/// correct for every possible way a given stream could be sliced up. Most of these tests
/// therefore run the same input at several chunk sizes and require identical output.
/// </para>
/// <para>
/// The NAL bytes here are structurally valid (correct NAL type in the header nibble) but
/// their SPS payloads are not necessarily bit-exact H.264, since these tests are about
/// framing and dedup, not codec parsing. <see cref="RealEncoderOutputRoundTrips"/> below
/// covers the dimension-parsing path against a genuine encoder instead.
/// </para>
/// </summary>
public class AndroidH264AssemblerTests
{
    private const int NalIdr = H264.NalTypeIdr;
    private const int NalSlice = H264.NalTypeSlice;
    private const int NalSps = H264.NalTypeSps;
    private const int NalPps = H264.NalTypePps;
    private const int NalAud = H264.NalTypeAccessUnitDelimiter;
    private const int NalSei = 6;

    // NAL header byte (forbidden=0, nri=3, type) followed by filler payload bytes long
    // enough for AvcDecoderConfiguration.FromAnnexB's minimum-length check.
    private static readonly byte[] Sps1 = [(byte)(0x60 | NalSps), 0x42, 0x00, 0x0A, 0xAA];
    private static readonly byte[] Sps2 = [(byte)(0x60 | NalSps), 0x42, 0x00, 0x0B, 0xBB]; // distinct from Sps1
    private static readonly byte[] Pps1 = [(byte)(0x60 | NalPps), 0x11];

    private static byte[] WithStartCode(byte[] nal)
    {
        var result = new byte[nal.Length + 4];
        result[3] = 1;
        Array.Copy(nal, 0, result, 4, nal.Length);
        return result;
    }

    private static byte[] Concat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    private static byte[] Slice(int type, byte marker) => [(byte)type, marker, 0x88, 0x99];

    /// <summary>
    /// A start code with nothing after it. The assembler only ever closes a NAL when the
    /// *next* one's start code arrives - by design, since that is the only way to know a
    /// NAL is really finished rather than truncated mid-chunk - so every one of these finite
    /// test fixtures needs a trailing start code standing in for "the next NAL screenrecord
    /// would have sent", or its real last NAL would never close and the test would be
    /// checking the wrong thing.
    /// </summary>
    private static byte[] TrailingStartCode => [0, 0, 0, 1];

    private static byte[] BuildStream(byte[] sps, byte[] pps, params (int type, byte marker)[] slices)
    {
        var parts = new List<byte[]> { WithStartCode(sps), WithStartCode(pps) };
        parts.AddRange(slices.Select(s => WithStartCode(Slice(s.type, s.marker))));
        parts.Add(TrailingStartCode);
        return Concat([.. parts]);
    }

    private static List<AndroidH264Unit> FeedInChunks(byte[] stream, int chunkSize)
    {
        var assembler = new AndroidH264Assembler();
        var results = new List<AndroidH264Unit>();
        for (var offset = 0; offset < stream.Length; offset += chunkSize)
        {
            var length = Math.Min(chunkSize, stream.Length - offset);
            results.AddRange(assembler.Feed(stream.AsSpan(offset, length)));
        }
        return results;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(7)]
    [InlineData(64)]
    [InlineData(4096)]
    [InlineData(int.MaxValue)] // the whole stream in one Feed call
    public void SpsPpsIdrThenTwoSlices_ProducesOneFormatAndThreeSamples_RegardlessOfChunking(int chunkSize)
    {
        var stream = BuildStream(Sps1, Pps1, (NalIdr, 0xAA), (NalSlice, 0xBB), (NalSlice, 0xCC));

        var results = FeedInChunks(stream, chunkSize);

        var format = Assert.Single(results.OfType<AndroidFormatUnit>());
        Assert.Equal(VideoCodec.H264, format.Format.Codec);

        var samples = results.OfType<AndroidSampleUnit>().ToList();
        Assert.Equal(3, samples.Count);
        Assert.True(samples[0].IsKeyFrame);
        Assert.False(samples[1].IsKeyFrame);
        Assert.False(samples[2].IsKeyFrame);

        // Format must be reported before any sample relies on it, same as AirPlay's contract.
        Assert.True(results.IndexOf(format) < results.IndexOf(samples[0]));
    }

    [Fact]
    public void RepeatedIdenticalSpsPps_AnnouncesFormatOnlyOnce()
    {
        var stream = Concat(
            BuildStream(Sps1, Pps1, (NalIdr, 1)),
            BuildStream(Sps1, Pps1, (NalIdr, 2)),
            BuildStream(Sps1, Pps1, (NalIdr, 3)));

        var results = FeedInChunks(stream, 37);

        Assert.Single(results.OfType<AndroidFormatUnit>());
        Assert.Equal(3, results.OfType<AndroidSampleUnit>().Count());
    }

    [Fact]
    public void ChangedSps_AnnouncesANewFormat()
    {
        var stream = Concat(
            BuildStream(Sps1, Pps1, (NalIdr, 1)),
            BuildStream(Sps2, Pps1, (NalIdr, 2)));

        var results = FeedInChunks(stream, 17);

        Assert.Equal(2, results.OfType<AndroidFormatUnit>().Count());
    }

    [Fact]
    public void AudAndSei_AreIgnored()
    {
        var stream = Concat(
            WithStartCode([(byte)NalAud, 0xF0]),
            WithStartCode(Sps1),
            WithStartCode(Pps1),
            WithStartCode([(byte)NalSei, 0x01, 0x02]),
            WithStartCode(Slice(NalIdr, 0xAA)),
            TrailingStartCode);

        var results = FeedInChunks(stream, 5);

        Assert.Single(results.OfType<AndroidFormatUnit>());
        Assert.Single(results.OfType<AndroidSampleUnit>());
    }

    [Fact]
    public void GarbageBeforeFirstStartCode_IsDiscardedWithoutCorruptingWhatFollows()
    {
        var garbage = new byte[] { 0x01, 0x02, 0x00, 0x00, 0x03, 0x9, 0x9 };
        var stream = Concat(garbage, BuildStream(Sps1, Pps1, (NalIdr, 1)));

        var results = FeedInChunks(stream, 3);

        Assert.Single(results.OfType<AndroidFormatUnit>());
        Assert.Single(results.OfType<AndroidSampleUnit>());
    }

    [Fact]
    public void FourByteAndThreeByteStartCodes_AreBothAccepted()
    {
        // BuildStream always uses 4-byte start codes; this stream mixes in a 3-byte one,
        // which is legal Annex-B and is what some encoders emit for non-first NALs.
        var idr = Slice(NalIdr, 0xAA);
        var threeByteStartCode = new byte[] { 0, 0, 1 };
        var stream = Concat(WithStartCode(Sps1), WithStartCode(Pps1), threeByteStartCode, idr, TrailingStartCode);

        var results = FeedInChunks(stream, 6);

        Assert.Single(results.OfType<AndroidFormatUnit>());
        Assert.Single(results.OfType<AndroidSampleUnit>());
    }

    [Fact]
    public void ANalThatNeverCloses_IsDroppedRatherThanGrowingWithoutBound()
    {
        var assembler = new AndroidH264Assembler();

        var results = new List<AndroidH264Unit>();
        results.AddRange(assembler.Feed(WithStartCode(Sps1)));
        results.AddRange(assembler.Feed(WithStartCode(Pps1)));
        // This IDR is the NAL that is about to never close: its start code opens it, and
        // the flood below never supplies the one that would close it, so - correctly - it
        // is what the runaway guard drops. SPS and PPS closed properly before it and are
        // unaffected; only this one is sacrificed.
        results.AddRange(assembler.Feed(WithStartCode(Slice(NalIdr, 0xAA))));

        // Feed well past the assembler's runaway guard (16 MB) without ever supplying the
        // start code that would close the IDR opened above. Filled with a constant byte
        // that can contain no "00 00" run at all, so it is guaranteed both to contain no
        // start code and, on the odd chance a stray byte from it ever got misread as a NAL
        // header, to map to a type this assembler ignores (0xFF & 0x1f = 31) - the test
        // should fail only for the runaway guard, never for an unrelated coincidence.
        var chunk = new byte[1024 * 1024];
        Array.Fill(chunk, (byte)0xFF);

        for (var i = 0; i < 20; i++) results.AddRange(assembler.Feed(chunk));

        // It must recover: a start code establishes a clean boundary regardless of whatever
        // the flood left open, and a fresh, well-formed NAL after it is parsed normally.
        results.AddRange(assembler.Feed(TrailingStartCode));
        results.AddRange(assembler.Feed(Slice(NalSlice, 0xBB)));
        results.AddRange(assembler.Feed(TrailingStartCode));

        // Only the post-flood recovery slice survives - the pre-flood IDR was correctly
        // sacrificed along with the flood it opened into.
        Assert.Single(results.OfType<AndroidSampleUnit>());
        Assert.Single(results.OfType<AndroidFormatUnit>());
    }

    private const string SkipReason =
        "FFmpeg is not installed; run tools/fetch-ffmpeg.ps1 to enable the real-encoder test.";

    /// <summary>
    /// Round-trips a genuinely encoded stream (real SPS/PPS bytes, not the synthetic
    /// fixtures above) through the assembler, chunked arbitrarily, and checks the reported
    /// dimensions actually match what was encoded - the one thing the synthetic fixtures
    /// above cannot verify, since <see cref="SpsParser"/>'s exp-Golomb parsing needs a
    /// bit-exact SPS to get right.
    /// </summary>
    [SkippableFact]
    public void RealEncoderOutputRoundTrips_320x240() => AssertRealEncoderRoundTrips(320, 240, chunkSize: 13);

    [SkippableFact]
    public void RealEncoderOutputRoundTrips_640x480() => AssertRealEncoderRoundTrips(640, 480, chunkSize: 71);

    [SkippableFact]
    public void RealEncoderOutputRoundTrips_PortraitPhoneSize() =>
        AssertRealEncoderRoundTrips(1088, 1920, chunkSize: 4096); // odd-ish phone-like portrait size, larger chunk

    private static void AssertRealEncoderRoundTrips(int width, int height, int chunkSize)
    {
        Skip.IfNot(H264EncoderHarness.IsAvailable, SkipReason);

        var accessUnits = H264EncoderHarness.EncodeSplitField(width, height, frameCount: 3);
        // Plus a trailing start code standing in for whatever screenrecord would have sent
        // next: the assembler only closes a NAL once it sees the one after it, so without
        // this the last frame's slice NAL would never close.
        var stream = Concat([.. accessUnits, TrailingStartCode]);

        var results = FeedInChunks(stream, chunkSize);

        var format = Assert.Single(results.OfType<AndroidFormatUnit>());
        Assert.Equal(width, format.Format.Width);
        Assert.Equal(height, format.Format.Height);

        var samples = results.OfType<AndroidSampleUnit>().ToList();
        Assert.Equal(3, samples.Count);
        Assert.All(samples, s => Assert.True(s.IsKeyFrame)); // gop_size=1 in the harness: every frame is an IDR.
    }
}
