# Feasibility: automated in-game regression tests

**Status:** built and passing — see [`harness/`](../../harness/README.md) and
[`test/run-ingame.ps1`](../../test/run-ingame.ps1). This doc records why it was built and how it works.
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
[test/BlueprintsIncluded.Tests/GameAssemblies.cs](../../test/BlueprintsIncluded.Tests/GameAssemblies.cs)
detects this and skips every `[RequiresGameInstall*]` test unless a real install is
configured.

Even with a real install's executable *publicised* DLLs on disk, the pipeline core stays
out of reach in a plain test process:

- [`BlueprintState.CreateBlueprint`](../../src/BlueprintsIncluded/BlueprintData/BlueprintState.cs)
  walks `Grid.Objects` / `Grid.Element` / `Grid.Mass` / utility network managers — all
  populated only by a loaded colony + running sim.
- `BuildingConfig.WriteJson`
  ([BuildingConfig.cs](../../src/BlueprintsIncluded/BlueprintData/BuildingConfig.cs))
  early-returns unless `Assets.GetBuildingDef` resolves, which needs the building database
  built during colony load. This is exactly why
  [BlueprintRoundTripTests.cs](../../test/BlueprintsIncluded.Tests/BlueprintData/BlueprintRoundTripTests.cs)
  round-trips names / notes / dig locations / metadata but **not** `BuildingConfigurations`.
- The visualizers instantiate `GameObject`s and query `Grid` per frame — pure Unity scene
  work.

So the only way to exercise capture → serialize-with-real-defs → place → data-transfer is
inside a booted player with a colony loaded. See also [test/README.md](../../test/README.md)
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
  [README.md](../../README.md).
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
[HarnessMod.cs](../../harness/BlueprintsIncludedHarness/HarnessMod.cs). `test/run-ingame.ps1 -Perf`
drives it. Instead of asserting, `PerfRunner`
([harness/.../Perf/PerfRunner.cs](../../harness/BlueprintsIncludedHarness/Perf/PerfRunner.cs)):

1. Loads the fixture save (unchanged bootstrap), **pauses the sim**
   (`SpeedControlScreen.Instance.Pause`).
