using System.Buffers.Binary;

namespace SoulScreen.Android;

/// <summary>
/// Byte-level framing for the scrcpy server's video and audio sockets, verified against
/// scrcpy's own client source (app/src/demuxer.c) and its developer documentation
/// (doc/develop.md) rather than guessed: a wrong bit here would silently corrupt every
/// timestamp and keyframe flag rather than fail loudly.
/// <para>
/// Both sockets carry a stream of fixed 12-byte headers, each either a *session* packet
/// (sent once at the start and again whenever the phone rotates, since scrcpy restarts
/// its own capture session and re-announces the new size) or a *frame* packet (config -
/// SPS/PPS/audio setup - or real payload) followed by exactly that many payload bytes.
/// The two header shapes are told apart by the top bit of the first byte.
/// </para>
/// </summary>
public readonly struct ScrcpyPacketHeader
{
    public const int Size = 12;

    private const ulong ConfigFlag = 1UL << 62;
    private const ulong KeyFrameFlag = 1UL << 61;
    private const ulong PtsMask = KeyFrameFlag - 1;

    public bool IsSessionPacket { get; private init; }

    /// <summary>Only meaningful when <see cref="IsSessionPacket"/>: the phone's next capture
    /// size, sent again after every rotation.</summary>
    public int SessionWidth { get; private init; }
    public int SessionHeight { get; private init; }

    /// <summary>Only meaningful when not a session packet.</summary>
    public bool IsConfig { get; private init; }
    public bool IsKeyFrame { get; private init; }
    public long PtsUs { get; private init; }
    public int PayloadSize { get; private init; }

    public static ScrcpyPacketHeader Parse(ReadOnlySpan<byte> header)
    {
        if (header.Length != Size)
            throw new ArgumentException($"A scrcpy packet header is exactly {Size} bytes.", nameof(header));

        // "Session packets and media packets are distinguished by their first bit."
        if ((header[0] & 0x80) != 0)
        {
            return new ScrcpyPacketHeader
            {
                IsSessionPacket = true,
                SessionWidth = (int)BinaryPrimitives.ReadUInt32BigEndian(header[4..8]),
                SessionHeight = (int)BinaryPrimitives.ReadUInt32BigEndian(header[8..12]),
            };
        }

        var ptsAndFlags = BinaryPrimitives.ReadUInt64BigEndian(header[..8]);
        return new ScrcpyPacketHeader
        {
            IsConfig = (ptsAndFlags & ConfigFlag) != 0,
            IsKeyFrame = (ptsAndFlags & KeyFrameFlag) != 0,
            PtsUs = (long)(ptsAndFlags & PtsMask),
            PayloadSize = (int)BinaryPrimitives.ReadUInt32BigEndian(header[8..12]),
        };
    }
}

/// <summary>
/// The four-byte codec identifiers scrcpy sends first on the video and audio sockets.
/// Four-letter names are their plain ASCII; three-letter names are NUL-padded at the
/// *front* - e.g. "raw" is bytes [0x00, 'r', 'a', 'w'], not ['r','a','w', 0x00] - which
/// matches reading each one as the big-endian u32 scrcpy's own source defines it as
/// (0x00726177 for "raw"), not just its ASCII spelled left to right.
/// </summary>
public static class ScrcpyCodecIds
{
    public static readonly byte[] VideoH264 = "h264"u8.ToArray();
    public static readonly byte[] VideoH265 = "h265"u8.ToArray();
    public static readonly byte[] VideoAv1 = [0x00, (byte)'a', (byte)'v', (byte)'1'];

    public static readonly byte[] AudioOpus = "opus"u8.ToArray();
    public static readonly byte[] AudioAac = [0x00, (byte)'a', (byte)'a', (byte)'c'];
    public static readonly byte[] AudioFlac = "flac"u8.ToArray();
    public static readonly byte[] AudioRaw = [0x00, (byte)'r', (byte)'a', (byte)'w'];
}

public static class ScrcpyDeviceMeta
{
    /// <summary>Bytes on the wire before the device name: one dummy byte (forward-tunnel
    /// only) plus the fixed-width name field.</summary>
    public const int DummyByteLength = 1;
    public const int DeviceNameFieldLength = 64;

    /// <summary>Decodes the fixed-width, NUL-padded UTF-8 device name field.</summary>
    public static string ParseDeviceName(ReadOnlySpan<byte> field)
    {
        var nul = field.IndexOf((byte)0);
        var trimmed = nul >= 0 ? field[..nul] : field;
        return System.Text.Encoding.UTF8.GetString(trimmed);
    }
}
