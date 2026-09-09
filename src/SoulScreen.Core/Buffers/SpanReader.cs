using System.Buffers.Binary;
using System.Text;

namespace SoulScreen.Core.Buffers;

/// <summary>Forward-only reader over a span. Throws on truncated input rather than
/// silently returning zeros, because every protocol here is length-prefixed.</summary>
public ref struct SpanReader(ReadOnlySpan<byte> data)
{
    private readonly ReadOnlySpan<byte> _data = data;

    public int Position { get; set; }

    public readonly int Remaining => _data.Length - Position;

    public readonly bool IsEmpty => Remaining <= 0;

    public byte ReadUInt8() => Take(1)[0];

    public ushort ReadUInt16BE() => BinaryPrimitives.ReadUInt16BigEndian(Take(2));

    public ushort ReadUInt16LE() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));

    public uint ReadUInt24BE()
    {
        var s = Take(3);
        return (uint)((s[0] << 16) | (s[1] << 8) | s[2]);
    }

    public uint ReadUInt32BE() => BinaryPrimitives.ReadUInt32BigEndian(Take(4));

    public uint ReadUInt32LE() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));

    public ulong ReadUInt64BE() => BinaryPrimitives.ReadUInt64BigEndian(Take(8));

    public ulong ReadUInt64LE() => BinaryPrimitives.ReadUInt64LittleEndian(Take(8));

    public double ReadDoubleLE() => BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(Take(8)));

    public ReadOnlySpan<byte> ReadBytes(int count) => Take(count);

    public string ReadAscii(int count) => Encoding.ASCII.GetString(Take(count));

    /// <summary>Reads a four-character code such as "sbuf" without advancing past it twice.</summary>
    public string ReadFourCc() => ReadAscii(4);

    public readonly ReadOnlySpan<byte> PeekAll() => _data[Position..];

    public void Skip(int count) => Take(count);

    private ReadOnlySpan<byte> Take(int count)
    {
        if (count < 0 || count > Remaining)
            throw new EndOfStreamException($"Need {count} bytes at offset {Position} but only {Remaining} remain.");
        var slice = _data.Slice(Position, count);
        Position += count;
        return slice;
    }
}
