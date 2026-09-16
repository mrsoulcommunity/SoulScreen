namespace SoulScreen.App.Logic;

/// <summary>
/// A short history of session figures, for the sparkline under the statistics overlay.
/// <para>
/// The half-second metrics tick appends one sample; the graph draws what the buffer holds,
/// dropping whole points rather than rescaling the past when it runs out of room. A fixed
/// ring means the graph never changes what it shows about a spike that has already
/// scrolled away.
/// </para>
/// Deliberately free of WPF so the buffering can be tested on its own.
/// </summary>
internal sealed class MetricsHistory
{
    private readonly double[] _values;
    private int _count;
    private int _head;

    public MetricsHistory(int capacity)
    {
        if (capacity < 2) throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "a sparkline needs somewhere to move");
        _values = new double[capacity];
    }

    public int Capacity => _values.Length;
    public int Count => _count;

    /// <summary>True once the buffer has wrapped: new points now overwrite the oldest.</summary>
    public bool IsFull => _count == _values.Length;

    /// <summary>Appends a reading, dropping the oldest when full.</summary>
    public void Add(double value)
    {
        // A NaN would poison every min/max and break the line's geometry.
        if (double.IsNaN(value)) value = 0;
        _values[_head] = value;
        _head = (_head + 1) % _values.Length;
        if (_count < _values.Length) _count++;
    }

    /// <summary>Empties the buffer, for the start of a session.</summary>
    public void Clear()
    {
        _count = 0;
        _head = 0;
    }

    /// <summary>The readings, oldest first. The array is internal state copied out, so the
    /// caller can never see a half-written sample.</summary>
    public double[] ToArray()
    {
        var result = new double[_count];
        CopyTo(result.AsSpan());
        return result;
    }

    /// <summary>Copies the readings, oldest first, into <paramref name="destination"/>.
    /// Avoids the per-call array allocation that <see cref="ToArray"/> would make on
    /// every metrics tick when the graph is being redrawn.</summary>
    public void CopyTo(Span<double> destination)
    {
        var n = Math.Min(_count, destination.Length);
        var start = IsFull ? _head : 0;
        for (var i = 0; i < n; i++)
            destination[i] = _values[(start + i) % _values.Length];
    }

    /// <summary>Highest reading, or null while empty.</summary>
    public double? Max()
    {
        if (_count == 0) return null;
        var max = double.MinValue;
        for (var i = 0; i < _count; i++) max = Math.Max(max, _values[i]);
        return max;
    }
}
