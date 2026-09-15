# In-game regression test harness (dev only)

A separate ONI mod (`staticID` **`BlueprintsIncludedHarness`**) that boots the game, loads a
committed fixture colony, and either runs assertions against the live blueprint pipeline — the
colony-dependent slice `dotnet test` structurally can't reach — or (in perf mode) times parts of
that pipeline. Writes JUnit XML or `perf.json`, then quits.

Background and rationale: [`docs/blueprints-included/in-game-regression-testing.md`](../docs/blueprints-included/in-game-regression-testing.md).

## Not part of the normal build

- Listed in `OniMods.slnx` for the IDE, but **excluded from CLI solution builds**
  (`<Build … Project="false" />`) — `dotnet build`, `dotnet test` and CI never build it, and it
  can't race the mod's in-place ILRepack.
- Built and deployed only by [`test/run-ingame.ps1`](../test/run-ingame.ps1). Building the csproj
  by hand needs `-p:SolutionDir=<repo>\` (a solution build would supply it; the inherited
  assembly-publicizer path needs it) — omit it and you get
  `MSB3191: Unable to create directory "*Undefined*/PublicisedAssembly"`, which looks like a path
  bug but is just the missing property — and the mod must already be built (`<Reference>` points at
  `src/BlueprintsIncluded/bin/Debug/netstandard2.1/BlueprintsIncluded.dll` directly, not a
  `ProjectReference` — a second build instance of the mod corrupts its incremental state and races
  its in-place ILRepack). At runtime it resolves `BlueprintsV2.*` against the merged
  `BlueprintsIncluded.dll` that ONI loads as the main mod, so only `BlueprintsIncludedHarness.dll`
  is deployed.
- Dormant unless activated: it does nothing unless `%TEMP%\bpi-harness\run` exists (the launcher
  writes it) or `BPI_HARNESS=1`/`perf` is set. Safe to leave enabled in `mods/dev`.

> **If a second mod ever needs an in-game harness:** the reusable half is `HarnessMod.cs` (the gate)
> and `HarnessRunner.cs`, `Assert.cs`, `JUnitWriter.cs`, `Screenshot.cs` and `Perf/AllocProbe.cs` /
> `PerfInstrumentation.cs`. The Blueprints-specific half is `HarnessCases.cs`, `FixtureBuilder.cs`,
> `FixtureLayout.cs`, `ExceptionSweep.cs` and the rest of `Perf/`. Splitting them is deliberately
> deferred until there is a second consumer to design against — one mod's harness is not a
> framework.

## Prerequisites

1. A real ONI install configured in `Directory.Build.props.user` (`GameLibsFolder` + `ModFolder`) —
   see the repo [README](../README.md).
2. `dotnet tool restore` (publicizer + refasmer), same as normal in-game dev.
3. A loadable colony committed at `harness/fixtures/poc-colony.sav` (there is one) — see
   [`fixtures/README.md`](fixtures/README.md). The harness places its own buildings into it, so it
   just has to load.

## Running

```
pwsh test/run-ingame.ps1        # or: powershell test/run-ingame.ps1
```

Use `powershell` (Windows PowerShell) if `pwsh` isn't on PATH — the script runs under both.

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

```
pwsh test/run-ingame.ps1 -Attribution
```

Implies `-Perf` and additionally instruments the per-visual methods of the per-frame update path
(sentinel mode `perf-attribution`). Those Harmony wrappers cost more than the methods they measure,
so such a run is honest about **call counts and allocation** but inflates every timing in it: read
its hotspot table, and never compare its medians with a run without it.

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
| `Perf/PerfInstrumentation.cs` | generic manual-Harmony-patch registry (patch by name, one prefix/postfix pair) for call-count + cumulative-time + allocated-bytes hotspot attribution — import (`GetValidMaterials`/`SanitizeSelectedTags`), visualize (`TileVisual` ctor, `KInstantiate`, tile-block registration, coloring), use (`TryUse`/`IsPlaceable`/`PlacePlannedBuilding`, `BuildingDef.Instantiate`, `ApplyBuildingData`, `UpdateConduitConnectionBits`), and create (`StoreAdditionalBuildingData`/`GetAdditionalBuildingData`, `NaturalBuildingCell`) targets |
| `Perf/AllocProbe.cs` | the allocation counters that work on ONI's Mono (`GC.GetTotalMemory` + Unity's native `Profiler` total), with a gen-0 collection guard - see docs §7 |
| `Perf/PerfWriter.cs` | writes `perf.json` |

## Adding a regression case

Add a `HarnessCase` to `HarnessCases.All` — the body is a `Func<IEnumerator>`, so it can `yield
return null` to let frames pass (needed for placement / sim settling). Throw (any exception,
`HarnessAssertException` for assertions) on failure; return normally on pass. `PlaceAt` is a shared
helper that digs a target, drops the mod's tech/material gates, drives
VisualizeBlueprint → [rotate] → UseBlueprint, and hands back the new build orders.

The §3 case table from [`docs/blueprints-included/in-game-regression-testing.md`](../docs/blueprints-included/in-game-regression-testing.md)
is covered. Natural extensions: more building types / layers, place-with-settings applied to the
built object, replacement visualizers over occupied terrain.

**Six things worth knowing before writing a case**, most of which cost a run, found while adding
the replacement-vis and scheduled-path cases. None of them fail loudly, which is what makes them
expensive:

- **The sim is paused for the whole regression run**, so `GameScheduler` callbacks never fire.
  Measured, not inferred: `simPaused=True, timeScale=0, GameScheduler fired=False, UIScheduler
  fired=True`. Nothing in the harness pauses it — the perf run's explicit
  `SpeedControlScreen.Instance.Pause` is perf-only; the game simply loads paused. `UIScheduler`
  runs on real time and does fire, so the cause is game time being stopped rather than the
  scheduling machinery. Four paths in this mod ride on it: `ReplacementVis` seating, the delayed
  settings application in `DataTransferPatches`, `ReconstructablePatches`, and
  `UnderConstructionDataSettingHelper`. `SchedulerProbe` in `HarnessCases.cs` is the check.

  **To let scheduled work land, yield `WithSimRunning` — and note the lever is `Time.timeScale`,
  not the speed UI.** Measured with the harness driving: `SpeedControlScreen.Unpause(false)` and
  `SetSpeed(0)` are both no-ops, `IsPaused` stays true and `timeScale` stays 0. Since
  `GameScheduler` ticks on scaled time, setting `timeScale` directly is enough. The speed screen
  goes on reporting paused throughout, so assert on `timeScale` rather than `IsPaused` when
  checking the clock was put back. Keep the window short and restore it in a `finally`: with time
  running, dupes act and temperatures move, and every later case inherits that.

  **The scene partitioner is not affected**, and that distinction matters when writing a case:
  it fires on grid writes rather than game time, so a `GameScenePartitioner` callback still
  arrives when something in its extents changes. In `replacement-vis-places-once-per-cell` the
  blocked vis retries entirely on its own the moment the obstruction is deconstructed — no
  hand-delivered kick needed. Expect real callbacks from grid changes; expect nothing from the
  clock.
- **`OnSpawn` lands a frame or two after `SetActive`, not inside it.** Read a `KMonoBehaviour`'s
  state straight after activating it and you read it before `OnSpawn` has run. `SeatedVisSpawn`
  exists so this cannot be got wrong for replacement visualizers: yield it, then read `.Vis`.
- **Solid rock does not block a placement.** ONI queues a build order inside rock quite
  happily and lets a dupe dig it out, so `BuildingDef.TryPlace` succeeds there. To make a
  placement fail on purpose, occupy the cell with a finished building on the same object
  layer instead.
- **Nothing a dupe would have to carry actually happens.** No material gets delivered, so a
  reconstruct never goes ahead — `TryCommenceReconstruct` leaves the original building in the
  cell and no replacement plan appears. `reconstruct-reapplies-stored-settings` works anyway
  because the mod patches it with a *prefix*: the store-and-schedule runs either way. When a
  case needs an effect that normally waits on a dupe, look for a seam that does not.
- **`Constructable.OnCompleteWork(null)` does not finish a build.** It is the work callback;
  nothing completes, with the clock running or stopped. `Constructable.FinishConstruction(
  UtilityConnections, WorkerBase)` — non-public, so reached by reflection — is the step that
  swaps the plan for the finished building and fires the gameplay event the mod listens for.
  `completed-construction-applies-stored-settings` builds its argument list from
  `GetParameters()` rather than pinning that signature. (`Deconstructable.OnCompleteWork(null)`
  *does* work, which is what makes this asymmetry easy to walk into.)
- **An exception inside a nested `yield return`-ed `IEnumerator` used to kill the whole run.**
  Fixed: `HarnessRunner` now drives nested enumerators on its own stack instead of handing them
  to Unity, so a throw anywhere in a case's call tree fails *that case* — reported with the
  helper's own stack frame — and the rest of the suite still runs. Worth knowing because the old
  symptom told you nothing: no `results.xml`, and `run-ingame.ps1` sitting out its 300 s timeout
  in silence.

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

**Picking `TryPatchAllOverloads` vs `TryPatchEachOverload`:** the former puts every overload on one
shared accumulator, which is fine only if the overloads never call each other. If they do forward
(as `GameUtil.KInstantiate`'s do), a single logical call gets timed twice — once in the outer
overload, once in the inner — inflating both the total and the call count. That's how
`KInstantiate` came to be reported as ~90% of `visualize`'s cost when the real figure is ~60% (see
docs §7). Prefer `TryPatchEachOverload` when unsure: it names each accumulator with the overload's
signature, so forwarding shows up as two lines with identical call counts instead of hiding inside
one inflated number. A call count that doesn't match what you expect from the sweep arithmetic is
the tell — check it before trusting any hotspot figure, especially one you're about to base a
refactor decision on.
