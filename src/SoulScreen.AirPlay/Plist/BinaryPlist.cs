using System.Buffers.Binary;
using System.Text;
using SoulScreen.Core.Buffers;

namespace SoulScreen.AirPlay.Plist;

/// <summary>
/// Reader and writer for Apple's <c>bplist00</c> container - the format AirPlay uses for
/// SETUP, /info, SET_PARAMETER and most other structured RTSP bodies.
/// </summary>
public static class BinaryPlist
{
    private static readonly byte[] Magic = "bplist00"u8.ToArray();

    public static bool LooksLikeBinaryPlist(ReadOnlySpan<byte> data) =>
        data.Length >= 8 && data[..8].SequenceEqual(Magic);

    // ---------------------------------------------------------------- reading

    public static PlistValue Read(ReadOnlySpan<byte> data)
    {
        if (!LooksLikeBinaryPlist(data))
            throw new InvalidDataException("Not a binary plist (missing bplist00 magic).");
        if (data.Length < 8 + 32)
            throw new InvalidDataException("Binary plist is too short to hold a trailer.");

        var trailer = data[^32..];
        int offsetIntSize = trailer[6];
        int objectRefSize = trailer[7];
        var numObjects = BinaryPrimitives.ReadUInt64BigEndian(trailer[8..16]);
        var topObject = BinaryPrimitives.ReadUInt64BigEndian(trailer[16..24]);
        var offsetTableOffset = BinaryPrimitives.ReadUInt64BigEndian(trailer[24..32]);

        if (offsetIntSize is < 1 or > 8 || objectRefSize is < 1 or > 8)
            throw new InvalidDataException("Binary plist trailer has an implausible integer size.");
        if (numObjects > (ulong)data.Length || topObject >= numObjects)
            throw new InvalidDataException("Binary plist trailer is inconsistent with the payload.");
        if (offsetTableOffset + numObjects * (ulong)offsetIntSize > (ulong)data.Length)
            throw new InvalidDataException("Binary plist offset table runs past the end of the payload.");

        var offsets = new int[numObjects];
        for (var i = 0UL; i < numObjects; i++)
        {
            var slice = data.Slice((int)(offsetTableOffset + i * (ulong)offsetIntSize), offsetIntSize);
            offsets[i] = (int)ReadSizedUInt(slice);
        }

        var ctx = new ReadContext(offsets, objectRefSize);
        return ReadObject(data, (int)topObject, ctx);
    }

    private sealed class ReadContext(int[] offsets, int refSize)
    {
        public int[] Offsets { get; } = offsets;
        public int RefSize { get; } = refSize;
        /// <summary>Guards against a maliciously self-referencing object graph.</summary>
        public int Depth;
    }

    private static PlistValue ReadObject(ReadOnlySpan<byte> data, int index, ReadContext ctx)
    {
        if (index < 0 || index >= ctx.Offsets.Length)
            throw new InvalidDataException($"Object reference {index} is out of range.");
        if (++ctx.Depth > 64)
            throw new InvalidDataException("Binary plist nesting is too deep.");
        try
        {
            var reader = new SpanReader(data) { Position = ctx.Offsets[index] };
            var marker = reader.ReadUInt8();
            var high = marker >> 4;
            var low = marker & 0x0f;

            switch (high)
            {
                case 0x0:
                    return low switch
                    {
                        0x0 => PlistValue.Null,
                        0x8 => new PlistBoolean(false),
                        0x9 => new PlistBoolean(true),
                        0xf => PlistValue.Null, // fill byte
                        _ => throw new InvalidDataException($"Unknown primitive marker 0x{marker:x2}."),
                    };

                case 0x1: // integer, 2^low bytes
                {
                    var size = 1 << low;
                    var bytes = reader.ReadBytes(size);
                    // 1/2/4-byte integers are unsigned; 8-byte and 16-byte are signed two's complement.
                    return size switch
                    {
                        1 => new PlistInteger(bytes[0]),
                        2 => new PlistInteger(BinaryPrimitives.ReadUInt16BigEndian(bytes)),
                        4 => new PlistInteger(BinaryPrimitives.ReadUInt32BigEndian(bytes)),
                        8 => new PlistInteger(BinaryPrimitives.ReadInt64BigEndian(bytes)),
                        // 128-bit integers only occur for huge values; keep the low 64 bits.
                        16 => new PlistInteger(BinaryPrimitives.ReadInt64BigEndian(bytes[8..])),
                        _ => throw new InvalidDataException($"Unsupported integer width {size}."),
                    };
                }

                case 0x2: // real
                {
                    var size = 1 << low;
                    var bytes = reader.ReadBytes(size);
                    return size switch
                    {
                        4 => new PlistReal(BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32BigEndian(bytes))),
                        8 => new PlistReal(BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64BigEndian(bytes))),
                        _ => throw new InvalidDataException($"Unsupported real width {size}."),
                    };
                }

                case 0x3: // date
                    return PlistDate.FromAppleSeconds(
                        BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64BigEndian(reader.ReadBytes(8))));

