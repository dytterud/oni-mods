# In-game regression test harness (dev only)

A separate ONI mod (`staticID` **`BlueprintsIncludedHarness`**) that boots the game, loads a
committed fixture colony, and either runs assertions against the live blueprint pipeline — the
colony-dependent slice `dotnet test` structurally can't reach — or (in perf mode) times parts of
that pipeline. Writes JUnit XML or `perf.json`, then quits.

Background and rationale: [`docs/in-game-regression-testing.md`](../docs/in-game-regression-testing.md).

## Not part of the normal build

- Listed in `BlueprintsIncluded.slnx` for the IDE, but **excluded from CLI solution builds**
  (`<Build … Project="false" />`) — `dotnet build`, `dotnet test` and CI never build it, and it
  can't race the mod's in-place ILRepack.
- Built and deployed only by [`test/run-ingame.ps1`](../test/run-ingame.ps1). Building the csproj
  by hand needs `-p:SolutionDir=<repo>\` (a solution build would supply it; the inherited
  assembly-publicizer path needs it) and the mod must already be built (`<Reference>` points at
  `src/BlueprintsIncluded/bin/Debug/netstandard2.1/BlueprintsIncluded.dll` directly, not a
  `ProjectReference` — a second build instance of the mod corrupts its incremental state and races
  its in-place ILRepack). At runtime it resolves `BlueprintsV2.*` against the merged
  `BlueprintsIncluded.dll` that ONI loads as the main mod, so only `BlueprintsIncludedHarness.dll`
  is deployed.
- Dormant unless activated: it does nothing unless `%TEMP%\bpi-harness\run` exists (the launcher
  writes it) or `BPI_HARNESS=1`/`perf` is set. Safe to leave enabled in `mods/dev`.

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

```
pwsh test/run-ingame.ps1 -Perf
```

Same build/deploy/launch, but runs the perf benchmarks instead (docs §7) — polls
`%TEMP%\bpi-harness\perf.json`, prints a size-sweep table + hotspot totals, always exits 0 (a
benchmark run has no pass/fail). Default timeout is longer (900s) since the size sweep takes a while.

Artifacts in `%TEMP%\bpi-harness\`: `results.xml` (JUnit) or `perf.json`, `harness.log` (plain text
trace either way).

The ONI window appears during the run (it self-quits on completion). A screenshot is only needed to
diagnose a hang.

## Layout

| File | Role |
|---|---|
| `HarnessMod.cs` | `UserMod2` entry, activation gate (`HarnessGate.Mode`: `Run`/`Perf`), `MainMenu.OnSpawn` hook |
| `HarnessRunner.cs` | `DontDestroyOnLoad` MonoBehaviour: load fixture → wait for sim → place buildings → run cases *or* `Perf.PerfRunner` → write results → quit |
| `HarnessCases.cs` | the regression cases: capture + JSON round-trip, place → build orders, rotation rotates the layout, priority data-transfer round-trip, element-note capture round-trip, exception sweep |
| `FixtureLayout.cs` | the building set to place + the capture rectangle (offsets from the Printing Pod) |
| `FixtureBuilder.cs` | places that set via `BuildingDef.Build` |
| `ExceptionSweep.cs` | fails a regression run on any mod-related Error/Exception log frame |
| `Assert.cs` / `JUnitWriter.cs` | tiny assertion + JUnit report helpers |
| `Perf/SyntheticBlueprint.cs` | builds an N-building blueprint in code - `Build` (in-memory, used by placement-perf) and `BuildJson` (serialized, used by import-perf) |
| `Perf/PerfRunner.cs` | warmup + timed iterations per (operation, size): `deserialize`/`full-import` (import), `visualize`/`use` (placement, real `GameObject`s + a once-dug-and-revealed region), `create` (`RunCreateSweep`, `CreateBlueprint` over real finished buildings) |
| `Perf/PerfInstrumentation.cs` | generic manual-Harmony-patch registry (patch by name, one prefix/postfix pair) for call-count + cumulative-time hotspot attribution — import (`GetValidMaterials`/`SanitizeSelectedTags`), visualize (`TileVisual` ctor, `KInstantiate`, tile-block registration, coloring), use (`TryUse`/`IsPlaceable`/`PlacePlannedBuilding`, `BuildingDef.Instantiate`, `ApplyBuildingData`, `UpdateConduitConnectionBits`), and create (`StoreAdditionalBuildingData`/`GetAdditionalBuildingData`, `NaturalBuildingCell`) targets |
| `Perf/PerfWriter.cs` | writes `perf.json` |

## Adding a regression case

Add a `HarnessCase` to `HarnessCases.All` — the body is a `Func<IEnumerator>`, so it can `yield
return null` to let frames pass (needed for placement / sim settling). Throw (any exception,
`HarnessAssertException` for assertions) on failure; return normally on pass. `PlaceAt` is a shared
helper that digs a target, drops the mod's tech/material gates, drives
VisualizeBlueprint → [rotate] → UseBlueprint, and hands back the new build orders.

The §3 case table from [`docs/in-game-regression-testing.md`](../docs/in-game-regression-testing.md)
is covered. Natural extensions: more building types / layers, place-with-settings applied to the
built object, replacement visualizers over occupied terrain.

## Adding a perf operation

Add a `TimeOp(...)` call inside `PerfRunner.Run` (or, for placement, inside
`RunPlacementSweep`) for the new operation. If it's a pure-data operation like import, extend
`SyntheticBlueprint.Build`/`BuildJson` — no live `Grid` needed. If it mutates real game state (like
placement's `use`), give `TimeOp`'s optional `setup` callback the job of moving to fresh input
before each timed call rather than trying to make the operation itself idempotent.

**Built:** import (`deserialize`/`full-import`, code-only), placement (`visualize`/`use`, a
once-dug-and-*revealed* region anchor-relative to the Printing Pod + real `GameObject`s via
`BlueprintState`), and creation (`create` — `BlueprintState.CreateBlueprint`, a pure read over a
rectangle of real finished buildings placed via `BuildingDef.Build` in a tight loop, the same call
`FixtureBuilder.PlaceAll` uses one at a time for the regression fixture).

Two real bugs turned up while getting `visualize`/`use`/`create` trustworthy, both worth knowing
before adding another region-based operation: **(1)** digging clears terrain but doesn't reveal fog
of war at a distance — `BuildingVisual.ValidCell` and `CreateBlueprint`'s capture both gate on
`Grid.IsVisible`, so the region needs an explicit `Grid.Reveal(cell, byte.MaxValue, forceReveal:
true)` after digging; **(2)** the default `BottomCenter` blueprint anchor shifts placement math in
`GetRotatedCell` by half the blueprint's width, and when that goes negative the linear cell index
wraps into the previous row at a huge x instead of failing cleanly — `PerfRunner` works around it
by flipping the private `BlueprintTransformationInfo._state` field to `BlueprintAnchorState.BottomLeft`
(shift 0,0) via reflection, since `VisualizeBlueprint`'s own `CheckPermittedRotations` call
resets the shift from `_state` on every redraw (so overriding the derived floats alone doesn't
stick). See docs §7 for the full story and numbers. A region-based perf operation should always add
a correctness check (a captured/created count vs. expected) rather than trusting the timer alone —
both bugs above produced plausible-looking timings while doing ~0–50% of the intended work.
