# Changelog

Notable changes to Blueprints Included. Format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

Versions track upstream **Blueprints Expanded**'s numbering rather than this fork's own
history — `7.0.0` is the upstream version this fork branched from, so a bump here means
"aligned with that upstream release", not "seventh release of this fork".

## [Unreleased]

Nothing has been released from this fork yet: there are no tags or GitHub releases, and
`mod_info.yaml` still reports the inherited `7.0.0`. Everything below is in `main` and
unreleased.

### Fixed

- **Instant-built buildings no longer spawn at their material's melting point.** Placing a
  blueprint with instant build enabled passed the minimum melting point straight through as
  the spawn temperature, so a basalt insulated tile materialised at 1530 K instead of its
  normal 293 K — hot enough to damage itself and dump that heat into the surrounding cells.
  ([#1](https://github.com/dytterud/BlueprintsIncluded/issues/1),
  [#5](https://github.com/dytterud/BlueprintsIncluded/pull/5))
- **Wide blueprints near the left or bottom map edge could build on unrelated cells.** The
  anchor shift (`BottomCenter` by default, half the blueprint's width) was applied before
  converting to a cell index, and a negative result silently wrapped into a different row
  instead of failing — sometimes onto a cell that still passed validation and got built on.
- **Blueprint creation captured every building's data twice.** A building registered on two
  object layers (a tile sits on its own layer *and* a replacement layer) was found once per
  layer, and the whole per-building capture re-ran each time.
- Removed a redundant tile re-registration during preview.

### Performance

- **Large blueprint previews roughly twice as fast** — 181 ms → 86 ms for a 2000-building
  preview. A tile's preview prefab carries no anim controller and so could never render
  anything; the clone existed only to be handed to a placement check that consults just the
  cell it's given, so one shared placeholder now serves every tile of a given building type
  instead of one clone each.
- **Faster blueprint import** — the valid-materials lookup dominated import cost at ~85 µs
  per building per ingredient, and its inputs are fixed once the game's databases load, so
  it's now cached per material category.

### Added

Development infrastructure; no effect on the mod in-game:

- CI on every push and pull request — offline build plus tests, no game install needed
  ([#13](https://github.com/dytterud/BlueprintsIncluded/pull/13)).
- An in-game regression harness that boots the game, loads a fixture colony, runs assertions
  against the live blueprint pipeline and quits — covering the colony-dependent behaviour
  unit tests structurally can't reach. Plus a perf mode for timing blueprint operations.
- Unit test projects (xUnit), and a manual [smoke-test checklist](docs/smoke-test-checklist.md)
  for what neither tier covers.
- An automated upstream scan that opens issues for fixes and features landing in upstream
  `BlueprintsV2/` or `UtilLibs/` — see [docs/upstream-sync.md](docs/upstream-sync.md). This
  fork has no shared git history with upstream, so nothing can be cherry-picked.
- [CONTRIBUTING.md](CONTRIBUTING.md), [NOTICE](NOTICE), and issue forms.

### Changed

Internal only; no in-game behaviour change:

- Nullable reference types enabled across the mod, with nullable warnings promoted to build
  errors.
- File-scoped namespaces, 4-space indentation, `EnforceCodeStyleInBuild` (style violations
  are build errors), central package management, and PolySharp for language-feature polyfills.
- Repository restructured to a conventional `src/` + `test/` layout. The C# root namespace
  stays `BlueprintsV2` deliberately — renaming it would break save compatibility and every
  translation file.

### Fork baseline

Forked from upstream [`0bd6de6`](https://github.com/Sgt-Imalas/Sgt_Imalas-Oni-Mods/commit/0bd6de6)
(2026-09-05). History was squashed, so there is no merge-base with upstream; the fork point was
later pinned by content comparison rather than by date. Not yet ported from upstream:
see the open [`upstream-sync`](https://github.com/dytterud/BlueprintsIncluded/issues?q=is%3Aissue+is%3Aopen+label%3Aupstream-sync)
issues.

[Unreleased]: https://github.com/dytterud/BlueprintsIncluded/commits/main
