using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using BlueprintsV2.BlueprintData;
using BlueprintsV2.BlueprintData.NoteToolPlacedEntities;
using BlueprintsV2.Visualizers;
using UnityEngine;

namespace BlueprintsV2.Harness;

internal readonly struct HarnessCase
{
    public HarnessCase(string name, Func<IEnumerator> body)
    {
        Name = name;
        Body = body;
    }

    public string Name { get; }
    public Func<IEnumerator> Body { get; }
}

internal readonly struct CaseResult
{
    public CaseResult(string name, bool passed, string? message)
    {
        Name = name;
        Passed = passed;
        Message = message;
    }

    public string Name { get; }
    public bool Passed { get; }
    public string? Message { get; }
}

internal sealed class ResultSet
{
    private readonly List<CaseResult> results = new();

    public void Add(CaseResult r) => results.Add(r);
    public IReadOnlyList<CaseResult> Items => results;
    public int Count => results.Count;
    public int PassCount => results.Count(r => r.Passed);
}

/// <summary>
/// The assertion cases. Each exercises a slice of the blueprint pipeline that needs a live colony
/// - real <c>BuildingDef</c> resolution, <c>Grid</c> capture, actual placement - so it can't be a
/// plain <c>dotnet test</c>.
/// </summary>
internal static class HarnessCases
{
    /// <summary>Active Printing Pod cell, set by <see cref="HarnessRunner"/> before the cases run.</summary>
    public static int AnchorCell;

    /// <summary>PrefabIDs <see cref="FixtureBuilder"/> actually placed, set by the runner.</summary>
    public static IReadOnlyList<string> PlacedPrefabIds = System.Array.Empty<string>();

    /// <summary>Run log, set by <see cref="HarnessRunner"/>.</summary>
    public static HarnessLog? Log;

    public static IEnumerable<HarnessCase> All => new[]
    {
        new HarnessCase("capture-roundtrip-with-real-defs", CaptureRoundTrip),
        new HarnessCase("place-blueprint-creates-build-orders", PlaceBlueprint),
        new HarnessCase("blueprint-rotation-rotates-the-layout", RotationLayout),
        new HarnessCase("data-transfer-priority-round-trips", DataTransferPriority),
        new HarnessCase("element-note-capture-round-trips", NoteCaptureRoundTrip),
        new HarnessCase("instabuild-spawns-below-melting-point", InstabuildSpawnTemperature),
        new HarnessCase("note-visibility-toggle-hides-notes", NoteVisibilityToggle),
        new HarnessCase("planned-buildings-match-for-data-transfer", PlannedBuildingMatch),
        new HarnessCase("dig-placer-preview-filter-hides-digs", DigPlacerPreviewFilter),
        new HarnessCase("conduit-flags-ignore-captured-orientation", ConduitFlagsIgnoreCapturedOrientation),
        new HarnessCase("tile-seating-map-tracks-the-drag", TileSeatingMapTracksTheDrag),
        new HarnessCase("preview-follows-the-cursor", PreviewFollowsTheCursor),
    };

    // ---- capture + JSON round-trip ------------------------------------

    private static IEnumerator CaptureRoundTrip()
    {
        var (topLeft, bottomRight) = FixtureLayout.CaptureRect(AnchorCell);

        Blueprint captured = BlueprintState.CreateBlueprint(topLeft, bottomRight, filter: null);

        Assert.Equal(PlacedPrefabIds.Count, captured.BuildingConfigurations.Count, "captured building count");

        foreach (var bc in captured.BuildingConfigurations)
        {
            Assert.True(bc.BuildingDef != null, $"captured building '{bc.BuildingDefId}' resolved a BuildingDef");
            Assert.True(bc.SelectedElements.Count > 0, $"captured building '{bc.BuildingDefId}' has selected elements");
        }

        CollectionAssert.SameItems(
            PlacedPrefabIds,
            captured.BuildingConfigurations.Select(b => b.BuildingDef!.PrefabID),
            "captured prefab ids");

        foreach (var bc in captured.BuildingConfigurations)
            Log?.Line($"  captured {Describe(bc)}");

        var sb = new StringBuilder();
        using (var sw = new System.IO.StringWriter(sb))
            captured.WriteJsonString(sw);
        Blueprint restored = new Blueprint(sb);

        Assert.Equal(
            captured.BuildingConfigurations.Count,
            restored.BuildingConfigurations.Count,
            "building count survives round-trip");

        var before = captured.BuildingConfigurations.Select(Describe).OrderBy(s => s).ToList();
        var after = restored.BuildingConfigurations.Select(Describe).OrderBy(s => s).ToList();
        var diffs = before.Zip(after, (b, a) => (b, a)).Where(p => p.b != p.a).ToList();
        if (diffs.Count > 0)
            throw new HarnessAssertException(
                "round-trip changed building(s) (element = tag hash):\n" +
                string.Join("\n", diffs.Select(d => $"  before: {d.b}\n  after:  {d.a}")) +
                "\n  --- json ---\n" + sb);

        yield break;
    }

    // ---- place a captured blueprint ---------------------------------

    private static IEnumerator PlaceBlueprint()
    {
        // The three tile/ladder pieces (one row). ManualGenerator is a floored 2x2 that needs
        // its own supporting terrain - out of scope for "are build orders created".
        Blueprint bp = Snapshot(TileRowTopLeft(), TileRowBottomRight());
        int expected = bp.BuildingConfigurations.Count(b => !b.BuildingDisabled);
        Assert.Equal(3, expected, "row blueprint has 3 buildings");

        var result = new PlacementResult();
        var target = new Vector2I(Grid.CellToXY(AnchorCell).x - 8, Grid.CellToXY(AnchorCell).y - 2);
        yield return PlaceAt(bp, target, rotateSteps: 0, result);

        Log?.Line($"  {result.Orders.Count} build order(s): " +
                  string.Join(", ", result.Orders.Select(o => $"{o.id}@{o.cell}")));

        CollectionAssert.SameItems(
            bp.BuildingConfigurations.Where(b => !b.BuildingDisabled).Select(b => b.BuildingDef!.PrefabID),
            result.Orders.Select(o => o.id),
            "build orders match the blueprint's buildings");
        Assert.True(result.OutsideRegion.Count == 0,
            "all build orders inside the target area (strays: " + string.Join(", ", result.OutsideRegion) + ")");
    }

    // ---- rotating the blueprint rotates the placed layout ----------

