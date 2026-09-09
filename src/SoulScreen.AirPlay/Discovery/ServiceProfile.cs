namespace SoulScreen.AirPlay.Discovery;

/// <summary>
/// One DNS-SD service to advertise. AirPlay mirroring needs two of these: the
/// <c>_airplay._tcp</c> service that iOS lists under Screen Mirroring, and the
/// <c>_raop._tcp</c> service that carries the audio half of the session.
/// </summary>
public sealed class ServiceProfile
{
    public ServiceProfile(string instanceName, string serviceType, ushort port, string hostName)
    {
        InstanceName = instanceName;
        ServiceType = serviceType.TrimEnd('.');
        Port = port;
        HostName = hostName.EndsWith(".local", StringComparison.OrdinalIgnoreCase) ? hostName : hostName + ".local";
    }

    /// <summary>Service instance label, e.g. "SoulScreen" or "AABBCCDDEEFF@SoulScreen".</summary>
    public string InstanceName { get; }

    /// <summary>Service type without the domain, e.g. "_airplay._tcp".</summary>
    public string ServiceType { get; }

    public ushort Port { get; }

    /// <summary>Host the SRV record points at, e.g. "SoulScreen.local".</summary>
    public string HostName { get; }

    /// <summary>TXT entries as raw "key=value" strings, in the order Apple's receivers emit them.</summary>
    public List<string> TxtEntries { get; } = [];

    /// <summary>Fully qualified service type, e.g. "_airplay._tcp.local".</summary>
    public string QualifiedServiceType => ServiceType + ".local";

    /// <summary>Fully qualified instance name, e.g. "SoulScreen._airplay._tcp.local".</summary>
    public string FullName => InstanceName + "." + QualifiedServiceType;

    public void SetTxt(string key, string value)
    {
        var prefix = key + "=";
        var index = TxtEntries.FindIndex(e => e.StartsWith(prefix, StringComparison.Ordinal));
        if (index >= 0) TxtEntries[index] = prefix + value;
        else TxtEntries.Add(prefix + value);
    }

    /// <summary>
    /// The TXT entries in DNS record data form - each entry prefixed with its length.
    /// <para>
    /// This is not only for the mDNS responder. The first thing iOS asks a receiver over
    /// HTTP is <c>GET /info</c> with a qualifier of "txtAirPlay", and the reply it expects
    /// is exactly these bytes wrapped in a property list, so the two paths have to agree
    /// byte for byte.
    /// </para>
    /// </summary>
    public byte[] EncodeTxtRecordData()
    {
        var writer = new Core.Buffers.BufferWriter(256);
        foreach (var entry in TxtEntries)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(entry);
            if (bytes.Length > 255)
                throw new InvalidOperationException($"TXT entry is {bytes.Length} bytes; the limit is 255.");
            writer.WriteUInt8((byte)bytes.Length);
            writer.Write(bytes);
        }
        return writer.ToArray();
    }

    public override string ToString() => $"{FullName} -> {HostName}:{Port} ({TxtEntries.Count} TXT)";
}
