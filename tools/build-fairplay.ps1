<#
.SYNOPSIS
    Builds soulscreen_fairplay.dll, the native FairPlay helper SoulScreen needs for
    wireless AirPlay mirroring.

.DESCRIPTION
    iOS will not hand over the AES key for a mirroring stream until the receiver answers
    Apple's proprietary FairPlay SAP handshake on /fp-setup. The only public implementation
    is the reverse-engineered "playfair" code carried by RPiPlay, which is GPLv3 and about
    half a megabyte of generated lookup tables - far too much to port by hand and not
    something we want inside a permissively licensed C# tree.

    So it stays where it belongs: this script downloads those sources into native/fairplay
    (which is gitignored), compiles them into a standalone DLL with MinGW gcc, and the
    managed side reaches it through P/Invoke. SoulScreen runs without the DLL - discovery,
    pairing and the USB transport all work - but AirPlay mirroring will report that
    FairPlay support is missing.

    Sources: https://github.com/FD-/RPiPlay (GPLv3). See native/fairplay/LICENSE.md after
    the first run.

.PARAMETER Force
    Re-download the sources even if they are already present.

.EXAMPLE
    pwsh tools/build-fairplay.ps1
#>
[CmdletBinding()]
param(
    [switch]$Force,
    [string]$Gcc
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
$nativeDir = Join-Path $repoRoot 'native/fairplay'
$playfairDir = Join-Path $nativeDir 'playfair'
$outputDir = Join-Path $repoRoot 'native'
$outputDll = Join-Path $outputDir 'soulscreen_fairplay.dll'

$baseUrl = 'https://raw.githubusercontent.com/FD-/RPiPlay/master/lib'

# ---------------------------------------------------------------- locate gcc

function Find-Gcc {
    if ($Gcc) {
        if (Test-Path $Gcc) { return $Gcc }
        throw "The -Gcc path '$Gcc' does not exist."
    }

    $onPath = Get-Command gcc -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }

    # WinLibs and MSYS2 are the two common ways to get a 64-bit MinGW on Windows.
    $candidates = @(
        "$env:LOCALAPPDATA\Microsoft\WinGet\Packages\*\mingw64\bin\gcc.exe"
        'C:\msys64\mingw64\bin\gcc.exe'
        'C:\mingw64\bin\gcc.exe'
        "$env:ProgramFiles\LLVM\bin\clang.exe"
    )
    foreach ($pattern in $candidates) {
        $found = Get-ChildItem -Path $pattern -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($found) { return $found.FullName }
    }

    throw @'
No C compiler found. Install one of:
  winget install BrechtSanders.WinLibs.POSIX.UCRT
  winget install MSYS2.MSYS2
then re-run this script (or pass -Gcc <path to gcc.exe>).
'@
}

# ------------------------------------------------------------ fetch sources

function Get-Source {
    param([string]$RelativeUrl, [string]$Destination)

    if ((Test-Path $Destination) -and -not $Force) {
        Write-Host "  have    $(Split-Path -Leaf $Destination)"
        return
    }

    $url = "$baseUrl/$RelativeUrl"
    Write-Host "  fetch   $(Split-Path -Leaf $Destination)"
    # Invoke-WebRequest is used rather than curl because the bundled curl on some Windows
    # installs fails the github TLS handshake through schannel.
    Invoke-WebRequest -Uri $url -OutFile $Destination -UseBasicParsing -TimeoutSec 120
}

Write-Host ''
Write-Host 'SoulScreen FairPlay helper' -ForegroundColor Cyan
Write-Host '--------------------------'

$gccPath = Find-Gcc
Write-Host "compiler: $gccPath"
$gccVersion = (& $gccPath --version | Select-Object -First 1)
Write-Host "          $gccVersion"
Write-Host ''

New-Item -ItemType Directory -Force -Path $playfairDir | Out-Null
New-Item -ItemType Directory -Force -Path $outputDir | Out-Null

Write-Host 'sources:'
Get-Source 'fairplay_playfair.c' (Join-Path $nativeDir 'fairplay_playfair.c')
foreach ($file in @('playfair.c', 'playfair.h', 'omg_hax.c', 'omg_hax.h', 'hand_garble.c', 'modified_md5.c', 'sap_hash.c', 'LICENSE.md')) {
    Get-Source "playfair/$file" (Join-Path $playfairDir $file)
}

