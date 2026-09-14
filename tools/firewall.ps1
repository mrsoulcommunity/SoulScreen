<#
.SYNOPSIS
    Adds (or removes) the Windows Firewall rules SoulScreen needs to be reachable from an iPhone.

.DESCRIPTION
    An AirPlay receiver is a server: the phone opens connections to this PC, so Windows
    Firewall has to allow them inbound.

      * UDP 5353 - mDNS, how the phone discovers the receiver at all
      * TCP 7000 - the RTSP control channel
      * dynamic  - the video (TCP) and audio (UDP) ports are allocated per session, so
                   those rules are scoped to the program rather than to a port

    Rules are created for the Private network profile only. If your Wi-Fi is classified as
    Public, either reclassify it (Settings > Network > Properties > Private network) or
    re-run with -IncludePublic - though allowing inbound connections on a public network
    is not something to do on an untrusted one.

    Requires an elevated PowerShell.

.PARAMETER Remove
    Delete the rules instead of creating them.

.PARAMETER IncludePublic
    Also apply the rules on the Public network profile.

.EXAMPLE
    # From an elevated prompt, in the repository root:
    pwsh tools/firewall.ps1
#>
[CmdletBinding()]
param(
    [switch]$Remove,
    [switch]$IncludePublic
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$groupName = 'SoulScreen'
$repoRoot = Split-Path -Parent $PSScriptRoot

function Test-Elevated {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

if (-not (Test-Elevated)) {
    Write-Host ''
    Write-Host 'This script changes Windows Firewall and needs an elevated PowerShell.' -ForegroundColor Yellow
    Write-Host 'Right-click Windows Terminal or PowerShell, "Run as administrator", then:'
    Write-Host "    cd '$repoRoot'"
    Write-Host "    pwsh tools/firewall.ps1$(if ($Remove) { ' -Remove' })"
    Write-Host ''
    exit 1
}

# ------------------------------------------------------------------- removal

if ($Remove) {
    $existing = Get-NetFirewallRule -Group $groupName -ErrorAction SilentlyContinue
    if (-not $existing) {
        Write-Host "No $groupName firewall rules found."
        exit 0
    }
    $existing | Remove-NetFirewallRule
    Write-Host "Removed $($existing.Count) $groupName firewall rule(s)." -ForegroundColor Green
    exit 0
}

# ------------------------------------------------------------------ creation

$profiles = if ($IncludePublic) { 'Private,Public' } else { 'Private' }

# Program rules cover the per-session media ports, which are allocated dynamically and so
# cannot be listed here.
$programs = @(
    Join-Path $repoRoot 'src/SoulScreen.App/bin/Debug/net8.0-windows/SoulScreen.App.exe'
    Join-Path $repoRoot 'src/SoulScreen.App/bin/Release/net8.0-windows/SoulScreen.App.exe'
    # Where RUN.bat starts the app from.
    Join-Path $repoRoot 'artifacts/run/app/SoulScreen.App.exe'
    Join-Path $repoRoot 'src/SoulScreen.Cli/bin/Debug/net8.0/SoulScreen.Cli.exe'
    Join-Path $repoRoot 'src/SoulScreen.Cli/bin/Release/net8.0/SoulScreen.Cli.exe'
) | Where-Object { Test-Path $_ }

Write-Host ''
Write-Host 'SoulScreen firewall rules' -ForegroundColor Cyan
Write-Host '-------------------------'
Write-Host "profiles: $profiles"

# Start clean so re-running does not pile up duplicates.
Get-NetFirewallRule -Group $groupName -ErrorAction SilentlyContinue | Remove-NetFirewallRule

New-NetFirewallRule -DisplayName 'SoulScreen mDNS (UDP 5353)' -Group $groupName `
    -Direction Inbound -Action Allow -Protocol UDP -LocalPort 5353 -Profile $profiles | Out-Null
Write-Host '  added  UDP 5353  (discovery)'

New-NetFirewallRule -DisplayName 'SoulScreen AirPlay control (TCP 7000)' -Group $groupName `
    -Direction Inbound -Action Allow -Protocol TCP -LocalPort 7000 -Profile $profiles | Out-Null
Write-Host '  added  TCP 7000  (control channel)'

if ($programs.Count -eq 0) {
    Write-Host ''
    Write-Host 'No built executables found, so no program rules were added.' -ForegroundColor Yellow
    Write-Host 'Build first (dotnet build), then re-run this script so the per-session'
    Write-Host 'video and audio ports are allowed too.'
} else {
    foreach ($program in $programs) {
        $name = Split-Path -Leaf $program
        $config = if ($program -match '\\artifacts\\run\\') { 'RUN.bat' } elseif ($program -match '\\Release\\') { 'Release' } else { 'Debug' }
        New-NetFirewallRule -DisplayName "SoulScreen $name ($config, TCP)" -Group $groupName `
            -Direction Inbound -Action Allow -Protocol TCP -Program $program -Profile $profiles | Out-Null
        New-NetFirewallRule -DisplayName "SoulScreen $name ($config, UDP)" -Group $groupName `
            -Direction Inbound -Action Allow -Protocol UDP -Program $program -Profile $profiles | Out-Null
        Write-Host "  added  program  $program"
    }
}

Write-Host ''
Write-Host 'Done. Remove them again with: pwsh tools/firewall.ps1 -Remove' -ForegroundColor Green

# A receiver on a network profile the rules do not cover is the single most common reason
# the phone never lists it, so say which profile the active connection is using.
$active = Get-NetConnectionProfile | Select-Object -ExpandProperty NetworkCategory -Unique
Write-Host ''
Write-Host "Active network profile(s): $($active -join ', ')"
if ($active -notcontains 'Private' -and -not $IncludePublic) {
    Write-Host 'None of your active networks are Private, so these rules will not apply yet.' -ForegroundColor Yellow
    Write-Host 'Set the Wi-Fi you share with the iPhone to Private, or re-run with -IncludePublic.'
}
Write-Host ''
