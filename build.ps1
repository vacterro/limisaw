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

# ── encoding integrity preflight ─────────────────────────────────────────────
# An editor once rewrote LIMISAW.cs through the wrong code page: the file gained
# a UTF-8 BOM and every non-ASCII glyph (— · ─ ▸ ●) turned into CP1251 mojibake.
# This gate fails the build before that class of corruption can ship again:
# LIMISAW.cs must be valid UTF-8, with no BOM, and free of the byte signatures
# that mojibake leaves behind. Byte-level on purpose: the check itself stays
# pure ASCII, so it cannot be broken by the very encoding it polices.
$gateFile = Join-Path $root 'LIMISAW.cs'
function Test-BytePattern([byte[]]$Haystack, [byte[]]$Needle) {
    if ($Needle.Length -eq 0 -or $Haystack.Length -lt $Needle.Length) { return $false }
    for ($i = 0; $i -le $Haystack.Length - $Needle.Length; $i++) {
        $match = $true
        for ($j = 0; $j -lt $Needle.Length; $j++) {
            if ($Haystack[$i + $j] -ne $Needle[$j]) { $match = $false; break }
        }
        if ($match) { return $true }
    }
    return $false
}
# UTF-8 byte sequences this repo must never ship: the CP1251-mojibake glyphs it
# once suffered (em dash/quotes, middle dot, box drawing, arrow, bullet) plus
# the Unicode replacement character U+FFFD, which is the fingerprint of text an
# editor already failed to decode — it is valid UTF-8, so only an explicit byte
# signature catches it. Legitimate non-ASCII (— · ─ ▸ ●) is not rejected.
$gateSignatures = @(
    @{ Name = 'Unicode replacement character U+FFFD';              Bytes = [byte[]](0xEF,0xBF,0xBD) },
    @{ Name = 'CP1251 mojibake v+Zh (em dash / quote corruption)'; Bytes = [byte[]](0xD0,0xB2,0xD0,0x82) },
    @{ Name = 'CP1251 mojibake V+middot';                          Bytes = [byte[]](0xD0,0x92,0xC2,0xB7) },
    @{ Name = 'CP1251 mojibake v+rdquote+Zh (box drawing)';        Bytes = [byte[]](0xD0,0xB2,0xE2,0x80,0x9D,0xD0,0x82) },
    @{ Name = 'CP1251 mojibake v+endash+yo (arrow)';               Bytes = [byte[]](0xD0,0xB2,0xE2,0x80,0x93,0xD1,0x91) },
    @{ Name = 'CP1251 mojibake v+emdash+Dzh (bullet)';             Bytes = [byte[]](0xD0,0xB2,0xE2,0x80,0x94,0xD0,0x8F) }
)
# One reusable rule so the -Tests self-check can exercise it on a temporary
# fixture instead of mutating the real source.
function Get-EncodingErrors([byte[]]$bytes) {
    $errors = New-Object System.Collections.Generic.List[string]
    if ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) {
        $errors.Add('LIMISAW.cs starts with a UTF-8 BOM (repository text format is UTF-8 without BOM)')
    }
    try { [void](New-Object System.Text.UTF8Encoding($false, $true)).GetString($bytes) }
    catch { $errors.Add('LIMISAW.cs is not valid UTF-8') }
    foreach ($sig in $gateSignatures) {
        if (Test-BytePattern $bytes $sig.Bytes) { $errors.Add('LIMISAW.cs contains ' + $sig.Name) }
    }
    return $errors
}
$gateBytes = [System.IO.File]::ReadAllBytes($gateFile)
$gateErrors = @(Get-EncodingErrors $gateBytes)
# Self-check: prove the rule rejects U+FFFD rather than merely finding the real
# file clean. The fixture is the clean bytes plus EF BF BD; the real source is
# never touched.
$gateFixture = New-Object byte[] ($gateBytes.Length + 3)
[Array]::Copy($gateBytes, $gateFixture, $gateBytes.Length)
$gateFixture[$gateBytes.Length] = 0xEF
$gateFixture[$gateBytes.Length + 1] = 0xBF
$gateFixture[$gateBytes.Length + 2] = 0xBD
$fixtureErrors = @(Get-EncodingErrors $gateFixture)
if (-not ($fixtureErrors -match 'U\+FFFD')) {
    throw 'encoding integrity gate self-check FAILED: a fixture containing EF BF BD was not rejected'
}
if ($gateErrors.Count -gt 0) {
    throw ("encoding integrity gate FAILED: " + ($gateErrors -join '; '))
}

