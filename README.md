# ONI Mods

Mods for [Oxygen Not Included](https://www.klei.com/games/oxygen-not-included), built from one
repo with shared build machinery.

| Mod | What it does | Status |
|-----|--------------|--------|
| [Blueprints Included](src/BlueprintsIncluded/README.md) | Design, store and re-place your favourite builds, including building settings | [0.1.0](https://github.com/dytterud/oni-mods/releases/tag/blueprints-included%2Fv0.1.0) |
| [Save Profiler](src/SaveProfiler/README.md) | Measures where autosave time goes, per phase and per component type | unreleased |

Each mod keeps its own `README.md`, `CHANGELOG.md` and `CLAUDE.md` next to its project, and its
own docs under `docs/<mod-slug>/`.

## Repo layout

| Path | What |
|------|------|
| `src/<Mod>/` | one project per mod |
| `src/UtilLibs/` | vendored shared helper library (ILRepacked into the mod dlls) |
| `test/` | one xUnit project per production project |
| `harness/` | dev-only in-game regression harness (a mod itself); see [harness/README.md](harness/README.md) |
| `docs/` | repo-wide docs; per-mod docs in `docs/<mod-slug>/` |
| `lib/` | committed ONI / Unity reference assemblies for offline builds |
| `Directory.Build.props` / `.targets` | shared build logic (mod.yaml generation, ILRepack, packaging) |
| `OniMods.slnx` | solution |

## Building

### Offline (no game install required)

```
dotnet build OniMods.slnx -c Release -p:OfflineBuild=true
```

Each mod is packaged into a ready-to-load folder at `Builds/<Mod>/`, complete with its
`mod.yaml`, `mod_info.yaml`, assets, and the `LICENSE` / `NOTICE` that must travel with a
distributed copy.

### Against a local ONI install (for in-game testing)

1. Copy `Directory.Build.props.default` to `Directory.Build.props.user` and set
   `GameLibsFolder` (the game's `OxygenNotIncluded_Data/Managed` folder) and
   `ModFolder` (your `Klei/OxygenNotIncluded/mods/dev` folder).
2. `dotnet tool restore` (installs the assembly publicizer / refasmer tools).
3. `dotnet build OniMods.slnx -c Debug` — each mod is copied to your dev mods folder.

## Adding a mod

See [CLAUDE.md](CLAUDE.md#adding-a-mod) — a new `src/<Mod>/<Mod>.csproj` with a handful of
properties gets the whole packaging pipeline for free.

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md) — build and test tiers, the conventions that fail the
build, and how to report a bug usefully.

Before measuring performance, read [docs/perf-method.md](docs/perf-method.md) — the noise floor
here is ±10% between runs, timing wrappers cost more than the methods they measure, and this repo
has published and retracted several plausible-but-unmeasured conclusions. It is short, and it is
the accumulated cost of learning those the hard way.

## License

MIT — see [LICENSE](LICENSE), and [NOTICE](NOTICE) for the attribution chain, the bundled
third-party components, and Klei's terms.

**No commercial use.** These mods build against Oxygen Not Included's assemblies and are
distributed under [Klei's Mod and UGC guidelines](https://support.klei.com/hc/en-us/articles/27787028069012),
which forbid it. That restriction sits on top of the MIT grant: MIT's permission to sell copies
doesn't extend to a mod whose game code isn't ours to license.
