using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using SoulScreen.Core.Media;
using SoulScreen.Media;
using Xunit;

namespace SoulScreen.Tests;

public class SessionRecorderTests
{
    private static bool CanRun => FFmpegRuntime.IsAvailable && H264EncoderHarness.IsAvailable;

    private const string SkipReason =
        "FFmpeg is not installed; run tools/fetch-ffmpeg.ps1 to enable the recording tests.";

    /// <summary>
    /// Records a real stream and reads the file back with an independent demuxer. The point
    /// is that the result is a playable MP4, not merely that no call returned an error -
    /// a missing trailer or wrong extradata produces a file that writes cleanly and then
    /// refuses to open.
    /// </summary>
    [SkippableFact]
    public void ProducesAPlayableMp4()
    {
        Skip.IfNot(CanRun, SkipReason);

        const int width = 320, height = 240, frameCount = 12;
        var accessUnits = H264EncoderHarness.EncodeSplitField(width, height, frameCount);
        var format = FormatFrom(accessUnits[0], width, height);

        var path = Path.Combine(Path.GetTempPath(), $"soulscreen-record-{Guid.NewGuid():N}.mp4");
        try
        {
            using (var recorder = SessionRecorder.Create(path, format))
            {
                for (var i = 0; i < accessUnits.Count; i++)
                {
                    // Every access unit from the harness is a keyframe: gop_size is 1.
                    recorder.Write(accessUnits[i], timestampUs: i * 33_333L, isKeyFrame: true);
                }

                Assert.Equal(frameCount, recorder.FrameCount);
                Assert.True(recorder.Duration > TimeSpan.Zero);
            }

            var probe = Mp4Probe.Read(path);
            Assert.Equal(1, probe.StreamCount);
            Assert.Equal(AVCodecID.AV_CODEC_ID_H264, probe.CodecId);
            Assert.Equal(width, probe.Width);
            Assert.Equal(height, probe.Height);
            Assert.Equal(frameCount, probe.PacketCount);
            Assert.True(probe.ExtradataLength > 0, "the track should carry an avcC record");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    /// <summary>
    /// A recording started mid-GOP would open on frames the decoder cannot use, so the
    /// recorder waits for a keyframe before writing anything.
    /// </summary>
    [SkippableFact]
    public void IgnoresFramesBeforeTheFirstKeyFrame()
    {
        Skip.IfNot(CanRun, SkipReason);

        var accessUnits = H264EncoderHarness.EncodeSplitField(160, 120, frameCount: 3);
        var format = FormatFrom(accessUnits[0], 160, 120);

        var path = Path.Combine(Path.GetTempPath(), $"soulscreen-record-{Guid.NewGuid():N}.mp4");
        try
        {
            using var recorder = SessionRecorder.Create(path, format);

            recorder.Write(accessUnits[1], timestampUs: 0, isKeyFrame: false);
            recorder.Write(accessUnits[2], timestampUs: 33_333, isKeyFrame: false);
            Assert.Equal(0, recorder.FrameCount);

            recorder.Write(accessUnits[0], timestampUs: 66_666, isKeyFrame: true);
            Assert.Equal(1, recorder.FrameCount);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    /// <summary>
    /// With an audio track asked for, the file carries two streams, the second AAC, and the
    /// audio runs the length of the picture - including silence for the stretch before the
    /// first packet and for packets that never came, so nothing drifts.
    /// </summary>
    [SkippableFact]
    public void RecordsAnAudioTrackAlongsideTheVideo()
    {
        Skip.IfNot(CanRun, SkipReason);

        const int width = 160, height = 120, frameCount = 30;
        var accessUnits = H264EncoderHarness.EncodeSplitField(width, height, frameCount);
        var format = FormatFrom(accessUnits[0], width, height);

        var path = Path.Combine(Path.GetTempPath(), $"soulscreen-record-{Guid.NewGuid():N}.mp4");
        try
        {
            using (var recorder = SessionRecorder.Create(path, format, RecordingAudioTrack.AirPlay))
            {
                Assert.True(recorder.HasAudio);

                // Audio before the first picture is dropped: there is nothing to line it up with.
                recorder.WriteAudio(new byte[480 * 4], timestampUs: 0);
                Assert.Equal(0, recorder.AudioPacketCount);

                // One second of picture at thirty a second, and one second of 440 Hz in
                // AirPlay-sized packets with a 100 ms hole in the middle.
                var tone = Tone(480, 44100, 440);
                for (var i = 0; i < frameCount; i++)
                {
                    recorder.Write(accessUnits[i], timestampUs: i * 33_333L, isKeyFrame: true);

                    for (var packet = 0; packet < 3; packet++)
                    {
                        var index = i * 3 + packet;
                        if (index is >= 40 and < 49) continue; // lost packets
                        recorder.WriteAudio(tone, timestampUs: index * 480L * 1_000_000 / 44100);
                    }
                }

                Assert.Equal(frameCount, recorder.FrameCount);
                Assert.True(recorder.AudioPacketCount > 70, $"only {recorder.AudioPacketCount} audio packets were accepted");
            }

            var probe = Mp4Probe.Read(path);
            Assert.Equal(2, probe.StreamCount);
            Assert.Equal(AVCodecID.AV_CODEC_ID_H264, probe.CodecId);
            Assert.Equal(frameCount, probe.PacketCount);
            Assert.Equal(AVCodecID.AV_CODEC_ID_AAC, probe.AudioCodecId);
            Assert.Equal(44100, probe.AudioSampleRate);
            Assert.True(probe.AudioExtradataLength > 0, "the audio track should carry an AudioSpecificConfig");
            // A second of audio is about 43 packets of 1024 samples; the hole was filled with
            // silence rather than closed up, so the count covers the whole second.
            Assert.InRange(probe.AudioPacketCount, 40, 48);
            Assert.InRange(probe.AudioDuration.TotalSeconds, 0.9, 1.15);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    /// <summary>Interleaved 16-bit stereo PCM of one packet of a sine wave.</summary>
    private static byte[] Tone(int frames, int sampleRate, double frequency)
    {
        var pcm = new byte[frames * 4];
        for (var i = 0; i < frames; i++)
        {
            var sample = (short)(Math.Sin(2 * Math.PI * frequency * i / sampleRate) * 12000);
            BitConverter.TryWriteBytes(pcm.AsSpan(i * 4), sample);
            BitConverter.TryWriteBytes(pcm.AsSpan(i * 4 + 2), sample);
        }
        return pcm;
    }

    [SkippableFact]
    public void RefusesToStartWithoutACodecConfiguration()
    {
        Skip.IfNot(FFmpegRuntime.IsAvailable, SkipReason);

        var format = new VideoFormat(VideoCodec.H264, 1920, 1080, [], 60);
        var path = Path.Combine(Path.GetTempPath(), $"soulscreen-record-{Guid.NewGuid():N}.mp4");

        var failure = Assert.Throws<FFmpegUnavailableException>(() => SessionRecorder.Create(path, format));
        Assert.Contains("codec configuration", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Builds a format from the parameter sets carried in an access unit.</summary>
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
}

/// <summary>Reads an MP4 back through FFmpeg's demuxer, which is independent of the muxer
/// that wrote it.</summary>
internal static unsafe class Mp4Probe
{
    internal sealed record Result(
        int StreamCount,
        AVCodecID CodecId,
        int Width,
        int Height,
        int ExtradataLength,
        int PacketCount,
        AVCodecID AudioCodecId = AVCodecID.AV_CODEC_ID_NONE,
        int AudioSampleRate = 0,
        int AudioExtradataLength = 0,
        int AudioPacketCount = 0,
        TimeSpan AudioDuration = default);

    public static Result Read(string path)
    {
        FFmpegRuntime.ThrowIfUnavailable();

        AVFormatContext* format = null;
        var result = ffmpeg.avformat_open_input(&format, path, null, null);
        if (result < 0)
            throw new InvalidOperationException($"Could not open {path}: {FFmpegRuntime.DescribeError(result)}");

        try
        {
            result = ffmpeg.avformat_find_stream_info(format, null);
            if (result < 0)
                throw new InvalidOperationException($"Could not read stream info: {FFmpegRuntime.DescribeError(result)}");

            var stream = format->streams[0];
            var parameters = stream->codecpar;
            var audio = format->nb_streams > 1 ? format->streams[1] : null;

            var packets = 0;
            var audioPackets = 0;
            var packet = ffmpeg.av_packet_alloc();
            try
            {
                while (ffmpeg.av_read_frame(format, packet) >= 0)
                {
                    if (packet->stream_index == 0) packets++;
                    else audioPackets++;
                    ffmpeg.av_packet_unref(packet);
                }
            }
            finally
            {
                ffmpeg.av_packet_free(&packet);
            }

            var audioDuration = TimeSpan.Zero;
            if (audio is not null && audio->duration > 0)
                audioDuration = TimeSpan.FromSeconds(audio->duration * ffmpeg.av_q2d(audio->time_base));

            return new Result(
                (int)format->nb_streams,
                parameters->codec_id,
                parameters->width,
                parameters->height,
                parameters->extradata_size,
                packets,
                audio is null ? AVCodecID.AV_CODEC_ID_NONE : audio->codecpar->codec_id,
                audio is null ? 0 : audio->codecpar->sample_rate,
                audio is null ? 0 : audio->codecpar->extradata_size,
                audioPackets,
                audioDuration);
        }
        finally
        {
            ffmpeg.avformat_close_input(&format);
        }
    }
}