$cscCandidates = @(
    'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe',
    'C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe'
)
$csc = $cscCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $csc) { throw "no .NET Framework 4.x compiler found; looked in: $($cscCandidates -join ', ')" }

$sources = @('LIMISAW.cs', 'Probe.cs', 'ProbeClaude.cs', 'ProbeAntigravity.cs', 'ProbeZcode.cs', 'ProbeFreebuff.cs', 'Assets.cs', 'ChildSweeper.cs', 'Connections.cs') |
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
# The primary identity icon is LIMISAW.ico, generated from
# assets\branding\LIMISAW1.png by tools\make_ico.cs. The legacy heh.ico is not
# a build input any more, but an old override file beside the exe still works
# at runtime (see Assets.AppIcon). If the generated icon is missing — a fresh
# checkout that skipped it — it is regenerated here from the brand asset so
# the build never silently falls back to the legacy artwork.
$icon = Join-Path $root 'LIMISAW.ico'
if (-not (Test-Path -LiteralPath $icon)) {
    $brand = Join-Path $root 'assets\branding\LIMISAW1.png'
    if (-not (Test-Path -LiteralPath $brand)) { throw "missing LIMISAW.ico and no assets\branding\LIMISAW1.png to regenerate it from" }
    if (-not $Quiet) { Write-Host 'LIMISAW.ico missing - regenerating from assets\branding\LIMISAW1.png...' -ForegroundColor Yellow }
    $gen = Join-Path $env:TEMP "limisaw_make_ico_$PID.exe"
    try {
        & $csc -nologo -out:$gen -r:System.dll -r:System.Drawing.dll (Join-Path $root 'tools\make_ico.cs')
        if ($LASTEXITCODE -ne 0) { throw "could not compile tools\make_ico.cs to regenerate LIMISAW.ico" }
        & $gen $brand $icon
        if ($LASTEXITCODE -ne 0) { throw "make_ico failed regenerating LIMISAW.ico" }
    } finally { Remove-Item -LiteralPath $gen -Force -ErrorAction SilentlyContinue }
}
if (-not (Test-Path -LiteralPath $icon)) { throw "missing LIMISAW.ico: the app icon lives in the win32 icon group, so the build needs it" }

$exe = Join-Path $root 'LIMISAW.exe'

# The release identity, stamped from the one file that owns it. VERSION is
# canonical (the CHANGELOG's newest heading and the git tag must match it — the
# ship gate checks), so the exe's file version is DERIVED here rather than
# maintained in a second place that could drift.
$version = (Get-Content -LiteralPath (Join-Path $root 'VERSION') -Raw).Trim()
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw "VERSION '$version' is not a release version" }

# Written per build so csc picks it up as the assembly's version attributes.
$info = Join-Path $env:TEMP "limisaw_version_$PID.cs"
Set-Content -LiteralPath $info -Encoding UTF8 -Value @"
using System.Reflection;
[assembly: AssemblyVersion("$version.0")]
[assembly: AssemblyFileVersion("$version.0")]
[assembly: AssemblyProduct("LIMISAW")]
[assembly: AssemblyTitle("LIMISAW")]
[assembly: AssemblyCompany("vacterro")]
[assembly: AssemblyCopyright("Copyright (c) 2026 vac34")]
[assembly: AssemblyDescription("Agent quota monitor: Codex, Claude Code, Antigravity and Zcode limits in the tray.")]
"@
try {
    $arguments = @('-nologo', '-target:winexe', "-out:$exe", '-optimize+', "-win32icon:$icon")
    $arguments += $references | ForEach-Object { "-r:$_" }
    $arguments += $resources
    $arguments += $sources
    $arguments += $info

    if (-not $Quiet) { Write-Host "Building LIMISAW.exe v$version ($($resources.Count) embedded resources + the win32 icon)..." -ForegroundColor Cyan }
    & $csc @arguments
    if ($LASTEXITCODE -ne 0) { throw "compiler returned $LASTEXITCODE" }
} finally { Remove-Item -LiteralPath $info -Force -ErrorAction SilentlyContinue }
$size = [math]::Round((Get-Item -LiteralPath $exe).Length / 1KB)
if (-not $Quiet) { Write-Host "LIMISAW.exe: ${size} KB" -ForegroundColor Green }

