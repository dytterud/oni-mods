using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using BlueprintsV2.BlueprintData;
using BlueprintsV2.BlueprintData.NoteToolPlacedEntities;
using BlueprintsV2.Visualizers;
using BlueprintsV2.Visualizers.ReplacementVisualizers;
using HarmonyLib;
using Newtonsoft.Json.Linq;
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
        new HarnessCase("mod-component-lookup-resolves-and-caches", ModComponentLookupResolvesAndCaches),
        new HarnessCase("replacement-vis-places-once-per-cell", ReplacementVisPlacesOnce),
        new HarnessCase("scheduled-seating-kick-delivers-when-time-runs", SeatingKickDelivers),
        new HarnessCase("completed-construction-applies-stored-settings", CompletionAppliesStoredSettings),
        new HarnessCase("rotation-counts-in-same-building-detection", RotationConsideredForSameBuilding),
        new HarnessCase("reconstruct-reapplies-stored-settings", ReconstructReappliesSettings),
        new HarnessCase("preconfigure-screen-loads-the-plan's-settings", PreconfigureLoadsStoredSettings),
        new HarnessCase("preconfigure-screen-opens-while-the-game-is-paused", PreconfigureWorksWhilePaused),
        new HarnessCase("preconfigure-button-shows-for-smi-backed-buildings", PreconfigureButtonShowsForSmiBackedBuildings),
        new HarnessCase("building-data-api-survives-a-dead-gameobject", BuildingDataApiSurvivesDeadGameObject),
        new HarnessCase("anim-less-previews-are-all-tile-visuals", AnimLessPreviewsAreAllTileVisuals),
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
    /// <summary>
    /// Covers the branch of <c>ModComponentLookup</c> that this fixture otherwise cannot reach.
    ///
    /// Every production caller names a component belonging to Aki's decor mods, so with only our own
    /// mod installed the name never resolves and only the "type absent" path runs - the path the
    /// create-perf measurement exercised. A player who has those mods takes the *resolving* path
    /// instead, where the lookup returns a real component, and until this case nothing asserted that
    /// path at all. <see cref="HarnessProbeComponent"/> makes it reachable without depending on any
    /// external mod.
    ///
    /// The lookup replaced <c>GetComponent(string)</c>, so the load-bearing property is that the two
    /// agree: same instance for a name that resolves, null for one that does not. Asserting only
    /// "returns non-null" would pass even if it had started finding the wrong component.
    /// </summary>
    private static IEnumerator ModComponentLookupResolvesAndCaches()
    {
        var find = typeof(Blueprint).Assembly
            .GetType("BlueprintsV2.BlueprintData.ModComponentLookup")
            ?.GetMethod("Find", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
        Assert.True(find != null, "ModComponentLookup.Find was found (is the cached lookup still in the mod?)");

        Component? Find(GameObject go, string name) => find!.Invoke(null, new object[] { go, name }) as Component;

        const string probeName = nameof(HarnessProbeComponent);
        const string absentName = "HarnessNoSuchComponent_9d3f1a";

        var carrier = new GameObject("HarnessLookupCarrier");
        var bare = new GameObject("HarnessLookupBare");
        var subclassOnly = new GameObject("HarnessLookupSubclassOnly");
        try
        {
            var probe = carrier.AddComponent<HarnessProbeComponent>();
            var subclass = subclassOnly.AddComponent<HarnessProbeComponentSubclass>();
            yield return null;

            ///The resolving branch, component present. Identity rather than non-null: the whole risk
            ///of resolving a type by name is binding to the wrong one, which fails silently.
            var found = Find(carrier, probeName);
            Assert.True(ReferenceEquals(probe, found), "Find returns the component instance on the object");
            Assert.True(ReferenceEquals(carrier.GetComponent(probeName), found),
                "Find agrees with GetComponent(string) for a name that resolves");

            ///The resolving branch, component absent - the type exists, this object just lacks it.
            ///Distinct from the absent-type case below, and the one the production callers hit for
            ///most buildings when the decor mods *are* installed.
            Assert.True(Find(bare, probeName) == null, "Find returns null when the type exists but the object lacks it");

            ///The absent-type branch: nothing defines this name, so nothing can carry it.
            Assert.True(Find(carrier, absentName) == null, "Find returns null for a type nothing defines");
            Assert.True(carrier.GetComponent(absentName) == null,
                "GetComponent(string) agrees the absent name finds nothing");

            ///Documented behaviour difference, pinned rather than left to be discovered later:
            ///GetComponent(Type) matches assignable subclasses, where the string overload matched an
            ///exact class name. Only observable on an object carrying the subclass and not the base.
            var viaLookup = Find(subclassOnly, probeName);
            var viaString = subclassOnly.GetComponent(probeName);
            Log?.Line($"  subclass-only object: Find={(viaLookup == null ? "null" : viaLookup.GetType().Name)}, " +
                      $"GetComponent(string)={(viaString == null ? "null" : viaString.GetType().Name)}");
            Assert.True(ReferenceEquals(subclass, viaLookup),
                "Find matches a subclass instance through the base type name (typed-lookup semantics)");

            ///Caching is the point of the type, so check a repeat call still answers correctly rather
            ///than having poisoned itself - a negative result cached against the wrong key, say.
            Assert.True(ReferenceEquals(probe, Find(carrier, probeName)), "a repeat lookup still finds the component");
            Assert.True(Find(carrier, absentName) == null, "a repeat lookup of an absent name is still null");
        }
        finally
        {
            UnityEngine.Object.Destroy(carrier);
            UnityEngine.Object.Destroy(bare);
            UnityEngine.Object.Destroy(subclassOnly);
        }
    }

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

    // ---- a replacement vis places once, not once per callback -----

    /// <summary>
    /// <c>ReplacementVis</c> kept no record that a placement had already succeeded, so a second
    /// callback arriving before the vis was torn down placed the queued building again -
    /// overwriting a cell N times stacked N overlapping construction orders.
    ///
    /// The unguarded window is narrow and specific: inside <c>TryPlacingQueuedBP</c>, after
    /// <c>replacementInProgress</c> goes back to false but before <c>FinalizePlacementCheck</c>
    /// reaches <c>DestroySelf</c> (which is what sets <c>markedForDeletion</c>). A
    /// <c>GameScenePartitioner</c> callback landing there found every guard clear.
    ///
    /// Winning that race on demand is not something a harness case can do, and probing the vis
    /// after it has placed means probing a destroyed object. So this asserts on the guard itself,
    /// the way <see cref="PlannedBuildingMatch"/> asserts on its predicate: one vis takes the
    /// normal path and must latch, a second has the latch pre-set and must then refuse to start a
    /// placement check at all. The second is the re-entry the race produces.
    /// </summary>
    private static IEnumerator ReplacementVisPlacesOnce()
    {
        Blueprint bp = Snapshot(TileRowTopLeft(), TileRowBottomRight());
        var config = bp.BuildingConfigurations.FirstOrDefault(b => b.BuildingDef?.PrefabID == "Ladder");
        Assert.True(config != null, "captured the fixture's Ladder to queue as a replacement");

        var xy = Grid.CellToXY(AnchorCell);
        var cfg = ModConfig();
        bool savedTech = cfg.RequireConstructable_Tech, savedMat = cfg.RequireConstructable_Material;
        cfg.RequireConstructable_Tech = false;
        cfg.RequireConstructable_Material = false;

        ///instabuild would take TryPlacingQueuedBP's def.Build branch and hand back finished
        ///buildings; the reported bug is about unfinished ones stacking, so count Constructables
        bool savedInstant = DebugHandler.InstantBuildMode;
        DebugHandler.InstantBuildMode = false;

        ReplacementVis? placing = null;
        ReplacementVis? latched = null;
        ReplacementVis? blocked = null;
        try
        {
            ///--- the normal path: one vis, one build order, latch set ---
            ///below every other case's dig region (PlaceAt reaches y-18 at its deepest), so these
            ///two cells are this case's alone - a collision would silently test nothing
            int placeCell = Grid.XYToCell(xy.x - 8, xy.y - 20);
            yield return ClearCell(placeCell);
            AssertCellEmpty(placeCell, "normal-path");

            var before = Constructables();
            var spawn = SpawnSeatedVis(config!, placeCell);
            yield return spawn;
            placing = spawn.Vis;

            Assert.True(!GetBool(placing, "placementSuccessful"),
                "a freshly seated vis has not latched - the retry path stays open until something is built");
            Assert.True(GetPrivate(placing, "scheduledSpawnCheck") is SchedulerHandle handle
                        && !handle.Equals(default(SchedulerHandle)),
                "SeatVis kept the next-frame handle, so UnseatVis has something to clear");

            ///records why the kick below has to be delivered by hand - see SchedulerProbe
            yield return SchedulerProbe();

            yield return Kick(placing);

            var placed = Constructables().Where(c => !before.Contains(c))
                .Where(c => Grid.PosToCell(c.gameObject) == placeCell).ToList();
            Log?.Line($"  normal path: {placed.Count} build order(s) at {Grid.CellToXY(placeCell)}, " +
                      $"latched={GetBool(placing!, "placementSuccessful")}");
            Assert.Equal(1, placed.Count, "a replacement vis over a clear cell queues exactly one building");
            Assert.True(GetBool(placing!, "placementSuccessful"),
                "the vis latched placementSuccessful once it actually built something");

            ///--- the re-entry the race produces: latch set, callback must do nothing ---
            int guardCell = Grid.XYToCell(xy.x - 3, xy.y - 20);
            yield return ClearCell(guardCell);
            AssertCellEmpty(guardCell, "guard");

            var beforeGuard = Constructables();
            var guardSpawn = SpawnSeatedVis(config!, guardCell);
            yield return guardSpawn;
            latched = guardSpawn.Vis;

            Assert.True(!GetBool(latched, "placementSuccessful"),
                "the guard vis has not placed on its own, so the latch below is the only thing under test");
            ///the state the vis holds during the window described above: placement done, teardown
            ///not yet reached, so markedForDeletion and replacementInProgress are both still false
            SetBool(latched, "placementSuccessful", true);

            yield return Kick(latched);

            Assert.True(GetPrivate(latched, "check") == null,
                "a latched vis does not start a placement check when a callback arrives");

            var extra = Constructables().Where(c => !beforeGuard.Contains(c))
                .Where(c => Grid.PosToCell(c.gameObject) == guardCell).ToList();
            Log?.Line($"  latched vis: {extra.Count} build order(s) at {Grid.CellToXY(guardCell)}, " +
                      $"markedForDeletion={GetBool(latched, "markedForDeletion")}, " +
                      $"replacementInProgress={GetBool(latched, "replacementInProgress")}");
            Assert.Equal(0, extra.Count, "a latched vis places nothing, so a re-entrant callback cannot duplicate");

            ///--- the retry path: a failed placement must NOT latch, and a later callback must
            ///--- still place. This is the risk in the fix - latching too eagerly would strand a
            ///--- vis whose cell has not cleared yet, and the building would silently never appear.
            ///The obstruction is a finished building of the same def already in the cell - TryPlace
            ///refuses to put a second one on the same object layer. Solid rock does NOT work: ONI
            ///queues a build order inside rock quite happily and lets a dupe dig it out.
            ///SeatVis queues the occupant for deconstruction, which never completes while the sim
            ///is paused, so the cell stays blocked until this case clears it by hand.
            int blockedCell = Grid.XYToCell(xy.x + 2, xy.y - 20);
            yield return ClearCell(blockedCell);
            AssertCellEmpty(blockedCell, "blocked");

            var blocker = config!.BuildingDef!.Build(blockedCell, Orientation.Neutral,
                resource_storage: null, FixtureBuilder.SelectElements(config.BuildingDef),
                temperature: 293.15f, playsound: false, timeBuilt: 0f);
            Assert.True(blocker != null, "built the obstructing building the retry has to wait on");
            for (int i = 0; i < 5; i++) yield return null;

            var beforeBlocked = Constructables();
            var blockedSpawn = SpawnSeatedVis(config!, blockedCell);
            yield return blockedSpawn;
            blocked = blockedSpawn.Vis;

            yield return Kick(blocked);

            var whileBlocked = Constructables().Where(c => !beforeBlocked.Contains(c))
                .Where(c => Grid.PosToCell(c.gameObject) == blockedCell).ToList();
            Log?.Line($"  blocked: {whileBlocked.Count} build order(s), " +
                      $"latched={GetBool(blocked, "placementSuccessful")}, " +
                      $"markedForDeletion={GetBool(blocked, "markedForDeletion")}");
            Assert.Equal(0, whileBlocked.Count, "placement into an occupied cell produces no build order");
            Assert.True(!GetBool(blocked, "placementSuccessful"),
                "a FAILED placement does not latch - this is what keeps the retry path open");
            Assert.True(!GetBool(blocked, "markedForDeletion"),
                "the vis survives a failed placement, so it is still there to retry");

            ///--- the re-entrancy guard stays closed for the whole check (issue #64) ---
            ///
            ///The scene partitioner dispatches synchronously - Grid's ObjectLayerIndexer setter
            ///calls GameScenePartitioner.TriggerEvent inline - so a grid write inside
            ///FinalizePlacementCheck re-enters OnPreoccupiedCellChanged before the method returns.
            ///`check` used to be cleared on entry, leaving the guard down for exactly that window.
            ///
            ///<para>Modelled rather than waited for: a prefix on UpdateVisualState (reached from
            ///inside FinalizePlacementCheck on the failure branch, which is the state this vis is in
            ///right now) delivers the callback at the moment a real grid write would. A second
            ///prefix counts how many times FinalizePlacementCheck runs for this vis. With the guard
            ///cleared on entry the callback starts a second coroutine and the count reaches 2; with
            ///it latched until the check finishes, the callback is refused and the count stays
            ///1.</para>
            yield return ReentrancyProbe(blocked!);

            ///Complete the deconstruct SeatVis already queued, which is what would happen on its own
            ///if the sim were running. This is the mod's own instant-deconstruct call
            ///(DoDeconstrucThingsAt uses it under InstantBuild).
            Assert.True(blocker!.TryGetComponent<Deconstructable>(out var blockerDecon),
                "the obstructing building is deconstructable, so the cell can be cleared");
            blockerDecon.OnCompleteWork(null);

            ///Freeing the cell changes the grid, and the scene partitioner delivers that even with
            ///the sim paused - unlike GameScheduler, it is driven by grid writes rather than game
            ///time. So the vis usually retries on its own here. The extra Kick covers the case
            ///where it has not, and is a no-op once the latch is set.
            for (int i = 0; i < 10; i++) yield return null;
            yield return Kick(blocked);

            var afterClear = Constructables().Where(c => !beforeBlocked.Contains(c))
                .Where(c => Grid.PosToCell(c.gameObject) == blockedCell).ToList();
            Log?.Line($"  after clearing: {afterClear.Count} build order(s) at {Grid.CellToXY(blockedCell)}, " +
                      $"latched={GetBool(blocked!, "placementSuccessful")}");
            Assert.Equal(1, afterClear.Count,
                "once the cell clears the retrying vis places exactly one building - not zero " +
                "(the latch would have stranded it) and not two");
            Assert.True(GetBool(blocked!, "placementSuccessful"),
                "and only then does it latch");
        }
        finally
        {
            DestroyVis(placing);
            DestroyVis(latched);
            DestroyVis(blocked);
            DebugHandler.InstantBuildMode = savedInstant;
            cfg.RequireConstructable_Tech = savedTech;
            cfg.RequireConstructable_Material = savedMat;
        }
    }

    // ---- settings land when construction finishes --------------------

    /// <summary>
    /// The other end of blueprint data transfer, and the one path `dotnet test` is furthest from:
    /// a blueprint queues a building, the plan carries the captured settings on an
    /// <c>UnderConstructionDataTransfer</c>, and when a dupe finishes building it
    /// <c>DataTransferPatches.OnBuildingConstructed</c> schedules
    /// <c>UnderConstructionDataTransfer.TransferDataTo</c> for the next frame.
    ///
    /// That last hop rides on <c>GameScheduler</c>, so it never ran in the harness before
    /// <see cref="WithSimRunning"/> existed - this whole path has been untested until now.
    ///
    /// The case reads the priority twice, before and after the clock runs, because only the
    /// difference isolates the scheduled hop. If the finished building already carried the
    /// setting with the clock stopped, something else put it there and this would be asserting
    /// nothing.
    /// </summary>
    private static IEnumerator CompletionAppliesStoredSettings()
    {
        ///non-default on both axes, so neither a default class nor a default value passes by luck
        var captured = new PrioritySetting(PriorityScreen.PriorityClass.high, 7);

        ///Stamp every building in the captured row, not one chosen up front: placement on this
        ///terrain does not always get all three down (see blueprint-rotation-rotates-the-layout),
        ///so which building this case ends up asserting on has to be decided after the fact.
        var stamped = new List<(Prioritizable prio, PrioritySetting original)>();
        foreach (var spec in FixtureLayout.Buildings.Where(s => s.Dy == -5))
        {
            var go = Grid.Objects[FixtureLayout.CellOf(spec, AnchorCell), (int)ObjectLayer.Building];
            if (go != null && go.TryGetComponent<Prioritizable>(out var p))
            {
                stamped.Add((p, p.GetMasterPriority()));
                p.SetMasterPriority(captured);
            }
        }
        Assert.True(stamped.Count > 0, "at least one prioritizable building in the captured row");
        yield return null;

        Blueprint bp;
        try
        {
            bp = Snapshot(TileRowTopLeft(), TileRowBottomRight());
        }
        finally
        {
            foreach (var (p, original) in stamped)
                p.SetMasterPriority(original);
        }

        var carriers = bp.BuildingConfigurations
            .Where(b => b.BuildingDef != null && b.TryGetDataValue("Prioritizable", out _))
            .Select(b => b.BuildingDef!.PrefabID)
            .ToHashSet();
        Log?.Line($"  captured Prioritizable data on: {string.Join(", ", carriers)}");
        Assert.True(carriers.Count > 0, "the capture carries Prioritizable data for at least one building");

        var xy = Grid.CellToXY(AnchorCell);
        var result = new PlacementResult();

        ///a plan, not an instabuilt building - the whole path under test starts from a plan
        bool savedInstant = DebugHandler.InstantBuildMode;
        DebugHandler.InstantBuildMode = false;
        try
        {
            ///well clear of every other case's dig region, including the y-20 row the
            ///replacement-vis cases use (x-8 .. x+7)
            yield return PlaceAt(bp, new Vector2I(xy.x + 24, xy.y - 20), rotateSteps: 0, result);
        }
        finally
        {
            DebugHandler.InstantBuildMode = savedInstant;
        }

        Assert.True(result.Finished.Count == 0, "nothing was instabuilt, so these are genuinely plans");
        Log?.Line($"  queued: {string.Join(", ", result.Orders.Select(o => $"{o.id}@{o.cell}"))}");

        var order = result.Orders.FirstOrDefault(o => carriers.Contains(o.id));
        Assert.True(order.id != null && carriers.Contains(order.id),
            $"a building carrying Prioritizable data was queued (got {result.Orders.Count} order(s): " +
            string.Join(", ", result.Orders.Select(o => o.id)) + ")");

        var targetDef = Assets.GetBuildingDef(order.id);
        Assert.True(targetDef != null, $"resolved the BuildingDef for the queued {order.id}");

        int cell = Grid.XYToCell(order.cell.x, order.cell.y);
        var plan = Grid.Objects[cell, (int)targetDef!.ObjectLayer];
        Assert.True(plan != null, $"found the queued {order.id} at {order.cell}");
        Assert.True(plan!.TryGetComponent<UnderConstructionDataTransfer>(out var transfer),
            "the plan carries an UnderConstructionDataTransfer");
        Assert.True(transfer.GetStoredData().ContainsKey("Prioritizable"),
            "the plan is holding the captured priority to apply on completion");

        ///finish the build the way a dupe would - this is what fires the gameplay event that
        ///DataTransferPatches listens for
        Assert.True(plan!.TryGetComponent<Constructable>(out var constructable), "the plan is constructable");

        ///OnCompleteWork(null) is not enough - it is the work callback, and the build still does
        ///not finish (verified: nothing completes in 30 frames, clock running or stopped).
        ///FinishConstruction is the step that actually swaps the plan for the finished building,
        ///and it is what fires the gameplay event DataTransferPatches listens for.
        var finish = typeof(Constructable)
            .GetMethod("FinishConstruction", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
        Assert.True(finish != null, "Constructable.FinishConstruction exists to complete the build with");

        ///its signature is not part of any API this mod uses, so build the argument list from
        ///whatever it declares rather than pinning a shape that a game update could change
        var pars = finish!.GetParameters();
        Log?.Line($"  FinishConstruction({string.Join(", ", pars.Select(p => p.ParameterType.Name + " " + p.Name))})");
        var args = pars.Select(p => p.ParameterType.IsValueType
                ? Activator.CreateInstance(p.ParameterType)
                : null)
            .ToArray();

        ///FinishConstruction reads Constructable.initialTemperature and hands it straight to
        ///BuildingDef.Build (verified in the shipped assembly's IL: OnCompleteWork is the only
        ///thing that writes that field, FinishConstruction reads it). OnCompleteWork is the step
        ///a dupe goes through and this case deliberately skips, so the field is still 0 here -
        ///and a zero trips Klei's "temperature <= 0" assert, which hands the run to ONI's crash
        ///reporter and submits a crash report to Klei from what is only a test finishing a build
        ///by hand. Seed it the way OnCompleteWork would have.
        var initialTemperature = typeof(Constructable)
            .GetField("initialTemperature", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.True(initialTemperature != null,
            "Constructable.initialTemperature exists to seed the finished building's temperature");
        ///room temperature: the case asserts on priority, never on temperature, so this only has
        ///to be a plausible value above zero. The cell's own temperature would be the realistic
        ///choice, but the harness digs this area out to vacuum, so it is always the fallback that
        ///runs - a branch that never executes is worse than the constant it hides.
        const float buildTemperature = 293.15f;
        initialTemperature!.SetValue(constructable, buildTemperature);
        Log?.Line($"  seeded initialTemperature={buildTemperature:F1}K (OnCompleteWork would have)");

        finish.Invoke(constructable, args);

        ///Poll with the clock still stopped. If the building finishes here, the priority read
        ///below is the control: the scheduled transfer cannot have run yet.
        GameObject? built = null;
        for (int i = 0; i < 30 && built == null; i++)
        {
            yield return null;
            var at = Grid.Objects[cell, (int)targetDef!.ObjectLayer];
            if (at != null && at.GetComponent<BuildingComplete>() != null)
                built = at;
        }

        PrioritySetting? beforeClock = null;
        if (built != null && built.TryGetComponent<Prioritizable>(out var prioBefore))
            beforeClock = prioBefore.GetMasterPriority();
        Log?.Line($"  clock stopped: completed={built != null}" +
                  (beforeClock == null ? "" : $", priority={beforeClock.Value.priority_class}/{beforeClock.Value.priority_value}") +
                  $" (captured {captured.priority_class}/{captured.priority_value})");

        yield return WithSimRunning(frames: 30);

        built ??= Grid.Objects[cell, (int)targetDef!.ObjectLayer];
        Assert.True(built != null && built.GetComponent<BuildingComplete>() != null,
            "construction completed, so there is a finished building to apply settings to");
        Assert.True(built!.TryGetComponent<Prioritizable>(out var builtPrio), $"the finished {order.id} is prioritizable");

        var afterClock = builtPrio.GetMasterPriority();
        Log?.Line($"  after the clock ran: priority={afterClock.priority_class}/{afterClock.priority_value}");

        Assert.Equal(captured.priority_class, afterClock.priority_class,
            "the scheduled transfer applied the captured priority class");
        Assert.Equal(captured.priority_value, afterClock.priority_value,
            "the scheduled transfer applied the captured priority value");

        ///Only meaningful when the building finished before the clock ran - otherwise there was no
        ///moment at which to observe "completed but not yet transferred", and the assertions above
        ///are all this case can claim.
        if (beforeClock != null)
            Assert.True(beforeClock.Value.priority_class != captured.priority_class
                        || beforeClock.Value.priority_value != captured.priority_value,
                "the setting was NOT already on the finished building before the clock ran - " +
                "otherwise something other than the scheduled transfer put it there");
        else
            Log?.Line("  NOTE: construction did not complete with the clock stopped, so the " +
                      "before/after control could not be taken - see the case comment");
    }

    // ---- rotation counts in same-building detection ------------------

    /// <summary>
    /// <c>SameBuildingAlreadyFinishedInPlace</c> used to decide "a matching building is already
    /// here" from the def and the cell alone, so a blueprint entry rotated differently from the
    /// building in that cell still counted as the same building - and with apply-settings on, the
    /// blueprint then forced its own orientation onto it (upstream report #357).
    ///
    /// Asserted on the predicate rather than by driving a placement, the way
    /// <see cref="PlannedBuildingMatch"/> is: five call sites read this one answer and each makes
    /// its own decision from it, so the predicate is the thing worth pinning.
    ///
    /// The two carve-outs matter as much as the rule. A building with no <c>Rotatable</c> has no
    /// orientation to disagree about, and drywall's <c>Rotatable</c> is visual variation rather
    /// than placement orientation - comparing it would make identical drywall read as different
    /// and churn.
    /// </summary>
    private static IEnumerator RotationConsideredForSameBuilding()
    {
        var xy = Grid.CellToXY(AnchorCell);
        int cell = Grid.XYToCell(xy.x + 14, xy.y - 20);

        ///Flips and rotations are asserted separately, and both halves have to run.
        ///
        ///<c>Orientation</c> is a single enum - Neutral/R90/R180/R270 and FlipH/FlipV all live in
        ///it - and the predicate compares the whole value, so the two families go down identical
        ///code. The split is not about suspecting they differ; it is because this probe used to
        ///pick ONE def by shape and test whichever family it happened to land in. Every 1x1
        ///Building-layer def that builds on a bare cell is flip-only (Corner Moulding, in
        ///practice), so the rotation half was never reached and the R90 arm of the switch below
        ///was dead. A def-order change in a future game build could have silently swapped which
        ///family was covered, with the case name and the assertions reading the same either way.
        ///
        ///The rotate probe deliberately does not constrain the footprint. Of the defs permitting
        ///R90 or R360, the 1x1 ones are rocket-interior fittings, Gravitas POI props, a dev
        ///spawner, or need a foundation under them; the ones that build anywhere (the valves) are
        ///1x2. <see cref="ClearCell"/> digs a 5x5 region, so a taller probe is accommodated.
        var probes = new (string Family, Func<BuildingDef, bool> Accepts, int Cell)[]
        {
            ("flip", d => d.WidthInCells == 1 && d.HeightInCells == 1
                          && (d.PermittedRotations == PermittedRotations.FlipH
                              || d.PermittedRotations == PermittedRotations.FlipV),
             cell),
            ("rotate", d => d.PermittedRotations == PermittedRotations.R90
                            || d.PermittedRotations == PermittedRotations.R360,
             Grid.XYToCell(xy.x + 20, xy.y - 20)),
        };

        foreach (var probe in probes)
        {
            yield return ClearCell(probe.Cell);
            AssertCellEmpty(probe.Cell, $"rotation ({probe.Family})");

            ///picked by shape and permitted rotations, so this does not pin a prefab id
            GameObject? built = null;
            BuildingDef? probeDef = null;
            foreach (var def in Assets.BuildingDefs.Where(d =>
                         d != null
                         && d.ObjectLayer == ObjectLayer.Building
                         && d.BuildingComplete != null
                         && d.BuildingComplete.GetComponent<Rotatable>() != null
                         && probe.Accepts(d)))
            {
                GameObject? go = null;
                try
                {
                    go = def.Build(probe.Cell, Orientation.Neutral, null,
                                   FixtureBuilder.SelectElements(def), 293.15f, false, 0f);
                }
                catch { /* some defs need conditions a bare cell cannot give; try the next */ }

                if (go != null && go.GetComponent<Rotatable>() != null)
                {
                    built = go;
                    probeDef = def;
                    break;
                }
                if (go != null)
                    go.DeleteObject();
            }

            if (built == null || probeDef == null)
            {
                ///Not a silent pass: the other family still runs, and the gap is named in the log.
                Log?.Line($"  REACHABILITY: no buildable {probe.Family} def in this install, so the " +
                          $"{probe.Family} half of the rule is unexercised");
                continue;
            }

            for (int i = 0; i < 5; i++) yield return null;

            var mismatch = probeDef.PermittedRotations switch
            {
                PermittedRotations.FlipH => Orientation.FlipH,
                PermittedRotations.FlipV => Orientation.FlipV,
                _ => Orientation.R90,
            };
            Log?.Line($"  {probe.Family}: built {probeDef.PrefabID}@{Grid.CellToXY(probe.Cell)} at " +
                      $"Neutral (rotations={probeDef.PermittedRotations}); comparing against {mismatch}");

            try
            {
                Assert.True(MatchesAt(probeDef, probe.Cell, Orientation.Neutral),
                    $"[{probe.Family}] a same-def building at the SAME orientation still matches");
                Assert.True(!MatchesAt(probeDef, probe.Cell, mismatch),
                    $"[{probe.Family}] a same-def building at a DIFFERENT orientation ({mismatch}) " +
                    "no longer matches - this is the fix");
            }
            finally
            {
                built.DeleteObject();
            }
            for (int i = 0; i < 3; i++) yield return null;
        }

        ///--- a building with no Rotatable must behave exactly as before ---
        var tileDef = Assets.GetBuildingDef("Tile");
        var tile = Components.BuildingCompletes.Items
            .FirstOrDefault(b => b != null && b.Def != null && b.Def.PrefabID == "Tile");
        if (tile != null && tileDef != null && tile.GetComponent<Rotatable>() == null)
        {
            int tileCell = Grid.PosToCell(tile.gameObject);
            Assert.True(MatchesAt(tileDef, tileCell, Orientation.Neutral),
                "a non-rotatable building matches at Neutral");
            Assert.True(MatchesAt(tileDef, tileCell, Orientation.R90),
                "a non-rotatable building matches regardless of the blueprint's orientation");
            Log?.Line($"  non-rotatable {tileDef.PrefabID}: matches at both orientations, as before");
        }
        else
        {
            Log?.Line("  REACHABILITY: the fixture Tile is rotatable or missing, so the " +
                      "no-Rotatable carve-out was not exercised");
        }

        ///--- drywall keeps matching despite a differing rotation ---
        var drywallDef = Assets.BuildingDefs.FirstOrDefault(d =>
            d != null && d.ObjectLayer == ObjectLayer.Backwall
            && d.WidthInCells == 1 && d.HeightInCells == 1
            && d.BuildingComplete != null && d.BuildingComplete.GetComponent<Rotatable>() != null);

        if (drywallDef == null)
        {
            Log?.Line("  REACHABILITY: no 1x1 Backwall building with a Rotatable in this install, " +
                      "so the drywall carve-out is unexercised - it is shape-based, not id-based, " +
                      "so a DLC that adds one would be covered by this case as written");
            yield break;
        }

        int wallCell = Grid.XYToCell(xy.x + 17, xy.y - 20);
        yield return ClearCell(wallCell);
        GameObject? wall = null;
        try
        {
            wall = drywallDef.Build(wallCell, Orientation.Neutral, null,
                                    FixtureBuilder.SelectElements(drywallDef), 293.15f, false, 0f);
        }
        catch { /* fall through to the reachability note */ }

        if (wall == null)
        {
            Log?.Line($"  REACHABILITY: {drywallDef.PrefabID} would not build here, drywall carve-out unexercised");
            yield break;
        }

        for (int i = 0; i < 5; i++) yield return null;
        try
        {
            Assert.True(MatchesAt(drywallDef, wallCell, Orientation.R90),
                $"{drywallDef.PrefabID} (1x1 Backwall) still matches at a different rotation - " +
                "its Rotatable is visual variation, not placement orientation");
            Log?.Line($"  drywall carve-out holds for {drywallDef.PrefabID}");
        }
        finally
        {
            wall.DeleteObject();
        }
    }

    // ---- the reflectable data API against a dead GameObject ----------

    /// <summary>
    /// <c>GetAdditionalBuildingData</c> and <c>GetAllAdditionalBuildingData</c> are public
    /// reflectable surface (#61) with no internal callers, so the argument arrives from another mod
    /// and cannot be constrained from this side. Neither guarded its input: the first handed the
    /// GameObject to every registered handler in turn, and the second dereferenced it again for
    /// <c>TryGetComponent</c>.
    ///
    /// <para>Both cases matter, and the second is why upstream 8018c30 is not enough on its own -
    /// it guards only the inner method, which leaves the outer entry point throwing for exactly the
    /// caller the guard exists for. This case would still pass with that partial fix on the null
    /// argument and fail on the destroyed one, which is the distinction worth pinning.</para>
    ///
    /// <para>A <b>destroyed</b> GameObject is the interesting input, not just null: Unity's
    /// fake-null means it still satisfies the compiler's non-null contract while failing
    /// <c>== null</c> at runtime, so a guard written as <c>== null</c> would let it through.</para>
    /// </summary>
    private static IEnumerator BuildingDataApiSurvivesDeadGameObject()
    {
        var apiType = typeof(Blueprint).Assembly.GetType("BlueprintsV2.ModAPI.API_Methods");
        Assert.True(apiType != null, "API_Methods was found");

        var methods = new[] { "GetAdditionalBuildingData", "GetAllAdditionalBuildingData" }
            .Select(n => apiType!.GetMethod(n, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static))
            .ToList();
        Assert.True(methods.All(m => m != null), "both data-reading entry points were found");

        ///A real destroyed GameObject, not a null reference dressed up as one - Destroy is deferred
        ///to end of frame, so the frames below are what actually make it dead.
        var doomed = new GameObject("BPI-Harness-DoomedProbe");
        UnityEngine.Object.Destroy(doomed);
        for (int i = 0; i < 3; i++) yield return null;

        Assert.True(doomed == null,
            "the probe GameObject is destroyed - Unity's fake-null reports it as null");
        Assert.True(!ReferenceEquals(doomed, null),
            "...while the managed reference is still there, which is the case a plain == null " +
            "guard in the API would miss");

        foreach (var method in methods)
        {
            foreach (var (label, arg) in new[] { ("null", (GameObject?)null), ("destroyed", doomed) })
            {
                object? result;
                try
                {
                    result = method!.Invoke(null, new object?[] { arg });
                }
                catch (TargetInvocationException e)
                {
                    Assert.True(false,
                        $"{method.Name}({label}) threw {e.InnerException?.GetType().Name ?? "?"} - " +
                        "an external caller handing over a dead building must get an empty result, " +
                        "not an exception from inside a handler it has never heard of");
                    yield break;
                }

                ///The harness's own #85 case hard-casts this return value, and so may a consumer -
                ///so the guard path has to return an empty dictionary rather than null.
                var dict = result as Dictionary<string, JObject>;
                Assert.True(dict != null,
                    $"{method.Name}({label}) returns a dictionary, not null");
                Assert.Equal(0, dict!.Count,
                    $"{method.Name}({label}) returns an EMPTY dictionary");
                Log?.Line($"  {method.Name}({label}): returned {dict.Count} entries, no throw");
            }
        }
    }

    // ---- preconfigure button for SMI-backed buildings ----------------

    /// <summary>
    /// Storage Tile and Radbolt Chamber had no preconfigure button on a planned building
    /// (issue #85). <c>UnderConstructionDataSettingHelper.HasDataTransferComponents</c> decides
    /// that by asking the <b>prefab</b> what data it carries, and their two handlers read a live
    /// state machine, which a prefab does not have - so both returned null and the gate saw
    /// nothing.
    ///
    /// <para>Asserted against the gate's own input rather than by opening the side screen: the
    /// screen needs a selected planned building and a UI frame, while the decision being fixed is
    /// entirely "what does the prefab report". <see cref="PreconfigureLoadsStoredSettings"/> covers
    /// the screen end of it.</para>
    ///
    /// <para><c>Battery</c> is the control, and it matters as much as the positive cases. It has no
    /// registered handler and nothing to configure, so its button is *correctly* hidden - the only
    /// data it reports is <c>BuildingEnabledButton</c>, which the gate filters out. If a future
    /// change made the gate answer "yes" for everything, the positive assertions alone would still
    /// pass and this one would catch it.</para>
    ///
    /// <para>Probes by prefab id, since the whole point is these specific state-machine-backed
    /// buildings; a missing def logs REACHABILITY and skips rather than failing, so a DLC layout
    /// this fixture does not have cannot turn into a red run.</para>
    /// </summary>
    private static IEnumerator PreconfigureButtonShowsForSmiBackedBuildings()
    {
        ///API_Methods is internal to the mod, so reach it the way the other cases reach internals
        ///rather than widening production visibility for a test.
        var getData = typeof(Blueprint).Assembly
            .GetType("BlueprintsV2.ModAPI.API_Methods")
            ?.GetMethod("GetAdditionalBuildingData",
                        BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
        Assert.True(getData != null,
            "API_Methods.GetAdditionalBuildingData was found (is the data-transfer API still there?)");

        ///Mirrors HasDataTransferComponents: everything the handlers report for the prefab, minus
        ///the components it deliberately ignores. Non-empty means the button shows.
        bool ButtonShowsFor(BuildingDef def, out int reported, out string keys)
        {
            var data = (Dictionary<string, JObject>)getData!.Invoke(
                null, new object[] { def.BuildingComplete });
            reported = data.Count;
            var kept = data.Keys
                .Where(k => !UnderConstructionDataSettingHelper.ComponentsToIgnore.Contains(k))
                .OrderBy(k => k)
                .ToArray();
            keys = kept.Length > 0 ? string.Join(", ", kept) : "(none)";
            return kept.Length > 0;
        }

        foreach (var probe in new[]
                 {
                     ("StorageTile", true),
                     ("HEPBattery", true),
                     ("Battery", false),
                 })
        {
            var def = Assets.GetBuildingDef(probe.Item1);
            if (def == null || def.BuildingComplete == null)
            {
                Log?.Line($"  REACHABILITY: no {probe.Item1} def in this install, not exercised");
                continue;
            }

            bool shows = ButtonShowsFor(def, out int reported, out string keys);
            Log?.Line($"  {probe.Item1}: {reported} handler(s) reported, kept [{keys}] -> " +
                      $"button {(shows ? "SHOWS" : "HIDDEN")} (expected {(probe.Item2 ? "SHOWS" : "HIDDEN")})");

            if (probe.Item2)
                Assert.True(shows,
                    $"{probe.Item1} offers a preconfigure button - its settings are transferable, so " +
                    "a planned one must be configurable before it is built");
            else
                Assert.True(!shows,
                    $"{probe.Item1} still has NO preconfigure button - it has nothing to configure, " +
                    "so the prefab fallback must not hand one to every building");
        }

        ///--- the prefab fallback must not leak into capture ---
        ///
        ///This is the assertion for the one place the fix deliberately differs from upstream
        ///03ceccc. The fallback is guarded on "no live state machine" so it answers only for a
        ///prefab; drop that guard and an *unconfigured* storage tile starts reporting an empty
        ///entry, which is not inert - BuildingConfig.HasAnyBuildingData counts entries rather than
        ///content, so the preview would paint the apply-settings colour and placement would pop
        ///"Settings applied!" over a tile with nothing to apply.
        var storageTileDef = Assets.GetBuildingDef("StorageTile");
        if (storageTileDef == null)
        {
            Log?.Line("  REACHABILITY: no StorageTile def, capture-leak guard not exercised");
            yield break;
        }

        var xy = Grid.CellToXY(AnchorCell);
        int tileCell = Grid.XYToCell(xy.x + 23, xy.y - 20);
        yield return ClearCell(tileCell);
        AssertCellEmpty(tileCell, "storage tile capture-leak");

        GameObject? liveTile = null;
        try
        {
            liveTile = storageTileDef.Build(tileCell, Orientation.Neutral, null,
                                            FixtureBuilder.SelectElements(storageTileDef),
                                            293.15f, false, 0f);
        }
        catch { /* fall through to the reachability note */ }

        if (liveTile == null)
        {
            Log?.Line("  REACHABILITY: a StorageTile would not build here, capture-leak guard not exercised");
            yield break;
        }

        for (int i = 0; i < 5; i++) yield return null;
        try
        {
            var live = (Dictionary<string, JObject>)getData!.Invoke(null, new object[] { liveTile });
            bool leaked = live.ContainsKey("StorageTile");
            Log?.Line($"  live unconfigured StorageTile reports {live.Count} handler(s), " +
                      $"StorageTile entry present={leaked} (expected False)");
            Assert.True(!leaked,
                "an unconfigured live StorageTile stores NO data entry - the prefab fallback is " +
                "guarded on having no state machine, so capture is unchanged");
        }
        finally
        {
            liveTile.DeleteObject();
        }
        for (int i = 0; i < 3; i++) yield return null;
    }

    /// <summary>
    /// Ends a preconfigure edit session the way the game does: drop the selection <b>first</b>,
    /// give the details screen a frame to notice, and only then destroy the temporary building.
    ///
    /// <para>Production never has to think about this. <c>UnderConstructionDataSettingHelper.CleanUp</c>
    /// is only reached from <c>HandleDeselection</c> - i.e. after the game has already moved the
    /// selection - or from the scheduled callback when the target is destroyed already. A case that
    /// calls <c>CleanUp</c> directly skips that and destroys a GameObject the details screen still
    /// points at.</para>
    ///
    /// <para>Klei's <c>SimpleInfoScreen.Refresh</c> then does
    /// <c>RefreshMovePanel(movePanel, selectedTarget)</c> with no null check, and that calls
    /// <c>targetEntity.GetComponent&lt;CancellableMove&gt;()</c> - which throws
    /// <c>NullReferenceException</c> on a destroyed object, once per frame, for the rest of the
    /// run. It went unnoticed for as long as these were the last cases in the list: the harness quit
    /// before another frame ran. <see cref="ExceptionSweep"/> could not see it either, because the
    /// stack has no mod frame in it - which is why that sweep now has a second, unattributed
    /// bucket.</para>
    ///
    /// <para>Has to be its own iterator: the deselect needs a frame to land before the destroy, and
    /// a <c>finally</c> block cannot <c>yield</c>.</para>
    /// </summary>
    private static IEnumerator EndPreconfigureEditing()
    {
        ///Deselect through the SAME path the mod selected with. StartEditingUnderConstructionData
        ///selects its temporary building with Game.Instance.Trigger(SelectObject, target), so
        ///SelectTool.Select(null) alone does not undo it - SimpleInfoScreen keeps its cached
        ///selectedTarget and NREs on it once the object is destroyed.
        if (Game.Instance != null)
            Game.Instance.Trigger((int)GameHashes.SelectObject, null);
        if (SelectTool.Instance != null)
            SelectTool.Instance.Select(null);

        for (int i = 0; i < 3; i++)
            yield return null;

        PreconfigureCleanUp();
    }

    /// <summary><c>UnderConstructionDataSettingHelper.CleanUp</c> is internal to the mod assembly.</summary>
    private static void PreconfigureCleanUp()
        => typeof(UnderConstructionDataSettingHelper)
            .GetMethod("CleanUp", BindingFlags.NonPublic | BindingFlags.Static)
            ?.Invoke(null, null);

    /// <summary>
    /// Runs <c>SameBuildingAlreadyFinishedInPlace</c> for <paramref name="def"/> at
    /// <paramref name="cell"/>, as though the blueprint entry were captured at
    /// <paramref name="orientation"/>.
    /// </summary>
    private static bool MatchesAt(BuildingDef def, int cell, Orientation orientation)
    {
        var config = new BuildingConfig
        {
            Offset = new Vector2I(0, 0),
            BuildingDef = def,
            BuildingDefId = def.PrefabID,
            Orientation = orientation,
        };
        foreach (var tag in FixtureBuilder.SelectElements(def))
            config.SelectedElements.Add(tag);

        var visual = new BuildingVisual(config, cell, BlueprintState.PlayerId_DefaultTilePreviews);
        try
        {
            return visual.SameBuildingAlreadyFinishedInPlace(cell, out _, false, includePlanned: true);
        }
        finally
        {
            visual.DestroyVisualizer();
        }
    }

    // ---- reconstruct carries settings to the replacement plan --------

    /// <summary>
    /// A material swap tears the finished building down and queues a new plan in its place.
    /// <c>ReconstructablePatches</c> stashes the building's settings in a prefix on
    /// <c>TryCommenceReconstruct</c> and re-applies them 0.1 s of game time later, to whatever
    /// occupies the cell by then - in real play the new plan.
    ///
    /// The reconstruct does not actually go ahead here: nothing delivers the swap material in a
    /// harness, so no plan appears and the cell still holds the original building. That does not
    /// matter for what is under test, because the patch is a *prefix* - the store-and-schedule
    /// runs either way, and the scheduled callback applies to the cell's occupant regardless of
    /// which building that is.
    ///
    /// The delay is <c>GameScheduler.Schedule(0.1f)</c>, not a next-frame callback, so this needs
    /// real game time to elapse rather than just frames.
    /// </summary>
    private static IEnumerator ReconstructReappliesSettings()
    {
        var source = Components.BuildingCompletes.Items.FirstOrDefault(b =>
            b != null && b.Def != null
            && b.TryGetComponent<Reconstructable>(out var r) && r.AllowReconstruct
            && b.TryGetComponent<Prioritizable>(out _));
        if (source == null)
        {
            Log?.Line("  REACHABILITY: no reconstructable, prioritizable building in the fixture - " +
                      "nothing to exercise the reconstruct path with");
            yield break;
        }

        int cell = Grid.PosToCell(source.gameObject);
        var def = source.Def;
        var reconstructable = source.GetComponent<Reconstructable>();
        var sourcePrio = source.GetComponent<Prioritizable>();

        ///a material the building is not already made of, or the swap is a no-op
        var currentElement = source.GetComponent<PrimaryElement>().Element.tag;
        var newElement = FixtureBuilder.SelectElements(def)
            .Concat(new[] { ElementLoader.FindElementByHash(SimHashes.SandStone).tag })
            .FirstOrDefault(t => t != currentElement);
        Log?.Line($"  reconstructing {def.PrefabID}@{Grid.CellToXY(cell)} from {currentElement} to {newElement}");

        var captured = new PrioritySetting(PriorityScreen.PriorityClass.high, 9);
        var originalPriority = sourcePrio.GetMasterPriority();
        sourcePrio.SetMasterPriority(captured);
        yield return null;

        try
        {
            reconstructable.RequestReconstruct(newElement);
            for (int i = 0; i < 5; i++) yield return null;

            ///TryCommenceReconstruct is what the mod patches; its signature is not an API this
            ///harness should pin, so build the arguments from what it declares
            var commence = typeof(Reconstructable).GetMethod("TryCommenceReconstruct",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.True(commence != null, "Reconstructable.TryCommenceReconstruct exists");
            var pars = commence!.GetParameters();
            Log?.Line($"  TryCommenceReconstruct({string.Join(", ", pars.Select(p => p.ParameterType.Name + " " + p.Name))})");
            var args = pars.Select(p => p.ParameterType == typeof(Tag)
                    ? (object?)newElement
                    : p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null)
                .ToArray();
            object? commenced = commence.Invoke(reconstructable, args);
            for (int i = 0; i < 5; i++) yield return null;

            ///The patch is a *prefix*, so the store-and-schedule runs whether or not the
            ///reconstruct itself goes ahead - and in a harness it does not, because no dupe
            ///delivers the swap material. That is fine for what is under test: the scheduled
            ///callback re-applies the stored data to whatever occupies the cell 0.1 s later,
            ///which in real play is the new plan and here is the original building.
            ///
            ///So overwrite the priority now. If the scheduled re-apply runs it will put the
            ///captured value back; if it never fires, the overwrite stands. That is a sharper
            ///control than reading a plan's default would have been.
            var overwritten = new PrioritySetting(PriorityScreen.PriorityClass.basic, 1);
            sourcePrio.SetMasterPriority(overwritten);
            var occupant = Grid.Objects[cell, (int)def.ObjectLayer];
            Log?.Line($"  TryCommenceReconstruct returned {commenced}; " +
                      $"cell now holds {(occupant == null ? "nothing" : occupant.name)}");

            var beforeClock = sourcePrio.GetMasterPriority();
            Log?.Line($"  clock stopped: priority={beforeClock.priority_class}/{beforeClock.priority_value} " +
                      $"(overwritten; captured was {captured.priority_class}/{captured.priority_value})");
            Assert.Equal(overwritten.priority_value, beforeClock.priority_value,
                "the overwrite took, so the scheduled re-apply is the only thing that can undo it");

            ///0.1s of game time, and a frame is worth ~0.014s here, so give it room
            yield return WithSimRunning(frames: 60);

            var afterClock = sourcePrio.GetMasterPriority();
            Log?.Line($"  after the clock ran: priority={afterClock.priority_class}/{afterClock.priority_value}");

            Assert.Equal(captured.priority_class, afterClock.priority_class,
                "the scheduled re-apply restored the stored priority class");
            Assert.Equal(captured.priority_value, afterClock.priority_value,
                "the scheduled re-apply restored the stored priority value");
        }
        finally
        {
            sourcePrio.SetMasterPriority(originalPriority);
        }
    }

    // ---- the preconfigure screen loads a plan's stored settings ------

    /// <summary>
    /// The "preconfigure this building" side-screen button spawns a throwaway copy of the finished
    /// building, and one frame later <c>UnderConstructionDataSettingHelper</c> copies the plan's
    /// stored settings onto it so the player edits something that already reflects the blueprint.
    /// That copy is the one-frame scheduler hop under test.
    ///
    /// This is the clock-running half. <see cref="PreconfigureWorksWhilePaused"/> is the other
    /// half, and the one that matters: reaching for <see cref="WithSimRunning"/> here is what let
    /// the paused bug through.
    /// </summary>
    private static IEnumerator PreconfigureLoadsStoredSettings()
    {
        var transferComponent = UnityEngine.Object
            .FindObjectsByType<UnderConstructionDataTransfer>(FindObjectsSortMode.None)
            .FirstOrDefault(t => t != null && t.GetStoredData().ContainsKey("Prioritizable"));
        var plan = transferComponent == null ? null : transferComponent.building;
        if (plan == null)
        {
            Log?.Line("  REACHABILITY: no queued building carrying stored Prioritizable data - " +
                      "earlier cases normally leave one behind");
            yield break;
        }

        var transfer = plan.GetComponent<UnderConstructionDataTransfer>();
        Log?.Line($"  editing the stored settings of {plan.Def.PrefabID}@{Grid.CellToXY(Grid.PosToCell(plan))}");

        bool tornDown = false;
        try
        {
            UnderConstructionDataSettingHelper.StartEditingUnderConstructionData(transfer);

            var beforeSelectable = UnderConstructionDataSettingHelper.TemporarySelectable;
            Log?.Line($"  clock stopped: temporary building selected={beforeSelectable != null}");

            yield return WithSimRunning(frames: 30);

            var temp = UnderConstructionDataSettingHelper.TemporarySelectable;
            Assert.True(temp != null, "the preconfigure flow spawned a temporary building to edit");
            Assert.True(temp!.TryGetComponent<Prioritizable>(out var tempPrio),
                "the temporary building is prioritizable");

            ///priority_value is nested inside the serialized Prioritizable, not a top-level
            ///property, so find it wherever it sits rather than assuming the shape
            var stored = JObject.Parse(transfer.GetStoredData()["Prioritizable"]);
            var valueToken = stored.Descendants().OfType<JProperty>()
                .FirstOrDefault(prop => prop.Name == "priority_value");
            Assert.True(valueToken != null, $"the stored Prioritizable carries a priority_value: {stored}");
            int storedValue = valueToken!.Value.Value<int>();
            var applied = tempPrio.GetMasterPriority();
            Log?.Line($"  temporary building priority={applied.priority_class}/{applied.priority_value}, " +
                      $"plan stored priority_value={storedValue}");

            Assert.Equal(storedValue, applied.priority_value,
                "the scheduled transfer put the plan's stored priority on the temporary building");

            ///Ordered teardown on the success path - deselect, let the screen notice, then destroy.
            yield return EndPreconfigureEditing();
            tornDown = true;
        }
        finally
        {
            ///Safety net for the failure path only: an assertion throws past the ordered teardown
            ///above, and leaking the temporary building into later cases would be worse than the
            ///exception storm a bare destroy causes on a run that is already red.
            if (!tornDown)
            {
                PreconfigureCleanUp();
                if (SelectTool.Instance != null)
                    SelectTool.Instance.Select(null);
            }
        }
    }

    /// <summary>
    /// The paused half of <see cref="PreconfigureLoadsStoredSettings"/>, and the regression that
    /// case structurally could not catch, because it runs the clock before it asserts.
    ///
    /// Reported 2026-09-16: preconfiguring a building from a blueprint pasted while the game was
    /// paused did nothing at all - no side screen - and unpausing minutes later threw a
    /// NullReferenceException out of <c>DetailsScreen.OnSelectObject</c>. The one-frame hop rode
    /// on <c>GameScheduler</c>, which is driven by the sim clock, so the callback sat in the queue
    /// for the whole pause and then fired against a temporary building that had long since been
    /// cleaned up. It rides <c>UIScheduler</c> now, which ticks on real time.
    ///
    /// So there is deliberately no <see cref="WithSimRunning"/> here: the colony is paused for the
    /// whole harness run, which is exactly the condition under test. Plain frames only.
    /// </summary>
    private static IEnumerator PreconfigureWorksWhilePaused()
    {
        var transferComponent = UnityEngine.Object
            .FindObjectsByType<UnderConstructionDataTransfer>(FindObjectsSortMode.None)
            .FirstOrDefault(t => t != null && t.GetStoredData().ContainsKey("Prioritizable"));
        var plan = transferComponent == null ? null : transferComponent.building;
        if (plan == null)
        {
            Log?.Line("  REACHABILITY: no queued building carrying stored Prioritizable data - " +
                      "earlier cases normally leave one behind");
            yield break;
        }

        ///if something ever starts the clock for the harness, this case silently stops testing
        ///the thing it exists to test, so say so rather than passing on a technicality
        Assert.True(Time.timeScale == 0f,
            $"the colony is paused, so the sim clock is stopped (timeScale={Time.timeScale})");

        var transfer = plan.GetComponent<UnderConstructionDataTransfer>();
        Log?.Line($"  editing the stored settings of {plan.Def.PrefabID}@{Grid.CellToXY(Grid.PosToCell(plan))} while paused");

        bool tornDown = false;
        try
        {
            UnderConstructionDataSettingHelper.StartEditingUnderConstructionData(transfer);

            ///the hop is one frame; give it a few, but never a running clock
            for (int i = 0; i < 10; i++)
                yield return null;

            var temp = UnderConstructionDataSettingHelper.TemporarySelectable;
            Assert.True(temp != null, "the preconfigure flow spawned a temporary building to edit");
            Assert.True(temp!.TryGetComponent<Prioritizable>(out var tempPrio),
                "the temporary building is prioritizable");

            ///priority_value is nested inside the serialized Prioritizable, not a top-level
            ///property, so find it wherever it sits rather than assuming the shape
            var stored = JObject.Parse(transfer.GetStoredData()["Prioritizable"]);
            var valueToken = stored.Descendants().OfType<JProperty>()
                .FirstOrDefault(prop => prop.Name == "priority_value");
            Assert.True(valueToken != null, $"the stored Prioritizable carries a priority_value: {stored}");
            int storedValue = valueToken!.Value.Value<int>();
            var applied = tempPrio.GetMasterPriority();
            Log?.Line($"  paused: temporary building priority={applied.priority_class}/{applied.priority_value}, " +
                      $"plan stored priority_value={storedValue}");

            Assert.Equal(storedValue, applied.priority_value,
                "the transfer arrived with the clock stopped, so the player sees the screen while paused");

            ///Ordered teardown on the success path - deselect, let the screen notice, then destroy.
            yield return EndPreconfigureEditing();
            tornDown = true;
        }
        finally
        {
            ///Safety net for the failure path only: an assertion throws past the ordered teardown
            ///above, and leaking the temporary building into later cases would be worse than the
            ///exception storm a bare destroy causes on a run that is already red.
            if (!tornDown)
            {
                PreconfigureCleanUp();
                if (SelectTool.Instance != null)
                    SelectTool.Instance.Select(null);
            }
        }
    }

    // ---- a GameScheduler-driven path delivers for real ---------------

    /// <summary>
    /// <see cref="ReplacementVisPlacesOnce"/> hand-delivers the placement callback, because the
    /// colony is paused for the whole run and <c>GameScheduler</c> never ticks. That leaves the
    /// scheduled delivery itself untested - the case proves the callback's *body* is right, not
    /// that anything ever calls it.
    ///
    /// This closes that: seat a vis, let game time run briefly, and require the building to be
    /// placed with no hand-delivered kick. It fails if `SeatVis` stops scheduling the check, or
    /// if the harness's pause behaviour changes such that scheduled work silently stops arriving.
    /// </summary>
    private static IEnumerator SeatingKickDelivers()
    {
        Blueprint bp = Snapshot(TileRowTopLeft(), TileRowBottomRight());
        var config = bp.BuildingConfigurations.FirstOrDefault(b => b.BuildingDef?.PrefabID == "Ladder");
        Assert.True(config != null, "captured the fixture's Ladder to queue as a replacement");

        var xy = Grid.CellToXY(AnchorCell);
        var cfg = ModConfig();
        bool savedTech = cfg.RequireConstructable_Tech, savedMat = cfg.RequireConstructable_Material;
        cfg.RequireConstructable_Tech = false;
        cfg.RequireConstructable_Material = false;
        bool savedInstant = DebugHandler.InstantBuildMode;
        DebugHandler.InstantBuildMode = false;

        ReplacementVis? vis = null;
        try
        {
            int cell = Grid.XYToCell(xy.x + 7, xy.y - 20);
            yield return ClearCell(cell);
            AssertCellEmpty(cell, "scheduled-kick");

            var before = Constructables();
            var spawn = SpawnSeatedVis(config!, cell);
            yield return spawn;
            vis = spawn.Vis;

            ///paused, the scheduled check cannot arrive - so nothing should have happened yet
            Assert.True(!GetBool(vis, "placementSuccessful"),
                "nothing is placed while the clock is stopped, so the run below is what delivers it");

            bool firedWhileRunning = false;
            GameScheduler.Instance.ScheduleNextFrame("bpi-harness-kick-probe", _ => firedWhileRunning = true);

            float clockBefore = GameClock.Instance.GetTime();
            yield return WithSimRunning(frames: 30);
            Log?.Line($"  game clock advanced {GameClock.Instance.GetTime() - clockBefore:F2}s");

            var placed = Constructables().Where(c => !before.Contains(c))
                .Where(c => Grid.PosToCell(c.gameObject) == cell).ToList();
            Log?.Line($"  with the sim running: GameScheduler fired={firedWhileRunning}, " +
                      $"{placed.Count} build order(s) at {Grid.CellToXY(cell)}, " +
                      $"latched={GetBool(vis!, "placementSuccessful")}");

            Assert.True(firedWhileRunning,
                "GameScheduler delivers once game time runs - if this fails the blind spot is back");
            Assert.Equal(1, placed.Count,
                "the vis's own scheduled seating check placed the building, with no hand-delivered callback");
            ///timeScale, not IsPaused: the speed screen reports paused the whole time, so it says
            ///nothing about whether the clock was left running for the cases that follow
            Assert.Equal(0f, Time.timeScale,
                "the clock is stopped again, so later cases are unaffected");
        }
        finally
        {
            DestroyVis(vis);
            DebugHandler.InstantBuildMode = savedInstant;
            cfg.RequireConstructable_Tech = savedTech;
            cfg.RequireConstructable_Material = savedMat;
        }
    }

    /// <summary>
    /// Records why <see cref="Kick"/> has to deliver the placement callback by hand: whether the
    /// sim is paused, and whether a freshly scheduled <c>GameScheduler</c> callback lands at all.
    /// <c>UIScheduler</c> is the control - it runs on real time, so if it fires and
    /// <c>GameScheduler</c> does not, the cause is game time being stopped rather than the
    /// scheduling machinery being broken.
    /// </summary>
    private static IEnumerator SchedulerProbe()
    {
        bool gameFired = false, uiFired = false;
        GameScheduler.Instance.ScheduleNextFrame("bpi-harness-probe-game", _ => gameFired = true);
        UIScheduler.Instance.ScheduleNextFrame("bpi-harness-probe-ui", _ => uiFired = true);

        for (int i = 0; i < 10; i++)
            yield return null;

        Log?.Line($"  scheduler probe: simPaused={SpeedControlScreen.Instance?.IsPaused}, " +
                  $"timeScale={Time.timeScale}, " +
                  $"GameScheduler fired={gameFired}, UIScheduler fired={uiFired}");
    }

    /// <summary>
    /// Lets game time run for <paramref name="frames"/> frames, then puts the clock back where it
    /// was. The colony is paused for the whole harness run, so <c>GameScheduler</c> callbacks never
    /// land otherwise - and four paths in this mod are driven by them
    /// (<c>ReplacementVis</c> seating, the delayed settings application in
    /// <c>DataTransferPatches</c>, <c>ReconstructablePatches</c>, and
    /// <c>UnderConstructionDataSettingHelper</c>).
    ///
    /// Keep the window short and restore the clock in a <c>finally</c>: with the sim running,
    /// dupes act, temperatures move and debris settles, and every later case inherits whatever
    /// that did to the colony.
    ///
    /// <c>Time.timeScale</c> is the lever, not the speed UI. Measured: with the harness driving,
    /// <c>SpeedControlScreen.Unpause(false)</c> and <c>SetSpeed(0)</c> are both no-ops -
    /// <c>IsPaused</c> stays true and <c>timeScale</c> stays 0. Since <c>GameScheduler</c> ticks on
    /// scaled time (which is exactly why <c>UIScheduler</c> fires during a run and it does not),
    /// setting <c>timeScale</c> is enough to make scheduled work land. The speed screen keeps
    /// reporting paused throughout, so assert on <c>timeScale</c> rather than on <c>IsPaused</c>.
    /// </summary>
    private static IEnumerator WithSimRunning(int frames)
    {
        float savedScale = Time.timeScale;
        Time.timeScale = 1f;
        try
        {
            for (int i = 0; i < frames; i++)
                yield return null;
        }
        finally
        {
            Time.timeScale = savedScale;
        }
    }

    /// <summary>
    /// Delivers the <c>OnPreoccupiedCellChanged</c> callback the scene partitioner would deliver,
    /// then lets the one-frame <c>DelayedPlacementCheck</c> coroutine run to completion.
    /// </summary>
    /// <summary>Set while <see cref="ReentrancyProbe"/> runs; the patches below ignore every other vis.</summary>
    private static ReplacementVis? reentryTarget;
    private static int finalizeCount;
    private static bool reentryFired;

    /// <summary>Counts entries to <c>FinalizePlacementCheck</c> for the vis under test.</summary>
    private static void CountFinalize(ReplacementVis __instance)
    {
        if (ReferenceEquals(__instance, reentryTarget))
            finalizeCount++;
    }

    /// <summary>
    /// Stands in for a synchronous partitioner callback arriving mid-check. Fires once, from inside
    /// <c>UpdateVisualState</c>, which runs within <c>FinalizePlacementCheck</c> on the failure
    /// branch - the same stack a real <c>Grid.Objects</c> write would deliver on.
    /// </summary>
    private static void ReenterFromVisualState(ReplacementVis __instance)
    {
        if (reentryFired || !ReferenceEquals(__instance, reentryTarget))
            return;
        reentryFired = true;

        typeof(ReplacementVis)
            .GetMethod("OnPreoccupiedCellChanged", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(__instance, new object?[] { null });
    }

    /// <summary>
    /// Drives one check on <paramref name="vis"/> with a re-entrant callback delivered from inside
    /// it, and asserts the check does not run twice. See the call site for why this models the real
    /// mechanism rather than waiting for it.
    /// </summary>
    private static IEnumerator ReentrancyProbe(ReplacementVis vis)
    {
        var harmony = HarnessGate.HarmonyInstance;
        if (harmony == null)
        {
            Log?.Line("  REACHABILITY: no Harmony instance, re-entrancy guard not exercised");
            yield break;
        }

        var finalize = AccessTools.Method(typeof(ReplacementVis), "FinalizePlacementCheck");
        var visualState = AccessTools.Method(typeof(ReplacementVis), "UpdateVisualState");
        if (finalize == null || visualState == null)
        {
            Log?.Line("  REACHABILITY: FinalizePlacementCheck/UpdateVisualState not found, " +
                      "re-entrancy guard not exercised");
            yield break;
        }

        reentryTarget = vis;
        finalizeCount = 0;
        reentryFired = false;

        harmony.Patch(finalize, prefix: new HarmonyMethod(AccessTools.Method(
            typeof(HarnessCases), nameof(CountFinalize))));
        harmony.Patch(visualState, prefix: new HarmonyMethod(AccessTools.Method(
            typeof(HarnessCases), nameof(ReenterFromVisualState))));

        try
        {
            yield return Kick(vis);
            ///a second check would land the frame after the re-entrant callback; Kick already
            ///waits ten, which is more than enough for one to show up
        }
        finally
        {
            harmony.Unpatch(finalize, AccessTools.Method(typeof(HarnessCases), nameof(CountFinalize)));
            harmony.Unpatch(visualState, AccessTools.Method(typeof(HarnessCases), nameof(ReenterFromVisualState)));
            reentryTarget = null;
        }

        Log?.Line($"  re-entrancy: callback delivered mid-check={reentryFired}, " +
                  $"FinalizePlacementCheck ran {finalizeCount}x (expected 1)");

        Assert.True(reentryFired,
            "the probe actually reached UpdateVisualState - otherwise the count below proves nothing");
        Assert.Equal(1, finalizeCount,
            "a callback arriving DURING a placement check is refused - the guard stays latched " +
            "until the check finishes, so no second check runs against half-finished state");
    }

    private static IEnumerator Kick(ReplacementVis vis)
    {
        typeof(ReplacementVis)
            .GetMethod("OnPreoccupiedCellChanged", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(vis, new object?[] { null });

        for (int i = 0; i < 10; i++)
            yield return null;
    }

    /// <summary>
    /// The cells this case uses must start free of buildings, or it would be measuring another
    /// case's leftovers instead of its own placement. Only buildings count: digging leaves loose
    /// debris on the <c>Pickupables</c> layer, which does not obstruct a placement.
    /// </summary>
    private static void AssertCellEmpty(int cell, string which)
    {
        for (int layer = 0; layer < (int)ObjectLayer.NumLayers; layer++)
        {
            var occupant = Grid.Objects[cell, layer];
            if (occupant == null || !occupant.TryGetComponent<Building>(out var occupying))
                continue;

            Assert.True(false,
                $"{which} cell {Grid.CellToXY(cell)} starts free of buildings " +
                $"(found {occupying.Def?.PrefabID ?? "?"} on layer {(ObjectLayer)layer})");
        }
    }

    /// <summary>Digs and reveals a small area around <paramref name="cell"/> so a placement there
    /// is not fighting terrain or fog of war.</summary>
    private static IEnumerator ClearCell(int cell)
    {
        var xy = Grid.CellToXY(cell);
        var region = new List<int>();
        for (int dx = -2; dx <= 2; dx++)
            for (int dy = -2; dy <= 2; dy++)
            {
                int c = Grid.XYToCell(xy.x + dx, xy.y + dy);
                if (Grid.IsValidCell(c))
                    region.Add(c);
            }
        foreach (int c in region)
        {
            if (Grid.IsSolidCell(c))
                SimMessages.Dig(c, skipEvent: true);
            Grid.Reveal(c, byte.MaxValue, forceReveal: true);
        }
        float waited = 0f;
        while (region.Any(Grid.IsSolidCell) && waited < 20f)
        {
            waited += Time.unscaledDeltaTime;
            yield return null;
        }
    }

    ///BPV2_BuildingReplacer is ReplacementVisualizerMultiEntityConfig.BUILDING_ID - that type is
    ///internal to the mod, so the id is spelled out here; Assets.GetPrefab fails loudly on a rename.
    private const string BuildingReplacerPrefabId = "BPV2_BuildingReplacer";

    private static SeatedVisSpawn SpawnSeatedVis(BuildingConfig config, int cell) => new(config, cell);

    /// <summary>
    /// Spawns a replacement vis and does not hand it back until it is actually seated.
    /// <c>OnSpawn</c> - and so <c>SeatVis</c> - lands a frame or two after <c>SetActive</c> rather
    /// than inside it, so a caller reading vis state straight after spawning reads it before
    /// <c>SeatVis</c> has run: <c>scheduledSpawnCheck</c> is still <c>default</c> and
    /// <c>occupiedCells</c> is still empty. Waiting here rather than at each call site means a
    /// later case cannot get that wrong.
    ///
    /// Yield it, then read <see cref="Vis"/>.
    /// </summary>
    private sealed class SeatedVisSpawn : IEnumerator
    {
        private readonly IEnumerator steps;

        /// <summary>The seated vis. Only valid once this has been yielded to completion.</summary>
        public ReplacementVis Vis = null!;

        public SeatedVisSpawn(BuildingConfig config, int cell) => steps = Run(config, cell);

        private IEnumerator Run(BuildingConfig config, int cell)
        {
            var prefab = Assets.GetPrefab(BuildingReplacerPrefabId);
            Assert.True(prefab != null, $"resolved the replacement-vis prefab '{BuildingReplacerPrefabId}'");

            var go = Util.KInstantiate(prefab, Grid.CellToPosCBC(cell, config.BuildingDef!.SceneLayer));
            var vis = go.GetComponent<ReplacementVis>();
            Assert.True(vis != null, "the replacement-vis prefab carries a ReplacementVis");

            vis!.Configure(cell, config, Orientation.Neutral, config.SelectedElements, flags: -1);
            go.SetActive(true);
            Vis = vis;

            float waited = 0f;
            while (!IsSeated(vis) && waited < 5f)
            {
                waited += Time.unscaledDeltaTime;
                yield return null;
            }
            Assert.True(IsSeated(vis), $"the vis seated within {waited:F1}s of SetActive");
        }

        ///SeatVis fills occupiedCells via DetermineOccupiedCells, so a non-empty set is the signal
        ///that OnSpawn has been through. HashSet<T> does NOT implement the non-generic
        ///ICollection, so match the concrete type - a pattern on ICollection never matches and
        ///this silently never seats.
        private static bool IsSeated(ReplacementVis vis) =>
            GetPrivate(vis, "occupiedCells") is HashSet<int> cells && cells.Count > 0;

        public bool MoveNext() => steps.MoveNext();
        public object Current => steps.Current;
        public void Reset() => steps.Reset();
    }

    private static void DestroyVis(ReplacementVis? vis)
    {
        if (vis == null)
            return;
        typeof(ReplacementVis)
            .GetMethod("DestroySelf", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(vis, null);
    }

    private static object? GetPrivate(ReplacementVis vis, string field) =>
        typeof(ReplacementVis)
            .GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(vis);

    private static bool GetBool(ReplacementVis vis, string field) => (bool)GetPrivate(vis, field)!;

    private static void SetBool(ReplacementVis vis, string field, bool value) =>
        typeof(ReplacementVis)
            .GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(vis, value);

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

    // ---- #72 (b): no colour is computed for a preview that can't show it --------------

    // ModAssets.GetVisualizerType is public but on an internal class - reach it by reflection.
    private static readonly MethodInfo GetVisualizerTypeMethod =
        typeof(Blueprint).Assembly
            .GetType("BlueprintsV2.ModAssets")!
            .GetMethod("GetVisualizerType", BindingFlags.Public | BindingFlags.Static)!;

    /// <summary>
    /// Settles #72 (b). The base <c>BuildingVisual.ApplyColorIfChanged</c> computes
    /// <c>GetVisualizerColor</c> and throws it away when the preview has no
    /// <c>KBatchedAnimController</c>. Upstream skips that computation; porting the skip only pays if
    /// such a visual reaches the base method. A preview without an anim controller is exactly
    /// what <c>GetSharedPlaceholder</c> keys on. <c>TileVisual</c> overrides the method and uses
    /// the colour for the block-tile atlas. So the waste needs an anim-less preview on a def that
    /// routes to <c>BuildingVisual</c> or <c>UtilityVisual</c>, which don't override it. This walks
    /// every loaded def, including other enabled mods', and asserts there are none.
    /// </summary>
    private static IEnumerator AnimLessPreviewsAreAllTileVisuals()
    {
        var animLessByType = new Dictionary<VisualizerType, List<string>>();
        int total = 0;
        foreach (var def in Assets.BuildingDefs)
        {
            if (def == null || def.BuildingPreview == null || def.BuildingComplete == null)
                continue;
            total++;
            if (def.BuildingPreview.TryGetComponent<KBatchedAnimController>(out _))
                continue;

            var type = (VisualizerType)GetVisualizerTypeMethod.Invoke(null, new object[] { def })!;
            if (!animLessByType.TryGetValue(type, out var ids))
                animLessByType[type] = ids = new List<string>();
            ids.Add(def.PrefabID);
        }

        foreach (var kv in animLessByType)
            Log?.Line($"  anim-less previews routed to {kv.Key}: {kv.Value.Count} " +
                      $"[{string.Join(", ", kv.Value.OrderBy(x => x).Take(15))}{(kv.Value.Count > 15 ? ", ..." : "")}]");
        Log?.Line($"  defs scanned: {total}");

        var wasted = animLessByType
            .Where(kv => kv.Key != VisualizerType.TILE)
            .SelectMany(kv => kv.Value.Select(id => $"{id} ({kv.Key})"))
            .OrderBy(x => x)
            .ToList();
        Assert.True(wasted.Count == 0,
            "no anim-less preview reaches the base ApplyColorIfChanged, got: " + string.Join(", ", wasted));
        yield break;
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
