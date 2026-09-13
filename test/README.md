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

## In-game testing

The blueprint pipeline (create → save → reload → place, notes, folders, MP) needs a running
colony, so it can't be unit-tested — but it **can** be tested automatically. Don't assume
in-game verification is out of reach; it isn't, and that assumption has repeatedly led to
changes shipping with "couldn't be verified" when they could have been.

```
powershell test/run-ingame.ps1
```

Builds both mods, launches ONI via Steam, loads a committed fixture colony, runs assertion
cases against the live pipeline, writes JUnit XML and quits on its own. Needs a real install
configured in `Directory.Build.props.user`; takes a few minutes with the game window up, so
check with whoever is at the machine first. Use `powershell` if `pwsh` isn't on PATH.

Add cases in `harness/BlueprintsIncludedHarness/HarnessCases.cs` — each is a coroutine that can
let frames pass, and `PlaceAt` already drives dig → VisualizeBlueprint → [rotate] →
UseBlueprint. It can capture screenshots too. Details:
[`harness/README.md`](../harness/README.md); background:
[`docs/blueprints-included/in-game-regression-testing.md`](../docs/blueprints-included/in-game-regression-testing.md).

The harness is a separate dev-only mod: in the solution for the IDE, but excluded from CLI
solution builds and **not** run by `dotnet test`, so it never races the mod's in-place ILRepack.

What it can't do is judge whether something *looks* right — it asserts values and captures
screenshots for a human to review. [`docs/blueprints-included/smoke-test-checklist.md`](../docs/blueprints-included/smoke-test-checklist.md)
is the fallback for that.

## Shared config

`test/Directory.Build.props` chains the repo-root props (for the game `<Reference>` items and
offline `./lib` detection), then overrides the target framework and opts every test project
out of the mod build pipeline (mod.yaml, ILRepack, publicizer, copy-to-dev-folder).
