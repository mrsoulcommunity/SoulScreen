# SoulScreen — run doc

## Preview tab (this worktree)

SoulScreen is a **WPF desktop app** — there is no web dev server and no port. The Preview
tab shows a **static, interactive HTML mock** of the WPF shell instead:

- Page: `.freebuff/preview.html` (absolute: `D:\Project\App-SoulScreen\.freebuff\preview.html`)
- Mode: standalone `htmlPath` registration — **no process, no port, no dependencies**.
- It is a faithful mock of `src/SoulScreen.App/MainWindow.xaml` + `Theme.xaml` (same
  palette, layout, volume slider, settings overlay, log panel, status bar). Buttons work;
  it is a UI mock, not the receiver.

To change what the preview shows, edit `.freebuff/preview.html` and reload the tab.

## Running the real app (Windows 11, .NET 8, x64)

Reproduce the artifacts a fresh checkout needs:

```powershell
# 1. Build the managed code
dotnet build

# 2. FairPlay helper (gitignored; downloads GPLv3 sources and compiles with MinGW gcc)
pwsh tools/build-fairplay.ps1        # produces native/soulscreen_fairplay.dll

# 3. FFmpeg libraries (gitignored)
pwsh tools/fetch-ffmpeg.ps1          # produces native/ffmpeg/*.dll

# 4. Firewall rules for the mDNS/RTSP ports (elevated)
pwsh tools/firewall.ps1
```

Run it:

```powershell
# WPF shell (the window the preview mocks)
dotnet run --project src/SoulScreen.App

# or headless receiver
dotnet run --project src/SoulScreen.Cli -- serve --dump capture
```

A self-contained published build (stages both native pieces beside the exe):

```powershell
pwsh tools/publish.ps1               # -> dist/SoulScreen/SoulScreen.App.exe
```

Verify everything with `dotnet test` (57 tests) and
`dotnet run --project src/SoulScreen.Cli -- fairplay` for the native helper self-test.
