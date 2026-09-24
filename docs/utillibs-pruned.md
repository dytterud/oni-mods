# Pruned `UtilLibs` files

`src/UtilLibs/` is a vendored copy of SGT_Imalas' shared helper library, ILRepacked into the
mod dll. It serves *every* Imalas ONI mod, so most of it was machinery this fork never called.
On 2026-09-10 the unreachable part — the bulk of the library — was removed.

The file list below is the record of what went, and it is what the
[upstream sync](blueprints-included/upstream-sync.md) checks before triaging a `UtilLibs` commit. It is
deliberately not summarised as a count: a count goes stale the moment one more file is
pruned or restored, and the list is the thing that has to be right.

## Why this is not a new judgement

The sync job already had a [reachability
filter](blueprints-included/upstream-sync.md#the-utillibs-relevance-filter) that records `unused-helper` and opens
no issue for upstream commits touching helpers this mod cannot reach. Two of the five `UtilLibs`
commits it had triaged before the prune landed in files on this list. Removing them makes a
standing verdict physical; it does not change what the job reports.

## How the set was chosen

Reachability from `src/BlueprintsIncluded/`, `test/` and `harness/`, followed transitively
through `src/UtilLibs/` — then **confirmed by the compiler**: the candidates were deleted and
`dotnet build` + `dotnet test` had to stay green. Static analysis only proposed the set; the
build decided it. Two files came back that way (`UI/FUI/FInputField.cs` and
`FNumberInputField.cs`, both needed by the retained `FSlider.cs`).

## The restore invariant

**Every pruned file was byte-identical to upstream when it was removed**, so nothing
fork-local was lost. [`upstream-sync.md`](blueprints-included/upstream-sync.md#why-there-is-no-merge-base) records
that only four `UtilLibs` files ever differed from upstream, and none of them are on this list:
`UtilMethods.cs` and `InjectionMethods.cs` are still live, `UtilLibs.csproj` was kept, and
`UI/FUI/FSlider.cs` is retained (see below).

So **restore from upstream, not from this repo's history** — upstream is newer and was
equivalent at the fork point.

### Restoring one

Needed only if a ported `BlueprintsV2` change actually calls the helper.

1. Take the file from upstream `UtilLibs/<path>` in
   [`Sgt-Imalas/Sgt_Imalas-Oni-Mods`](https://github.com/Sgt-Imalas/Sgt_Imalas-Oni-Mods).
2. Drop it at `src/UtilLibs/<path>` — the trees are 1:1 under a different root.
3. Keep this project's conventions: block-scoped namespaces, `ImplicitUsings` off, `Nullable`
   off. See [CLAUDE.md](../CLAUDE.md).
4. **Delete its entry below in the same commit**, so the manifest never claims a file is gone
   when it is back.

## Dead but retained — *not* pruned

Four `KMonoBehaviour` subclasses are unreachable from the mod but were kept anyway.

    src/UtilLibs/UI/FUI/FSlider.cs
    src/UtilLibs/UI/FUI/FExpandToggle.cs
    src/UtilLibs/UI/FUI/GridLayoutSizeAdjustment.cs
    src/UtilLibs/UI/FUI/PasswordInputVisibilityToggle.cs

Two more (`UI/FUI/FInputField.cs`, `UI/FUI/FNumberInputField.cs`) survive only because
`FSlider.cs` needs them to compile.

**The reason originally given for keeping them has been withdrawn.** It was that the external
Unity project behind `ModAssets/assets/*/blueprints_ui` might have attached one to a prefab, and
that a missing script breaks that prefab at load — which could not be checked, because the
bundles are compressed. They have since been decompressed. Every `MonoScript` the three bundles
reference resolves to `UnityEngine.UI` or `Unity.TextMeshPro`; there is no mod script in them at
all, and no prefab that could break. See [asset provenance](blueprints-included/asset-provenance.md).

They stay for now only because removing them is unrelated to the change that established this,
and is tracked separately. There is no longer a reason not to.

These six are **present in the tree**, so the normal `direct`/`indirect` reachability tests
apply to them — do not treat them as pruned. Note that upstream
[`de689d1`](https://github.com/Sgt-Imalas/Sgt_Imalas-Oni-Mods/commit/de689d1) already changed
`FSlider.cs` and was triaged `unused-helper`, so this file is dead code and behind upstream.
Both facts are intentional.

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
