using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using BlueprintsV2.BlueprintData;
using UnityEngine;

namespace BlueprintsV2.Harness.Perf;

/// <summary>
/// docs/in-game-regression-testing.md §7: opt-in benchmark pass, not part of the pass/fail
/// regression run. Pauses the sim, then for each blueprint size runs a warmup batch (discarded)
/// followed by timed iterations of each import operation, recording elapsed time, allocations, and
/// the <see cref="PerfInstrumentation"/> hotspot totals, and writes <c>perf.json</c>.
/// </summary>
internal static class PerfRunner
{
    private static readonly int[] Sizes = { 100, 500, 1000, 5000 };

    // ModAssets.TryImportBlueprintFromString is internal - reach it by reflection, same pattern
    // FixtureBuilder uses for ModAssets.GetValidMaterials.
    private static readonly MethodInfo TryImportBlueprintFromStringMethod =
        typeof(Blueprint).Assembly.GetType("BlueprintsV2.ModAssets")!
            .GetMethod("TryImportBlueprintFromString", BindingFlags.NonPublic | BindingFlags.Static)!;

    public static IEnumerator Run(HarnessLog log)
    {
        SpeedControlScreen.Instance?.Pause(false);

        if (HarnessGate.HarmonyInstance != null)
        {
            try
            {
                PerfInstrumentation.Apply(HarnessGate.HarmonyInstance);
            }
            catch (Exception e)
            {
                log.Line("perf instrumentation failed to apply (hotspot numbers will be zero): " + e);
            }
        }
        PerfInstrumentation.ResetAll();

        var report = new PerfReport();

        foreach (int n in Sizes)
        {
            log.Line($"building synthetic blueprint N={n}");
            string json = SyntheticBlueprint.BuildJson(n);
            log.Line($"  json length {json.Length:N0} chars");

            yield return TimeOp(report, log, "deserialize", n, warmup: 3, iterations: 10,
                () => { _ = new Blueprint(new System.Text.StringBuilder(json)); });

            yield return TimeOp(report, log, "full-import", n, warmup: 2, iterations: 5,
                () => RunFullImport(json));
        }

        var gvm = PerfInstrumentation.GetValidMaterialsSnapshot();
        var sst = PerfInstrumentation.SanitizeSelectedTagsSnapshot();
        log.Line($"  GetValidMaterials: {gvm.Calls} calls, {gvm.TotalMs:F1}ms total, {gvm.AvgUsPerCall:F1}us/call");
        log.Line($"  SanitizeSelectedTags: {sst.Calls} calls, {sst.TotalMs:F1}ms total, {sst.AvgUsPerCall:F1}us/call");

        PerfWriter.Write(HarnessGate.PerfPath, report, gvm, sst);
        log.Line($"wrote {HarnessGate.PerfPath}");
    }

    /// <summary>Times <paramref name="body"/> over warmup + timed iterations. Alloc numbers are
    /// recorded per §7 but come back ~0 on ONI's embedded Mono - <c>GC.GetAllocatedBytesForCurrentThread</c>
    /// isn't meaningfully implemented on this runtime (verified: still 0 at N=5000, where real
    /// allocation is unquestionably in the hundreds of KB). Left in in case a future ONI Unity/Mono
    /// upgrade makes it work; don't read the current numbers as "no allocation".</summary>
    private static IEnumerator TimeOp(PerfReport report, HarnessLog log, string opName, int n,
        int warmup, int iterations, SysAction body)
    {
        for (int i = 0; i < warmup; i++)
        {
            body();
            yield return null;
        }

        var timesMs = new List<double>(iterations);
        var allocBytes = new List<long>(iterations);
        for (int i = 0; i < iterations; i++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            var sw = Stopwatch.StartNew();
            body();
            sw.Stop();
            long after = GC.GetAllocatedBytesForCurrentThread();

            timesMs.Add(sw.Elapsed.TotalMilliseconds);
            allocBytes.Add(after - before);
            yield return null;
        }

        timesMs.Sort();
        double median = Percentile(timesMs, 0.5);
        double p95 = Percentile(timesMs, 0.95);
        double meanAlloc = allocBytes.Count == 0 ? 0 : allocBytes.Average();

        log.Line($"  {opName}-N{n}: median={median:F2}ms p95={p95:F2}ms alloc={meanAlloc / 1024.0:F1}KB");
        report.Add(opName, n, iterations, median, p95, meanAlloc);
    }

    private static double Percentile(List<double> sortedAscending, double p)
    {
        if (sortedAscending.Count == 0)
            return 0;
        int idx = (int)Math.Ceiling(p * sortedAscending.Count) - 1;
        idx = Math.Max(0, Math.Min(sortedAscending.Count - 1, idx));
        return sortedAscending[idx];
    }

    /// <summary>The clipboard-import path minus the OS clipboard: parse, write to disk, and (via
    /// HandleBlueprintLoading) reparse from disk. Cleans up afterward so iterations don't pile up
    /// blueprint files / folder entries.</summary>
    private static void RunFullImport(string json)
    {
        object?[] args = { json, null, true };
        TryImportBlueprintFromStringMethod.Invoke(null, args);
        if (args[1] is Blueprint bp)
        {
            bp.DeleteFile();
            bp.RemoveFromFolder();
        }
    }
}
