#!/usr/bin/env pwsh
<#
.SYNOPSIS
  Build + deploy BlueprintsIncluded and the dev-only in-game test harness, launch ONI, wait for
  the harness to write its results, print a summary and exit non-zero on any regression failure.

.DESCRIPTION
  Requires a real ONI install configured via Directory.Build.props.user (GameLibsFolder + ModFolder).
  See docs/in-game-regression-testing.md and harness/README.md.

  Patches mods.json for the run (restored afterward): enables BlueprintsIncluded + the harness in
  load order, and disables any other mod that shares the BlueprintsV2 staticID (e.g. the upstream
  "Blueprints Expanded") so its patches don't collide. -NoModConfig skips this - enable/disable and
  order the mods yourself in ONI's Mods screen and re-run with -SkipBuild.

  -Perf switches to the harness's opt-in benchmark mode (docs §7): a blueprint-size sweep timing
  import operations, with no pass/fail semantics - it always exits 0. Without -Perf this runs the
  regression assertion cases (JUnit results, exit code = failure count).

  The harness only acts when it finds the sentinel this script writes, so leaving it in mods/dev
  between runs is harmless.
#>
[CmdletBinding()]
param(
    [int]$TimeoutSeconds,
    [switch]$Perf,
    [switch]$SkipBuild,
    [switch]$KeepSentinel,
    [switch]$NoModConfig,
    [string[]]$Dlc = @('', 'EXPANSION1_ID')
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
if (-not $PSBoundParameters.ContainsKey('TimeoutSeconds')) {
    $TimeoutSeconds = if ($Perf) { 900 } else { 300 }   # perf's size sweep runs longer
}

function Get-BuildProp([string]$name) {
    $userProps = Join-Path $repo 'Directory.Build.props.user'
    if (-not (Test-Path $userProps)) {
        throw "Directory.Build.props.user not found. Copy Directory.Build.props.default to it and set GameLibsFolder + ModFolder."
    }
    $xml = [xml](Get-Content $userProps)
    $node = @($xml.Project.PropertyGroup) |
        ForEach-Object { $_.$name } |
        Where-Object { $_ } |
        Select-Object -First 1
    if ($node -is [System.Xml.XmlElement]) { $node = $node.InnerText }
    if (-not $node) { throw "$name is not set in Directory.Build.props.user" }
    return ([string]$node).Trim()
}

$modFolder = Get-BuildProp 'ModFolder'   # ...\Klei\OxygenNotIncluded\mods\dev
$modsJson  = Join-Path (Split-Path $modFolder) 'mods.json'

$outDir       = Join-Path ([System.IO.Path]::GetTempPath()) 'bpi-harness'
$sentinel     = Join-Path $outDir 'run'
$resultsXml   = Join-Path $outDir 'results.xml'
$perfJson     = Join-Path $outDir 'perf.json'
$waitFor      = if ($Perf) { $perfJson } else { $resultsXml }
$harnessLog   = Join-Path $outDir 'harness.log'
$modsJsonBak  = Join-Path $outDir 'mods.json.bak'
$fixtureSrc   = Join-Path $repo 'harness/fixtures/poc-colony.sav'
$fixtureDst   = Join-Path $outDir 'poc-colony.sav'   # harness loads this absolute path

New-Item -ItemType Directory -Force -Path $outDir | Out-Null
Remove-Item $resultsXml, $perfJson, $harnessLog -ErrorAction SilentlyContinue

# ---- build + deploy --------------------------------------------------
if (-not $SkipBuild) {
    # Persistent build nodes can keep bin/ files open between runs (ILRepack overwrites in place).
    dotnet build-server shutdown 2>&1 | Out-Null
    Get-Process OxygenNotIncluded -ErrorAction SilentlyContinue | Stop-Process -Force

    Write-Host '==> building BlueprintsIncluded (Debug -> mods/dev)'
    dotnet build (Join-Path $repo 'BlueprintsIncluded.slnx') -c Debug --nologo
    if ($LASTEXITCODE) { throw 'mod build failed' }

    Write-Host '==> building harness (Debug -> mods/dev)'
    dotnet build (Join-Path $repo 'harness/BlueprintsIncludedHarness/BlueprintsIncludedHarness.csproj') -c Debug --nologo "-p:SolutionDir=$repo\"
    if ($LASTEXITCODE) { throw 'harness build failed' }
}

$harnessDst = Join-Path $modFolder 'BlueprintsIncludedHarness_dev'
if (-not (Test-Path (Join-Path $harnessDst 'BlueprintsIncludedHarness.dll'))) {
    throw "harness not deployed to $harnessDst - run without -SkipBuild"
}
Write-Host "==> harness deployed to $harnessDst"

# ---- mods.json: enable ours, disable the conflicting BlueprintsV2 ---
$modsJsonPatched = $false
if (-not $NoModConfig) {
    if (-not (Test-Path $modsJson)) {
        Write-Warning "mods.json not found at $modsJson - configure the mods by hand in ONI, then re-run with -NoModConfig -SkipBuild."
    } else {
        Copy-Item $modsJson $modsJsonBak -Force
        $j = Get-Content $modsJson -Raw | ConvertFrom-Json
        $mine = 'BlueprintsIncluded_dev', 'BlueprintsIncludedHarness_dev'   # main first
        $picked = foreach ($id in $mine) { $j.mods | Where-Object { $_.label.id -eq $id } | Select-Object -First 1 }
        if (@($picked).Count -ne 2) {
            Write-Warning "expected both dev mods in mods.json (found $(@($picked).Count)) - launch ONI once so it registers them, then re-run."
        } else {
            foreach ($m in $picked) { $m.enabled = $true; $m.enabledForDlc = @($Dlc) }
            # disable anything else claiming the BlueprintsV2 / BlueprintsIncluded staticID
            foreach ($m in $j.mods) {
                if (($mine -notcontains $m.label.id) -and ($m.staticID -in 'BlueprintsV2', 'BlueprintsIncluded')) {
                    $m.enabled = $false
                    Write-Host "==> mods.json: disabled conflicting '$($m.label.title)' ($($m.label.id))"
                }
            }
            $rest = $j.mods | Where-Object { $mine -notcontains $_.label.id }
            $j.mods = @($rest) + @($picked)   # ours last => loaded last, main before harness
            ($j | ConvertTo-Json -Depth 12) | Set-Content -Path $modsJson -Encoding UTF8
            $modsJsonPatched = $true
            Write-Host "==> mods.json: enabled BlueprintsIncluded + harness (dlc: $($Dlc -join ',')), harness after main"
        }
    }
}

$exitCode = 1
try {
    if (-not (Test-Path $fixtureSrc)) { throw "fixture save missing: $fixtureSrc  (see harness/fixtures/README.md)" }
    Copy-Item $fixtureSrc $fixtureDst -Force
    Write-Host "==> copied fixture to $fixtureDst"

    $mode = if ($Perf) { 'perf' } else { 'run' }
    Set-Content -Path $sentinel -Value @($mode, (Get-Date -Format o))
    Write-Host "==> wrote sentinel $sentinel (mode=$mode)"

    Write-Host '==> launching ONI (steam://run/457140)'
    Start-Process 'steam://run/457140'

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline -and -not (Test-Path $waitFor)) { Start-Sleep -Seconds 3 }

    if (-not (Test-Path $waitFor)) {
        Write-Host '==> killing ONI (no results before timeout)'
        Get-Process -Name 'OxygenNotIncluded' -ErrorAction SilentlyContinue | Stop-Process -Force
        if (Test-Path $harnessLog) { Write-Host '--- harness.log ---'; Get-Content $harnessLog }
        throw "harness did not produce $waitFor within ${TimeoutSeconds}s"
    }

    Start-Sleep -Seconds 5
    Get-Process -Name 'OxygenNotIncluded' -ErrorAction SilentlyContinue | Stop-Process -Force

    if ($Perf) {
        $report = Get-Content $perfJson -Raw | ConvertFrom-Json
        Write-Host ''
        Write-Host 'NOTE: allocKB reads ~0 on ONI''s embedded Mono - GC.GetAllocatedBytesForCurrentThread' -ForegroundColor DarkYellow
        Write-Host '      is not meaningfully implemented there. Time + hotspot call counts are reliable.' -ForegroundColor DarkYellow
        foreach ($op in $report.operations) {
            Write-Host "==> $($op.name)"
            "{0,8} {1,10} {2,10} {3,10} {4,12}" -f 'N', 'iters', 'medianMs', 'p95Ms', 'allocKB' | Write-Host
            foreach ($r in $op.results) {
                "{0,8} {1,10} {2,10:F2} {3,10:F2} {4,12:F1}" -f $r.n, $r.iterations, $r.medianMs, $r.p95Ms, ($r.meanAllocBytes / 1024.0) | Write-Host
            }
            Write-Host ''
        }
        Write-Host '==> hotspots'
        "{0,-22} {1,10} {2,10} {3,14}" -f 'method', 'calls', 'totalMs', 'avgUsPerCall' | Write-Host
        foreach ($h in $report.hotspots) {
            "{0,-22} {1,10} {2,10:F1} {3,14:F1}" -f $h.name, $h.totalCalls, $h.totalMs, $h.avgUsPerCall | Write-Host
        }
        if (Test-Path $harnessLog) { Write-Host ''; Write-Host '--- harness.log ---'; Get-Content $harnessLog }
        $exitCode = 0   # a benchmark run has no pass/fail - see docs §7
    } else {
        $suite = ([xml](Get-Content $resultsXml)).testsuite
        Write-Host ''
        Write-Host "==> $($suite.tests) case(s), $($suite.failures) failure(s)"
        foreach ($tc in $suite.testcase) {
            if ($tc.failure) {
                Write-Host ("  FAIL  {0}" -f $tc.name) -ForegroundColor Red
                Write-Host ("        {0}" -f ($tc.failure.'#text' -replace "`n", "`n        "))
            } else {
                Write-Host ("  PASS  {0}" -f $tc.name) -ForegroundColor Green
            }
        }
        if (Test-Path $harnessLog) { Write-Host ''; Write-Host '--- harness.log ---'; Get-Content $harnessLog }
        $exitCode = [int]$suite.failures
    }
}
finally {
    if (-not $KeepSentinel) { Remove-Item $sentinel -ErrorAction SilentlyContinue }
    if ($modsJsonPatched -and (Test-Path $modsJsonBak)) {
        Copy-Item $modsJsonBak $modsJson -Force
        Write-Host "==> restored mods.json"
    }
}

exit $exitCode
