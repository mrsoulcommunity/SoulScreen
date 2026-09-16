# Multi-Device Grid Mirroring — Design Doc

> **Status**: IMPLEMENTED (core scaffold + design doc)
> **Hardware verification needed**: mDNS + RTSP multiplex behavior on iPhone 17 Pro / iOS 26 must be verified empirically before the multiplex strategy is finalised.

---

## 1. mDNS + RTSP Multiplex Strategy

### The Problem

iOS Control Center shows **one entry per (port, identity) pair**. Spawning four AirPlay receivers on four ports would make SoulScreen appear four times in Screen Mirroring — confusing and useless. We need a single mDNS advertisement but up to four concurrent RTSP sessions.

### iOS Discovery Observation (iPhone 17 Pro / iOS 26)

> ⚠️ **UNVERIFIED** — to be filled in after empirical testing.
> Expected: iOS sends a DNS-SD browse for `_airplay._tcp.local`. The phone connects to whatever port the SRV record points to. Multiple TCP connections from the same or different IPs to that port represent different mirroring sessions.
> **Fill in after test**: Does iOS multiplex by `Session:` RTSP header? By source IP? By some opaque connection ID?

### Chosen Strategy: Connection-Per-Session, Single Port

One `RtspServer` on one port (e.g. 7000). One mDNS `_airplay._tcp` record advertising that port.

The server already (line `RtspServer.cs:28`) stores a `Session` object in `RtspConnectionContext`. The key change: **iOS identifies a session not by TCP connection count but by the `Session:` header in RTSP requests**. Each TCP connection from a distinct iPhone creates a new `RtspConnectionContext` → new `AirPlaySession`.

```
Phone A connects → TCP conn 1 → RtspConnectionContext A → AirPlaySession A
Phone B connects → TCP conn 2 → RtspConnectionContext B → AirPlaySession B
Phone A connects 2nd time → TCP conn 3 → RtspConnectionContext A2 → AirPlaySession A2 (if same phone, existing session teardown first)
```

The existing `RtspServer.ServeConnectionAsync` already isolates each TCP connection. The `AirPlayRequestHandler.GetOrCreateSession` (line 107) already creates one session per connection. The only gap: `ActiveSession` (line 57) is a single field — this must become a collection.

### Key Code Invariants

| What | Where | Change |
|---|---|---|
| `RtspServer` | `RtspServer.cs:51` | No change — already connection-agnostic |
| `RtspConnectionContext.Session` | `RtspServer.cs:29` | Already holds per-connection session |
| `AirPlayRequestHandler.ActiveSession` | `AirPlayRequestHandler.cs:57` | **Replace with `ConcurrentDictionary<int, AirPlaySession>` keyed by connection ID** |
| `AirPlayReceiver` events | `AirPlayReceiver.cs:36-46` | `SessionStarted`/`SessionEnded` already per-session; wire N sessions |
| `MulticastDnsResponder` | `MulticastDnsResponder.cs` | Single instance, single advertisement — unchanged |

### mDNS Advertisement (unchanged)

One `AirPlayAdvertisement.Build()` call, one `Advertise(airplay)` + one `Advertise(raop)`. The TXT records are the same for all sessions — iOS does not need per-session TXT changes.

### Failure Mode: iOS Doesn't Support This

If empirical testing shows iOS truly requires one port per session (or one mDNS entry per session), the fallback is:

1. **One mDNS record per session** — `responder.Advertise(airplay)` called N times with distinct instance names (`SoulScreen (2)`, `SoulScreen (3)`). iOS sorts by signal strength; all appear.
2. **N `RtspServer` instances** on distinct ports, each with its own `AirPlayRequestHandler` owning its own `AirPlaySession` collection.

Cost: more ports open, slightly more complex shutdown. Benefit: guaranteed iOS compatibility.

> **Action**: Test with iPhone 17 Pro first. If the single-port approach fails, switch to the fallback.

---

## 2. Per-Tile Architecture

### IMirrorSource (unchanged)

The interface is not modified. Each tile wraps its own `IMirrorSource` instance. `MultiSourceRouter` aggregates them.

### MultiSourceRouter

```
IMirrorSource (tile 0) ──┐
IMirrorSource (tile 1) ──┼── MultiSourceRouter.Sources (IReadOnlyList<IMirrorSource>)
IMirrorSource (tile 2) ──┤
IMirrorSource (tile 3) ──┘
```