if (-not $Tests) { return }

# ── tests ────────────────────────────────────────────────────────────────────
# Each harness is a standalone console exe. The ones that reflect over
# LIMISAW.exe must run from the repo root, which is where they are launched.
$testRefs = @{
    'connections_foundation' = @('System.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll', 'System.Web.Extensions.dll')
    'connections_concurrency' = @('System.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll', 'System.Web.Extensions.dll')
    'connections_onboarding' = @('System.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll', 'System.Web.Extensions.dll')
    'cli_connections' = @('System.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll', 'System.Web.Extensions.dll')
    'accounts_visibility' = @('System.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll')
    'antigravity_connection' = @('System.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll', 'System.Web.Extensions.dll')
    'antigravity_journal' = @('System.dll')
    'body_cache'      = @('System.dll')
    'lazy_discovery'  = @('System.dll')
    'settings_paint_cost' = @('System.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll')
    'sound_paint_cost' = @('System.dll')
    'discovery_generation' = @('System.dll')
    'cache_cap'       = @('System.dll')
    'apply_thread'    = @('System.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll')
    'carry_forward'   = @('System.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll')
    'child_job'       = @('System.dll')
    'claude_cache'    = @('System.dll')
    'claude_homes'    = @('System.dll')
    'codex_session'   = @('System.dll')
    'codex_scheduling' = @('System.dll')
    'codex_remote_identity' = @('System.dll')
    'codex_reserve'   = @('System.dll', 'System.Web.Extensions.dll')
    'core_refresh_ownership' = @('System.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll', 'System.Web.Extensions.dll')
    'core004_dispatch_failure' = @('System.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll')
    'core002_progress_ownership' = @('System.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll', 'System.Web.Extensions.dll')
    'core003_settings_revision' = @('System.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll')
    'freebuff'        = @('System.dll', 'System.Web.Extensions.dll')
    'gdi_paint'       = @('System.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll')
    'limits'          = @('System.dll')
    'hover_cache'     = @('System.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll')
    'ini_reload'      = @('System.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll')
    'ini_long_values' = @('System.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll')
    'layout_fit'      = @('System.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll')
    'low_channels'    = @('System.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll', 'System.Web.Extensions.dll')
    'metric_identity' = @('System.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll')
    'notify_alerts'   = @('System.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll', 'System.Web.Extensions.dll')
    'notify_settings' = @('System.dll')
    'pixel_purity'    = @('System.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll', 'System.Web.Extensions.dll')
    'provider_slots'  = @('System.dll')
    'probe_projection'= @('System.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll')
    'refresh_coalesce'= @('System.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll')
    'refresh_shutdown'= @('System.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll')
    'reset_lock'      = @('System.dll')
    'save_partial'    = @('System.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll')
    'settings_consistency' = @('System.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll')
    'settings_recovery' = @('System.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll')
    'settings_ux'     = @('System.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll', 'System.Runtime.Serialization.dll')
    'single_instance' = @('System.dll')
    'slider_commit'   = @('System.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll')
    'sound_cache'     = @('System.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll')
    'standalone'      = @('System.dll', 'System.Drawing.dll')
    'tooltips'        = @('System.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll')
    'viewport_paint'  = @('System.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll')
    'tray_countdown'  = @('System.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll', 'System.Web.Extensions.dll')
    'tray_error'      = @('System.dll')
    'tray_items'      = @('System.dll')
    'tray_popup'      = @('System.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll')
    'tray_render_modes' = @('System.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll', 'System.Web.Extensions.dll')
    'tray_tip'        = @('System.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll')
    'cmd_shim'        = @('System.dll')
    'unknown_quota'   = @('System.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll')
    'zcode_budget'    = @('System.dll')
    'zcode_detection' = @('System.dll')
    'zcode_response'  = @('System.dll')
    'audio_pipeline'  = @('System.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll')
    'payload_bounds'  = @('System.dll', 'System.Web.Extensions.dll')
    'codex_rpc_start_transaction' = @('System.dll')
    'codex_rate_limit_auth' = @('System.dll')
}

