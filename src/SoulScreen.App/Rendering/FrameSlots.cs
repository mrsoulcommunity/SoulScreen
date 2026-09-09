namespace SoulScreen.App.Rendering;

/// <summary>
/// A small hand-off queue of decoded pictures between the decode thread and the UI thread.
/// <para>
/// It is a queue rather than a single latest-wins slot because arrival is not even. Frames
/// leave the phone at a steady rate but reach the decoder in bursts, and showing only the
/// newest at each composition pass turns that jitter straight into uneven motion: two frames
/// arriving together means one is never seen. Releasing one per pass instead spreads a burst
/// back out, which is what the eye reads as smooth.
/// </para>
/// <para>
/// The queue stays shallow on purpose. Every frame held is latency, so once it is deeper
/// than <see cref="MaxHeldFrames"/> the oldest are discarded rather than played out - a
/// picture that is behind is worse than one that skipped.
/// </para>
/// </summary>
internal sealed class FrameSlots
{
    /// <summary>
    /// Slots in the pool: enough for the writer, the reader, and a couple queued between
    /// them.
    /// </summary>
    private const int SlotCount = 5;

    /// <summary>
    /// How many finished pictures may wait. Two is one composition pass of slack at any
    /// sensible refresh rate, which absorbs jitter without adding visible delay.
    /// </summary>
    private const int MaxHeldFrames = 2;

    private readonly byte[][] _slots = new byte[SlotCount][];
    private readonly Queue<int> _ready = new(SlotCount);
    private readonly object _lock = new();

    /// <summary>Slot the decode thread is filling.</summary>
    private int _writeIndex;

    /// <summary>Slot the UI thread is copying out of, or -1.</summary>
    private int _readingIndex = -1;

    public FrameSlots(int byteCount)
    {
        ByteCount = byteCount;
        for (var i = 0; i < SlotCount; i++) _slots[i] = new byte[byteCount];
    }

    public int ByteCount { get; }

    /// <summary>Finished pictures waiting to be shown.</summary>
    public int QueuedCount
    {
        get { lock (_lock) return _ready.Count; }
    }

    /// <summary>The array to fill. Safe from the writing thread until <see cref="Publish"/>.</summary>
    public byte[] BeginWrite()
    {
        lock (_lock) return _slots[_writeIndex];
    }

    /// <summary>
    /// Queues the slot just filled and moves the writer on.
    /// </summary>
    /// <returns>The number of older pictures discarded to stay within the depth limit.</returns>
    public int Publish()
    {
        lock (_lock)
        {
            _ready.Enqueue(_writeIndex);

            var discarded = 0;
            while (_ready.Count > MaxHeldFrames)
            {
                _ready.Dequeue();
                discarded++;
            }

            _writeIndex = NextFree();
            return discarded;
        }
    }

    /// <summary>
    /// Takes the oldest waiting picture, or null when none has arrived. The array stays
    /// valid until the matching <see cref="EndRead"/>.
    /// </summary>
    public byte[]? BeginRead()
    {
        lock (_lock)
        {
            if (_ready.Count == 0) return null;
            _readingIndex = _ready.Dequeue();
            return _slots[_readingIndex];
        }
    }

    /// <summary>Releases the slot taken by <see cref="BeginRead"/> back to the writer.</summary>
    public void EndRead()
    {
        lock (_lock) _readingIndex = -1;
    }

    /// <summary>Picks a slot that is neither queued nor being read.</summary>
    private int NextFree()
    {
        for (var offset = 1; offset <= SlotCount; offset++)
        {
            var candidate = (_writeIndex + offset) % SlotCount;
            if (candidate == _readingIndex) continue;
            if (_ready.Contains(candidate)) continue;
            return candidate;
        }
        // Unreachable: at most MaxHeldFrames + 1 slots are ever spoken for.
        return _writeIndex;
    }
}
