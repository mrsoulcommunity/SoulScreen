namespace SoulScreen.AirPlay.Streams;

/// <summary>
/// Decides which arriving audio packets carry sound that has not been played yet.
/// <para>
/// A mirroring sender does not send each audio packet once. SoulScreen advertises
/// <see cref="AirPlayFeatures.AudioRedundant"/>, and iOS takes it at its word: every packet
/// goes out several times over, so the ninety-odd packets a second that 480-sample AAC-ELD
/// frames make arrive as nearly three hundred. The copies decrypt and decode exactly as the
/// original does, which is what made them easy to miss. Played as they arrive, the sound is
/// fed in at three times the rate the sound card plays it, into a buffer that can only be kept
/// from overflowing by cutting most of it back out again - and what survives is garbled,
/// stuttering and late.
/// </para>
/// <para>
/// So a packet is let through only when it is newer, by sequence number and by timestamp,
/// than the last one that was. The copies that follow it are dropped, and so is anything
/// that turns up after a newer packet has already gone through: the moment it was recorded
/// for has passed, and the hole it left has already been dealt with downstream.
/// </para>
/// <para>Not thread-safe; call it from the single thread that receives the packets.</para>
/// </summary>
public sealed class RtpSequenceFilter
{
    /// <summary>
    /// Refusals in a row after which the sender is taken to have restarted its numbering.
    /// Redundant copies never come near it - every run of them ends at the next new packet -
    /// but a sequence that jumped backwards would otherwise be refused for good.
    /// </summary>
    private const int ResyncAfterRefusals = 64;

    /// <summary>A forward jump at least this long is a restart rather than loss.</summary>
    private const int MaximumCountedLoss = 1000;

    private ushort _lastSequence;
    private uint _lastTimestamp;
    private bool _started;
    private int _refusalsInARow;

    /// <summary>Packets let through.</summary>
    public long AcceptedCount { get; private set; }

    /// <summary>Copies of a packet that had already been let through.</summary>
    public long DuplicateCount { get; private set; }

    /// <summary>Packets older than one already let through: reordered, or an older copy.</summary>
    public long LateCount { get; private set; }

    /// <summary>Packets skipped over in the sequence that no copy of ever arrived.</summary>
    public long LostCount { get; private set; }

    /// <summary>
    /// Returns true if the packet with this RTP sequence number and timestamp should be
    /// played, and false if it is a copy of one already played or has arrived too late.
    /// </summary>
    public bool Accept(ushort sequence, uint timestamp)
    {
        if (_started && _refusalsInARow < ResyncAfterRefusals)
        {
            if (sequence == _lastSequence || timestamp == _lastTimestamp)
            {
                DuplicateCount++;
                _refusalsInARow++;
                return false;
            }

            // Both fields wrap, so "newer" means less than half of their range ahead.
            var sequenceAdvance = (ushort)(sequence - _lastSequence);
            var timestampAdvance = timestamp - _lastTimestamp;
            if (sequenceAdvance >= 0x8000 || timestampAdvance >= 0x8000_0000)
            {
                LateCount++;
                _refusalsInARow++;
                return false;
            }

            if (sequenceAdvance - 1 is > 0 and < MaximumCountedLoss) LostCount += sequenceAdvance - 1;
        }

        _lastSequence = sequence;
        _lastTimestamp = timestamp;
        _started = true;
        _refusalsInARow = 0;
        AcceptedCount++;
        return true;
    }
}
