using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using BlueprintsV2.BlueprintData;
using BlueprintsV2.Visualizers;
using HarmonyLib;
using UnityEngine;

namespace BlueprintsV2.Harness.Perf;

/// <summary>
/// Manually Harmony-patches mod/game methods to count calls + accumulate elapsed time, so
/// <c>perf.json</c> can directly attribute cost rather than leaving it to inference from
/// wall-clock alone. Applied only in perf mode, via the same <see cref="Harmony"/> instance ONI
/// gave the mod in <c>OnLoad</c> - manual (not attribute) patching because several targets live
/// on an <c>internal</c> mod type, or are picked at runtime (every overload of a game utility
/// method), so they can't be named at compile time with <c>[HarmonyPatch]</c>.
///
/// One prefix/postfix pair serves every target: the postfix reads <c>__originalMethod</c> to look
/// up which <see cref="Accumulator"/> to add to, so adding a new hotspot candidate is one
/// <see cref="PatchOne"/>/<see cref="PatchAllOverloads"/> call, not a new method.
/// </summary>
internal static class PerfInstrumentation
{
    private static readonly Dictionary<MethodBase, Accumulator> byMethod = new();
    private static readonly Dictionary<string, Accumulator> byName = new(StringComparer.Ordinal);
    private static bool applied;

    /// <summary>Registration tally for the end-of-Apply summary - see the note where it is logged.
    /// <c>unresolved</c> holds the names whose target could not be found, which are exactly the
    /// hotspots that will read 0 calls for a reason that has nothing to do with the code under
    /// test.</summary>
    private static int registered;
    private static readonly List<string> unresolved = [];

    /// <summary>On for an attribution run (<c>run-ingame.ps1 -Attribution</c>, i.e. sentinel mode
    /// <c>perf-attribution</c>), off for a normal benchmark run. See the comment at its use site:
    /// instrumenting methods that run once per visual costs more than the methods do, so a run with
    /// this on reports call counts and allocation honestly but its <c>update-visual</c> medians must
    /// not be compared with a run without it. Driven by the sentinel rather than a recompile so the
    /// two runs are the same binary.</summary>
    private static bool PerVisualHotspots => HarnessGate.Attribution;

