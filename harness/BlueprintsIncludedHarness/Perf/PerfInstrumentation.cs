using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using BlueprintsV2.BlueprintData;
using BlueprintsV2.Visualizers;
using HarmonyLib;

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
        TryPatchAllOverloads(harmony, log, "KInstantiate", typeof(GameUtil));
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

        applied = true;
    }

    private static bool TryResolveType(string fullName, out Type? type, HarnessLog log)
    {
        type = typeof(Blueprint).Assembly.GetType(fullName);
        if (type == null)
            log.Line($"  perf instrumentation: type not found: {fullName} (its hotspot(s) will be zero)");
        return type != null;
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
