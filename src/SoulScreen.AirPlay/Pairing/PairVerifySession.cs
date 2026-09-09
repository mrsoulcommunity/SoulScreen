using System.Security.Cryptography;
using Org.BouncyCastle.Crypto.Agreement;
using Org.BouncyCastle.Crypto.Parameters;
using SoulScreen.AirPlay.Crypto;
using SoulScreen.Core.Logging;

namespace SoulScreen.AirPlay.Pairing;

/// <summary>
/// The legacy (pre-HomeKit) AirPlay pair-verify exchange, run once per control connection.
/// <para>
/// Step 1: the sender presents an ephemeral X25519 public key plus its long-term Ed25519
/// public key. We answer with our own X25519 public key and an Ed25519 signature over
/// (ours || theirs), encrypted under a key derived from the shared secret.
/// </para>
/// <para>
/// Step 2: the sender returns the mirror-image signature, encrypted with the same
/// keystream continued. Verifying it proves the sender holds the Ed25519 key it claimed.
/// </para>
/// </summary>
public sealed class PairVerifySession : IDisposable
{
    private const int KeySize = 32;
    private const int SignatureSize = 64;

    private static readonly byte[] AesKeyLabel = "Pair-Verify-AES-Key"u8.ToArray();
    private static readonly byte[] AesIvLabel = "Pair-Verify-AES-IV"u8.ToArray();

    private readonly ILogger _log = Log.For("pairing");
    private readonly DeviceIdentity _identity;

    private X25519PrivateKeyParameters? _ephemeralPrivate;
    private byte[]? _ourCurvePublic;
    private byte[]? _theirCurvePublic;
    private byte[]? _theirEdPublic;
    private AesCtr? _cipher;

    public PairVerifySession(DeviceIdentity identity) => _identity = identity;

    /// <summary>True once step 2 has verified the sender's signature.</summary>
    public bool IsVerified { get; private set; }

    /// <summary>
    /// The raw 32-byte X25519 shared secret.
    /// <para>
    /// It is not only used for this handshake: the mirroring stream key is derived from
    /// the FairPlay key folded together with this secret, so a session that skipped
    /// pair-verify cannot decrypt a single frame.
    /// </para>
    /// </summary>
    public byte[]? SharedSecret { get; private set; }

    /// <summary>
    /// Handles one /pair-verify request body and produces the response body.
    /// </summary>
    /// <exception cref="InvalidDataException">The body is malformed or the signature does not verify.</exception>
    public byte[] Handle(ReadOnlySpan<byte> body)
    {
        if (body.Length < 4) throw new InvalidDataException($"pair-verify body is only {body.Length} bytes.");

        // The first byte is 1 for the opening message and 0 for the confirmation.
        return body[0] == 1 ? HandleStart(body) : HandleFinish(body);
    }

    private byte[] HandleStart(ReadOnlySpan<byte> body)
    {
        const int expected = 4 + KeySize + KeySize;
        if (body.Length < expected)
            throw new InvalidDataException($"pair-verify step 1 needs {expected} bytes, got {body.Length}.");

        _theirCurvePublic = body.Slice(4, KeySize).ToArray();
        _theirEdPublic = body.Slice(4 + KeySize, KeySize).ToArray();

        var random = new Org.BouncyCastle.Security.SecureRandom();
        _ephemeralPrivate = new X25519PrivateKeyParameters(random);
        _ourCurvePublic = _ephemeralPrivate.GeneratePublicKey().GetEncoded();

        var agreement = new X25519Agreement();
        agreement.Init(_ephemeralPrivate);
        var sharedSecret = new byte[agreement.AgreementSize];
        agreement.CalculateAgreement(new X25519PublicKeyParameters(_theirCurvePublic, 0), sharedSecret, 0);

        // Sign (our ephemeral key || their ephemeral key) with the receiver's long-term key.
        Span<byte> signedMessage = stackalloc byte[KeySize * 2];
        _ourCurvePublic.CopyTo(signedMessage);
        _theirCurvePublic.CopyTo(signedMessage[KeySize..]);
        var signature = _identity.Sign(signedMessage);

        var aesKey = Derive(AesKeyLabel, sharedSecret);
        var aesIv = Derive(AesIvLabel, sharedSecret);
        _cipher = new AesCtr(aesKey, aesIv);
        // Kept rather than wiped: the mirroring stream key derivation needs it later.
        SharedSecret = sharedSecret;
        CryptographicOperations.ZeroMemory(aesKey);
        CryptographicOperations.ZeroMemory(aesIv);

        var encryptedSignature = _cipher.ProcessToArray(signature);

        var response = new byte[KeySize + SignatureSize];
        _ourCurvePublic.CopyTo(response, 0);
        encryptedSignature.CopyTo(response, KeySize);

        _log.Debug("pair-verify step 1 answered");
        return response;
    }

    private byte[] HandleFinish(ReadOnlySpan<byte> body)
    {
        if (_cipher is null || _ourCurvePublic is null || _theirCurvePublic is null || _theirEdPublic is null)
            throw new InvalidDataException("pair-verify step 2 arrived before step 1.");

        const int expected = 4 + SignatureSize;
        if (body.Length < expected)
            throw new InvalidDataException($"pair-verify step 2 needs {expected} bytes, got {body.Length}.");

        // The keystream continues from step 1 rather than restarting.
        var signature = _cipher.ProcessToArray(body.Slice(4, SignatureSize));

        Span<byte> signedMessage = stackalloc byte[KeySize * 2];
        _theirCurvePublic.CopyTo(signedMessage);
        _ourCurvePublic.CopyTo(signedMessage[KeySize..]);

        if (!DeviceIdentity.Verify(_theirEdPublic, signedMessage, signature))
            throw new InvalidDataException("pair-verify signature did not verify.");

        IsVerified = true;
        _log.Info("pair-verify completed");
        return [];
    }

    /// <summary>SHA-512 over a label concatenated with the shared secret, truncated to an AES-128 key.</summary>
    private static byte[] Derive(ReadOnlySpan<byte> label, ReadOnlySpan<byte> sharedSecret)
    {
        Span<byte> input = stackalloc byte[label.Length + sharedSecret.Length];
        label.CopyTo(input);
        sharedSecret.CopyTo(input[label.Length..]);

        Span<byte> digest = stackalloc byte[64];
        SHA512.HashData(input, digest);
        return digest[..16].ToArray();
    }

    public void Dispose()
    {
        _cipher?.Dispose();
        if (SharedSecret is not null)
        {
            CryptographicOperations.ZeroMemory(SharedSecret);
            SharedSecret = null;
        }
    }
}