2. Builds an N-building blueprint in code
   ([SyntheticBlueprint.cs](../../harness/BlueprintsIncludedHarness/Perf/SyntheticBlueprint.cs) —
   single-ingredient raw-mineral `Tile`s, so nothing gets re-sanitized) for
   N = 100 / 500 / 1000 / 5000, and times two operations against its JSON: `deserialize`
   (`new Blueprint(sb)`) and `full-import` (the clipboard-minus-clipboard path,
   `ModAssets.TryImportBlueprintFromString`, reflected since it's `internal`), each with a
   warmup batch then timed iterations.
3. Records per iteration: `Stopwatch` elapsed plus two allocation counters and a GC guard
   ([AllocProbe.cs](../../harness/BlueprintsIncludedHarness/Perf/AllocProbe.cs)) — see
   **Allocation** below.
4. Also Harmony-patches `BuildingConfig.SanitizeSelectedTags` and `ModAssets.GetValidMaterials`
   ([PerfInstrumentation.cs](../../harness/BlueprintsIncludedHarness/Perf/PerfInstrumentation.cs))
   to count calls + accumulate time, so the result directly attributes cost instead of leaving
   it to inference.
5. Writes median / p95 / alloc-per-op plus the hotspot totals to `%TEMP%/bpi-harness/perf.json`
   ([PerfWriter.cs](../../harness/BlueprintsIncludedHarness/Perf/PerfWriter.cs)). No committed
   baseline / regression-diff yet — this is investigation, not a gate.

### Allocation

`GC.GetAllocatedBytesForCurrentThread()` — what this mode used to record — reads **0 at every N**
under Mono's Boehm GC, so for a long time the harness measured time only. Two counters that do work
replaced it (measured over a full sweep, 2026-09-11):

| counter | verdict |
|---|---|
| `GC.GetTotalMemory(false)` | **live**, scales with N on every sweep — the managed-heap number |
| `Profiler.GetTotalAllocatedMemoryLong()` | **live**, and the only view of Unity *native* memory |
| `Profiler.GetMonoUsedSizeLong()` | byte-for-byte identical to `GC.GetTotalMemory` in every row — **dropped as redundant** |
| `GC.GetAllocatedBytesForCurrentThread()` | 0.0 in every row — **dropped** |

The two survivors answer different questions and both are needed: the managed counter reads ~0 for
`use` and the dialog's cold opens (their cost is `GameObject`s, which never touch the managed heap),
while the native counter reads 0 for `deserialize` / `full-import` / `update-visual` (pure managed
work). A zero in one column is a finding, not a dead counter.

Sample numbers (medians, this machine, two consecutive runs):

| op | managed | native |
|---|---:|---:|
| `deserialize` N=5000 | 36.6 MB | 0 |
| `full-import` N=5000 | 67.7 MB | 0 |
| `update-visual-tile` N=2000 (per frame) | 2 388 KB | 0 |
| `update-visual-ladder` N=1000 (per frame) | 252 KB | 0 |
| `use` N=1000 | 7.5 MB | 4.9 MB |
| `create` N=1000 | 900 KB | 0 |
| `open-list-cold` L=500 | 16.9 MB | 15.8 MB |
| `idle-frame` (3 frames, noise floor) | 16 KB | 2.9 KB |

Repeatability is much better than the timings': the per-frame and native figures reproduced
**byte-for-byte** across both runs (`update-visual-tile` N=2000 2 388 KB, `use` N=1000 native
4 903.4 KB), and the big import figures within ~6%. Small-N rows are the noisy ones — `deserialize`
N=100 read 620 KB then 144 KB — so read allocation at the large end of a sweep, where the signal is
well clear of the floor.

**Reading the numbers:**

- They are **heap deltas, not an allocation counter** — a collection inside the measured body makes
  one meaningless (possibly negative). Each iteration therefore also brackets
  `GC.CollectionCount(0)`, and `AllocStats` medians only the iterations where it didn't move,
  reporting the rest as the `gc0` column. **`gc0 = n/n` means nothing could be excluded and that
  row is noise** — the big `open-preview-*-fresh` rows routinely hit that.
- No forced `GC.Collect` between iterations: cleaner deltas, but it would change the heap state each
  body sees and make the timings incomparable with the baselines above. The collection count is the
  guard instead.
- `idle-frame` is the floor: the paused game allocates ~16 KB managed / ~2.9 KB native over the same
  three frames with no harness work in them. A delta near that is not a measurement.
- `-settled` rows deliberately carry no allocation (`-` in the table): the probe closes with the
  synchronous timer, so the settle frames' unrelated game work isn't charged to the op.

**Finding + fix (one real run, one machine, before/after — see limits below):** `GetValidMaterials`
— an uncached scan of `ElementLoader.elements` plus a `List<Tag>` alloc and an `OrderBy` sort,
called once per building per ingredient from `SanitizeSelectedTags` — accounted for essentially
**all** of `SanitizeSelectedTags`'s cost and thus the large majority of import time. Fixed by
caching its result per `(category tag, omitDisabledElements)` — the inputs (the element table,
each element's disabled state, which prefabs carry a given `GameTags.MaterialBuildingElements`
tag) are all established once when the game's databases load and don't change for the life of the
process, so the cache never needs invalidating (`ModAssets.ValidMaterialsCache`).

| | before | after | |
|---|---:|---:|---|
| `GetValidMaterials` | 84.7 µs/call | 0.1 µs/call | ~850× |
| `SanitizeSelectedTags` | 85.2 µs/call | 0.5 µs/call | ~170× |
| `deserialize` N=100 | 9.9 ms | 1.8 ms | ~5.5× |
| `deserialize` N=5000 | 504.7 ms | 84.8 ms | ~5.9× |
| `full-import` N=5000 | 1014.9 ms | 195.9 ms | ~5.2× |

(both runs: 178,200 total `GetValidMaterials`/`SanitizeSelectedTags` calls across the full size
sweep). Scaling was already linear in N, not quadratic — the win is entirely from removing a large
per-call constant. Regression suite re-verified 6/6 green after the change (the cache doesn't
alter `SanitizeSelectedTags`'s decisions, only how fast it makes them). Remaining ~85 ms at
N=5000 is the JSON parse + object graph construction itself — the next thing to profile if this
matters again.

**Limits — treat output as directional, not absolute:**

- Not headless: GPU / vsync / background load add noise. Only medians over many iterations
  mean anything; sub-10% deltas are lost.
- Numbers are machine-specific — a baseline is only valid on the machine that produced it,
  never across machines or CI.
- Allocation is measured as heap deltas, not by an allocation counter — check the `gc0` column
  before believing a row, and ignore anything near the `idle-frame` floor (see **Allocation**).

**Complement — for CPU-only paths, prefer BenchmarkDotNet** in a test project against a
real install's executable publicised DLLs (no game launch, portable, rigorous). That covers
the serialization layer cleanly; the in-game perf mode is for placement / capture /
visualizer work that needs a live `Grid`.

**Built next: blueprint placement.** Same `PerfRunner`, run right after the import sweep. Unlike
import, placement drives `BlueprintState.VisualizeBlueprint` / `UseBlueprint`, which instantiate
real Unity `GameObject`s per building and need real, diggable `Grid` cells — not just an in-memory
`Blueprint`. `SyntheticBlueprint.Build` (the same single-ingredient-`Tile` generator, now exposed
as an in-memory `Blueprint` alongside the existing JSON serializer) supplies the layout for
N = 100 / 500 / 1000 / 2000 — capped below import's 5000 since a `GameObject` is much heavier per
unit than a parsed JSON node, and the sweep needs a correspondingly large dug area. Before timing,
`PerfRunner` digs a rectangle once, sized off `Grid.WidthInCells`/`HeightInCells` at absolute map
coordinates (not anchor-relative, so a large sweep can't run off the map edge near the fixture
colony) and reuses `HarnessCases.PlaceAt`'s dig-then-poll-until-clear pattern. Two operations:

- **`visualize`** (warmup 2, iterations 5): `VisualizeBlueprint` redrawn at one fixed spot every
  call — it clears and rebuilds its own preview `GameObject`s each time, so this is exactly what
  real mouse-hover redraw does and is safe to repeat in place.
- **`use`** (warmup 1, iterations 2): `UseBlueprint`, which commits real build orders
  (`Constructable`s). Each draw claims a fresh strip of the pre-dug region instead of
  canceling/destroying the previous draw's orders — simpler than reverse-engineering Klei's
  construction-cancel path, and harmless since the sim stays paused for the whole run and the
  process quits without saving.

No rotation, no cleanup of the committed build orders.

**Two real bugs hid behind plausible-looking numbers here — both fixed in the harness, not
production code, but the second is a latent production edge case worth knowing about.** An
initial run reported `use` timings that *looked* fine (1–9 ms, scaling with N) while every single
placement was silently failing — the harness was trusting the timer, not checking the output.
Adding a correctness check (counting real `Constructable`s created, the same instinct behind
`create`'s own `captured X (expected N)` check below) caught it:

1. **Fog of war.** `BuildingVisual.ValidCell` and `CreateBlueprint`'s capture scan both gate on
   `Grid.IsVisible(cell)`. Digging clears terrain but does **not** reveal fog of war at a distance
   — only something with vision (a Duplicant, a scanner) does that normally, and the harness's
   region sits far from anything with vision. Fixed by force-revealing the whole dug region with
   `Grid.Reveal(cell, byte.MaxValue, forceReveal: true)` right after digging it.
2. **Anchor-shift wraparound.** Even revealed, `use` still only placed ~50% of buildings —
   entirely explained by X, not Y, confirmed by seeing it happen within a single unrotated row.
   `BlueprintTransformationInfo.GetRotatedCell` shifts every building's X offset by the *default*
   `BottomCenter` anchor state (half the blueprint's width) before converting to a cell. For a
   building whose offset is smaller than that shift, the shifted value goes negative — and
   `Grid.PosToCell`'s linear cell index (`x + y·Grid.WidthInCells`) silently **wraps a negative x
   into the previous row at a huge x** instead of failing cleanly, landing the building somewhere
   unrelated (often genuinely invalid) instead of where it was asked to go. `originShiftX`/`Y` are
   private with no public "set an exact anchor" API, and setting them directly didn't stick either
   — `VisualizeBlueprint` calls `CheckPermittedRotations` → `RefreshAnchorState` on every single
   call, which recomputes both floats from the (still default) `_state` field, undoing the
   override before the first placement even happens. The harness fix flips `_state` itself (via
   reflection) to `BottomLeft` — the one `BlueprintAnchorState` with a (0, 0) shift — so every
   refresh keeps landing on zero. Our synthetic blueprints want to place literally at
   `origin + offset`, with no anchor semantics to honor, so this is correct for the harness; a real
   player previewing a wide blueprint near the map's left/bottom edge with a non-`BottomLeft`
   anchor could hit the same wraparound in actual gameplay — confirmed reachable (the default
   anchor is `BottomCenter`, and `UseBlueprintTool` feeds the raw cursor cell straight into
   `GetRotatedCell` with no clamping) and **fixed in production**: `GetRotatedCell` now
   bounds-checks the shifted position against `Grid.WidthInCells`/`HeightInCells` before calling
   `Grid.PosToCell`, returning `Grid.InvalidCell` instead of wrapping. The harness's `_state`
   reflection workaround above is no longer strictly required but is left in place since the
   sweep still wants literal `origin + offset` placement, not anchor semantics.

**Finding (one real run, one machine, after both fixes — see limits below):**

| N | `visualize` | `use` (real placements / expected) |
|---:|---:|---:|
| 100 | 14.8 ms | 16.3 ms (300/300) |
| 500 | 49.8 ms | 78.8 ms (1500/1500) |
| 1000 | 93.5 ms | 140.2 ms (2880/3000) |
| 2000 | 178.9 ms | *(not swept — see room note)* |

`use`'s N=1000 shortfall (96%) is a residual, much smaller effect than the two bugs above and
wasn't chased further. Both operations are linear in N, but now `use` (`UseBlueprint`, which
commits the real build order) is **the more expensive one**, not visualize — the opposite of the
original (buggy) measurement's conclusion. That makes sense once `use` is actually doing its job:
committing a real building is strictly more work than a preview (`PlacePlannedBuilding` → building
instantiation, on top of everything `visualize` already pays for via `VisualizeBlueprint`'s own
internal redraw). `visualize` itself was never affected by either bug — it doesn't consult
`Grid.IsVisible` or `GetRotatedCell` to decide *whether* to build a preview, only to color it — so
its numbers and the hotspot breakdown below stand unchanged.

The `use`/`create` sweep also shares the map with the small regression-case footprint near the
Printing Pod (`FixtureLayout`) and needs its own dug-and-revealed, same-world region — this
particular fixture map only has ~94–130 rows of that room below the anchor, well short of what
N=100/500/1000/2000 × 3 draws needs for `use` *and* a separate band for `create`. `visualize`
doesn't consume region rows at all (it redraws one fixed spot regardless of size), so it keeps the
full N=2000 sweep; `use`/`create` share a smaller `{100, 500, 1000}` sweep sized to fit.

**Instrumented follow-up.** `PerfInstrumentation` gained a generic patch-by-name mechanism (one
prefix/postfix pair looks up the accumulator via `__originalMethod`, so adding a hotspot candidate
is one `PatchOne`/`PatchAllOverloads` call, not a new method) and patched `TileVisual`'s
constructor plus the pieces it calls: `GameUtil.KInstantiate`, `CustomTileRenderer.AddTileBlock`/
`RefreshCell`, `BuildingVisual.GetVisualizerColor`, `VisualsUtilities.SetTileColor`,
`UpdateRequirementsState`, `ApplyAdditionalBuildingData`. Result: **`TileVisual`'s constructor is
essentially all of `visualize`'s cost, and `GameUtil.KInstantiate` (Unity's real GameObject
instantiation) is the largest single piece of it** — unlike `GetValidMaterials`, this isn't a
redundant computation to cache; it's genuine per-building object creation.

⚠️ **The first measurement of `KInstantiate`'s share (~90%) was wrong — inflated by double
counting.** It was patched with `TryPatchAllOverloads`, which puts every overload on one shared
accumulator; `KInstantiate`'s overloads forward to each other, so a single logical call was timed
twice (once in the outer overload, once in the inner) and counted twice. Re-measured with
`TryPatchEachOverload` (one accumulator per overload, signature in the name):

| Overload | Calls | Total | Per call |
|---|---:|---:|---:|
| `KInstantiate(GameObject,Vector3,SceneLayer,String,Int32)` — what `BuildingVisual` calls | 36,466 | 1402.7 ms | 38.5 µs |
| `KInstantiate(GameObject,Vector3,SceneLayer,GameObject,String,Int32)` — it forwards to this | 36,466 | 1388.1 ms | 38.1 µs |
| `KInstantiate(GameObject,SceneLayer,String,Int32)` | 2 | 0.3 ms | — |
| `KInstantiate(Component,SceneLayer,String,Int32)` | 0 | — | — |

Identical call counts confirm the 1:1 forwarding; the true figure is the outer overload's
**38.5 µs/call / 1402.7 ms**, and the wrapper itself adds almost nothing (~0.4 µs/call). Against
`TileVisual.ctor`'s 64.2 µs/call, the raw clone is **~60% of the constructor, not ~90%** — the
remaining ~25.7 µs is coloring, tile-block registration, requirements state and anim setup.

**What that means for the object-pooling idea:** a pooled, reused visual still has to be
repositioned, recolored, re-registered with the tile renderer and re-evaluated for requirements
state — so pooling can only save the raw clone, i.e. `visualize` N=2000 goes ~177 ms → ~100 ms
(**~43% faster**), not the near-elimination the inflated number implied. Weighed against
restructuring a visual lifecycle shared with rotation, flipping, replacement tiles and
multiplayer's per-player visualizer lists — for an action that runs once per blueprint selection
or rotation, not per frame — **the ceiling isn't judged worth the blast radius; pooling is not
recommended.** `BuildingDef.Instantiate` (`use`, 140.5 µs/call, 91% of `TryUse`) has only one
overload and was never double-counted, but pooling doesn't apply to it at all: a committed build
order's `GameObject` is real, persistent state.

### Candidate levers for the object-creation cost

Pooling (above) is only one way to attack the clone. Unity's `Instantiate` cost scales with the
prefab's component count, child count and `Awake`/`OnEnable` work, so these are the other angles.
**Only lever 1 has been tested so far**; the rest are recorded here to be picked up later.

| # | Lever | Idea | Status |
|---|---|---|---|
| 1 | Don't clone a `GameObject` per tile | See the prefab dump below — a tile's preview clone renders nothing. **Implemented: `visualize` N=2000 181 ms → 86 ms (−52%).** | **done** |
| 2 | Clone a lighter prefab | Largely subsumed by lever 1 for tiles (`Tile.BuildingPreview` is already only 7 components, no children). May still apply to non-tiles — `ManualGenerator.BuildingPreview` carries 10 including `BuildingCellVisualizer` and `LogicPorts`. | low priority |
| 3 | Spread creation across frames | Doesn't reduce total cost, but turns a ~177 ms hitch into invisible background work. Arguably what actually matters for a hover preview. Lowest risk of the five. | not started |
| 4 | Instantiate inactive, configure, activate once | **Already in effect** — the dump shows `active=False` on both preview prefabs, so `Awake`/`OnEnable` are deferred until `BuildingVisual`'s `SetActive(true)`. Nothing to gain. | closed |
| 5 | Cull off-screen buildings | Most of a 2000-building preview is off-camera. Biggest theoretical win, but **blocked**: `UseBlueprint` places by iterating `FoundationVisuals`/`DependentVisuals`, so placement is coupled to visuals existing — culling would silently place fewer buildings. Decoupling placement from the visual list has to come first. | blocked |

Unrelated aside found while checking these: `Config.AutoPreviewCuttoff` (default 2000) gates the
*selection-screen thumbnail* (`BlueprintPreviewScreen`), not the in-world hover preview — but it
establishes the precedent that this mod already degrades gracefully above a building-count
threshold rather than doing the expensive thing.

**Lever 1, tested — a tile's preview clone renders nothing.** Dumped what
`BuildingDef.BuildingPreview` actually carries (temporary `[prefab-diag]` logging in `PerfRunner`):

```
Tile:            isKAnimTile=True BlockTileAtlas=True SceneLayer=TileMain ObjectLayer=Building ReplacementLayer=ReplacementTile
Tile.BuildingPreview:            active=False components=7  directChildren=0 descendants=0
  [Transform, KPrefabID, KSelectable, StateMachineController, PrimaryElement, BuildingPreview, BuildingFacade]
  no KBatchedAnimController
ManualGenerator.BuildingPreview: active=False components=10 directChildren=0 descendants=0
  [Transform, KPrefabID, KSelectable, StateMachineController, PrimaryElement, BuildingPreview,
   KBatchedAnimController, BuildingFacade, BuildingCellVisualizer, LogicPorts]
  kbac enabled=True visibilityType=Default animFiles=1 initialAnim=place
```

A tile's preview has **no `KBatchedAnimController`**, so `BuildingVisual`'s ctor takes the `else`
branch (`SetLayerRecursively`) and never sets up or plays an anim — the earlier worry that
`kbac.Play("place")` runs for tiles too was wrong, that path is non-tile only. None of the seven
components a tile preview *does* carry render anything: the visible art comes entirely from
`CustomTileRenderer`'s block-tile atlas (~1.7 µs/call), while the 38.5 µs clone is a
**non-rendering token object**, needed only to be passed as the `source` argument to
`BuildingDef.IsValidPlaceLocation`/`TryReplaceTile`, positioned, and destroyed.

That makes a **shared dummy per `BuildingDef`** more attractive than pooling: the same ~43%
ceiling on `visualize`, but instead of restructuring the visual lifecycle it only changes how
`Visualizer` is obtained plus not destroying it — and it's naturally scoped to tiles, which are
the bulk of most large blueprints. Verify before implementing:

1. Does `IsValidPlaceLocation(source, cell, …)` read the source's *transform position*, or only the
   explicit `cell`? (Likely the latter — `source` is probably just "exclude myself from occupancy" —
   but a shared dummy can only be at one position at a time, so this is the load-bearing question.)
2. `ApplyAdditionalBuildingData(Visualizer, …)` *writes* onto the object in the ctor; with a shared
   instance that becomes last-write-wins. Skip it, or confirm it's meaningless for a non-rendering
   tile preview.
3. `DestroyVisualizer` must not destroy a shared instance.
4. Multiplayer shares `FoundationVisuals` per player — the dummy may need to be per-`(def, playerId)`
   rather than per-`def`.

**Probe result — the source argument is positional-independent.** Ran all four questions in-game
(`[probe]` lines, since removed):

```
T1 objAtA   queryA(valid)      = True
T2 objAtA   queryB(valid, far) = True    <- object 100 cells away, B still validates
T3 objAtA   queryOccupied      = False  "Must be built in unoccupied space"   <- decisive
T5 objAtSolid queryB(valid)    = True    <- moved the object, answer unchanged
T7 nullSource queryB(valid)    = True    <- null source works
T8 nullSource queryOccupied    = False  "Must be built in unoccupied space"
T9 inactiveObj queryB(valid)   = True    <- inactive behaves identically
```

T2 + T3 show the answer tracks the *queried cell* while the object sits elsewhere, and T5 shows
moving the object doesn't change it: **`IsValidPlaceLocation` never reads the source's transform.**
It also tolerates a `null` source and an inactive one.

**Implemented in [`BuildingVisual.cs`](../../src/BlueprintsIncluded/Visualizers/BuildingVisual.cs).**
One lazily-created placeholder per `BuildingDef`, kept inactive, never moved and never destroyed,
reused instead of cloning. Gated on *"the preview prefab has no `KBatchedAnimController`"* rather
than on "is a tile" — a preview that can't render is safe to share by construction, and any def
whose preview *can* render keeps its own clone, so no per-def audit is needed. When shared, the
ctor also skips `ApplyAdditionalBuildingData` (it writes onto the object; meaningless
last-write-wins on a shared one, and the real building still gets its data at placement), and
`MoveVisualizer`/`DestroyVisualizer` skip the position write and the destroy.

| N | `visualize` before | after | |
|---:|---:|---:|---:|
| 100 | 14.9 ms | 9.1 ms | −39% |
| 500 | 50.3 ms | 25.0 ms | −50% |
| 1000 | 94.8 ms | 46.1 ms | −51% |
| 2000 | 181.1 ms | **86.2 ms** | **−52%** |

Better than the ~43% the clone's share alone predicted, because the work that accompanied each
clone (`SetActive`, `ApplyAdditionalBuildingData`, the transform writes, the teardown `Destroy`)
goes with it. `TileVisual.ctor` 64.2 → **19.0 µs/call**; `KInstantiate` 36,466 → **6,467 calls**
(exactly the 30,000 tile visuals removed); `ApplyAdditionalBuildingData` 34,680 → 4,680.
`use` and `create` are untouched (`Instantiate` still 4,680 calls @ ~149 µs; `create` N=1000 still
~1125 ms, 1000/1000 captured), and the tile-rendering path is provably intact — `AddTileBlock`
(60,000), `RefreshCell` (360,720), `SetTileColor` (60,000) and `GetVisualizerColor` (60,000) all
kept identical call counts. The surviving `KInstantiate` calls got *dearer* per call (38.5 →
98.6 µs) only because the cheap tile clones left the average.

The instrumentation also caught a real (if smaller) redundancy: `TileVisual.UpdateGrid` unregisters
and re-registers a tile's mesh block (`CustomTileRenderer.AddTileBlock`/`RefreshCell`) on *every*
forced redraw, even when the tile hasn't actually moved. Fixed in
[`TileVisual.cs`](../../src/BlueprintsIncluded/Visualizers/TileVisual.cs) by skipping that cycle when
already seated at the same cell. **Measured impact: negligible** (`visualize` N=2000: 164.8 ms →
162.9 ms, noise-level) — the default `BottomCenter` blueprint anchor shifts every building's X
position by half the blueprint's width on the very first post-construction redraw
(`VisualizeBlueprint` places each building once at its raw offset, then immediately corrects every
one of them via its own trailing `UpdateVisual(forcingRedraw: true)` call), so the "same cell" case
essentially never triggers in this benchmark. Most of the modest `AddTileBlock`/`RefreshCell`
call-count drop actually came from removing a redundant duplicate `UpdateVisual` call the harness's
own `use`-sweep setup had been making (`PerfRunner.cs`), not from the production fix. Kept anyway —
harmless, strictly correct, and would help other anchor states (`BottomLeft`/`TopLeft`, shift = 0)
or a plain same-position force-redraw.

Regression suite re-verified 6/6 green. Two real options identified but **not attempted this
pass** (tracked as a follow-up task, not started): restructure `VisualizeBlueprint` to compute each
building's final anchor-shifted/rotated cell *before* construction instead of placing-then-
correcting (touches `BlueprintState.cs`'s shared placement math — foundation and dependent
visuals, tiles and non-tiles, snapshots and normal blueprints); or object-pool the visualizer
`GameObject`s so redraws reuse existing previews instead of destroy+recreate every
`VisualizeBlueprint` call (bigger architectural change, addresses `KInstantiate` directly).

**`use`'s own hotspot breakdown** (added once the two bugs above were fixed and `use` turned out to
be the costlier operation, not `visualize`): patched `BuildingVisual.TryUse`/`IsPlaceable`/
`PlacePlannedBuilding`, `BuildingDef.Instantiate`, `ApplyBuildingData`, and
`UpdateConduitConnectionBits`. Result: **`BuildingDef.Instantiate` — real Unity `GameObject`
creation for the planned-building order — is ~93–97% of `use`'s per-building cost** (179.4 µs of
`TryUse`'s 192.2 µs/call), the same shape as `visualize`'s `KInstantiate` finding and just as
genuinely necessary (not a redundancy to fix): `IsPlaceable`'s validity gate is cheap (9.5 µs/call,
confirming the earlier wraparound-bug investigation), and `ApplyBuildingData`/
`UpdateConduitConnectionBits` are negligible (5.1 / 1.1 µs/call). No fix pursued — a committed build
order's `GameObject` *is* the real, simulation-tracked order, not a discardable preview, so the
"object pool it" idea above doesn't transfer here the way it might for `visualize`.