    /// <summary>Applies every registered patch, logging (via <paramref name="log"/>) and skipping
    /// any single target that can't be resolved/patched rather than aborting the rest - one bad
    /// target (a renamed method, a shifted overload set) should cost that one hotspot's numbers,
    /// not every other hotspot's too.</summary>
    public static void Apply(Harmony harmony, HarnessLog log)
    {
        if (applied)
            return;

        // ModAssets/API_Methods/CustomTileRenderer are internal mod types - reach them by name
        // (same trick FixtureBuilder uses for GetValidMaterials). BuildingConfig, BuildingVisual,
        // TileVisual and VisualsUtilities are public, so those patch directly.
        TryResolveType("BlueprintsV2.ModAssets", out var modAssetsType, log);
        TryResolveType("BlueprintsV2.ModAPI.API_Methods", out var apiMethodsType, log);
        TryResolveType("BlueprintsV2.Visualizers.CustomTileRenderer", out var customTileRendererType, log);

        // Import hotspots (docs §7).
        if (modAssetsType != null)
            TryPatchOne(harmony, log, "GetValidMaterials", () => AccessTools.Method(modAssetsType, "GetValidMaterials"));
        TryPatchOne(harmony, log, "SanitizeSelectedTags", () => AccessTools.Method(typeof(BuildingConfig), "SanitizeSelectedTags"));

        // Placement/visualize hotspot candidates (docs §7): the pieces of TileVisual's
        // construction (GameUtil.KInstantiate and CustomTileRenderer's tile-block methods are
        // overloaded - patch every overload by name rather than pinning one signature that could
        // shift with a game update).
        TryPatchOne(harmony, log, "TileVisual.ctor",
            () => AccessTools.Constructor(typeof(TileVisual), new[] { typeof(BuildingConfig), typeof(int), typeof(ulong) }));
        // Per-overload, not shared: KInstantiate's overloads forward to each other, so a single
        // shared accumulator double-counted the nested time (72,934 calls against ~30,000
        // TileVisual ctors) and inflated its share of visualize's cost. Splitting them makes the
        // outer vs inner split visible - this is the number a pooling refactor would be betting on.
        TryPatchEachOverload(harmony, log, "KInstantiate", typeof(GameUtil));
        if (customTileRendererType != null)
        {
            TryPatchAllOverloads(harmony, log, "AddTileBlock", customTileRendererType);
            TryPatchAllOverloads(harmony, log, "RefreshCell", customTileRendererType);
        }
        TryPatchOne(harmony, log, "GetVisualizerColor",
            () => AccessTools.Method(typeof(BuildingVisual), nameof(BuildingVisual.GetVisualizerColor)));
        TryPatchOne(harmony, log, "SetTileColor",
            () => AccessTools.Method(typeof(VisualsUtilities), nameof(VisualsUtilities.SetTileColor)));
        TryPatchOne(harmony, log, "UpdateRequirementsState",
            () => AccessTools.Method(typeof(BuildingVisual), nameof(BuildingVisual.UpdateRequirementsState)));
        if (apiMethodsType != null)
            TryPatchOne(harmony, log, "ApplyAdditionalBuildingData", () => AccessTools.Method(apiMethodsType, "ApplyAdditionalBuildingData"));

        // create hotspot candidates (docs §7): CreateBlueprint's per-found-building capture calls
        // StoreAdditionalBuildingData, which loops every one of the ~35 registered
        // AdditionalBuildingDataEntries handlers (GetAdditionalBuildingData) per building looking
        // for a matching component - worth confirming directly rather than assuming.
        if (apiMethodsType != null)
        {
            TryPatchOne(harmony, log, "StoreAdditionalBuildingData", () => AccessTools.Method(apiMethodsType, "StoreAdditionalBuildingData"));
            TryPatchOne(harmony, log, "GetAdditionalBuildingData", () => AccessTools.Method(apiMethodsType, "GetAdditionalBuildingData"));

            // Four of those ~35 handlers, picked because two of them resolve their component by
            // STRING and two by type - a natural control pair, since all four run once per captured
            // building with identical wrapper overhead.
            //
            // TryStoreBackwall does GetComponent("Backwall"); TryStoreMoodLamp does
            // GetComponent("MoodLamp") *and* GetComponent("TintableLamp"). Both are registered
            // whenever Aki's decor mods are ABSENT (API_Methods' `if (!Aki_..._API_Integrated)`
            // fallbacks), which is the fixture's situation - so in this run all three string lookups
            // execute on every building and every one of them returns null. GetComponent(string) is
            // Unity's slowest component lookup: it resolves a type from a string on each call.
            //
            // TryStoreArtableSkin and TryStoreBuildingSkin take the typed TryGetComponent<T> path.
            // Same loop, same per-building frequency, same wrapper - so the difference between the
            // two pairs is the lookup mechanism and little else. If the string handlers are not
            // dearer, there is nothing here worth fixing.
            TryResolveType("BlueprintsV2.BlueprintData.SkinHelper", out var skinHelperType, log);
            if (skinHelperType != null)
            {
                TryPatchOne(harmony, log, "SkinHelper.TryStoreBackwall (string)",
                    () => AccessTools.Method(skinHelperType, "TryStoreBackwall"));
                TryPatchOne(harmony, log, "SkinHelper.TryStoreMoodLamp (string x2)",
                    () => AccessTools.Method(skinHelperType, "TryStoreMoodLamp"));
                TryPatchOne(harmony, log, "SkinHelper.TryStoreArtableSkin (typed)",
                    () => AccessTools.Method(skinHelperType, "TryStoreArtableSkin"));
                TryPatchOne(harmony, log, "SkinHelper.TryStoreBuildingSkin (typed)",
                    () => AccessTools.Method(skinHelperType, "TryStoreBuildingSkin"));
            }
        }
        TryPatchAllOverloads(harmony, log, "NaturalBuildingCell", typeof(GameUtil));

        // update-visual hotspots (docs §7): the per-frame redraw the Use Blueprint tool runs from
        // OnMouseMove. These three run once per update (or once per visual on an already-expensive
        // body), so their wrapper overhead is proportionally negligible and they stay always-on.
        TryPatchOne(harmony, log, "UpdateVisual",
            () => AccessTools.Method(typeof(BlueprintState), nameof(BlueprintState.UpdateVisual)));
        TryPatchOne(harmony, log, "StoreOccupiedArea",
            () => AccessTools.Method(typeof(BlueprintState), "StoreOccupiedArea"));
        // Resolved by widest-overload rather than by a pinned parameter list. It *was* pinned to
        // five types, and adding `bool moveTransform = true` in the shared-parent-transform change
        // silently dropped it to zero calls - a hotspot reading 0 looks identical to a code path
        // that never ran, which is the failure mode the KInstantiateUI note below also describes.
        // The widest overload is the right target regardless: the narrow one forwards to it, so
        // patching both under one accumulator would double-count (see KInstantiate, docs §7).
        TryPatchOne(harmony, log, "ApplyRotatedCellAndMove",
            () => WidestOverload(typeof(BlueprintState.BlueprintTransformationInfo), "ApplyRotatedCellAndMove", log));

        // The rest of that path runs once PER VISUAL - 2000x per call at N=2000 - on bodies that are
        // a handful of arithmetic ops. A Harmony wrapper costs more than the method it measures
        // there, which would flatten exactly the improvement the integer rotation work is chasing. So: off for the timing runs, on for one attribution run, and
        // the two must never be compared against each other (the same caveat §7 already carries
        // about the iteration bump).
        if (PerVisualHotspots)
        {
            TryPatchOne(harmony, log, "GetRotatedCell",
                () => AccessTools.Method(typeof(BlueprintState.BlueprintTransformationInfo), nameof(BlueprintState.BlueprintTransformationInfo.GetRotatedCell)));
            TryPatchOne(harmony, log, "ApplyRotation",
                () => AccessTools.Method(typeof(BuildingVisual), nameof(BuildingVisual.ApplyRotation)));
            TryPatchOne(harmony, log, "ApplyColorIfChanged",
                () => AccessTools.Method(typeof(BuildingVisual), nameof(BuildingVisual.ApplyColorIfChanged)));
            TryPatchOne(harmony, log, "ValidCell",
                () => AccessTools.Method(typeof(BuildingVisual), nameof(BuildingVisual.ValidCell)));
            TryPatchOne(harmony, log, "HasTech",
                () => AccessTools.Method(typeof(BuildingVisual), nameof(BuildingVisual.HasTech)));
            TryPatchOne(harmony, log, "AllowedInWorld",
                () => AccessTools.Method(typeof(BuildingVisual), nameof(BuildingVisual.AllowedInWorld)));

            // The tile path specifically. update-visual-tile costs ~2x update-visual-ladder per
            // visual and allocates ~1.2 KB per tile per frame, and none of the hotspots above can
            // see why: TileVisual overrides ApplyRotation and ApplyColorIfChanged without calling
            // base, so tiles appear in neither count. What they do instead is re-seat themselves in
            // the renderer on every move - Clean() (RemoveTileBlock + RefreshCell on the old cell)
            // then AddTileBlock + RefreshCell on the new one - and each RefreshCell fans out to
            // five cells per layer. These counts are the ones that say whether the fan-out, the
            // re-seating, or the game-side Rebuild is the thing to attack.
            TryPatchOne(harmony, log, "TileVisual.MoveVisualizerCore",
                () => AccessTools.Method(typeof(TileVisual), "MoveVisualizerCore"));
            TryPatchOne(harmony, log, "TileVisual.UpdateGrid",
                () => AccessTools.Method(typeof(TileVisual), "UpdateGrid"));
            TryPatchOne(harmony, log, "TileVisual.Clean",
                () => AccessTools.Method(typeof(TileVisual), nameof(TileVisual.Clean)));
            TryPatchOne(harmony, log, "TileVisual.ApplyColorIfChanged",
                () => AccessTools.Method(typeof(TileVisual), nameof(TileVisual.ApplyColorIfChanged)));
            if (customTileRendererType != null)
            {
                // RefreshCell itself is already patched above (always-on, both overloads under one
                // accumulator - they forward to each other, so that count is inflated by design and
                // only RefreshCellInternal's is the real fan-out figure). Don't re-register it here:
                // a second harmony.Patch on the same method would count every call twice.
                TryPatchOne(harmony, log, "RefreshCellInternal",
                    () => AccessTools.Method(customTileRendererType, "RefreshCellInternal"));
                TryPatchOne(harmony, log, "RemoveTileBlock",
                    () => AccessTools.Method(customTileRendererType, "RemoveTileBlock"));
                TryPatchOne(harmony, log, "GetVisualizerConnectionBits",
                    () => AccessTools.Method(customTileRendererType, "GetVisualizerConnectionBits"));
            }
            // Game-side: what RefreshCellInternal actually asks the renderer to do. If the cost is
            // here, no amount of mod-side dedup helps beyond calling it less often - which is
            // exactly the question.
            TryPatchOne(harmony, log, "BlockTileRenderer.Rebuild",
                () => AccessTools.Method(typeof(Rendering.BlockTileRenderer), "Rebuild"));

            // The other half of RefreshCellInternal's body, and the one §7 blames for making a
            // refresh expensive over occupied cells - the claim that explains why batching the
            // fan-out is worth 25-31% over a real base and ~0 over vacuum. That was inferred from
            // the dense-vs-sparse gap, never measured, and it is load-bearing enough to be worth
            // checking: it is why the batching survived a first measurement that read -2%.
            //
            // The remaining piece (the GetComponentInChildren walk that finds this component) is a
            // Unity generic and cannot be patched sanely; wrapping it mod-side purely to time it
            // would add a ~0.2us wrapper to something plausibly of the same order. Take it as the
            // residual instead: RefreshCellInternal - Rebuild - Refresh - wrapper floor.
            //
            // Parameter list pinned rather than looked up by name alone: a name-only AccessTools
            // lookup threw AmbiguousMatchException on KInstantiateUI and silently left that hotspot
            // at zero (docs §7).
            TryPatchOne(harmony, log, "KAnimGraphTileVisualizer.Refresh",
                () => AccessTools.Method(typeof(KAnimGraphTileVisualizer), "Refresh", Type.EmptyTypes));

            // Inside GetVisualizerColor. It allocates ~230 B/call over 240,000 calls - ~460 KB of
            // the 2,448 KB a 2000-tile frame allocates - but its instrumented children only account
            // for ~31 B of that (ValidCell 13, UpdateRequirementsState 11, AllowedInWorld 7). So the
            // bytes are in its own body or in these four, which nothing has measured yet. The first
            // line it runs is LocalPlayerId, which reaches SessionInfoAPI.LocalUserID - an external
            // mod API called once per visual per frame, and the prime suspect.
            TryPatchOne(harmony, log, "LocalPlayerId",
                () => AccessTools.Method(typeof(BlueprintState), nameof(BlueprintState.LocalPlayerId)));
            TryPatchOne(harmony, log, "IsMultiplayerVisualizer",
                () => AccessTools.Method(typeof(BlueprintState), nameof(BlueprintState.IsMultiplayerVisualizer)));
            TryPatchOne(harmony, log, "SameBuildingAlreadyFinishedInPlace",
                () => AccessTools.Method(typeof(BuildingVisual), nameof(BuildingVisual.SameBuildingAlreadyFinishedInPlace)));
            TryPatchOne(harmony, log, "CanForceRebuild",
                () => AccessTools.Method(typeof(BuildingVisual), nameof(BuildingVisual.CanForceRebuild)));
        }

        // UpdateBlueprintButtons uses this as a LINQ OrderBy key selector for its date sorts, so
        // it runs once per blueprint per listing - it is the O(n) half of what used to make that
        // listing O(n²). Public type, so no name lookup needed.
        TryPatchOne(harmony, log, "GetBlueprintIndex",
            () => AccessTools.Method(typeof(BlueprintFolder), nameof(BlueprintFolder.GetBlueprintIndex)));

        // Selection-screen open hotspots (docs §7), all reached by name for the same reason as
        // ModAssets above - BlueprintSelectionScreen, BlueprintPreviewScreen and the Vis_*
        // previews are internal types. These names are unique to the dialog path (nothing in the
        // import/placement/create sweeps touches them), so their run totals need no separating out.
        TryResolveType("BlueprintsV2.UnityUI.BlueprintSelectionScreen", out var selectionScreenType, log);
        TryResolveType("BlueprintsV2.UnityUI.BlueprintPreviewScreen", out var previewScreenType, log);
        TryResolveType("BlueprintsV2.UnityUI.Components.PreviewVisualizers.Vis_BuildingPreview", out var visBuildingType, log);
        TryResolveType("BlueprintsV2.UnityUI.Components.PreviewVisualizers.Vis_TilePreview", out var visTileType, log);

        if (selectionScreenType != null)
        {
            TryPatchOne(harmony, log, "ShowWindow", () => AccessTools.Method(selectionScreenType, "ShowWindow"));
            TryPatchOne(harmony, log, "ClearUIState", () => AccessTools.Method(selectionScreenType, "ClearUIState"));
            TryPatchOne(harmony, log, "UpdateBlueprintButtons", () => AccessTools.Method(selectionScreenType, "UpdateBlueprintButtons"));
            TryPatchOne(harmony, log, "AddOrGetBlueprintEntry", () => AccessTools.Method(selectionScreenType, "AddOrGetBlueprintEntry"));
            TryPatchOne(harmony, log, "RefreshEntryHighlight", () => AccessTools.Method(selectionScreenType, "RefreshEntryHighlight"));
            TryPatchOne(harmony, log, "SetMaterialState", () => AccessTools.Method(selectionScreenType, "SetMaterialState"));
            TryPatchOne(harmony, log, "UpdateBuildingButtons", () => AccessTools.Method(selectionScreenType, "UpdateBuildingButtons"));
        }
        if (previewScreenType != null)
        {
            TryPatchOne(harmony, log, "LoadBlueprintPreview", () => AccessTools.Method(previewScreenType, "LoadBlueprintPreview"));
            TryPatchOne(harmony, log, "Preview.ClearExisting", () => AccessTools.Method(previewScreenType, "ClearExisting"));
            TryPatchOne(harmony, log, "GeneratePreview_Buildings", () => AccessTools.Method(previewScreenType, "GeneratePreview_Buildings"));
            TryPatchOne(harmony, log, "RefreshVisualizerVisibility", () => AccessTools.Method(previewScreenType, "RefreshVisualizerVisibility"));
        }
        // Cold-open ceiling check (docs §7): what fraction of the ~464 us it costs to bring one
        // FileHierarchyEntry into existence could a pool actually avoid? A pooled row still has to
        // be rebound - blueprint, label, icon, tooltip, click handlers - so only the clone and the
        // one-time component construction are addressable. Splitting OnPrefabInit (built once per
        // row: 5 FButtons, a FToggleButton, 6 tooltips) from OnSpawn (rebound per row) is the
        // measurement that decides whether pooling is worth a runtime-switchable code path.
        TryResolveType("BlueprintsV2.UnityUI.Components.FileHierarchyEntry", out var hierarchyEntryType, log);
        if (hierarchyEntryType != null)
        {
            TryPatchOne(harmony, log, "FileHierarchyEntry.OnPrefabInit", () => AccessTools.Method(hierarchyEntryType, "OnPrefabInit"));
            TryPatchOne(harmony, log, "FileHierarchyEntry.OnSpawn", () => AccessTools.Method(hierarchyEntryType, "OnSpawn"));
            TryPatchOne(harmony, log, "FileHierarchyEntry.RefreshIcon", () => AccessTools.Method(hierarchyEntryType, "RefreshIcon"));
        }
        // Per-overload: the Transform overload forwards to the GameObject one, and a shared
        // accumulator would count the inner call's time twice - the same trap KInstantiate fell
        // into above.
        TryResolveType("UtilLibs.UIUtils", out var uiUtilsType, log);
        if (uiUtilsType != null)
            TryPatchEachOverload(harmony, log, "AddSimpleTooltipToObject", uiUtilsType);
        // The raw UI clone itself. The non-generic overload only: Util.KInstantiateUI<T> is generic,
        // which Harmony cannot patch as an open definition, and it forwards here anyway. Selected by
        // hand rather than by parameter types - the generic and non-generic overloads take the same
        // three parameters, so AccessTools.Method(type, name, types) throws AmbiguousMatchException.
        TryPatchOne(harmony, log, "KInstantiateUI", () => typeof(Util)
            .GetMethods(AccessTools.all)
            .FirstOrDefault(m => m.Name == "KInstantiateUI"
                && !m.IsGenericMethodDefinition
                && m.GetParameters().Length == 3));

        // Vis_ConduitPreview.Init overrides Vis_BuildingPreview.Init and calls base, so patching
        // the base alone is right - patching both would count a conduit's time twice. (Neither
        // synthetic blueprint contains conduits anyway; this is about not lying if one ever does.)
        if (visBuildingType != null)
        {
            TryPatchOne(harmony, log, "Vis_BuildingPreview.Init", () => AccessTools.Method(visBuildingType, "Init"));
            // Init only wires the KBatchedAnimController up; the first Play (loading and batching
            // the anim) happens in OnSpawn, which Unity runs from Start - i.e. on a later frame,
            // outside the stopwatch around the open. Without this hotspot that cost is invisible in
            // everything except the -settled numbers.
            TryPatchOne(harmony, log, "Vis_BuildingPreview.OnSpawn", () => AccessTools.Method(visBuildingType, "OnSpawn"));
        }
        if (visTileType != null)
        {
            // Pin the parameter list: Vis_TilePreview.Init(BuildingConfig) shadows rather than
            // overrides the parameterless Vis_SpritePreview.Init() it inherits, and a name-only
            // lookup resolves to that inherited one - which Harmony then refuses ("you can only
            // patch implemented methods"), silently leaving the tile branch unattributed.
            TryPatchOne(harmony, log, "Vis_TilePreview.Init",
                () => AccessTools.Method(visTileType, "Init", new[] { typeof(BuildingConfig) }));
            TryPatchOne(harmony, log, "Vis_TilePreview.ConnectAll", () => AccessTools.Method(visTileType, "ConnectAll"));
        }

        // use hotspot candidates (docs §7): now the costlier placement operation (140ms vs
        // visualize's 93.5ms at N=1000) but never broken down the way visualize/create were.
        // TryUse ⊇ IsPlaceable (the ValidCell/IsValidPlaceLocation gate, already known cheap from
        // the earlier wraparound investigation) + PlacePlannedBuilding ⊇ BuildingDef.Instantiate
        // (real GameObject creation, the likely mirror of visualize's KInstantiate / create's
        // BuildingDef.Build) + ApplyBuildingData (Rotatable, ApplyAdditionalBuildingData,
        // Prioritizable, UpdateConduitConnectionBits).
        TryPatchOne(harmony, log, "TryUse",
            () => AccessTools.Method(typeof(BuildingVisual), nameof(BuildingVisual.TryUse), new[] { typeof(int) }));
        TryPatchOne(harmony, log, "IsPlaceable",
            () => AccessTools.Method(typeof(BuildingVisual), nameof(BuildingVisual.IsPlaceable)));
        TryPatchOne(harmony, log, "PlacePlannedBuilding",
            () => AccessTools.Method(typeof(BuildingVisual), nameof(BuildingVisual.PlacePlannedBuilding)));
        TryPatchEachOverload(harmony, log, "Instantiate", typeof(BuildingDef));
        TryPatchOne(harmony, log, "ApplyBuildingData",
            () => AccessTools.Method(typeof(BuildingVisual), nameof(BuildingVisual.ApplyBuildingData)));
        TryPatchOne(harmony, log, "UpdateConduitConnectionBits",
            () => AccessTools.Method(typeof(BuildingVisual), "UpdateConduitConnectionBits"));

        // A failed registration only ever printed one line among ~60, and a hotspot that reads zero
        // calls is indistinguishable from a code path that genuinely never ran - so
        // ApplyRotatedCellAndMove sat broken across two commits without anyone noticing. Summarise at
        // the end, loudly, so an unresolved target is the last thing the run log says about
        // instrumentation rather than something to scroll back for.
        if (unresolved.Count > 0)
        {
            log.Line($"  *** perf instrumentation: {unresolved.Count} of {registered + unresolved.Count} " +
                     $"target(s) DID NOT RESOLVE - their hotspots will read 0 calls, which is not the " +
                     $"same as 'never called': {string.Join(", ", unresolved)}");
        }
        else
        {
            log.Line($"  perf instrumentation: all {registered} target(s) resolved");
        }

        applied = true;
    }

