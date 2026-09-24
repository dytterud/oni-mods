# CLAUDE.md — Blueprints Included

Mod-specific instructions. The repo-wide ones (build, conventions, Harmony, adding a mod) are in
the root [CLAUDE.md](../../CLAUDE.md) — read both.

Standalone fork of Blueprints Expanded (`BlueprintsV2`) by SGT_Imalas. The C# root namespace is
still `BlueprintsV2`; the mod `staticID` / `AssemblyName` is `BlueprintsIncluded`, so ONI treats
it as its own mod. **Keep the `BlueprintsV2` root namespace** — renaming it breaks KSerialization
save compatibility and every `.po` translation.

History was squashed, so there is no merge-base with upstream. What may be taken from upstream,
and how, is in [CONTRIBUTING.md](../../docs/blueprints-included/asset-provenance.md#the-rule).
Upstream relicensed away from MIT on 2026-09-07: behaviour may be taken, code never.
Implementing an `upstream-sync` issue is clean-room: work from the issue alone, in a session that
has not read upstream's code for that change and does not access upstream. Never decompile
upstream's released DLLs.

## Layout

Sub-namespaces mirror folders: `Patches/`, `Tools/`, `BlueprintData/`, `UnityUI/`,
`Visualizers/`, `ModAPI/`. Entry point is `Mod.cs`; `Config.cs` and `STRINGS.cs` sit beside it.

## Testing this mod

The blueprint pipeline needs a running colony, so much of it is only reachable in-game. Add
harness cases in `harness/BlueprintsIncludedHarness/HarnessCases.cs`; the harness can also
capture screenshots. See
[docs/blueprints-included/in-game-regression-testing.md](../../docs/blueprints-included/in-game-regression-testing.md)
for how that works and §7 for the performance mode.

The [smoke-test checklist](../../docs/blueprints-included/smoke-test-checklist.md) is the human
fallback for what the harness cannot assert — anything needing an eye on the screen rather than
an assertion, tile art while dragging being the standing example.

## Gotchas

- `ScreenReferenceBindingTests` (game-gated) reflects over every `KMonoBehaviour` and fails if a
  non-nullable `Component`/`GameObject` field is only ever `= null!` and never bound in code —
  it catches "declared a widget, forgot to wire it in `Init()`".
- The mod detects upstream **Blueprints Expanded** at load and queues a main-menu warning, but
  **do not rely on that warning being seen**: measured 2026-09-13, the pair crashes inside
  `Assets.OnPrefabInit` (upstream's `SpritePatch` prefix) and ONI's crash handler replaces the main
  menu, so the queued dialog is never drained. The dependable signal is the log line `incompatible
  mod found: BlueprintsV2`.
- Do not run this mod alongside upstream **Blueprints Expanded**. The `staticID`s differ
  (`BlueprintsIncluded` vs `BlueprintsV2`) so ONI will happily load both, but they share the
  `BlueprintsV2` root namespace and Harmony-patch the same methods.
