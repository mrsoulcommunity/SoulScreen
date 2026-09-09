using System.Collections;
using System.Globalization;

namespace SoulScreen.AirPlay.Plist;

public enum PlistKind { Null, Boolean, Integer, Real, String, Data, Date, Array, Dictionary }

/// <summary>
/// A property-list node. AirPlay carries almost every non-trivial message as a binary
/// plist, so this is the common currency between the RTSP handlers and the wire format.
/// </summary>
public abstract class PlistValue
{
    public abstract PlistKind Kind { get; }

    public static readonly PlistValue Null = new PlistNull();

    public static implicit operator PlistValue(string value) => new PlistString(value);
    public static implicit operator PlistValue(bool value) => new PlistBoolean(value);
    public static implicit operator PlistValue(long value) => new PlistInteger(value);
    public static implicit operator PlistValue(int value) => new PlistInteger(value);
    public static implicit operator PlistValue(double value) => new PlistReal(value);
    public static implicit operator PlistValue(byte[] value) => new PlistData(value);

    public string AsString() => this switch
    {
        PlistString s => s.Value,
        PlistInteger i => i.Value.ToString(CultureInfo.InvariantCulture),
        PlistReal r => r.Value.ToString(CultureInfo.InvariantCulture),
        PlistBoolean b => b.Value ? "true" : "false",
        _ => throw new InvalidOperationException($"Cannot read {Kind} as string."),
    };

    public long AsInteger() => this switch
    {
        PlistInteger i => i.Value,
        PlistReal r => (long)r.Value,
        PlistBoolean b => b.Value ? 1 : 0,
        PlistString s when long.TryParse(s.Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) => v,
        _ => throw new InvalidOperationException($"Cannot read {Kind} as integer."),
    };

    public double AsReal() => this switch
    {
        PlistReal r => r.Value,
        PlistInteger i => i.Value,
        _ => throw new InvalidOperationException($"Cannot read {Kind} as real."),
    };

    public bool AsBoolean() => this switch
    {
        PlistBoolean b => b.Value,
        PlistInteger i => i.Value != 0,
        PlistString s => s.Value is "true" or "1" or "YES",
        _ => throw new InvalidOperationException($"Cannot read {Kind} as boolean."),
    };

    public byte[] AsData() => this is PlistData d
        ? d.Value
        : throw new InvalidOperationException($"Cannot read {Kind} as data.");

    public PlistDictionary AsDictionary() => this as PlistDictionary
        ?? throw new InvalidOperationException($"Cannot read {Kind} as dictionary.");

    public PlistArray AsArray() => this as PlistArray
        ?? throw new InvalidOperationException($"Cannot read {Kind} as array.");
}

public sealed class PlistNull : PlistValue
{
    public override PlistKind Kind => PlistKind.Null;
    public override string ToString() => "<null>";
}

public sealed class PlistBoolean(bool value) : PlistValue
{
    public bool Value { get; } = value;
    public override PlistKind Kind => PlistKind.Boolean;
    public override string ToString() => Value ? "true" : "false";
}

public sealed class PlistInteger(long value) : PlistValue
{
    public long Value { get; } = value;
    public override PlistKind Kind => PlistKind.Integer;
    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);
}

public sealed class PlistReal(double value) : PlistValue
{
    public double Value { get; } = value;
    public override PlistKind Kind => PlistKind.Real;
    public override string ToString() => Value.ToString("R", CultureInfo.InvariantCulture);
}

public sealed class PlistString(string value) : PlistValue
{
    public string Value { get; } = value;
    public override PlistKind Kind => PlistKind.String;
    public override string ToString() => Value;
}

public sealed class PlistData(byte[] value) : PlistValue
{
    public byte[] Value { get; } = value;
    public override PlistKind Kind => PlistKind.Data;
    public override string ToString() => $"<{Value.Length} bytes>";
}

