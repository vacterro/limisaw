#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Build LIMISAW.exe — one self-contained file.

.DESCRIPTION
    Compiles with the .NET Framework 4.x compiler that ships inside Windows, so
    there is nothing to install to build this either. Every palette, sound and
    the icon are embedded as resources, which is what makes the resulting exe
    runnable from an empty folder.

    -Tests also builds and runs the whole test suite against the fresh exe.
#>

param(
    [switch]$Tests,
    [switch]$Quiet
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Definition

$cscCandidates = @(
    'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe',
    'C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe'
)
$csc = $cscCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $csc) { throw "no .NET Framework 4.x compiler found; looked in: $($cscCandidates -join ', ')" }

$sources = @('LIMISAW.cs', 'Probe.cs', 'ProbeClaude.cs', 'ProbeAntigravity.cs', 'Assets.cs') |
    ForEach-Object { Join-Path $root $_ }
$missing = $sources | Where-Object { -not (Test-Path -LiteralPath $_) }
if ($missing) { throw "missing source file(s): $($missing -join ', ')" }

$references = @('System.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll',
                'System.Web.Extensions.dll')

# Resource names must match what Assets.cs looks for. The icon is NOT embedded
# as a managed resource: -win32icon: already puts it in the exe's win32 icon
# group, which Explorer needs anyway, and Assets.AppIcon reads it back from
# there — one copy instead of two.
$resources = @()
foreach ($theme in Get-ChildItem -LiteralPath (Join-Path $root 'Themes') -Filter '*.json') {
    $resources += "-resource:$($theme.FullName),Limisaw.Themes.$($theme.Name)"
}
foreach ($sound in Get-ChildItem -LiteralPath (Join-Path $root 'Sounds') -Filter '*.wav') {
    $resources += "-resource:$($sound.FullName),Limisaw.Sounds.$($sound.Name)"
}
$icon = Join-Path $root 'heh.ico'
if (-not (Test-Path -LiteralPath $icon)) { throw "missing heh.ico: the app icon lives in the win32 icon group, so the build needs it" }

$exe = Join-Path $root 'LIMISAW.exe'
$arguments = @('-nologo', '-target:winexe', "-out:$exe", '-optimize+', "-win32icon:$icon")
$arguments += $references | ForEach-Object { "-r:$_" }
$arguments += $resources
$arguments += $sources

if (-not $Quiet) { Write-Host "Building LIMISAW.exe ($($resources.Count) embedded resources + the win32 icon)..." -ForegroundColor Cyan }
& $csc @arguments
if ($LASTEXITCODE -ne 0) { throw "compiler returned $LASTEXITCODE" }
$size = [math]::Round((Get-Item -LiteralPath $exe).Length / 1KB)
if (-not $Quiet) { Write-Host "LIMISAW.exe: ${size} KB" -ForegroundColor Green }

if (-not $Tests) { return }

# ── tests ────────────────────────────────────────────────────────────────────
# Each harness is a standalone console exe. The ones that reflect over
# LIMISAW.exe must run from the repo root, which is where they are launched.
$testRefs = @{
    'limits'          = @('System.dll')
    'carry_forward'   = @('System.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll')
    'layout_fit'      = @('System.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll')
    'notify_alerts'   = @('System.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll', 'System.Web.Extensions.dll')
    'notify_settings' = @('System.dll')
    'pixel_purity'    = @('System.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll', 'System.Web.Extensions.dll')
    'reset_lock'      = @('System.dll')
    'standalone'      = @('System.dll', 'System.Drawing.dll')
    'tray_countdown'  = @('System.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll', 'System.Web.Extensions.dll')
    'tray_error'      = @('System.dll')
    'tray_items'      = @('System.dll')
    'tray_popup'      = @('System.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll')
    'tray_tip'        = @('System.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll')
}

$outDir = Join-Path $root 'tests\bin'
New-Item -ItemType Directory -Path $outDir -Force | Out-Null
$failed = New-Object System.Collections.Generic.List[string]

foreach ($name in ($testRefs.Keys | Sort-Object)) {
    $source = Join-Path $root "tests\$name.cs"
    if (-not (Test-Path -LiteralPath $source)) { continue }
    $testExe = Join-Path $outDir "$name.exe"
    # NOT $args: that is PowerShell's automatic variable for the caller's own
    # arguments, and assigning it inside a script is a silent trap.
    $testArgs = @('-nologo', "-out:$testExe")
    $testArgs += $testRefs[$name] | ForEach-Object { "-r:$_" }
    # limits.cs asserts the probe's own rules, so it links the engine sources
    # rather than reflecting over the built exe. -main picks its entry point
    # over LIMISAW.cs's own.
    if ($name -eq 'limits') {
        $testArgs += @('-r:System.Web.Extensions.dll', '-r:System.Drawing.dll', '-r:System.Windows.Forms.dll',
                       '-main:LimitsTest')
        $testArgs += @((Join-Path $root 'Probe.cs'), (Join-Path $root 'ProbeClaude.cs'),
                       (Join-Path $root 'ProbeAntigravity.cs'), (Join-Path $root 'Assets.cs'),
                       (Join-Path $root 'LIMISAW.cs'))
    }
    $testArgs += $source
    & $csc @testArgs
    if ($LASTEXITCODE -ne 0) { $failed.Add("$name (build)"); continue }

    Write-Host "--- $name" -ForegroundColor Yellow
    Push-Location $root
    try {
        & $testExe | Select-Object -Last 2
        if ($LASTEXITCODE -ne 0) { $failed.Add($name) }
    } finally { Pop-Location }
}

Write-Host ''
if ($failed.Count -eq 0) {
    Write-Host 'All tests passed.' -ForegroundColor Green
} else {
    Write-Host "FAILED: $($failed -join ', ')" -ForegroundColor Red
    exit 1
}
