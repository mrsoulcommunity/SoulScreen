using System.Net;
using System.Text;
using SoulScreen.Core.Buffers;

namespace SoulScreen.AirPlay.Discovery;

public enum DnsRecordType : ushort
{
    A = 1,
    Ptr = 12,
    Txt = 16,
    Aaaa = 28,
    Srv = 33,
    Nsec = 47,
    Any = 255,
}

public static class DnsClass
{
    public const ushort Internet = 1;
    /// <summary>In a question this bit asks for a unicast reply; in a record it means
    /// "flush anything else cached for this name".</summary>
    public const ushort TopBit = 0x8000;
}

public sealed record DnsQuestion(string Name, DnsRecordType Type, bool WantsUnicastReply);

public abstract record DnsRecord(string Name, DnsRecordType Type, uint Ttl, bool CacheFlush)
{
    internal abstract void WriteRData(BufferWriter writer, NameCompressor compressor);
}

public sealed record ARecord(string Name, IPAddress Address, uint Ttl = 120, bool CacheFlush = true)
    : DnsRecord(Name, DnsRecordType.A, Ttl, CacheFlush)
{
    internal override void WriteRData(BufferWriter writer, NameCompressor compressor)
        => writer.Write(Address.GetAddressBytes());
}

public sealed record AaaaRecord(string Name, IPAddress Address, uint Ttl = 120, bool CacheFlush = true)
    : DnsRecord(Name, DnsRecordType.Aaaa, Ttl, CacheFlush)
{
    internal override void WriteRData(BufferWriter writer, NameCompressor compressor)
        => writer.Write(Address.GetAddressBytes());
}

public sealed record PtrRecord(string Name, string Target, uint Ttl = 4500, bool CacheFlush = false)
    : DnsRecord(Name, DnsRecordType.Ptr, Ttl, CacheFlush)
{
    internal override void WriteRData(BufferWriter writer, NameCompressor compressor)
        => compressor.WriteName(writer, Target);
}

public sealed record SrvRecord(
    string Name,
    string Target,
    ushort Port,
    ushort Priority = 0,
    ushort Weight = 0,
    uint Ttl = 120,
    bool CacheFlush = true) : DnsRecord(Name, DnsRecordType.Srv, Ttl, CacheFlush)
{
    internal override void WriteRData(BufferWriter writer, NameCompressor compressor)
    {
        writer.WriteUInt16BE(Priority);
        writer.WriteUInt16BE(Weight);
        writer.WriteUInt16BE(Port);
        // RFC 2782 targets are not compressed by most stacks; Apple does not compress
        // them either, and some parsers choke when they are.
        compressor.WriteNameUncompressed(writer, Target);
    }
}

public sealed record TxtRecord(string Name, IReadOnlyList<string> Entries, uint Ttl = 4500, bool CacheFlush = true)
    : DnsRecord(Name, DnsRecordType.Txt, Ttl, CacheFlush)
{
    internal override void WriteRData(BufferWriter writer, NameCompressor compressor)
    {
        if (Entries.Count == 0)
        {
            // An empty TXT record must still carry one zero-length string.
            writer.WriteUInt8(0);
            return;
        }
        foreach (var entry in Entries)
        {
            var bytes = Encoding.UTF8.GetBytes(entry);
            if (bytes.Length > 255)
                throw new InvalidOperationException($"TXT entry is {bytes.Length} bytes; the limit is 255.");
            writer.WriteUInt8((byte)bytes.Length);
            writer.Write(bytes);
        }
    }
}

/// <summary>A record we parsed but do not model; kept so responders can echo known-answers.</summary>
public sealed record RawRecord(string Name, DnsRecordType Type, uint Ttl, bool CacheFlush, byte[] RData)
    : DnsRecord(Name, Type, Ttl, CacheFlush)
{
    internal override void WriteRData(BufferWriter writer, NameCompressor compressor) => writer.Write(RData);
}

/// <summary>Tracks where each name was first written so later occurrences become pointers.</summary>
public sealed class NameCompressor
{
    private readonly Dictionary<string, int> _offsets = new(StringComparer.OrdinalIgnoreCase);

    public void WriteName(BufferWriter writer, string name)
    {
        var remaining = name.TrimEnd('.');
        while (remaining.Length > 0)
        {
            if (_offsets.TryGetValue(remaining, out var offset))
            {
                writer.WriteUInt16BE((ushort)(0xC000 | offset));
                return;
            }

            // Only offsets that fit in 14 bits are addressable by a compression pointer.
            if (writer.Length < 0x3FFF) _offsets[remaining] = writer.Length;

            var dot = remaining.IndexOf('.');
            var label = dot < 0 ? remaining : remaining[..dot];
            WriteLabel(writer, label);
            remaining = dot < 0 ? string.Empty : remaining[(dot + 1)..];
        }
        writer.WriteUInt8(0);
    }

    public void WriteNameUncompressed(BufferWriter writer, string name)
    {
        foreach (var label in name.TrimEnd('.').Split('.', StringSplitOptions.RemoveEmptyEntries))
            WriteLabel(writer, label);
        writer.WriteUInt8(0);
    }

