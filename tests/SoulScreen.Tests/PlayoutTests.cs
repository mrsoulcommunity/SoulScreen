using System.Buffers.Binary;
using SoulScreen.Media;
using Xunit;

namespace SoulScreen.Tests;

/// <summary>
/// Drives the playout controller with a simulated phone, network and sound card, so the two
/// things that matter - no silence, and no more delay than the network makes necessary - are
/// measured rather than listened for.
/// <para>
/// The sound card takes 441 frames every ten milliseconds, as a shared-mode WASAPI device at
/// 44.1 kHz does; the phone makes a 480-frame packet every 10.9 ms, and the network decides
/// when each one arrives, never out of order. Levels are read when each packet arrives, before
/// it is added - the lowest the buffer gets on the way to that packet.
/// </para>
/// </summary>
public class PlayoutControllerTests
{
    private const int SampleRate = 44100;
    private const int PacketFrames = 480;
    private const int DeviceReadFrames = 441;

    private static readonly double PacketPeriodMs = PacketFrames * 1000.0 / SampleRate;

    private static readonly double ReserveMs = PlayoutController.Reserve.TotalMilliseconds;

    /// <summary>A network that never hiccups: five milliseconds, every packet.</summary>
    private static double Steady(int packet) => 5;

    [Fact]
    public void LeavesAnEvenStreamAloneOnceItHasItsMargin()
    {
        var run = Simulate(60, Steady);

        Assert.Empty(run.SilencesBetween(2, 60));
        Assert.Equal(0, run.Controller!.SkipCount);
        Assert.True(run.Controller.StretchedFrames + run.Controller.ShrunkFrames < 200,
            $"{run.Controller.StretchedFrames} frames stretched and {run.Controller.ShrunkFrames} shrunk on a perfect stream");

        // The delay is the reserve plus the saw-tooth of packets landing and periods leaving.
        Assert.InRange(run.MeanLevelMs(10, 60), ReserveMs - 5, ReserveMs + 20);
    }

    /// <summary>
    /// Three hundred parts per million, either way, is a poor pair of clocks - and ten minutes of
    /// it is 7,938 frames of drift that has to go somewhere. It should all go into single-frame
    /// splices, none of it into silence or skips, and the buffer should not move while it does.
    /// </summary>
    [Theory]
    [InlineData(300)]
    [InlineData(-300)]
    public void TakesClockDriftOutAFrameAtATime(double cardFasterPpm)
    {
        var random = new Random(7);
        var delays = Enumerable.Range(0, 60_000).Select(_ => 5 + random.NextDouble() * 4).ToArray();

        var run = Simulate(600, n => delays[n], cardFasterPpm);
        var controller = run.Controller!;

        Assert.Empty(run.SilencesBetween(3, 600));
        Assert.Equal(0, controller.SkipCount);
        Assert.InRange(run.MaxLevelMs(10, 600), 0, ReserveMs + 30);

        var drift = 600.0 * SampleRate * Math.Abs(cardFasterPpm) / 1_000_000;
        var (towards, against) = cardFasterPpm > 0
            ? (controller.StretchedFrames, controller.ShrunkFrames)
            : (controller.ShrunkFrames, controller.StretchedFrames);

        Assert.InRange(towards - against, drift - 1500, drift + 1500);
        Assert.True(against < drift / 4, $"{against} frames spliced the wrong way while correcting {drift:0}");
    }

    /// <summary>
    /// Wi-Fi holding packets back and releasing them together every fifty milliseconds. The
    /// margin has to be measured at the bottom of each gap, not on average, or the card runs dry
    /// at the end of every one.
    /// </summary>
    [Fact]
    public void RidesOutPacketsArrivingInClumps()
    {
        var run = Simulate(60, n =>
        {
            var made = n * PacketPeriodMs;
            return Math.Ceiling((made + 2) / 50) * 50 - made;
        });

        Assert.Empty(run.SilencesBetween(3, 60));
        Assert.Equal(0, run.Controller!.SkipCount);
        Assert.InRange(run.MeanLevelMs(10, 60), ReserveMs, ReserveMs + 45);
    }