    private static bool TryResolveType(string fullName, out Type? type, HarnessLog log)
    {
        type = typeof(Blueprint).Assembly.GetType(fullName);
        if (type == null)
            log.Line($"  perf instrumentation: type not found: {fullName} (its hotspot(s) will be zero)");
        return type != null;
    }

    /// <summary>Patches every overload of <paramref name="methodName"/> under its *own* accumulator,
    /// named with its parameter list, logging each signature as it goes. Use this instead of
    /// <see cref="TryPatchAllOverloads"/> when the overloads may forward to each other: a shared
    /// accumulator counts a nested call's time twice (once in the outer overload, once in the
    /// inner), inflating the total and the call count - which is exactly what happened to the
    /// first `KInstantiate` measurement (docs §7).</summary>
    private static bool TryPatchEachOverload(Harmony harmony, HarnessLog log, string methodName, Type declaringType)
    {
        try
        {
            var overloads = declaringType.GetMethods(AccessTools.all).Where(m => m.Name == methodName).ToList();
            if (overloads.Count == 0)
            {
                log.Line($"  perf instrumentation: {declaringType.Name}.{methodName}: no overload found (its hotspot will be zero)");
                return false;
            }
            foreach (var overload in overloads)
            {
                string sig = $"{methodName}({string.Join(",", overload.GetParameters().Select(p => p.ParameterType.Name))})";
                log.Line($"  perf instrumentation: patching {declaringType.Name}.{sig}");
                Register(sig, overload, harmony);
            }
            return true;
        }
        catch (Exception e)
        {
            log.Line($"  perf instrumentation: {declaringType.Name}.{methodName}: failed to patch (its hotspot will be zero): {e}");
            return false;
        }
    }

