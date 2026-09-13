# Blueprints Included

An Oxygen Not Included mod for designing, storing and re-placing your favourite builds,
including building settings.

This is a standalone fork of **Blueprints Expanded** (`BlueprintsV2`) by SGT_Imalas, which is
itself a rewrite of the original **Blueprints** mod by Mayall. The C# namespace is still
`BlueprintsV2`; the mod's `staticID` is `BlueprintsIncluded`, so ONI treats it as a separate mod
from the upstream Steam item.

> **Don't run both.** The `staticID`s differ, so ONI will happily enable this and Blueprints
> Expanded at once — but they share a root namespace and patch the same game methods.

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

## Credit

Mayall (original Blueprints), SGT_Imalas (Blueprints Expanded rewrite). See
[NOTICE](../../NOTICE) for the full attribution chain and Klei's terms.
