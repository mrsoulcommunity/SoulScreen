using SoulScreen.Core.Sources;

namespace SoulScreen.Usb;

/// <summary>
/// Placeholder for the wired transport: the hidden QuickTime video stream an iPhone exposes
/// over its cable.
/// <para>
/// Not implemented. This file exists so the plan and its blocker are recorded next to where
/// the work would go, rather than living only in a conversation.
/// </para>
///
/// <para><b>Why it is worth having.</b> The wired path carries the same H.264 the wireless
/// one does, but with none of the Wi-Fi variance and no FairPlay: the stream is not
/// encrypted, so the whole pairing and key-unwrapping layer disappears. Latency is roughly
/// half.</para>
///
/// <para><b>How it works.</b> An iPhone ships with a second, inactive USB configuration.
/// A vendor control request switches to it, the device re-enumerates, and a pair of bulk
/// endpoints then carry a packet protocol: an initial handshake exchanging clock references,
/// then a stream of "sample buffer" records holding H.264 access units and their timing.
/// The framing is a nested four-character-code format, closely related to CoreMedia's
/// in-memory layout.</para>
///
/// <para><b>What blocks it on Windows.</b> Reading those endpoints needs a driver bound to
/// the interface that exposes them. Apple's own driver does not claim it, so WinUSB has to
/// be attached - usually by hand with Zadig. That is a system-wide change to how the phone
/// is seen, and it can stop Apple Devices and iTunes recognising it until reverted. The
/// change is reversible, but it is not something an app should make on a user's behalf, and
/// it is the reason this is deferred rather than merely unfinished.</para>
///
/// <para><b>Where it would plug in.</b> <see cref="IMirrorSource"/> already describes
/// everything the rest of SoulScreen needs from a transport, and the render pipeline,
/// recorder and UI are written against it rather than against AirPlay. A USB source would
/// implement that interface and emit the same H.264 access units; nothing downstream would
/// change.</para>
/// </summary>
public static class UsbTransport
{
    /// <summary>Always false. See the type documentation for what implementing this needs.</summary>
    public static bool IsSupported => false;

    /// <summary>Explains the current state, for a UI that wants to offer the option.</summary>
    public static string UnavailableReason =>
        "The USB transport is not implemented yet. It needs the WinUSB driver bound to the " +
        "iPhone's QuickTime interface, which changes how Windows sees the phone.";
}
