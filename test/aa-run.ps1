<#
.SYNOPSIS
  A/A noise run: measures the SAME build twice and reports the per-op difference.

.DESCRIPTION
  There is no code change between the two passes, so every difference it reports is
  harness/machine noise. That number is the floor a real perf delta has to beat before it means
  anything - see docs/blueprints-included/in-game-regression-testing.md, "A/A noise measurement".

  Worth re-running whenever the machine, the ONI version or the sweep's iteration counts change;
  the noise floor is a property of all three, and a stale one is worse than none.

  Takes roughly twice a single `run-ingame.ps1 -Perf` (~15 minutes), with the game window up.

.PARAMETER OutDir
  Where to write aa-1.json / aa-2.json. Defaults to a temp folder; it is measurement scratch, not
  something to commit.
#>
param(
    [string]$OutDir = (Join-Path $env:TEMP 'bpi-harness-aa')
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$perfJson = Join-Path $env:TEMP 'bpi-harness\perf.json'

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

foreach ($pass in 1, 2) {
    Write-Host "=== A/A pass $pass of 2 ===" -ForegroundColor Cyan
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $repo 'test\run-ingame.ps1') -Perf
    if (-not (Test-Path $perfJson)) {
        throw "pass ${pass}: $perfJson was not produced - the perf run did not complete."
    }
    Copy-Item $perfJson (Join-Path $OutDir "aa-$pass.json") -Force
    Write-Host "=== saved aa-$pass.json ===" -ForegroundColor Cyan
}

$a = (Get-Content (Join-Path $OutDir 'aa-1.json') -Raw | ConvertFrom-Json)
$b = (Get-Content (Join-Path $OutDir 'aa-2.json') -Raw | ConvertFrom-Json)

function Get-Medians($report) {
    $map = @{}
    foreach ($op in $report.operations) {
        foreach ($r in $op.results) { $map["$($op.name)|$($r.n)"] = $r.medianMs }
    }
    return $map
}

$ma = Get-Medians $a
$mb = Get-Medians $b
$deltas = @()

Write-Host ''
"{0,-36} {1,6} {2,10} {3,10} {4,9}" -f 'op', 'N', 'pass1', 'pass2', 'delta%' | Write-Host
foreach ($key in ($ma.Keys | Sort-Object)) {
    if (-not $mb.ContainsKey($key) -or $ma[$key] -le 0) { continue }
    $x = $ma[$key]; $y = $mb[$key]
    $d = ($y - $x) / $x * 100.0
    $deltas += $d
    $parts = $key -split '\|'
    "{0,-36} {1,6} {2,10:F2} {3,10:F2} {4,9:F1}" -f $parts[0], $parts[1], $x, $y, $d | Write-Host
}

if ($deltas.Count -eq 0) { Write-Warning 'no comparable operations found'; return }

$sortedSigned = $deltas | Sort-Object
$sortedAbs = $deltas | ForEach-Object { [Math]::Abs($_) } | Sort-Object
$median = $sortedSigned[[int]($sortedSigned.Count / 2)]
$fasterInPass2 = ($deltas | Where-Object { $_ -lt 0 }).Count

Write-Host ''
Write-Host '==> noise floor (no code change between passes)'
Write-Host ("  pass 2 faster on {0}/{1} ops ({2:F0}%) - a large imbalance means systematic drift, not random noise" -f `
    $fasterInPass2, $deltas.Count, ($fasterInPass2 / $deltas.Count * 100))
Write-Host ("  signed delta: median {0:F1}%" -f $median)
Write-Host ("  |delta|: p50 {0:F1}%  p90 {1:F1}%  max {2:F1}%" -f `
    $sortedAbs[[int]($sortedAbs.Count / 2)],
    $sortedAbs[[Math]::Max(0, [int]($sortedAbs.Count * 0.9) - 1)],
    $sortedAbs[-1])
Write-Host '  Treat any single-run delta smaller than these as unmeasured.' -ForegroundColor DarkYellow
