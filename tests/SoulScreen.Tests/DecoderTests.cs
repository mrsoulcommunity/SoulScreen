using SoulScreen.Media;
using Xunit;

namespace SoulScreen.Tests;

/// <summary>
/// Exercises the decode path against a genuine H.264 stream rather than a synthetic one.
/// <para>
/// Skipped when the FFmpeg runtime has not been downloaded - <c>tools/fetch-ffmpeg.ps1</c>
/// is a setup step, not something a fresh clone has - so the rest of the suite still runs.
/// </para>
/// </summary>
public class H264DecoderTests
{
    private static bool CanRun => FFmpegRuntime.IsAvailable && H264EncoderHarness.IsAvailable;

    private const string SkipReason =
        "FFmpeg is not installed; run tools/fetch-ffmpeg.ps1 to enable the decode tests.";

    [SkippableFact]
    public void DecodesEveryEncodedFrame()
    {
        Skip.IfNot(CanRun, SkipReason);

        const int width = 320, height = 240, frameCount = 10;
        var accessUnits = H264EncoderHarness.EncodeSplitField(width, height, frameCount);
        Assert.Equal(frameCount, accessUnits.Count);

        using var decoder = new H264Decoder();
        var decoded = 0;

        foreach (var accessUnit in accessUnits)
        {
            foreach (var frame in decoder.Decode(accessUnit, timestampUs: decoded * 33_333L))
            {
                using (frame)
                {
                    Assert.Equal(width, frame.Width);
                    Assert.Equal(height, frame.Height);
                    Assert.Equal(width * 4, frame.Stride);
                    decoded++;
                }
            }
        }

        foreach (var frame in decoder.Flush())
        {
            using (frame) decoded++;
        }

        Assert.Equal(frameCount, decoded);
        Assert.Equal(0, decoder.ErrorCount);
    }

    /// <summary>
    /// The source is limited-range luma, so a correct conversion has to expand it: black
    /// must land near 0 and white near 255, not at the 16 and 235 they were encoded as.
    /// </summary>
    [SkippableFact]
    public void ExpandsLimitedRangeLumaAndKeepsGeometry()
    {
        Skip.IfNot(CanRun, SkipReason);

        const int width = 320, height = 240;
        var accessUnits = H264EncoderHarness.EncodeSplitField(width, height, frameCount: 3);

        using var decoder = new H264Decoder();
        using var frame = DecodeFirst(decoder, accessUnits);

        var pixels = frame.Pixels;

        // Sample well inside each half to stay clear of ringing at the boundary.
        var left = SampleLuminance(pixels, frame.Stride, x: width / 4, y: height / 2);
        var right = SampleLuminance(pixels, frame.Stride, x: width * 3 / 4, y: height / 2);

        Assert.True(left < 24, $"expected the left half to be near black, got {left}");
        Assert.True(right > 232, $"expected the right half to be near white, got {right}");
    }

    [SkippableFact]
    public void CarriesTheTimestampThrough()
    {
        Skip.IfNot(CanRun, SkipReason);

        const long timestamp = 1_234_567;
        var accessUnits = H264EncoderHarness.EncodeSplitField(160, 120, frameCount: 1);

        using var decoder = new H264Decoder();
        using var frame = decoder.Decode(accessUnits[0], timestamp).Concat(decoder.Flush()).First();

        Assert.Equal(timestamp, frame.TimestampUs);
    }

    /// <summary>
    /// A wrong stream key hands the decoder noise. It has to reject it and still decode
    /// the next good frame, because the AirPlay session carries on regardless.
    /// </summary>
    [SkippableFact]
    public void SurvivesCorruptInput()
    {
        Skip.IfNot(CanRun, SkipReason);

        using var decoder = new H264Decoder();

        var noise = new byte[4096];
        Random.Shared.NextBytes(noise);
        foreach (var frame in decoder.Decode(noise, 0)) frame.Dispose();

        var accessUnits = H264EncoderHarness.EncodeSplitField(160, 120, frameCount: 2);
        var recovered = 0;
        foreach (var accessUnit in accessUnits)
            foreach (var frame in decoder.Decode(accessUnit, 0)) { frame.Dispose(); recovered++; }
        foreach (var frame in decoder.Flush()) { frame.Dispose(); recovered++; }

        Assert.Equal(2, recovered);
    }

    [SkippableFact]
    public void HandlesAResolutionChangeMidStream()
    {
        Skip.IfNot(CanRun, SkipReason);

        // What happens when the phone is rotated: a new SPS with different dimensions.
        var portrait = H264EncoderHarness.EncodeSplitField(240, 320, frameCount: 2);
        var landscape = H264EncoderHarness.EncodeSplitField(320, 240, frameCount: 2);

        using var decoder = new H264Decoder();

        using (var first = DecodeFirst(decoder, portrait))
        {
            Assert.Equal(240, first.Width);
            Assert.Equal(320, first.Height);
        }

        using (var second = DecodeFirst(decoder, landscape))
        {
            Assert.Equal(320, second.Width);
            Assert.Equal(240, second.Height);
        }
    }

    private static DecodedVideoFrame DecodeFirst(H264Decoder decoder, IReadOnlyList<byte[]> accessUnits)
    {
        foreach (var accessUnit in accessUnits)
        {
            var frames = decoder.Decode(accessUnit, timestampUs: 0);
            if (frames.Count == 0) continue;
            for (var i = 1; i < frames.Count; i++) frames[i].Dispose();
            return frames[0];
        }

        var flushed = decoder.Flush();
        Assert.NotEmpty(flushed);
        for (var i = 1; i < flushed.Count; i++) flushed[i].Dispose();
        return flushed[0];
    }

    /// <summary>BGRA, so blue, green, red, alpha; the harness encodes greyscale.</summary>
    private static int SampleLuminance(ReadOnlySpan<byte> pixels, int stride, int x, int y)
    {
        var offset = y * stride + x * 4;
        return (pixels[offset] + pixels[offset + 1] + pixels[offset + 2]) / 3;
    }
}
