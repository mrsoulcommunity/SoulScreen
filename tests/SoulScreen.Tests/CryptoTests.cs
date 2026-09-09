using SoulScreen.AirPlay.Crypto;
using SoulScreen.AirPlay.Streams;
using SoulScreen.Core.Buffers;
using Xunit;

namespace SoulScreen.Tests;

public class AesCtrTests
{
    /// <summary>NIST SP 800-38A, F.5.1 CTR-AES128.Encrypt.</summary>
    [Fact]
    public void MatchesTheNistCounterModeVector()
    {
        var key = Hex.Parse("2b7e151628aed2a6abf7158809cf4f3c");
        var counter = Hex.Parse("f0f1f2f3f4f5f6f7f8f9fafbfcfdfeff");
        var plaintext = Hex.Parse(
            "6bc1bee22e409f96e93d7e117393172a" +
            "ae2d8a571e03ac9c9eb76fac45af8e51" +
            "30c81c46a35ce411e5fbc1191a0a52ef" +
            "f69f2445df4f9b17ad2b417be66c3710");
        var expected = Hex.Parse(
            "874d6191b620e3261bef6864990db6ce" +
            "9806f66b7970fdff8617187bb9fffdff" +
            "5ae4df3edbd5d35e5b4f09020db03eab" +
            "1e031dda2fbe03d1792170a0f3009cee");

        using var cipher = new AesCtr(key, counter);
        Assert.Equal(expected, cipher.ProcessToArray(plaintext));
    }

    /// <summary>
    /// The keystream must span calls: pair-verify encrypts 64 bytes in one message and
    /// decrypts the next with the counter continuing, not restarting.
    /// </summary>
    [Fact]
    public void KeystreamContinuesAcrossCalls()
    {
        var key = Hex.Parse("2b7e151628aed2a6abf7158809cf4f3c");
        var counter = Hex.Parse("f0f1f2f3f4f5f6f7f8f9fafbfcfdfeff");
        var plaintext = Hex.Parse(
            "6bc1bee22e409f96e93d7e117393172a" +
            "ae2d8a571e03ac9c9eb76fac45af8e51");

        using var whole = new AesCtr(key, counter);
        var reference = whole.ProcessToArray(plaintext);

        using var piecewise = new AesCtr(key, counter);
        var first = piecewise.ProcessToArray(plaintext.AsSpan(0, 7));   // mid-block split
        var second = piecewise.ProcessToArray(plaintext.AsSpan(7));

        Assert.Equal(reference, first.Concat(second));
    }

    [Fact]
    public void StartFreshBlockDiscardsTheRestOfTheCurrentBlock()
    {
        var key = Hex.Parse("2b7e151628aed2a6abf7158809cf4f3c");
        var counter = Hex.Parse("f0f1f2f3f4f5f6f7f8f9fafbfcfdfeff");

        // Consume 4 bytes, drop the rest of that block, then take 16.
        using var skipping = new AesCtr(key, counter);
        skipping.ProcessToArray(new byte[4]);
        skipping.StartFreshBlock();
        var afterSkip = skipping.ProcessToArray(new byte[16]);

        // The same 16 bytes should come from the second counter block.
        using var straight = new AesCtr(key, counter);
        straight.ProcessToArray(new byte[16]);
        var secondBlock = straight.ProcessToArray(new byte[16]);

        Assert.Equal(secondBlock, afterSkip);
    }
}

public class MirrorStreamCipherTests
{
    private static readonly byte[] FairPlayKey = [.. Enumerable.Range(0, 16).Select(i => (byte)i)];
    private static readonly byte[] EcdhSecret = [.. Enumerable.Range(0x20, 32).Select(i => (byte)i)];
    private const ulong StreamConnectionId = 12345678901234567890UL;

    /// <summary>
    /// Locks in the exact key schedule and the carry behaviour between packets.
    /// <para>
    /// The expected bytes were produced independently (Python hashlib plus a raw AES-ECB
    /// counter walk) from the documented derivation: SHA-512 of the FairPlay key with the
    /// pair-verify ECDH secret, then SHA-512 of "AirPlayStreamKey"/"AirPlayStreamIV" plus
    /// the stream connection id with the first half of that digest.
    /// </para>
    /// <para>
    /// The two packets are chosen so the first ends mid-block: its trailing 4 bytes and the
    /// whole of the second packet must come from the same keystream block.
    /// </para>
    /// </summary>
    [Fact]
    public void DecryptsAcrossAPacketBoundaryUsingTheCarriedKeystream()
    {
        var firstPacket = Hex.Parse("a8d75c8b6fd72d478d23148d2d9e620ef7c317ec");   // 20 bytes
        var secondPacket = Hex.Parse("7fb0e40c55ab4d16b24fd80d");                  // 12 bytes

        using var cipher = new MirrorStreamCipher(FairPlayKey, EcdhSecret, StreamConnectionId);

        cipher.Decrypt(firstPacket);
        Assert.Equal(Enumerable.Repeat((byte)0xAA, 20), firstPacket);

        cipher.Decrypt(secondPacket);
        Assert.Equal(Enumerable.Repeat((byte)0xBB, 12), secondPacket);
    }

    [Fact]
    public void HandlesAPacketShorterThanTheOutstandingCarry()
    {
        // 20 bytes leaves 12 bytes of carry; feeding 5 then 7 must consume it in order and
        // land on the same plaintext as one 12-byte packet would.
        using var split = new MirrorStreamCipher(FairPlayKey, EcdhSecret, StreamConnectionId);
        var lead = Hex.Parse("a8d75c8b6fd72d478d23148d2d9e620ef7c317ec");
        split.Decrypt(lead);

        var tail = Hex.Parse("7fb0e40c55ab4d16b24fd80d");
        split.Decrypt(tail.AsSpan(0, 5));
        split.Decrypt(tail.AsSpan(5));

        Assert.Equal(Enumerable.Repeat((byte)0xBB, 12), tail);
    }

    [Fact]
    public void DerivationDependsOnEveryInput()
    {
        var payload = new byte[32];

        var outputs = new List<string>();
        foreach (var (key, secret, id) in new[]
                 {
                     (FairPlayKey, EcdhSecret, StreamConnectionId),
                     (FairPlayKey, EcdhSecret, StreamConnectionId + 1),
                     ([.. FairPlayKey.Select(b => (byte)(b ^ 1))], EcdhSecret, StreamConnectionId),
                     (FairPlayKey, (byte[])[.. EcdhSecret.Select(b => (byte)(b ^ 1))], StreamConnectionId),
                 })
        {
            using var cipher = new MirrorStreamCipher(key, secret, id);
            var block = (byte[])payload.Clone();
            cipher.Decrypt(block);
            outputs.Add(Convert.ToHexString(block));
        }

        Assert.Equal(outputs.Count, outputs.Distinct().Count());
    }
}
