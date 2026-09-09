namespace SoulScreen.App.Rendering;

/// <summary>
/// A three-slot hand-off of decoded pictures between the decode thread and the UI thread.
/// <para>
/// Three, not one: with a single buffer the writer would overwrite pixels the reader is
/// still copying out, which tears. Three is the smallest count where a writer, a reader and
/// a queued picture can each own one, so neither side ever waits for the other and the only
/// synchronised work is swapping a pair of indices.
/// </para>
/// <para>
/// The slots hold managed arrays rather than the shared section itself, so that the section
/// - which WPF reads asynchronously - is only ever written on the UI thread, immediately
/// before the frame it belongs to is composed.
/// </para>
/// </summary>
internal sealed class FrameSlots
{
    private const int SlotCount = 3;

    private readonly byte[][] _slots = new byte[SlotCount][];
    private readonly object _lock = new();

    /// <summary>Slot the decode thread is filling.</summary>
    private int _writeIndex;

    /// <summary>Slot holding a finished picture not yet taken, or -1.</summary>
    private int _readyIndex = -1;

    /// <summary>Slot the UI thread is copying out of, or -1.</summary>
    private int _readingIndex = -1;

    public FrameSlots(int byteCount)
    {
        ByteCount = byteCount;
        for (var i = 0; i < SlotCount; i++) _slots[i] = new byte[byteCount];
    }

    public int ByteCount { get; }

    /// <summary>True when a finished picture is waiting to be shown.</summary>
    public bool HasReady
    {
        get { lock (_lock) return _readyIndex >= 0; }
    }

    /// <summary>The array to fill. Safe from the writing thread until <see cref="Publish"/>.</summary>
    public byte[] BeginWrite()
    {
        lock (_lock) return _slots[_writeIndex];
    }

    /// <summary>
    /// Marks the slot just filled as the one to show, and moves the writer to a free slot.
    /// </summary>
    /// <returns>True when this replaced a picture that was never shown.</returns>
    public bool Publish()
    {
        lock (_lock)
        {
            var superseded = _readyIndex >= 0;
            _readyIndex = _writeIndex;
            _writeIndex = NextFree();
            return superseded;
        }
    }

    /// <summary>
    /// Takes the waiting picture, or null when none has arrived. The array stays valid until
    /// the matching <see cref="EndRead"/>.
    /// </summary>
    public byte[]? BeginRead()
    {
        lock (_lock)
        {
            if (_readyIndex < 0) return null;
            _readingIndex = _readyIndex;
            _readyIndex = -1;
            return _slots[_readingIndex];
        }
    }

    /// <summary>Releases the slot taken by <see cref="BeginRead"/> back to the writer.</summary>
    public void EndRead()
    {
        lock (_lock) _readingIndex = -1;
    }

    /// <summary>Picks a slot that is neither waiting to be shown nor being read.</summary>
    private int NextFree()
    {
        for (var offset = 1; offset <= SlotCount; offset++)
        {
            var candidate = (_writeIndex + offset) % SlotCount;
            if (candidate != _readyIndex && candidate != _readingIndex) return candidate;
        }
        // Unreachable with three slots and at most two reserved.
        return _writeIndex;
    }
}
