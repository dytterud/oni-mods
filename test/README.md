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

Pure logic with no game types (string/collection helpers, math, JSON shaping) runs
everywhere. That is most of what is currently unit-testable — the production code has few
seams between logic and game state. Widening coverage mostly means extracting plain
functions / small interfaces, not more test infrastructure.

## Shared config

`test/Directory.Build.props` chains the repo-root props (for the game `<Reference>` items and
offline `./lib` detection), then overrides the target framework and opts every test project
out of the mod build pipeline (mod.yaml, ILRepack, publicizer, copy-to-dev-folder).