    private static void WriteLabel(BufferWriter writer, string label)
    {
        var bytes = Encoding.UTF8.GetBytes(label);
        if (bytes.Length is 0 or > 63)
            throw new InvalidOperationException($"DNS label '{label}' must be 1-63 bytes, got {bytes.Length}.");
        writer.WriteUInt8((byte)bytes.Length);
        writer.Write(bytes);
    }
}

public sealed class DnsMessage
{
    public ushort Id { get; set; }
    public ushort Flags { get; set; }
    public List<DnsQuestion> Questions { get; } = [];
    public List<DnsRecord> Answers { get; } = [];
    public List<DnsRecord> Authorities { get; } = [];
    public List<DnsRecord> Additionals { get; } = [];

    public bool IsResponse => (Flags & 0x8000) != 0;
    public bool IsQuery => !IsResponse;

    public static DnsMessage Response() => new() { Flags = 0x8400 }; // QR + Authoritative Answer

    public byte[] ToArray()
    {
        var writer = new BufferWriter(512);
        var compressor = new NameCompressor();

        writer.WriteUInt16BE(Id);
        writer.WriteUInt16BE(Flags);
        writer.WriteUInt16BE((ushort)Questions.Count);
        writer.WriteUInt16BE((ushort)Answers.Count);
        writer.WriteUInt16BE((ushort)Authorities.Count);
        writer.WriteUInt16BE((ushort)Additionals.Count);

        foreach (var question in Questions)
        {
            compressor.WriteName(writer, question.Name);
            writer.WriteUInt16BE((ushort)question.Type);
            writer.WriteUInt16BE((ushort)(DnsClass.Internet | (question.WantsUnicastReply ? DnsClass.TopBit : 0)));
        }

        foreach (var record in Answers.Concat(Authorities).Concat(Additionals))
            WriteRecord(writer, compressor, record);

        return writer.ToArray();
    }

    private static void WriteRecord(BufferWriter writer, NameCompressor compressor, DnsRecord record)
    {
        compressor.WriteName(writer, record.Name);
        writer.WriteUInt16BE((ushort)record.Type);
        writer.WriteUInt16BE((ushort)(DnsClass.Internet | (record.CacheFlush ? DnsClass.TopBit : 0)));
        writer.WriteUInt32BE(record.Ttl);

        var lengthOffset = writer.Length;
        writer.WriteUInt16BE(0);
        var rdataStart = writer.Length;
        record.WriteRData(writer, compressor);
        var rdataLength = writer.Length - rdataStart;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(writer.SpanAt(lengthOffset, 2), (ushort)rdataLength);
    }

    public static DnsMessage Parse(ReadOnlySpan<byte> data)
    {
        var reader = new SpanReader(data);
        var message = new DnsMessage
        {
            Id = reader.ReadUInt16BE(),
            Flags = reader.ReadUInt16BE(),
        };
        int questionCount = reader.ReadUInt16BE();
        int answerCount = reader.ReadUInt16BE();
        int authorityCount = reader.ReadUInt16BE();
        int additionalCount = reader.ReadUInt16BE();

        for (var i = 0; i < questionCount; i++)
        {
            var name = ReadName(data, ref reader);
            var type = (DnsRecordType)reader.ReadUInt16BE();
            var klass = reader.ReadUInt16BE();
            message.Questions.Add(new DnsQuestion(name, type, (klass & DnsClass.TopBit) != 0));
        }

        ReadRecords(data, ref reader, answerCount, message.Answers);
        ReadRecords(data, ref reader, authorityCount, message.Authorities);
        ReadRecords(data, ref reader, additionalCount, message.Additionals);
        return message;
    }

    private static void ReadRecords(ReadOnlySpan<byte> data, ref SpanReader reader, int count, List<DnsRecord> into)
    {
        for (var i = 0; i < count && !reader.IsEmpty; i++)
        {
            var name = ReadName(data, ref reader);
            var type = (DnsRecordType)reader.ReadUInt16BE();
            var klass = reader.ReadUInt16BE();
            var ttl = reader.ReadUInt32BE();
            var rdataLength = reader.ReadUInt16BE();
            var rdata = reader.ReadBytes(rdataLength).ToArray();
            into.Add(new RawRecord(name, type, ttl, (klass & DnsClass.TopBit) != 0, rdata));
        }
    }

    /// <summary>Reads a possibly compressed name. Pointers may only ever jump backwards,
    /// which is what stops a crafted packet from looping forever.</summary>
    private static string ReadName(ReadOnlySpan<byte> data, ref SpanReader reader)
    {
        var labels = new List<string>();
        var position = reader.Position;
        var followedPointer = false;
        var limit = position;

        while (true)
        {
            if (position >= data.Length) break;
            var length = data[position];

            if ((length & 0xC0) == 0xC0)
            {
                if (position + 1 >= data.Length) break;
                var pointer = ((length & 0x3F) << 8) | data[position + 1];
                if (!followedPointer)
                {
                    reader.Position = position + 2;
                    followedPointer = true;
                }
                if (pointer >= limit) break; // must point strictly backwards
                limit = pointer;
                position = pointer;
                continue;
            }

            position++;
            if (length == 0) break;
            if (position + length > data.Length) break;
            labels.Add(Encoding.UTF8.GetString(data.Slice(position, length)));
            position += length;
        }

        if (!followedPointer) reader.Position = position;
        return string.Join('.', labels);
    }
}
