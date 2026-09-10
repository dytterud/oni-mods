# Feasibility: automated in-game regression tests

**Status:** built and passing — see [`harness/`](../harness/README.md) and
[`test/run-ingame.ps1`](../test/run-ingame.ps1). This doc records why it was built and how it works.
§3 below is the original design sketch; the harness now covers that whole case table. §7 (perf mode)
is also built, scoped so far to blueprint import. Decisions are in [§8](#8-decision-checklist).

**Implemented:** separate dev-only harness mod (`staticID` `BlueprintsIncludedHarness`, in the
`.slnx` for the IDE but excluded from CLI builds), activation via sentinel file / `BPI_HARNESS` env
var, a `MainMenu.OnSpawn` → `LoadScreen.DoLoad` bootstrap that loads a committed fixture colony and
places a known building set in code, and six regression cases: capture + JSON round-trip
**including** `BuildingConfigurations` (the slice `dotnet test` can't reach), place → `Constructable`
build orders, blueprint rotation rotates the placed layout, a non-default `Prioritizable` priority
round-trips byte-identically, an element-note capture round-trips, and a mod-wide exception sweep.
Plus a perf mode (§7) and the PowerShell launcher (builds, deploys, patches `mods.json` for the run,
launches ONI, polls, reports JUnit or the perf table). Verified green against a real install.

## 1. Summary & recommendation

Launching ONI and running assertions against the live blueprint pipeline is **feasible on a
developer machine** and not feasible in headless CI. ONI ships the Unity **Mono** player
(game build 737790, ~Unity 2020.3); there is no usable `-batchmode -nographics` path and no
command-line switch to load a save or select a mod, so it needs a real GPU + display and
all control must come from inside a mod.

It is only worth automating the slice that genuinely needs a colony —
`Grid` / `Assets.GetBuildingDef` / scene singletons / the native sim. Everything else is
already reachable from `dotnet test` (see [§6](#6-cheaper-complementary-option)).

**Recommendation:** if this gets built, use a **separate dev-only harness mod** in the repo
+ a **committed fixture save** + a **PowerShell launcher**. Start with a proof-of-concept
(load fixture → one capture + round-trip assertion → exception sweep → quit), then expand.
Run it locally as a pre-release gate. Never wire it into CI.

## 2. Why this can't be a normal `dotnet test`

The committed `lib/*.dll` are refasmer **reference assemblies** — they compile but their
method bodies throw. `GameAssemblies.Probe()` in
[test/BlueprintsIncluded.Tests/GameAssemblies.cs](../test/BlueprintsIncluded.Tests/GameAssemblies.cs)
detects this and skips every `[RequiresGameInstall*]` test unless a real install is
configured.

Even with a real install's executable *publicised* DLLs on disk, the pipeline core stays
out of reach in a plain test process:

- [`BlueprintState.CreateBlueprint`](../src/BlueprintsIncluded/BlueprintData/BlueprintState.cs)
  walks `Grid.Objects` / `Grid.Element` / `Grid.Mass` / utility network managers — all
  populated only by a loaded colony + running sim.
- `BuildingConfig.WriteJson`
  ([BuildingConfig.cs](../src/BlueprintsIncluded/BlueprintData/BuildingConfig.cs))
  early-returns unless `Assets.GetBuildingDef` resolves, which needs the building database
  built during colony load. This is exactly why
  [BlueprintRoundTripTests.cs](../test/BlueprintsIncluded.Tests/BlueprintData/BlueprintRoundTripTests.cs)
  round-trips names / notes / dig locations / metadata but **not** `BuildingConfigurations`.
- The visualizers instantiate `GameObject`s and query `Grid` per frame — pure Unity scene
  work.

So the only way to exercise capture → serialize-with-real-defs → place → data-transfer is
inside a booted player with a colony loaded. See also [test/README.md](../test/README.md)
and [smoke-test-checklist.md](smoke-test-checklist.md).

## 3. Proposed architecture

Description, not implementation.

### Harness mod

A new dev-only project in the repo (e.g. `test/InGameHarness/`, assembly / `staticID`
`BlueprintsIncludedHarness`). It is a `UserMod2` that references `BlueprintsIncluded` types
directly (project reference or a path reference to the built dll).

- **Never** built in Release, never written to `Builds/` or deployed to Steam. Only an
  explicit `dotnet build -c Debug` copies it into `$(ModFolder)` next to the mod, reusing
  the existing `CopyModsToDevFolder` target and `Directory.Build.props.user` config from
  [README.md](../README.md).
- Because it references real mod types, an API change in `BlueprintsIncluded` breaks the
  harness at **compile time** rather than silently rotting — a feature.

### Activation gate

The harness does nothing unless a signal is present, so it is harmless if left enabled in
`mods/dev`:

- env var `BPI_HARNESS=1`, or
- a sentinel file at a known path (e.g. `%TEMP%/bpi-harness/run`).

### Colony bootstrap

Harmony postfix on a `MainMenu` spawn/update method. When activated:

1. Load a **committed fixture save** — a tiny sandbox colony containing a known, fixed set
   of buildings — via `SaveLoader` / `LoadScreen.DoLoad`. (Most deterministic.)
2. Fallback: drive `NewGameFlow` with a fixed seed + minimal settings.
3. Wait for `Game.Instance` + `Grid` ready, then settle N sim frames before asserting.

### Test cases

Plain methods, each wrapped in `try/catch`, collecting `{name, passed, message}`:

| Case | What it does | Asserts |
|---|---|---|
| Capture | `BlueprintState.CreateBlueprint(topLeft, bottomRight, null)` over the fixture's known area | `BuildingConfigurations` count, cell positions, orientations, selected elements |
| Round-trip w/ real defs | `Blueprint.WriteJsonString` → `new Blueprint(sb)` | `BuildingConfigurations` equal before/after (the part unit tests can't reach) |
| Place | place on empty ground; rotate through 4 orientations | `Constructable` build orders + dig commands at expected cells; orders rotate |
| Notes | text + element note capture/restore against real `SandboxToolParameterMenu` | all note fields restored |
| Data transfer | set a building setting in fixture → capture → place | setting applied to the placed build order |
| Exception sweep | subscribe `Application.logMessageReceived` for the whole run | zero Error/Exception frames mentioning `BlueprintsV2` / `BlueprintsIncluded` |

### Output & teardown

Write JUnit XML + a plain-text log to `%TEMP%/bpi-harness/results.xml`, then
`Application.Quit()`.

### PowerShell launcher — `test/run-ingame.ps1`

1. `dotnet build -c Debug` (mod + harness).
2. Deploy both to `$(ModFolder)`; write the activation sentinel; copy the fixture save into
   the ONI saves folder.
3. Launch ONI (`steam://run/457140`, or the exe directly with Steam already running).
4. Poll `results.xml` with a ~5 min timeout; kill the process if it hangs.
5. Parse XML → print a summary → exit non-zero on any failure.
6. Clear the sentinel.

### Claude's role

Run `test/run-ingame.ps1`, poll the result file, report. The game window appears (can be
minimized); the run is unattended once launched. A screenshot via computer-use is only
needed to diagnose a hang.

## 4. Effort

| Scope | Estimate |
|---|---|
| POC: load fixture, one capture + round-trip assertion, exception sweep, quit, launcher | ~1 day |
| Working harness: 3–5 assertions + launcher + fixture + docs | ~2–4 focused days |

## 5. Risks & mitigations

- **Save / new-game automation is timing-sensitive** (screen lifecycle). → Study existing
  ONI dev/test mods (DebugConsole, FastTrack, sandbox tooling) for load entry points;
  prefer fixture-save load over the new-game flow.
- **Fixture save breaks on ONI updates.** → Keep a documented regen procedure; keep the
  new-game-with-seed fallback; pin `minimumSupportedBuild`.
- **World-gen / sim determinism.** → Fixed seed, fixture save, settle N sim frames before
  asserting.
- **Not headless.** → Cannot run in cloud CI. Accepted; local-only.
- **Steam dependency, first-run / EULA / DLC-selection dialogs.** → Document a one-time
  manual setup pass.

## 6. Cheaper complementary option

Before or alongside the harness, widen the `[RequiresGameInstall*]` tests that already run
in `dotnet test` against a real install's executable publicised DLLs — anything that does
**not** need a live `Grid` / `Assets`: more serialization shapes, data-transfer JSON
shaping, note round-trips, reflection checks. This needs no new infrastructure. The in-game
harness is only for the colony-dependent remainder (real `BuildingDef` resolution, actual
placement, sim interaction).

## 7. Performance measurement (separate mode)

Not part of the regression pass — a **pass/fail run and a benchmark run want different
things** (fast and deterministic vs. many warmed-up iterations). The harness mod carries an
opt-in profiling mode, useful **while actively working on a performance change** to confirm
it helped and didn't regress allocations.

**Built so far: blueprint import.** Activated by the sentinel's first line (`perf` instead of
`run`) or `BPI_HARNESS=perf` — see `HarnessGate.Mode` in
[HarnessMod.cs](../harness/BlueprintsIncludedHarness/HarnessMod.cs). `test/run-ingame.ps1 -Perf`
drives it. Instead of asserting, `PerfRunner`
([harness/.../Perf/PerfRunner.cs](../harness/BlueprintsIncludedHarness/Perf/PerfRunner.cs)):

1. Loads the fixture save (unchanged bootstrap), **pauses the sim**
   (`SpeedControlScreen.Instance.Pause`).
2. Builds an N-building blueprint in code
   ([SyntheticBlueprint.cs](../harness/BlueprintsIncludedHarness/Perf/SyntheticBlueprint.cs) —
   single-ingredient raw-mineral `Tile`s, so nothing gets re-sanitized) for
   N = 100 / 500 / 1000 / 5000, and times two operations against its JSON: `deserialize`
   (`new Blueprint(sb)`) and `full-import` (the clipboard-minus-clipboard path,
   `ModAssets.TryImportBlueprintFromString`, reflected since it's `internal`), each with a
   warmup batch then timed iterations.
3. Records per iteration: `Stopwatch` elapsed and `GC.GetAllocatedBytesForCurrentThread()`
   delta — **the latter reads ~0 on ONI's embedded Mono regardless of N**, confirmed even at
   N=5000 where real allocation is unquestionably in the hundreds of KB. Not implemented
   meaningfully on this runtime; time and call counts are reliable, allocation numbers are not
   (left in for a future Unity/Mono upgrade).
4. Also Harmony-patches `BuildingConfig.SanitizeSelectedTags` and `ModAssets.GetValidMaterials`
   ([PerfInstrumentation.cs](../harness/BlueprintsIncludedHarness/Perf/PerfInstrumentation.cs))
   to count calls + accumulate time, so the result directly attributes cost instead of leaving
   it to inference.
5. Writes median / p95 / alloc-per-op plus the hotspot totals to `%TEMP%/bpi-harness/perf.json`
   ([PerfWriter.cs](../harness/BlueprintsIncludedHarness/Perf/PerfWriter.cs)). No committed
   baseline / regression-diff yet — this is investigation, not a gate.

**Finding (one real run, one machine — see limits below):** `GetValidMaterials` — an uncached
scan of `ElementLoader.elements` plus a `List<Tag>` alloc and an `OrderBy` sort, called once per
building per ingredient from `SanitizeSelectedTags` — accounts for essentially **all** of
`SanitizeSelectedTags`'s cost (both had ~178k calls / ~15.1s total in one sweep of all four
sizes) and thus the large majority of import time: `deserialize` scaled from 9.9 ms at N=100 to
505 ms at N=5000 (~85–100 µs per building, consistent with the ~85 µs/call `GetValidMaterials`
average), `full-import` roughly double that at each N because it parses the document twice (see
§2). Scaling is linear in N, not quadratic — but a large, easily-cacheable per-call constant
dominates a 5000-building import. The natural fix, not yet made: cache `GetValidMaterials`'
result per category tag (it depends only on the static element table + disabled state, not on
the blueprint being read).

**Limits — treat output as directional, not absolute:**

- Not headless: GPU / vsync / background load add noise. Only medians over many iterations
  mean anything; sub-10% deltas are lost.
- Numbers are machine-specific — a baseline is only valid on the machine that produced it,
  never across machines or CI.
- Allocation tracking (`GC.GetAllocatedBytesForCurrentThread`) does not work on ONI's Mono —
  see above.

**Complement — for CPU-only paths, prefer BenchmarkDotNet** in a test project against a
real install's executable publicised DLLs (no game launch, portable, rigorous). That covers
the serialization layer cleanly; the in-game perf mode is for placement / capture /
visualizer work that needs a live `Grid`.

## 8. Decision checklist

- [x] Build the POC? — yes, `harness/`.
- [x] Commit a fixture save to the repo, or rely on new-game-with-seed? — **committed fixture save**
      (`harness/fixtures/poc-colony.sav`); new-game fallback deferred.
- [x] Add `test/run-ingame.ps1`? — yes.
- [x] Activation gate: env var, sentinel file, or both? — **both** (`%TEMP%/bpi-harness/run` sentinel
      written by the launcher, or `BPI_HARNESS=1`).
- [x] Separate harness mod vs. flag-gated code inside `BlueprintsIncluded`? — **separate mod**,
      compiled against the already-built merged `BlueprintsIncluded.dll` via a plain `<Reference>`
      (not `ProjectReference` — that double-builds the mod and corrupts its incremental state).
      Listed in `BlueprintsIncluded.slnx` for the IDE but excluded from CLI solution builds
      (`<Build … Project="false" />`) so it never races the mod's in-place ILRepack; built directly
      by `test/run-ingame.ps1`, which builds the mod first so the referenced DLL is current.
- [x] Include the [§7](#7-performance-measurement-separate-mode) perf mode, or add it later? — **built**,
      scoped to blueprint import (`test/run-ingame.ps1 -Perf`); placement/creation perf still open.

### Next steps

- [x] Commit a loadable `harness/fixtures/poc-colony.sav` (fresh `Cesspool` sandbox). The harness
      places its own building set (`FixtureLayout` / `FixtureBuilder`) rather than baking buildings
      into the save.
- [x] Run `test/run-ingame.ps1` end-to-end against a real install — green.
- [x] Place case: capture → `VisualizeBlueprint`/`UseBlueprint` → assert `Constructable` build
      orders for the right buildings at the right cells (mod tech/material requirements toggled off
      for the run since a cycle-0 colony has neither).
- [x] Rotation case: rotating the blueprint flips the placed layout (row → column).
- [x] Data-transfer case: a non-default `Prioritizable` priority is captured into
      `BuildingConfig.AdditionalBuildingData` and is byte-identical after the JSON round-trip.
- [x] Notes case: place an `ElementNote` entity → capture with the notes filter forced on →
      `WorldNotes` holds an Oxygen element note that round-trips through the JSON format.
- [x] Perf mode, blueprint import: size sweep (100/500/1000/5000) timing `deserialize` and
      `full-import`, plus `GetValidMaterials`/`SanitizeSelectedTags` call-count + time
      instrumentation. Finding: `GetValidMaterials`'s uncached element-table scan accounts for
      essentially all import cost; see §7.
- [ ] Perf mode follow-ups (the user's stated next steps): placement of large blueprints, creation
      of large blueprints (`CreateBlueprint` over a big captured area).
- [ ] Optional follow-ups: more building types / layers, place-with-settings applied to the built
      object, replacement visualizers over occupied terrain, a committed perf baseline + diff.
- [ ] `run-ingame.ps1` currently removes the dev `Blueprints Expanded` (`mods/dev/BlueprintsV2`)
      from `mods.json` when it's present alongside; ONI re-adds it disabled on next launch, but a
      cleaner disable-in-place would avoid the churn.
