# In-game regression test harness (dev only)

A separate ONI mod (`staticID` **`BlueprintsIncludedHarness`**) that boots the game, loads a
committed fixture colony, runs assertions against the live blueprint pipeline — the colony-dependent
slice `dotnet test` structurally can't reach — writes JUnit XML, and quits.

Background and rationale: [`docs/in-game-regression-testing.md`](../docs/in-game-regression-testing.md).
This is the **proof-of-concept** stage: one assertion case (capture + JSON round-trip with real
`BuildingDef` resolution) plus an exception sweep.

## Not part of the normal build

- Listed in `BlueprintsIncluded.slnx` for the IDE, but **excluded from CLI solution builds**
  (`<Build … Project="false" />`) — `dotnet build`, `dotnet test` and CI never build it, and it
  can't race the mod's in-place ILRepack.
- Built and deployed only by [`test/run-ingame.ps1`](../test/run-ingame.ps1). Building the csproj
  by hand needs `-p:SolutionDir=<repo>\` (a solution build would supply it; the inherited
  assembly-publicizer path needs it).
- Compiles against the mod via a `ProjectReference` (isolated non-packed instance → compile-time
  coupling, a mod API change breaks the harness build). At runtime it resolves `BlueprintsV2.*`
  against the merged `BlueprintsIncluded.dll` that ONI loads as the main mod, so only
  `BlueprintsIncludedHarness.dll` is deployed.
- Dormant unless activated: it does nothing unless `%TEMP%\bpi-harness\run` exists (the launcher
  writes it) or `BPI_HARNESS=1` is set. Safe to leave enabled in `mods/dev`.

## Prerequisites

1. A real ONI install configured in `Directory.Build.props.user` (`GameLibsFolder` + `ModFolder`) —
   see the repo [README](../README.md).
2. `dotnet tool restore` (publicizer + refasmer), same as normal in-game dev.
3. A loadable colony committed at `harness/fixtures/poc-colony.sav` (there is one) — see
   [`fixtures/README.md`](fixtures/README.md). The harness places its own buildings into it, so it
   just has to load.

## Running

```
pwsh test/run-ingame.ps1
```

Builds + deploys both mods, copies the fixture to `%TEMP%\bpi-harness\poc-colony.sav` (the harness
loads that absolute path), writes the sentinel, launches ONI via Steam, polls
`%TEMP%\bpi-harness\results.xml` (5 min default timeout, `-TimeoutSeconds` to change), kills the game
if it hangs, prints a PASS/FAIL summary, exits non-zero on any failure.

Artifacts in `%TEMP%\bpi-harness\`: `results.xml` (JUnit), `harness.log` (plain text trace).

The ONI window appears during the run (it self-quits on completion). A screenshot is only needed to
diagnose a hang.

## Layout

| File | Role |
|---|---|
| `HarnessMod.cs` | `UserMod2` entry, activation gate, `MainMenu.OnSpawn` hook |
| `HarnessRunner.cs` | `DontDestroyOnLoad` MonoBehaviour: load fixture → wait for sim → place buildings → run cases → write results → quit |
| `HarnessCases.cs` | the assertion cases: capture + JSON round-trip, place → build orders, rotation rotates the layout, priority data-transfer round-trip, element-note capture round-trip, exception sweep |
| `FixtureLayout.cs` | the building set to place + the capture rectangle (offsets from the Printing Pod) |
| `FixtureBuilder.cs` | places that set via `BuildingDef.Build` |
| `ExceptionSweep.cs` | fails the run on any mod-related Error/Exception log frame |
| `Assert.cs` / `JUnitWriter.cs` | tiny assertion + report helpers |

## Adding a case

Add a `HarnessCase` to `HarnessCases.All` — the body is a `Func<IEnumerator>`, so it can `yield
return null` to let frames pass (needed for placement / sim settling). Throw (any exception,
`HarnessAssertException` for assertions) on failure; return normally on pass. `PlaceAt` is a shared
helper that digs a target, drops the mod's tech/material gates, drives
VisualizeBlueprint → [rotate] → UseBlueprint, and hands back the new build orders.

The §3 case table from [`docs/in-game-regression-testing.md`](../docs/in-game-regression-testing.md)
is covered. Natural extensions: more building types / layers, place-with-settings applied to the
built object, replacement visualizers over occupied terrain, the §7 perf mode.
