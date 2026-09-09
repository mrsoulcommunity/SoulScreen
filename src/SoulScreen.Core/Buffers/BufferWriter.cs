using System.Buffers.Binary;
using System.Text;

namespace SoulScreen.Core.Buffers;

/// <summary>Growable big/little-endian byte builder used by the plist writer and the
/// protocol encoders.</summary>
public sealed class BufferWriter(int capacity = 256)
{
    private byte[] _buffer = new byte[Math.Max(capacity, 16)];

    public int Length { get; private set; }

    public ReadOnlySpan<byte> WrittenSpan => _buffer.AsSpan(0, Length);

    public byte[] ToArray() => WrittenSpan.ToArray();

    public void WriteUInt8(byte value) => Reserve(1)[0] = value;

    public void WriteUInt16BE(ushort value) => BinaryPrimitives.WriteUInt16BigEndian(Reserve(2), value);

    public void WriteUInt32BE(uint value) => BinaryPrimitives.WriteUInt32BigEndian(Reserve(4), value);

    public void WriteUInt32LE(uint value) => BinaryPrimitives.WriteUInt32LittleEndian(Reserve(4), value);

    public void WriteUInt64BE(ulong value) => BinaryPrimitives.WriteUInt64BigEndian(Reserve(8), value);

    public void WriteUInt64LE(ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(Reserve(8), value);

    public void WriteDoubleBE(double value) => BinaryPrimitives.WriteInt64BigEndian(Reserve(8), BitConverter.DoubleToInt64Bits(value));

    public void Write(ReadOnlySpan<byte> value) => value.CopyTo(Reserve(value.Length));

    public void WriteAscii(string value) => Encoding.ASCII.GetBytes(value, Reserve(value.Length));

    public void WriteUtf8(string value)
    {
        var count = Encoding.UTF8.GetByteCount(value);
        Encoding.UTF8.GetBytes(value, Reserve(count));
    }

    /// <summary>Overwrites <paramref name="count"/> bytes at <paramref name="offset"/>,
    /// used to backfill length fields once a nested structure is complete.</summary>
    public Span<byte> SpanAt(int offset, int count)
    {
        if (offset < 0 || offset + count > Length) throw new ArgumentOutOfRangeException(nameof(offset));
        return _buffer.AsSpan(offset, count);
    }

    private Span<byte> Reserve(int count)
    {
        if (Length + count > _buffer.Length)
        {
            var size = _buffer.Length;
            while (size < Length + count) size *= 2;
            Array.Resize(ref _buffer, size);
        }
        var span = _buffer.AsSpan(Length, count);
        Length += count;
        return span;
    }
}
