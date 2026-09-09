using SoulScreen.AirPlay.Pairing;

namespace SoulScreen.AirPlay.Discovery;

/// <summary>
/// Builds the two DNS-SD services an AirPlay mirroring receiver must publish, with the
/// TXT entries an Apple TV emits. iOS reads these before it ever opens a TCP connection,
/// so the receiver's whole capability story is told here.
/// </summary>
public static class AirPlayAdvertisement
{
    public static (ServiceProfile AirPlay, ServiceProfile Raop) Build(AirPlayOptions options, DeviceIdentity identity)
    {
        var featureString = AirPlayFeaturePresets.Format(options.Features);
        var hostName = SanitiseLabel(options.DeviceName);
        var instanceName = SanitiseLabel(options.DeviceName);
        var publicKey = identity.Ed25519PublicKeyHex;

        var airplay = new ServiceProfile(instanceName, "_airplay._tcp", options.Port, hostName);
        airplay.SetTxt("acl", "0");
        airplay.SetTxt("deviceid", identity.DeviceId);
        airplay.SetTxt("features", featureString);
        airplay.SetTxt("rsf", "0x0");
        airplay.SetTxt("fv", "p20.78000.12");
        // 0x4 marks the receiver as available and not currently in a session.
        airplay.SetTxt("flags", "0x4");
        airplay.SetTxt("model", options.Model);
        airplay.SetTxt("manufacturer", "SoulScreen");
        airplay.SetTxt("serialNumber", identity.DeviceId.Replace(":", ""));
        airplay.SetTxt("protovers", "1.1");
        airplay.SetTxt("srcvers", options.SourceVersion);
        airplay.SetTxt("pi", identity.PublicIdentifier.ToString());
        airplay.SetTxt("psi", identity.PublicIdentifier.ToString());
        airplay.SetTxt("gid", identity.PublicIdentifier.ToString());
        airplay.SetTxt("gcgl", "0");
        airplay.SetTxt("pk", publicKey);
        if (!string.IsNullOrEmpty(options.Password)) airplay.SetTxt("pw", "true");

        // RAOP instances are named "<deviceid without separators>@<display name>".
        var raopInstance = identity.DeviceId.Replace(":", "") + "@" + instanceName;
        var raop = new ServiceProfile(raopInstance, "_raop._tcp", options.Port, hostName);
        raop.SetTxt("txtvers", "1");
        raop.SetTxt("ch", "2");                  // stereo
        raop.SetTxt("cn", "0,1,2,3");            // PCM, ALAC, AAC-LC, AAC-ELD
        raop.SetTxt("da", "true");
        raop.SetTxt("et", "0,3,5");              // none, FairPlay, FairPlay SAPv2.5
        raop.SetTxt("ft", featureString);
        raop.SetTxt("md", "0,1,2");              // text, artwork, progress metadata
        raop.SetTxt("am", options.Model);
        raop.SetTxt("pk", publicKey);
        raop.SetTxt("sf", "0x4");
        raop.SetTxt("tp", "UDP");
        raop.SetTxt("sr", "44100");
        raop.SetTxt("ss", "16");
        raop.SetTxt("sv", "false");
        raop.SetTxt("vn", "65537");
        raop.SetTxt("vs", options.SourceVersion);
        if (!string.IsNullOrEmpty(options.Password)) raop.SetTxt("pw", "true");

        return (airplay, raop);
    }

    /// <summary>
    /// A DNS label cannot contain a dot, and our name writer splits on them. Rather than
    /// teach the writer about DNS-SD escaping for a name the user typed, fold dots into
    /// spaces so "Kasra v2.0" advertises cleanly.
    /// </summary>
    private static string SanitiseLabel(string name)
    {
        var cleaned = name.Replace('.', ' ').Trim();
        if (cleaned.Length == 0) cleaned = "SoulScreen";
        // A label is limited to 63 bytes on the wire.
        while (System.Text.Encoding.UTF8.GetByteCount(cleaned) > 63)
            cleaned = cleaned[..^1].TrimEnd();
        return cleaned;
    }
}
