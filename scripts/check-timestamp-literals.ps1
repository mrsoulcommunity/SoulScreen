# Fails the build if a capture-filename site bypasses the TimestampFormatting helper.
# Catches new ad-hoc ToString("yyyy...") or DateTime.Now.ToString(...) calls in src/
# outside the formatter itself. Pattern matches the common shapes; the failure message
# prints the offending path:line.
$ErrorActionPreference = 'Stop'

$src = Join-Path $PSScriptRoot '..\src' | Resolve-Path
$allowedDir = Join-Path $src 'SoulScreen.Core\Time'
$allowedDirApp = Join-Path $src 'SoulScreen.App\Logic'   # CaptureTimestampFormatter lives here

# Two patterns the prompt calls out: format-string literal with a 'y' (year), and chained
# .ToString( on a DateTime.
$patterns = @(
    # Catches "ToString("yyyy...") or "ToString("d MMM yyyy") etc. - format strings that
    # carry a year. The "d MMM yyyy" form is the existing Gregorian fallback used inside
    # the formatter itself, so it's allowed.
    @{ Label = 'ToString("y...'; Regex = 'ToString\(\s*"[^"]*y[^"]*"'; Exclude = @('ToString\("d MMM yyyy", CultureInfo\.CurrentCulture', 'ToString\("yyyy-MM-dd", CultureInfo\.InvariantCulture', 'ToString\("yyyy-MM-ddTHH:mm:ssZ", CultureInfo\.InvariantCulture') },
    @{ Label = 'DateTime.Now.ToString('; Regex = 'DateTime\.(Now|UtcNow)\.ToString\(' }
)

$failures = New-Object System.Collections.Generic.List[string]

Get-ChildItem -Path $src -Recurse -Filter *.cs |
    Where-Object {
        $path = $_.FullName
        $path -ne $allowedDir -and
        -not ($path.StartsWith($allowedDir, [System.StringComparison]::OrdinalIgnoreCase)) -and
        -not ($path.StartsWith($allowedDirApp, [System.StringComparison]::OrdinalIgnoreCase))
    } |
    ForEach-Object {
        $file = $_.FullName
        $lines = Get-Content -LiteralPath $file
        for ($i = 0; $i -lt $lines.Count; $i++) {
            foreach ($p in $patterns) {
                if ($lines[$i] -match $p.Regex) {
                    $allowed = $false
                    if ($p.Exclude) {
                        foreach ($ex in $p.Exclude) {
                            if ($lines[$i] -match $ex) { $allowed = $true; break }
                        }
                    }
                    if (-not $allowed) {
                        $rel = $file.Substring($src.Path.Length + 1)
                        $failures.Add("$rel`:$($i + 1) ($($p.Label))")
                    }
                }
            }
        }
    }

if ($failures.Count -gt 0) {
    Write-Host ''
    Write-Host 'Timestamp formatting regression: these sites bypass CaptureTimestampFormatter/TimestampFormatting:' -ForegroundColor Red
    $failures | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
    Write-Host ''
    Write-Host 'Route them through TimestampFormatting or CaptureTimestampFormatter instead.' -ForegroundColor Yellow
    exit 1
}

Write-Host 'check-timestamp-literals: clean'