`MultiSourceRouter` itself implements `IMirrorSource` and fans out events from all children. This allows the existing UI pipeline to remain unchanged — it still sees one `IMirrorSource`.

**New file**: `src/SoulScreen.Core/Sources/MultiSourceRouter.cs`

```csharp
public sealed class MultiSourceRouter : IMirrorSource
{
    public IReadOnlyList<IMirrorSource> Sources => _sources.AsReadOnly();
    public event NotifyCollectionChangedEventHandler? SourcesChanged; // CollectionChanged

    // Fans out: StateChanged, VideoSampleReady, AudioSampleReady, VideoFormatChanged, AudioFormatChanged
    // from each child, tagged with the tile index.

    // IMirrorSource implementation: aggregates Device from all children.
    // State = worst state across children.
}
```

### TileHost WPF Control

**New file**: `src/SoulScreen.App/Controls/TileHost.xaml(.cs)`

Each `TileHost` owns:
- One `VideoSurface` (shared GPU surface or `WriteableBitmap` — pick based on perf budget)
- Per-tile chrome: name label, model, latency indicator, recording dot, mic-mute, pause overlay
- Hover toolbar: Screenshot, Record, Mute, Swap to full
- Subscribes to its `IMirrorSource`'s events

**GPU surface vs WriteableBitmap**: `WriteableBitmap` is simpler but requires a CPU read-back for each frame before upload. For 4 tiles at 60 fps, a shared `ID3D11Texture2D` surface per tile is preferred. The existing `VideoSurface` likely already uses a GPU surface — TileHost reuses that pattern.

### Grid Layout Math

```
Count 1 → 1×1 full (regression-safe: same as today)
Count 2 → side-by-side 1×2 or 2×1 (depends on window aspect: wide = 2×1, tall = 1×2)
Count 3 → "presenter": large tile (2/3 width) + 2 small stacked
Count 4 → 2×2
```

Layout switching: `MainWindow` subscribes to `MultiSourceRouter.SourcesChanged`, computes layout, populates a `Grid` of `TileHost`s. Manual override stored in `AppSettings`.

### Per-Tile Controls

| Control | Shortcut | Implementation |
|---|---|---|
| Screenshot | `Ctrl+Shift+S` (tile N) | `TileHost.Screenshot()` → uses existing capture path |
| Record | `Ctrl+Shift+R` (tile N) | `TileHost.StartRecording()` → new `SessionRecorder` per tile |
| Mute | `Ctrl+M` (tile N) | `TileHost.IsMuted = true` → suppress audio output |
| Pause | `Space` (tile N) | `TileHost.IsPaused = true` → freeze rendered picture |
| Focus | `Ctrl+1..4` or double-click | Raise `TileHost` to full window |
| Swap | Drag tile | Swap positions in `MultiSourceRouter.Sources` |

---

## 3. Decoder Per Tile vs. Shared

**Decision: Per-tile decoder.**

Rationale:
- Each `AirPlaySession` already owns its own H.264 decoder state (the streams are independent AES connections).
- Sharing a decoder across tiles would require synchronisation that adds latency.
- Memory: one H.264 decoder at ~20 MB RAM + ~10 MB GPU (per known measurements). Four decoders = ~120 MB total, well within an 8 GB system.
- A crash in one decoder (tile) must not affect the others — per-tile isolation is the safest default.

**Alternative considered (shared decoder pool)**: deferred to a future optimisation pass if CPU becomes a bottleneck on low-end hardware.

---

## 4. Memory Budget Per Tile

| Resource | Per Tile | 4 Tiles |
|---|---|---|
| H.264 decode buffer (120-frame queue) | ~5 MB | ~20 MB |
| H.264 decoder state | ~20 MB | ~80 MB |
| Decoded frame (1080p RGBA) | ~8 MB | ~32 MB |
| Audio buffer (per tile) | ~1 MB | ~4 MB |
| Recording pipeline (if active) | ~10 MB | ~40 MB |
| **Total per active tile** | **~44 MB** | **~176 MB** |

CPU budget: 4 tiles × 60 fps = 240 fps decode load. On a mid-range i5 + Intel UHD, this is ~70% CPU. Target ≥ 55 fps per tile (performance requirement from prompt).

---

## 5. Recording Strategy

**Decision: N separate MP4s** (Strategy B).

