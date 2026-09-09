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

    public override string ToString() => $"{FullName} -> {HostName}:{Port} ({TxtEntries.Count} TXT)";
}
