# Blueprints Included

An Oxygen Not Included mod for designing, storing and re-placing your favourite
builds, including building settings.

This is a standalone fork of **Blueprints Expanded** (`BlueprintsV2`) by SGT_Imalas,
which itself is a rewrite of the original **Blueprints** mod by Mayall. The C#
namespace is still `BlueprintsV2`; the mod's `staticID` is `BlueprintsIncluded`, so
ONI treats it as a separate mod from the upstream Steam item.

## Repo layout

| Path | What |
|------|------|
| `src/BlueprintsIncluded/` | the mod project (`.csproj`, source, `ModAssets/`) |
| `src/UtilLibs/` | vendored shared helper library (ILRepacked into the mod dll) |
| `lib/` | committed ONI / Unity reference assemblies + `ONI_Together_API.dll` for offline builds |
| `Directory.Build.props` / `.targets` | shared build logic (mod.yaml generation, ILRepack, asset copy) |
| `BlueprintsIncluded.slnx` | solution |

## Building

### Offline (no game install required)

```
dotnet build BlueprintsIncluded.slnx -c Release -p:OfflineBuild=true
```

Output: `src/BlueprintsIncluded/bin/BlueprintsIncluded.dll` (ILRepack-merged with
`UtilLibs` + `PLib`), and a ready-to-load mod folder at `Builds/BlueprintsIncluded/`.

### Against a local ONI install (for in-game testing)

1. Copy `Directory.Build.props.default` to `Directory.Build.props.user` and set
   `GameLibsFolder` (the game's `OxygenNotIncluded_Data/Managed` folder) and
   `ModFolder` (your `Klei/OxygenNotIncluded/mods/dev` folder).
2. `dotnet tool restore` (installs the assembly publicizer / refasmer tools).
3. `dotnet build BlueprintsIncluded.slnx -c Debug` — the built mod is copied to
   your dev mods folder.

## License

MIT — see [LICENSE](LICENSE), and [NOTICE](NOTICE) for the attribution chain, the
bundled third-party components, and Klei's terms.

**No commercial use.** This mod builds against Oxygen Not Included's assemblies and
is distributed under [Klei's Mod and UGC guidelines](https://support.klei.com/hc/en-us/articles/27787028069012),
which forbid it. That restriction sits on top of the MIT grant: MIT's permission to
sell copies doesn't extend to this mod, because the game code it depends on isn't
ours to license.

Credit: Mayall (original Blueprints), SGT_Imalas (Blueprints Expanded rewrite).