                case 0x4: // data
                {
                    var count = ReadCount(ref reader, low, data, ctx);
                    return new PlistData(reader.ReadBytes(count).ToArray());
                }

                case 0x5: // ASCII string
                {
                    var count = ReadCount(ref reader, low, data, ctx);
                    return new PlistString(Encoding.ASCII.GetString(reader.ReadBytes(count)));
                }

                case 0x6: // UTF-16BE string, count is in characters
                {
                    var count = ReadCount(ref reader, low, data, ctx);
                    return new PlistString(Encoding.BigEndianUnicode.GetString(reader.ReadBytes(count * 2)));
                }

                case 0x8: // UID - only appears in keyed archives; surface it as an integer
                {
                    var bytes = reader.ReadBytes(low + 1);
                    return new PlistInteger((long)ReadSizedUInt(bytes));
                }

                case 0xa: // array
                case 0xc: // set - treat like an array
                {
                    var count = ReadCount(ref reader, low, data, ctx);
                    var array = new PlistArray();
                    for (var i = 0; i < count; i++)
                        array.Add(ReadObject(data, (int)ReadSizedUInt(reader.ReadBytes(ctx.RefSize)), ctx));
                    return array;
                }

                case 0xd: // dictionary
                {
                    var count = ReadCount(ref reader, low, data, ctx);
                    var keyRefs = new int[count];
                    for (var i = 0; i < count; i++) keyRefs[i] = (int)ReadSizedUInt(reader.ReadBytes(ctx.RefSize));
                    var dict = new PlistDictionary();
                    for (var i = 0; i < count; i++)
                    {
                        var valueRef = (int)ReadSizedUInt(reader.ReadBytes(ctx.RefSize));
                        var key = ReadObject(data, keyRefs[i], ctx);
                        dict[key.AsString()] = ReadObject(data, valueRef, ctx);
                    }
                    return dict;
                }

