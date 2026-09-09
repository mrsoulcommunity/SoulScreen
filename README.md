# SoulScreen

Mirror an iPhone screen onto Windows.

Two transports, one app:

- **AirPlay** — the PC advertises itself as an AirPlay receiver, so it shows up under
  *Control Center → Screen Mirroring* with nothing installed on the phone.
- **USB** — the hidden QuickTime video stream an iPhone exposes over its cable. Lower
  latency, no FairPlay, but needs a driver swap on Windows. *(in progress)*

Windows 11, .NET 8, x64.

---

## Status

Wireless mirroring works end to end. Verified against an iPhone 17 Pro on iOS 26:
the receiver appears in Control Center, pairs, completes FairPlay, and renders the
phone's screen at 498x1080 / 59.9 fps.

| Piece | State |
| --- | --- |
| mDNS / Bonjour advertisement | working — receiver appears in Control Center |
| RTSP control channel | working |
| Legacy pairing (pair-setup / pair-verify) | working |
| FairPlay handshake (`/fp-setup`) | working, via an optional native helper |
| Mirrored video: receive, decrypt, decode, display | working |
| Audio (AAC-ELD decode and playback) | working |
| Recording to MP4 | working — remuxed, not re-encoded |
| USB transport | deferred; see [src/SoulScreen.Usb/UsbTransport.cs](src/SoulScreen.Usb/UsbTransport.cs) |

---

## Getting started

```powershell
# 1. Build
dotnet build

# 2. Build the FairPlay helper (needed for wireless mirroring - see below)
pwsh tools/build-fairplay.ps1

# 3. Allow the phone through Windows Firewall (elevated PowerShell)
pwsh tools/firewall.ps1

# 4. Run the receiver
dotnet run --project src/SoulScreen.Cli -- serve --dump capture
```

Then on the iPhone: **Control Center → Screen Mirroring → SoulScreen**.

For a build you can keep and run without the source tree:

```powershell
pwsh tools/publish.ps1                 # framework-dependent, ~110 MB
pwsh tools/publish.ps1 -SelfContained  # bundles the .NET runtime too
```

### In the app

| | |
| --- | --- |
| `F11` | Fullscreen (`Esc` leaves) |
| `Ctrl+S` | Screenshot to Pictures\SoulScreen |
| `Ctrl+R` | Record the session to MP4 |
| `Ctrl+M` | Mute the phone's audio |

Recording remuxes the phone's own H.264 rather than re-encoding it, so it costs
almost nothing and loses no quality. It starts on the next keyframe, which is why
the counter can sit at "waiting for a keyframe" for a moment. Video only for now —
the audio track is not muxed.

With `--dump capture` the decrypted H.264 is written to `capture\mirror-*.h264`, which
plays in VLC or `ffplay` — the quickest way to confirm the protocol side end to end while
the in-app renderer is still being built.

### CLI

```
serve      [--name <n>] [--port <p>] [--dump <dir>] [--no-audio]   run the receiver
advertise  [--name <n>] [--port <p>]                               mDNS only, no control channel
browse                                                             list AirPlay receivers on this LAN
fairplay                                                           self-test the native helper
```

`--trace` on any command turns on per-request protocol logging.

---

## Why there is a native helper

iOS will not release the AES key for a mirroring stream until the receiver answers Apple's
proprietary **FairPlay SAP** handshake on `/fp-setup`. There is no public specification and
no clean-room implementation; every open-source AirPlay receiver uses the same
reverse-engineered `playfair` code, which is **GPLv3** and roughly half a megabyte of
generated lookup tables.

`tools/build-fairplay.ps1` downloads those sources into `native/fairplay` (gitignored) and
compiles them with MinGW gcc into a standalone `soulscreen_fairplay.dll`. The managed code
reaches it through five P/Invoke entry points and nothing else, so the GPL code is never
linked into the C# assemblies and is not committed to this repository.

Without the DLL, SoulScreen still starts, still advertises, and still pairs — mirroring
just fails at the handshake with a message saying so. Check it with:

```powershell
dotnet run --project src/SoulScreen.Cli -- fairplay
```

You need a C compiler for the build step:

```powershell
winget install BrechtSanders.WinLibs.POSIX.UCRT   # or MSYS2.MSYS2
```

---

## Layout

```
src/
  SoulScreen.Core/       transport-agnostic contracts: IMirrorSource, media samples, logging
  SoulScreen.AirPlay/    the wireless receiver
    Discovery/           mDNS responder and the DNS-SD service definitions
    Rtsp/                control-channel server and the AirPlay method handlers
    Pairing/             device identity, pair-setup, pair-verify
    FairPlay/            P/Invoke bridge to the native helper
    Streams/             mirrored video, audio, stream ciphers, H.264 helpers
    Plist/               binary and XML property lists
    Crypto/              stateful AES-CTR
  SoulScreen.Usb/        the USB/QuickTime transport (in progress)
  SoulScreen.Media/      decode and render (in progress)
  SoulScreen.App/        WPF shell
  SoulScreen.Cli/        headless receiver and protocol tools
tests/SoulScreen.Tests/  protocol, crypto and wire-format tests
tools/                   build and diagnostic scripts
```

`SoulScreen.AirPlay` has no dependency on the UI and can be embedded on its own.

---

## Testing

```powershell
dotnet test
python tools/check-plist-interop.py    # after the suite: verifies the plist writer against plistlib
```

The suite deliberately checks against *independent* implementations rather than only
round-tripping our own code:

- binary property lists are read from, and verified by, Python's `plistlib`
- AES-CTR is checked against the NIST SP 800-38A vectors
- the mirroring stream key schedule and its cross-packet keystream carry are pinned to
  values computed outside this codebase
- SPS dimension parsing is checked against streams produced by a separate exp-Golomb encoder

---

## Troubleshooting

**The receiver never appears on the phone.**
Both devices must be on the same subnet — a "guest" or client-isolated Wi-Fi will not work.
Check the firewall rules applied to the right network profile (`tools/firewall.ps1` prints
your active profile), then confirm the advertisement is going out with
`dotnet run --project src/SoulScreen.Cli -- browse` from this PC.

**It appears, but mirroring fails immediately.**
Almost always FairPlay. Run the `fairplay` self-test. The log line to look for is
`fp-setup failed` or `FairPlay helper missing`.

**It connects but the picture is garbage, with `did not parse as length-prefixed H.264`.**
The stream key is wrong. That key is derived from the FairPlay key *and* the pair-verify
ECDH secret, so it usually means pair-verify did not complete — run with `--trace` and
check for `pair-verify completed`.

**The picture is letterboxed.**
iOS encodes to fit the resolution the receiver advertises, so a portrait phone against a
16:9 receiver gets black bars. Set a portrait size such as 1080 x 1920 under Settings if
you mostly mirror portrait.

**Everything connects but the window stays black.**
Check the activity log for `reference picture missing`. That means keyframes are being
lost, and the picture cannot recover until the phone sends another one. It should not
happen — the pipeline refuses to drop keyframes — but a machine that cannot decode in real
time will show it; lower the advertised resolution or frame rate in Settings.

**Port 5353 will not bind.**
Apple's Bonjour service (installed with iTunes) also uses it. SoulScreen shares the port
with address reuse, so this normally works; if it does not, stopping the
*Bonjour Service* in `services.msc` frees it.

---

## Licence and scope

SoulScreen's own code is unencumbered. The FairPlay helper it can load is built from
GPLv3 sources that this repository does not contain or redistribute; see
`native/fairplay/LICENSE.md` after running the build script.

Built for interoperability on hardware you own.
