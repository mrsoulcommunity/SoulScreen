using FFmpeg.AutoGen;
using SoulScreen.Core.Media;
using SoulScreen.Media;
using Xunit;

namespace SoulScreen.Tests;

/// <summary>
/// Cuts clips out of a recording made the way the app makes one, and reads the result back
/// through an independent demuxer: a file that writes cleanly and then refuses to open is
/// the failure worth catching.
/// </summary>
public class ClipExportTests
{
    private static bool CanRun => FFmpegRuntime.IsAvailable && H264EncoderHarness.IsAvailable;

    private const string SkipReason =
        "FFmpeg is not installed; run tools/fetch-ffmpeg.ps1 to enable the clip tests.";

    [SkippableFact]
    public void WritesAnAnimatedGif()
    {
        Skip.IfNot(CanRun && ClipExporter.Supports(ClipFormat.Gif), SkipReason);

        var source = Record(frames: 45);
        var clip = Path.ChangeExtension(source, ".gif");
        try
        {
            var progress = new RecordingProgress();
            var result = ClipExporter.Export(
                new ClipRequest(source, clip, TimeSpan.FromSeconds(0.5), TimeSpan.FromSeconds(1), ClipFormat.Gif),
                progress);

            Assert.True(File.Exists(clip));
            Assert.Equal(ClipFormat.Gif, result.Format);
            Assert.True(result.FrameCount >= 8, $"only {result.FrameCount} pictures were written");
            Assert.True(result.Bytes > 0);
            Assert.True(result.Width <= 480 && result.Height <= 480, $"{result.Width}x{result.Height} is larger than a GIF should be");

            // A GIF is signed, and ends with its trailer: either missing means nothing opens it.
            var bytes = File.ReadAllBytes(clip);
            Assert.Equal("GIF89a", System.Text.Encoding.ASCII.GetString(bytes, 0, 6));
            Assert.Equal(0x3b, bytes[^1]);

            // Read back through FFmpeg's own GIF reader: a clip that is a single frame, or
            // whose blocks are out of order, is a still picture with a .gif name.
            var probe = Mp4Probe.Read(clip);
            Assert.Equal(AVCodecID.AV_CODEC_ID_GIF, probe.CodecId);
            Assert.Equal(result.Width, probe.Width);
            Assert.Equal(result.Height, probe.Height);
            Assert.True(probe.PacketCount >= result.FrameCount - 2,
                $"the GIF holds {probe.PacketCount} of the {result.FrameCount} pictures that were written");
            Assert.Contains(progress.Values, value => value > 0);
            Assert.Equal(1, progress.Values[^1], 3);
        }
        finally
        {
            Delete(source, clip);
        }
    }

    [SkippableFact]
    public void WritesAShortWebM()
    {
        Skip.IfNot(CanRun && ClipExporter.Supports(ClipFormat.WebM), SkipReason);

        var source = Record(frames: 45);
        var clip = Path.ChangeExtension(source, ".webm");
        try
        {
            var result = ClipExporter.Export(
                new ClipRequest(source, clip, TimeSpan.FromSeconds(0.5), TimeSpan.FromSeconds(1), ClipFormat.WebM));

            Assert.True(File.Exists(clip));
            Assert.Equal(ClipFormat.WebM, result.Format);
            Assert.True(result.FrameCount >= 8, $"only {result.FrameCount} pictures were written");

            var probe = Mp4Probe.Read(clip);
            Assert.Equal(1, probe.StreamCount);
            Assert.Contains(probe.CodecId, new[] { AVCodecID.AV_CODEC_ID_VP9, AVCodecID.AV_CODEC_ID_VP8 });
            Assert.Equal(result.Width, probe.Width);
            Assert.Equal(result.Height, probe.Height);
            Assert.True(probe.PacketCount > 0, "the WebM carries no picture");
            // A clip is never bigger than the recording it came from.
            Assert.True(result.Width <= 320, $"{result.Width} pixels wide is larger than the recording");
        }
        finally
        {
            Delete(source, clip);
        }
    }

