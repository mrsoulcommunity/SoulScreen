<#
.SYNOPSIS
    Builds a runnable SoulScreen into dist/.

.DESCRIPTION
    Publishes the WPF app and stages the two native pieces beside it: the FairPlay helper,
    without which wireless mirroring stops at the handshake, and the FFmpeg libraries,
    without which nothing is decoded. Both are produced by the other scripts in this folder
    and are checked for here rather than silently omitted - a build that looks complete and
    then fails on the phone is the worst outcome.

.PARAMETER SelfContained
    Bundle the .NET runtime, so the output runs on a PC with nothing installed. Adds
    roughly 150 MB.

.PARAMETER Output
    Where to publish. Defaults to dist/SoulScreen.

.EXAMPLE
    pwsh tools/publish.ps1
    pwsh tools/publish.ps1 -SelfContained
#>
[CmdletBinding()]
param(
    [switch]$SelfContained,
    [string]$Output
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
$outputDir = if ($Output) { $Output } else { Join-Path $repoRoot 'dist/SoulScreen' }
$project = Join-Path $repoRoot 'src/SoulScreen.App/SoulScreen.App.csproj'

Write-Host ''
Write-Host 'SoulScreen publish' -ForegroundColor Cyan
Write-Host '------------------'

# ------------------------------------------------------ check the native pieces

$fairplay = Join-Path $repoRoot 'native/soulscreen_fairplay.dll'
$ffmpegDir = Join-Path $repoRoot 'native/ffmpeg'
$missing = @()

if (-not (Test-Path $fairplay)) { $missing += 'FairPlay helper  -  pwsh tools/build-fairplay.ps1' }
if (-not (Test-Path (Join-Path $ffmpegDir 'avcodec-*.dll'))) { $missing += 'FFmpeg libraries -  pwsh tools/fetch-ffmpeg.ps1' }

if ($missing.Count -gt 0) {
    Write-Host ''
    Write-Host 'Missing native components:' -ForegroundColor Yellow
    foreach ($item in $missing) { Write-Host "  $item" }
    Write-Host ''
    Write-Host 'Publish anyway? The app will start but will not mirror. (y/N) ' -NoNewline
    if ((Read-Host) -notmatch '^[Yy]') { exit 1 }
}

# --------------------------------------------------------------------- publish

if (Test-Path $outputDir) { Remove-Item -Recurse -Force $outputDir }

$publishArgs = @(
    'publish', $project
    '-c', 'Release'
    '-r', 'win-x64'
    '-o', $outputDir
    '--nologo'
    "-p:SelfContained=$($SelfContained.IsPresent.ToString().ToLower())"
    '-p:DebugType=none'
    # Trimming is off deliberately: the app reaches FFmpeg and the FairPlay helper through
    # P/Invoke and reflection-driven XAML, both of which trimming breaks in ways that only
    # show up at runtime.
    '-p:PublishTrimmed=false'
)

Write-Host ''
Write-Host "publishing $(if ($SelfContained) { 'self-contained' } else { 'framework-dependent' })..."
& dotnet @publishArgs | Select-Object -Last 3
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

# --------------------------------------------------------------- stage natives

if (Test-Path $fairplay) {
    Copy-Item $fairplay -Destination $outputDir -Force
    Write-Host '  staged soulscreen_fairplay.dll'
}

if (Test-Path $ffmpegDir) {
    $ffmpegOut = Join-Path $outputDir 'ffmpeg'
    New-Item -ItemType Directory -Force -Path $ffmpegOut | Out-Null
    Copy-Item (Join-Path $ffmpegDir '*.dll') -Destination $ffmpegOut -Force
    $count = (Get-ChildItem $ffmpegOut -Filter *.dll).Count
    Write-Host "  staged $count FFmpeg DLL(s) into ffmpeg\"
}

# ------------------------------------------------------------------- summarise

$sizeMb = [math]::Round((Get-ChildItem $outputDir -Recurse -File | Measure-Object -Property Length -Sum).Sum / 1MB, 1)
$exe = Join-Path $outputDir 'SoulScreen.App.exe'

Write-Host ''
Write-Host "published $sizeMb MB to $outputDir" -ForegroundColor Green
Write-Host "  run: $exe"
if (-not $SelfContained) {
    Write-Host '  needs the .NET 8 Desktop Runtime:  winget install Microsoft.DotNet.DesktopRuntime.8'
}
Write-Host ''
Write-Host 'On a new PC, allow it through Windows Firewall the first time it asks,'
Write-Host 'or run tools/firewall.ps1 from an elevated prompt.'
Write-Host ''