# fairplay_playfair.c includes "fairplay.h", which lives elsewhere in RPiPlay and drags in
# its logger. These four declarations are all it actually needs.
$fairplayHeader = @'
/* Minimal declarations for RPiPlay's fairplay_playfair.c, written for SoulScreen.
   The logger type is opaque here and is never dereferenced by that translation unit. */
#ifndef SOULSCREEN_FAIRPLAY_H
#define SOULSCREEN_FAIRPLAY_H

typedef struct logger_s logger_t;
typedef struct fairplay_s fairplay_t;

fairplay_t *fairplay_init(logger_t *logger);
int fairplay_setup(fairplay_t *fp, const unsigned char req[16], unsigned char res[142]);
int fairplay_handshake(fairplay_t *fp, const unsigned char req[164], unsigned char res[32]);
int fairplay_decrypt(fairplay_t *fp, const unsigned char input[72], unsigned char output[16]);
void fairplay_destroy(fairplay_t *fp);

#endif
'@
Set-Content -Path (Join-Path $nativeDir 'fairplay.h') -Value $fairplayHeader -Encoding ascii

# The exported surface SoulScreen.AirPlay P/Invokes. Kept deliberately thin: allocate a
# session, run the two handshake rounds, decrypt the SETUP key, free the session.
$shim = @'
/* SoulScreen <-> playfair bridge. Written for SoulScreen; the heavy lifting happens in
   the RPiPlay sources this is linked against. */
#include <stdlib.h>
#include <string.h>
#include "fairplay.h"

#define SS_EXPORT __declspec(dllexport)

SS_EXPORT const char *ss_fairplay_version(void)
{
    return "soulscreen-fairplay/1 (playfair via RPiPlay)";
}

SS_EXPORT void *ss_fairplay_create(void)
{
    return (void *)fairplay_init(0);
}

SS_EXPORT void ss_fairplay_destroy(void *handle)
{
    if (handle) fairplay_destroy((fairplay_t *)handle);
}

/* Round one: 16 bytes in, 142 bytes out. */
SS_EXPORT int ss_fairplay_setup(void *handle, const unsigned char *req, int req_len,
                                unsigned char *res, int res_len)
{
    if (!handle || !req || !res) return -2;
    if (req_len < 16 || res_len < 142) return -3;
    return fairplay_setup((fairplay_t *)handle, req, res);
}

/* Round two: 164 bytes in, 32 bytes out. */
SS_EXPORT int ss_fairplay_handshake(void *handle, const unsigned char *req, int req_len,
                                    unsigned char *res, int res_len)
{
    if (!handle || !req || !res) return -2;
    if (req_len < 164 || res_len < 32) return -3;
    return fairplay_handshake((fairplay_t *)handle, req, res);
}

/* Unwraps the 72-byte "ekey" from SETUP into the 16-byte AES key for the media streams. */
SS_EXPORT int ss_fairplay_decrypt(void *handle, const unsigned char *input, int input_len,
                                  unsigned char *output, int output_len)
{
    if (!handle || !input || !output) return -2;
    if (input_len < 72 || output_len < 16) return -3;
    return fairplay_decrypt((fairplay_t *)handle, input, output);
}
'@
Set-Content -Path (Join-Path $nativeDir 'shim.c') -Value $shim -Encoding ascii

# ------------------------------------------------------------------- compile

Write-Host ''
Write-Host 'compiling...'

$sources = @(
    (Join-Path $nativeDir 'shim.c')
    (Join-Path $nativeDir 'fairplay_playfair.c')
    (Join-Path $playfairDir 'playfair.c')
    (Join-Path $playfairDir 'omg_hax.c')
    (Join-Path $playfairDir 'hand_garble.c')
    (Join-Path $playfairDir 'modified_md5.c')
    (Join-Path $playfairDir 'sap_hash.c')
)

$gccArgs = @(
    '-O2'
    '-shared'
    '-static-libgcc'
    # The reverse-engineered sources are full of intentional type punning and unused
    # results; their warnings are noise we cannot act on.
    '-w'
    "-I$nativeDir"
    "-I$playfairDir"
    '-o'
    $outputDll
) + $sources

& $gccPath @gccArgs
if ($LASTEXITCODE -ne 0) { throw "gcc failed with exit code $LASTEXITCODE" }

$size = [math]::Round((Get-Item $outputDll).Length / 1KB)
Write-Host ''
Write-Host "built $outputDll ($size KB)" -ForegroundColor Green
Write-Host 'The DLL is copied next to SoulScreen.exe automatically on the next build.'
Write-Host ''
