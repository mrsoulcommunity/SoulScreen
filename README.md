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
| Light and dark themes, eight accent colours | working — follows Windows, or set by hand |
| Command palette, mini player, captures gallery | working |
| Markup: pen, highlighter and laser pointer over the picture | working |
| Captures viewer, with recordings played in place | working |
| Ask before an iPhone mirrors; allowed and blocked iPhones | working |
| Connection check: network, firewall and helpers, with fixes | working |
| Shortcuts from any app, taskbar thumbnail buttons | working |
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

Or double-click `RUN.bat` for the app itself: it builds a Release copy into `artifacts\run`
and starts it from there, first offering to close a copy that is already running, since only
one runs at a time.

For a build you can keep and run without the source tree:

```powershell
pwsh tools/publish.ps1                 # framework-dependent, ~110 MB
pwsh tools/publish.ps1 -SelfContained  # bundles the .NET runtime too
```

### In the app

| | |
| --- | --- |
| `Ctrl+K` | Find any command |
| `F11` | Fullscreen (`Esc` leaves) |
| `Ctrl+Shift+M` | Mini player (double-click or `Esc` returns) |
| `Space` | Pause the picture; the phone stays connected |
| `Ctrl+S` | Screenshot to Pictures\SoulScreen |
| `Ctrl+G` | Captures: every screenshot and recording |
| `Ctrl+Shift+C` | Screenshot to the clipboard |
| `Ctrl+R` | Record the session to MP4 |
| `Ctrl+E` | Markup: draw over the picture (`Ctrl+Z` takes back a stroke) |
| `Ctrl+M` | Mute the phone's audio |
| `Ctrl+↑` `Ctrl+↓` | Volume |
| `Ctrl+1` … `Ctrl+4` | Fit, fill, stretch, actual size |
| `Ctrl+Shift+R` | Rotate 90° |
| `Ctrl`+wheel | Zoom; drag to pan, `Ctrl+0` to reset |
| `Ctrl+I` | Statistics overlay |
| `Ctrl+T` | Keep the window on top |
| `Ctrl+D` | Disconnect the phone |
| `Ctrl+L` `Ctrl+,` | Activity log, settings |
| `Ctrl+Alt+Shift+S` `R` `M` `O` | From any app, once switched on: screenshot, record, mini player, show the window |
| `F1` | The list above, in the app |

Right-clicking the picture reaches the same things. The whole list is in the app under
**Settings → Keyboard shortcuts**.

### Finding a command

`Ctrl+K` opens the command palette: type a few letters of what you want - "rec", "mini",
"light" - and press Enter. It lists only what can be done at that moment, so there is no
"Stop recording" while nothing is recording, and each row shows its shortcut, which is the
quickest way to learn them. Every word typed has to match, so "rot up" goes straight to
turning the picture upright.

### The mini player

`Ctrl+Shift+M` shrinks the window to the phone's screen alone, in the corner of the display,
above everything else - for keeping an eye on the phone while working in another app. Drag
the picture to move it - let go near an edge and it settles against it - drag an edge to resize it; mute, pause and screenshot appear over
the picture while the pointer is on it. Double-click or `Esc` puts the window back exactly
as it was, and the player reopens next time where and at the size it was left.

### Pausing the picture

`Space` holds the picture still while the phone carries on underneath - to point at
something during a presentation, or read a message before it scrolls away. Sound, recording
and the connection all keep going, and resuming lands on the live picture, not on a backlog.

### Captures

`Ctrl+G` shows every screenshot and recording in the capture folder. Click one
to look at it without leaving the app: the arrow keys step through the rest, `Space` plays a
recording and `Delete` sends the one on screen to the Recycle Bin. Drag a tile out to drop the
file into Explorer or a chat; right-click, or `Ctrl+C` and `Delete` on a focused tile, to copy it (as the file,
and as the picture for a screenshot), find it in Explorer, or move it to the Recycle Bin. Only
files SoulScreen named are listed, so pointing the capture folder at Pictures does not bring
the whole of Pictures in with it. Screenshots can be saved as PNG or, a fraction of the size,
as JPEG. Two taken in the same second no longer overwrite each other.

The gallery has its own **search** - every word typed must appear in the file name, and typing
`rec` or `shot` finds recordings and screenshots by kind without learning any syntax - and a
**sort** choice: newest first, oldest first, or largest first. A summary line adds the folder
up: how many captures and how much drive they take. A **storage budget** (off, 5, 20 or 50 GB,
or a byte count hand-set in settings.json) moves the oldest captures to the Recycle Bin once
the folder grows past it, so a long recording session can never quietly fill the drive.