                default:
                    throw new InvalidDataException($"Unknown object marker 0x{marker:x2}.");
            }
        }
        finally
        {
            ctx.Depth--;
        }
    }

    /// <summary>Collection markers store a length of 0-14 inline; 15 means an integer object follows.</summary>
    private static int ReadCount(ref SpanReader reader, int low, ReadOnlySpan<byte> data, ReadContext ctx)
    {
        if (low != 0x0f) return low;
        var marker = reader.ReadUInt8();
        if ((marker & 0xf0) != 0x10)
            throw new InvalidDataException("Expected an integer marker for an extended length.");
        var count = (long)ReadSizedUInt(reader.ReadBytes(1 << (marker & 0x0f)));
        if (count < 0 || count > data.Length)
            throw new InvalidDataException($"Extended length {count} is out of range.");
        return (int)count;
    }

    private static ulong ReadSizedUInt(ReadOnlySpan<byte> bytes)
    {
        ulong value = 0;
        foreach (var b in bytes) value = (value << 8) | b;
        return value;
    }

    // ---------------------------------------------------------------- writing

    public static byte[] Write(PlistValue root)
    {
        var objects = new List<PlistValue>();
        var refs = new Dictionary<PlistValue, int>(ReferenceEqualityComparer.Instance);
        // Strings repeat constantly (dictionary keys), so give identical text one object.
        var stringPool = new Dictionary<string, int>(StringComparer.Ordinal);
        Flatten(root, objects, refs, stringPool);

        var refSize = ByteWidth((ulong)Math.Max(objects.Count - 1, 0));
        var body = new BufferWriter(1024);
        body.WriteAscii("bplist00");

        var offsets = new int[objects.Count];
        for (var i = 0; i < objects.Count; i++)
        {
            offsets[i] = body.Length;
            WriteObject(body, objects[i], refs, stringPool, refSize);
        }

        var offsetTableOffset = body.Length;
        var offsetSize = ByteWidth((ulong)offsetTableOffset);
        foreach (var offset in offsets)
            WriteSizedUInt(body, (ulong)offset, offsetSize);

        Span<byte> trailer = stackalloc byte[32];
        trailer.Clear();
        trailer[6] = (byte)offsetSize;
        trailer[7] = (byte)refSize;
        BinaryPrimitives.WriteUInt64BigEndian(trailer[8..16], (ulong)objects.Count);
        BinaryPrimitives.WriteUInt64BigEndian(trailer[16..24], 0); // root is always object 0
        BinaryPrimitives.WriteUInt64BigEndian(trailer[24..32], (ulong)offsetTableOffset);
        body.Write(trailer);

        return body.ToArray();
    }

    private static void Flatten(
        PlistValue value,
        List<PlistValue> objects,
        Dictionary<PlistValue, int> refs,
        Dictionary<string, int> stringPool)
    {
        if (refs.ContainsKey(value)) return;

        if (value is PlistString s && stringPool.TryGetValue(s.Value, out var pooled))
        {
            refs[value] = pooled;
            return;
        }

        var index = objects.Count;
        objects.Add(value);
        refs[value] = index;
        if (value is PlistString str) stringPool[str.Value] = index;

        switch (value)
        {
            case PlistArray array:
                foreach (var item in array) Flatten(item, objects, refs, stringPool);
                break;
            case PlistDictionary dict:
                // Apple writes all keys before all values; the flattening order does not
                // have to match, only the reference table does.
                foreach (var key in dict.Keys) Flatten(new PlistString(key), objects, refs, stringPool);
                foreach (var kv in dict) Flatten(kv.Value, objects, refs, stringPool);
                break;
        }
    }

    private static void WriteObject(
        BufferWriter w,
        PlistValue value,
        Dictionary<PlistValue, int> refs,
        Dictionary<string, int> stringPool,
        int refSize)
    {
        switch (value)
        {
            case PlistNull:
                w.WriteUInt8(0x00);
                break;

            case PlistBoolean b:
                w.WriteUInt8(b.Value ? (byte)0x09 : (byte)0x08);
                break;

            case PlistInteger i:
                WriteInteger(w, i.Value);
                break;

            case PlistReal r:
                w.WriteUInt8(0x23);
                w.WriteDoubleBE(r.Value);
                break;

            case PlistDate d:
                w.WriteUInt8(0x33);
                w.WriteDoubleBE(d.SecondsSinceAppleEpoch);
                break;

            case PlistData data:
                WriteMarkerAndCount(w, 0x40, data.Value.Length);
                w.Write(data.Value);
                break;

            case PlistString s:
                if (IsAscii(s.Value))
                {
                    WriteMarkerAndCount(w, 0x50, s.Value.Length);
                    w.WriteAscii(s.Value);
                }
                else
                {
                    // Count is in UTF-16 code units, which is exactly string.Length in .NET.
                    WriteMarkerAndCount(w, 0x60, s.Value.Length);
                    w.Write(Encoding.BigEndianUnicode.GetBytes(s.Value));
                }
                break;

            case PlistArray array:
                WriteMarkerAndCount(w, 0xa0, array.Count);
                foreach (var item in array)
                    WriteSizedUInt(w, (ulong)Resolve(item, refs, stringPool), refSize);
                break;

            case PlistDictionary dict:
                WriteMarkerAndCount(w, 0xd0, dict.Count);
                foreach (var key in dict.Keys)
                    WriteSizedUInt(w, (ulong)stringPool[key], refSize);
                foreach (var kv in dict)
                    WriteSizedUInt(w, (ulong)Resolve(kv.Value, refs, stringPool), refSize);
                break;

            default:
                throw new NotSupportedException($"Cannot serialise {value.GetType().Name}.");
        }
    }

    private static int Resolve(PlistValue value, Dictionary<PlistValue, int> refs, Dictionary<string, int> stringPool)
        => value is PlistString s && stringPool.TryGetValue(s.Value, out var pooled) ? pooled : refs[value];

    private static void WriteInteger(BufferWriter w, long value)
    {
        // Negative values must use the signed 8-byte form.
        if (value < 0)
        {
            w.WriteUInt8(0x13);
            w.WriteUInt64BE(unchecked((ulong)value));
        }
        else if (value <= byte.MaxValue)
        {
            w.WriteUInt8(0x10);
            w.WriteUInt8((byte)value);
        }
        else if (value <= ushort.MaxValue)
        {
            w.WriteUInt8(0x11);
            w.WriteUInt16BE((ushort)value);
        }
        else if (value <= uint.MaxValue)
        {
            w.WriteUInt8(0x12);
            w.WriteUInt32BE((uint)value);
        }
        else
        {
            w.WriteUInt8(0x13);
            w.WriteUInt64BE((ulong)value);
        }
    }

    private static void WriteMarkerAndCount(BufferWriter w, byte marker, int count)
    {
        if (count < 15)
        {
            w.WriteUInt8((byte)(marker | count));
        }
        else
        {
            w.WriteUInt8((byte)(marker | 0x0f));
            WriteInteger(w, count);
        }
    }

    private static void WriteSizedUInt(BufferWriter w, ulong value, int size)
    {
        for (var shift = (size - 1) * 8; shift >= 0; shift -= 8)
            w.WriteUInt8((byte)(value >> shift));
    }

    private static int ByteWidth(ulong maxValue) => maxValue switch
    {
        <= byte.MaxValue => 1,
        <= ushort.MaxValue => 2,
        <= uint.MaxValue => 4,
        _ => 8,
    };

    private static bool IsAscii(string value)
    {
        foreach (var c in value)
            if (c > 0x7f) return false;
        return true;
    }
}
