# Changelog

Notable changes to Blueprints Included. Format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

Versions are this fork's own, starting at `0.1.0`. They deliberately do **not** track
upstream **Blueprints Expanded**'s numbering: the two codebases have already diverged, so a
shared number would imply a parity that doesn't exist. The mod inherited `7.0.0` at the fork
and was never published under it.

## [Unreleased]

### Added

- **Paste a blueprint straight onto the map.** The snapshot tool's "use last snapshot" key is
  bound to Ctrl+V, so it now does what that key suggests: a blueprint string on your clipboard is
  put in hand, ready to place. With nothing usable on the clipboard it brings back your last
  snapshot, as before. It works from the tool and from anywhere else, where it also switches to
  the tool. Each path says which of the two it did, so the fallback is never silent. Ported from
  upstream Blueprints Expanded, where it was silent and the hotkey's help text still described
  only snapshots. (#97)

- **Save or copy a snapshot from the blueprint panel.** Two buttons appear while placing a
  snapshot: *Save as blueprint*, which asks for a name and keeps the snapshot in your blueprint
  collection, and *Copy to clipboard*, which exports it as the usual shareable string. Snapshots
  are otherwise lost when you leave the game. Ported from upstream Blueprints Expanded, where both
  buttons were wired to the same handler (so Export did nothing) and saving would have thrown,
  because a snapshot has no file location until it is named. (#96)

- **"Copy to Clipboard" for the create-blueprint tool.** A new checkbox under *Auto. Sync to
  Overlays* in the tool's filter menu. With it ticked, a blueprint you create is also exported to
  your clipboard as the same shareable string the blueprint list's export button produces, so it
  can be pasted straight into the blueprint editor website — no round trip through a saved file.
  The copy happens once you confirm the name, so the string carries it, and re-taking an existing
  blueprint copies the updated version. The setting is remembered, and is in the mod options as
  *Create Blueprint Tool Copy to Clipboard* (off by default).

  The snapshot tool had the same checkbox, added at the same time; it is gone again in favour of
  the Copy to clipboard button above, which does the same thing on demand, from the panel you are
  already looking at, without overwriting your clipboard on every snapshot.

- **Snap to Grid.** A new toggle in the blueprint state panel. With it on, clicking places the
  blueprint as usual and dragging places more copies on a grid anchored at the click, for both
  the blueprint tool and the snapshot tool. Two fields set the step between copies. They default
  to the blueprint's exact width and height, so copies sit edge to edge, and they follow its
  rotation. Ported from upstream Blueprints Expanded, with these fixed along the way: a fast drag
  no longer skips copies, a slight wobble off the drag line no longer stops placement, dragging
  back over a copy doesn't place it twice, a click at the map's corner can start a drag, and a
  step of 0 or a mistyped value can no longer break placement. (#79)

- **Detects a conflicting blueprint mod at load.** Upstream Blueprints Expanded, or a second copy
  of this mod, is now found during startup and logged by name, and a warning is queued for the main
  menu. Both carry the same root namespace and patch the same game methods, so running them together
  applies every patch twice.

  Note the limit, which was measured rather than assumed: against the current upstream the pair
  crashes during asset load and ONI's own crash handler appears before the main menu, so the queued
  dialog is never shown. What you get in that case is the log line `incompatible mod found:
  BlueprintsV2`, which names the cause in a Player.log.

### Changed

- **All five translations are complete again.** German and French were missing about half the mod's
  text and fell back to English; Korean, Russian and Chinese had smaller gaps. Every string the mod
  shows is now translated in all five languages, including this fork's own additions, which have
  never existed upstream to be translated. (#87)


- **The "force build over existing buildings" tooltip now describes what the toggle actually
  does.** It said the option replaces a finished building "of the same type" with one built from
  the blueprint's material. The check behind it is broader: any building blocking a blueprint
  building is marked for deconstruction, and planned buildings in the way are cancelled. The
  same-type-different-material case is one path through it, not the whole behaviour. Text only;
  the key had no translations yet, so nothing else changed. (#81)

### Fixed

- **Buildings that attach to a back wall are placed correctly.** Three things were wrong with the
  check that lets a blueprint place them. A building over a *real* back wall was refused, because
  the occupancy test answered "occupied" for every layer wherever a back wall existed, so it
  reported the building's own layer as taken. A multi-cell building was accepted while hanging off
  the end of a back wall, because only the cell it is anchored at was checked. And a rotated
  building recorded the cells of its unrotated shape, so the preview's own occupancy was wrong.
  Measured in-game: a shelf over a buried back wall was refused before this change and accepted
  after; a rotated 3x1 bridge recorded a horizontal row before and the correct vertical one after.

  The check is also stricter in two ways the game already was: it wants a real back wall or an
  actual back-wall building, not merely something sharing that layer, and it no longer reads
  outside the map or across a world boundary for a building sitting at the edge. (#76)
- **Preconfiguring a building no longer punches a hole in the world border.** The preconfigure
  screen spawns a temporary copy of the building at the map's bottom-left corner, inside the
  indestructible border wall. For a building that occupies cells - a tile, a door - that replaced
  the wall, and destroying the copy afterwards left vacuum there, open to space. The wall is now
  restored, with the element, mass and temperature it had. Measured in-game: the corner cell went
  Unobtanium to Vacuum before this change, and stays Unobtanium after. (#80)

- **The note-visibility button's tooltip names the key it is bound to now.** The tooltip was
  written once, when a colony loaded, so rebinding the key mid-game left it showing the old one
  until the next load. It is now rebuilt each time it is shown. With no key bound (the default),
  it no longer ends in "[NONE]". (#68)

- **Selecting a text note no longer writes the note's own text back to it.** Opening a note's
  side screen pushed its title and text into the input fields, and those pushes came back
  through the fields' change handlers, so every selection re-saved the note and fired a
  multiplayer note update. Measured in-game: two write-backs per selection before this change,
  none after. The two clear buttons, which used to be repainted by that stray event, are now
  refreshed explicitly. (#63)

- **A replacement preview can no longer run two placement checks at once.** The guard that stops a
  second check starting was released when a check began rather than when it finished, and the game
  delivers grid-change callbacks immediately rather than on the next frame - so a building placed by
  the check could trigger a second check against half-finished state. Measured in-game: the check ran
  twice before this change, once after. (#64)

- **The blueprint-data API no longer throws when handed a building that has been destroyed.** Both
  read entry points are reflectable by other mods, so the argument comes from outside this mod; they
  now return an empty result instead of failing inside a handler the caller has never heard of.
  (#67)

- **Storage Tiles and Radbolt Chambers can be preconfigured again.** Neither offered the
  "preconfigure" button on a planned building, so their settings could not be set before the
  building was built. The check behind the button asks the building's prefab what data it carries,
  and both answer by reading a live state machine, which a prefab has none of. They now report an
  empty entry instead, which is enough for the button without changing what a blueprint captures.
  (#85)

- **Buildings on the gantry object layer are no longer dropped from a capture when a layer filter
  is active.** The layer had no entry in the object-layer to filter-layer map, and an unmapped
  layer is treated as not allowed, so those buildings were skipped whenever filtering was on. It
  now maps onto the Buildings filter, alongside the other building layers. (#75)

- **Stopped logging a warning for every hauling point in a capture.** A hauling point has neither
  a constructable nor a deconstructable component - it is captured off its own marker component,
  and falls through to the material-category default on purpose. That path logged "had neither
  constructable nor deconstructable component" each time, which was noise rather than a problem.
  The marker lookup is also skipped now when a constructable is present, since that is already
  enough to capture the building. (#73)

- **Hardening: instant-build no longer marks the tile layer for a tile piece with no block-tile
  atlas.** Marking and refreshing the tile layer for a def that has no atlas to draw from cannot
  produce art. No such def is known to exist in the base game; upstream added the same guard
  without saying what hit it, so this is defensive. (#77)

## [0.1.0] - 2026-09-13

First release of the fork. Everything below was developed after the split from
**Blueprints Expanded**; the fork baseline itself is recorded at the end.

### Fixed

- **Instant-built buildings no longer spawn at their material's melting point.** Placing a
  blueprint with instant build enabled passed the minimum melting point straight through as
  the spawn temperature, so a basalt insulated tile materialised at 1530 K instead of its
  normal 293 K — hot enough to damage itself and dump that heat into the surrounding cells.
  ([#1](https://github.com/dytterud/oni-mods/issues/1),
  [#5](https://github.com/dytterud/oni-mods/pull/5))
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
  ([#13](https://github.com/dytterud/oni-mods/pull/13)).
- An in-game regression harness that boots the game, loads a fixture colony, runs assertions
  against the live blueprint pipeline and quits — covering the colony-dependent behaviour
  unit tests structurally can't reach. Plus a perf mode for timing blueprint operations.
- Unit test projects (xUnit), and a manual [smoke-test checklist](../../docs/blueprints-included/smoke-test-checklist.md)
  for what neither tier covers.
- An automated upstream scan that opens issues for fixes and features landing in upstream
  `BlueprintsV2/` or `UtilLibs/` — see [docs/blueprints-included/upstream-sync.md](../../docs/blueprints-included/upstream-sync.md). This
  fork has no shared git history with upstream, so nothing can be cherry-picked.
- [CONTRIBUTING.md](../../CONTRIBUTING.md), [NOTICE](../../NOTICE), and issue forms.

### Changed

Internal only; no in-game behaviour change:

- Nullable reference types enabled across the mod, with nullable warnings promoted to build
  errors.
- File-scoped namespaces, 4-space indentation, `EnforceCodeStyleInBuild` (style violations
  are build errors), central package management, and PolySharp for language-feature polyfills.
- **Version reset to `0.1.0`**, from the `7.0.0` inherited at the fork point. Never published
  under the old number, so nothing sees this as a downgrade.
- Build metadata corrected: the compiled assembly credited upstream's author rather than this
  fork's. Upstream's copyright remains where it belongs, in [LICENSE](../../LICENSE) and
  [NOTICE](../../NOTICE).
- Repository restructured to a conventional `src/` + `test/` layout. The C# root namespace
  stays `BlueprintsV2` deliberately — renaming it would break save compatibility and every
  translation file.

### Fork baseline

Forked from upstream [`0bd6de6`](https://github.com/Sgt-Imalas/Sgt_Imalas-Oni-Mods/commit/0bd6de6)
(2026-09-05). History was squashed, so there is no merge-base with upstream; the fork point was
later pinned by content comparison rather than by date. Not yet ported from upstream:
see the open [`upstream-sync`](https://github.com/dytterud/oni-mods/issues?q=is%3Aissue+is%3Aopen+label%3Aupstream-sync)
issues.

[Unreleased]: https://github.com/dytterud/oni-mods/compare/blueprints-included/v0.1.0...main
[0.1.0]: https://github.com/dytterud/oni-mods/releases/tag/blueprints-included%2Fv0.1.0