Recording remuxes the phone's own H.264 rather than re-encoding it, so the picture costs
almost nothing and loses no quality. Sound is the exception: the phone sends AAC-ELD, which
few players outside FFmpeg can open, so it is decoded and re-encoded as ordinary AAC — a
fraction of a core, and a file that plays everywhere. The audio track is laid against the
picture by arrival time and lost packets become silence, so the two do not drift apart over
a long recording. Recording starts on the next keyframe, which is why the counter can sit at
"waiting for a keyframe" for a moment. Right-clicking while recording offers a **timed stop**
- 1, 5, 10 or 30 minutes, or cancel - and the countdown reads on the recording badge, so a
recording left to end on its own is never a surprise.

### Markup

`Ctrl+E` lays a pen, a highlighter and a laser pointer over the picture, for pointing something
out in a demonstration or a lesson. `P`, `H`, `L` and `E` pick the pen, highlighter, laser and
eraser; the round button beside them chooses the ink and the width, and `Ctrl+Z` takes back the
last stroke. A laser stroke fades on its own a second after it is drawn.

The drawing stays on the picture as the window is resized, zooms with it, and is carried into
any screenshot taken while marking up, at the phone's own resolution. `Space` still pauses the
picture, which is the easy way to draw on something that would otherwise scroll away. Leaving
markup (`Done` or `Esc`) clears the drawing, as does anything that changes the picture's shape:
a new fit, a rotation, or the phone turning.

### Who may mirror

With **Ask before an iPhone mirrors** (Settings, Privacy), a phone that has not been allowed
before waits on a question: its screen and sound stay hidden, and nothing of it can be captured,
until it is allowed. **Always allow** remembers it; **Block this iPhone** turns it away from then
on, whether asking is switched on or not. Both lists can be edited in the same place.

AirPlay mirroring tells the receiver only a phone's name and model, so that is what the lists go
by. It keeps the wrong phone off the screen - a housemate's, a colleague's - rather than being a
lock.

### When the iPhone cannot find this PC

**iPhone can't find this PC?** on the idle screen, or *Check the connection* in the command
palette, reads everything on this PC that decides it: whether the receiver is running, which
network the PC is on and whether Windows treats it as public, what Windows Firewall does with
SoulScreen, and whether the FairPlay helper, the decoder, graphics acceleration and a sound device
are there. Each gets a plain verdict and, where there is one, a fix. The firewall fix replaces
whatever rules name SoulScreen - including the block Windows leaves behind when its "allow
access?" prompt is closed - with rules that allow it, through Windows' own permission prompt.
**Copy the report** puts the whole check on the clipboard.

### Without the window in front

**Shortcuts that work in any app** (Settings, Window and system) adds `Ctrl+Alt+Shift+S` for a
screenshot, `R` to record, `M` for the mini player and `O` to bring SoulScreen forward, whichever
application has the keyboard. They are off by default, since they take keys from every other
app, and a shortcut another program already holds is named in the settings. The taskbar
thumbnail carries screenshot, record, mute and mini player buttons, the taskbar button wears a
red dot while recording, and the notification-area menu can take a screenshot, record, or open
the mini player and Captures.

A recording watches the drive it is writing to: when space runs low it says roughly how much
recording time is left, and it stops before the drive fills, so what was recorded stays playable.

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

- **Search** (`Ctrl+F` while the panel is open) narrows every card to the options that match.
  With the window wide enough, a sidebar lists the sections as System Settings does.
- **Theme** follows the Windows light or dark setting, or can be pinned either way, and the
  **accent colour** can be any of Apple's eight.
- **When a phone connects**, SoulScreen can come to the front, go fullscreen, and start
  recording on its own - and leave fullscreen again when the phone disconnects.
- **Fit**, **rotation** and **mirror** decide how the phone's screen sits in the window; the
  window can hold the picture's shape while it is resized.
- **Round the picture's corners** draws the phone's screen with its own rounded corners.
- **Smoothness** chooses how much picture is held back to even out Wi-Fi - see below.
- **Output device** sends the phone's sound to a chosen endpoint rather than the default.
- **Minimise / close to the notification area** keeps the receiver running with the window
  out of the way, and **start when you sign in** opens it there ready for the phone.
- **Animations** can follow Windows' own reduce-motion setting, be pinned on, or be pinned
  off - every transition in the app answers to it.
- **Display** moves the window to any monitor this PC has, and keeps it there when one is
  unplugged or the desktop changes.
- **Export / import settings** writes the preferences to a JSON file and reads them back on
  another machine: preferences travel, while the receiver name, the window place, the phone
  history and the trust decisions stay this machine's own.
- **Performance graph** draws the last minute of frame pace and buffering under the
  statistics overlay, so a stutter can be told from a spike at a glance.

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
