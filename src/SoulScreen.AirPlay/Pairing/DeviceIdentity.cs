using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using SoulScreen.Core.Buffers;
using SoulScreen.Core.Logging;

namespace SoulScreen.AirPlay.Pairing;

/// <summary>
/// The receiver's long-lived identity: a MAC-shaped device id, a public identifier UUID,
/// and the Ed25519 key pair that pair-setup publishes and pair-verify signs with.
/// <para>
/// iOS remembers a receiver by its public key. Regenerating it on every launch would make
/// the phone treat us as a brand new device each time, so this is persisted to disk.
/// </para>
/// </summary>
public sealed class DeviceIdentity
{
    private readonly Ed25519PrivateKeyParameters _privateKey;

    private DeviceIdentity(byte[] deviceId, Guid publicIdentifier, byte[] ed25519Seed)
    {
        DeviceIdBytes = deviceId;
        PublicIdentifier = publicIdentifier;
        Ed25519Seed = ed25519Seed;
        _privateKey = new Ed25519PrivateKeyParameters(ed25519Seed, 0);
        Ed25519PublicKey = _privateKey.GeneratePublicKey().GetEncoded();
    }

    /// <summary>Six bytes formatted as a MAC address for the "deviceid" TXT entry.</summary>
    public byte[] DeviceIdBytes { get; }

    /// <summary>"AA:BB:CC:DD:EE:FF" - the form iOS expects in TXT records and /info.</summary>
    public string DeviceId => string.Join(':', DeviceIdBytes.Select(b => b.ToString("X2")));

    /// <summary>The "pi" TXT entry: a stable UUID identifying this receiver.</summary>
    public Guid PublicIdentifier { get; }

    /// <summary>32-byte Ed25519 seed. Never leaves this process.</summary>
    private byte[] Ed25519Seed { get; }

    /// <summary>32-byte Ed25519 public key, published as the "pk" TXT entry.</summary>
    public byte[] Ed25519PublicKey { get; }

    public string Ed25519PublicKeyHex => Hex.ToLower(Ed25519PublicKey);

    public byte[] Sign(ReadOnlySpan<byte> message)
    {
        var signer = new Ed25519Signer();
        signer.Init(true, _privateKey);
        signer.BlockUpdate(message.ToArray(), 0, message.Length);
        return signer.GenerateSignature();
    }

    public static bool Verify(ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> message, ReadOnlySpan<byte> signature)
    {
        try
        {
            var verifier = new Ed25519Signer();
            verifier.Init(false, new Ed25519PublicKeyParameters(publicKey.ToArray(), 0));
            verifier.BlockUpdate(message.ToArray(), 0, message.Length);
            return verifier.VerifySignature(signature.ToArray());
        }
        catch (Exception)
        {
            return false;
        }
    }

    // ------------------------------------------------------------- persistence

    private sealed record StoredIdentity(string DeviceId, string PublicIdentifier, string Ed25519Seed);

    public static DeviceIdentity LoadOrCreate(string stateDirectory)
    {
        var log = Log.For("identity");
        var path = Path.Combine(stateDirectory, "identity.json");

        if (File.Exists(path))
        {
            try
            {
                var stored = JsonSerializer.Deserialize<StoredIdentity>(File.ReadAllText(path));
                if (stored is not null)
                {
                    var identity = new DeviceIdentity(
                        Hex.Parse(stored.DeviceId),
                        Guid.Parse(stored.PublicIdentifier),
                        Hex.Parse(stored.Ed25519Seed));
                    log.Debug($"loaded identity {identity.DeviceId} pk={identity.Ed25519PublicKeyHex[..16]}...");
                    return identity;
                }
            }
            catch (Exception ex)
            {
                // A corrupt identity file is not worth failing over; make a fresh one and
                // accept that paired devices will see a new receiver.
                log.Warn($"identity file at {path} is unreadable, regenerating", ex);
            }
        }

        var created = new DeviceIdentity(CreateDeviceId(), Guid.NewGuid(), RandomNumberGenerator.GetBytes(32));
        try
        {
            Directory.CreateDirectory(stateDirectory);
            File.WriteAllText(path, JsonSerializer.Serialize(
                new StoredIdentity(
                    Hex.ToUpper(created.DeviceIdBytes),
                    created.PublicIdentifier.ToString(),
                    Hex.ToLower(created.Ed25519Seed)),
                new JsonSerializerOptions { WriteIndented = true }));
            log.Info($"created identity {created.DeviceId}");
        }
        catch (Exception ex)
        {
            log.Warn($"could not persist identity to {path}; it will change on next launch", ex);
        }
        return created;
    }

    /// <summary>
    /// Derives the device id from the primary network adapter so it stays stable even if
    /// the state file is deleted, falling back to random bytes on a machine with no usable
    /// adapter. The locally-administered bit is set so the id can never collide with real
    /// Apple hardware on the same network.
    /// </summary>
    private static byte[] CreateDeviceId()
    {
        var nic = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up)
            .Where(n => n.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211)
            .FirstOrDefault(n => n.GetIPProperties().UnicastAddresses
                .Any(a => a.Address.AddressFamily == AddressFamily.InterNetwork));

        var bytes = nic?.GetPhysicalAddress().GetAddressBytes();
        if (bytes is not { Length: 6 }) bytes = RandomNumberGenerator.GetBytes(6);

        var id = (byte[])bytes.Clone();
        id[0] = (byte)((id[0] | 0x02) & 0xFE); // locally administered, unicast
        return id;
    }
}