    /// <summary>
    /// The overload of <paramref name="methodName"/> taking the most parameters, or null if there is
    /// none. For the mod's own "narrow overload forwards to the wide one" pairs this picks the wide
    /// one - the only one worth timing, since patching both under a shared accumulator double-counts
    /// the nested call (docs §7, <c>KInstantiate</c>).
    ///
    /// Use it in preference to a pinned parameter list wherever a method is likely to grow an
    /// optional parameter: a pin that stops matching leaves the hotspot silently reading zero, which
    /// is indistinguishable from a path that never executed. Logs the signature it settled on so the
    /// choice is visible in the run log rather than assumed.
    /// </summary>
    private static MethodBase? WidestOverload(Type declaringType, string methodName, HarnessLog log)
    {
        MethodBase? widest = null;
        foreach (var candidate in declaringType.GetMethods(AccessTools.all))
        {
            if (candidate.Name != methodName)
                continue;
            if (widest == null || candidate.GetParameters().Length > widest.GetParameters().Length)
                widest = candidate;
        }

        if (widest == null)
            log.Line($"  perf instrumentation: {declaringType.Name}.{methodName}: no overload found");
        else
            log.Line($"  perf instrumentation: {declaringType.Name}.{methodName} resolved to " +
                     $"({string.Join(",", widest.GetParameters().Select(p => p.ParameterType.Name))})");
        return widest;
    }

