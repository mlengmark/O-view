<#
.SYNOPSIS
O-view's check: build, every test (including the smoke test), and both panels' sample renders
compared with the golden images. Run it before every PR.

.DESCRIPTION
    powershell -File tools/check.ps1                  # the full check
    powershell -File tools/check.ps1 -UpdateGolden    # accept this run's renders as the new goldens

Steps:
  1. Builds O-view.slnx.
  2. Runs every test project, including the smoke test (tests/O-view.App.Tests/SmokeTests.cs),
     which runs the real engine against tests/fixtures/claude-home.
  3. Renders every shared sample state (O-view.Presentation's SampleStates) for both panels
     into artifacts/samples/windows and artifacts/samples/linux.
  4. Compares each render with tests/golden/<head>/<same name>, pixel by pixel, writing a diff
     image for every one that changed to artifacts/diff/<head>/.

Windows only: the Windows panel is WPF. The Linux panel is rendered here too, through Avalonia
on Windows, so both heads' goldens come from the same machine. CI's `visual` job runs this
script on windows-latest and its renders are the authority; if a local run disagrees with CI,
trust CI.

Exits 0 when everything passed and no golden image changed (or -UpdateGolden accepted the
changes), 1 otherwise.

.PARAMETER UpdateGolden
Copy this run's changed and new renders into tests/golden, and delete goldens that are no
longer rendered. Only for the states a slice declares as changing.

.PARAMETER SkipBuild
Use the existing build output.

.PARAMETER SkipTests
Skip step 2 (CI's visual job does: the test job has already run them).

.PARAMETER ChannelThreshold
A pixel counts as different when any colour channel moves by more than this (0-255). Absorbs
anti-aliasing noise without hiding a moved line or changed text.

.PARAMETER MaxDifferentPixels
An image counts as changed when more than this many pixels differ.
#>
[CmdletBinding()]
param(
    [switch]$UpdateGolden,
    [switch]$SkipBuild,
    [switch]$SkipTests,
    [string]$Configuration = 'Release',
    [int]$ChannelThreshold = 40,
    [int]$MaxDifferentPixels = 0
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

$samples = Join-Path $root 'artifacts/samples'
$diffs = Join-Path $root 'artifacts/diff'
$golden = Join-Path $root 'tests/golden'
$failures = New-Object System.Collections.Generic.List[string]
$summary = New-Object System.Collections.Generic.List[string]

function Step([string]$text) { Write-Host ""; Write-Host "== $text" -ForegroundColor Cyan }

# -- 1. build ------------------------------------------------------------------------------
if (-not $SkipBuild) {
    Step 'Build'
    & dotnet build O-view.slnx -c $Configuration -v minimal --nologo
    if ($LASTEXITCODE -ne 0) { Write-Host 'Build failed.' -ForegroundColor Red; exit 1 }
}

# -- 2. tests, including the smoke test ----------------------------------------------------
if (-not $SkipTests) {
    Step 'Tests (including the smoke test)'
    & dotnet test O-view.slnx -c $Configuration --no-build --nologo
    if ($LASTEXITCODE -ne 0) {
        $failures.Add('tests failed')
        $summary.Add('- Tests: **failed**')
    } else {
        $summary.Add('- Tests: passed (smoke test included)')
    }
}

# -- 3. render both panels -----------------------------------------------------------------
Step 'Render the shared sample states'
if (Test-Path $samples) { Remove-Item $samples -Recurse -Force }
$windowsOut = Join-Path $samples 'windows'
$linuxOut = Join-Path $samples 'linux'
New-Item -ItemType Directory -Force $windowsOut, $linuxOut | Out-Null

$tray = Join-Path $root "src/O-view.Tray/bin/$Configuration/net10.0-windows/O-view.Tray.exe"
$process = Start-Process -FilePath $tray -ArgumentList @('--popup-samples', ('"{0}"' -f $windowsOut)) -Wait -PassThru -NoNewWindow
if ($process.ExitCode -ne 0) { $failures.Add("Windows renderer exited with $($process.ExitCode)") }

$linux = Join-Path $root "src/O-view.Linux/bin/$Configuration/net10.0/o-view.dll"
& dotnet $linux --panel-samples $linuxOut | Out-Host
if ($LASTEXITCODE -ne 0) { $failures.Add("Linux renderer exited with $LASTEXITCODE") }

Write-Host ("rendered {0} Windows and {1} Linux images" -f
    (Get-ChildItem $windowsOut -Filter *.png).Count, (Get-ChildItem $linuxOut -Filter *.png).Count)

# -- 4. compare with the goldens -----------------------------------------------------------
Step 'Compare with tests/golden'
$compareSource = Get-Content -Raw (Join-Path $PSScriptRoot 'ImageCompare.cs')
if ($PSVersionTable.PSEdition -eq 'Core') {
    Add-Type -TypeDefinition $compareSource -ReferencedAssemblies System.Drawing.Common, System.Drawing.Primitives, System.Runtime.InteropServices
} else {
    Add-Type -TypeDefinition $compareSource -ReferencedAssemblies System.Drawing
}

if (Test-Path $diffs) { Remove-Item $diffs -Recurse -Force }
$changedTotal = 0; $newTotal = 0; $missingTotal = 0

foreach ($head in 'windows', 'linux') {
    $renderDir = Join-Path $samples $head
    $goldDir = Join-Path $golden $head
    $diffDir = Join-Path $diffs $head
    New-Item -ItemType Directory -Force $goldDir, $diffDir | Out-Null

    $rendered = @(Get-ChildItem $renderDir -Filter *.png | ForEach-Object Name)
    $goldens = @(Get-ChildItem $goldDir -Filter *.png -ErrorAction SilentlyContinue | ForEach-Object Name)

    $new = @($rendered | Where-Object { $goldens -notcontains $_ })
    $missing = @($goldens | Where-Object { $rendered -notcontains $_ })
    $changed = New-Object System.Collections.Generic.List[string]

    foreach ($name in ($rendered | Where-Object { $goldens -contains $_ })) {
        $result = [OViewTools.ImageCompare]::Compare(
            (Join-Path $goldDir $name), (Join-Path $renderDir $name), $ChannelThreshold, (Join-Path $diffDir $name))
        if (-not $result.SizeMatches) {
            $changed.Add("$name (size changed)")
        } elseif ($result.DifferentPixels -gt $MaxDifferentPixels) {
            $changed.Add(("{0} ({1} pixels, max channel change {2})" -f $name, $result.DifferentPixels, $result.MaxChannelDelta))
        }
    }

    $changedTotal += $changed.Count; $newTotal += $new.Count; $missingTotal += $missing.Count
    $verdict = if ($changed.Count + $new.Count + $missing.Count -eq 0) { 'unchanged' } else { 'CHANGED' }
    Write-Host ("{0}: {1} rendered, {2} changed, {3} new, {4} no longer rendered - {5}" -f
        $head, $rendered.Count, $changed.Count, $new.Count, $missing.Count, $verdict)
    foreach ($c in $changed) { Write-Host "   changed: $c" -ForegroundColor Yellow }
    foreach ($n in $new) { Write-Host "   new:     $n" -ForegroundColor Yellow }
    foreach ($m in $missing) { Write-Host "   missing: $m" -ForegroundColor Yellow }

    $summary.Add(("- Golden ({0}): {1} rendered | {2} changed | {3} new | {4} no longer rendered" -f
        $head, $rendered.Count, $changed.Count, $new.Count, $missing.Count))
    foreach ($c in $changed) { $summary.Add("  - changed: $c") }
    foreach ($n in $new) { $summary.Add("  - new: $n") }
    foreach ($m in $missing) { $summary.Add("  - no longer rendered: $m") }

    if ($UpdateGolden) {
        foreach ($name in $rendered) { Copy-Item (Join-Path $renderDir $name) (Join-Path $goldDir $name) -Force }
        foreach ($name in $missing) { Remove-Item (Join-Path $goldDir $name) -Force }
    }
}

$goldenDiffers = ($changedTotal + $newTotal + $missingTotal) -gt 0
if ($goldenDiffers -and -not $UpdateGolden) {
    $failures.Add('golden images differ (diff images in artifacts/diff/)')
}
if ($UpdateGolden -and $goldenDiffers) {
    Write-Host "tests/golden updated: $changedTotal changed, $newTotal added, $missingTotal removed. Commit only the states your slice declares." -ForegroundColor Yellow
}

# -- result --------------------------------------------------------------------------------
$report = @('## tools/check.ps1') + $summary
$report += if ($failures.Count -eq 0) { '**Result: passed**' } else { "**Result: failed** - $($failures -join '; ')" }
$report | Set-Content -Encoding utf8 (Join-Path $root 'artifacts/check-summary.md')
if ($env:GITHUB_STEP_SUMMARY) { $report | Add-Content -Encoding utf8 $env:GITHUB_STEP_SUMMARY }

Step 'Result'
if ($failures.Count -eq 0) {
    Write-Host 'Passed.' -ForegroundColor Green
    exit 0
}
foreach ($f in $failures) { Write-Host "FAILED: $f" -ForegroundColor Red }
exit 1
