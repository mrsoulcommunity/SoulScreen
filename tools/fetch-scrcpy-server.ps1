<#
.SYNOPSIS
    Downloads the scrcpy server, which SoulScreen's Android transport pushes to the phone
    and runs to get audio and rotation-aware video - the two things Android's own
    "screenrecord" cannot do.

.DESCRIPTION
    Without this file, SoulScreen's Android source falls back to "adb exec-out screenrecord
    --output-format=h264" - video only, and a fixed capture size that letterboxes instead of
    following a rotation. With it, SoulScreen instead pushes this server to
    /data/local/tmp/scrcpy-server.jar and runs it over adb (the same thing the scrcpy
    desktop app does), which adds audio (raw PCM) and re-announces its capture session - new
    dimensions, fresh SPS/PPS - whenever the phone rotates, all inside the same connection.

    This is Genymobile's official, signed release asset (Apache-2.0), fetched by exact
    version and verified against its published SHA-256 rather than trusted blindly, since it
    is bytecode that runs on the phone as the shell user. SoulScreen's client is written
    against protocol version 4.1 specifically - a newer or older server will refuse to run
    (scrcpy enforces an exact client/server version match by design) - so this always fetches
    that pinned version rather than "latest".

    Saved to native/scrcpy/scrcpy-server.jar. That directory is gitignored; nothing here is
    redistributed with SoulScreen's own source.

.PARAMETER Force
    Re-download even if the server is already present.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File tools\fetch-scrcpy-server.ps1
#>
[CmdletBinding()]
param([switch]$Force)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# Pinned to the protocol version SoulScreen.Android's ScrcpySession speaks. Bumping this
# without updating the client's protocol assumptions will make the server refuse to start
# (scrcpy checks the client version string it is passed against its own build) or, worse,
# speak a wire format the client was not written against.
$scrcpyVersion = '4.1'
$assetUrl = "https://github.com/Genymobile/scrcpy/releases/download/v$scrcpyVersion/scrcpy-server-v$scrcpyVersion"
$expectedSha256 = 'deacb991ed2509715160ffdc7907e47b4160eb30d1566217e9047fd5b8850cae'

$repoRoot = Split-Path -Parent $PSScriptRoot
$targetDir = Join-Path $repoRoot 'native/scrcpy'
$targetFile = Join-Path $targetDir 'scrcpy-server.jar'

Write-Host ''
Write-Host 'SoulScreen scrcpy server runtime' -ForegroundColor Cyan
Write-Host '--------------------------------'

if ((Test-Path $targetFile) -and -not $Force) {
    $hash = (Get-FileHash -Path $targetFile -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($hash -eq $expectedSha256) {
        Write-Host "Already present and verified: $targetFile"
        Write-Host 'Use -Force to re-download.'
        Write-Host ''
        exit 0
    }
    Write-Host 'Existing file does not match the expected checksum; re-downloading.' -ForegroundColor DarkYellow
}

$tempFile = Join-Path ([IO.Path]::GetTempPath()) "scrcpy-server-v$scrcpyVersion"

function Get-ServerJar {
    param([string]$Url, [string]$Destination)

    $attempts = 5
    for ($attempt = 1; $attempt -le $attempts; $attempt++) {
        Write-Host "  attempt $attempt of $attempts..."
        if (Test-Path $Destination) { Remove-Item -Force $Destination }

        $previousProgress = $ProgressPreference
        $ProgressPreference = 'SilentlyContinue'
        try {
            Invoke-WebRequest -Uri $Url -OutFile $Destination -UseBasicParsing -TimeoutSec 120
        } catch {
            Write-Host "    $($_.Exception.Message)" -ForegroundColor DarkYellow
        } finally {
            $ProgressPreference = $previousProgress
        }

        if (Test-Path $Destination) {
            $hash = (Get-FileHash -Path $Destination -Algorithm SHA256).Hash.ToLowerInvariant()
            if ($hash -eq $expectedSha256) { return }
            Write-Host "    checksum mismatch (got $hash)" -ForegroundColor DarkYellow
        }

        if ($attempt -lt $attempts) { Start-Sleep -Seconds (2 * $attempt) }
    }

    throw "Could not download a verified copy of $Url after $attempts attempts."
}

Write-Host 'downloading scrcpy-server (about 0.7 MB)...'
Get-ServerJar -Url $assetUrl -Destination $tempFile

New-Item -ItemType Directory -Force -Path $targetDir | Out-Null
Copy-Item -Path $tempFile -Destination $targetFile -Force
Remove-Item -Force $tempFile

Write-Host "verified and placed: $targetFile" -ForegroundColor Green
Write-Host ''