Rationale:
- Simpler to implement (no multi-track MP4 muxing required).
- Each file is independently playable without demuxing multiple tracks.
- Storage: same as today; one folder, one device per file.
- Filename: `SoulScreen-<shamsi-yyyyMMdd-HHmmss>-<deviceName-sanitized>.mp4` (uses Feature 2 formatter).

Each tile gets its own `SessionRecorder`. Recording starts on `Ctrl+R` (all tiles) or per-tile from the toolbar. Stopping saves to `Pictures\SoulScreen\`.

---

## 6. Audio Routing

Each `AirPlaySession` already connects to a distinct audio stream. Audio routing per tile means routing that tile's `AudioSampleReady` samples to a different Windows output device.

**Implementation**: `AudioPipeline` (in `SoulScreen.Media`) already has `AudioDevices` enumeration. Per-tile audio route: a `TileHost`-specific `AudioOutputDevice` setting. Default is `AppSettings.OutputDevice` (global).

UI: dropdown in per-tile toolbar listing `AudioDevices.EnumerateDevices()`.

---

## 7. Per-Tile Approval / Blacklist

Existing `AppSettings.AllowedPhones` / `BlockedPhones` is evaluated by phone name. **Change**: evaluate against the specific `SourceDeviceInfo` for each tile's session.

- `AllowedPhones` / `BlockedPhones` → `MultiSourceRouter.ApprovePhone(tileIndex, deviceName)` → returns `ApprovalResult.Allow | Blocked | PendingApproval`
- Approval prompt shows: `"Kasra's iPhone 17 Pro (tile 2) — allow?"`
- Blocked tile shows a `BlockedState` UI (name + "Blocked" label, no video surface)

---

## 8. Failure Mode: One Tile Crashes

If an `AirPlaySession` errors:
1. `SessionEnded` fires on that session's handler.
2. `MultiSourceRouter` removes the source from its list, fires `SourcesChanged`.
3. `TileHost` for that tile shows a "Disconnected" state.
4. Other tiles continue unaffected.
5. Grid relayouts automatically (sources count changed).

No shared state is corrupted. The `RtspServer` connection for that phone closes cleanly.

---

## 9. Session Summary Aggregation

```
"Mirrored 3 of 4 tiles for 47 m 12 s; one tile dropped at 14:32 (network); 2 screenshots, 1 recording"
```

`MultiSourceRouter.SessionSummary` aggregates:
- Total duration (earliest start → latest end)
- Dropped tiles (count + last-seen time + reason if known)
- Per-tile screenshot/recording counts

---

## 10. Grid Layout Sketches

```
1 tile                   2 tiles (wide window)     2 tiles (tall window)
┌───────────────────┐    ┌──────────┬──────────┐   ┌────────┬────────┐
│                   │    │  Tile 0  │  Tile 1  │   │ Tile 0 │ Tile 1 │
│      Tile 0       │    │          │          │   │        │        │
│                   │    └──────────┴──────────┘   └────────┴────────┘
└───────────────────┘

3 tiles ("presenter")          4 tiles
┌──────────────────┬────────┐  ┌────────┬────────┐
│                  │ Tile 1 │  │ Tile 0 │ Tile 1 │
│     Tile 0       ├────────┤  ├────────┼────────┤
│   (2/3 width)    │ Tile 2 │  │ Tile 2 │ Tile 3 │
└──────────────────┴────────┘  └────────┴────────┘
```

---

## 11. Manual Test Checklist Summary

| # | Scenario | Key assertions |
|---|---|---|
| 1 | 1 phone (regression) | No UI change; single tile full-screen |
| 2 | 2 phones, same second | Both appear in grid; both independent |
| 3 | 3 phones | Presenter layout correct |
| 4 | 4 phones | 2×2 grid; each tile has name/model/latency |
| 5 | Drop one mid-session | Others keep going; dropped tile shows Disconnected |
| 6 | Different audio device per tile | Each tile's audio routes to its selected device |
| 7 | Blocked phone | Blocked tile shows Blocked state, no pixels |
| 8 | Mini-player in grid mode | Grid preserved; `Ctrl+Shift+M` shrinks window |
| 9 | Record all 4 | 4 MP4s in `Pictures\SoulScreen` |
| 10 | End all sessions | Summary shows aggregated stats |

Full checklist in: `artifacts/testbuild/multi-device-manual-YYYYMMDD.md`
