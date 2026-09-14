using SoulScreen.AirPlay.Streams;
using Xunit;

namespace SoulScreen.Tests;

public class RtpSequenceFilterTests
{
    /// <summary>RTP timestamp advance per AAC-ELD packet.</summary>
    private const uint Samples = 480;

    /// <summary>
    /// What a real session recorded: some 270 audio packets a second where 480-sample frames at
    /// 44.1 kHz make 92, and not one of them counted as lost - every packet sent three times,
    /// back to back.
    /// </summary>
    [Fact]
    public void LetsEachPacketThroughOnceHoweverManyCopiesArrive()
    {
        var filter = new RtpSequenceFilter();
        var delivered = new List<int>();

        for (var n = 0; n < 100; n++)
            for (var copy = 0; copy < 3; copy++)
                if (filter.Accept((ushort)(5000 + n), 1000 + (uint)n * Samples)) delivered.Add(n);

        Assert.Equal(Enumerable.Range(0, 100), delivered);
        Assert.Equal(200, filter.DuplicateCount);
        Assert.Equal(0, filter.LostCount);
    }

    /// <summary>A copy sent under a sequence number of its own still carries the timestamp of
    /// the audio it repeats.</summary>
    [Fact]
    public void RecognisesCopiesSentUnderSequenceNumbersOfTheirOwn()
    {
        var filter = new RtpSequenceFilter();
        ushort sequence = 0;
        var delivered = 0;

        for (uint n = 0; n < 50; n++)
            for (var copy = 0; copy < 3; copy++)
                if (filter.Accept(sequence++, n * Samples)) delivered++;

        Assert.Equal(50, delivered);
    }

    /// <summary>A packet overtaken on the way, and a copy trailing a newer packet, are both too
    /// late to play: the moment they belong to has gone.</summary>
    [Fact]
    public void RefusesPacketsOlderThanOneAlreadyLetThrough()
    {
        var filter = new RtpSequenceFilter();

        Assert.True(filter.Accept(10, 10 * Samples));
        Assert.True(filter.Accept(12, 12 * Samples));
        Assert.False(filter.Accept(11, 11 * Samples));
        Assert.False(filter.Accept(10, 10 * Samples));
        Assert.True(filter.Accept(13, 13 * Samples));

        Assert.Equal(2, filter.LateCount);
        Assert.Equal(1, filter.LostCount);
    }

    [Fact]
    public void CountsPacketsNoCopyOfWhichArrived()
    {
        var filter = new RtpSequenceFilter();

        foreach (ushort n in new ushort[] { 1, 2, 5, 6 })
            Assert.True(filter.Accept(n, n * Samples));

        Assert.Equal(2, filter.LostCount);
    }

    [Fact]
    public void FollowsBothCountersAcrossTheirWrap()
    {
        var filter = new RtpSequenceFilter();
        ushort sequence = 65534;
        var timestamp = uint.MaxValue - Samples;

        for (var i = 0; i < 4; i++)
        {
            Assert.True(filter.Accept(sequence, timestamp));
            Assert.False(filter.Accept(sequence, timestamp));
            sequence = unchecked((ushort)(sequence + 1));
            timestamp = unchecked(timestamp + Samples);
        }

        Assert.Equal(0, filter.LostCount);
        Assert.Equal(0, filter.LateCount);
    }

    /// <summary>
    /// A sender that restarts its numbering further back looks, one packet at a time, exactly
    /// like a run of stale packets. It is refused until that can no longer be reordering, and
    /// then followed - not refused for the rest of the session.
    /// </summary>
    [Fact]
    public void FollowsASenderThatRestartsItsNumbering()
    {
        var filter = new RtpSequenceFilter();
        for (ushort n = 40000; n < 40010; n++) filter.Accept(n, n * Samples);

        var accepted = 0;
        for (ushort n = 100; n < 300; n++)
            if (filter.Accept(n, n * Samples)) accepted++;

        Assert.True(accepted > 100, $"only {accepted} of 200 packets after the restart got through");
    }
}
