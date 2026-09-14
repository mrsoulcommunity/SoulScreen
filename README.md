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
| Recording to MP4 | working — video remuxed, audio re-encoded to AAC |
| Light and dark themes | working — follows Windows, or set by hand |
| Notification-area icon, launch at sign-in | working |
| Demo pattern, for trying the app with no phone | working |
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
| `Ctrl+Shift+C` | Screenshot to the clipboard |
| `Ctrl+R` | Record the session to MP4 |
| `Ctrl+M` | Mute the phone's audio |
| `Ctrl+↑` `Ctrl+↓` | Volume |
| `Ctrl+1` … `Ctrl+4` | Fit, fill, stretch, actual size |
| `Ctrl+Shift+R` | Rotate 90° |
| `Ctrl`+wheel | Zoom; drag to pan, `Ctrl+0` to reset |
| `Ctrl+I` | Statistics overlay |
| `Ctrl+T` | Keep the window on top |
| `Ctrl+D` | Disconnect the phone |
| `Ctrl+L` `Ctrl+,` | Activity log, settings |
| `F1` | The list above, in the app |

Right-clicking the picture reaches the same things. The whole list is in the app under
**Settings → Keyboard shortcuts**.

Recording remuxes the phone's own H.264 rather than re-encoding it, so the picture costs
almost nothing and loses no quality. Sound is the exception: the phone sends AAC-ELD, which
few players outside FFmpeg can open, so it is decoded and re-encoded as ordinary AAC — a
fraction of a core, and a file that plays everywhere. The audio track is laid against the
picture by arrival time and lost packets become silence, so the two do not drift apart over
a long recording. Recording starts on the next keyframe, which is why the counter can sit at
"waiting for a keyframe" for a moment.

### No phone to hand

**Try a demo without a phone**, on the idle screen, encodes a moving test pattern and puts
it through the same decode, pacing and display path a real session uses. It is the quickest
way to see whether this PC can keep up, and to try the picture controls and recording
without reaching for a phone. Disconnect ends it and the receiver comes back.

### Settings

Everything is in one panel (`Ctrl+,`), and everything but the receiver's own name, port,
advertised resolution and audio switch takes effect the moment it is changed. Those four
need the receiver to restart, so they are gathered behind one **Apply** button rather than
restarting it under you as you type.

Worth knowing about:

- **Theme** follows the Windows light or dark setting, or can be pinned either way.
- **Fit**, **rotation** and **mirror** decide how the phone's screen sits in the window; the
  window can hold the picture's shape while it is resized.
- **Smoothness** chooses how much picture is held back to even out Wi-Fi - see below.
- **Output device** sends the phone's sound to a chosen endpoint rather than the default.
- **Minimise / close to the notification area** keeps the receiver running with the window
  out of the way, and **start when you sign in** opens it there ready for the phone.

### Smoothness, and the delay it costs

Frames leave the phone at an even sixty a second and arrive over Wi-Fi in clumps —
two or three at once, then nothing for fifty milliseconds. Drawing each one as it
turns up puts that clumping on the screen; the picture judders even though no frame
was lost. So SoulScreen holds a cushion of decoded frames and releases them at the
rate the phone is sending. The cushion is a tenth of a second — measured in time rather
than frames, so it stays the same when the phone drops to thirty frames a second — and
that tenth of a second is the difference between "every frame arrived" and "it looks
smooth".

**Smoothness** in the settings moves that figure: 160 ms hides all but a real outage,
50 ms is for a link good enough not to need the cushion, and the default 100 ms suits an
ordinary home network. The audio margin follows it, so the sound stays level with the lips
whichever is chosen.

Where the display cannot show every frame — a 30 Hz panel, or a window Windows is
compositing at a reduced rate because something covers it — the cushion would otherwise
fill and stay full, leaving the mirror a third of a second behind for the rest of the
session. Instead one extra frame per pass is skipped until the delay is back where it was
set. Those frames are counted, and they are the ones the display was never going to show.

Audio is held to match, so the two stay in step. The phone sends every audio packet
three times over, and each is played once. About 80 ms of sound is kept waiting ahead
of the speakers, so a Wi-Fi pause shorter than that is never heard; a pause that is
heard raises the margin for a while, so the next one like it is not. Audio that a stall
delivers all at once is spliced or skipped back into step with the picture rather than
left trailing behind it.

The status bar carries the figures that say whether it is working:

| | |
| --- | --- |
| `60 fps` | frames the phone is sending, as decoded |
| `60 Hz` | your display's refresh rate. A source rate that is not a whole fraction of this judders no matter what — see the note the app raises when they do not divide |
| `105 ms` | how long a frame takes from leaving the decoder to reaching the screen, cushion included |
| `buf 6` | frames in the cushion: a tenth of a second's worth, so six at 60 fps and three at 30. A persistent zero means it is being drained faster than it fills |
| `12 skipped` | frames that never reached the screen. Zero on a display that keeps up with the phone; on one that cannot, the shortfall between the two rates |
| `audio 90 ms` | audio waiting to be played. Around 90 ms normally, and higher for a while after a Wi-Fi pause that was heard |

`Ctrl+I` puts the same figures over the picture, with the device, the bit rate and the
breakdown of what was skipped and where.

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
  SoulScreen.Media/      decode, pace, play and record
  SoulScreen.App/        WPF shell: chrome, picture, settings, log, tray, metrics
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
- frame pacing is driven from a simulated clock and compared against the obvious
  alternative — draw each frame at the next composition pass — on arrival patterns where
  the two visibly differ, so "it looks smoother" is a measurement rather than an opinion
- a display slower than the source is simulated too, and the cushion is required to hold
  its delay rather than fill to the ceiling and stay there
- recordings are written and then read back with an independent demuxer, including the
  audio track: two streams, the right codecs, and an audio length that matches the picture
  even when packets went missing on the way in

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
Check the activity log for `the decoder fell N frames behind`. Decoding is not keeping up
with the phone, so the backlog was thrown away and the picture waits for the next keyframe
— which in mirroring can be a couple of seconds, and if it keeps happening the window
spends most of its time waiting. Lower the advertised resolution or frame rate in Settings.
The other thing to check is the render tier logged at startup: `render tier 0` means Windows
is compositing this window on the CPU, usually a remote desktop session or a missing
graphics driver, and nothing on this side will make 60 fps smooth in that state.

**The sound is choppy or late.**
Each audio stream logs how it went when it closes: `audio stream closed after N packets
(… lost); discarded … redundant copies and … late packets`. Redundant copies should come
to about twice the packet count — that is the phone's redundancy, discarded as intended.
When the receiver stops, `audio closed after … packets: S stalls …` follows. A stall is a
Wi-Fi pause long enough to be heard: a few in a session is ordinary Wi-Fi, while one every
few seconds means the phone's link is struggling, and a 5 GHz network will do more for it
than anything on this side can.

**The picture is blocky, or drifts green, and then snaps clean for a moment.**
That is a run of inter frames decoded against a reference that does not match the phone's,
healing each time a keyframe arrives. It should no longer happen: the decoder is held to
exact, specification-compliant reconstruction, and the frame queue is never thinned by
pulling single frames out of the middle of it. If you do see it, the activity log around
the moment it starts is the thing to capture.

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
