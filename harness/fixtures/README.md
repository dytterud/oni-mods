# Harness fixtures

## `poc-colony.sav`

The colony the harness loads before asserting. It only has to be a **loadable colony** — the
harness places its own known building set into it at runtime (`FixtureBuilder` /
`FixtureLayout.Buildings`), so nothing needs to be pre-built here.

Currently a copy of a fresh (cycle 0) `Cesspool` sandbox save.

### Regenerating

Any small colony works. To refresh it:

1. In ONI, start or load a small colony (sandbox is fine, keeps things deterministic).
2. Let it settle a few seconds, then **Save**.
3. Copy the `.sav` over `harness/fixtures/poc-colony.sav` and commit it (the repo `.gitignore`
   does not exclude `.sav`).

If an ONI update stops the save from loading, regenerate it the same way. Keep
`minimumSupportedBuild` in the harness `mod_info.yaml` (from `TargetGameVersion`) in mind — a
fixture from a much older build may refuse to load.

### The building placement

`FixtureLayout.Buildings` places BuildableRaw tile/ladder pieces at cell offsets from the colony's
**active Printing Pod**, built from Sandstone. If a different fixture save changes the terrain
around the pod, `%TEMP%\bpi-harness\harness.log` logs the pod cell and every placement — adjust the
offsets in `FixtureLayout.cs` so they all land on open starting-area ground. Keep them
BuildableRaw: a wrong-category material gets re-sanitized on blueprint read, which the round-trip
case would (correctly) flag.

### Why a committed save + code-placed buildings

Loading a fixed save gives world-gen / sim determinism; defining the buildings in code (rather
than baking them into the save) means there's no second artifact to keep in sync and the
"expected layout" is the same data that does the placing.
