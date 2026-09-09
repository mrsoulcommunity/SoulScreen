namespace SoulScreen.Core.Buffers;

public static class Hex
{
    public static string ToLower(ReadOnlySpan<byte> bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

    public static string ToUpper(ReadOnlySpan<byte> bytes) => Convert.ToHexString(bytes);

    public static byte[] Parse(string hex)
    {
        Span<char> cleaned = hex.Length <= 512 ? stackalloc char[hex.Length] : new char[hex.Length];
        var n = 0;
        foreach (var c in hex)
            if (!char.IsWhiteSpace(c) && c != ':' && c != '-')
                cleaned[n++] = c;
        return Convert.FromHexString(cleaned[..n]);
    }

    /// <summary>Hex dump for protocol tracing: 16 bytes per line with an ASCII gutter.</summary>
    public static string Dump(ReadOnlySpan<byte> bytes, int maxBytes = 256)
    {
        var take = Math.Min(bytes.Length, maxBytes);
        var sb = new System.Text.StringBuilder(take * 4 + 64);
        for (var offset = 0; offset < take; offset += 16)
        {
            var line = bytes[offset..Math.Min(offset + 16, take)];
            sb.Append(offset.ToString("x4")).Append("  ");
            for (var i = 0; i < 16; i++)
                sb.Append(i < line.Length ? line[i].ToString("x2") : "  ").Append(i == 7 ? "  " : ' ');
            sb.Append(' ');
            foreach (var b in line)
                sb.Append(b is >= 0x20 and < 0x7f ? (char)b : '.');
            sb.Append('\n');
        }
        if (bytes.Length > take)
            sb.Append($"... {bytes.Length - take} more bytes\n");
        return sb.ToString();
    }
}