**Built last: creating a blueprint.** `BlueprintState.CreateBlueprint` — capturing a rectangle of
the world into a `Blueprint` — is the mirror image of import: unlike `visualize`/`use`, it's a pure
read (scans every cell × every `Grid.ObjectLayers` in the rectangle for a `Constructable`/
`Deconstructable`-bearing `GameObject`, builds a fresh `Blueprint` object, mutates nothing), so it's
safe to time repeatedly over one fixed rectangle the same way `visualize` is. The rectangle needs
real, *finished* buildings to capture — `RunCreateSweep` places them directly via `BuildingDef.Build`
(the same call `FixtureBuilder.PlaceAll` uses for the regression fixture, just in a tight loop
instead of one at a time), in a dug-and-revealed band disjoint from `use`'s. A captured-count check
(`captured X building(s) (expected N)`) verifies the scan actually found everything, the same
instinct that caught `use`'s two bugs above.

| N | `create` (before) | captured / expected |
|---:|---:|---:|
| 100 | 181.0 ms | 100 / 100 |
| 500 | 910.9 ms | 500 / 500 |
| 1000 | 1815.9 ms | 1000 / 1000 |

Strikingly linear — 910.9 / 181.0 ≈ 5.03 and 1815.9 / 181.0 ≈ 10.03, almost exactly matching the
5× and 10× size ratios — at a consistent **~1.8 ms/building** (dense 1-building-per-cell packing,
so this is also ~1.8 ms per scanned cell here).