    private static bool TryPatchOne(Harmony harmony, HarnessLog log, string name, Func<MethodBase?> resolve)
    {
        try
        {
            var method = resolve();
            if (method == null)
            {
                log.Line($"  perf instrumentation: {name}: method not found (its hotspot will be zero)");
                unresolved.Add(name);
                return false;
            }
            Register(name, method, harmony);
            registered++;
            return true;
        }
        catch (Exception e)
        {
            log.Line($"  perf instrumentation: {name}: failed to patch (its hotspot will be zero): {e}");
            unresolved.Add(name);
            return false;
        }
    }

    /// <summary>Patches every overload of <paramref name="methodName"/> on <paramref name="declaringType"/>
    /// under one shared accumulator - for utility methods where pinning one exact overload's
    /// parameter types is brittle against a game update.</summary>
    private static bool TryPatchAllOverloads(Harmony harmony, HarnessLog log, string methodName, Type declaringType)
    {
        try
        {
            var overloads = declaringType.GetMethods(AccessTools.all).Where(m => m.Name == methodName).ToList();
            if (overloads.Count == 0)
            {
                log.Line($"  perf instrumentation: {declaringType.Name}.{methodName}: no overload found (its hotspot will be zero)");
                return false;
            }
            foreach (var overload in overloads)
                Register(methodName, overload, harmony);
            return true;
        }
        catch (Exception e)
        {
            log.Line($"  perf instrumentation: {declaringType.Name}.{methodName}: failed to patch (its hotspot will be zero): {e}");
            return false;
        }
    }

