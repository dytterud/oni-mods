# Pruned `UtilLibs` files

`src/UtilLibs/` is a vendored copy of SGT_Imalas' shared helper library, ILRepacked into the
mod dll. It serves *every* Imalas ONI mod, so most of it was machinery this fork never called.
On 2026-09-10 the unreachable part — the bulk of the library — was removed. Six `UI/FUI/` files
kept back then for prefab safety followed on 2026-09-25 (#124).

The file list below is the record of what went; check it before porting an upstream `UtilLibs`
change. It is
deliberately not summarised as a count: a count goes stale the moment one more file is
pruned or restored, and the list is the thing that has to be right.

## Why this is not a new judgement

Every file on the list was already unreachable from this mod, so no change to one could reach the
shipped dll. Removing them made that standing fact physical; nothing a player sees changed.

## How the set was chosen

Reachability from `src/BlueprintsIncluded/`, `test/` and `harness/`, followed transitively
through `src/UtilLibs/` — then **confirmed by the compiler**: the candidates were deleted and
`dotnet build` + `dotnet test` had to stay green. Static analysis only proposed the set; the
build decided it. Two files came back that way (`UI/FUI/FInputField.cs` and
`FNumberInputField.cs`, both needed by `FSlider.cs`, then still retained).

The second pass (#124) removed the six files of the
[prefab-safety batch](#uifui-prefab-safety-batch), confirmed the same way.

## The restore invariant

**Every pruned file but one was byte-identical to upstream when it was removed.** At the fork
point only four `UtilLibs` files differed from upstream: `UtilMethods.cs` and
`InjectionMethods.cs` are still live, `UtilLibs.csproj` was kept, and `UI/FUI/FSlider.cs` —
the exception — went in the second pass with its fork-local difference. That difference is
kept in history, not lost.

So **restore from this repo's history, not from upstream.** The copy from before the prune is
the one this fork holds under MIT. Upstream relicensed away from MIT on
2026-09-07 (see [asset provenance](blueprints-included/asset-provenance.md#the-relicense-cut)), so
its newer copy is not ours to take.

### Restoring one

Needed only if a ported `BlueprintsV2` change actually calls the helper.

1. Take the file from before the commit that deleted it and put it back at
   `src/UtilLibs/<path>`. The first pass was `58f2ed0`; for either pass,
   `git log -1 --format=%h --diff-filter=D -- src/UtilLibs/<path>` names the commit, and
   `git show <commit>^:src/UtilLibs/<path>` prints the file.
2. If upstream has changed it since and the port needs that change, describe it and reimplement
   it [clean-room](blueprints-included/asset-provenance.md#the-rule) — never take
   upstream's copy.
3. Keep this project's conventions: block-scoped namespaces, `ImplicitUsings` off, `Nullable`
   off. See [CLAUDE.md](../CLAUDE.md).
4. **Delete its entry below in the same commit**, so the manifest never claims a file is gone
   when it is back.

## The pruned files

Paths are fork-relative with forward slashes. An upstream-form path
(`UtilLibs/RecipeBuilder.cs`) is a substring of the fork-relative one
(`src/UtilLibs/RecipeBuilder.cs`), so one `grep -F` answers the question from either direction:

```bash
grep -F "UtilLibs/RecipeBuilder.cs" docs/utillibs-pruned.md
```

### MarkdownExport/

    src/UtilLibs/MarkdownExport/Exporter.cs
    src/UtilLibs/MarkdownExport/IMD_Entry.cs
    src/UtilLibs/MarkdownExport/IMD_File.cs
    src/UtilLibs/MarkdownExport/MD_BlueprintCollectionEntry.cs
    src/UtilLibs/MarkdownExport/MD_BlueprintEntry.cs
    src/UtilLibs/MarkdownExport/MD_BuildingEntry.cs
    src/UtilLibs/MarkdownExport/MD_ComplexRecipes.cs
    src/UtilLibs/MarkdownExport/MD_CritterConsumptionsTable.cs
    src/UtilLibs/MarkdownExport/MD_Directory.cs
    src/UtilLibs/MarkdownExport/MD_ElementConverter.cs
    src/UtilLibs/MarkdownExport/MD_EnergyGenerator.cs
    src/UtilLibs/MarkdownExport/MD_EntityEntry.cs
    src/UtilLibs/MarkdownExport/MD_GeyserEntry.cs
    src/UtilLibs/MarkdownExport/MD_Header.cs
    src/UtilLibs/MarkdownExport/MD_Localization/MD_Localization.cs
    src/UtilLibs/MarkdownExport/MD_Localization/MarkdownUtil.cs
    src/UtilLibs/MarkdownExport/MD_Localization/TranslationGroup.cs
    src/UtilLibs/MarkdownExport/MD_Localization/TranslationKeyBuilder.cs
    src/UtilLibs/MarkdownExport/MD_Page.cs
    src/UtilLibs/MarkdownExport/MD_SubstanceTable.cs
    src/UtilLibs/MarkdownExport/MD_TagsTable.cs
    src/UtilLibs/MarkdownExport/MD_Text.cs

### Unity_UI_Extensions/

    src/UtilLibs/UI/FUI/Unity_UI_Extensions/LICENSE.md
    src/UtilLibs/UI/FUI/Unity_UI_Extensions/Scripts/Animation/CoroutineTween.cs
    src/UtilLibs/UI/FUI/Unity_UI_Extensions/Scripts/Controls/DropdownEx/DropdownEx.cs
    src/UtilLibs/UI/FUI/Unity_UI_Extensions/Scripts/Controls/ReorderableList/ReorderableList.cs
    src/UtilLibs/UI/FUI/Unity_UI_Extensions/Scripts/Controls/ReorderableList/ReorderableListContent.cs
    src/UtilLibs/UI/FUI/Unity_UI_Extensions/Scripts/Controls/ReorderableList/ReorderableListDebug.cs
    src/UtilLibs/UI/FUI/Unity_UI_Extensions/Scripts/Controls/ReorderableList/ReorderableListElement.cs
    src/UtilLibs/UI/FUI/Unity_UI_Extensions/Scripts/Controls/Sliders/MinMaxSlider.cs
    src/UtilLibs/UI/FUI/Unity_UI_Extensions/Scripts/Controls/Sliders/MinMaxSliderAudio.cs
    src/UtilLibs/UI/FUI/Unity_UI_Extensions/Scripts/Utilities/ListPool.cs
    src/UtilLibs/UI/FUI/Unity_UI_Extensions/Scripts/Utilities/MinMaxValues.cs
    src/UtilLibs/UI/FUI/Unity_UI_Extensions/Scripts/Utilities/ObjectPool.cs

### BuildingPortUtils/

    src/UtilLibs/BuildingPortUtils/ConduitDisplayPortPatching.cs
    src/UtilLibs/BuildingPortUtils/DisplayConduitPortInfo.cs
    src/UtilLibs/BuildingPortUtils/PortConduitConsumer.cs
    src/UtilLibs/BuildingPortUtils/PortConduitDispenserBase.cs
    src/UtilLibs/BuildingPortUtils/PortConduitExtensions.cs
    src/UtilLibs/BuildingPortUtils/PortDisplay2.cs
    src/UtilLibs/BuildingPortUtils/PortDisplayController.cs
    src/UtilLibs/BuildingPortUtils/PortDisplayInput.cs
    src/UtilLibs/BuildingPortUtils/PortDisplayOutput.cs
    src/UtilLibs/BuildingPortUtils/SharedConduitUtils.cs

### SharedTweaks/

    src/UtilLibs/SharedTweaks/AttachmentPointTagNameFix.cs
    src/UtilLibs/SharedTweaks/DynamicMaterialSelectorHeaderHeight.cs
    src/UtilLibs/SharedTweaks/ElementConverterDescriptionImprovement.cs
    src/UtilLibs/SharedTweaks/ResearchNotificationMessageFix.cs
    src/UtilLibs/SharedTweaks/ResearchScreenBetterConnectionLines.cs
    src/UtilLibs/SharedTweaks/ResearchScreenCollapseEntries.cs
    src/UtilLibs/SharedTweaks/SelectedRecipeQueueScreenSizeFix.cs
    src/UtilLibs/SharedTweaks/SkillsWidgetBetterConnectionLines.cs
    src/UtilLibs/SharedTweaks/TranslationFix.cs

### ModVersionCheck/ and Updating/

    src/UtilLibs/ModVersionCheck/ModUpdatingState.cs
    src/UtilLibs/ModVersionCheck/OutdatedVersionInfoPatches.cs
    src/UtilLibs/ModVersionCheck/VersionChecker.cs
    src/UtilLibs/Updating/DataClasses/RemoteModInfo.cs
    src/UtilLibs/Updating/DataClasses/RemoteVersionInfo.cs
    src/UtilLibs/Updating/LoadExtraAnimations.cs
    src/UtilLibs/Updating/ModFileLoader.cs
    src/UtilLibs/Updating/VersionCheck.cs
    src/UtilLibs/Updating/WebRequestHelper.cs

### ElementUtilNamespace/

    src/UtilLibs/ElementUtilNamespace/ElementGrouping.cs
    src/UtilLibs/ElementUtilNamespace/ElementInfo.cs
    src/UtilLibs/ElementUtilNamespace/ElementUtil.cs

### UI/FUI/

    src/UtilLibs/UI/FUI/ModMenuButton.cs
    src/UtilLibs/UI/FUI/SideScreen.cs

### UI/FUI/ prefab-safety batch

Pruned in the second pass (#124). The first pass kept these four `KMonoBehaviour` subclasses
in case a bundle prefab had one attached, since a missing script breaks that prefab at load:

    src/UtilLibs/UI/FUI/FSlider.cs
    src/UtilLibs/UI/FUI/FExpandToggle.cs
    src/UtilLibs/UI/FUI/GridLayoutSizeAdjustment.cs
    src/UtilLibs/UI/FUI/PasswordInputVisibilityToggle.cs

and these two, needed only by `FSlider.cs`:

    src/UtilLibs/UI/FUI/FInputField.cs
    src/UtilLibs/UI/FUI/FNumberInputField.cs

That reason no longer holds. The inherited bundles, once decompressed, referenced no mod script,
and the bundles this fork now builds from its own spec (#120) use only stock `UnityEngine.UI` /
`Unity.TextMeshPro` components. See [asset provenance](blueprints-included/asset-provenance.md).
`FInputField2.cs` is a different class and stays.

`FSlider.cs` carried a fork-local difference from upstream and was already behind it: upstream
[`de689d1`](https://github.com/Sgt-Imalas/Sgt_Imalas-Oni-Mods/commit/de689d1) was triaged
`unused-helper`. Restore it, if ever, from this repo's history, as for any other file.

### YeetUtils/

    src/UtilLibs/YeetUtils/Rotator.cs
    src/UtilLibs/YeetUtils/YeetHelper.cs

### ModSyncing/ and SharedModConfigMenu/

    src/UtilLibs/ModSyncing/ModSyncUtils.cs
    src/UtilLibs/SharedModConfigMenu/OptionRegistry.cs

### Root-level helpers

    src/UtilLibs/AccessibilityUtils.cs
    src/UtilLibs/ArtHelper.cs
    src/UtilLibs/BuildingUtil.cs
    src/UtilLibs/CGMWorldGenUtils.cs
    src/UtilLibs/CONSTS.cs
    src/UtilLibs/CodexUtils.cs
    src/UtilLibs/DuperyShared.cs
    src/UtilLibs/EffectBuilder.cs
    src/UtilLibs/GermUtils.cs
    src/UtilLibs/MinionAnimUtils.cs
    src/UtilLibs/ModListUtils.cs
    src/UtilLibs/NameIdHelper.cs
    src/UtilLibs/RecipeBuilder.cs
    src/UtilLibs/ReflectionHelper.cs
    src/UtilLibs/RocketryUtils.cs
    src/UtilLibs/SandboxUtil.cs
    src/UtilLibs/SanitationUtils.cs
    src/UtilLibs/SoundUtils.cs
    src/UtilLibs/SupplyClosetUtils.cs
    src/UtilLibs/TechUtils.cs
    src/UtilLibs/TranslatableTextBuilder.cs
    src/UtilLibs/TranspilerHelper.cs

## What survived

For orientation, what still lives in `src/UtilLibs/`: the FUI widget wrappers actually used
by this mod's screens, `AssetUtils`, `IO_Utils`, `UIUtils`, `UtilMethods`, `Extensions`,
`SgtLogger`, `LocalisationUtil`, `GameStrings`, `InjectionMethods`,
`ModHashes`, `DialogUtil`, `CompatibilityNotifications`, the TMP conversion helpers,
`ModAPIClasses/DecorPackA_ModAPI.cs`, and `SharedTweaks/ModsScreenMarkIncompatbileMods.cs`.