    /// <summary>
    /// A clip is asked for more than the recording holds: the recording decides where it
    /// ends, not the request, and nothing is padded out to reach the length asked for.
    /// </summary>
    [SkippableFact]
    public void ClampsAClipToTheLengthOfTheRecording()
    {
        Skip.IfNot(CanRun && ClipExporter.Supports(ClipFormat.Gif), SkipReason);

        var source = Record(frames: 45);
        var clip = Path.ChangeExtension(source, ".gif");
        try
        {
            var result = ClipExporter.Export(
                new ClipRequest(source, clip, TimeSpan.Zero, TimeSpan.FromSeconds(30), ClipFormat.Gif));

            Assert.True(result.Duration < TimeSpan.FromSeconds(2.5), $"{result.Duration.TotalSeconds:0.#} s is longer than the recording");
            Assert.True(result.FrameCount >= 8, $"only {result.FrameCount} pictures were written");
        }
        finally
        {
            Delete(source, clip);
        }
    }

    /// <summary>A moment asked for in the last second of a recording: what is there is what
    /// is written, and the file is still a whole one.</summary>
    [SkippableFact]
    public void WritesWhatIsLeftAtTheEndOfARecording()
    {
        Skip.IfNot(CanRun && ClipExporter.Supports(ClipFormat.Gif), SkipReason);

        var source = Record(frames: 45); // one and a half seconds
        var clip = Path.ChangeExtension(source, ".gif");
        try
        {
            var result = ClipExporter.Export(
                new ClipRequest(source, clip, TimeSpan.FromSeconds(1.4), TimeSpan.FromSeconds(5), ClipFormat.Gif));

            Assert.True(result.FrameCount >= 1, "the tail of the recording produced no pictures");
            Assert.True(result.Duration <= TimeSpan.FromSeconds(1),
                $"{result.Duration.TotalSeconds:0.#} s is longer than the recording had left");
            Assert.Equal(AVCodecID.AV_CODEC_ID_GIF, Mp4Probe.Read(clip).CodecId);
        }
        finally
        {
            Delete(source, clip);
        }
    }

    [SkippableFact]
    public void RefusesARecordingThatIsNoLongerThere()
    {
        Skip.IfNot(FFmpegRuntime.IsAvailable, SkipReason);

        var missing = Path.Combine(Path.GetTempPath(), $"soulscreen-clip-{Guid.NewGuid():N}.mp4");
        var clip = Path.ChangeExtension(missing, ".gif");

        var failure = Assert.Throws<IOException>(() => ClipExporter.Export(
            new ClipRequest(missing, clip, TimeSpan.Zero, TimeSpan.FromSeconds(3), ClipFormat.Gif)));

        // The name is what the user is told about, so it has to be in there.
        Assert.Contains(Path.GetFileName(missing), failure.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(clip));
    }

    /// <summary>Records a real H.264 stream to an MP4, exactly as a mirrored session does.</summary>
    private static string Record(int frames)
    {
        const int width = 320, height = 240;
        var accessUnits = H264EncoderHarness.EncodeSplitField(width, height, frames);
        var format = FormatFrom(accessUnits[0], width, height);

        var path = Path.Combine(Path.GetTempPath(), $"soulscreen-clip-{Guid.NewGuid():N}.mp4");
        using var recorder = SessionRecorder.Create(path, format);
        for (var index = 0; index < accessUnits.Count; index++)
            recorder.Write(accessUnits[index], timestampUs: index * 33_333L, isKeyFrame: true);

        return path;
    }

    private static VideoFormat FormatFrom(byte[] accessUnit, int width, int height)
    {
        var parameterSets = new List<byte>();
        foreach (var range in H264.SplitAnnexB(accessUnit))
        {
            var (offset, length) = range.GetOffsetAndLength(accessUnit.Length);
            if (length == 0) continue;

            var nalType = accessUnit[offset] & 0x1f;
            if (nalType is not (H264.NalTypeSps or H264.NalTypePps)) continue;

            parameterSets.AddRange([0, 0, 0, 1]);
            parameterSets.AddRange(accessUnit.AsSpan(offset, length).ToArray());
        }

        Assert.NotEmpty(parameterSets);
        return new VideoFormat(VideoCodec.H264, width, height, [.. parameterSets], 30);
    }

    private static void Delete(params string[] paths)
    {
        foreach (var path in paths)
            if (File.Exists(path)) File.Delete(path);
    }

    /// <summary>Records the progress the exporter reports, on the thread that reported it.</summary>
    private sealed class RecordingProgress : IProgress<double>
    {
        public List<double> Values { get; } = [];

        public void Report(double value) => Values.Add(value);
    }
}