public sealed class PlistDate(DateTime utc) : PlistValue
{
    /// <summary>Plist dates are seconds since 2001-01-01 UTC, the Apple epoch.</summary>
    public static readonly DateTime AppleEpoch = new(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public DateTime ValueUtc { get; } = utc;
    public override PlistKind Kind => PlistKind.Date;
    public double SecondsSinceAppleEpoch => (ValueUtc - AppleEpoch).TotalSeconds;
    public static PlistDate FromAppleSeconds(double seconds) => new(AppleEpoch.AddSeconds(seconds));
    public override string ToString() => ValueUtc.ToString("O");
}

public sealed class PlistArray : PlistValue, IList<PlistValue>
{
    private readonly List<PlistValue> _items = [];

    public PlistArray() { }
    public PlistArray(IEnumerable<PlistValue> items) => _items.AddRange(items);

    public override PlistKind Kind => PlistKind.Array;

    public PlistValue this[int index] { get => _items[index]; set => _items[index] = value; }
    public int Count => _items.Count;
    public bool IsReadOnly => false;
    public void Add(PlistValue item) => _items.Add(item);
    public void Clear() => _items.Clear();
    public bool Contains(PlistValue item) => _items.Contains(item);
    public void CopyTo(PlistValue[] array, int arrayIndex) => _items.CopyTo(array, arrayIndex);
    public IEnumerator<PlistValue> GetEnumerator() => _items.GetEnumerator();
    public int IndexOf(PlistValue item) => _items.IndexOf(item);
    public void Insert(int index, PlistValue item) => _items.Insert(index, item);
    public bool Remove(PlistValue item) => _items.Remove(item);
    public void RemoveAt(int index) => _items.RemoveAt(index);
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    public override string ToString() => "[" + string.Join(", ", _items) + "]";
}

public sealed class PlistDictionary : PlistValue, IEnumerable<KeyValuePair<string, PlistValue>>
{
    // Insertion order is preserved: a few senders parse our responses loosely and are
    // easier to debug against a reference capture when key order matches.
    private readonly Dictionary<string, PlistValue> _map = [];
    private readonly List<string> _order = [];

    public override PlistKind Kind => PlistKind.Dictionary;

    public int Count => _order.Count;

    public IReadOnlyList<string> Keys => _order;

    public PlistValue this[string key]
    {
        get => _map[key];
        set
        {
            if (!_map.ContainsKey(key)) _order.Add(key);
            _map[key] = value;
        }
    }

    public bool ContainsKey(string key) => _map.ContainsKey(key);

    public bool TryGetValue(string key, out PlistValue value) => _map.TryGetValue(key, out value!);

    public PlistValue? GetOrDefault(string key) => _map.TryGetValue(key, out var v) ? v : null;

    public string? GetString(string key) => _map.TryGetValue(key, out var v) && v is not PlistNull ? v.AsString() : null;

    public long? GetInteger(string key) => _map.TryGetValue(key, out var v) && v is not PlistNull ? v.AsInteger() : null;

    public bool? GetBoolean(string key) => _map.TryGetValue(key, out var v) && v is not PlistNull ? v.AsBoolean() : null;

    public byte[]? GetData(string key) => _map.TryGetValue(key, out var v) && v is PlistData d ? d.Value : null;

    public PlistDictionary? GetDictionary(string key) => _map.TryGetValue(key, out var v) ? v as PlistDictionary : null;

    public PlistArray? GetArray(string key) => _map.TryGetValue(key, out var v) ? v as PlistArray : null;

    public bool Remove(string key) => _map.Remove(key) && _order.Remove(key);

    public IEnumerator<KeyValuePair<string, PlistValue>> GetEnumerator()
    {
        foreach (var key in _order) yield return new KeyValuePair<string, PlistValue>(key, _map[key]);
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public override string ToString() =>
        "{" + string.Join(", ", this.Select(kv => kv.Key + "=" + kv.Value)) + "}";
}