    /// <summary>
    /// Four tenths of a second in which nothing arrives, and then all of it at once. The silence
    /// is unavoidable - nothing can play audio that has not arrived - but what arrives afterwards
    /// is four tenths of a second of delay, and playing everything as it comes keeps that delay
    /// for the rest of the session.
    /// </summary>
    [Fact]
    public void SkipsBackIntoStepAfterAStallInsteadOfStayingBehind()
    {
        static double Stall(int n)
        {
            var made = n * PacketPeriodMs;
            return made is >= 20_000 and < 20_400 ? 20_405 - made : 5;
        }

        var run = Simulate(90, Stall);
        var naive = Simulate(90, Stall, controlled: false);

        Assert.NotEmpty(run.SilencesBetween(20, 20.5));
        Assert.Empty(run.SilencesBetween(20.5, 90));
        Assert.Equal(1, run.Controller!.SkipCount);

        Assert.True(run.MaxLevelMs(25, 90) < 200, $"still {run.MaxLevelMs(25, 90):0} ms behind after the stall");
        Assert.True(naive.MeanLevelMs(25, 90) > 350, $"playing everything left {naive.MeanLevelMs(25, 90):0} ms");

        // The reserve the stall raised is on its way back down by the end.
        Assert.True(run.MeanLevelMs(85, 90) < run.MeanLevelMs(25, 50),
            $"{run.MeanLevelMs(85, 90):0} ms at the end against {run.MeanLevelMs(25, 50):0} ms after the stall");
    }

    /// <summary>
    /// The same stall, longer than the reserve, every ten seconds. The first is heard; raising
    /// the reserve by what it cost is what should stop the rest from being heard too.
    /// </summary>
    [Fact]
    public void RaisesTheReserveSoARecurringStallStopsBeingHeard()
    {
        var run = Simulate(120, n =>
        {
            var made = n * PacketPeriodMs;
            var intoCycle = made % 10_000;
            return made >= 10_000 && intoCycle < 120 ? 125 - intoCycle : 5;
        });

        var heard = Enumerable.Range(1, 11).Count(k => run.SilencesBetween(k * 10, k * 10 + 0.3).Any());
        Assert.NotEmpty(run.SilencesBetween(10, 10.3));
        Assert.True(heard <= 1, $"{heard} of 11 stalls were heard");
        Assert.Empty(run.SilencesBetween(3, 10));
    }

    [Fact]
    public void PrimesTheFirstPacketWithTheReserve()
    {
        var controller = new PlayoutController(SampleRate);

        var first = controller.Next(queuedFrames: 0, PacketFrames, starvedFrames: 0);

        Assert.Equal((int)(PlayoutController.Reserve.Ticks * SampleRate / TimeSpan.TicksPerSecond), first.SilenceFrames);
        Assert.True(first.FadeIn);
        Assert.False(first.Skip);
    }

    private sealed class Outcome(PlayoutController? controller)
    {
        public PlayoutController? Controller { get; } = controller;
        public List<(double AtMs, double QueuedMs)> Levels { get; } = [];
        public List<(double AtMs, int Frames)> Silences { get; } = [];

        public IEnumerable<(double AtMs, int Frames)> SilencesBetween(double fromSeconds, double toSeconds)
            => Silences.Where(s => s.AtMs >= fromSeconds * 1000 && s.AtMs < toSeconds * 1000);

        public double MeanLevelMs(double fromSeconds, double toSeconds) => LevelsBetween(fromSeconds, toSeconds).Average();

        public double MaxLevelMs(double fromSeconds, double toSeconds) => LevelsBetween(fromSeconds, toSeconds).Max();

        private IEnumerable<double> LevelsBetween(double fromSeconds, double toSeconds)
            => Levels.Where(l => l.AtMs >= fromSeconds * 1000 && l.AtMs < toSeconds * 1000).Select(l => l.QueuedMs);
    }