**Instrumented follow-up, and a real fix.** Reusing the same `PatchOne`/`PatchAllOverloads`
mechanism `visualize` already had, patched `API_Methods.StoreAdditionalBuildingData` (loops every
one of the mod's ~35 registered `AdditionalBuildingDataEntries` handlers per building, each doing a
`TryGetComponent` check for one data type — `Prioritizable`, `Automatable`, `Filterable`, and so
on), `GetAdditionalBuildingData` (the handler loop itself), and `GameUtil.NaturalBuildingCell`.
Result: **`StoreAdditionalBuildingData` averaged ~685 µs/call — and ran exactly 2× the expected
call count** (22,400 instead of 11,200 for this sweep). `CreateBlueprint`'s per-cell × per-layer
scan finds a building once per `Grid.Objects` layer it's registered on — `Tile` sits on two (its
own layer and a `ReplacementLayer`) — and was redoing the *entire* expensive per-building capture
(fresh `BuildingConfig`, `SelectedElements` copy, conduit-flag lookup,
`StoreAdditionalBuildingData`) on every hit, only deduplicating the final list entry by value
afterward (`BuildingConfigurations.Contains`) — after already paying for the redundant work. Fixed
in [`BlueprintState.cs`](../../src/BlueprintsIncluded/BlueprintData/BlueprintState.cs) by tracking
already-captured `GameObject`s and skipping straight to `emptyCell = false` on a repeat hit,
before any of the expensive work runs (safe regardless of *why* the same object is found again —
multiple layers at one cell, or multiple cells for a multi-cell building — since a `BuildingConfig`
depends only on the `GameObject`'s own state, not which layer/cell it was found from).

| N | `create` (before) | `create` (after) | captured / expected |
|---:|---:|---:|---:|
| 100 | 181.0 ms | 117.6 ms (−35%) | 100 / 100 |
| 500 | 910.9 ms | 559.4 ms (−39%) | 500 / 500 |
| 1000 | 1815.9 ms | 1132.5 ms (−38%) | 1000 / 1000 |

`StoreAdditionalBuildingData`'s call count dropped to exactly 11,200 (half), confirming the fix;
its now-correctly-attributed cost (~678 µs/call, unchanged per call) is still the majority of
`create`'s remaining time (~60%). Regression suite re-verified 6/6 green.

**Investigated as a follow-up, declined:** cache *which* of the ~35 `AdditionalBuildingDataEntries`
handlers actually apply *per `BuildingDef`* (most building types have none of most data types —
`Tile` has none of the ~35), so most of the 35 `TryGetComponent` checks per building could be
skipped entirely instead of run and found empty. **Not safe as scoped.**
`DataTransfer_Prioritizable.TryGetData` is a plain `TryGetComponent<Prioritizable>` check like the
other 34 — but [`BuildingVisual.ApplyBuildingData`](../../src/BlueprintsIncluded/Visualizers/BuildingVisual.cs)
calls `building.FindOrAddComponent<Prioritizable>()` at blueprint-placement time, so whether a given
`GameObject` has a `Prioritizable` component depends on *how that instance was placed*, not on its
`BuildingDef` — two buildings of the identical type can differ. A per-`BuildingDef` "never applies,
skip forever" cache would silently drop legitimately-set priority data for exactly the instances
where it *was* set. No other handler shows this pattern in the mod's own code, but that only clears
this mod's code — Klei's own systems could add one of the other ~34 component types to a `GameObject`
after initial placement under conditions not fully ruled out here. Given the risk (silent per-building
data loss in a save/restore-adjacent feature) against the payoff (`create` is an occasional dev-tool
action, not a hot path, and is already ~38% faster from the fix above), left un-implemented.

**Built next: opening the selection screen.** The dialog the Use Blueprint tool puts up, reported
as slow to open. `SelectionScreenPerf` drives the real `BlueprintSelectionScreen.ShowWindow` (the
screen, `ModAssets` and the `Vis_*` previews are all internal types, so everything is reached by
reflection) after swapping the root folder's contents for a synthetic library, and puts the two
independent pieces of work on their own axes rather than reporting one blended number:

- **the file list** — `UpdateBlueprintButtons` walks the folder and gives every blueprint a
  `FileHierarchyEntry` GameObject, instantiated on first sight and cached in `BlueprintEntries`.
  Swept over library size L with the preview suppressed, both **cold** (entry cache emptied first —
  a session's first open) and **warm** (every open after that).
- **the preview** — `LoadBlueprintPreview` destroys and rebuilds one GameObject per building in the
  selected blueprint. Swept over building count N with a fixed 10-blueprint library, for both
  visualizer branches: `Tile` takes the cheap `Vis_TilePreview` path, `Ladder` (1×1, raw-mineral,
  `LadderTile` so it misses the tile branch) the `Vis_BuildingPreview` one with a real
  `KBatchedAnimController` per building.

Which opens draw a preview at all is worth knowing: `ShowingInfoPreview` starts false, so a
session's *first* open shows none — but both close paths (`OnCloseClicked`, `OnPlaceBlueprint`) set
it back to true, so **every subsequent open does**. The sweep sets the flag explicitly per case
rather than relying on that ordering.

`TimeOp` grew a `settleFrames` option for this, reporting a second `…-settled` figure measured
through the next 3 rendered frames. UI work needs it: a `KMonoBehaviour`'s `OnSpawn` (and with it a
`KBatchedAnimController`'s first `Play`) runs from Unity's `Start`, i.e. *after* the call that
instantiated it returned, so a synchronous-only number would credit that work to nobody. The
`idle-frame` op is the floor to compare against — the same 3 frames with no work in them, **60.0 ms
here** (~20 ms/frame in this paused colony).

| N / L | `open-list-cold` | `open-list-warm` | `open-preview-tile` | `open-preview-ladder` |
|---:|---:|---:|---:|---:|
| 100 / 10 | 6.6 ms | 2.3 ms | 24.1 ms | 37.6 ms |
| 500 / 50 | 28.8 ms | 7.5 ms | 319.3 ms | 155.6 ms |
| 1000 / 200 | 119.9 ms | 35.7 ms | 456.5 ms | 328.6 ms |
| 2000 / 500 | 531.0 ms | 125.7 ms | 699.4 ms | 835.7 ms |

(list columns read against L, preview columns against N; medians, sim paused. Settled figures run
roughly 60–1400 ms higher — e.g. `open-preview-ladder` N=2000 is 835.7 ms synchronous,
**2258.0 ms settled**, against the 60.0 ms idle floor, so well over half that blueprint's cost
lands on the frames *after* the call returns.)

**Finding: the preview is ~3⁄4 of the open, and it is the plain GameObject clone again.** Over the
84 opens the sweep performs:

| Hotspot | Calls | Total | Share of `ShowWindow` |
|---|---:|---:|---:|
| `ShowWindow` | 84 | 20,936.6 ms | — |
| ↳ `ClearUIState` | 84 | 18,699.1 ms | 89% |
| ↳ `SetMaterialState` | 84 | 15,955.9 ms | **76%** |
| ↳ `LoadBlueprintPreview` | 48 | 15,900.9 ms | 76% |
| ↳ `GeneratePreview_Buildings` | 48 | 15,476.6 ms | 74% |
| ↳ `UpdateBlueprintButtons` | 84 | 2,743.0 ms | **13%** |
| ↳ `AddOrGetBlueprintEntry` | 7,368 | 1,058.1 ms | 5% |
| `Preview.ClearExisting` | 48 | 370.9 ms | 2% |
| `Vis_TilePreview.ConnectAll` | 48 | 111.0 ms | <1% |
| `UpdateBuildingButtons` | 48 | 20.2 ms | <1% |
| `RefreshVisualizerVisibility` | 96 | 17.1 ms | <1% |

The two suspicions going in were half right. The list *is* a cost and it *is* worst on a session's
first open (cold 531.0 ms vs warm 125.7 ms at L=500 — entry instantiation, ~464 µs per entry once
the warm baseline is subtracted from `AddOrGetBlueprintEntry`'s average), but at 13% it is second,
and only bites libraries far larger than most. The preview dominates.

Inside the preview, the expected culprit was wrong: **the `KBatchedAnimController` is cheap**.
`Vis_BuildingPreview.Init` is 921.6 ms / 21,600 calls (42.7 µs) and its `OnSpawn` — the first
`kbac.Play`, the part that was supposed to be expensive — is **2.4 µs/call**, 51 ms in total.
That leaves ~14.5 s of `GeneratePreview_Buildings`'s 15.5 s in the loop body itself: one
`Instantiate(BuildingEntry, transform)` of the preview-entry prefab per building, plus its
`AddOrGet` and transform writes. Same shape as `visualize`'s `KInstantiate` finding — per-building
Unity object creation, not a redundant computation to cache.

That points the fix at the same levers as lever 3/1 above rather than at anything anim-related:
skip the rebuild entirely when the target blueprint and its disabled-building state haven't changed
since the last load (every open currently rebuilds from scratch, and so does every click in the
file list); pool the entry objects instead of destroy-and-clone; or spread generation over frames
so the window paints immediately. Worth noting `Config.AutoPreviewCuttoff`'s default of **2000**
means the existing graceful-degradation gate almost never fires — a 2000-building preview costs
835.7 ms synchronous / 2258.0 ms settled and is still drawn automatically.

⚠️ **One hole in the first run's attribution, since fixed.** `Vis_TilePreview.Init` reported 0
calls: it *shadows* rather than overrides the parameterless `Vis_SpritePreview.Init()` it inherits,
a name-only `AccessTools.Method` lookup resolved to the inherited one, and Harmony refused it ("you
can only patch implemented methods"). The lookup now pins `new[] { typeof(BuildingConfig) }`, and
the re-run below has the number: **6.7 µs/call** against `Vis_BuildingPreview.Init`'s 27.4 µs. It
doesn't move the conclusion — together the two `Init`s are 859 ms of `GeneratePreview_Buildings`'
18,247 ms (~5%).

**Fix — cache the preview instead of redrawing it.** `LoadBlueprintPreview` runs on far more than a
change of blueprint: every reopen of the screen, every material-override action, and every
`RefreshOnBpChanges` tick, each destroying and re-instantiating every building. It now returns
early when the same blueprint at the same content revision is already drawn, doing only the filter
reset `ClearExisting` owed (which re-tints every visual via `RefreshVisualizerVisibility`).

Two details make it safe:

- **Reference equality, not `Blueprint`'s own `==`.** That operator compares `FilePath`, and a
  file-watcher reload builds a *new* instance for the same path — which must redraw.
- **A `ContentRevision` counter on `Blueprint`**, bumped in `CacheCost()`. Reference identity alone
  is not enough: a retake calls `UpdateFrom`, which rewrites `BuildingConfigurations` on the
  *existing* instance. Hanging the counter off `CacheCost` rather than auditing every writer works
  because every content-rewriting path already calls it (load, `UpdateFrom`, override). It
  over-signals on a pure material override — which changes no preview geometry, since tiles draw
  with `SimHashes.COMPOSITION` and building previews only read `BuildingDef.AnimFiles` — and that
  is the right way to be wrong: a spurious rebuild costs one redraw, a missed one shows stale
  geometry.

The "loaded" marker is set inside `GeneratePreview`, not `LoadBlueprintPreview`, so the
over-`AutoPreviewCuttoff` path (confirm prompt, draws only on Override) marks itself when it
actually draws.

`SelectionScreenPerf` gained an `open-preview-*-fresh` op whose setup calls `CacheCost()` to defeat
the cache deliberately — otherwise a *broken* generator would read as a spectacular speedup.

| N | reopen before | reopen after | first draw (`-fresh`) |
|---:|---:|---:|---:|
| `tile` 100 | 24.1 ms | **8.3 ms** (−66%) | 24.9 ms |
| `tile` 1000 | 456.5 ms | **34.9 ms** (−92%) | 464.5 ms |
| `tile` 2000 | 699.4 ms | **73.5 ms** (−89%) | 673.7 ms |
| `ladder` 100 | 37.6 ms | **10.8 ms** (−71%) | 31.4 ms |
| `ladder` 1000 | 328.6 ms | **51.5 ms** (−84%) | 262.2 ms |
| `ladder` 2000 | 835.7 ms | **110.0 ms** (−87%) | 836.9 ms |

Settled, against a 62.3 ms idle floor: `ladder` N=2000 **2258.0 → 879.6 ms**, `tile` N=2000
**1347.5 → 546.2 ms**. The `-fresh` column matching the before column is the control — generation
itself is untouched, and the cached column is fast because it skipped work.

Call counts confirm the mechanism rather than just the timings: `LoadBlueprintPreview` ran 96 times
while `GeneratePreview_Buildings` ran **56** — exactly the 8 warmup draws plus the 48 deliberately
invalidated `-fresh` ones, so the other 40 opens drew nothing. Regression suite re-verified
**11/11 green**.

**What's left in a cached reopen** (`tile` N=2000, 73.5 ms, against 2.2 ms for a preview-less open
of a comparable library): not the preview contents, but `ShowInfo`'s `SetActive` on a hierarchy
that still holds 2000 child objects, plus `SetMaterialState`'s own cost/building-count passes over
a 2000-building blueprint. Well past the point of diminishing returns, and not investigated
further.

**Second pass — the file list (the other 13%).** Three separate problems in
[`BlueprintSelectionScreen`](../../src/BlueprintsIncluded/UnityUI/BlueprintSelectionScreen.cs), all
shipped together:

1. **`UpdateBlueprintButtons` rebuilt unconditionally.** Every open hid every cached entry (across
   all folders ever visited), then re-showed, re-ordered (`SetAsLastSibling`) and re-highlighted
   every entry in the current folder — 126.1 ms at L=500 with nothing left to instantiate. Now
   skipped when folder, sort order, `BlueprintFolder.ContentRevision`, root subfolder count and
   entry-cache count all match, updating only the highlight when the selection moved. An explicit
   `InvalidateBlueprintList()` covers what the key can't see: a rename (which changes name ordering
   *and* FilePath without touching the folder), a move, a delete, and a non-empty search filter
   (which hides entries behind the list builder's back). The entry-cache count is in the key partly
   to defend against entries being dropped from underneath it — which is exactly what
   `SelectionScreenPerf`'s cold sweep does.
2. **The list could be built twice per open.** `ClearUIState` calls `ClearSearchbars`, which set
   `BlueprintSearchbar.Text`, firing TMP's `onValueChanged` → `ApplyBlueprintFilter("")` →
   `UpdateBlueprintButtons()` — and then called `UpdateBlueprintButtons()` itself. `FInputField2`
   already has a guard for exactly this (`DataTextUpdate`, set by `SetTextFromData`), but it is
   only honoured by its `AddListener` helper, and the screen had subscribed to `OnValueChanged`
   directly, bypassing it. Now uses `AddListener` + `SetTextFromData`. Invisible in the benchmark —
   TMP suppresses the event when the text is already empty, so it only fired on the first open
   after an actual search.
3. **Date sorting was O(n²).** `BlueprintFolder.GetBlueprintIndex` did `contentsList.Contains` then
   `contentsList.IndexOf` — two O(n) scans — as a LINQ `OrderBy` key selector, so it ran once per
   element, at ~n² `Blueprint.Equals` FilePath string comparisons per listing. Creation-date-
   descending is the *default* sort, so this was the normal path. Now a dictionary built on demand
   and dropped on any add/remove.

`SelectionScreenPerf` gained `open-list-warm-fresh`, whose setup re-adds the identical library
(same order, but `ContentRevision` bumps) to defeat the cache — which incidentally separates
fix 3 from fix 1, since `-fresh` still pays the sort:

| L=500, preview suppressed | before | after |
|---|---:|---:|
| `open-list-warm` — reopen | 126.1 ms | **25.9 ms** (−79%) |
| `open-list-warm-fresh` — real rebuild | 126.1 ms | **67.6 ms** (−46%) |

So **fix 3 alone took the rebuild 126.1 → 67.6 ms**, and **fix 1 takes a reopen that needs no
rebuild to 25.9 ms**. `GetBlueprintIndex` is now 0.7 µs/call (4.6 ms over 6,928 calls). Settled,
L=500: 222.5 → 116.6 ms.

Cold opens moved 531.0 → 488.1 ms, but **that delta is not established** and should not be quoted
as a win. The cold sweep runs with the preview suppressed, so the run that shipped only the preview
cache changed nothing on this path — and still reported 548.4 ms, **+3.3%** against the baseline's
531.0 ms. With one run per configuration and only `ColdIterations = 3` samples per point, anything
under ~10% here is indistinguishable from run-to-run drift, exactly as the Limits above warn. The
direction is plausible (fix 3 genuinely does less work per listing) and that is all this run
supports.

That is a measurement-capability gap, not just a caveat: a session's first open is dominated by
instantiating a `FileHierarchyEntry` per blueprint (~464 µs each), and the levers left there —
pooling or virtualizing the entries — are exactly the kind that might land 5-15% each. Banking wins
that size needs the harness to resolve them first: more cold iterations, and an A/A run (the same
build measured twice) to quantify the noise band before trusting any single-digit delta. Neither
was done here.
Preview numbers held (`tile` N=2000 reopen 72.8 ms, fresh 676.7 ms), so the list work didn't disturb
the preview cache. Regression suite re-verified **11/11 green**.

### Ceiling check on the cold open — and why pooling is the wrong lever

Before committing to a runtime-switchable pooling path (needed for interleaved A/B, see the A/A
section below), measure what a pool could even win. Patched `FileHierarchyEntry.OnPrefabInit` /
`OnSpawn` / `RefreshIcon`, `Util.KInstantiateUI` and `UIUtils.AddSimpleTooltipToObject` (per
overload — the `Transform` one forwards to the `GameObject` one, the same double-counting trap
`KInstantiate` fell into earlier).

| Hotspot | Calls | Per call |
|---|---:|---:|
| `AddOrGetBlueprintEntry` (7,608 real creations among 16,048 calls) | 16,048 | ~386 µs *per creation* |
| `FileHierarchyEntry.OnPrefabInit` — 5 `FButton`s, an `FToggleButton`, 6 tooltips, ~7 `transform.Find` | 7,608 | 152.8 µs |
| ↳ of which tooltips (5 × `Transform` overload @ 6.6 µs + 1 × `GameObject` @ 7.2 µs) | 38,056 / 45,671 | ~40 µs (26%) |
| `FileHierarchyEntry.OnSpawn` — the rebinding a pooled row would still pay | 7,608 | **20.2 µs** |
| `FileHierarchyEntry.RefreshIcon` | 7,784 | 2.5 µs |

So ~539 µs to bring a row into existence, of which only ~23 µs (**~4%**) is rebinding. On the face
of it a pool could avoid ~95% of it, and per-entry creation is ~90% of the cold open itself
(500 × 539 µs ≈ 270 ms of a 301 ms `open-list-cold` at L=500).

**And yet pooling is still the wrong lever, because the mod already has its pool.**
`BlueprintEntries` caches an entry per blueprint for the life of the screen, and nothing destroys
entries except deleting a blueprint. A player therefore pays creation exactly **once per blueprint
per session** — which is what `open-list-cold` deliberately recreates by emptying the cache, and is
*not* a cost any repeat open pays (that is `open-list-warm`, now 25.9 ms at L=500). A pool would be
a second cache in front of an existing one.

**The lever that does apply is virtualization** — build rows only for what's actually on screen,
turning the first open from O(library) into O(visible rows), roughly 15. Same ~90% ceiling, and it
attacks the cost that is genuinely there.

**Recommendation: not worth doing yet.** The cost is once per session and scales with library size:
~301 ms at L=500, ~113 ms at L=200, ~29 ms at L=50, ~7 ms at L=10. For a normal library it is
imperceptible; it only bites people holding 200+ blueprints, once, at the first open. Weighed
against reworking the file list into a virtualized scroller — with the folder/sort/filter/highlight
behaviour that already has four invalidation paths — the payoff does not justify it unless large
libraries turn out to be common. Revisit if that is reported.

⚠️ `KInstantiateUI` reported **0 calls** in this run: the generic `KInstantiateUI<T>` and the
non-generic overload take the same three parameters, so `AccessTools.Method(type, name, types)`
threw `AmbiguousMatchException`. Fixed (the overload is now selected by hand, filtering out the
generic definition) but **not re-measured** — it would only split the clone from the wiring *inside*
`AddOrGetBlueprintEntry`'s ~386 µs, and the conclusion above doesn't rest on that split.

### A/A noise measurement — what a perf delta here has to beat

Prompted by the unsupportable "−8%" above. Two full perf passes were run on the **identical
build**, no code change between them ([`test/aa-run.ps1`](../../test/aa-run.ps1)), and their `perf.json` medians
diffed. Anything that differs is harness/machine noise. Iteration counts were raised first
(`SelectionScreenPerf`: cold 3 → 10, warm/preview 5 → 10).

**The noise is not symmetric — there is a systematic drift favouring whichever pass runs second.**

| | |
|---|---:|
| ops where pass 2 was faster | **64 / 75 (85%)** |
| signed delta, median / mean | **−4.2% / −4.9%** |
| \|delta\|, p50 / p90 / p95 | 4.2% / 8.9% / 11.8% |
| residual \|delta\| once the −4.2% shift is removed | p50 2.9% / p90 5.2% |

Three things follow, and the first is the uncomfortable one:

1. **Every before/after comparison in §7 ran its "after" pass second**, so each carries a ~4-5%
   tailwind. The preview cache (84-92%) and the file-list fixes (79% / 46%) are an order of
   magnitude outside that and are unaffected. The cold-open "−8%" was inside it — dead, as above.
2. **A single run pair supports a delta of roughly ±10% or better** (4% systematic + ~5% p90
   residual), and nothing finer. Anything smaller is reading the weather.
3. **The iteration bump moved the numbers it was measuring.** `open-list-cold` L=500 now reports
   **~300-320 ms**, against ~490-550 ms at 3 iterations: the *first* cold iteration is a large
   outlier (first-time Unity prefab/layout work) and with 3 samples the median sat on it. Absolute
   figures recorded before this change are not comparable with ones recorded after it — including
   every table above.

**To resolve anything smaller, stop comparing runs.** Interleave A and B *within one game run* — a
runtime toggle flipped per iteration inside the same sweep — which cancels the between-launch drift
by construction rather than trying to subtract it. That needs the change under test to be
switchable at runtime, so it is a per-investigation cost, not a one-off. Until that exists, treat
single-digit deltas here as unmeasured.

⚠️ `RefreshEntryHighlight` reports **0 calls** in the sweep: the benchmark never moves the selection
while the screen is open, so the cheap highlight-only branch of the cached path is exercised only by
the regression suite and by hand, never by a timing here.

**Noted, not fixed.** `Blueprint.Rename` calls `InferFileLocation`, changing `FilePath` — and
`Blueprint.GetHashCode` *is* `FilePath.GetHashCode()`, while blueprints are held as keys in
`BlueprintFolder`'s `HashSet` and the screen's `BlueprintEntries` dictionary. Renaming therefore
mutates a live hash key. Pre-existing and out of scope for a performance pass, but it is why fix 1
invalidates explicitly on rename rather than trusting the folder's revision counter.

**Built last: `update-visual` — the only per-frame path in the mod.** Everything measured above is a
*one-shot* action: importing a file, opening the dialog, selecting a blueprint, clicking to place,
dragging a capture rectangle. `BlueprintState.UpdateVisual` is not. `UseBlueprintTool.OnMouseMove`
calls it on **every cursor cell change** while a blueprint is on the cursor, and it walks every
visual doing rotation, cell maths and colour evaluation. At 60 fps a frame is 16.7 ms; this sweep
found a 2000-building blueprint costing **55 ms per cursor step** before the fixes below — three
frames dropped per cell of mouse travel, which is what "dragging a large blueprint is a slideshow"
actually is.

**This is the first §7 measurement to use the interleaved method** the A/A section above prescribes,
rather than comparing two runs. `PerfSwitches` (a temporary `internal static class` in the mod, one
`bool` per fix, reached from the harness by reflection via `PerfSwitchAccess`) is flipped **between
iterations of one sweep**, and the two arms are reported as separate operations
(`update-visual-{variant}-opt` / `-base`). Both arms therefore see the same machine, the same
process and the same thermal state, so the ~4-5% second-pass drift cannot apply by construction —
which matters because three of the four fixes were individually expected to land under the ±10%
single-run-pair floor. The switches and the interleaving were **scaffolding, and have since been
deleted** — `PerfSwitches`, `PerfSwitchAccess` and the superseded code paths are gone, and the
sweep now times the single (optimised) path as an ongoing `update-visual-{variant}` benchmark. They
are described here because the numbers below cannot be reproduced without rebuilding them; the
method is the reusable part, not the scaffolding.

**Two variants, and why that is not optional.** `BlueprintState.AddVisual` files each visual under
`FoundationVisuals` or `DependentVisuals` by `BuildingDef.IsFoundation`, and `UpdateVisual` treats
the two lists differently — only the dependent list gets the second `RefreshColor` pass that fix 3
deduplicates against. `SyntheticBlueprint`'s default all-`Tile` blueprint is **entirely
foundations** (the sweep logs `2000 foundation, 0 dependent`), so a tile-only sweep would have
measured the largest of the four fixes as exactly zero. The sweep runs `Tile` for the foundation
path and `Ladder` for the dependent one (`0 foundation, 2000 dependent`), and logs the actual split
rather than trusting that reasoning — the same instinct as `create`'s captured-count check.

Other things that keep it honest: the cursor oscillates by one cell rather than sitting still
(`UpdateVisual` early-returns when the origin equals `lastBlueprintPos`, so a fixed origin would
time the early-out); it runs inside the already-dug **and revealed** region, since
`BuildingVisual.ValidCell` short-circuits on `Grid.IsVisible` and outside it the colour evaluation —
the bulk of the work — never runs, the trap that made `use` report plausible numbers while silently
failing; and it takes no `settleFrames`, because `UpdateVisual` creates no `GameObject`s and its
whole cost is synchronous. It consumes no region rows, like `visualize`.

| N | tile base | tile opt | | ladder base | ladder opt | |
|---:|---:|---:|---:|---:|---:|---:|
| 100 | 4.30 ms | 3.57 ms | −17% | 5.25 ms | 3.16 ms | −40% |
| 500 | 11.38 ms | 7.74 ms | −32% | 15.58 ms | 5.10 ms | −67% |
| 1000 | 20.31 ms | 12.95 ms | −36% | 28.70 ms | 7.68 ms | −73% |
| 2000 | 38.00 ms | **23.48 ms** | **−38%** | 55.19 ms | **12.83 ms** | **−77%** |

Per visual at N=2000 the dependent path goes 27.6 → 6.4 µs. The dependent case gains most because
it was paying for the duplicated colour evaluation (fix 3) on top of everything the foundation case
pays; the foundation case keeps a floor the fixes don't touch, namely `CustomTileRenderer`'s
`AddTileBlock`/`RefreshCell`/`SetTileColor` work per tile.

**The four fixes**, all in [`BlueprintState.cs`](../../src/BlueprintsIncluded/BlueprintData/BlueprintState.cs)
and [`BuildingVisual.cs`](../../src/BlueprintsIncluded/Visualizers/BuildingVisual.cs):

1. **Rotation gating.** `ApplyRotatedCellAndMove` called `IVisual.ApplyRotation` for every visual on
   every update, but rotation and flip only change via hotkey and every path that changes them
   redraws with `forcingRedraw` — so on a plain cursor move it re-applied an orientation every
   visual already had. Now decided **once per update** (three comparisons against the last-applied
   triple) rather than cached per visual, which also covers implementations like `UtilityVisual`
   whose `ApplyRotation` does work *around* its `base` call that a base-class early-return would
   not suppress.
2. **Integer rotation.** `GetRotatedCell` built a `Quaternion.Euler` → `Matrix4x4.Rotate` plus a
   `Matrix4x4.Scale`, then two `MultiplyVector` calls and two `Mathf.Round`s — per visual, per
   move — for a transform that is only ever four 90° steps and two sign flips. Every input is
   already integral (`Offset` is a `Vector2I`, both origin shifts are `int` casts), so it is now a
   swap and some negations. The `Mathf.Round` calls went with the floats: they existed to undo
   float error the matrices themselves introduced. The bounds check stays exactly as it was — that
   is the anchor-shift wraparound fix above.
3. **Deferred dependent colour.** `UpdateVisual` moved each dependent visual (colouring it via
   `MoveVisualizer` → `ApplyColorIfChanged`) and then immediately ran a second pass calling
   `RefreshColor()`, colouring it again. The second pass exists for a real reason — colour depends
   on occupancy, which isn't complete until every visual has been placed and `StoreOccupiedArea`'d —
   so **the first call is the wasted one**. `GetVisualizerColor` is the heaviest per-visual call in
   the path (9.1 µs/call: `UpdateRequirementsState`, `SameBuildingAlreadyFinishedInPlace`, and
   `ValidCell` → `IsValidPlaceLocation`, which allocates a `failReason` string every call), so
   halving it for dependents also halves that string churn. Foundations still colour on move, which
   makes this provably a no-op behaviourally: the dependent's second colour call already overwrote
   the first. `IVisual.MoveVisualizer` is public API and did **not** change shape — the optional
   colour step lives on an `internal virtual MoveVisualizerCore`.
4. **Per-`BuildingDef` memo.** `HasTech()`, `AllowedInWorld()` (→ `IsBuildable` → `AllowedByRules`,
   which does a `BuildingComplete.HasTag(...)`, i.e. a `GetComponent<KPrefabID>`) and
   `UpdateRequirementsState()` (→ `PlanScreen.GetBuildableStateForDef`) are functions of the
   `BuildingDef` alone, but were asked once per *visual* — thousands of visuals across a few dozen
   defs. Unlike `ModAssets.ValidMaterialsCache` these genuinely change as the colony runs (tech
   completes, materials run out), so the memo is **explicitly scoped**: opened and closed around one
   `UpdateVisual` pass, and every call from outside that window (`TryUse`, `IsPlaceable` at
   placement time, the UI) computes fresh exactly as before. Staleness is impossible by
   construction rather than by a generation counter that has to be got right.

Incidentally: passing the new flags meant replacing `List.ForEach(lambda)` with indexed `for` loops
in `UpdateVisual`, which also removes three closure allocations per mouse move.

**Attribution run** (`PerfInstrumentation.PerVisualHotspots`, off by default — instrumenting
methods that run once per visual costs more than the methods do, so a run with it on reports call
counts honestly but **its medians must not be compared with a run without it**; the same run
reported ladder N=2000 at 15.96 / 63.27 ms against 12.83 / 55.19 above). Hotspot totals mix both
arms and every other sweep, so read the **call counts**, not the timings:

| Hotspot | Calls | µs/call |
|---|---:|---:|
| `ApplyRotatedCellAndMove` | 346,800 | 10.0 |
| `ApplyRotation` | **82,800** | 0.2 |
| `ApplyColorIfChanged` | 241,200 | 10.9 |
| `GetVisualizerColor` | 463,200 | 9.1 |
| `ValidCell` | 468,000 | 2.6 |
| `AllowedInWorld` | 465,384 | 2.8 |
| `HasTech` | 465,384 | 0.2 |
| `GetRotatedCell` | 351,600 | 0.4 |

`ApplyRotation`'s count is the decisive one, and it lands **exactly** on the prediction: the ladder
sweep is 3600 visuals summed over its four sizes × 44 iterations, of which only the 22 base-arm
iterations should rotate (3600 × 22 = 79,200) plus one forced redraw per visual at each
`VisualizeBlueprint` (3,600) — 82,800, against 346,800 `ApplyRotatedCellAndMove` calls. In the
optimised arm `ApplyRotation` is called **zero** times during cursor movement. (`TileVisual`
overrides `ApplyRotation` and `ApplyColorIfChanged` without calling `base`, so tiles do not appear
in either count — which is why these two are ladder-only figures.)

Regression suite re-verified **11/11 green on both arms** — the optimised default, and a build with
all four switches forced off — so the A/B comparison is not measuring a broken baseline. 108 unit
tests, 0 skipped.

⚠️ One methodology note for next time: the first attempt at this sweep was lost to a
`TypeInitializationException` in the harness itself (`PerfSwitchAccess`'s `switches` field
initialised before the `ExpectedNames` array it reads — static initialisers run in declaration
order). It aborted the whole benchmark coroutine after `visualize`, and the launcher then sat out
its full 1800 s timeout before reporting. `Resolve()` is now total and a miss degrades to "skip this
sweep"; a sub-sweep that throws should never be able to cost the run its other numbers.

**Not pursued.** Extending the deferred-colour fix to foundation visuals would be *more* correct —
they currently colour before dependents are registered, so a foundation's colour can be stale with
respect to dependent occupancy — but that is a visible behaviour change rather than a pure
deduplication, and does not belong in a performance pass.

### The tile-renderer follow-up — one real bug, and a negative result

The pointer that used to close this section ("the remaining floor is `CustomTileRenderer`'s per-tile
mesh work — `RefreshCell` alone: 1,335,096 calls at 1.0 µs") was chased next, with the allocation
counters now working and a new attribution mode (`run-ingame.ps1 -Attribution`, sentinel
`perf-attribution`) that turns the per-visual hotspots on without a recompile and records **managed
bytes per call** alongside time. Its call counts are honest; its timings are wrapper-dominated and
may only be compared with another attribution run.

**Found: `CleanableVisuals` was never cleared.** `AddVisual` files every `TileVisual` into it, but
`ClearVisuals` emptied only `FoundationVisuals` / `DependentVisuals`. Since `CleanDirtyVisuals` walks
that list at the top of *every* `UpdateVisual`, the per-frame cost grew with every blueprint picked
up in the session, and every `TileVisual` ever drawn stayed reachable for the life of the world — a
leak as well as a tax. The attribution run measured **8,031,000 `Clean()` calls against 150,000 real
re-seats** (53 per re-seat, nearly all on long-destroyed visuals). Fixed by clearing the list in
`ClearVisuals`, after `CleanDirtyVisuals` has let the live ones unregister themselves.

**Tried: batching the refresh fan-out.** Each seat/unseat dirties a five-cell cross per layer
(`RefreshCell` → 5 × `RefreshCellInternal`), and in a solid block those crosses overlap almost
completely — 20 internal refreshes per tile per cursor step. `CustomTileRenderer.BeginBatch` /
`EndBatch` now collect `(player, cell, layer)` into a `HashSet` and flush once. Deferring is safe: a
refresh only reads the *current* tile map, every mutation dirties the cells it can affect, and the
flush happens inside the same frame — so each cell is refreshed once from the finished state instead
of once per neighbour that moved past it. Batches wrap `UpdateVisual`, `ClearVisuals` and
`VisualizeBlueprint`.

| hotspot | before | after |
|---|---:|---:|
| `TileVisual.Clean` | 8,031,000 | 300,000 (−96%) |
| `RefreshCellInternal` | 3,006,720 | 321,560 (−89%) |
| `BlockTileRenderer.Rebuild` | 3,055,012 | 369,852 (−88%) |
| `AddTileBlock` / `RemoveTileBlock` | 150,000 | 150,000 (unchanged by design) |

**First measurement: it bought ~nothing.** `update-visual-tile` N=2000 went 23.26 → 22.76 ms (−2%),
allocation 2,444 → 2,448 KB — both inside the ±10% noise floor. It was kept anyway, on the argument
that the fixture's tiles sit in dug-out space where a `Rebuild` is at its cheapest, with a note that
the measurement to run before re-litigating it was a drag across *existing* tiles.

**That measurement was then run, and it reverses the verdict: the batching is worth 25-31%.**
Interleaved arms (a temporary `PerfSwitches.RefreshBatching` flipped every iteration — the method
this section prescribes; scaffolding since deleted), one run:

| drag | batch on | batch off | |
|---|---:|---:|---|
| empty space, N=1000 | 8.57 ms | 11.38 ms | **−24.7%** |
| empty space, N=2000 | 15.41 ms | 20.91 ms | **−26.3%** |
| over real finished tiles, N=1000 | 8.91 ms | 12.89 ms | **−30.8%** |

Allocation is identical between arms (732 vs 736 KB at N=2000), confirming the refresh path is not
where the per-frame garbage comes from.

**Why the first measurement missed it — the lesson worth keeping.** The −2% sweep dragged a blueprint
across *vacuum*; the A/B above ran after the `use` and `create` sweeps had left real build orders and
finished tiles in those cells. **The batching's value scales with how occupied the cells under the
cursor are** — ~0 in empty space, ~30% over a real base, which is where players actually drag
blueprints. A per-frame optimisation measured over empty terrain is measured in the one condition
that cannot show it working.

> ⚠️ **The *mechanism* this section originally gave for that was wrong, and is retracted.** It read:
> *"`RefreshCellInternal` looks up `Grid.Objects[cell, layer]`, finds nothing, and returns before
> reaching the `KAnimGraphTileVisualizer.Refresh()` that makes a refresh expensive."* That was
> inferred from the dense-vs-sparse gap, never measured — and measuring it refutes it.
>
> An attribution run patched `KAnimGraphTileVisualizer.Refresh` directly: **0 calls**, against
> 76,482 `RefreshCellInternal` calls, in a run whose dense sweep reported 1000/1000 cells occupied.
> The patch resolved (the run log's registration summary confirms all 56 targets bound), so the zero
> is the measurement, not a broken pin.
>
> `RunUpdateVisualDenseSweep`'s reachability probe says why: over 16 sampled cells on
> `TileLayer=FoundationTile`, **16 carry a `GameObject` and 0 of those carry a
> `KAnimGraphTileVisualizer`**. So it is not the `Grid.Objects` lookup that comes back empty — that
> one succeeds — it is the component lookup. A finished `Tile`'s art comes from
> `CustomTileRenderer`'s block-tile atlas, not from an anim-graph visualizer, so that branch is
> **unreachable for tile blueprints entirely**, occupied cells or not. What a refresh actually costs
> is `BlockTileRenderer.Rebuild` (0.3 µs/call) plus two lookups, and the batching wins by calling
> `Rebuild` 9× less often — not by skipping an expensive `Refresh()`.
>
> **Still open: why `Rebuild` costs more over occupied cells.** That is the only remaining
> explanation for the 25–31%, and it is *not* measured here — writing it in as fact would repeat the
> error being corrected. `Rebuild`'s hotspot count is polluted by Klei's own renderers (124,768 calls
> against our 76,482 refreshes), so settling it needs per-sweep accumulator snapshots to compare its
> µs/call between a vacuum-only and a dense-only sweep. The A/B result itself (25–31%, interleaved)
> stands regardless — only the explanation was wrong.

**And the refresh path is now ~2% of the frame, which closes it.** From the same run, the whole of
`UpdateVisual` against the refresh path inside it:

| hotspot | calls | total | µs/call |
|---|---:|---:|---:|
| `UpdateVisual` | 324 | 2,251.5 ms | 6,949 |
| ↳ `ApplyRotatedCellAndMove` | 226,800 | 930.3 ms | 4.1 |
| ↳ `StoreOccupiedArea` | 226,800 | 130.7 ms | 0.6 |
| ↳ `RefreshCellInternal` | 76,482 | **44.3 ms** | 0.6 |
| ↳ ↳ `BlockTileRenderer.Rebuild` | 124,768 | 36.8 ms | 0.3 |
| ↳ ↳ `KAnimGraphTileVisualizer.Refresh` | **0** | — | — |

After batching and delta-seating, the entire refresh path costs **44 ms against `UpdateVisual`'s
2,251 ms** — under 2%, and most of that is Klei's `Rebuild`. Two independent attribution runs
reported identical call counts (76,482 / 124,768 / 0), so this is stable, not a sampling artifact.
**There is no further win available here**; the per-visual move and colour path is 20× larger.

> ⚠️ **A harness bug hid part of this, and is fixed.** `ApplyRotatedCellAndMove`'s hotspot was pinned
> to a five-type parameter list; the shared-parent-transform change added `bool moveTransform = true`
> and the pin silently stopped matching, so it read **0 calls** from that commit until this one — and
> a hotspot reading zero is indistinguishable from a code path that never ran, which is exactly how
> it survived. It now resolves by widest-overload (the narrow overload forwards to the wide one, so
> patching both under one accumulator would double-count, per `KInstantiate`), and `Apply` ends with
> a summary naming any target that failed to bind: *"all 56 target(s) resolved"*, or a loud list of
> the ones that did not. The 4.1 µs/call figure above is the first time that method has been
> measured. The shared-parent commit's own conclusion is unaffected — it rested on an interleaved
> A/B, not on attribution.

`update-visual-tile-dense` is a permanent sweep for exactly this reason (it reuses the finished tiles
`create` already built, so it costs no extra region). Note it is *cheaper* than the sparse drag —
8.91 vs 11.70 ms at N=1000, 80 vs 596 KB — because over an identical finished tile
`SameBuildingAlreadyFinishedInPlace` short-circuits `GetVisualizerColor` before it reaches
`ValidCell`. So the expensive per-frame case is **empty space**, where the game is asked whether a
cell nobody occupies is a legal build location; the dense drag is the cheap one. Dense-vs-sparse is
therefore not a single-variable comparison — only the interleaved A/B isolates the batching.

**Where the per-frame time actually is**, from the same run: tiles cost ~11.4 µs/visual/frame,
dependents ~6.1 µs. The ~6 µs both pay is `Visualizer.transform.SetPosition` (a native Unity write
per visual) plus colour evaluation; the extra ~5.3 µs tiles pay is the `AddTileBlock` /
`RemoveTileBlock` dictionary churn itself, which neither change above touches.

> ⚠️ **An earlier revision of this section attributed ~460 KB of the per-frame allocation to
> `GetVisualizerColor` (230 B/call) and blamed the `failReason` string in Klei's
> `IsValidPlaceLocation`. That was the harness measuring itself** — `TimerPrefix` called
> `Stopwatch.StartNew()`, and a `Stopwatch` is a class, so every instrumented call allocated ~40
> bytes. A child hotspot's prefix runs *inside* its parent's measured window, so each patched child
> charged its wrapper's allocation to its parent: the same method read 230 B/call, then 368 B/call
> once four more of its children were patched, with no code change in between. The wrapper now uses
> `Stopwatch.GetTimestamp()` (a `long`, no allocation). Corrected figures below.

**The colour path, measured properly** (`-Attribution`, allocation-free wrappers):

| hotspot | calls | µs/call | bytes/call |
|---|---:|---:|---:|
| `GetVisualizerColor` | 240,000 | 5.79 | **56** |
| ↳ `ValidCell` (Klei's `IsValidPlaceLocation`) | 244,800 | 2.53 | 44 |
| ↳ `UpdateRequirementsState` | 277,200 | 1.18 | 11 |
| ↳ `AllowedInWorld` / `HasTech` / `LocalPlayerId` / `SameBuildingAlreadyFinishedInPlace` / `CanForceRebuild` | ~240,000 each | ~0.2 | 0 |

A wrapper costs ~0.2 µs, which is the floor those trivial methods are reading, so treat anything near
it as unmeasured rather than free.

**Why the colour path was not optimised.** Two reasons, both from this table. Its allocation is ~56
B/call — roughly 112 KB of a 2000-tile frame, under 10%, not the 460 KB the retracted figure claimed.
And there is no safe cache key: on a drag **every visual gets a new cell every frame**, so a
per-(visual, cell) colour memo never hits in the one case that matters. Caching across frames by
(def, cell, orientation) *would* hit — a translating blueprint reuses its neighbours' cells — but the
answer depends on world state at that cell and on the blueprint's own occupancy map, which differ at
the fringe; the failure mode is a mis-tinted preview, which is a visible behaviour change and does
not belong in a performance pass. The per-frame multiplayer check (`LocalPlayerId` → external
`SessionInfoAPI.LocalUserID`, once per visual per frame) was a suspect and measured **0 B, 0.19 µs**:
hoisting it out of the loop would buy nothing.

> ⚠️ **Attribution-mode allocation is perturbed, like its timings.** The same mod code reports
> 2,448 KB/frame (`update-visual-tile` N=2000) in a normal run and 1,316 KB/frame under
> `-Attribution`: millions of `GC.GetTotalMemory` probes change GC behaviour enough to halve the
> measured delta. Compare attribution allocation only with another attribution run, and take
> frame-level allocation from a normal `-Perf` run.

### Delta-seating the tiles — the tile surcharge is gone

Lever 2 below, implemented. Two changes, because either one alone would have been cancelled out by
the other:

1. **The renderer reconciles instead of unseat-then-reseat.** `TileVisual` now tells
   `CustomTileRenderer.NoteCellChanged` *that* a cell changed (recording what it held at first touch)
   and updates `ActiveTileVisuals` eagerly; the batch flush compares each touched cell's before/after
   def and only then removes/adds blocks and dirties crosses. On a cursor move every interior cell of
   a translating blueprint is vacated by one tile and re-filled by its neighbour **with the same
   def** — before == after, so it costs nothing. Only the leading and trailing edges do work.
   O(N) → O(perimeter). Outside a batch the original immediate path is unchanged.
2. **The colour cache outlives one update.** `CleanDirtyVisuals` was wiping `ColoredCells` every
   frame, which made every tile's colour look changed to `VisualsUtilities.SetTileColor` and
   re-dirtied a five-cell cross per tile per move — the same O(N) churn arriving by a different
   route, and enough to cancel fix 1 completely. It is now cleared in `ClearVisuals` instead, i.e.
   once per blueprint put down. Stale entries are harmless: `GetCachedCellColor` is only consulted
   for cells that carry a tile block.

| sweep | before | after | |
|---|---:|---:|---|
| `update-visual-tile` N=1000 | 11.70 ms / 596 KB | **6.86 ms / 68 KB** | −41% |
| `update-visual-tile` N=2000 | 21.38 ms / 1,312 KB | **11.76 ms / 132 KB** | −45% time, −90% alloc |
| `update-visual-tile-dense` N=1000 | 8.91 ms / 80 KB | **4.87 ms / ~0 KB** | −45% |
| `visualize` N=2000 | 68.19 ms / 5,108 KB | **46.19 ms / 476 KB** | −32% time, −91% alloc |
| `update-visual-ladder` N=2000 (control) | 11.82 ms | 11.89 ms | +1%, unchanged |

The decisive pair is the last two rows: tiles now cost **11.76 ms against dependents' 11.89 ms** at
the same N. The ~5.3 µs/visual tile surcharge that every measurement above carried is gone — tiles
became as cheap as visuals that never touch the tile renderer at all, which is what "interior cells
do nothing" predicts. The unchanged ladder control rules out a machine-wide drift.

**What guards it:** `tile-seating-map-tracks-the-drag` (12th regression case) drags a 3x3 tile
blueprint — the smallest footprint that *has* an interior cell — one cell at a time and then further
than its own width, asserting after each step that every footprint cell is seated with the right def,
that **the ring just outside it is empty** (the assertion that catches a tile left behind), and that
nothing is seated after `ClearVisuals`. The map is the assertable half; whether the tile *art* looks
right while dragging is a human judgement and belongs to the
[smoke-test checklist](smoke-test-checklist.md). That case failed on its first run (`6/9 seated,
3 stray`) because the default `BottomCenter` anchor shifts a 3-wide blueprint one cell in x — the
case now pins `_state` to `BottomLeft`, the same reflection `PerfRunner` uses.

### The shared parent transform — 3-5%, not the headline it was billed as

Lever 1, implemented. Every anim-backed visualizer is parented to one root per player
(`BuildingVisual.VisualRoots`); a plain cursor move translates **the root once** and passes
`moveTransform: false` down, so each visual still updates its cell for validity and colour but writes
no transform. Rotation and flip change the offsets themselves and fall back to per-visual writes,
gated by the `applyRotation` flag that already existed. Visuals on the shared placeholder are never
parented (nothing reads their position, and moving one would drag every tile sharing it), and
`DigVisual` / the note visuals are not `BuildingVisual`s, so they position themselves as before.

Interleaved A/B, one run:

| | on | off | |
|---|---:|---:|---|
| `shared-parent-ladder` N=2000 (anim-backed, parented) | 7.30 ms | 7.67 ms | **−4.7%** |
| `shared-parent-ladder` N=1000 | 4.62 ms | 4.78 ms | **−3.3%** |
| `shared-parent-tile` N=2000 (control — shared placeholder, no transform) | 6.98 ms | 6.94 ms | +0.6% |
| `shared-parent-tile` N=1000 | 4.45 ms | 4.45 ms | +0.1% |

**A between-run pair had read −11% / −9% for ladder and −6% for tile. The interleaved arms put the
real figures at −4.7% / −3.3% and the tile control at zero** — so most of that apparent win, and all
of the tile's, was drift. This is the third change in this section predicted to be large that
measured small (the refresh batching read as nothing and was worth 25-31%; the colour path's
allocation was an instrumentation artifact), and the only reason any of the three is stated correctly
here is that the number was checked rather than reasoned about.

Why it is small: N `SetPosition` calls were never the bottleneck. Unity still propagates the parent's
move to N children in native code, so the saving is the managed interop, not the work. The dominant
per-visual cost is the colour evaluation.

**What guards it:** `preview-follows-the-cursor` (13th case) drags a 3-tall `Ladder` blueprint —
whose preview carries a `KBatchedAnimController`, so it is parented and rendered from its transform —
and asserts every visualizer's **world position** moved by exactly the drag distance. Asserting the
cell field instead would have passed even with the art frozen, since the cell updates either way. The
open question this answers is whether a `KBatchedAnimController` notices a *parent* move at all: it
caches its position for the anim batch, and if it did not, previews would render at stale positions
while every other assertion still passed. It does, confirmed by the assertion and by the screenshots
either side.

**One lever left:**

1. ~~**One shared parent transform.**~~ Done, above.
3. ~~**The colour path.**~~ **Tried and rejected** — see *The colour path, measured properly* above.
   Its allocation was an instrumentation artifact, and a drag gives every visual a new cell each
   frame, so there is nothing safe to cache. What is left there is Klei's `IsValidPlaceLocation`
   (~2.5 µs/call), reachable only by reimplementing the game's own validity rules.

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
      Listed in `OniMods.slnx` for the IDE but excluded from CLI solution builds
      (`<Build … Project="false" />`) so it never races the mod's in-place ILRepack; built directly
      by `test/run-ingame.ps1`, which builds the mod first so the referenced DLL is current.
- [x] Include the [§7](#7-performance-measurement-separate-mode) perf mode, or add it later? — **built**,
      covering blueprint import, placement (`visualize`/`use`), and creation (`test/run-ingame.ps1 -Perf`).

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
- [x] Perf mode follow-ups (the user's stated next steps): placement of large blueprints
      (`visualize`/`use`, found + fixed a fog-of-war and an anchor-shift-wraparound bug along the
      way) and creation of large blueprints (`CreateBlueprint` over a big captured area, ~1.8
      ms/building, cleanly linear). See §7.
- [x] Perf mode, selection-screen open: library-size and preview-size sweeps (cold/warm list,
      both visualizer branches) plus `TimeOp`'s new `settleFrames` / `idle-frame` baseline for work
      that lands after the call returns. Finding: the preview rebuild is ~76% of an open and the
      cost is per-building GameObject instantiation, not the `KBatchedAnimController` (2.4 µs per
      `OnSpawn`); the file list is 13%, worst on a session's first open. **Fixed** by caching the
      drawn preview on (blueprint instance, `ContentRevision`) — reopening an unchanged blueprint
      is 84-92% cheaper, 11/11 green. Second pass on the file list (cache the built list, stop
      double-building it after a search, de-O(n²) the default date sort): reopening a
      500-blueprint folder 126.1 -> 25.9 ms, 11/11 green. See §7.
- [x] Perf mode, `update-visual`: the mod's only per-frame path (`UseBlueprintTool.OnMouseMove` ->
      `BlueprintState.UpdateVisual`), swept over foundation (`Tile`) and dependent (`Ladder`)
      blueprints and measured with A and B **interleaved inside one run**, the method the A/A
      section prescribes. Four fixes — rotation gating, integer rotation maths, deferred dependent
      colouring, and a scoped per-`BuildingDef` memo: N=2000 goes 38.0 -> 23.5 ms (tile) and
      55.2 -> 12.8 ms (dependent), i.e. from three dropped frames per cursor step to under one.
      11/11 green on both arms. See §7.
- [x] Allocation measurement: `GC.GetAllocatedBytesForCurrentThread` reads 0 under Mono's Boehm GC,
      so it was replaced by `GC.GetTotalMemory` (managed heap) + `Profiler.GetTotalAllocatedMemoryLong`
      (Unity native) with a gen-0 collection guard, and a `-Attribution` run mode that adds
      per-visual hotspots and bytes-per-call. Both counters verified live and reproducible; the two
      candidates that weren't (`GetMonoUsedSizeLong`, the per-thread counter) were deleted rather
      than left in hopefully. See §7 **Allocation**.
- [x] Tile-renderer follow-up: found and fixed a real leak (`CleanableVisuals` never cleared - every
      `TileVisual` ever drawn stayed reachable and was walked on every cursor move, 8.0M `Clean()`
      calls against 150k real re-seats), and batched the refresh fan-out (-89% `RefreshCellInternal`).
      The batching first measured as a null result over empty terrain; an interleaved A/B over
      occupied cells put it at **-25 to -31%**. 11/11 green. See §7 *The tile-renderer follow-up*.
      (The mechanism originally given for that gap — an expensive `KAnimGraphTileVisualizer.Refresh`
      skipped over empty cells — was measured and **retracted**: that branch is unreachable for tile
      blueprints. See the retraction note in §7.)
- [x] `update-visual-tile-dense`: a permanent drag sweep over real finished tiles (reuses the
      rectangle `create` builds, so it costs no extra region). Added because every tile-renderer
      number before it came from a drag across empty dug-out space - the one condition in which the
      refresh work being optimised does not happen.
- [x] Delta-seat the tiles: the per-frame path no longer unseats and re-seats every tile, it records
      which cells changed and reconciles them at the batch flush, and the colour cache now outlives a
      single update. `update-visual-tile` N=2000 **21.38 -> 11.76 ms and 1,312 -> 132 KB/frame**; the
      tile surcharge over dependents is gone entirely (11.76 vs 11.89 ms). Guarded by a new
      `tile-seating-map-tracks-the-drag` case; 12/12 green. See §7 *Delta-seating the tiles*.
- [x] Shared parent transform: every anim-backed visualizer is parented to one root per player, so
      a plain cursor move is one transform write instead of N. Interleaved A/B: **−4.7% / −3.3%** on
      anim-backed visuals, ~0 on the tile control (a between-run pair had read −11%, nearly all
      drift). Guarded by `preview-follows-the-cursor`, which asserts world positions rather than
      cells — a `KBatchedAnimController` caches its position for the anim batch, and whether it
      notices a *parent* move was the one thing no other assertion could reach. See §7.
- [ ] **Per-frame cost from here.** All three levers are done or rejected; the path is no longer
      obvious, so measure before choosing. What the numbers now say: the dominant per-visual cost is
      the colour evaluation, and the expensive case is a drag over **empty space**, where
      `ValidCell` → Klei's `IsValidPlaceLocation` asks whether a cell nobody occupies is a legal
      build location. Caching that needs an invalidation condition nobody has found a safe one for
      (§7, *The colour path*) — the remaining idea is to reimplement the parts of the game's validity
      rules that apply to a 1x1 tile, which is a correctness risk, not a perf trick.
      **The refresh half of this is now closed with evidence**: attribution puts the whole refresh
      path at 44 ms against `UpdateVisual`'s 2,251 ms (under 2%), so whatever is left is not there.
      That measurement also retracted the mechanism §7 had given for the batching win, and fixed a
      pinned-signature bug that had `ApplyRotatedCellAndMove` reading 0 calls for two commits.
- [ ] **Settle why `Rebuild` costs more over occupied cells.** The only surviving explanation for the
      batching's 25-31%, and currently unmeasured — `Rebuild`'s hotspot count is polluted by Klei's
      own renderers (124,768 against our 76,482 refreshes). Needs per-sweep accumulator snapshots to
      compare its µs/call between a vacuum-only and a dense-only sweep. Low value (the path is 2% of
      the frame); worth doing only if the explanation is ever load-bearing again.
- [ ] Optional follow-ups: more building types / layers, place-with-settings applied to the built
      object, replacement visualizers over occupied terrain, a committed perf baseline + diff.
- [ ] `run-ingame.ps1` currently removes the dev `Blueprints Expanded` (`mods/dev/BlueprintsV2`)
      from `mods.json` when it's present alongside; ONI re-adds it disabled on next launch, but a
      cleaner disable-in-place would avoid the churn.
