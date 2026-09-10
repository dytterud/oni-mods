#!/usr/bin/env pwsh
<#
.SYNOPSIS
  Build + deploy BlueprintsIncluded and the dev-only in-game test harness, launch ONI, wait for
  the harness to write its JUnit results, print a summary and exit non-zero on any failure.

.DESCRIPTION
  Requires a real ONI install configured via Directory.Build.props.user (GameLibsFolder + ModFolder).
  See docs/in-game-regression-testing.md and harness/README.md.

  Patches mods.json for the run (restored afterward): enables BlueprintsIncluded + the harness in
  load order, and disables any other mod that shares the BlueprintsV2 staticID (e.g. the upstream
  "Blueprints Expanded") so its patches don't collide. -NoModConfig skips this - enable/disable and
  order the mods yourself in ONI's Mods screen and re-run with -SkipBuild.

  The harness only acts when it finds the sentinel this script writes, so leaving it in mods/dev
  between runs is harmless.
#>
[CmdletBinding()]
param(
    [int]$TimeoutSeconds = 300,
    [switch]$SkipBuild,
    [switch]$KeepSentinel,
    [switch]$NoModConfig,
    [string[]]$Dlc = @('', 'EXPANSION1_ID')
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot

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
$harnessLog   = Join-Path $outDir 'harness.log'
$modsJsonBak  = Join-Path $outDir 'mods.json.bak'
$fixtureSrc   = Join-Path $repo 'harness/fixtures/poc-colony.sav'
$fixtureDst   = Join-Path $outDir 'poc-colony.sav'   # harness loads this absolute path

New-Item -ItemType Directory -Force -Path $outDir | Out-Null
Remove-Item $resultsXml, $harnessLog -ErrorAction SilentlyContinue

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

    Set-Content -Path $sentinel -Value (Get-Date -Format o)
    Write-Host "==> wrote sentinel $sentinel"

    Write-Host '==> launching ONI (steam://run/457140)'
    Start-Process 'steam://run/457140'

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline -and -not (Test-Path $resultsXml)) { Start-Sleep -Seconds 3 }

    if (-not (Test-Path $resultsXml)) {
        Write-Host '==> killing ONI (no results before timeout)'
        Get-Process -Name 'OxygenNotIncluded' -ErrorAction SilentlyContinue | Stop-Process -Force
        if (Test-Path $harnessLog) { Write-Host '--- harness.log ---'; Get-Content $harnessLog }
        throw "harness did not produce $resultsXml within ${TimeoutSeconds}s"
    }

    Start-Sleep -Seconds 5
    Get-Process -Name 'OxygenNotIncluded' -ErrorAction SilentlyContinue | Stop-Process -Force

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
finally {
    if (-not $KeepSentinel) { Remove-Item $sentinel -ErrorAction SilentlyContinue }
    if ($modsJsonPatched -and (Test-Path $modsJsonBak)) {
        Copy-Item $modsJsonBak $modsJson -Force
        Write-Host "==> restored mods.json"
    }
}

exit $exitCode
