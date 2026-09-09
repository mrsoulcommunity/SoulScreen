using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using SoulScreen.AirPlay.Crypto;

namespace SoulScreen.AirPlay.Streams;

/// <summary>
/// Decrypts the mirroring video stream.
/// <para>
/// The key is not the one FairPlay unwraps directly. That key is first folded together
/// with the X25519 secret established during pair-verify, then run through a pair of
/// SHA-512 derivations labelled with the stream connection id, producing the AES-128-CTR
/// key and IV for this particular stream. Both halves matter: without a completed
/// pair-verify the ECDH secret is missing and every frame decodes to noise.
/// </para>
/// <para>
/// The counter is not restarted per packet, but each packet does begin on a block
/// boundary. Whatever keystream was left over from the previous packet's trailing partial
/// block is what decrypts the next packet's first bytes, so this object is stateful and
/// must see every packet, in order.
/// </para>
/// </summary>
public sealed class MirrorStreamCipher : IDisposable
{
    private readonly AesCtr _cipher;

    /// <summary>Leftover keystream from the previous packet's trailing partial block.</summary>
    private readonly byte[] _carry = new byte[16];

    /// <summary>How many bytes of <see cref="_carry"/> are still unused.</summary>
    private int _carryCount;

    public MirrorStreamCipher(ReadOnlySpan<byte> fairPlayKey, ReadOnlySpan<byte> ecdhSecret, ulong streamConnectionId)
    {
        if (fairPlayKey.Length < 16) throw new ArgumentException("FairPlay key must be 16 bytes.", nameof(fairPlayKey));
        if (ecdhSecret.Length < 32) throw new ArgumentException("ECDH secret must be 32 bytes.", nameof(ecdhSecret));

        var sessionKey = AirPlayKeys.SessionKey(fairPlayKey, ecdhSecret);

        var id = streamConnectionId.ToString(CultureInfo.InvariantCulture);
        var key = Derive("AirPlayStreamKey" + id, sessionKey);
        var iv = Derive("AirPlayStreamIV" + id, sessionKey);
        CryptographicOperations.ZeroMemory(sessionKey);

        _cipher = new AesCtr(key, iv);
        CryptographicOperations.ZeroMemory(key);
        CryptographicOperations.ZeroMemory(iv);
    }

    /// <summary>First 16 bytes of SHA-512 over the label followed by the session key.</summary>
    private static byte[] Derive(string label, ReadOnlySpan<byte> sessionKey)
    {
        var labelBytes = Encoding.ASCII.GetBytes(label);
        Span<byte> input = stackalloc byte[labelBytes.Length + 16];
        labelBytes.CopyTo(input);
        sessionKey[..16].CopyTo(input[labelBytes.Length..]);

        Span<byte> digest = stackalloc byte[64];
        SHA512.HashData(input, digest);
        return digest[..16].ToArray();
    }

    /// <summary>Decrypts one packet in place. Packets must be presented in arrival order.</summary>
    public void Decrypt(Span<byte> packet)
    {
        var offset = 0;

        // Spend any keystream left over from the previous packet first.
        if (_carryCount > 0)
        {
            var take = Math.Min(_carryCount, packet.Length);
            var carryStart = 16 - _carryCount;
            for (var i = 0; i < take; i++)
                packet[i] ^= _carry[carryStart + i];

            offset = take;
            _carryCount -= take;
            // A packet shorter than the carry leaves some keystream still unspent.
            if (_carryCount > 0) return;
        }

        var remaining = packet.Length - offset;
        var wholeBlocks = remaining / 16 * 16;

        // Each packet resumes on a block boundary rather than mid-block.
        _cipher.StartFreshBlock();
        if (wholeBlocks > 0)
            _cipher.Process(packet.Slice(offset, wholeBlocks));

        var tail = remaining - wholeBlocks;
        if (tail == 0) return;

        // Decrypt the trailing partial block as a full one against a zero-padded buffer.
        // The padding positions come back as raw keystream, which is exactly what the next
        // packet's leading bytes need.
        var tailStart = offset + wholeBlocks;
        Array.Clear(_carry);
        packet.Slice(tailStart, tail).CopyTo(_carry);
        _cipher.Process(_carry);
        _carry.AsSpan(0, tail).CopyTo(packet[tailStart..]);
        _carryCount = 16 - tail;
    }

    public void Dispose()
    {
        _cipher.Dispose();
        CryptographicOperations.ZeroMemory(_carry);
    }
}
