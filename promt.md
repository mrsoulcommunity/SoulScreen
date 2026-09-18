# SoulScreen — Feature Implementation Prompts

Three production-ready prompts for AI coding agents (Claude Code, OpenCode, Codex, …).
Each prompt is **self-contained** and **grounded in the current codebase** — verified facts
about existing files, types, and behavior are inline so the agent does not have to re-discover
them.

> **How to use**
> 1. Pick one prompt.
> 2. Open your coding agent inside this repository (`D:\Project\App-SoulScreen`).
> 3. Paste the prompt verbatim, including the **Context** block.
> 4. The agent must produce the **Deliverables** in order, then verify against the
>    **Definition of done**.
> 5. Stop the agent the moment it declares done — re-run the manual checklist yourself.

---

## Table of contents

1. [Feature 1 — Multi-Device Grid Mirroring](#feature-1--multi-device-grid-mirroring)
2. [Feature 2 — Persian (Shamsi) + Gregorian Timestamps](#feature-2--persian-shamsi--gregorian-timestamps)
3. [Feature 3 — Audience Window (second display for the room)](#feature-3--audience-window-second-display-for-the-room)

---

## Feature 1 — Multi-Device Grid Mirroring

### Goal

Allow **2, 3, or 4 iPhones** to mirror simultaneously into a grid (1+1, 2×1, 2×2) in the same
window. Per-tile controls (mute / pause / screenshot), per-tile identity (name, model,
latency, recording dot), and per-tile recording. Designed for QA, family sharing, live
demonstrations, and educational sessions.

### Context (project ground truth — verified)

You are working on **SoulScreen** — an iPhone-to-Windows 11 mirroring receiver
(.NET 8 / WPF, C#). Source root: `D:\Project\App-SoulScreen`. The following facts
were verified directly from the repository before this prompt was written. Do not
re-derive them — start from them.

| Fact | Verified location |
|---|---|
| Receive path lives behind `IMirrorSource` | `src/SoulScreen.Core/Sources/IMirrorSource.cs` — interface exposes `event EventHandler<MediaSample> VideoSampleReady`, `AudioSampleReady`, `Device`, `State`. |
| Concrete implementation | `src/SoulScreen.AirPlay/AirPlayReceiver.cs` (`public sealed class AirPlayReceiver : IMirrorSource`). |
| One session today | `MainWindow.xaml.cs:40` — `IMirrorSource? ActiveSource => _receiver ?? _demo;` |
| Single video surface | `MainWindow.xaml` has exactly one `VideoHost` element bound to `ActiveSource`. |
| mDNS advertisement | Single instance, single port (Settings → AirPlay). |
| Pairing / FairPlay | Per-session state already exists in `AirPlaySession`, `FairPlaySession`, `PairVerifySession`. |
| Recording pipeline | `src/SoulScreen.Media/` (H.264 remux, AAC re-encode). |
| Smoothness / buffer settings | Per-receiver, applied in the existing renderer; see references in `MainWindow.Metrics.cs`. |
| Approval / Allowed / Blocked phones | `AppSettings.AllowedPhones` / `BlockedPhones`, evaluated by phone name. |
| Mini player | `src/SoulScreen.App/MainWindow.MiniPlayer.cs`. |
| Focus mode | `src/SoulScreen.App/MainWindow.Focus.cs`. |
| Session summary | `src/SoulScreen.App/MainWindow.Reconnect.cs` (`EndSessionBookkeeping`). |
| Test suite | `dotnet test` — 240 passing tests at the time this prompt was written. |
| Verified device | iPhone 17 Pro on iOS 26 (per README). |

### Functional requirements

1. **Discoverable from iOS — no client change.** When a second iPhone opens
   *Control Center → Screen Mirroring*, it must see this PC exactly as today.
   On the PC side, **do not** spawn four AirPlay receivers on four ports — that
   breaks iOS discovery (the phone only shows one entry per port/identity).
   Use **one** mDNS instance and either:
   - a TCP server that multiplexes RTSP sessions by `Session:` header, or
   - one mDNS record per session with the same port.
   Verify Apple's behavior empirically against the iPhone 17 Pro / iOS 26 setup
   and record exactly what you observe in `docs/multi-device.md`.

2. **Concurrent sessions — up to 4.** Each session owns its own:
   pairing, FairPlay, H.264 decoder, audio output endpoint, recording pipeline.
   A drop on one tile must not affect the others.

3. **Auto-layout by count.**
   - 1 → full picture (regression-safe).
   - 2 → side-by-side (1×2 or 2×1, depending on window aspect).
   - 3 → one large + two small (the "presenter" layout).
   - 4 → 2×2.
   Manual override: `Ctrl+1..4` to pick a layout, double-click a tile to focus it
   (return with `Esc` or `Ctrl+H`), drag a tile to swap cells (snap).
   Tile sizing must reuse the existing fit/fill/stretch logic — search for `Fit`,
   `Fill`, `Stretch` in `MainWindow.xaml.cs` and reuse it, do not reimplement.

4. **Per-tile chrome.** Each tile shows:
   - Phone name (truncated, full name on hover).
   - Model identifier (e.g. `iPhone 17 Pro`).
   - Latency / signal indicator.
   - Recording dot if this tile is being recorded.
   - Mic-mute icon (mirrors `Ctrl+M` for that tile only).
   - Pause overlay (mirrors `Space` for that tile only).
   - Hover reveals a per-tile toolbar: Screenshot, Record, Mute, Swap to full.
   `Ctrl+H` (Focus mode) hides chrome for all tiles the same way it does for one
   tile today.

5. **Per-tile recording.** `Ctrl+R` records **all** tiles. Choose **one** of these
   storage strategies and document it in the README:
   - **A.** One MP4 with N video tracks (preferred for sync + storage).
   - **B.** N separate MP4s in `Pictures\SoulScreen`, named with the device name
     and the timestamp formatter from Feature 2.
   Each filename is parseable back to a `DateTime`. Add a comment block in the
   recorder describing why you picked A or B.

6. **Per-tile audio routing.** Each tile's audio can be sent to a different
   Windows output device. Pick from a dropdown on the tile toolbar. The global
   `Output device` setting in `AppSettings` remains the default for all tiles.
   Honor the existing `Smoothness` / `AudioBufferMs` per tile.

7. **Per-tile approval / blacklist.** The existing "Ask before an iPhone mirrors"
   toggle and the Allowed/Blocked phone lists must apply per phone, not globally.
   The approval prompt identifies the tile (`Kasra's iPhone 17 Pro — allow?`).
   A blocked phone's tile stays in a `Blocked` state and never shows pixels.

8. **Mini player in grid mode.** `Ctrl+Shift+M` shrinks the window but **keeps
   the grid**. Add a setting `Mini player: focused tile only / keep grid`
   (default `keep grid`).

9. **Session summary aggregates all tiles.** Example:
   `Recorded 3 of 4 tiles; one tile dropped at 14:32 (network); total 47 m 12 s.`

10. **Zero regression in single-phone mode.** A user with one phone never sees
    any UI change. Performance budget: 4 tiles @ 60 fps on a mid-range i5 +
    Intel UHD must hold ≥ 55 fps per tile. Profile with the existing performance
    graph (`Ctrl+I`); record numbers in `docs/multi-device.md`.

### Architectural constraints (non-negotiable)

- **`IMirrorSource` is the abstraction.** All tile logic talks to `IMirrorSource`.
  Do not bypass it to reach `AirPlayReceiver` directly.
- **`VideoHost` is a single WPF element today.** Introduce a **`TileHost`**
  (`src/SoulScreen.App/Controls/TileHost.xaml(.cs)`) that owns its own video
  surface + tile chrome. The grid container in `MainWindow.xaml` is a `Grid` of
  `TileHost`s.
- Each `TileHost` subscribes to its own `IMirrorSource.VideoSampleReady` and
  renders to its own `WriteableBitmap` (or a shared GPU surface — pick and
  justify in the design doc).
- **mDNS + RTSP multiplex.** See requirement 1. Verify on hardware before coding.
- **No new third-party dependencies** unless explicitly approved; prefer
  `MediaFoundation` / `NAudio` (already used).
- **No drive-by refactors.** Keep the existing `MainWindow.*.cs` partial-class
  style. Match the project's C# conventions exactly (file-scoped namespaces,
  primary constructors where the project already uses them, brace style per
  `.editorconfig` if present).
- All new code must compile with `TreatWarningsAsErrors` if the project enables
  it (verify in `Directory.Build.props` / `.targets`).

### Deliverables (in this order)

1. **Design doc** — `docs/multi-device.md`, ≤ 300 lines. Cover:
   - mDNS + RTSP multiplex strategy with the verified iPhone 17 Pro / iOS 26
     observation.
   - Audio routing strategy.
   - Decoder per tile vs. shared; justify.
   - Memory budget per tile (RAM + GPU).
   - Sketches of the 3 grid layouts (ASCII is fine).
   - Failure mode for one tile crashing.
2. **Core changes**:
   - `IMirrorSource` **unchanged**.
   - New `MultiSourceRouter` at `src/SoulScreen.Core/Sources/MultiSourceRouter.cs`
     with `IReadOnlyList<IMirrorSource> Sources` + `CollectionChanged`.
   - New `TileHost` WPF control.
   - Grid container in `MainWindow.xaml`.
3. **AirPlay side**: extend `AirPlayReceiver` to accept multiple `AirPlaySession`s,
   each identified by a new `SessionId`. Pairing/FairPlay already per-session —
   wire N sessions.
4. **UI**: per-tile chrome, layout switching, mini-player behavior, settings
   additions (`Max tiles`, `Default audio per tile`), command-palette entries
   (`Layout: 2-up`, `Layout: grid`, `Focus tile 1..4`, `Mute tile N`,
   `Screenshot tile N`).
5. **Recording**: extend the existing recorder (`src/SoulScreen.Media/`) to
   accept a tile id; produce one MP4 with N tracks OR N MP4s — pick one, document.
6. **Approval / privacy**: verify `AllowedPhones` / `BlockedPhones` is keyed by
   phone name and applies per session.
7. **Tests** in `tests/`:
   - `MultiSourceRouterTests`: add/remove; one source throws → others keep going.
   - `GridLayoutTests`: layout math for all four counts.
   - `RecordingFilenameTests`: round-trip with the formatter from Feature 2.
   Existing 240 tests must keep passing.
8. **README updates**: new **Multi-device mirroring** section, new shortcuts,
   one screenshot of 2×2 grid saved under `dist/`.
9. **Manual test checklist** — Markdown, ≤ 80 lines, for:
   1 phone (regression) · 2 phones same second · 3 phones · 4 phones · drop
   one mid-session · different audio device per tile · blocked phone ·
   mini-player behavior · record all 4 · end all sessions.

### Out of scope

- Android mirroring (different protocol).
- USB transport (deferred per README).
- Cloud relay / non-LAN (separate prompt).
- Any change to the iOS client.

### Definition of done

- `dotnet build` clean, **no new warnings**.
- `dotnet test` — all 240 existing tests + new tests pass.
- Manual checklist executed end-to-end against the iPhone 17 Pro / iOS 26
  setup; results recorded in
  `artifacts/testbuild/multi-device-manual-YYYYMMDD.md`.
- Performance: 4-tile CPU and RAM recorded with the existing perf graph;
  numbers in the design doc.
- Activity log (`Ctrl+L`) shows no warnings on a clean session.

---

## Feature 2 — Persian (Shamsi) + Gregorian Timestamps

### Goal

Display and persist timestamps in **both Shamsi (Persian) and Gregorian**,
switchable by user and locale. Every surface that currently shows a time or
date — capture filenames, in-app overlay timestamps, the recording badge,
session summary card, gallery item subtitles, clipboard filenames, the
activity log entries — uses the new formatter. Shamsi is **primary** when the
system locale or user setting says so; Gregorian is always available as a
secondary display (e.g. `1403/06/24 — 2024/09/15`).

### Context (project ground truth — verified)

You are working on **SoulScreen** — an iPhone-to-Windows 11 mirroring receiver
(.NET 8 / WPF, C#). Source root: `D:\Project\App-SoulScreen`. The following
facts were verified directly from the repository before this prompt was
written. Do not re-derive them — start from them.

| Fact | Verified location |
|---|---|
| Captures written with `DateTime.Now` | Grep for `SoulScreen-`, `ToString("yyyy-MM-dd`, `Path.Combine(captureFolder` across `src/SoulScreen.App/MainWindow.Clips.cs`, `MainWindow.Captures.cs`, `src/SoulScreen.Media/`. |
| Wall-clock UTC embedded in samples | `src/SoulScreen.Core/Sources/MediaSample.cs` (or equivalent) — search `DateTimeKind.Utc`, `Timestamp`. |
| Settings are JSON | `AppSettings.json`, edited by `src/SoulScreen.App/MainWindow.Settings.cs`. |
| Localization-relevant switches | Already live in Settings (Theme, Animations, Display). |
| Same-second collision suffix | Verified to exist per README: "Two taken in the same second no longer overwrite each other." |
| No Persian calendar code today | Verified by absence across `src/`. |
| Test suite | `dotnet test` — 240 passing tests at the time this prompt was written. |

### Functional requirements

1. **Formatter** at `src/SoulScreen.Core/Time/PersianDateTime.cs`:
   - `string ToStringShamsi(DateTime utc, PersianDateFormat fmt = Short)`
     — Short `1403/06/24`, Long `1403 شهریور 24`. Output is parseable by
     `DateTime.Parse` after concatenation with a Gregorian anchor (document the
     anchor in the doc-comment).
   - `string ToStringShamsiWithGregorian(DateTime utc)` → `1403/06/24 (2024-09-15)`.
   - `string FormatForFilename(DateTime utc, string deviceName = "")`
     → `SoulScreen-<shamsi-yyyyMMdd-HHmmss>-<deviceName-sanitized>` and the
     Gregorian variant when Shamsi is off. Sanitization: strip path-invalid
     chars, collapse whitespace to `-`, max 32 chars.
   - Use `System.Globalization.PersianCalendar` (built into .NET, no NuGet).
   - Wrap it: `PersianCalendar` throws on dates before year 62 — guard with
     `DateTime` clamps and tests.

2. **Locale resolution** (priority order):
   1. `AppSettings.Timestamps.UseShamsi` explicit bool (default `false`).
   2. Else `CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "fa"`
      → Shamsi on.
   3. Else off (Gregorian only).

3. **Settings UI** — new section **Settings → Regional → Timestamps**:
   - `Use Persian (Shamsi) dates` toggle.
   - `Show Gregorian next to Shamsi` toggle.
   - `First day of week` dropdown (Saturday / Sunday / Monday) — applies to any
     calendar widget added in this feature; default Saturday.
   - Preview row: `Today: 1403/06/24 — 2024-09-15`.

4. **Capture filenames** — patch every site found by the grep above:
   - Screenshot filename (search `SoulScreen-` + `screenshot` in
     `MainWindow.Captures.cs`).
   - Recording filename (search `recording` / `.mp4` in
     `src/SoulScreen.Media/`).
   - Clip filename (search `clip` / `gif` / `webm` in `MainWindow.Clips.cs`).
   - Clipboard paste name (search `Clipboard` in `MainWindow.*.cs`).
   - **No regression in collision handling** — keep the existing collision
     suffix.
   - **Format must be parseable back** to `DateTime`. If the existing code
     parses, update the parser alongside the formatter; document the parse
     contract in the doc-comment.

5. **Gallery subtitles** (`MainWindow.Captures.cs` tile metadata):
   - `<shamsi-short> — <HH:mm:ss>` and, if the toggle is on, `(2024-09-15
     14:32:08)` underneath.
   - **Sort order still uses the actual `DateTime`, not the display string.**

6. **Session summary card** on disconnect (search `SessionSummary` in
   `MainWindow.Reconnect.cs`):
   - Header shows the session range in Shamsi when on.
   - Body shows duration.
   - If the toggle is on, append Gregorian in parentheses.

7. **Recording badge** over the picture (search `RecordingBadge` /
   `RecordButton` in `MainWindow.ControlBar.cs`):
   - No change to the running counter itself.
   - The file name label in the badge uses the formatter.

8. **Activity log** entries (`Ctrl+L`): each row's timestamp uses the
   formatter.

9. **Zero regression for users who don't speak Persian.** Default behavior is
   identical to today. Switching on Shamsi must not affect any existing
   capture (filenames still sortable; gallery still works; tests still pass).

10. **Tests** in `tests/` — minimum 12 cases:
    - Known Gregorian → Shamsi mappings:
      - `2024-03-20` → `1403/01/01` (Nowruz)
      - `2025-03-21` → `1404/01/01` (Nowruz, leap-year adjacent)
      - `2024-09-15` → `1403/06/25` (sanity-check with `PersianCalendar`)
    - Edge: leap Shamsi year 1403.
    - Edge: dates before PersianCalendar year 62 → fallback to Gregorian with a
      `[?]` marker in the formatted string; unit test for that fallback.
    - Filename parser round-trip: format → `File.Exists` → read mtime → parse
      back to same `DateTime` within ±1 s, for both Gregorian and Shamsi
      formats.
    - Locale resolution: explicit setting overrides UI culture;
      `fa-IR` triggers Shamsi; `en-US` does not.

### Architectural constraints

- **One formatter, used everywhere.** No ad-hoc `ToString("yyyy-MM-dd")` left in
  the codebase after this change. Add a **Roslyn analyzer** *or* a **CI grep
  step** that fails the build if a new capture-filename site bypasses the
  helper.
- **No new NuGet dependencies.** `System.Globalization.PersianCalendar` is
  sufficient.
- **Persian glyphs only when Shamsi is shown.** English glyphs otherwise. Don't
  leak Persian month names into the English UI.
- **Accessibility.** Every date string exposed through automation
  (`AutomationProperties.Name`) must include both calendars when both are
  configured, so a screen reader reads both.
- **No timezone bugs.** Internally always store UTC; convert at the formatter
  boundary. Verify the recorder writes UTC (`DateTimeKind.Utc`).
- **No drive-by refactors.** Match the project's C# style exactly.

### Deliverables (in this order)

1. `src/SoulScreen.Core/Time/PersianDateTime.cs` — the formatter + parser.
2. Settings additions in `src/SoulScreen.App/MainWindow.Settings.cs` and the
   settings XAML in `MainWindow.xaml` (or the settings panel partial, if one
   exists).
3. Patches to **every** capture filename site found by grep — list each
   `path:line` in the PR description.
4. Patches to gallery, summary, recording badge, activity log.
5. Tests — ≥ 12 cases, including the three boundary dates above.
6. README update under a new **Regional** subsection with:
   - One screenshot of the Settings panel.
   - One screenshot of a gallery tile showing both calendars.
7. CI grep step (PowerShell — runs in `RUN.bat` or the existing build script):
   fails if `ToString("yyyy")` or `DateTime.Now.ToString(` appears in `src/`
   outside `src/SoulScreen.Core/Time/`. The failure message must point to the
   offending file and line.

### Out of scope

- Hijri calendar, lunar calendars.
- RTL UI overhaul (separate prompt).
- Translating UI strings to Persian.
- Any change to the recorder's internal timestamp format — only its on-disk
  filename and on-screen labels change.

### Definition of done

- `dotnet build` and `dotnet test` clean; all 240 existing tests + new tests
  pass.
- Switching the toggle re-renders gallery, summary, and log **without restart**.
- A user who never touches the toggle sees zero visual change vs. today
  (verified by screenshot diff against the current `dist/` build).
- Filename parser round-trip test passes for Gregorian and Shamsi formats.
- CI grep step is wired into `RUN.bat` (or the existing build entry point) and
  produces a clear failure message with `path:line` when triggered.

---

## Feature 3 — Audience Window (second display for the room)

### Goal

A **second, chrome-free window** that mirrors the live picture onto a projector or a
second monitor while every control — markup, recording, fit, volume — stays on the
laptop in front of the teacher. One keystroke (`Ctrl+Shift+D`) opens it; closing it
costs nothing while it is shut. Built for classrooms, demos and meetings: the room
looks at the wall, the presenter keeps the tools.

### Context (project ground truth — verified)

You are working on **SoulScreen** — an iPhone-to-Windows 11 mirroring receiver
(.NET 8 / WPF, C#). Source root: `D:\Project\App-SoulScreen`. The following facts
were verified directly from the repository before this prompt was written. Do not
re-derive them — start from them.

| Fact | Verified location |
|---|---|
| Receive path lives behind `IMirrorSource` | `src/SoulScreen.Core/Sources/IMirrorSource.cs` |
| Decoded frames are raised **once**, with ownership transferred | `src/SoulScreen.Media/VideoPipeline.cs` — the `FrameDecoded` event; its doc comment states "The handler takes ownership of the frame and must dispose it when done" |
| The one consumer today | `src/SoulScreen.App/MainWindow.xaml.cs:608` — `OnFrameDecoded(object? sender, DecodedVideoFrame frame) => Video.Present(frame);` |
| Frame buffers come from `ArrayPool`, `Rent` is **internal to SoulScreen.Media**, `Dispose` is idempotent (`Interlocked.Exchange`) | `src/SoulScreen.Media/DecodedVideoFrame.cs` |
| The picture surface: one `WriteableBitmap` per geometry, `TryLock(3 ms)`, frames queued by an internal `FramePacer`, `Present` safe from any thread | `src/SoulScreen.App/Rendering/VideoSurface.cs` (allocate at `:178`, `Snapshot()` frozen copy at `:326`) |
| A second independent consumer of the same pipeline pattern **already exists and works** | `src/SoulScreen.App/Controls/TileHost.xaml.cs` — own `VideoSurface`, own pacing, fixed `PresentationDelay = TimeSpan.FromMilliseconds(50)` at `:61`, `Video.Present(frame)` at `:182` |
| Monitor enumeration and placement are done and tested | `src/SoulScreen.App/DisplayService.cs` (`ListChoices(Window)`, `VirtualDesktop`, `MoveTo`) + `src/SoulScreen.App/Logic/DisplayLayout.cs` (`PlacementFor`, `Resolve`, `IsOnDisplay`) |
| `Ctrl+Shift` is taken by **C** (copy shot), **M** (mini player), **P** (presentation mode), **R** (rotate); `Ctrl+Shift+D` is free | `src/SoulScreen.App/MainWindow.Chrome.cs:518-523` |
| The name **"Presentation mode" is taken** — it is fullscreen+focus (`Ctrl+Shift+P`) | `src/SoulScreen.App/MainWindow.Focus.cs:112` `TogglePresentationMode`; palette entry `MainWindow.Commands.cs:301` |
| Command palette entries are `yield return new(title, group, glyph, shortcut, action, keywords)` | `src/SoulScreen.App/MainWindow.Commands.cs` |
| Display choice is a string setting: `"current"`, `"primary"`, or an index as text | `src/SoulScreen.App/AppSettings.cs` — `TargetDisplay` beside `WindowLeft` (`:446-453`) |
| Settings import is an **explicit whitelist** — anything not listed in `ApplyImported` stays machine-local | `src/SoulScreen.App/Logic/SettingsTransfer.cs` |
| Markup is an `InkCanvas` | `src/SoulScreen.App/MainWindow.Markup.cs` (`EditingMode` at `:330-332`, stroke collection at `:423`); declared `MainWindow.xaml:308` |
| Multi-device grid has its **own** pipelines per tile | `src/SoulScreen.App/MainWindow.MultiDevice.cs` + `TileHost` |
| The demo source emits through the same `IMirrorSource` — everything here is testable with no phone | `src/SoulScreen.Media/DemoSource.cs:99` |
| Session teardown clears the main surface | `src/SoulScreen.App/MainWindow.xaml.cs:869` `Video.Clear()` |
| Test suite | `dotnet test` — 406 passing at the time this prompt was written (README badge) |
| `LangVersion` 12, nullable on, no new packages | `Directory.Build.props` |

### Strategy — one copy, not a second decoder

The pipeline hands each decoded frame to **one** owner, and the owner disposes it.
So a second window cannot simply subscribe a second decoder-facing handler and take
the same frame. Choose the strategy below; the alternatives were considered and
rejected:

| Option | Verdict |
|---|---|
| **A. A frame tap that copies (chosen)** | A small class in `SoulScreen.Media` subscribes to `FrameDecoded`, memcpy's the pixels into a fresh `ArrayPool` buffer **synchronously on the decode thread**, and raises the copy for the audience window's own `VideoSurface`. ~1 ms per 1080p frame, only while the window is open. No protocol, decoder or pipeline changes. |
| B. Second `VideoPipeline` on the same `IMirrorSource` | Rejected: a second decode of every frame — double CPU, and two decoders can disagree mid-session. Also doubles the work `FrameDecoded`'s ownership rule forbids. |
| C. `VideoSurface.Snapshot()` per frame on the UI thread | Rejected: a frozen 8 MB `WriteableBitmap` copy on the UI thread sixty times a second (`VideoSurface.cs:326` is per-call) — exactly the cost the pipeline's own doc comment warns sank the decode thread once. |
| D. Shared D3D surface / `D3DImage` | Rejected for this prompt: native interop work and no precedent in the repo; revisit only if Option A's measured cost exceeds its budget. |

### Functional requirements

1. **Open, close, remember.** `Ctrl+Shift+D` toggles the audience window; the command
   palette lists **"Audience window"** (group *Window*, keywords "audience projector
   second display teaching class screen wall") with the same shortcut. The F1 list and
   Settings → Keyboard shortcuts gain the row. Window placement (Left/Top/Width/Height)
   is remembered in settings and restored; **openness is not** — a ghost window on the
   projector after a reboot is worse than one keystroke.

2. **What it shows.** The live picture of the active session, `Stretch.Uniform`, in a
   borderless window with no chrome of its own. Hovering reveals exactly two small
   buttons: fullscreen (`F11` also works) and close. `Esc` leaves fullscreen first;
   a second `Esc` does nothing else (the main window keeps its own Esc semantics).
   `ShowInTaskbar=false` — the main window stays the app's taskbar identity.

3. **Zero cost while closed.** No subscription, no copies, no allocations per frame.
   `FrameTap` exposes a copied-frames counter so a test (and the activity log) can
   prove zero while closed.

4. **The tap contract.** `src/SoulScreen.Media/FrameTap.cs`:
   - Subscribes to `VideoPipeline.FrameDecoded`; subscribes **before** the main
     window's existing handler and copies synchronously, so the source buffer is
     intact regardless of when the pacer disposes it.
   - The copy is a fresh `DecodedVideoFrame` via the internal `Rent` (same over-
     allocation behaviour as the existing path), `TimestampUs` preserved; `Dispose`
     of the inbound frame remains the existing consumer's job, untouched.
   - Hands the copy straight to its consumer (`FrameCopied`, raised on the decode
     thread, consumer takes ownership and must dispose). There is no second queue:
     the audience surface's own `FramePacer` cushion is the only buffering, and a
     slow projector surface drops frames there rather than back-pressuring decode.
   - `TotalCopyMicroseconds` and `FramesCopied` counters, reset per session.

5. **Independent pacing.** The audience surface runs its own `VideoSurface` with the
   fixed 50 ms delay `TileHost` uses — **not** `AppSettings.PresentationDelayFor`:
   that couples the main picture's delay to the audio reserve, and the audience
   window is silent. Latency measured on its surface starts at the copy; note this
   in a doc comment so nobody "fixes" the number twice.

6. **Pause is global across both windows.** `Space` (which sets `Video.IsFrozen` in
   the main window's pause path) freezes the audience surface too, in the same method.
   A presenter pausing to point at a still must see the same still on the wall.
   Markup drawing on a paused picture must appear on both.

7. **Display choice.** The settings card lists `DisplayService.ListChoices` entries
   ("1 (primary)", …). The pure decision — resolve the choice string, fall back to
   primary when the index no longer exists, null when the list is empty — lives in
   **`src/SoulScreen.App/Logic/AudienceDisplay.cs`**, WPF-free, in the house style of
   `Logic/DisplayLayout.cs`, and is what the window calls on open and on
   `SystemEvents.DisplaySettingsChanged`-style monitor changes. A monitor that is
   unplugged mid-show moves the window to the fallback with one toast and is not
   asked about again. Placement math itself is `DisplayLayout.PlacementFor`, using
   the **audience window's own DPI** (`ListChoices` takes the window it measures for —
   pass the audience window, not the main one).

8. **Session lifecycle.** Opening with no session shows the same idle placeholder the
   main window shows. A session ending does **not** close the audience window: it
   holds the last frame with a small "Session ended" chip, so the room never sees a
   desktop mid-lesson; closing it stays manual. A phone that drops and resumes inside
   the reconnect minute keeps the wall picture continuous. Closing the audience
   window never touches the session, the recording, or the main window.

9. **Markup mirroring.** The audience window hosts a read-only `InkCanvas`
   (`EditingMode=None`) that mirrors the main window's `MarkupCanvas.Strokes` via
   stroke-collection change events, cleared and erased together, so pen and
   highlighter reach the projector. The laser pointer's fade is reproduced as closely
   as the mirrored collection allows — if timed fade strokes cannot be mirrored
   faithfully, mirror them as ordinary strokes and say so in the README. A settings
   toggle **"Mirror markup to the audience window"** (default on) gates it.

10. **Machine-local by construction.** The audience display choice and placement are
    **not added** to `SettingsTransfer.ApplyImported` — the whitelist makes them local
    automatically, the same policy the window place already follows. A test pins this:
    importing settings must not move the audience window.

11. **Scope guards.** Single-device sessions only: in grid mode the command toasts
    "The audience window follows the main picture — not available in grid mode" and
    does nothing (grid tiles have their own pipelines). Works with the demo source —
    every logic path is exercisable with no phone. The audience window captures
    nothing of its own: screenshots and recordings keep coming from the main
    surface, and its statistics never enter the `Ctrl+I` overlay.

12. **Performance budget.** Copy ≤ 1.5 ms per 1080p frame on the decode thread; the
    audience surface ≥ 55 fps at 1080p60 on the reference hardware; the main window's
    presented-fps, read from its own `VideoSurface`, changes by no more than 2 fps
    with the window open. Extra RAM ~40-70 MB (cushion + bitmap). Record measured
    numbers in the manual checklist.

### Architectural constraints (non-negotiable)

- **`VideoPipeline.cs`, `SessionRecorder`, `IMirrorSource` and `VideoSurface` are not
  modified.** Everything new is additive: `FrameTap`, `Logic/AudienceDisplay.cs`,
  `AudienceWindow.xaml(.cs)`, a `MainWindow.Audience.cs` partial, settings fields.
- **Naming.** The feature is the **"Audience window"** everywhere a user can read it.
  "Presentation mode" is taken (`MainWindow.Focus.cs:112`) and must not be reused or
  overloaded.
- `FrameTap` lives in `SoulScreen.Media` because `DecodedVideoFrame.Rent` is internal
  to that assembly; it must stay free of WPF so it is testable headlessly.
- **No new third-party dependencies.** `System.Windows.Forms.Screen` via the existing
  `DisplayService`, `PersianCalendar`-style stdlib-only rules apply.
- Match the house C# style exactly: file-scoped namespaces, `sealed` where it fits,
  doc comments that say **why**, WPF-free `Logic/`. New code compiles warning-clean
  (`TreatWarningsAsErrors` is currently off — keep it that way, don't flip it).
- The audience window's theming follows the app theme (accent and light/dark), so the
  chip and hover buttons do not look bolted on.

### Deliverables (in this order)

1. `src/SoulScreen.Media/FrameTap.cs` + `tests/SoulScreen.Tests/FrameTapTests.cs`
   (≥ 6 tests): copy independence (mutate the source buffer after the tap; the copy
   holds the old pixels), dispose safety (disposing the inbound frame does not
   corrupt an undelivered copy), zero copies while unsubscribed, ownership passes to
   the consumer (the copy is disposed exactly once by the consumer), counters, and
   back-pressure (a consumer that never drains does not grow anything unboundedly).
2. `src/SoulScreen.App/Logic/AudienceDisplay.cs` + tests (≥ 4): valid index resolves;
   out-of-range index falls back to primary; `"primary"` and `"current"` behave as
   documented; empty display list yields null.
3. `src/SoulScreen.App/AudienceWindow.xaml(.cs)`: borderless window, own `VideoSurface`,
   mirrored `InkCanvas`, hover chrome, `F11`/`Esc`, session-end chip, DPI-correct
   placement through `DisplayService`/`DisplayLayout`.
4. `src/SoulScreen.App/MainWindow.Audience.cs` partial: tap lifecycle (subscribe on
   open, unsubscribe on close), open/close, hotkey, palette entry, grid-mode guard,
   session open/end/reconnect hooks, freeze-both-pauses wiring, monitor-change
   fallback with toast, activity-log lines ("audience window opened on display 2"…).
5. Settings: fields in `AppSettings.cs` beside the other window settings
   (`AudienceDisplay`, `AudienceWindowLeft/Top/Width/Height`), the card under
   **Settings → Window and system** (display dropdown, Open/Close, markup-mirror
   toggle), search-indexed like every other card, and **nothing added to
   `SettingsTransfer.ApplyImported`** (with the pinning test).
6. README: a short **Audience window** subsection under Usage, the `Ctrl+Shift+D` row
   in the shortcut table, and one line in the Status table. Manual test checklist
   (≤ 80 lines) saved at `artifacts/testbuild/audience-window-manual-checklist.md`:
   1-phone regression · open on display 2 · fullscreen/Esc · markup mirrored ·
   pause freezes both · session drop mid-show (chip, no desktop) · reconnect
   continuity · demo pattern with no phone · unplugged-monitor fallback · close
   during recording (recording unaffected) · fps numbers from `Ctrl+I` with the
   window open and closed.

### Out of scope

- Grid/multi-device audience views (tiles have their own pipelines).
- Audio on the audience window (the room hears the laptop, by design, via its own
  output device).
- More than one audience window; capturing **from** the audience window; virtual
  camera or RTMP (separate prompts if ever).
- Making the audience window always-on-top or resizable-chrome'd.

### Definition of done

- `dotnet build` clean with **no new warnings**; `dotnet test` — all existing tests
  (406 at the time of writing) plus the new ones pass.
- With the audience window closed: the tap is unsubscribed, `FramesCopied` stays at
  zero across a full session (tested), and the main window's fps is unchanged within
  measurement noise.
- With it open on the reference hardware: ≥ 55 fps on the audience surface at
  1080p60, copy cost within budget, numbers recorded in the manual checklist.
- Every behaviour above demonstrated with the **demo pattern and no phone attached**
  — the whole feature is verifiable in CI-like conditions except raw fps.
- Manual checklist executed against the iPhone 17 Pro / iOS 26 setup; results in
  `artifacts/testbuild/audience-window-manual-YYYYMMDD.md`.
- Activity log (`Ctrl+L`) shows no warnings on a clean open/serve/close cycle.

---

## General conventions for every prompt

- **No new dependencies** without an explicit `// Approved:` comment in the
  PR description.
- **Never** read, log, or commit secrets (`.env`, `appsettings.*.json` with
  real keys, etc.).
- **Never** `git commit` / `git push` unless the user asks.
- **Reference code as `path:line`**, never paste whole files.
- **Match the existing style**: file-scoped namespaces, `sealed` classes,
  primary constructors where the project uses them, brace style per
  `.editorconfig`.
- **Stop after ~3 failed attempts** on the same file and ask the user, rather
  than looping.
- **Verify, don't assume.** When the prompt says "search for X", the agent
  must run the search and report what it found before patching.