    private static IEnumerator RotationLayout()
    {
        Blueprint bp = Snapshot(TileRowTopLeft(), TileRowBottomRight());
        Assert.Equal(3, bp.BuildingConfigurations.Count, "row blueprint has 3 buildings");

        var xy = Grid.CellToXY(AnchorCell);
        var neutral = new PlacementResult();
        yield return PlaceAt(bp, new Vector2I(xy.x - 9, xy.y - 2), rotateSteps: 0, neutral);
        var rotated = new PlacementResult();
        yield return PlaceAt(bp, new Vector2I(xy.x - 3, xy.y - 4), rotateSteps: 1, rotated);

        Log?.Line("  neutral cells: " + Layout(neutral));
        Log?.Line("  R90 cells:     " + Layout(rotated));

        // One tile may fail on tight terrain; the surviving orders still show the shape flip.
        Assert.True(neutral.Orders.Count >= 2, $"neutral placement build orders ({Layout(neutral)})");
        Assert.True(rotated.Orders.Count >= 2, $"rotated placement build orders ({Layout(rotated)})");

        Assert.True(Spread(neutral, o => o.cell.x) >= 2 && Spread(neutral, o => o.cell.y) <= 1,
            $"neutral layout is a horizontal row ({Layout(neutral)})");
        Assert.True(Spread(rotated, o => o.cell.y) >= 2 && Spread(rotated, o => o.cell.x) <= 1,
            $"R90 layout is a vertical column ({Layout(rotated)})");
    }

    private static int Spread(PlacementResult r, Func<(string id, Vector2I cell), int> sel)
    {
        var vals = r.Orders.Select(sel).ToList();
        return vals.Count == 0 ? 0 : vals.Max() - vals.Min();
    }

    private static string Layout(PlacementResult r) =>
        string.Join(",", r.Orders.Select(o => o.cell));

    // ---- data transfer: a non-default priority survives capture + round-trip ----

    private static IEnumerator DataTransferPriority()
    {
        var generator = Components.BuildingCompletes.Items
            .FirstOrDefault(b => b != null && b.Def != null && b.Def.PrefabID == "ManualGenerator");
        if (generator == null || !generator.TryGetComponent<Prioritizable>(out var prio))
        {
            Assert.True(false, "no ManualGenerator with a Prioritizable in the fixture");
            yield break;
        }

        var original = prio.GetMasterPriority();
        var custom = new PrioritySetting(PriorityScreen.PriorityClass.high, 8);
        prio.SetMasterPriority(custom);
        yield return null;

        try
        {
            var (tl, br) = FixtureLayout.CaptureRect(AnchorCell);
            Blueprint bp = BlueprintState.CreateBlueprint(tl, br, filter: null);
            var bc = bp.BuildingConfigurations.FirstOrDefault(b => b.BuildingDef?.PrefabID == "ManualGenerator");
            Assert.True(bc != null, "captured the ManualGenerator");
            Assert.True(bc!.TryGetDataValue("Prioritizable", out var data), "ManualGenerator carries Prioritizable data");
            string dataJson = data!.ToString();
            Log?.Line("  captured priority data: " + dataJson.Replace("\n", " "));
            Assert.True(dataJson.Contains("priority_value") && dataJson.Contains("8"),
                "captured priority data holds the non-default value 8: " + dataJson);

            var sb = new StringBuilder();
            using (var sw = new System.IO.StringWriter(sb))
                bp.WriteJsonString(sw);
            Blueprint restored = new Blueprint(sb);
            var rbc = restored.BuildingConfigurations.FirstOrDefault(b => b.BuildingDef?.PrefabID == "ManualGenerator");
            Assert.True(rbc != null, "round-tripped the ManualGenerator");
            Assert.True(rbc!.TryGetDataValue("Prioritizable", out var rdata), "priority data survives round-trip");
            Assert.Equal(dataJson, rdata!.ToString(), "priority data is byte-identical after round-trip");
        }
        finally
        {
            prio.SetMasterPriority(original);
        }
    }

    // ---- note capture + round-trip -------------------------------

    private const string CollectNotesFilterKey = "BLUEPRINTV2_COLLECT_NOTES"; // BlueprintCreationFilterKeys.Collect_Notes_ID (internal)
    private const int BlueprintNotesLayer = (int)ObjectLayer.FillPlacer;      // ModAssets.BlueprintNotesLayer (internal)

    private static IEnumerator NoteCaptureRoundTrip()
    {
        var xy = Grid.CellToXY(AnchorCell);
        int noteCell = Grid.XYToCell(xy.x - 6, xy.y - 3);
        if (Grid.IsSolidCell(noteCell))
        {
            SimMessages.Dig(noteCell, skipEvent: true);
            for (int i = 0; i < 30 && Grid.IsSolidCell(noteCell); i++) yield return null;
        }

        var note = BlueprintsV2.BlueprintData.NoteToolPlacedEntities.ElementNote.Create(
            noteCell, SimHashes.Oxygen, amount: 100f, temperature: 296f);
        Assert.True(note != null, "created an ElementNote entity");
        for (int i = 0; i < 5; i++) yield return null;

        var menu = Tools.MultiToolParameterMenu.Instance;
        Assert.True(menu != null, "MultiToolParameterMenu.Instance exists");
        var paramsField = typeof(Tools.MultiToolParameterMenu)
            .GetField("parameters", BindingFlags.NonPublic | BindingFlags.Instance)!;
        object? savedParams = paramsField.GetValue(menu);
        paramsField.SetValue(menu, new Dictionary<string, ToolParameterMenu.ToggleState>
        {
            { CollectNotesFilterKey, ToolParameterMenu.ToggleState.On },
        });

        try
        {
            var tl = new Vector2I(xy.x - 9, xy.y - 3);
            var br = new Vector2I(xy.x - 1, xy.y - 3);
            Blueprint bp = BlueprintState.CreateBlueprint(tl, br, Tools.MultiToolParameterMenu.Instance);

            Assert.True(bp.WorldNotes.Count >= 1, $"note captured into WorldNotes ({bp.WorldNotes.Count})");
            var captured = bp.WorldNotes.Values.First();
            Assert.Equal(BlueprintNoteData.NoteType.Element, captured.Type, "captured note type");
            Assert.Equal(SimHashes.Oxygen, captured.ElementId, "captured note element");
            Log?.Line($"  captured note: {captured.Type} {captured.ElementId} mass={captured.ElementMass} temp={captured.ElementTemperature}");

            var sb = new StringBuilder();
            using (var sw = new System.IO.StringWriter(sb))
                bp.WriteJsonString(sw);
            Blueprint restored = new Blueprint(sb);

            Assert.Equal(bp.WorldNotes.Count, restored.WorldNotes.Count, "world-note count survives round-trip");
            var rnote = restored.WorldNotes.Values.First();
            Assert.Equal(BlueprintNoteData.NoteType.Element, rnote.Type, "round-tripped note type");
            Assert.Equal(SimHashes.Oxygen, rnote.ElementId, "round-tripped note element");
            Assert.Equal(captured.ElementTemperature, rnote.ElementTemperature, "round-tripped note temperature");
        }
        finally
        {
            paramsField.SetValue(menu, savedParams);
            BlueprintsV2.BlueprintData.NoteToolPlacedEntities.BlueprintNote.ClearExistingNote(noteCell);
        }
    }

