using System.Security.Cryptography;

namespace SoulScreen.AirPlay.Crypto;

/// <summary>
/// AES in counter mode with a persistent keystream position.
/// <para>
/// .NET has no built-in CTR mode, and AirPlay needs the stateful flavour: pair-verify
/// encrypts 64 bytes in one request and then decrypts the next request's 64 bytes with
/// the keystream continuing where it left off, so the counter cannot restart per call.
/// </para>
/// <para>Encryption and decryption are the same operation in CTR mode.</para>
/// </summary>
public sealed class AesCtr : IDisposable
{
    private readonly Aes _aes;
    private readonly ICryptoTransform _encryptor;
    private readonly byte[] _counter = new byte[16];
    private readonly byte[] _keystream = new byte[16];

    /// <summary>How many bytes of the current keystream block have already been consumed.</summary>
    private int _keystreamOffset = 16;

    public AesCtr(ReadOnlySpan<byte> key, ReadOnlySpan<byte> iv)
    {
        if (key.Length is not (16 or 24 or 32)) throw new ArgumentException("Key must be 16, 24 or 32 bytes.", nameof(key));
        if (iv.Length != 16) throw new ArgumentException("Counter block must be 16 bytes.", nameof(iv));

        _aes = Aes.Create();
        _aes.Key = key.ToArray();
        _aes.Mode = CipherMode.ECB;
        _aes.Padding = PaddingMode.None;
        _encryptor = _aes.CreateEncryptor();
        iv.CopyTo(_counter);
    }

    /// <summary>Transforms <paramref name="data"/> in place, advancing the keystream.</summary>
    public void Process(Span<byte> data)
    {
        for (var i = 0; i < data.Length; i++)
        {
            if (_keystreamOffset == 16)
            {
                _encryptor.TransformBlock(_counter, 0, 16, _keystream, 0);
                IncrementCounter();
                _keystreamOffset = 0;
            }
            data[i] ^= _keystream[_keystreamOffset++];
        }
    }

    public void Process(ReadOnlySpan<byte> input, Span<byte> output)
    {
        input.CopyTo(output);
        Process(output[..input.Length]);
    }

    /// <summary>
    /// Transforms into a fresh array. Named apart from <see cref="Process(Span{byte})"/>
    /// so a byte[] argument cannot silently bind to the in-place overload and discard the
    /// result the caller wanted.
    /// </summary>
    public byte[] ProcessToArray(ReadOnlySpan<byte> input)
    {
        var output = input.ToArray();
        Process(output);
        return output;
    }

    /// <summary>
    /// Discards the remainder of the current keystream block so the next byte comes from a
    /// freshly generated one, leaving the counter itself untouched.
    /// <para>
    /// The mirroring stream needs this: every packet restarts on a block boundary, and the
    /// leftover keystream from the previous packet's partial block is carried across by
    /// hand instead.
    /// </para>
    /// </summary>
    public void StartFreshBlock() => _keystreamOffset = 16;

    /// <summary>Restarts the keystream at a fresh counter block, discarding any partial block.</summary>
    public void Reset(ReadOnlySpan<byte> counterBlock)
    {
        if (counterBlock.Length != 16) throw new ArgumentException("Counter block must be 16 bytes.", nameof(counterBlock));
        counterBlock.CopyTo(_counter);
        _keystreamOffset = 16;
    }

    /// <summary>Big-endian increment across the whole 128-bit block, as NIST SP 800-38A specifies.</summary>
    private void IncrementCounter()
    {
        for (var i = 15; i >= 0; i--)
            if (++_counter[i] != 0) break;
    }

    public void Dispose()
    {
        _encryptor.Dispose();
        _aes.Dispose();
        CryptographicOperations.ZeroMemory(_keystream);
        CryptographicOperations.ZeroMemory(_counter);
    }
}
