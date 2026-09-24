# Blueprints Included

An Oxygen Not Included mod for designing, storing and re-placing your favourite builds,
including building settings.

This is a standalone fork of **Blueprints Expanded** (`BlueprintsV2`) by SGT_Imalas, which is
itself a rewrite of the original **Blueprints** mod by Mayall. The C# namespace is still
`BlueprintsV2`; the mod's `staticID` is `BlueprintsIncluded`, so ONI treats it as a separate mod
from the upstream Steam item.

> **Don't run both.** The `staticID`s differ, so ONI will happily enable this and Blueprints
> Expanded at once — but they share a root namespace and patch the same game methods.

## Compatibility with Blueprints Expanded

**Staying interchangeable with upstream is a design goal, not an accident.** You should be able to
switch between this and Blueprints Expanded — in either direction, at any time — without losing
anything:

- **Blueprint files.** The on-disk format is untouched: same schema version, same keys, no fields
  added. Blueprints made in either mod open in the other, and the two read the same
  `blueprints/` folder.
- **Saves.** The C# root namespace stays `BlueprintsV2` specifically so KSerialization sees the
  same type names. A colony saved with one mod loads with the other, blueprint-placed buildings
  and their stored settings intact. This is why the namespace has not been renamed to match the
  mod, and why it will not be.
- **Translations.** The `.po` files key off those same names, so existing translations keep
  working.

The one thing that is *not* compatible is running both at the same time — see the warning above.
Switching means disabling one and enabling the other, which is safe; having both enabled is not.

**The limits of that promise.** Compatibility is maintained by not gratuitously diverging, not by
testing against upstream — nothing here runs upstream's build, so a format change landing there
would be found by the [upstream scan](../../docs/blueprints-included/upstream-sync.md) rather than
caught automatically. If upstream ever changes the blueprint schema, this fork follows it rather
than forking the format. If that ever stops being possible, it will be said here plainly rather
than discovered by someone losing a blueprint library.

## How this differs from Blueprints Expanded

**It is the same mod, with bugs fixed and made a lot faster.** No features were added or removed,
and blueprint files are unchanged, so anything you built with upstream still loads here.

**Bug fixes** — see the [CHANGELOG](CHANGELOG.md) for what has been fixed in each release.

**Speed**, measured in-game rather than estimated:

| | upstream | here |
|---|---:|---:|
| Creating a blueprint (1000 buildings) | ~1076 ms | **~45 ms** |
| Dragging a 2000-building preview, per cursor step | 38–55 ms | **~12 ms** |
| Reopening the blueprint dialog (2000-building preview) | ~699 ms | **~74 ms** |
| Importing a 5000-building blueprint | ~505 ms | **~85 ms** |

The practical effect is that dragging a large blueprint went from a few dropped frames per cell of
mouse travel to none. Figures are from one machine and are directional — the method, the caveats
and the things deliberately *not* optimised are in
[§7](../../docs/blueprints-included/in-game-regression-testing.md#7-performance-measurement-separate-mode).

**Testing.** Upstream has none; this fork has CI on every push, unit tests, an in-game harness that
boots the game and asserts against a live colony, and a manual smoke checklist for what assertions
can't see.

**The trade-off, stated plainly:** upstream keeps developing, and this fork does not automatically
get its new work. A scheduled scan opens an issue for each upstream change so nothing is missed
silently, but anything still open is a feature or fix you would have upstream and don't have here —
see the open [`upstream-sync`](https://github.com/dytterud/oni-mods/issues?q=is%3Aissue+is%3Aopen+label%3Aupstream-sync)
issues and [how porting works](../../docs/blueprints-included/upstream-sync.md).

Per-release detail is in [CHANGELOG.md](CHANGELOG.md).

## Install

Unreleased. Until it is on the Workshop, build it (see the [repo README](../../README.md)) and
copy `Builds/BlueprintsIncluded/` into your `Klei/OxygenNotIncluded/mods/local/` folder.

## Docs

| | |
|---|---|
| [CHANGELOG.md](CHANGELOG.md) | what changed, per release |
| [In-game regression testing](../../docs/blueprints-included/in-game-regression-testing.md) | the harness, and §7's performance measurements |
| [Smoke-test checklist](../../docs/blueprints-included/smoke-test-checklist.md) | the manual pass, for what assertions can't see |
| [Upstream sync](../../docs/blueprints-included/upstream-sync.md) | how upstream changes are ported |
| [Asset provenance](../../docs/blueprints-included/asset-provenance.md) | where the UI bundles came from, and why they are pinned |
| [steamdesc.bbcode](steamdesc.bbcode) | the Steam Workshop page text, kept here so it is reviewed and updated alongside the code |

## Credit

Mayall (original Blueprints), SGT_Imalas (Blueprints Expanded rewrite). See
[NOTICE](../../NOTICE) for the full attribution chain and Klei's terms.