# Harness -> its entry-point class, for the ones compiled together WITH the
# engine sources instead of reflecting over LIMISAW.exe.
$engineLinked = @{
    'connections_foundation' = 'ConnectionsFoundationTest'
    'connections_concurrency' = 'ConnectionsConcurrencyTest'
    'core002_progress_ownership' = 'Core002ProgressOwnershipTest'
    'connections_onboarding' = 'ConnectionsOnboardingTest'
    'cli_connections' = 'CliConnectionsTest'
    'accounts_visibility' = 'AccountsVisibilityTest'
    'antigravity_connection' = 'AntigravityConnectionTest'
    'antigravity_journal' = 'AntigravityJournalTest'
    'body_cache'    = 'BodyCacheTest'
    'lazy_discovery' = 'LazyDiscoveryTest'
    'child_job'     = 'ChildJobTest'
    'claude_cache'  = 'ClaudeCacheTest'
    'claude_homes'  = 'ClaudeHomesTest'
    'cmd_shim'      = 'CmdShimTest'
    'codex_session' = 'CodexSessionTest'
    'codex_scheduling' = 'CodexSchedulingTest'
    'codex_remote_identity' = 'CodexRemoteIdentityTest'
    'codex_reserve' = 'CodexReserveTest'
    'freebuff' = 'FreebuffTest'
    'ini_long_values' = 'IniLongValuesTest'
    'limits'        = 'LimitsTest'
    'provider_slots' = 'ProviderSlotsTest'
    'settings_consistency' = 'SettingsConsistencyTest'
    'single_instance' = 'SingleInstanceTest'
    'tray_render_modes' = 'TrayRenderModesTest'
    'tooltips'        = 'TooltipsTest'
    'unknown_quota' = 'UnknownQuotaTest'
    'zcode_budget'  = 'ZcodeBudgetTest'
    'zcode_detection' = 'ZcodeDetectionTest'
    'zcode_response' = 'ZcodeResponseTest'
    'payload_bounds' = 'PayloadBoundsTest'
    'codex_rpc_start_transaction' = 'CodexRpcStartTransactionTest'
    'codex_rate_limit_auth' = 'CodexRateLimitAuthTest'
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
    # These harnesses assert the engine's own internals, so they LINK the engine
    # sources rather than reflecting over the built exe. -main picks the
    # harness's entry point over LIMISAW.cs's own.
    if ($engineLinked.ContainsKey($name)) {
        $testArgs += @('-r:System.Web.Extensions.dll', '-r:System.Drawing.dll', '-r:System.Windows.Forms.dll',
                       "-main:$($engineLinked[$name])")
        $testArgs += @((Join-Path $root 'Probe.cs'), (Join-Path $root 'ProbeClaude.cs'),
                       (Join-Path $root 'ProbeAntigravity.cs'), (Join-Path $root 'ProbeZcode.cs'),
                       (Join-Path $root 'ProbeFreebuff.cs'),
                       (Join-Path $root 'Assets.cs'), (Join-Path $root 'ChildSweeper.cs'),
                       (Join-Path $root 'Connections.cs'), (Join-Path $root 'LIMISAW.cs'))
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
