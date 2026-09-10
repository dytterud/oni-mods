using System;
using System.Diagnostics;
using BlueprintsV2.BlueprintData;
using HarmonyLib;

namespace BlueprintsV2.Harness.Perf;

/// <summary>
/// Manually Harmony-patches two mod methods to count calls + accumulate elapsed time, so
/// <c>perf.json</c> can directly attribute import cost rather than leaving it to inference from
/// wall-clock alone. Applied only in perf mode, via the same <see cref="Harmony"/> instance ONI gave
/// the mod in <c>OnLoad</c> - manual (not attribute) patching because one target lives on an
/// <c>internal</c> mod type the harness can't name at compile time.
/// </summary>
internal static class PerfInstrumentation
{
    private static readonly Accumulator GetValidMaterialsAcc = new();
    private static readonly Accumulator SanitizeSelectedTagsAcc = new();
    private static bool applied;

    public static void Apply(Harmony harmony)
    {
        if (applied)
            return;

        // ModAssets is internal - reach its Type by name (same trick FixtureBuilder uses for
        // GetValidMaterials). BuildingConfig is public, so that one patches directly.
        var modAssetsType = typeof(Blueprint).Assembly.GetType("BlueprintsV2.ModAssets")
            ?? throw new InvalidOperationException("BlueprintsV2.ModAssets type not found");
        var getValidMaterials = AccessTools.Method(modAssetsType, "GetValidMaterials")
            ?? throw new InvalidOperationException("ModAssets.GetValidMaterials method not found");
        var sanitizeSelectedTags = AccessTools.Method(typeof(BuildingConfig), "SanitizeSelectedTags")
            ?? throw new InvalidOperationException("BuildingConfig.SanitizeSelectedTags method not found");

        var prefix = new HarmonyMethod(typeof(PerfInstrumentation), nameof(TimerPrefix));
        harmony.Patch(getValidMaterials, prefix: prefix,
            postfix: new HarmonyMethod(typeof(PerfInstrumentation), nameof(PostfixGetValidMaterials)));
        harmony.Patch(sanitizeSelectedTags, prefix: prefix,
            postfix: new HarmonyMethod(typeof(PerfInstrumentation), nameof(PostfixSanitizeSelectedTags)));

        applied = true;
    }

    private static void TimerPrefix(out Stopwatch __state) => __state = Stopwatch.StartNew();
    private static void PostfixGetValidMaterials(Stopwatch __state) => GetValidMaterialsAcc.Add(__state.Elapsed);
    private static void PostfixSanitizeSelectedTags(Stopwatch __state) => SanitizeSelectedTagsAcc.Add(__state.Elapsed);

    public static void ResetAll()
    {
        GetValidMaterialsAcc.Reset();
        SanitizeSelectedTagsAcc.Reset();
    }

    public static Accumulator.Snapshot GetValidMaterialsSnapshot() => GetValidMaterialsAcc.Snap();
    public static Accumulator.Snapshot SanitizeSelectedTagsSnapshot() => SanitizeSelectedTagsAcc.Snap();

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