    // ---- instabuild spawn temperature ----------------------------

    /// <summary>
    /// Instabuilt buildings must not materialise at their material's melting point.
    ///
    /// <c>CreateFinishedBuildingInternal</c> passes a spawn temperature to
    /// <c>BuildingDef.Create</c>. Passing <c>ElementLoader.GetMinMeltingPointAmongElements</c>
    /// raw - as it did before - spawns the building exactly at melting point, hot enough to
    /// damage itself and to dump that heat into the surrounding cells. Asserted as the
    /// behaviour rather than the formula: strictly below melting point, and no hotter than the
    /// def's own temperature.
    /// </summary>
    private static IEnumerator InstabuildSpawnTemperature()
    {
        // No exact building count here: this case runs after the placement cases, whose build
        // orders land inside the tile row's capture rectangle, so the row picks up extras.
        // The temperature assertions below are what matter and they hold per building.
        Blueprint bp = Snapshot(TileRowTopLeft(), TileRowBottomRight());
        Assert.True(bp.BuildingConfigurations.Count >= 3,
            $"row blueprint captured buildings ({bp.BuildingConfigurations.Count})");

        var xy = Grid.CellToXY(AnchorCell);
        var result = new PlacementResult();

        bool savedInstantBuild = DebugHandler.InstantBuildMode;
        DebugHandler.InstantBuildMode = true;
        try
        {
            yield return PlaceAt(bp, new Vector2I(xy.x - 8, xy.y - 9), rotateSteps: 0, result);
        }
        finally
        {
            DebugHandler.InstantBuildMode = savedInstantBuild;
        }

        Assert.True(result.Orders.Count == 0,
            "instabuild produces finished buildings, not build orders (got: " + Layout(result) + ")");
        Assert.True(result.Finished.Count >= 2,
            $"instabuild placed finished buildings ({result.Finished.Count}: " +
            string.Join(", ", result.Finished.Select(f => f.id)) + ")");

        foreach (var f in result.Finished)
        {
            Log?.Line($"  {f.id}@{f.cell} spawned at {f.temperature:F1}K " +
                      $"(def {f.defTemperature:F1}K, min melting point {f.minMeltingPoint:F1}K)");

            Assert.True(f.temperature < f.minMeltingPoint - 1f,
                $"{f.id} spawned at {f.temperature:F1}K, below its {f.minMeltingPoint:F1}K melting point");
            Assert.True(f.temperature <= f.defTemperature + 0.5f,
                $"{f.id} spawned at {f.temperature:F1}K, no hotter than its def temperature {f.defTemperature:F1}K");
        }
    }

    // ---- preview follows the cursor ------------------------------

    /// <summary>
    /// A dragged preview's anim-backed visuals must actually be where the cursor is.
    ///
    /// This exists because the per-frame path no longer writes each visual's transform on a plain
    /// cursor move: every anim-backed visualizer is parented to one root per player and the root is
    /// translated once (docs §7). A <c>KBatchedAnimController</c> caches its position for the anim
    /// batch, so the open question that no assertion elsewhere can reach is whether it notices a
    /// <i>parent</i> move at all - if it does not, the previews render at stale positions while the
    /// blueprint appears to move, and every other case still passes.
    ///
    /// Asserted on the world position of the visualizer itself (which is what the batch renders
    /// from), not on the cell field - the cell updates either way, so it would pass even when the
    /// art is wrong. The screenshots either side are for the human half: whether it *looks* right.
    /// </summary>
    private static IEnumerator PreviewFollowsTheCursor()
    {
        var xy = Grid.CellToXY(AnchorCell);
        var start = new Vector2I(xy.x - 10, xy.y - 3);

        // Ladder: 1x1, raw mineral, and its preview carries a KBatchedAnimController - so unlike a
        // Tile it keeps its own clone, is parented to the root, and is rendered from its transform.
        var def = Assets.GetBuildingDef("Ladder");
        var sandstone = ElementLoader.FindElementByHash(SimHashes.SandStone).tag;
        var bp = new Blueprint("preview-follows", "");
        for (int dy = 0; dy < 3; dy++)
        {
            var bc = new BuildingConfig
            {
                Offset = new Vector2I(0, dy),
                BuildingDef = def,
                BuildingDefId = "Ladder",
                Orientation = Orientation.Neutral,
            };
            bc.SelectedElements.Add(sandstone);
            bp.BuildingConfigurations.Add(bc);
        }
        bp.CacheCost();

        BlueprintState.VisualizeBlueprint(start, bp);
        for (int i = 0; i < 3; i++) yield return null;

        var before = VisualizerWorldPositions();
        Assert.True(before.Count >= 3, $"the ladder preview produced visualizers ({before.Count})");
        yield return Screenshot.Capture("preview-at-start", Log);

        const int DragCells = 6;
        var moved = new Vector2I(start.x + DragCells, start.y);
        BlueprintState.UpdateVisual(BlueprintState.PlayerId_DefaultTilePreviews, moved, forcingRedraw: false);
        for (int i = 0; i < 3; i++) yield return null;

        var after = VisualizerWorldPositions();
        yield return Screenshot.Capture("preview-after-drag", Log);

        Assert.True(after.Count == before.Count,
            $"the same visualizers exist after the drag ({before.Count} -> {after.Count})");

        int followed = 0, stale = 0;
        for (int i = 0; i < before.Count; i++)
        {
            float dx = after[i].x - before[i].x;
            if (Mathf.Abs(dx - DragCells) < 0.01f)
                followed++;
            else
                stale++;
            Log?.Line($"  visualizer {i}: x {before[i].x:F2} -> {after[i].x:F2} (moved {dx:F2}, expected {DragCells})");
        }

        Assert.True(stale == 0,
            $"every anim-backed visualizer moved with the cursor ({followed} did, {stale} did not - " +
            "a stale one means the shared parent move is not reaching the KBatchedAnimController)");
    }