    /// <param name="delayMs">How long after packet n was made it reaches the receiver.</param>
    /// <param name="cardFasterPpm">How much faster the sound card's clock runs than the phone's.</param>
    /// <param name="controlled">False to queue every packet as it arrives, with no controller.</param>
    private static Outcome Simulate(double seconds, Func<int, double> delayMs, double cardFasterPpm = 0, bool controlled = true)
    {
        var outcome = new Outcome(controlled ? new PlayoutController(SampleRate) : null);
        var readPeriodMs = 10.0 / (1 + cardFasterPpm / 1_000_000);
        var endMs = seconds * 1000;

        long queued = 0;
        long starved = 0;
        var nextReadMs = 0.0;
        var packet = 0;
        var nextArrivalMs = delayMs(0);

        while (nextReadMs < endMs || nextArrivalMs < endMs)
        {
            if (nextReadMs <= nextArrivalMs)
            {
                var taken = (int)Math.Min(queued, DeviceReadFrames);
                queued -= taken;
                if (taken < DeviceReadFrames)
                {
                    starved += DeviceReadFrames - taken;
                    outcome.Silences.Add((nextReadMs, DeviceReadFrames - taken));
                }

                nextReadMs += readPeriodMs;
                continue;
            }

            outcome.Levels.Add((nextArrivalMs, queued * 1000.0 / SampleRate));

            if (outcome.Controller is { } controller)
            {
                var step = controller.Next((int)queued, PacketFrames, starved);
                queued += step.SilenceFrames;
                if (!step.Skip) queued += PacketFrames + step.AdjustFrames;
            }
            else
            {
                queued += PacketFrames;
            }

            packet++;
            nextArrivalMs = Math.Max(nextArrivalMs, packet * PacketPeriodMs + delayMs(packet));
        }

        return outcome;
    }
}

public class PcmSpliceTests
{
    private const int Channels = 2;
    private const int Align = 4;

    /// <summary>A rising ramp, the same on both channels, on which any jump shows as a big step.</summary>
    private static byte[] Ramp(int frames, int room = 0)
    {
        var pcm = new byte[(frames + room) * Align];
        for (var frame = 0; frame < frames; frame++)
            for (var channel = 0; channel < Channels; channel++)
                BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(frame * Align + channel * 2), (short)(frame * 10));
        return pcm;
    }

    private static short[] Channel(byte[] pcm, int length, int channel)
        => Enumerable.Range(0, length / Align)
            .Select(frame => BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(frame * Align + channel * 2)))
            .ToArray();

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(7)]
    public void ShrinkingRemovesFramesWithoutAJump(int frames)
    {
        var pcm = Ramp(480);

        var length = PcmSplice.Shrink(pcm, 480 * Align, Channels, frames);

        Assert.Equal((480 - frames) * Align, length);
        for (var channel = 0; channel < Channels; channel++)
        {
            var samples = Channel(pcm, length, channel);
            Assert.Equal(0, samples[0]);
            Assert.Equal(4790, samples[^1]);
            // Still rising everywhere, by at most two of the ramp's steps where a frame was folded away.
            for (var i = 1; i < samples.Length; i++) Assert.InRange(samples[i] - samples[i - 1], 1, 20);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(7)]
    public void StretchingInsertsFramesWithoutAJump(int frames)
    {
        var pcm = Ramp(480, room: frames);

        var length = PcmSplice.Stretch(pcm, 480 * Align, Channels, frames);

        Assert.Equal((480 + frames) * Align, length);
        for (var channel = 0; channel < Channels; channel++)
        {
            var samples = Channel(pcm, length, channel);
            Assert.Equal(0, samples[0]);
            Assert.Equal(4790, samples[^1]);
            // An inserted frame sits halfway between its neighbours, so no step exceeds the ramp's own.
            for (var i = 1; i < samples.Length; i++) Assert.InRange(samples[i] - samples[i - 1], 1, 10);
        }
    }

    [Fact]
    public void FadesRampFromAndToSilence()
    {
        var pcm = new byte[100 * Align];
        for (var offset = 0; offset < pcm.Length; offset += 2)
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(offset), 1000);

        PcmSplice.FadeIn(pcm, Channels, 10);
        PcmSplice.FadeOut(pcm, Channels, 10);

        var left = Channel(pcm, pcm.Length, 0);
        Assert.Equal(0, left[0]);
        Assert.Equal(900, left[9]);
        Assert.Equal(1000, left[10]);
        Assert.Equal(1000, left[89]);
        Assert.Equal(900, left[90]);
        Assert.Equal(0, left[99]);
    }
}
