# Tests

`dotnet test` from the repo root runs everything.

## Layout

One test project per production project (mirrors `src/`):

| Project | Targets | Runs offline? |
|---|---|---|
| `UtilLibs.Tests` | `src/UtilLibs` | yes |
| `BlueprintsIncluded.Tests` | `src/BlueprintsIncluded` | only the pure-BCL tests |

Framework: **xUnit**, test projects target **net8.0** (the mod is `netstandard2.1`, which is
an API contract, not a runtime — tests need a real one).

## The game-assembly constraint

The committed `lib/*.dll` are **reference assemblies** (refasmer output): they compile but
cannot execute. So any test that touches a Klei / Unity type at runtime is skipped unless a
real ONI install is configured via `Directory.Build.props.user` (see
`Directory.Build.props.default`). Those tests use `[RequiresGameInstallFact]` /
`[RequiresGameInstallTheory]` — see `BlueprintsIncluded.Tests/GameAssemblies.cs`.

So the counts differ by build, and both are correct. Don't compare the passed count against a
number written down somewhere — it changes with every test added. The **skip count** is the
signal:

| | expected |
|---|---|
| `dotnet test` with an install configured | **nothing skipped** |
| `dotnet test -p:OfflineBuild=true` | **every gated test skipped** |

The gate needs the real assemblies loadable *at runtime*, not just at compile time. Every game
`<Reference>` in `Directory.Build.props` is `<Private>False</Private>` — correct for the mod,
which must not ship copies of assemblies ONI already provides — so the
`CopyGameAssembliesForRuntime` target in `BlueprintsIncluded.Tests.csproj` copies them into the
test output for non-offline builds. Offline builds copy nothing and the gate skips.

If you add a gated test that reflects over a new widget type, its Unity assembly may need adding
to that target's list; the symptom is a `FileNotFoundException` out of
`RtFieldInfo.InitializeFieldType`, not a skip.

Pure logic with no game types (string/collection helpers, math, JSON shaping) runs
everywhere. That is most of what is currently unit-testable — the production code has few
seams between logic and game state. Widening coverage mostly means extracting plain
functions / small interfaces, not more test infrastructure.

## Manual smoke test

The blueprint pipeline (create → save → reload → place, notes, folders, MP) needs a
running colony, so it can't be unit-tested. Run [`docs/smoke-test-checklist.md`](../docs/smoke-test-checklist.md)
in-game after any change to the blueprint data, tools, visualizers, or UI.

Automating that pass by launching the game and asserting inside a live colony is feasible
on a dev machine (not in CI). A proof-of-concept harness exists in
[`harness/`](../harness/README.md) — a separate dev-only mod; it's in the solution for the IDE but
excluded from CLI solution builds and **not** run by `dotnet test`. Drive it with
[`test/run-ingame.ps1`](run-ingame.ps1). Background:
[`docs/in-game-regression-testing.md`](../docs/in-game-regression-testing.md).

## Shared config

`test/Directory.Build.props` chains the repo-root props (for the game `<Reference>` items and
offline `./lib` detection), then overrides the target framework and opts every test project
out of the mod build pipeline (mod.yaml, ILRepack, publicizer, copy-to-dev-folder).
