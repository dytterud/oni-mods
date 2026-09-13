# CLAUDE.md

Oxygen Not Included mods, C# / `netstandard2.1`. One repo, one project per mod under `src/`,
shared build machinery at the root. Per-mod instructions live in that mod's own `CLAUDE.md`
(e.g. [src/BlueprintsIncluded/CLAUDE.md](src/BlueprintsIncluded/CLAUDE.md)) — read both.

## Build & test

```
dotnet build OniMods.slnx -c Release -p:OfflineBuild=true   # no game install needed
dotnet test                                                 # from repo root, offline
powershell test/run-ingame.ps1                              # asserts inside a real colony
```

**You can test in-game — don't assume otherwise.** `test/run-ingame.ps1` builds the mod and the
harness, launches ONI via Steam, loads a fixture colony, runs assertion cases against the live
game, writes JUnit XML and quits on its own. It needs a real install configured
(`Directory.Build.props.user`) and takes a few minutes with the game window up, so ask first if
the user is at the machine — but it is the right tool whenever a change touches placement,
visualizers, capture or UI, and it is how you verify what `dotnet test` structurally cannot
reach. Use `powershell` if `pwsh` is not on PATH. See [harness/README.md](harness/README.md).

Offline build auto-activates when no ONI install is configured; it compiles against the
committed reference assemblies in `lib/`. For in-game testing against a real install, see
[README.md](README.md) (`Directory.Build.props.user` + `dotnet tool restore` + `-c Debug`).
Test details: [test/README.md](test/README.md).

## Layout

| Path | What |
|------|------|
| `src/<Mod>/` | one project per mod; its `CLAUDE.md`, `README.md` and `CHANGELOG.md` live with it |
| `src/UtilLibs/` | vendored helper lib, ILRepacked into each mod dll that uses it |
| `test/*` | one xUnit project per production project (net8.0) |
| `harness/` | dev-only in-game regression harness — a separate mod; in the slnx for the IDE but excluded from CLI solution builds / `dotnet test`; run via `test/run-ingame.ps1` |
| `docs/` | repo-wide docs; per-mod docs in `docs/<mod-slug>/` |
| `lib/` | committed refasmer reference assemblies — **compile only, cannot execute** |
| `Directory.Build.props` / `.targets` | shared build + mod-packaging pipeline |

## Adding a mod

Add `src/<Mod>/<Mod>.csproj` with `IsMod`, `IsPacked`, `ModName`, `ModDescription` and
`Version`; the `AssemblyName` becomes the ONI `staticID`. The shared targets then generate
`mod.yaml` / `mod_info.yaml` / `LauncherMetadata.json`, ILRepack the dependencies in, ship
`LICENSE` + `NOTICE`, and deploy to `Builds/<Mod>/` (offline) or the ONI dev mods folder (real
install). Anything at the root of the mod's `ModAssets/` — including a Workshop `preview.png` —
is copied to the mod folder root. Add the project to `OniMods.slnx` and a test project under
`test/`.

`$(RepoUrl)` and `$(SupportedContent)` are repo-level properties in `Directory.Build.props`; a
mod that genuinely supports only one DLC overrides `SupportedContent` in its own csproj.

## Conventions

- `.editorconfig` is authoritative. C# files are **UTF-8 with BOM**, CRLF, 4-space indent.
- **File-scoped** namespaces (`namespace X;`) for mod projects and their test projects
  (`csharp_style_namespace_declarations = file_scoped:warning`; IDE0161 surfaces as a build
  warning via `EnforceCodeStyleInBuild`, not an error). Vendored `UtilLibs` /
  `UtilLibs.Tests` stay **block-scoped** — the `.editorconfig` has a scoped override for
  those paths. `using` directives outside the namespace, System first. No `this.`
  qualification.
- **No `partial` types** — every class / struct / record / interface is declared in one
  place. Not build-enforced (no analyzer id covers it); keep it out in review.
- `ImplicitUsings` is **enabled** for mod projects and their test projects, **disabled**
  for `UtilLibs` / `UtilLibs.Tests` (vendored code relies on unqualified `UnityEngine`
  names) — add explicit `using`s when editing UtilLibs.
- `Nullable` is **enabled** for mod projects, **disabled** for vendored `UtilLibs` (same split
  as `ImplicitUsings`). The CS86xx family is in `WarningsAsErrors` (via the `nullable` token),
  so a regression fails the build. Klei-injected / FUI-builder-wired fields use `= null!;`
  (assigned before any use, never actually null in-game); genuinely-optional values use `T?`.
  Test projects clear `WarningsAsErrors`, so nullable stays advisory there.
- `EnforceCodeStyleInBuild=true`: IDExxxx style violations **fail the build** for production
  projects. `WarningsAsErrors=CS0618;CS0612;nullable`: calling an `[Obsolete]` game API or
  introducing a nullable warning breaks the build. `CS0649` is suppressed (Klei injects
  fields by reflection).
- `LangVersion=14.0` on `netstandard2.1` works via PolySharp compile-time polyfills.

## Harmony patching

Patches live in `src/<Mod>/Patches/*.cs`: an outer container class per file, inner
`[HarmonyPatch(typeof(T), nameof(T.M))]` class with `static Prefix` / `Postfix` using injection
params (`__instance`, `__result`, `___privateField`). Patches auto-apply via `UserMod2.OnLoad`
→ `harmony.PatchAll` — do **not** add an explicit `PatchAll`. Config is a PLib
`SingletonOptions<Config>`; user-facing strings go through `STRINGS.cs`; translations are
`ModAssets/translations/*.po`.

## Gotchas

- `Builds/` and `PublicisedAssembly/` are generated and gitignored.
- `mod.yaml`, `mod_info.yaml`, `LauncherMetadata.json` are generated by
  `Directory.Build.targets` — edit the targets, not the output files.
- Game-touching tests use `[RequiresGameInstallFact]` / `[RequiresGameInstallTheory]` and
  auto-skip in offline builds, so `dotnet test` legitimately reports two different numbers.
  What matters is the skip count: **zero skipped with a real install, every gated test skipped
  offline**. Anything skipped *despite* an install means the game assemblies aren't reaching
  the test output — see `CopyGameAssembliesForRuntime` in
  `test/BlueprintsIncluded.Tests/BlueprintsIncluded.Tests.csproj`.

## Contributing

- Commit messages end with: `Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>`
- PR descriptions end with the "Generated with Claude Code" footer and follow
  [.github/pull_request_template.md](.github/pull_request_template.md).