    private static void Register(string name, MethodBase method, Harmony harmony)
    {
        if (!byName.TryGetValue(name, out var acc))
            byName[name] = acc = new Accumulator();
        byMethod[method] = acc;
        harmony.Patch(method,
            prefix: new HarmonyMethod(typeof(PerfInstrumentation), nameof(TimerPrefix)),
            postfix: new HarmonyMethod(typeof(PerfInstrumentation), nameof(TimerPostfix)));
    }

    /// <summary>
    /// Managed-heap bytes as well as time: the counters that work on this runtime are in
    /// <see cref="AllocProbe"/>, and per-hotspot allocation is what decides whether a per-frame path
    /// is worth attacking with pooling or with plain arithmetic (docs §7).
    ///
    /// <b><c>Stopwatch.GetTimestamp()</c>, never <c>Stopwatch.StartNew()</c>.</b> A <c>Stopwatch</c>
    /// is a class, so starting one allocates ~40 bytes - and a child hotspot's prefix runs *inside*
    /// its parent's measured window, so every patched child charged its wrapper's allocation to its
    /// parent. That made a method's reported bytes/call grow as its children were instrumented
    /// (<c>GetVisualizerColor</c>: 230 B/call, then 368 B/call once four more children were patched)
    /// and it was measuring the harness, not the mod. A timestamp is a long: no allocation, so the
    /// bytes belong to the code under test.
    /// </summary>
    private static void TimerPrefix(out CallState __state) => __state = new CallState(Stopwatch.GetTimestamp(), GC.GetTotalMemory(false));

