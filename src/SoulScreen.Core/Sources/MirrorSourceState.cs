namespace SoulScreen.Core.Sources;

public enum MirrorSourceState
{
    /// <summary>Not listening; nothing advertised, no device attached.</summary>
    Stopped,
    /// <summary>Listening/advertising, but no device has started a session.</summary>
    Ready,
    /// <summary>A device is negotiating (pairing, handshake, stream setup).</summary>
    Connecting,
    /// <summary>Media is flowing.</summary>
    Streaming,
    /// <summary>The source hit an error it could not recover from; see the message.</summary>
    Faulted,
}

/// <param name="Name">Display name reported by the device, e.g. "Kasra's iPhone".</param>
/// <param name="Model">Hardware model string when the transport exposes one.</param>
/// <param name="Identifier">Stable per-device id used to remember pairings.</param>
public readonly record struct SourceDeviceInfo(string Name, string? Model = null, string? Identifier = null)
{
    public override string ToString() => Model is null ? Name : $"{Name} ({Model})";
}
