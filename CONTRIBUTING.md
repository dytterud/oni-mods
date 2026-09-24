# Contributing

Thanks for taking a look. This is a small mod with an unusual amount of build
machinery, so the parts most likely to trip you up are collected here.

`CLAUDE.md` covers the same ground tersely for AI assistants; this file is the
human version. If they ever disagree, `.editorconfig` and the build are
authoritative.

## Quick start

No game install needed to build or test:

```bash
dotnet build OniMods.slnx -c Release -p:OfflineBuild=true
```

```bash
dotnet test
```

Offline builds compile against the committed reference assemblies in `lib/`.
Those are metadata only — they compile but **cannot execute** — which is why some
tests skip (see below).

**Run `dotnet test` from the repo root, not against a project file.** Building
`test/BlueprintsIncluded.Tests/BlueprintsIncluded.Tests.csproj` directly leaves
`$(SolutionDir)` undefined, the assembly publicizer can't run, and you get a wall
of `CS0246: The type or namespace name 'Database' could not be found`. That is
not your change breaking; it's the wrong entry point.

## Testing in-game

The blueprint pipeline needs a running colony, so a lot of behaviour can't be
unit-tested. There are three tiers:

| Tier | Command | Needs a game install |
|---|---|---|
| Unit tests | `dotnet test` | no |
| In-game regression harness | `pwsh test/run-ingame.ps1` | yes |
| Manual smoke test | [`docs/blueprints-included/smoke-test-checklist.md`](docs/blueprints-included/smoke-test-checklist.md) | yes |

For the latter two, set up a real install first:

