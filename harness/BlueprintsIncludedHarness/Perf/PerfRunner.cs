using System;
using System.Collections;
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
    // visualize doesn't consume region rows (it redraws one fixed spot regardless of size), so it
    // keeps the full sweep; use/create each need their own disjoint row band per size, and this
    // fixture map only has ~94 rows of same-world room below the anchor - 2000 alone would eat
    // that whole budget for use whilst leaving nothing for create, so they share a smaller sweep.
    private static readonly int[] PlacementSizes = { 100, 500, 1000, 2000 };
    private static readonly int[] UseCreateSizes = { 100, 500, 1000 };
    private const int PlacementRowWidth = SyntheticBlueprint.RowWidth;
    private const int UseWarmup = 1, UseIterations = 2;               // mutating - kept small
    private const int VisualizeWarmup = 2, VisualizeIterations = 5;   // non-mutating - redrawable
    private const int CreateWarmup = 2, CreateIterations = 5;         // non-mutating - redrawable
    private const int UpdateVisualWarmup = 4, UpdateVisualIterations = 20;  // per-frame op, cheap per call
    private const int RegionEdgeMargin = 10;                          // cells kept clear of the map border
    private const int RegionGapFromAnchor = 20;                       // cells kept clear of the small regression-case footprint near the pod

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
        yield return SelectionScreenPerf.Run(report, log);

        var hotspots = PerfInstrumentation.SnapshotAll();
        foreach (var (name, snap) in hotspots)
            log.Line($"  {name}: {snap.Calls} calls, {snap.TotalMs:F1}ms total, {snap.AvgUsPerCall:F1}us/call");

        PerfWriter.Write(HarnessGate.PerfPath, report, hotspots);
        log.Line($"wrote {HarnessGate.PerfPath}");
    }

    /// <summary>
    /// Digs one region up front, then times <c>VisualizeBlueprint</c> (non-mutating - redrawn at
    /// one fixed spot every call, same as real mouse-hover redraw), <c>UseBlueprint</c> (mutating
    /// - creates real build orders, so each draw claims a fresh strip of the region) and, via
    /// <see cref="RunCreateSweep"/>, <c>CreateBlueprint</c> (non-mutating - a pure read over a
    /// rectangle of real finished buildings) for <see cref="PlacementSizes"/>. See docs §7.
    /// </summary>
    private static IEnumerator RunPlacementSweep(PerfReport report, HarnessLog log)
    {
        int useDraws = UseWarmup + UseIterations;
        int useRows = UseCreateSizes.Sum(RowsFor) * useDraws;
        // create's buildings are real finished GameObjects (Grid.Objects-occupying), unlike
        // visualize's previews or use's still-pending orders - they need their own disjoint band
        // of the region, built once per size (CreateBlueprint itself is a pure read, safe to time
        // repeatedly over the same rectangle like visualize).
        int createRows = UseCreateSizes.Sum(RowsFor);
        int regionRows = useRows + createRows;

        // Anchor-relative, not an arbitrary absolute map coordinate: UseBlueprint's placement
        // check (BuildingVisual.ValidCell) also gates on Grid.IsValidCellInWorld(cell,
        // ClusterManager.Instance.activeWorldId) - an absolute coordinate picked without regard
        // for world/asteroid boundaries can land in a different world than the fixture colony's,
        // which silently fails placement even after fixing visibility (CreateBlueprint has no such
        // gate, which is why it worked at the old absolute location while use still didn't).
        // Straight down from the Printing Pod stays in the same world and clears the small
        // regression-case footprint (which only extends to dy -6).
        var anchorXY = Grid.CellToXY(HarnessCases.AnchorCell);
        int maxRows = Math.Max(0, anchorXY.y - RegionGapFromAnchor - RegionEdgeMargin);
        if (regionRows > maxRows)
        {
            log.Line($"  placement-perf region ({regionRows} rows wanted) exceeds available room below " +
                     $"the anchor ({maxRows}); shrinking - use gets priority, create gets what's left, " +
                     $"largest size(s) of either will be skipped");
            regionRows = maxRows;
            // useRows must actually shrink too, not just the outer regionRows - otherwise use's own
            // skip check (below) compares against a budget bigger than what DigRegion/Reveal
            // actually covered, and its later sizes silently run onto undug/unrevealed cells.
            useRows = Math.Min(useRows, regionRows);
            createRows = regionRows - useRows;
        }

        // Extend in the same direction (negative dx) FixtureLayout's own buildings already use
        // successfully, rather than an arbitrary absolute X.
        int x0 = Math.Clamp(anchorXY.x - PlacementRowWidth - RegionGapFromAnchor,
            RegionEdgeMargin, Math.Max(RegionEdgeMargin, Grid.WidthInCells - PlacementRowWidth - RegionEdgeMargin));
        int y0 = RegionEdgeMargin;
        log.Line($"placement-perf region: x[{x0},{x0 + PlacementRowWidth}) y[{y0},{y0 + regionRows}) " +
                 $"- rows [0,{useRows}) for visualize/use, [{useRows},{regionRows}) for create " +
                 $"(anchor cell for reference: {HarnessCases.AnchorCell} {anchorXY})");

        yield return DigRegion(x0, y0, PlacementRowWidth, regionRows, log);

        var cfg = ModConfig();
        bool savedTech = cfg.RequireConstructable_Tech, savedMat = cfg.RequireConstructable_Material;
        cfg.RequireConstructable_Tech = false;
        cfg.RequireConstructable_Material = false;
        var st = BlueprintState.CurrentStateInfo();
        st.ForceOverrideTransformations = true;

        // BlueprintTransformationInfo.GetRotatedCell shifts every building's offset by the
        // default anchor state (BottomCenter: half the blueprint's width in X) before converting
        // to a cell - diagnosed via a Harmony postfix that this makes visPos.x negative for any
        // building whose offset is smaller than the shift, and Grid.PosToCell's linear cell index
        // (x + y*Grid.WidthInCells) then wraps a negative x into the *previous row* at a huge x,
        // landing in unrelated (often invalid) territory instead of failing cleanly - it accounted
        // for ~50% of "use" draws silently landing outside our dug/revealed region.
        //
        // Directly zeroing originShiftX/Y wasn't enough: VisualizeBlueprint calls
        // CheckPermittedRotations -> RefreshAnchorState, which recomputes both floats from _state
        // (still Config.Instance.DefaultAnchorState) on every single call, undoing the override
        // before the first placement even happens. _state has no public setter either, so flip it
        // (via reflection) to BottomLeft - the one AnchorState with a (0,0) shift - so every
        // RefreshAnchorState call keeps landing on zero instead of reverting to BottomCenter.
        var stateField = AccessTools.Field(typeof(BlueprintState.BlueprintTransformationInfo), "_state");
        object? savedState = stateField?.GetValue(st);
        stateField?.SetValue(st, BlueprintAnchorState.BottomLeft);
        if (stateField == null)
            log.Line("  _state field not found - use draws may still hit the anchor-shift wraparound");

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

            yield return RunUpdateVisualSweep(report, log, fixedOrigin);

            int rowCursor = 0;
            foreach (int n in UseCreateSizes)
            {
                int rows = RowsFor(n);
                if (rowCursor + rows * useDraws > useRows)
                {
                    log.Line($"  SKIP use-N{n}: not enough dug region left " +
                             $"({useRows - rowCursor} rows, need {rows * useDraws})");
                    continue;
                }

                log.Line($"use N={n}");
                var bp = SyntheticBlueprint.Build(n);
                Vector2I origin = default;
                int constructablesBefore = CountConstructables();
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
                int createdTotal = CountConstructables() - constructablesBefore;
                log.Line($"  use-N{n}: {createdTotal} Constructable(s) created across {useDraws} draws (expected {n * useDraws})");
                BlueprintState.ClearVisuals();
            }
        }
        finally
        {
            st.ForceOverrideTransformations = false;
            cfg.RequireConstructable_Tech = savedTech;
            cfg.RequireConstructable_Material = savedMat;
            if (stateField != null) stateField.SetValue(st, savedState);
        }

        yield return RunCreateSweep(report, log, x0, y0 + useRows, Math.Max(0, regionRows - useRows));
        yield return RunUpdateVisualDenseSweep(report, log);
    }

    /// <summary>
    /// Times <c>BlueprintState.UpdateVisual</c> - the redraw the Use Blueprint tool runs from
    /// <c>OnMouseMove</c> on every cursor cell change. Unlike every other operation in this file
    /// this one is <i>per frame</i>, so its cost is what decides whether dragging a large blueprint
    /// is smooth; it is also the only sweep that drives the path with <c>forcingRedraw: false</c>
    /// (<c>visualize</c>/<c>use</c> both force), which is the branch real mouse movement takes.
    ///
    /// Three things keep it honest:
    /// <list type="bullet">
    /// <item>The cursor <b>must move every call</b> - <c>UpdateVisual</c> early-returns when the
    /// origin equals <c>lastBlueprintPos</c>, so a fixed origin would time the early-out. It
    /// oscillates by one cell, which is both sufficient and exactly what a slow drag does.</item>
    /// <item>It runs <b>inside the dug and revealed region</b>. <c>BuildingVisual.ValidCell</c>
    /// short-circuits on <c>Grid.IsVisible</c>, so outside it the colour evaluation - the bulk of
    /// the work - would never run, the same trap that made <c>use</c> report plausible numbers
    /// while silently failing (docs §7). It consumes no region rows, like <c>visualize</c>.</item>
    /// <item>No <c>settleFrames</c>: <c>UpdateVisual</c> creates no <c>GameObject</c>s, so unlike
    /// <c>visualize</c> its whole cost is synchronous and the plain median is the real number.</item>
    /// </list>
    /// </summary>
    private static IEnumerator RunUpdateVisualSweep(PerfReport report, HarnessLog log, Vector2I regionOrigin)
    {
        foreach (var (buildingId, label) in UpdateVisualVariants)
        {
            foreach (int n in PlacementSizes)
            {
                log.Line($"update-visual [{label}] N={n}");
                var bp = SyntheticBlueprint.Build(n, buildingId, $"PerfUpdateVisual{label}{n}");
                BlueprintState.VisualizeBlueprint(regionOrigin, bp);
                log.Line($"  visuals: {VisualCounts()}");

                int step = 0;
                var cursor = regionOrigin;
                yield return TimeOp(report, log, $"update-visual-{label}", n,
                    warmup: UpdateVisualWarmup, iterations: UpdateVisualIterations,
                    setup: () => cursor = new Vector2I(regionOrigin.x + (step++ % 2), regionOrigin.y),
                    body: () => BlueprintState.UpdateVisual(BlueprintState.PlayerId_DefaultTilePreviews, cursor, forcingRedraw: false));

                BlueprintState.ClearVisuals();
            }
        }
    }

    /// <summary>
    /// <c>BlueprintState.AddVisual</c> files a visual under <c>FoundationVisuals</c> or
    /// <c>DependentVisuals</c> by <c>BuildingDef.IsFoundation</c>, and <c>UpdateVisual</c> treats
    /// the two lists differently - only the dependent list gets the second <c>RefreshColor</c> pass
    /// that the deferred-colour optimisation deduplicates against. An all-<c>Tile</c> blueprint is
    /// therefore <i>entirely</i> foundations and would measure that optimisation as exactly zero,
    /// so the sweep runs both: <c>Tile</c> for the foundation path and <c>Ladder</c> (1x1, raw
    /// mineral, not a foundation) for the dependent one. <see cref="VisualCounts"/> logs the actual
    /// split rather than trusting this comment.
    /// </summary>
    private static readonly (string BuildingId, string Label)[] UpdateVisualVariants =
    {
        ("Tile", "tile"),
        ("Ladder", "ladder"),
    };

    // FoundationVisuals/DependentVisuals are private statics on BlueprintState; read them by
    // reflection purely to report the split, so a variant that silently lands entirely in the wrong
    // list shows up in the log instead of as a timing that quietly measures the other path.
    private static string VisualCounts()
    {
        try
        {
            int foundation = VisualListCount("FoundationVisuals");
            int dependent = VisualListCount("DependentVisuals");
            return $"{foundation} foundation, {dependent} dependent";
        }
        catch (Exception e)
        {
            return "unavailable: " + e.Message;
        }
    }

    private static int VisualListCount(string fieldName)
    {
        var field = AccessTools.Field(typeof(BlueprintState), fieldName);
        if (field?.GetValue(null) is not System.Collections.IDictionary byPlayer)
            return -1;
        return byPlayer[BlueprintState.PlayerId_DefaultTilePreviews] is System.Collections.ICollection list
            ? list.Count
            : -1;
    }

    /// <summary>
    /// Times <c>BlueprintState.CreateBlueprint</c> - a pure read of <c>Grid</c> state into a new
    /// <see cref="Blueprint"/>, safe to repeat over the same rectangle like <c>visualize</c> - over
    /// a rectangle of real *finished* buildings (<c>BuildingDef.Build</c>, the same call
    /// <c>FixtureBuilder.PlaceAll</c> uses for the regression fixture). Capture needs a
    /// <c>Constructable</c> or <c>Deconstructable</c> component; a finished building is the
    /// representative "capture an existing base" scenario (as opposed to placement's still-pending
    /// orders, which would also capture but aren't what a player is usually blueprinting).
    /// </summary>
    /// <summary>The largest rectangle of real finished tiles <see cref="RunCreateSweep"/> managed to
    /// build, for <see cref="RunUpdateVisualDenseSweep"/> to drag a blueprint across. Null if create
    /// was skipped for want of region.</summary>
    private static (int X0, int Y0, int N)? denseTileField;

    private static IEnumerator RunCreateSweep(PerfReport report, HarnessLog log, int x0, int y0, int availableRows)
    {
        var def = Assets.GetBuildingDef("Tile");
        var elements = new List<Tag> { ElementLoader.FindElementByHash(SimHashes.SandStone).tag };

        int rowCursor = 0;
        foreach (int n in UseCreateSizes)
        {
            int rows = RowsFor(n);
            if (rowCursor + rows > availableRows)
            {
                log.Line($"  SKIP create-N{n}: not enough dug region left ({availableRows - rowCursor} rows, need {rows})");
                continue;
            }

            int rectY0 = y0 + rowCursor;
            rowCursor += rows;

            int built = 0;
            for (int i = 0; i < n; i++)
            {
                int cell = Grid.XYToCell(x0 + i % PlacementRowWidth, rectY0 + i / PlacementRowWidth);
                var go = def.Build(cell, Orientation.Neutral, resource_storage: null, elements,
                    temperature: 293.15f, playsound: false, timeBuilt: 0f);
                if (go != null)
                    built++;
                if (i % 200 == 0)
                    yield return null; // spread a few thousand Instantiate calls across frames
            }
            log.Line($"create N={n}: built {built}/{n} finished buildings");
            // Sizes ascend, so the last one that fits is the biggest field available to drag over.
            if (built == n)
                denseTileField = (x0, rectY0, n);

            // CreateBlueprint's convention: topLeft = (minX, maxY), bottomRight = (maxX, minY) -
            // same as FixtureLayout.CaptureRect.
            var topLeft = new Vector2I(x0, rectY0 + rows - 1);
            var bottomRight = new Vector2I(x0 + PlacementRowWidth - 1, rectY0);

            // The N=100..2000 timings came back suspiciously flat - verify the capture actually
            // scaled with N rather than silently capturing a fixed subset.
            Blueprint? lastCaptured = null;
            yield return TimeOp(report, log, "create", n, warmup: CreateWarmup, iterations: CreateIterations,
                () => { lastCaptured = BlueprintState.CreateBlueprint(topLeft, bottomRight, filter: null); });
            log.Line($"  create-N{n}: captured {lastCaptured?.BuildingConfigurations.Count ?? -1} building(s) (expected {n})");
        }
    }

    /// <summary>
    /// The same per-frame drag as <see cref="RunUpdateVisualSweep"/>, but over a field of <b>real
    /// finished tiles</b> instead of empty dug-out space - reusing the rectangle
    /// <see cref="RunCreateSweep"/> just built, so it costs no extra region.
    ///
    /// Why it exists: every number behind the tile-renderer work in docs §7 came from a drag across
    /// empty space, where <c>RefreshCellInternal</c> is at its cheapest - it looks up
    /// <c>Grid.Objects[cell, layer]</c>, finds nothing, and skips the
    /// <c>KAnimGraphTileVisualizer.Refresh()</c> entirely. Over real tiles that component exists and
    /// actually runs, which is both the realistic case (players drag blueprints over their base, not
    /// over vacuum) and the case the refresh batching was supposed to help. §7 names this run as the
    /// one that should decide whether that batching earns its keep.
    ///
    /// Reported as <c>update-visual-tile-dense</c>, directly comparable with
    /// <c>update-visual-tile</c> at the same N in the same run.
    /// </summary>
    private static IEnumerator RunUpdateVisualDenseSweep(PerfReport report, HarnessLog log)
    {
        if (denseTileField is not var (x0, y0, n))
        {
            log.Line("update-visual-dense: no finished-tile field was built (create sweep skipped) - skipping");
            yield break;
        }

        var origin = new Vector2I(x0, y0);
        int solid = 0;
        for (int i = 0; i < n; i++)
        {
            int cell = Grid.XYToCell(x0 + i % PlacementRowWidth, y0 + i / PlacementRowWidth);
            if (Grid.IsValidCell(cell) && Grid.Objects[cell, (int)ObjectLayer.FoundationTile] != null)
                solid++;
        }
        // The whole point is that the cells underneath are occupied - if they are not, this sweep is
        // measuring the same thing as the sparse one and its comparison is meaningless.
        log.Line($"update-visual-dense [tile] N={n}: {solid}/{n} cells carry a finished tile");

        var bp = SyntheticBlueprint.Build(n, "Tile", $"PerfUpdateVisualDense{n}");
        BlueprintState.VisualizeBlueprint(origin, bp);
        log.Line($"  visuals: {VisualCounts()}");

        int step = 0;
        var cursor = origin;
        yield return TimeOp(report, log, "update-visual-tile-dense", n,
            warmup: UpdateVisualWarmup, iterations: UpdateVisualIterations,
            setup: () => cursor = new Vector2I(origin.x + (step++ % 2), origin.y),
            body: () => BlueprintState.UpdateVisual(BlueprintState.PlayerId_DefaultTilePreviews, cursor, forcingRedraw: false));

        BlueprintState.ClearVisuals();
    }

    private static int RowsFor(int n) => (n + PlacementRowWidth - 1) / PlacementRowWidth;


    // Correctness check for "use" - it reported plausible-looking timings even while every call
    // was silently failing (see DigRegion's Grid.Reveal comment), so count real output instead of
    // trusting the timer alone. Same FindObjectsByType approach as HarnessCases.Constructables().
    private static int CountConstructables() =>
        UnityEngine.Object.FindObjectsByType<Constructable>(FindObjectsSortMode.None).Length;

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

        // Digging clears terrain but does NOT reveal fog of war at a distance - only something
        // with vision (a Duplicant, a scanner) does that normally. This region is far from the
        // fixture colony's starting reveal radius, and both UseBlueprint's placement check
        // (BuildingVisual.ValidCell -> Grid.IsVisible) and CreateBlueprint's capture scan gate on
        // Grid.IsVisible - confirmed via diagnostic dump that it was false here, silently making
        // every use/create call in this region a no-op despite reporting a "successful" timing.
        // Force-reveal every cell so the sweep actually measures the placement/capture path
        // instead of its early-return fast path.
        foreach (int c in cells)
            Grid.Reveal(c, byte.MaxValue, forceReveal: true);
        log.Line($"  revealed {cells.Count} cells (Grid.Reveal, forceReveal=true)");
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
    /// target cell) rather than being safely repeatable in place.
    ///
    /// <paramref name="settleFrames"/> &gt; 0 additionally reports <c>{opName}-settled</c>: the
    /// same call measured through the following N rendered frames instead of stopping at its
    /// return. Needed for anything that builds Unity objects rather than plain data - a
    /// <c>KMonoBehaviour</c>'s <c>OnSpawn</c> (and with it a <c>KBatchedAnimController</c>'s first
    /// <c>Play</c>) runs from Unity's <c>Start</c>, i.e. *after* the call that instantiated it has
    /// already returned, so the synchronous number alone would credit that work to nobody. Compare
    /// against the <c>idle-frame</c> baseline op - the same frames with no work in them.</summary>
    internal static IEnumerator TimeOp(PerfReport report, HarnessLog log, string opName, int n,
        int warmup, int iterations, SysAction body, SysAction? setup = null, int settleFrames = 0)
    {
        for (int i = 0; i < warmup; i++)
        {
            setup?.Invoke();
            body();
            yield return null;
        }

        var timesMs = new List<double>(iterations);
        var settledMs = new List<double>(iterations);
        var allocSamples = new List<AllocSample>(iterations);
        for (int i = 0; i < iterations; i++)
        {
            setup?.Invoke();
            var alloc = AllocProbe.Begin();
            var sw = Stopwatch.StartNew();
            body();
            double syncMs = sw.Elapsed.TotalMilliseconds;
            // Closed with the synchronous timer, not after settleFrames: the settle frames run
            // arbitrary unrelated game work, whose allocation would otherwise be charged to this op.
            var sample = alloc.End();

            for (int f = 0; f < settleFrames; f++)
                yield return null;
            sw.Stop();

            timesMs.Add(syncMs);
            settledMs.Add(sw.Elapsed.TotalMilliseconds);
            allocSamples.Add(sample);
            if (settleFrames == 0)
                yield return null;
        }

        Record(report, log, opName, n, iterations, timesMs, AllocStats.From(allocSamples));
        if (settleFrames > 0)
            Record(report, log, opName + "-settled", n, iterations, settledMs);
    }

    /// <summary>Sorts <paramref name="timesMs"/>, then logs and files its median/p95 under
    /// <paramref name="opName"/>. Split out of <see cref="TimeOp"/> so a caller that has to drive
    /// its own iteration loop - <see cref="SelectionScreenPerf"/>'s cold opens need a teardown
    /// whose <c>Destroy</c>s only take effect a frame later - still reports identically.</summary>
    internal static void Record(PerfReport report, HarnessLog log, string opName, int n,
        int iterations, List<double> timesMs, AllocStats? alloc = null)
    {
        timesMs.Sort();
        double median = Percentile(timesMs, 0.5);
        double p95 = Percentile(timesMs, 0.95);

        string allocText = alloc == null ? "alloc=not sampled" : alloc.Describe();
        log.Line($"  {opName}-N{n}: median={median:F2}ms p95={p95:F2}ms {allocText}");
        report.Add(opName, n, iterations, median, p95, alloc);
    }

    internal static double Percentile(List<double> sortedAscending, double p)
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
