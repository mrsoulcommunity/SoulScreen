namespace SoulScreen.AirPlay;

public sealed class AirPlayOptions
{
    /// <summary>Name shown in the iPhone's Screen Mirroring list.</summary>
    public string DeviceName { get; set; } = "SoulScreen";

    /// <summary>TCP port for the RTSP/HTTP control channel. 7000 is what Apple receivers use.</summary>
    public ushort Port { get; set; } = 7000;

    /// <summary>
    /// Model identifier reported to the sender. iOS tailors the stream it sends to the
    /// model it thinks it is talking to, and AppleTV3,2 is the safest match for a receiver
    /// that speaks the legacy pairing and FairPlay handshake.
    /// </summary>
    public string Model { get; set; } = "AppleTV3,2";

    /// <summary>Source version reported in TXT records and /info.</summary>
    public string SourceVersion { get; set; } = "220.68";

    public ulong Features { get; set; } = AirPlayFeaturePresets.MirroringWithAudio;

    /// <summary>Accept the phone's audio in addition to its screen.</summary>
    public bool EnableAudio { get; set; } = true;

    /// <summary>
    /// Optional passcode the sender must enter. Empty disables the prompt entirely, which
    /// is the usual choice on a home network.
    /// </summary>
    public string? Password { get; set; }

    /// <summary>Width advertised in /info. iOS uses it to pick an encode resolution.</summary>
    public int DisplayWidth { get; set; } = 1920;

    public int DisplayHeight { get; set; } = 1080;

    /// <summary>Refresh rate advertised in /info, as a frames-per-second value.</summary>
    public int DisplayRefreshRate { get; set; } = 60;

    /// <summary>
    /// Where the persistent pairing key and device id live. Defaults to
    /// %LOCALAPPDATA%\SoulScreen.
    /// </summary>
    public string StateDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SoulScreen");

    /// <summary>
    /// When set, every decrypted video and audio elementary stream is also written to this
    /// directory. Invaluable while bringing the protocol up: the dump can be played with
    /// ffplay even when the in-app renderer is not working yet.
    /// </summary>
    public string? DumpDirectory { get; set; }

    /// <summary>Log every RTSP request and response line. Very noisy; off by default.</summary>
    public bool TraceProtocol { get; set; }
}
