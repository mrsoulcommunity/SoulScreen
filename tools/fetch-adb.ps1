<#
.SYNOPSIS
    Downloads adb (Android Debug Bridge), which SoulScreen's Android transport drives to
    read an Android phone's screen over USB or Wi-Fi debugging.

.DESCRIPTION
    SoulScreen's Android source runs "adb exec-out screenrecord --output-format=h264 -" and
    reads the raw H.264 elementary stream straight off stdout. That needs adb.exe plus the
    two small DLLs it loads to talk to a USB device on Windows (AdbWinApi.dll,
    AdbWinUsbApi.dll) - none of it is bundled with the source.

    This fetches Google's official platform-tools package and keeps just those three files,
    in native/adb. That directory is gitignored; nothing here is redistributed with
    SoulScreen's own source.

.PARAMETER Force
    Re-download even if adb.exe is already present.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File tools\fetch-adb.ps1
#>
[CmdletBinding()]
param([switch]$Force)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$assetUrl = 'https://dl.google.com/android/repository/platform-tools-latest-windows.zip'

$repoRoot = Split-Path -Parent $PSScriptRoot
$targetDir = Join-Path $repoRoot 'native/adb'
$marker = Join-Path $targetDir 'adb.exe'

Write-Host ''
Write-Host 'SoulScreen adb runtime' -ForegroundColor Cyan
Write-Host '----------------------'

if ((Test-Path $marker) -and -not $Force) {
    Write-Host "Already present: $marker"
    Write-Host 'Use -Force to re-download.'
    Write-Host ''
    exit 0
}

$tempZip = Join-Path ([IO.Path]::GetTempPath()) 'platform-tools-latest-windows.zip'
$tempExtract = Join-Path ([IO.Path]::GetTempPath()) 'soulscreen-platform-tools'

# Google's CDN is generally reliable, but retry the way fetch-ffmpeg.ps1 does rather than
# fail the whole setup on one dropped connection.
function Get-Archive {
    param([string]$Url, [string]$Destination)

    $attempts = 5
    for ($attempt = 1; $attempt -le $attempts; $attempt++) {
        Write-Host "  attempt $attempt of $attempts..."
        if (Test-Path $Destination) { Remove-Item -Force $Destination }

        $previousProgress = $ProgressPreference
        $ProgressPreference = 'SilentlyContinue'
        try {
            Invoke-WebRequest -Uri $Url -OutFile $Destination -UseBasicParsing -TimeoutSec 300
        } catch {
            Write-Host "    $($_.Exception.Message)" -ForegroundColor DarkYellow
        } finally {
            $ProgressPreference = $previousProgress
        }

        if ((Test-Path $Destination) -and (Get-Item $Destination).Length -gt 1MB) {
            $header = [byte[]]::new(2)
            $stream = [IO.File]::OpenRead($Destination)
            try { $null = $stream.Read($header, 0, 2) } finally { $stream.Dispose() }
            if ($header[0] -eq 0x50 -and $header[1] -eq 0x4B) { return }
            Write-Host '    downloaded file is not a zip archive' -ForegroundColor DarkYellow
        }

        if ($attempt -lt $attempts) { Start-Sleep -Seconds (2 * $attempt) }
    }

    throw "Could not download $Url after $attempts attempts. Download it manually and place it at $Destination, then re-run."
}

if ((Test-Path $tempZip) -and (Get-Item $tempZip).Length -gt 1MB -and -not $Force) {
    Write-Host "using the archive already at $tempZip"
} else {
    Write-Host 'downloading platform-tools (about 10 MB)...'
    Get-Archive -Url $assetUrl -Destination $tempZip
}

$sizeMb = [math]::Round((Get-Item $tempZip).Length / 1MB, 1)
Write-Host "downloaded $sizeMb MB"

Write-Host 'extracting...'
if (Test-Path $tempExtract) { Remove-Item -Recurse -Force $tempExtract }
Expand-Archive -Path $tempZip -DestinationPath $tempExtract -Force

$sourceDir = Join-Path $tempExtract 'platform-tools'
if (-not (Test-Path $sourceDir)) { throw "The archive did not contain a platform-tools directory." }

New-Item -ItemType Directory -Force -Path $targetDir | Out-Null

# Windows PowerShell 5.1's "Get-ChildItem -Include" without "-Recurse" silently returns
# nothing, which is what broke fetch-ffmpeg.ps1 the same way - so this filters by hand
# instead of trusting -Include.
$wanted = 'adb.exe', 'AdbWinApi.dll', 'AdbWinUsbApi.dll'
$copied = 0
foreach ($name in $wanted) {
    $file = Join-Path $sourceDir $name
    if (Test-Path $file) {
        Copy-Item -Path $file -Destination $targetDir -Force
        $copied++
    }
}
if ($copied -eq 0) { throw "The archive's platform-tools directory held none of the expected files." }
if (-not (Test-Path (Join-Path $targetDir 'adb.exe'))) { throw "adb.exe was not among the files extracted." }

Remove-Item -Recurse -Force $tempExtract

Write-Host "placed $copied file(s) in $targetDir" -ForegroundColor Green
Write-Host ''
Write-Host 'On the phone: Settings > About phone > tap "Build number" 7 times, then' -ForegroundColor DarkGray
Write-Host 'Settings > Developer options > turn on "USB debugging". Connect the cable and' -ForegroundColor DarkGray
Write-Host 'tap "Allow" on the phone when SoulScreen first tries to read the screen.' -ForegroundColor DarkGray
Write-Host ''
