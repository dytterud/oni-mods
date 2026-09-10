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

    // Placement instantiates real GameObjects per building (much heavier per unit than JSON
    // parsing) and needs real dug Grid cells, so its sweep is capped lower than import's.
    private static readonly int[] PlacementSizes = { 100, 500, 1000, 2000 };
    private const int PlacementRowWidth = SyntheticBlueprint.RowWidth;
    private const int UseWarmup = 1, UseIterations = 2;               // mutating - kept small
    private const int VisualizeWarmup = 2, VisualizeIterations = 5;   // non-mutating - redrawable
    private const int RegionEdgeMargin = 10;                          // cells kept clear of the map border

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
                PerfInstrumentation.Apply(HarnessGate.HarmonyInstance, log);
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

        yield return RunPlacementSweep(report, log);

        var hotspots = PerfInstrumentation.SnapshotAll();
        foreach (var (name, snap) in hotspots)
            log.Line($"  {name}: {snap.Calls} calls, {snap.TotalMs:F1}ms total, {snap.AvgUsPerCall:F1}us/call");

        PerfWriter.Write(HarnessGate.PerfPath, report, hotspots);
        log.Line($"wrote {HarnessGate.PerfPath}");
    }

    /// <summary>
    /// Times <c>VisualizeBlueprint</c> (non-mutating - redrawn at one fixed spot every call, same
    /// as real mouse-hover redraw) and <c>UseBlueprint</c> (mutating - creates real build orders,
    /// so each draw claims a fresh strip of a region dug once up front) for
    /// <see cref="PlacementSizes"/>. Wall-clock only for this pass - see docs §7; a hotspot shows
    /// up as a large gap between the two operations, the same way GetValidMaterials did for
    /// import.
    /// </summary>
    private static IEnumerator RunPlacementSweep(PerfReport report, HarnessLog log)
    {
        int useDraws = UseWarmup + UseIterations;
        int regionRows = PlacementSizes.Sum(RowsFor) * useDraws;
        int maxRows = Grid.HeightInCells - 2 * RegionEdgeMargin;
        if (regionRows > maxRows)
        {
            log.Line($"  placement-perf region ({regionRows} rows) exceeds map height budget " +
                     $"({maxRows}); shrinking - largest size(s)' use draws will be skipped");
            regionRows = Math.Max(0, maxRows);
        }

        int x0 = Grid.WidthInCells / 2 - PlacementRowWidth / 2;
        int y0 = RegionEdgeMargin;
        log.Line($"placement-perf region: x[{x0},{x0 + PlacementRowWidth}) y[{y0},{y0 + regionRows}) " +
                 $"(anchor cell for reference: {HarnessCases.AnchorCell} {Grid.CellToXY(HarnessCases.AnchorCell)})");

        yield return DigRegion(x0, y0, PlacementRowWidth, regionRows, log);

        var cfg = ModConfig();
        bool savedTech = cfg.RequireConstructable_Tech, savedMat = cfg.RequireConstructable_Material;
        cfg.RequireConstructable_Tech = false;
        cfg.RequireConstructable_Material = false;
        var st = BlueprintState.CurrentStateInfo();
        st.ForceOverrideTransformations = true;

        try
        {
            var fixedOrigin = new Vector2I(x0, y0);
            foreach (int n in PlacementSizes)
            {
                log.Line($"visualize N={n}");
                var bp = SyntheticBlueprint.Build(n);
                yield return TimeOp(report, log, "visualize", n, warmup: VisualizeWarmup, iterations: VisualizeIterations,
                    () => BlueprintState.VisualizeBlueprint(fixedOrigin, bp));
                BlueprintState.ClearVisuals();
            }

            int rowCursor = 0;
            foreach (int n in PlacementSizes)
            {
                int rows = RowsFor(n);
                if (rowCursor + rows * useDraws > regionRows)
                {
                    log.Line($"  SKIP use-N{n}: not enough dug region left " +
                             $"({regionRows - rowCursor} rows, need {rows * useDraws})");
                    continue;
                }

                log.Line($"use N={n}");
                var bp = SyntheticBlueprint.Build(n);
                Vector2I origin = default;
                yield return TimeOp(report, log, "use", n, warmup: UseWarmup, iterations: UseIterations,
                    setup: () =>
                    {
                        origin = new Vector2I(x0, y0 + rowCursor);
                        rowCursor += rows;
                        // VisualizeBlueprint already calls UpdateVisual(forcingRedraw: true) at
                        // its own end - an extra explicit call here was redundant (instrumentation
                        // caught it: doubled the AddTileBlock/RefreshCell/GetVisualizerColor call
                        // counts for every use draw with no effect on the placed result).
                        BlueprintState.VisualizeBlueprint(origin, bp);
                    },
                    body: () => BlueprintState.UseBlueprint(BlueprintState.PlayerId_DefaultTilePreviews, origin, bp));
                BlueprintState.ClearVisuals();
            }
        }
        finally
        {
            st.ForceOverrideTransformations = false;
            cfg.RequireConstructable_Tech = savedTech;
            cfg.RequireConstructable_Material = savedMat;
        }
    }

    private static int RowsFor(int n) => (n + PlacementRowWidth - 1) / PlacementRowWidth;

    /// <summary>
    /// Digs every solid cell in the rectangle once, up front, then waits for it to clear - the
    /// same dig-then-poll pattern <c>HarnessCases.PlaceAt</c> uses for its (much smaller) regression
    /// case regions, generalized to absolute map coordinates so a large sweep can't run off the
    /// map edge near the fixture colony. Cells outside <c>Grid.IsValidCell</c> are silently
    /// dropped rather than dug.
    /// </summary>
    private static IEnumerator DigRegion(int x0, int y0, int width, int height, HarnessLog log)
    {
        var cells = new List<int>(width * height);
        for (int dy = 0; dy < height; dy++)
            for (int dx = 0; dx < width; dx++)
            {
                int c = Grid.XYToCell(x0 + dx, y0 + dy);
                if (Grid.IsValidCell(c))
                    cells.Add(c);
            }
        log.Line($"  digging placement-perf region: {cells.Count}/{width * height} valid cells");

        foreach (int c in cells)
            if (Grid.IsSolidCell(c))
                SimMessages.Dig(c, skipEvent: true);

        float waited = 0f;
        while (cells.Any(Grid.IsSolidCell) && waited < 180f)
        {
            waited += Time.unscaledDeltaTime;
            yield return null;
        }
        log.Line($"  region dig settled after {waited:F1}s ({cells.Count(Grid.IsSolidCell)} still solid)");
    }

    // Config.Instance lives on PLib's SingletonOptions<Config>, which this dll doesn't reference -
    // reach the inherited static getter by reflection, same as HarnessCases.ModConfig().
    private static Config ModConfig() => (Config)typeof(Config)
        .GetProperty("Instance", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)!
        .GetValue(null)!;

    /// <summary>Times <paramref name="body"/> over warmup + timed iterations. Alloc numbers are
    /// recorded per §7 but come back ~0 on ONI's embedded Mono - <c>GC.GetAllocatedBytesForCurrentThread</c>
    /// isn't meaningfully implemented on this runtime (verified: still 0 at N=5000, where real
    /// allocation is unquestionably in the hundreds of KB). Left in in case a future ONI Unity/Mono
    /// upgrade makes it work; don't read the current numbers as "no allocation".
    ///
    /// <paramref name="setup"/>, if given, runs immediately before every call to
    /// <paramref name="body"/> (warmup included) but outside the stopwatch - for an operation
    /// like <c>UseBlueprint</c> that mutates state and needs fresh input each call (e.g. a new
    /// target cell) rather than being safely repeatable in place.</summary>
    private static IEnumerator TimeOp(PerfReport report, HarnessLog log, string opName, int n,
        int warmup, int iterations, SysAction body, SysAction? setup = null)
    {
        for (int i = 0; i < warmup; i++)
        {
            setup?.Invoke();
            body();
            yield return null;
        }

        var timesMs = new List<double>(iterations);
        var allocBytes = new List<long>(iterations);
        for (int i = 0; i < iterations; i++)
        {
            setup?.Invoke();
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
