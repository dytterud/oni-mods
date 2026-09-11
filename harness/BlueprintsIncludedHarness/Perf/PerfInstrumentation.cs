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

    /// <summary>Flip to true for a one-off attribution run over the per-frame update path, then flip
    /// back. See the comment at its use site: instrumenting methods that run once per visual costs
    /// more than the methods do, so a run with this on reports call counts and relative shares
    /// honestly but its <c>update-visual</c> medians must not be compared with a run without it.
    /// </summary>
    private const bool PerVisualHotspots = false;

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
        }
        TryPatchAllOverloads(harmony, log, "NaturalBuildingCell", typeof(GameUtil));

        // update-visual hotspots (docs §7): the per-frame redraw the Use Blueprint tool runs from
        // OnMouseMove. These three run once per update (or once per visual on an already-expensive
        // body), so their wrapper overhead is proportionally negligible and they stay always-on.
        TryPatchOne(harmony, log, "UpdateVisual",
            () => AccessTools.Method(typeof(BlueprintState), nameof(BlueprintState.UpdateVisual)));
        TryPatchOne(harmony, log, "StoreOccupiedArea",
            () => AccessTools.Method(typeof(BlueprintState), "StoreOccupiedArea"));
        TryPatchOne(harmony, log, "ApplyRotatedCellAndMove",
            () => AccessTools.Method(typeof(BlueprintState.BlueprintTransformationInfo), "ApplyRotatedCellAndMove",
                new[] { typeof(Vector2I), typeof(IVisual), typeof(bool), typeof(bool), typeof(bool) }));

        // The rest of that path runs once PER VISUAL - 2000x per call at N=2000 - on bodies that are
        // a handful of arithmetic ops. A Harmony wrapper plus Stopwatch.StartNew() costs more than
        // the method it measures there, which would flatten exactly the improvement the integer
        // rotation work is chasing. So: off for the timing runs, on for one attribution run, and
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

    private static bool TryPatchOne(Harmony harmony, HarnessLog log, string name, Func<MethodBase?> resolve)
    {
        try
        {
            var method = resolve();
            if (method == null)
            {
                log.Line($"  perf instrumentation: {name}: method not found (its hotspot will be zero)");
                return false;
            }
            Register(name, method, harmony);
            return true;
        }
        catch (Exception e)
        {
            log.Line($"  perf instrumentation: {name}: failed to patch (its hotspot will be zero): {e}");
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

    private static void TimerPrefix(out Stopwatch __state) => __state = Stopwatch.StartNew();

    private static void TimerPostfix(Stopwatch __state, MethodBase __originalMethod)
    {
        if (byMethod.TryGetValue(__originalMethod, out var acc))
            acc.Add(__state.Elapsed);
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

        public void Add(TimeSpan elapsed)
        {
            lock (gate)
            {
                calls++;
                totalMs += elapsed.TotalMilliseconds;
            }
        }

        public void Reset()
        {
            lock (gate) { calls = 0; totalMs = 0; }
        }

        public Snapshot Snap()
        {
            lock (gate) return new Snapshot(calls, totalMs);
        }

        public readonly struct Snapshot
        {
            public Snapshot(long calls, double totalMs)
            {
                Calls = calls;
                TotalMs = totalMs;
            }

            public long Calls { get; }
            public double TotalMs { get; }
            public double AvgUsPerCall => Calls == 0 ? 0 : TotalMs * 1000.0 / Calls;
        }
    }
}