    /// <summary>World positions of the live preview visualizers, in list order - read from the
    /// transform the anim batch renders from.</summary>
    private static List<Vector3> VisualizerWorldPositions()
    {
        var positions = new List<Vector3>();
        foreach (var visual in LiveVisuals())
            if (visual is BuildingVisual bv && bv.Visualizer != null)
                positions.Add(bv.Visualizer.transform.GetPosition());
        return positions;
    }

    /// <summary>The foundation + dependent visual lists, which are private statics on
    /// BlueprintState - same reflection PerfRunner uses to report their split.</summary>
    private static IEnumerable<IVisual> LiveVisuals()
    {
        foreach (string fieldName in new[] { "FoundationVisuals", "DependentVisuals" })
        {
            var field = typeof(BlueprintState).GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Static);
            if (field?.GetValue(null) is not IDictionary byPlayer)
                continue;
            if (byPlayer[BlueprintState.PlayerId_DefaultTilePreviews] is not IEnumerable list)
                continue;
            foreach (var item in list)
                if (item is IVisual visual)
                    yield return visual;
        }
    }

    // ---- tile seating map ----------------------------------------

    /// <summary>
    /// The tile renderer's seating map (<c>TileVisual.ActiveTileVisuals</c>, read through
    /// <c>HasTileAt</c>) must hold exactly the blueprint's footprint after a drag, and nothing after
    /// the blueprint is put down.
    ///
    /// This exists because the per-frame path no longer unseats and re-seats every tile on every
    /// cursor move: it records which cells changed and reconciles them against the renderer when the
    /// update flushes (docs §7). That turns O(N) renderer work into O(perimeter), and its failure
    /// mode is a map that drifts out of step with the blueprint - a tile left seated at a cell the
    /// blueprint has moved off, or a cell never seated because a neighbour got there first.
    ///
    /// The map is the assertable half. The tile <i>art</i> - whether connection bits and seams look
    /// right while dragging - is not: it is a human judgement, and it belongs to the smoke-test
    /// checklist. What this case guarantees is that the data the art is computed from is correct.
    /// </summary>
    private static IEnumerator TileSeatingMapTracksTheDrag()
    {
        var xy = Grid.CellToXY(AnchorCell);
        var start = new Vector2I(xy.x - 6, xy.y - 12);

        // Pin the anchor to BottomLeft so "footprint" means origin + offset, with no half-width
        // shift to model here. The default (BottomCenter) shifts a 3-wide blueprint one cell in x,
        // which is what the first run of this case tripped over: all nine tiles were seated, one
        // column from where it looked. _state has no public setter and RefreshAnchorState recomputes
        // the shift from it on every CheckPermittedRotations, so the field is the only thing worth
        // setting - same reflection PerfRunner uses for the placement sweep.
        var st = BlueprintState.CurrentStateInfo(BlueprintState.PlayerId_DefaultTilePreviews);
        var stateField = typeof(BlueprintState.BlueprintTransformationInfo)
            .GetField("_state", BindingFlags.NonPublic | BindingFlags.Instance);
        object? savedState = stateField?.GetValue(st);
        stateField?.SetValue(st, BlueprintAnchorState.BottomLeft);
        if (stateField == null)
            Log?.Line("  _state field not found - footprint assertions may be off by the anchor shift");
        try
        {

        // A small solid block of tiles: an interior cell (the one the reconciliation elides) only
        // exists at 3x3 or bigger.
        const int W = 3, H = 3;
        var def = Assets.GetBuildingDef("Tile");
        var sandstone = ElementLoader.FindElementByHash(SimHashes.SandStone).tag;

        var bp = new Blueprint("seating-map", "");
        for (int dx = 0; dx < W; dx++)
            for (int dy = 0; dy < H; dy++)
            {
                var bc = new BuildingConfig
                {
                    Offset = new Vector2I(dx, dy),
                    BuildingDef = def,
                    BuildingDefId = "Tile",
                    Orientation = Orientation.Neutral,
                };
                bc.SelectedElements.Add(sandstone);
                bp.BuildingConfigurations.Add(bc);
            }
        bp.CacheCost();

        BlueprintState.VisualizeBlueprint(start, bp);
        yield return null;

        AssertFootprintSeated(start, W, H, def, "after the initial draw");

        // Drag it: one cell at a time (the case the reconciliation optimises), then a jump bigger
        // than the footprint so old and new share no cells at all.
        foreach (var origin in new[]
                 {
                     new Vector2I(start.x + 1, start.y),
                     new Vector2I(start.x + 2, start.y),
                     new Vector2I(start.x + 2, start.y - 1),
                     new Vector2I(start.x + 12, start.y - 6),
                 })
        {
            BlueprintState.UpdateVisual(BlueprintState.PlayerId_DefaultTilePreviews, origin, forcingRedraw: false);
            yield return null;
            AssertFootprintSeated(origin, W, H, def, $"after dragging to {origin.x},{origin.y}");
        }

        var last = new Vector2I(start.x + 12, start.y - 6);
        BlueprintState.ClearVisuals();
        yield return null;

        int stillSeated = 0;
        for (int dx = -1; dx <= W; dx++)
            for (int dy = -1; dy <= H; dy++)
                if (TileVisual.HasTileAt(BlueprintState.PlayerId_DefaultTilePreviews,
                        Grid.XYToCell(last.x + dx, last.y + dy), out _))
                    stillSeated++;
        Assert.True(stillSeated == 0, $"no tiles left seated after ClearVisuals (found {stillSeated})");
        Log?.Line("  seating map empty after ClearVisuals");
        }
        finally
        {
            if (stateField != null) stateField.SetValue(st, savedState);
        }
    }

    /// <summary>Every cell of the footprint carries the blueprint's def, and the ring just outside
    /// it carries nothing - the second half is what catches a tile left behind by a drag.</summary>
    private static void AssertFootprintSeated(Vector2I origin, int w, int h, BuildingDef def, string when)
    {
        ulong player = BlueprintState.PlayerId_DefaultTilePreviews;
        int seated = 0, wrongDef = 0, strays = 0;

        for (int dx = 0; dx < w; dx++)
            for (int dy = 0; dy < h; dy++)
            {
                if (!TileVisual.HasTileAt(player, Grid.XYToCell(origin.x + dx, origin.y + dy), out var seatedDef))
                    continue;
                seated++;
                if (seatedDef != def)
                    wrongDef++;
            }

        for (int dx = -1; dx <= w; dx++)
            for (int dy = -1; dy <= h; dy++)
            {
                if (dx >= 0 && dx < w && dy >= 0 && dy < h)
                    continue;
                if (TileVisual.HasTileAt(player, Grid.XYToCell(origin.x + dx, origin.y + dy), out _))
                    strays++;
            }

        Log?.Line($"  {when}: {seated}/{w * h} seated, {wrongDef} wrong def, {strays} stray neighbour(s)");
        Assert.True(seated == w * h, $"{when}: every footprint cell is seated ({seated}/{w * h})");
        Assert.True(wrongDef == 0, $"{when}: every seated cell carries the blueprint's def ({wrongDef} did not)");
        Assert.True(strays == 0, $"{when}: no tile left seated outside the footprint ({strays} found)");
    }

    // ---- note visibility toggle ----------------------------------

    /// <summary>
    /// The state and event half of the note-visibility toggle: flipping it must actually disable
    /// a seated note's renderer, and a deleted note must not be left subscribed to the static
    /// event.
    ///
    /// Also captures the HUD either side of the toggle. The top-left button itself cannot be
    /// asserted on - whether it is in the right place and reads clearly is a human judgement -
    /// so the frames are the evidence for that half.
    /// </summary>
    private static IEnumerator NoteVisibilityToggle()
    {
        var xy = Grid.CellToXY(AnchorCell);
        int noteCell = Grid.XYToCell(xy.x - 4, xy.y - 3);
        if (Grid.IsSolidCell(noteCell))
        {
            SimMessages.Dig(noteCell, skipEvent: true);
            for (int i = 0; i < 30 && Grid.IsSolidCell(noteCell); i++) yield return null;
        }

        Assert.True(BlueprintState.NoteVisibility, "notes start visible");

        ///seat: true is what the player-facing path does (CreateNoteTool, and the multiplayer
        ///packet). Only seated notes own a persistent rendered mesh and subscribe to the toggle;
        ///unseated ones are the transient preview visuals, which deliberately ignore it. Creating
        ///an unseated note here made this case fail against correct code.
        var note = BlueprintsV2.BlueprintData.NoteToolPlacedEntities.ElementNote.Create(
            noteCell, SimHashes.Oxygen, amount: 100f, temperature: 296f, seat: true);
        Assert.True(note != null, "created a seated ElementNote to toggle");
        for (int i = 0; i < 5; i++) yield return null;

        var renderer = note!.GetComponentInChildren<MeshRenderer>();
        Assert.True(renderer != null, "the note has a MeshRenderer");
        Assert.True(renderer!.enabled, "note renderer starts enabled");

        yield return Screenshot.Capture("notes-visible", Log);

        try
        {
            BlueprintState.ToggleNoteVisibility();
            for (int i = 0; i < 3; i++) yield return null;
            Assert.True(!BlueprintState.NoteVisibility, "toggle flipped the state off");
            Assert.True(!renderer.enabled, "toggling off disabled the note's renderer");
            yield return Screenshot.Capture("notes-hidden", Log);

            BlueprintState.ToggleNoteVisibility();
            for (int i = 0; i < 3; i++) yield return null;
            Assert.True(BlueprintState.NoteVisibility, "toggle flipped the state back on");
            Assert.True(renderer.enabled, "toggling on re-enabled the note's renderer");

            ///the event is static, so a note that failed to unsubscribe in OnCleanUp would be
            ///invoked after destruction. Delete it, then toggle twice more: a leak surfaces as a
            ///MissingReferenceException, which exception-sweep would also catch.
            BlueprintsV2.BlueprintData.NoteToolPlacedEntities.BlueprintNote.ClearExistingNote(noteCell);
            for (int i = 0; i < 5; i++) yield return null;
            BlueprintState.ToggleNoteVisibility();
            yield return null;
            BlueprintState.ToggleNoteVisibility();
            for (int i = 0; i < 3; i++) yield return null;
            Log?.Line("  toggled twice after deleting the note - no exception, so OnCleanUp unsubscribed");
        }
        finally
        {
            ///never leave the colony with notes hidden for later cases
            if (!BlueprintState.NoteVisibility)
                BlueprintState.ToggleNoteVisibility();
        }

        Assert.True(BlueprintState.NoteVisibility, "restored note visibility for later cases");
    }

    // ---- planned buildings match for data transfer ----------------

    /// <summary>
    /// The contract widened for planned-building data transfer:
    /// <c>BuildingVisual.SameBuildingAlreadyFinishedInPlace</c> must match a building that is only
    /// queued when <c>includePlanned</c> is set, and must not when it isn't.
    ///
    /// Asserted directly on the predicate rather than by driving a whole placement, because that
    /// predicate *is* the change - five call sites choose their flag from it, and each of those
    /// choices is a separate judgement that a single end-to-end run would not distinguish.
    /// </summary>
    private static IEnumerator PlannedBuildingMatch()
    {
        Blueprint bp = Snapshot(TileRowTopLeft(), TileRowBottomRight());
        var xy = Grid.CellToXY(AnchorCell);
        var planned = new PlacementResult();

        ///instabuild off: we want Constructables (queued), not finished buildings
        bool savedInstant = DebugHandler.InstantBuildMode;
        DebugHandler.InstantBuildMode = false;
        try
        {
            yield return PlaceAt(bp, new Vector2I(xy.x - 8, xy.y - 12), rotateSteps: 0, planned);
        }
        finally
        {
            DebugHandler.InstantBuildMode = savedInstant;
        }

        Assert.True(planned.Orders.Count >= 1,
            $"placement queued at least one building ({planned.Orders.Count} orders, {planned.Finished.Count} finished)");
        Assert.True(planned.Finished.Count == 0, "nothing was instabuilt, so these are genuinely planned");

        var order = planned.Orders[0];
        int cell = Grid.XYToCell(order.cell.x, order.cell.y);
        var config = bp.BuildingConfigurations.FirstOrDefault(b => b.BuildingDef?.PrefabID == order.id);
        Assert.True(config != null, $"found the blueprint config for the queued {order.id}");

        var visual = new BuildingVisual(config!, cell, BlueprintState.PlayerId_DefaultTilePreviews);
        try
        {
            bool withPlanned = visual.SameBuildingAlreadyFinishedInPlace(cell, out var match, false, includePlanned: true);
            Assert.True(withPlanned, $"includePlanned: true matches the queued {order.id} at {order.cell}");
            Assert.True(match != null, "the match is non-null, per [NotNullWhen(true)]");
            Assert.True(match is not BuildingComplete, "the match really is a planned building, not a finished one");

            bool withoutPlanned = visual.SameBuildingAlreadyFinishedInPlace(cell, out _, false, includePlanned: false);
            Assert.True(!withoutPlanned, "includePlanned: false does NOT match a queued building");

            Log?.Line($"  queued {order.id}@{order.cell}: includePlanned true={withPlanned} false={withoutPlanned}");

            ///and a finished building must still match either way - the fixture has real ones
            var built = Components.BuildingCompletes.Items
                .FirstOrDefault(b => b != null && b.Def != null && b.Def.PrefabID == "Tile");
            if (built != null)
            {
                int builtCell = Grid.PosToCell(built.gameObject);
                var builtConfig = bp.BuildingConfigurations.FirstOrDefault(b => b.BuildingDef?.PrefabID == "Tile");
                if (builtConfig != null)
                {
                    var builtVisual = new BuildingVisual(builtConfig, builtCell, BlueprintState.PlayerId_DefaultTilePreviews);
                    try
                    {
                        Assert.True(builtVisual.SameBuildingAlreadyFinishedInPlace(builtCell, out _, false, includePlanned: false),
                            "a finished building still matches with includePlanned: false");
                        Assert.True(builtVisual.SameBuildingAlreadyFinishedInPlace(builtCell, out _, false, includePlanned: true),
                            "a finished building also matches with includePlanned: true");
                    }
                    finally
                    {
                        builtVisual.DestroyVisualizer();
                    }
                }
            }
        }
        finally
        {
            visual.DestroyVisualizer();
        }
    }

    // ---- dig-placer preview filter --------------------------------

    private const string NonSolidDigFilterKey = "BLUEPRINTV2_PRESERVEAIRTILES"; // BlueprintCreationFilterKeys (internal)

    /// <summary>
    /// The dig-placer preview filter: with <c>DIGPLACER</c> blocked, visualizing a blueprint that
    /// carries dig locations must create no dig visuals; unblocked, it must create them.
    ///
    /// Counted by scene objects named <c>BlueprintModDigVisualizer</c> - the name
    /// <see cref="DigVisual"/> gives its clone - since the visual list itself is private.
    /// </summary>
    private static IEnumerator DigPlacerPreviewFilter()
    {
        var xy = Grid.CellToXY(AnchorCell);

        ///capture with the non-solid-cells filter on, which is what puts entries in DigLocations
        var menu = Tools.MultiToolParameterMenu.Instance;
        Assert.True(menu != null, "MultiToolParameterMenu.Instance exists");
        var paramsField = typeof(Tools.MultiToolParameterMenu)
            .GetField("parameters", BindingFlags.NonPublic | BindingFlags.Instance)!;
        object? savedParams = paramsField.GetValue(menu);
        paramsField.SetValue(menu, new Dictionary<string, ToolParameterMenu.ToggleState>
        {
            { NonSolidDigFilterKey, ToolParameterMenu.ToggleState.On },
        });

        ///Dig a dedicated patch rather than reusing ground the placement cases touched: those
        ///leave build orders behind, and a queued building makes the cell non-empty, which is
        ///exactly what the capture condition rejects. A cell must be non-solid AND empty.
        var digTl = new Vector2I(xy.x + 4, xy.y - 2);
        var digBr = new Vector2I(xy.x + 7, xy.y - 3);
        var patch = new List<int>();
        for (int x = digTl.x; x <= digBr.x; x++)
            for (int y = digBr.y; y <= digTl.y; y++)
            {
                int c = Grid.XYToCell(x, y);
                if (Grid.IsValidCell(c))
                    patch.Add(c);
            }
        foreach (int c in patch)
            if (Grid.IsSolidCell(c))
                SimMessages.Dig(c, skipEvent: true);
        float digWaited = 0f;
        while (patch.Any(Grid.IsSolidCell) && digWaited < 20f)
        {
            digWaited += Time.unscaledDeltaTime;
            yield return null;
        }
        Log?.Line($"  dug a {patch.Count}-cell patch; still solid: {patch.Count(Grid.IsSolidCell)}");

        Blueprint bp;
        try
        {
            ///createsSnapshot: true matters. A non-snapshot capture that came out with ONLY dig
            ///locations - no buildings or notes - has them deliberately cleared
            ///("clear to not spam quasi empty blueprints", BlueprintState.cs), and a bare dug
            ///patch is exactly that case.
            bp = BlueprintState.CreateBlueprint(digTl, digBr, Tools.MultiToolParameterMenu.Instance, createsSnapshot: true);
            bp.SetRandomSnapshotId();
        }
        finally
        {
            paramsField.SetValue(menu, savedParams);
        }

        Log?.Line($"  captured {bp.DigLocations.Count} dig location(s)");
        Assert.True(bp.DigLocations.Count > 0,
            "captured at least one dig location (needs non-solid empty cells in the rect)");

        var stateInfo = BlueprintState.CurrentStateInfo();
        var blocked = stateInfo.BlockedPlacementFilterLayers;
        bool wasBlocked = blocked.Contains(ToolParameterMenu.FILTERLAYERS.DIGPLACER);
        var target = new Vector2I(xy.x + 4, xy.y - 2);

        try
        {
            blocked.Add(ToolParameterMenu.FILTERLAYERS.DIGPLACER);
            BlueprintState.VisualizeBlueprint(target, bp);
            for (int i = 0; i < 5; i++) yield return null;
            int hidden = CountDigVisualizers();
            BlueprintState.ClearVisuals();
            for (int i = 0; i < 3; i++) yield return null;

            blocked.Remove(ToolParameterMenu.FILTERLAYERS.DIGPLACER);
            BlueprintState.VisualizeBlueprint(target, bp);
            for (int i = 0; i < 5; i++) yield return null;
            int shown = CountDigVisualizers();
            BlueprintState.ClearVisuals();
            for (int i = 0; i < 3; i++) yield return null;

            Log?.Line($"  dig visualizers: filtered={hidden} unfiltered={shown}");
            Assert.Equal(0, hidden, "no dig visuals created while DIGPLACER is filtered out");
            Assert.True(shown > 0, $"dig visuals created when the filter is off ({shown})");
        }
        finally
        {
            if (wasBlocked)
                blocked.Add(ToolParameterMenu.FILTERLAYERS.DIGPLACER);
            else
                blocked.Remove(ToolParameterMenu.FILTERLAYERS.DIGPLACER);
            BlueprintState.ClearVisuals();
        }
    }

    /// <summary>
    /// Counts dig visualizers including INACTIVE ones. <see cref="DigVisual"/> calls
    /// <c>SetActive(IsPlaceable(cell))</c>, and IsPlaceable requires the cell to be solid - you
    /// dig solid ground - so a visual over an already-dug cell is created but switched off. The
    /// preview filter controls whether the object is created at all, which is what this measures.
    /// </summary>
    private static int CountDigVisualizers() =>
        UnityEngine.Object.FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None)
            .Count(tr => tr != null && tr.name == "BlueprintModDigVisualizer");

    // ---- conduit rotation (issue #4) ------------------------------

    /// <summary>
    /// Issue #4: <c>BuildingVisual.GetRotatedUtilityConnectionFlags</c> used to shift the stored
    /// <c>UtilityConnections</c> mask by <c>capturedOrientation - blueprintRotation</c>. The stored
    /// flags are world-space (captured per grid cell), so the captured building's own facing must
    /// not enter it — an unrotated blueprint has to hand the mask back untouched.
    ///
    /// <para>This does two things a unit test can't. First it establishes <b>reachability</b>: the
    /// bad term only bites when a building carrying conduit flags is captured non-Neutral, and it
    /// is not obvious from the code that any such building exists — <c>VisualizerType.UTILITY</c>
    /// requires <c>IsTilePiece</c>, and tile pieces are not rotatable. The def sweep below settles
    /// that against the real catalogue and logs it either way.</para>
    ///
    /// <para>Second, when a candidate does exist it drives the whole pipeline: a synthetic blueprint
    /// carrying that def at R90 with known flags, placed unrotated, then reads the connections back
    /// off the object placement actually produced. That is the end-to-end claim the PR rests on.</para>
    /// </summary>
    private static IEnumerator ConduitFlagsIgnoreCapturedOrientation()
    {
        ///Reachability sweep: which utility-flag-carrying defs can be rotated at all?
        var utilityDefs = Assets.BuildingDefs
            .Where(d => d?.BuildingComplete != null
                        && d.BuildingComplete.GetComponent<IHaveUtilityNetworkMgr>() != null)
            .ToList();

        var rotatable = utilityDefs
            .Where(d => d.PermittedRotations != PermittedRotations.Unrotatable)
            .ToList();

        Log?.Line($"  utility defs (IHaveUtilityNetworkMgr): {utilityDefs.Count}");
        Log?.Line($"  of those, rotatable: {rotatable.Count}");
        foreach (var d in rotatable.Take(15))
            Log?.Line($"    {d.PrefabID}: rotations={d.PermittedRotations} tilePiece={d.IsTilePiece} " +
                      $"kAnimTile={d.isKAnimTile}");

        if (rotatable.Count == 0)
        {
            ///Not a failure: it means the removed term was dead code rather than a live bug, which
            ///is exactly what this run exists to find out. Reported so the PR can say so honestly.
            Log?.Line("  REACHABILITY: no rotatable utility def exists — the captured-orientation " +
                      "term could never fire in a real colony.");
            yield break;
        }

        ///UpdateConduitConnectionBits writes through a KAnimGraphTileVisualizer, so only a def whose
        ///prefab carries one can actually have its connections corrupted by the shift. Check the
        ///prefabs directly rather than placing one and hoping.
        var withTileVis = rotatable
            .Where(d => d.BuildingComplete.GetComponent<KAnimGraphTileVisualizer>() != null
                        || (d.BuildingPreview != null && d.BuildingPreview.GetComponent<KAnimGraphTileVisualizer>() != null))
            .ToList();

        Log?.Line($"  rotatable AND carrying a KAnimGraphTileVisualizer: {withTileVis.Count}");
        foreach (var d in withTileVis)
            Log?.Line($"    candidate {d.PrefabID}");

        if (withTileVis.Count == 0)
        {
            Log?.Line("  REACHABILITY: rotatable utility defs exist, but none carry a " +
                      "KAnimGraphTileVisualizer — UpdateConduitConnectionBits can never write " +
                      "connections for a rotatable building, so the captured-orientation term was " +
                      "dead for that path.");
            yield break;
        }

        var def = withTileVis[0];
        Log?.Line($"  REACHABILITY: reachable — exercising {def.PrefabID}");

        const int storedFlags = (int)(UtilityConnections.Left | UtilityConnections.Right);

        var bp = new Blueprint("ConduitRotationProbe", "");
        var bc = new BuildingConfig
        {
            Offset = new Vector2I(0, 0),
            BuildingDef = def,
            BuildingDefId = def.PrefabID,
            ///the term under test: a building captured facing R90
            Orientation = Orientation.R90,
        };
        foreach (var tag in FixtureBuilder.SelectElements(def))
            bc.SelectedElements.Add(tag);
        SetConduitFlags(bc, storedFlags);
        bp.BuildingConfigurations.Add(bc);
        bp.CacheCost();

        Assert.True(TryGetConduitFlags(bc, out int roundTripped), "synthetic config carries conduit flags");
        Assert.Equal(storedFlags, roundTripped, "synthetic config's stored flags");

        var xy = Grid.CellToXY(AnchorCell);
        var target = new Vector2I(xy.x + 14, xy.y - 6);
        var result = new PlacementResult();
        yield return PlaceAt(bp, target, rotateSteps: 0, result);

        Log?.Line($"  placed {result.Orders.Count} order(s), {result.Finished.Count} finished");
        foreach (var (id, cell) in result.Orders)
            Log?.Line($"    order {id}@{cell}");

        var placedCell = result.Orders
            .Concat(result.Finished.Select(f => (id: f.id, cell: f.cell)))
            .Where(o => o.id == def.PrefabID)
            .Select(o => (Vector2I?)o.cell)
            .FirstOrDefault();

        Assert.True(placedCell != null, $"placement produced a {def.PrefabID}");

        int cellIndex = Grid.XYToCell(placedCell!.Value.x, placedCell.Value.y);
        var visualizer = UnityEngine.Object
            .FindObjectsByType<KAnimGraphTileVisualizer>(FindObjectsInactive.Include, FindObjectsSortMode.None)
            .FirstOrDefault(v => v != null && Grid.PosToCell(v.transform.GetPosition()) == cellIndex);

        if (visualizer == null)
        {
            ///UpdateConduitConnectionBits only writes through a KAnimGraphTileVisualizer. Without
            ///one there is nothing for the shift to corrupt on this def, which is again a
            ///reachability finding rather than a failure.
            Log?.Line($"  REACHABILITY: {def.PrefabID} placed without a KAnimGraphTileVisualizer — " +
                      "UpdateConduitConnectionBits cannot write connections for it.");
            yield break;
        }

        int actual = (int)visualizer.Connections;
        Log?.Line($"  connections: stored={storedFlags} onPlacedObject={actual}");
        Assert.Equal(storedFlags, actual,
            "an unrotated blueprint places the stored world-space connections unshifted");
    }

    private static void SetConduitFlags(BuildingConfig bc, int flags) =>
        typeof(BuildingConfig)
            .GetMethod("SetConduitFlags", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(bc, new object[] { flags });

    private static bool TryGetConduitFlags(BuildingConfig bc, out int flags)
    {
        var args = new object[] { 0 };
        bool ok = (bool)typeof(BuildingConfig)
            .GetMethod("GetConduitFlags", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(bc, args)!;
        flags = (int)args[0];
        return ok;
    }

    // ---- placement helper ----------------------------------------

    private sealed class PlacementResult
    {
        public readonly List<(string id, Vector2I cell)> Orders = new();
        public readonly List<string> OutsideRegion = new();

        /// <summary>
        /// Buildings that came out already complete - the instabuild path. Empty on a normal
        /// placement, where <see cref="Orders"/> carries <c>Constructable</c>s instead.
        /// </summary>
        public readonly List<(string id, Vector2I cell, float temperature, float defTemperature, float minMeltingPoint)> Finished = new();
    }

    private static Blueprint Snapshot(Vector2I topLeft, Vector2I bottomRight)
    {
        Blueprint bp = BlueprintState.CreateBlueprint(topLeft, bottomRight, filter: null, createsSnapshot: true);
        bp.SetRandomSnapshotId();
        return bp;
    }

    // The three tile/ladder pieces from FixtureLayout - all on one row (dy -5, dx -8..-2).
    private static Vector2I TileRowTopLeft() { var xy = Grid.CellToXY(AnchorCell); return new(xy.x - 10, xy.y - 5); }
    private static Vector2I TileRowBottomRight() { var xy = Grid.CellToXY(AnchorCell); return new(xy.x - 1, xy.y - 5); }

    /// <summary>
    /// Digs a clear area at <paramref name="target"/>, disables the mod's tech/material gates
    /// (a cycle-0 colony has neither), drives VisualizeBlueprint -> [rotate] -> UseBlueprint, and
    /// records the new <see cref="Constructable"/> build orders.
    /// </summary>
    private static IEnumerator PlaceAt(Blueprint bp, Vector2I target, int rotateSteps, PlacementResult outResult)
    {
        var region = new List<int>();
        for (int dx = -12; dx <= 12; dx++)
            for (int dy = -6; dy <= 6; dy++)
            {
                int c = Grid.XYToCell(target.x + dx, target.y + dy);
                if (Grid.IsValidCell(c))
                    region.Add(c);
            }
        foreach (int c in region)
            if (Grid.IsSolidCell(c))
                SimMessages.Dig(c, skipEvent: true);
        float waited = 0f;
        while (region.Any(Grid.IsSolidCell) && waited < 20f)
        {
            waited += Time.unscaledDeltaTime;
            yield return null;
        }

        var before = Constructables();
        var completeBefore = BuildingCompletes();
        var cfg = ModConfig();
        bool savedTech = cfg.RequireConstructable_Tech, savedMat = cfg.RequireConstructable_Material;
        cfg.RequireConstructable_Tech = false;
        cfg.RequireConstructable_Material = false;

        var st = BlueprintState.CurrentStateInfo();
        st.IsPlacingSnapshot = true;
        st.ForceOverrideTransformations = true;
        st.ResetRotations();

        BlueprintState.VisualizeBlueprint(target, bp);
        for (int i = 0; i < 5; i++) yield return null;
        for (int r = 0; r < rotateSteps; r++)
        {
            st.TryRotateBlueprint();
            BlueprintState.RefreshBlueprintVisualizers(BlueprintState.PlayerId_DefaultTilePreviews, bp);
            for (int i = 0; i < 3; i++) yield return null;
        }
        BlueprintState.UpdateVisual(BlueprintState.PlayerId_DefaultTilePreviews, target, forcingRedraw: true, bp);
        for (int i = 0; i < 5; i++) yield return null;
        BlueprintState.UseBlueprint(BlueprintState.PlayerId_DefaultTilePreviews, target, bp);
        for (int i = 0; i < 15; i++) yield return null;

        foreach (var c in Constructables().Where(c => !before.Contains(c)))
        {
            int cell = Grid.PosToCell(c.gameObject);
            string id = c.gameObject.GetComponent<Building>()?.Def?.PrefabID ?? "?";
            outResult.Orders.Add((id, Grid.CellToXY(cell)));
            if (!region.Contains(cell))
                outResult.OutsideRegion.Add($"{id}@{Grid.CellToXY(cell)}");
        }

        foreach (var bc in BuildingCompletes().Where(b => !completeBefore.Contains(b)))
        {
            var def = bc.Def;
            if (def == null || !bc.TryGetComponent<PrimaryElement>(out var pe))
                continue;

            outResult.Finished.Add((
                def.PrefabID,
                Grid.CellToXY(Grid.PosToCell(bc.gameObject)),
                pe.Temperature,
                def.Temperature,
                ElementLoader.GetMinMeltingPointAmongElements(new[] { pe.Element.tag })));
        }

        BlueprintState.ClearVisuals();
        st.ResetRotations();
        st.ForceOverrideTransformations = false;
        st.IsPlacingSnapshot = false;
        cfg.RequireConstructable_Tech = savedTech;
        cfg.RequireConstructable_Material = savedMat;
    }

    private static HashSet<Constructable> Constructables() =>
        new(UnityEngine.Object.FindObjectsByType<Constructable>(FindObjectsSortMode.None));

    private static HashSet<BuildingComplete> BuildingCompletes() =>
        new(UnityEngine.Object.FindObjectsByType<BuildingComplete>(FindObjectsSortMode.None));

    // Config.Instance lives on PLib's SingletonOptions<Config>, which the harness dll doesn't
    // reference - reach the inherited static getter by reflection.
    private static Config ModConfig() => (Config)typeof(Config)
        .GetProperty("Instance", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)!
        .GetValue(null)!;

    // Element identity as the stored tag hash - a Tag rebuilt from a hash on read has no
    // resolvable name, so ToString() would spuriously differ.
    private static string Describe(BuildingConfig bc) =>
        $"{bc.BuildingDef?.PrefabID ?? bc.BuildingDefId ?? "<null>"} @ ({bc.Offset.x},{bc.Offset.y}) " +
        $"o={bc.Orientation} elems=[{string.Join(",", bc.SelectedElements.Select(t => t.GetHash()))}]";
}
