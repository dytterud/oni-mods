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
| `BlueprintsIncluded.csproj` | the mod project (repo root) |
| `UtilLibs/` | vendored shared helper library (built into the mod dll) |
| `Lib/` | committed ONI reference assemblies for offline builds |
| `TwitchLib/ONI_Together_API.dll` | optional multiplayer integration reference |
| `Directory.Build.props` / `.targets` | shared build logic (mod.yaml generation, ILRepack, asset copy) |

## Building

### Offline (no game install required)

```
msbuild -t:restore -property:Configuration=Release
msbuild -t:rebuild  -property:Configuration=Release -property:GameLibsFolder="Lib"
```

Output: `bin/BlueprintsIncluded.dll` (ILRepack-merged with `UtilLibs` + `PLib`), and
a ready-to-load mod folder at `Builds/BlueprintsIncluded/`.

### Against a local ONI install (for in-game testing)

1. Copy `Directory.Build.props.default` to `Directory.Build.props.user` and set
   `GameLibsFolder` (the game's `OxygenNotIncluded_Data/Managed` folder) and
   `ModFolder` (your `Klei/OxygenNotIncluded/mods/dev` folder).
2. `dotnet tool restore` (installs the assembly publicizer / refasmer tools).
3. `msbuild -t:rebuild -property:Configuration=Debug` — the built mod is copied to
   your dev mods folder.

## License

MIT — see [LICENSE](LICENSE). Credit: Mayall (original), SGT_Imalas (rewrite).
Game reference assemblies used under Klei's Mod/UGC guidelines; no commercial use.