    private static void TimerPostfix(CallState __state, MethodBase __originalMethod)
    {
        long ticks = Stopwatch.GetTimestamp() - __state.StartTimestamp;
        long heapAfter = GC.GetTotalMemory(false);
        if (byMethod.TryGetValue(__originalMethod, out var acc))
            acc.Add(ticks * TicksToMs, heapAfter - __state.HeapBefore);
    }

    private static readonly double TicksToMs = 1000.0 / Stopwatch.Frequency;

    private readonly struct CallState
    {
        public CallState(long startTimestamp, long heapBefore)
        {
            StartTimestamp = startTimestamp;
            HeapBefore = heapBefore;
        }

        public long StartTimestamp { get; }
        public long HeapBefore { get; }
    }

    public static void ResetAll()
    {
        foreach (var acc in byName.Values)
            acc.Reset();
    }

    /// <summary>Every registered hotspot's snapshot, keyed by the name passed to <see cref="Apply"/>'s
    /// Patch* calls (all overloads of one method share a single name/accumulator).</summary>
    public static IReadOnlyList<(string Name, Accumulator.Snapshot Snap)> SnapshotAll() =>
        byName.Select(kv => (kv.Key, kv.Value.Snap())).ToList();

    internal sealed class Accumulator
    {
        private readonly object gate = new();
        private long calls;
        private double totalMs;
        private long totalBytes;

        public void Add(double elapsedMs, long bytes)
        {
            lock (gate)
            {
                calls++;
                totalMs += elapsedMs;
                // Summed as measured, negatives included: a collection inside one call subtracts
                // there and the bytes it reclaimed were counted on the calls that allocated them,
                // so the total stays the right order of magnitude. Read bytes/call, not the total.
                totalBytes += bytes;
            }
        }

        public void Reset()
        {
            lock (gate) { calls = 0; totalMs = 0; totalBytes = 0; }
        }

        public Snapshot Snap()
        {
            lock (gate) return new Snapshot(calls, totalMs, totalBytes);
        }

        public readonly struct Snapshot
        {
            public Snapshot(long calls, double totalMs, long totalBytes)
            {
                Calls = calls;
                TotalMs = totalMs;
                TotalBytes = totalBytes;
            }

            public long Calls { get; }
            public double TotalMs { get; }
            public long TotalBytes { get; }
            public double AvgUsPerCall => Calls == 0 ? 0 : TotalMs * 1000.0 / Calls;
            public double AvgBytesPerCall => Calls == 0 ? 0 : (double)TotalBytes / Calls;
        }
    }
}
