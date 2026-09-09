<#
.SYNOPSIS
    Downloads the FFmpeg shared libraries SoulScreen uses to decode and render.

.DESCRIPTION
    SoulScreen receives an H.264 elementary stream and, when audio is enabled, AAC-ELD.
    FFmpeg covers both plus hardware-accelerated decoding through D3D11VA, and it is the
    only practical option for AAC-ELD on Windows - Media Foundation does not decode it.

    This fetches an LGPL shared build (no GPL-only components) from BtbN's FFmpeg-Builds
    and keeps just the DLLs, in native/ffmpeg. That directory is gitignored; nothing here
    is redistributed with SoulScreen's own source.

.PARAMETER Force
    Re-download even if the DLLs are already present.

.EXAMPLE
    pwsh tools/fetch-ffmpeg.ps1
#>
[CmdletBinding()]
param([switch]$Force)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# Pinned to the major version the FFmpeg.AutoGen binding in SoulScreen.Media targets.
# Bumping one without the other produces entry-point errors at the first decode.
$ffmpegSeries = 'n8.1'
$assetName = "ffmpeg-$ffmpegSeries-latest-win64-lgpl-shared-8.1.zip"
$assetUrl = "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/$assetName"

$repoRoot = Split-Path -Parent $PSScriptRoot
$targetDir = Join-Path $repoRoot 'native/ffmpeg'
$marker = Join-Path $targetDir 'avcodec-62.dll'

Write-Host ''
Write-Host 'SoulScreen FFmpeg runtime' -ForegroundColor Cyan
Write-Host '-------------------------'

if ((Test-Path $marker) -and -not $Force) {
    $count = (Get-ChildItem -Path $targetDir -Filter *.dll).Count
    Write-Host "Already present: $count DLL(s) in $targetDir"
    Write-Host 'Use -Force to re-download.'
    Write-Host ''
    exit 0
}

$tempZip = Join-Path ([IO.Path]::GetTempPath()) $assetName
$tempExtract = Join-Path ([IO.Path]::GetTempPath()) 'soulscreen-ffmpeg'

# GitHub redirects release downloads to a CDN that intermittently drops the connection or
# answers 500, especially from behind a VPN. Retry rather than fail the whole setup.
function Get-Archive {
    param([string]$Url, [string]$Destination)

    $attempts = 5
    for ($attempt = 1; $attempt -le $attempts; $attempt++) {
        Write-Host "  attempt $attempt of $attempts..."
        if (Test-Path $Destination) { Remove-Item -Force $Destination }

        $previousProgress = $ProgressPreference
        # The progress bar makes Invoke-WebRequest an order of magnitude slower.
        $ProgressPreference = 'SilentlyContinue'
        try {
            Invoke-WebRequest -Uri $Url -OutFile $Destination -UseBasicParsing -TimeoutSec 900
        } catch {
            Write-Host "    $($_.Exception.Message)" -ForegroundColor DarkYellow
        } finally {
            $ProgressPreference = $previousProgress
        }

        # A 500 from the CDN arrives as an HTML error page, so check the payload is really
        # a zip rather than trusting the request succeeded.
        if ((Test-Path $Destination) -and (Get-Item $Destination).Length -gt 10MB) {
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

if ((Test-Path $tempZip) -and (Get-Item $tempZip).Length -gt 10MB -and -not $Force) {
    Write-Host "using the archive already at $tempZip"
} else {
    Write-Host "downloading $assetName (about 70 MB)..."
    Get-Archive -Url $assetUrl -Destination $tempZip
}

$sizeMb = [math]::Round((Get-Item $tempZip).Length / 1MB, 1)
Write-Host "downloaded $sizeMb MB"

Write-Host 'extracting...'
if (Test-Path $tempExtract) { Remove-Item -Recurse -Force $tempExtract }
Expand-Archive -Path $tempZip -DestinationPath $tempExtract -Force

$binDir = Get-ChildItem -Path $tempExtract -Directory -Recurse |
    Where-Object { $_.Name -eq 'bin' } |
    Select-Object -First 1
if (-not $binDir) { throw "The archive did not contain a bin directory." }

New-Item -ItemType Directory -Force -Path $targetDir | Out-Null
Get-ChildItem -Path $targetDir -Filter *.dll | Remove-Item -Force

# Only what SoulScreen actually calls: decoding, pixel conversion, resampling, and
# muxing for the recording feature. avfilter and avdevice add ~32 MB and nothing we use.
$wanted = 'avcodec-*.dll', 'avutil-*.dll', 'swscale-*.dll', 'swresample-*.dll', 'avformat-*.dll'
$dlls = Get-ChildItem -Path $binDir.FullName -Include $wanted -File
foreach ($dll in $dlls) {
    Copy-Item -Path $dll.FullName -Destination $targetDir -Force
}
if ($dlls.Count -eq 0) { throw "The archive's bin directory held none of the expected FFmpeg DLLs." }

Remove-Item -Recurse -Force $tempExtract
Remove-Item -Force $tempZip

$totalMb = [math]::Round((Get-ChildItem $targetDir -Filter *.dll | Measure-Object -Property Length -Sum).Sum / 1MB, 1)
Write-Host ''
Write-Host "installed $($dlls.Count) DLL(s), $totalMb MB, into $targetDir" -ForegroundColor Green
Get-ChildItem $targetDir -Filter *.dll | ForEach-Object { Write-Host "  $($_.Name)" }
Write-Host ''
Write-Host 'These are copied next to SoulScreen.exe on the next build.'
Write-Host ''