1. Copy `Directory.Build.props.default` to `Directory.Build.props.user` and set
   `GameLibsFolder` (the game's `OxygenNotIncluded_Data/Managed`) and `ModFolder`
   (your `Klei/OxygenNotIncluded/mods/dev`). It's gitignored.
2. `dotnet tool restore` — installs the assembly publicizer and refasmer.
3. `dotnet build OniMods.slnx -c Debug` — copies the mod into your dev
   mods folder.

The harness boots ONI, loads a fixture colony, runs assertions against the live
pipeline, writes JUnit XML and quits. See
[`harness/README.md`](harness/README.md) and
[`docs/blueprints-included/in-game-regression-testing.md`](docs/blueprints-included/in-game-regression-testing.md).

**Run the smoke-test checklist** after changing blueprint data, tools,
visualizers or UI. The harness doesn't cover everything.

### Why `dotnet test` reports different numbers

Two outcomes are both correct — what distinguishes them is the **skip count**:

| | expected |
|---|---|
| with an install configured | nothing skipped |
| `-p:OfflineBuild=true` | every gated test skipped |

The skipped ones are `[RequiresGameInstallFact]` / `[RequiresGameInstallTheory]`
— they touch Klei/Unity types at runtime, which the reference assemblies can't
do. A *third* outcome, tests skipped **despite** an install, means the game
assemblies aren't reaching the test output; see `test/README.md`.

## Conventions

Most of these fail the build rather than showing up in review, so it's worth
skimming before you write much:

- **`.editorconfig` is authoritative.** C# files are **UTF-8 with BOM**, CRLF,
  4-space indent.
- **`EnforceCodeStyleInBuild=true`** — `IDExxxx` style violations are **errors**
  in the production projects, not suggestions.
- **`WarningsAsErrors=CS0618,CS0612,nullable`** — calling an `[Obsolete]` game
  API, or introducing any nullable warning, breaks the build. `CS0649` is
  suppressed, since Klei injects fields by reflection.
- **File-scoped namespaces** (`namespace X;`) in `BlueprintsIncluded` and its
  test project. Vendored `UtilLibs` / `UtilLibs.Tests` stay **block-scoped** —
  `.editorconfig` has a scoped override. `using` directives go outside the
  namespace, `System` first. No `this.` qualification.
- **No `partial` types.** Every type is declared in one place. Not
  build-enforced — please keep it out in review.
- **`Nullable` and `ImplicitUsings` are enabled for the mod, disabled for
  vendored `UtilLibs`** (which relies on unqualified `UnityEngine` names). When
  editing `UtilLibs`, add explicit `using`s.
- For Klei-injected or FUI-wired fields that are assigned before any use, write
  `= null!`. For genuinely optional values, use `T?`. Don't reach for `!` to
  silence a warning that's telling you something.
- **`LangVersion` is 14.0** on `netstandard2.1`, via PolySharp compile-time
  polyfills. Newer language features generally work; runtime APIs from later
  frameworks do not.
- Sub-namespaces mirror folders: `Patches/`, `Tools/`, `BlueprintData/`,
  `UnityUI/`, `Visualizers/`, `ModAPI/`.

### Harmony patches

One outer container class per file in `src/BlueprintsIncluded/Patches/`, with an
inner `[HarmonyPatch(typeof(T), nameof(T.M))]` class holding `static Prefix` /
`Postfix` and using injection parameters (`__instance`, `__result`,
`___privateField`). Patches auto-apply via `UserMod2.OnLoad` → `harmony.PatchAll`
— **don't** add another `PatchAll`.

User-facing strings go through `STRINGS.cs`; translations are
`ModAssets/translations/*.po`.

### Don't edit generated files

`mod.yaml`, `mod_info.yaml` and `LauncherMetadata.json` are generated by
`Directory.Build.targets` — change the targets, not the output. `Builds/` and
`PublicisedAssembly/` are generated and gitignored.

Also: **keep the `BlueprintsV2` root namespace.** Renaming it breaks
KSerialization save compatibility and every `.po` translation. It's intentional.

## Upstream fixes

This is a fork with no shared git history, so upstream fixes can't be
cherry-picked. A scheduled scan opens `upstream-sync` issues for changes landing
in upstream `BlueprintsV2/` or `UtilLibs/`; the procedure and the porting rules
are in [`docs/blueprints-included/upstream-sync.md`](docs/blueprints-included/upstream-sync.md). Read that before
porting anything from upstream — including why some upstream files must never be
synced wholesale.

Upstream relicensed to All Rights Reserved on 2026-09-07, so it is read for behaviour,
never copied: an `upstream-sync` issue describes the change in prose, and the port is
written fresh from that description by someone who has not read upstream's code for it.
The cut-off and the rule are in
[`upstream-sync.md`](docs/blueprints-included/upstream-sync.md#triage-rubric).

Those issues are labelled `bug` or `enhancement` alongside `upstream-sync`.
Feature parity with upstream is a goal, so an `enhancement` one is a port waiting
to be scheduled rather than a proposal to argue for. Close one as `wontfix` when
there is a reason - it fights a deliberate divergence, carries a side effect this
fork does not want, or costs far more than it gives - and record that reason.

## Pull requests

- Branch off `main`. CI (build + tests, offline) must pass.
- Fill in [the PR template](.github/pull_request_template.md). The
  **Verification** section matters most: say what you actually ran, and be
  explicit about what you did **not** verify. An honest gap is easy to check at
  review time; a hidden one isn't.
- If a change touches game-dependent behaviour and you have no install, say so
  rather than guessing. That's useful information, not a failing.
- Keep unrelated changes out. If you spot something else worth fixing, mention it
  or open an issue.
- **If you used AI to write the change, keep the disclosure note** in the PR
  template. AI-assisted commits here also carry a `Co-Authored-By:` trailer
  naming the model.

## Reporting bugs

Open an issue, and please include:

- ONI build number, and whether you're on the base game or an expansion
- Your other active mods — conflicts are common
- `Player.log` (`%USERPROFILE%\AppData\LocalLow\Klei\Oxygen Not Included\`)
- What you expected, and what happened instead
- A save file if the problem is specific to one colony

## Licence

MIT — see [LICENSE](LICENSE). By contributing you agree your work is licensed
the same way.

[NOTICE](NOTICE) carries what LICENSE can't: the attribution chain, the bundled
third-party components, and Klei's Mod and UGC guidelines — which **forbid
commercial use** of this mod, on top of the MIT grant.
