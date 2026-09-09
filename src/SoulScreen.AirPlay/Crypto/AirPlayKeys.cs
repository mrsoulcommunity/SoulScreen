using System.Security.Cryptography;

namespace SoulScreen.AirPlay.Crypto;

/// <summary>
/// Key derivations shared by the media streams.
/// </summary>
public static class AirPlayKeys
{
    /// <summary>
    /// Binds the key FairPlay unwrapped from SETUP to this pairing session.
    /// <para>
    /// Both media streams need this, and neither works without it. Video derives its
    /// AES-CTR key and IV from the result; audio uses the first sixteen bytes directly as
    /// its AES-CBC key. Using the unwrapped key as it came out of FairPlay decrypts to
    /// noise, which for audio looks like a decoder that rejects every packet while the
    /// transport reports none lost.
    /// </para>
    /// <para>
    /// The step exists because a receiver may hold sessions with several senders at once:
    /// folding in the per-session ECDH secret is what keeps their streams distinct. A sender
    /// that never paired has no secret, and then the unwrapped key is used as it is - but
    /// nothing SoulScreen advertises reaches that path, since it asks for legacy pairing.
    /// </para>
    /// </summary>
    /// <param name="fairPlayKey">The 16-byte key FairPlay unwrapped from the SETUP "ekey".</param>
    /// <param name="ecdhSecret">The 32-byte X25519 secret established during pair-verify.</param>
    /// <returns>16 bytes of session-bound key material.</returns>
    public static byte[] SessionKey(ReadOnlySpan<byte> fairPlayKey, ReadOnlySpan<byte> ecdhSecret)
    {
        if (fairPlayKey.Length < 16)
            throw new ArgumentException("The FairPlay key must be 16 bytes.", nameof(fairPlayKey));
        if (ecdhSecret.Length < 32)
            throw new ArgumentException("The ECDH secret must be 32 bytes.", nameof(ecdhSecret));

        Span<byte> input = stackalloc byte[48];
        fairPlayKey[..16].CopyTo(input);
        ecdhSecret[..32].CopyTo(input[16..]);

        Span<byte> digest = stackalloc byte[64];
        SHA512.HashData(input, digest);
        return digest[..16].ToArray();
    }
}
