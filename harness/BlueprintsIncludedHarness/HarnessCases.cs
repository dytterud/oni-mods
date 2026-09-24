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
        new HarnessCase("paste-key-takes-a-blueprint-from-the-clipboard", PasteFromClipboard),
        new HarnessCase("instabuild-spawns-below-melting-point", InstabuildSpawnTemperature),
        new HarnessCase("note-visibility-toggle-hides-notes", NoteVisibilityToggle),
        new HarnessCase("note-opacity-follows-the-setting", NoteOpacityFollowsTheSetting),
        new HarnessCase("planned-buildings-match-for-data-transfer", PlannedBuildingMatch),
        new HarnessCase("dig-placer-preview-filter-hides-digs", DigPlacerPreviewFilter),
        new HarnessCase("conduit-flags-ignore-captured-orientation", ConduitFlagsIgnoreCapturedOrientation),
        new HarnessCase("tile-seating-map-tracks-the-drag", TileSeatingMapTracksTheDrag),
        new HarnessCase("preview-follows-the-cursor", PreviewFollowsTheCursor),
        new HarnessCase("mod-component-lookup-resolves-and-caches", ModComponentLookupResolvesAndCaches),
        new HarnessCase("replacement-vis-places-once-per-cell", ReplacementVisPlacesOnce),
        new HarnessCase("replacement-vis-claims-its-port-cells", ReplacementVisClaimsPortCells),
        new HarnessCase("rocket-modules-stack-on-previewed-hardpoints", RocketModulesStack),
        new HarnessCase("scheduled-seating-kick-delivers-when-time-runs", SeatingKickDelivers),
        new HarnessCase("completed-construction-applies-stored-settings", CompletionAppliesStoredSettings),
        new HarnessCase("rotation-counts-in-same-building-detection", RotationConsideredForSameBuilding),
        new HarnessCase("reconstruct-reapplies-stored-settings", ReconstructReappliesSettings),
        new HarnessCase("preconfigure-screen-loads-the-plan's-settings", PreconfigureLoadsStoredSettings),
        new HarnessCase("preconfigure-leaves-the-world-border-intact", PreconfigureLeavesTheBorderIntact),
        new HarnessCase("preconfigure-screen-opens-while-the-game-is-paused", PreconfigureWorksWhilePaused),
        new HarnessCase("preconfigure-button-latch-fails-open", PreconfigureButtonLatchFailsOpen),
        new HarnessCase("preconfigure-seed-can-be-picked-and-kept", PreconfigureSeedCanBePickedAndKept),
        new HarnessCase("preconfigure-button-shows-for-smi-backed-buildings", PreconfigureButtonShowsForSmiBackedBuildings),
        new HarnessCase("building-data-api-survives-a-dead-gameobject", BuildingDataApiSurvivesDeadGameObject),
        new HarnessCase("anim-less-previews-are-all-tile-visuals", AnimLessPreviewsAreAllTileVisuals),
        new HarnessCase("note-side-screen-selection-does-not-write-back", NoteSideScreenSelection),
        new HarnessCase("snapshot-save-and-export-buttons-are-wired", SnapshotSaveAndExportButtons),
        new HarnessCase("grid-snap-row-is-built-and-wired", GridSnapRowIsWired),
        new HarnessCase("grid-snap-drag-places-copies-edge-to-edge", GridSnapDragPlacesCopies),
        new HarnessCase("note-toggle-tooltip-follows-a-rebind", NoteToggleTooltipFollowsARebind),
        new HarnessCase("backwall-building-needs-backwall-under-every-cell", BackwallCoversEveryCell),
        new HarnessCase("rotated-occupancy-follows-the-rotation", RotatedOccupancyFollowsTheRotation),
        new HarnessCase("backwall-building-is-accepted-over-a-real-back-wall", BackwallOverRealBackwall),
        new HarnessCase("game-sprites-replace-the-bundle-art", GameSpritesReplaceBundleArt),
        new HarnessCase("bundle-dialogs-open-and-bind", BundleDialogsOpenAndBind),
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

    // ---- #71: notes fade to the configured opacity ---------------------------------

    /// <summary>
    /// Note opacity is applied by scaling the alpha of the note's own material colour - our notes
    /// already draw through Klei's transparent placer shader, so no prefab rebuild was needed
    /// (upstream's cc28b8b swaps both note prefabs onto a SpriteRenderer to achieve the same
    /// thing). Asserts a note spawns at the configured opacity, and that retinting it - which both
    /// note types do whenever their element or symbol changes - does not undo the fade.
    /// </summary>
    private static IEnumerator NoteOpacityFollowsTheSetting()
    {
        var xy = Grid.CellToXY(AnchorCell);
        int cell = Grid.XYToCell(xy.x - 3, xy.y - 3);
        if (Grid.IsSolidCell(cell))
        {
            SimMessages.Dig(cell, skipEvent: true);
            for (int i = 0; i < 30 && Grid.IsSolidCell(cell); i++) yield return null;
        }

        var cfg = ModConfig();
        float savedOpacity = cfg.NoteOpacity;
        BlueprintNote? note = null;
        try
        {
            ///full opacity first, to read the alpha the material ships with
            cfg.NoteOpacity = 1f;
            note = ElementNote.Create(cell, SimHashes.Oxygen, amount: 100f, temperature: 296f, seat: true);
            Assert.True(note != null, "created a seated element note");
            for (int i = 0; i < 5; i++) yield return null;

            var renderer = note!.GetComponentInChildren<MeshRenderer>();
            Assert.True(renderer != null, "the note draws through a MeshRenderer");
            float baseAlpha = renderer!.material.color.a;
            Log?.Line($"  shader={renderer.material.shader?.name}, alpha at opacity 1: {baseAlpha:0.000}");
            Assert.True(baseAlpha > 0f, "the note's own material carries an alpha to scale");

            BlueprintNote.ClearExistingNote(cell);
            note = null;
            for (int i = 0; i < 3; i++) yield return null;

            ///and now faded
            cfg.NoteOpacity = 0.4f;
            note = ElementNote.Create(cell, SimHashes.Oxygen, amount: 100f, temperature: 296f, seat: true);
            for (int i = 0; i < 5; i++) yield return null;
            renderer = note!.GetComponentInChildren<MeshRenderer>();
            float fadedAlpha = renderer!.material.color.a;
            Log?.Line($"  alpha at opacity 0.4: {fadedAlpha:0.000} (expected {baseAlpha * 0.4f:0.000})");
            Assert.True(Mathf.Abs(fadedAlpha - baseAlpha * 0.4f) < 0.01f,
                "a note spawns at the configured opacity");

            ///retint: changing the element rewrites the material colour, and the fade must survive
            ((ElementNote)note).SetInfo(SimHashes.CrudeOil, 200f, 300f);
            ((ElementNote)note).SetElementTint();
            for (int i = 0; i < 3; i++) yield return null;
            float afterRetint = renderer.material.color.a;
            Log?.Line($"  alpha after a retint: {afterRetint:0.000}");
            Assert.True(Mathf.Abs(afterRetint - baseAlpha * 0.4f) < 0.01f,
                "retinting the note keeps it faded");

            yield return Screenshot.Capture("note-opacity-40", Log);
        }
        finally
        {
            cfg.NoteOpacity = savedOpacity;
            BlueprintNote.ClearExistingNote(cell);
        }
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

            ///and directly: nothing is left subscribed to the static event. A destroyed note still
            ///on it is the shape behind upstream's issue #362 - there it fires on a nulled
            ///renderer, here it would fire on a destroyed one. Either way the subscriber list is
            ///what says whether the unsubscribe held.
            var subscribers = ((Delegate?)AccessTools
                .Field(typeof(BlueprintNote), "OnNoteVisibilityChanged").GetValue(null))
                ?.GetInvocationList().Length ?? 0;
            Log?.Line($"  subscribers left on the visibility event: {subscribers}");
            Assert.Equal(0, subscribers, "a deleted note leaves nothing subscribed to the static toggle event");
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
    /// <summary>
    /// #80: the preconfigure flow spawns a temporary building at the world's bottom-left corner -
    /// inside the Unobtanium border wall - and destroys it again when the player is done. For a
    /// building that occupies cells (a tile, a door) that means the border cells are replaced with
    /// the temporary building's own element while it exists. Upstream now refills them afterwards,
    /// which implies the teardown leaves a hole in the wall.
    ///
    /// Records the spawn footprint's elements and solidity either side of a real edit session, so
    /// the claim is settled by measurement rather than by reading upstream's diff.
    /// </summary>
    private static IEnumerator PreconfigureLeavesTheBorderIntact()
    {
        var transferComponent = UnityEngine.Object
            .FindObjectsByType<UnderConstructionDataTransfer>(FindObjectsSortMode.None)
            .FirstOrDefault(t => t != null && t.GetStoredData().ContainsKey("Prioritizable"));
        var plan = transferComponent == null ? null : transferComponent.building;
        if (plan == null)
        {
            Log?.Line("  REACHABILITY: no queued building carrying stored data - earlier cases " +
                      "normally leave one behind");
            yield break;
        }

        var def = plan.Def;
        bool occupiesCells = def.BuildingComplete != null
            && def.BuildingComplete.GetComponent<SimCellOccupier>() != null;
        Log?.Line($"  plan {def.PrefabID} {def.WidthInCells}x{def.HeightInCells}, occupies cells: {occupiesCells}");

        ///the spawn cell the helper computes: the world's bottom-left corner, nudged right by half
        ///the building's width
        var world = plan.GetMyWorld();
        int spawnCell = Grid.XYToCell(world.WorldOffset.X, world.WorldOffset.Y)
            + Mathf.CeilToInt(def.WidthInCells / 2f);

        var footprint = new List<int>();
        def.RunOnArea(spawnCell, Orientation.Neutral, c =>
        {
            if (Grid.IsValidCell(c))
                footprint.Add(c);
        });
        ///The fixture's corner happens to be open vacuum, and the hole can only show where the
        ///border wall actually is - so put Unobtanium there first, exactly as a normal map's
        ///border row has it, and restore whatever was there afterwards.
        var original = footprint.ToDictionary(c => c, c => (Grid.Element[c]?.id ?? SimHashes.Void,
            Grid.Mass[c], Grid.Temperature[c]));
        foreach (int c in footprint)
            SimMessages.ReplaceElement(c, SimHashes.Unobtanium, CellEventLogger.Instance.DebugTool,
                1000f, 294.15f);
        yield return WithSimRunning(frames: 10);

        var before = footprint.ToDictionary(c => c, c => (Grid.Element[c]?.id ?? SimHashes.Void, Grid.Solid[c]));
        Log?.Line("  border cells before: " + string.Join(", ",
            before.Select(kv => $"{Grid.CellToXY(kv.Key)}={kv.Value.Item1}{(kv.Value.Item2 ? " solid" : string.Empty)}")));
        if (before.Values.Any(v => v.Item1 != SimHashes.Unobtanium))
        {
            Log?.Line("  REACHABILITY: could not make the spawn cells Unobtanium, so the border " +
                      "hole cannot be observed here");
            foreach (var kv in original)
                SimMessages.ReplaceElement(kv.Key, kv.Value.Item1, CellEventLogger.Instance.DebugTool,
                    kv.Value.Item2, kv.Value.Item3);
            yield break;
        }

        bool tornDown = false;
        try
        {
            UnderConstructionDataSettingHelper.StartEditingUnderConstructionData(
                plan.GetComponent<UnderConstructionDataTransfer>());
            yield return WithSimRunning(frames: 30);

            var during = footprint.ToDictionary(c => c, c => (Grid.Element[c]?.id ?? SimHashes.Void, Grid.Solid[c]));
            Log?.Line("  border cells while editing: " + string.Join(", ",
                during.Select(kv => $"{Grid.CellToXY(kv.Key)}={kv.Value.Item1}{(kv.Value.Item2 ? " solid" : string.Empty)}")));

            yield return EndPreconfigureEditing();
            tornDown = true;
            yield return WithSimRunning(frames: 30);

            var after = footprint.ToDictionary(c => c, c => (Grid.Element[c]?.id ?? SimHashes.Void, Grid.Solid[c]));
            Log?.Line("  border cells after: " + string.Join(", ",
                after.Select(kv => $"{Grid.CellToXY(kv.Key)}={kv.Value.Item1}{(kv.Value.Item2 ? " solid" : string.Empty)}")));

            var changed = footprint.Where(c => !before[c].Equals(after[c])).ToList();
            if (changed.Count > 0)
                Log?.Line("  CHANGED: " + string.Join(", ", changed.Select(c =>
                    $"{Grid.CellToXY(c)} {before[c].Item1}->{after[c].Item1}, solid {before[c].Item2}->{after[c].Item2}")));

            Assert.True(changed.Count == 0,
                "the preconfigure temporary building leaves the world border as it found it");
        }
        finally
        {
            if (!tornDown)
                PreconfigureCleanUp();
            foreach (var kv in original)
                SimMessages.ReplaceElement(kv.Key, kv.Value.Item1, CellEventLogger.Instance.DebugTool,
                    kv.Value.Item2, kv.Value.Item3);
        }
    }

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

    /// <summary><c>UnderConstructionDataSettingHelper.ResetSessionState</c> is internal too.</summary>
    private static void PreconfigureResetSessionState()
        => typeof(UnderConstructionDataSettingHelper)
            .GetMethod("ResetSessionState", BindingFlags.NonPublic | BindingFlags.Static)
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

    // ---- the preconfigure button's latch has to fail open -------------

    /// <summary>
    /// #109: <c>UnderConstructionDataTransfer.SelectButtonUnlocked</c> is a plain <c>static</c>
    /// that <c>OnSidescreenButtonPressed</c> takes and only <c>CleanUp</c> gives back - and
    /// <c>CleanUp</c> is reached only from a <c>SelectObject</c> event. Nothing re-initialises it
    /// on colony load, so once it is stuck every planned building in every colony loaded
    /// afterwards shows Preconfigure greyed out until the game is restarted. That is the shape of
    /// the upstream report: not one building, <i>any</i> building button.
    ///
    /// <para>Two halves, because the latch has two ways to come back:</para>
    /// <list type="number">
    /// <item>a session that ends normally hands it back - <c>SidescreenButtonInteractable()</c> is
    /// false while the screen is open and true again once it closes;</item>
    /// <item>a session that is abandoned hands it back too, through the teardown reset that
    /// <c>Game.DestroyInstances</c> now runs.</item>
    /// </list>
    ///
    /// <para>The second half sets the latch directly rather than spawning a building and dropping
    /// it: abandoning a live session for real means quitting to the main menu, which ends the
    /// harness run, and destroying the temporary building here without the deselect first is the
    /// <c>SimpleInfoScreen</c> NRE storm documented on <see cref="EndPreconfigureEditing"/>. What
    /// it cannot prove from inside one run is that <c>Game.DestroyInstances</c> calls the reset -
    /// only that the reset releases the latch when it is called.</para>
    /// </summary>
    private static IEnumerator PreconfigureButtonLatchFailsOpen()
    {
        var transferComponent = UnityEngine.Object
            .FindObjectsByType<UnderConstructionDataTransfer>(FindObjectsSortMode.None)
            .FirstOrDefault(t => t != null && t.GetStoredData().ContainsKey("Prioritizable"));
        var plan = transferComponent == null ? null : transferComponent.building;
        if (plan == null)
        {
            Log?.Line("  REACHABILITY: no queued building carrying stored data - earlier cases " +
                      "normally leave one behind");
            yield break;
        }

        var transfer = plan.GetComponent<UnderConstructionDataTransfer>();

        ///an earlier case that leaked the latch would make this one pass for the wrong reason
        Assert.True(transfer.SidescreenButtonInteractable(),
            "the Preconfigure button starts out interactable");

        bool tornDown = false;
        try
        {
            ///through the button itself, not StartEditingUnderConstructionData - the latch is the
            ///button's, and the press path is what #109 made exception-safe
            transfer.OnSidescreenButtonPressed();
            for (int i = 0; i < 10; i++)
                yield return null;

            Assert.True(!transfer.SidescreenButtonInteractable(),
                "the button greys out while a preconfigure session is open");

            yield return EndPreconfigureEditing();
            tornDown = true;
            for (int i = 0; i < 3; i++)
                yield return null;

            Assert.True(transfer.SidescreenButtonInteractable(),
                "a session that ends normally hands the latch back");
        }
        finally
        {
            if (!tornDown)
            {
                PreconfigureCleanUp();
                if (SelectTool.Instance != null)
                    SelectTool.Instance.Select(null);
            }
        }

        ///second half: the colony goes away with the latch taken
        UnderConstructionDataTransfer.SelectButtonUnlocked = false;
        Assert.True(!transfer.SidescreenButtonInteractable(),
            "a taken latch really does grey the button out, so the reset below is testing something");

        PreconfigureResetSessionState();

        Log?.Line($"  after the teardown reset: unlocked={UnderConstructionDataTransfer.SelectButtonUnlocked}, " +
                  $"temporary selectable={(UnderConstructionDataSettingHelper.TemporarySelectable == null ? "null" : "still set")}");

        Assert.True(transfer.SidescreenButtonInteractable(),
            "an abandoned session does not outlive the colony - the latch fails open on teardown");
        Assert.True(UnderConstructionDataSettingHelper.TemporarySelectable == null,
            "the teardown reset drops its reference to the temporary building too");

        yield break;
    }

    // ---- #110: can the seed picker pick a seed on a preconfigured planter? ----

    /// <summary>
    /// #110: upstream was told the Hydroponic Farm "cannot select seeds and cannot perform
    /// planting" during a preconfigure session. Reachability was never established by reading -
    /// <c>ReceptacleSideScreen</c> is Klei code and <c>lib/</c> holds reference assemblies with no
    /// method bodies - so this walks the screen the way a player does and measures each step:
    /// does it take the temporary building as a target, does the picker list rows, is a row
    /// selectable, does clicking it register a choice, does confirming it reach the receptacle,
    /// and does the choice survive the session onto the plan.
    ///
    /// <para>A <b>finished</b> Farm Tile built in the colony and selected normally is the control,
    /// and it runs last on purpose: <c>PlanterSideScreen</c> is not instantiated until something
    /// has selected a planter, so a control that ran first would find no screen at all. If the
    /// control gets through and the preconfigured ones do not, the fault is in the preconfigure
    /// machinery; if only the Hydroponic Farm stalls, it is in the building.</para>
    ///
    /// <para>Most of this is <c>protected</c> or <c>private</c> on Klei's screen, so it goes
    /// through reflection; the harness is not in that assembly. <c>CreateOrder</c> stands in for
    /// pressing the confirm button, which is what that button calls.</para>
    /// </summary>
    private static IEnumerator PreconfigureSeedCanBePickedAndKept()
    {
        var readings = new List<PickerReading>();

        foreach (string prefabId in new[] { "FarmTile", "HydroponicFarm" })
        {
            var def = Assets.GetBuildingDef(prefabId);
            if (def == null)
            {
                Log?.Line($"  REACHABILITY: no BuildingDef for {prefabId} in this install");
                continue;
            }

            var probe = new ProbeSeedPicker(def);
            yield return probe;
            if (probe.Reading.Reached)
                readings.Add(probe.Reading);
        }

        ///the control, last, so PlanterSideScreen already exists
        var controlDef = Assets.GetBuildingDef("FarmTile");
        if (controlDef == null)
        {
            Log?.Line("  REACHABILITY: no FarmTile BuildingDef - no control");
        }
        else
        {
            int cell = FreeFootprintCell(controlDef);
            if (cell < 0)
            {
                Log?.Line("  REACHABILITY: no clear spot for the control FarmTile");
            }
            else
            {
                controlDef.RunOnArea(cell, Orientation.Neutral, c =>
                {
                    if (Grid.IsSolidCell(c))
                        SimMessages.Dig(c, skipEvent: true);
                });
                var built = controlDef.Build(cell, Orientation.Neutral, resource_storage: null,
                    FixtureBuilder.SelectElements(controlDef), temperature: 293.15f,
                    playsound: false, timeBuilt: 0f);
                for (int i = 0; i < 10; i++)
                    yield return null;

                if (built == null)
                {
                    Log?.Line("  REACHABILITY: the control FarmTile did not build");
                }
                else
                {
                    try
                    {
                        ///select it the way the game does, so the details screen wires the side screen up
                        Game.Instance.Trigger((int)GameHashes.SelectObject, built);
                        if (SelectTool.Instance != null && built.TryGetComponent<KSelectable>(out var sel))
                            SelectTool.Instance.Select(sel);
                        for (int i = 0; i < 10; i++)
                            yield return null;

                        readings.Add(MeasurePicker(built, "FarmTile (finished, control)"));
                    }
                    finally
                    {
                        if (SelectTool.Instance != null)
                            SelectTool.Instance.Select(null);
                        if (Game.Instance != null)
                            Game.Instance.Trigger((int)GameHashes.SelectObject, null);
                    }
                    for (int i = 0; i < 3; i++)
                        yield return null;
                    built.DeleteObject();
                }
            }
        }

        Assert.True(readings.Count > 0,
            "at least one planter could be probed - see the REACHABILITY lines above");

        foreach (var r in readings)
            Log?.Line($"  SUMMARY {r.Label}: accepted={r.ScreenAcceptedTarget}, listed={r.ListedCount}, " +
                      $"selectableRows={r.SelectableCount}, clicked={r.ClickedTag}, " +
                      $"selectedAfterClick={r.SelectedTagAfterClick}, " +
                      $"canDepositAfterClick={r.AdditionalCanDepositAfterClick}, " +
                      $"requestedAfterConfirm={r.RequestedTagAfterConfirm}, storedOnPlan={r.StoredOnPlan}");

        var control = readings.FirstOrDefault(r => r.Label != null && r.Label.Contains("control"));
        if (control.Label == null || control.SelectableCount <= 0)
        {
            Log?.Line("  REACHABILITY: the control never got a selectable row either, so nothing " +
                      "measured here can be attributed to the preconfigure session");
            yield break;
        }

        foreach (var r in readings)
        {
            Assert.True(r.ScreenAcceptedTarget,
                $"the receptacle side screen accepts {r.Label} as a target");
            Assert.True(r.ListedCount > 0,
                $"{r.Label}'s seed picker lists something ({r.ListedCount})");
            Assert.True(r.SelectableCount > 0,
                $"at least one row in {r.Label}'s picker is selectable ({r.SelectableCount} of {r.ListedCount})");
            Assert.True(r.SelectedTagAfterClick != "(none)",
                $"clicking a row on {r.Label} registers a choice (got {r.SelectedTagAfterClick})");
            Assert.True(r.RequestedTagAfterConfirm != "(none)",
                $"confirming the choice on {r.Label} reaches the receptacle (got {r.RequestedTagAfterConfirm})");
        }

        foreach (var r in readings.Where(r => r.Label != null && r.Label.Contains("preconfigure")))
            Assert.True(r.StoredOnPlan.Contains(r.ClickedTag),
                $"the {r.Label} plan keeps the seed chosen during the session " +
                $"(clicked {r.ClickedTag}, stored {r.StoredOnPlan})");
    }

    /// <summary>What one walk over the receptacle side screen saw.</summary>
    private struct PickerReading
    {
        public string Label;
        /// <summary>False when the walk never got as far as a screen, so the rest means nothing.</summary>
        public bool Reached;
        public bool ScreenAcceptedTarget;
        public int ListedCount;
        public int SelectableCount;
        public string ClickedTag;
        public string SelectedTagAfterClick;
        public string AdditionalCanDepositAfterClick;
        public string RequestedTagAfterConfirm;
        public string StoredOnPlan;
    }

    /// <summary>
    /// Points the receptacle side screen at <paramref name="target"/>, clicks the first selectable
    /// row and confirms it, reporting what happened at each step. Synchronous: every step here is
    /// a direct call, not something the screen does a frame later.
    /// </summary>
    private static PickerReading MeasurePicker(GameObject target, string label)
    {
        var reading = new PickerReading
        {
            Label = label,
            ListedCount = -1,
            SelectableCount = -1,
            ClickedTag = "(none)",
            SelectedTagAfterClick = "(none)",
            AdditionalCanDepositAfterClick = "(unread)",
            RequestedTagAfterConfirm = "(none)",
            StoredOnPlan = "(none)",
        };

        var receptacle = target.GetComponent<SingleEntityReceptacle>();
        var operational = target.GetComponent<Operational>();
        var plot = target.GetComponent<PlantablePlot>();
        Log?.Line($"  {label} at {Grid.CellToXY(Grid.PosToCell(target))}: " +
                  $"receptacle={receptacle != null}, " +
                  $"operational={(operational == null ? "n/a" : operational.IsOperational.ToString())}, " +
                  $"liquidPipeInput={(plot == null ? "n/a" : plot.has_liquid_pipe_input.ToString())}, " +
                  $"direction={(receptacle == null ? "n/a" : receptacle.Direction.ToString())}, " +
                  $"rotatable={(receptacle == null ? "n/a" : (receptacle.rotatable != null).ToString())}, " +
                  $"validPlant={(plot == null ? "n/a" : plot.ValidPlant.ToString())}");

        var screen = Resources.FindObjectsOfTypeAll<ReceptacleSideScreen>()
            .FirstOrDefault(sc => sc != null && sc.gameObject.scene.IsValid());
        if (screen == null)
        {
            Log?.Line("  REACHABILITY: no ReceptacleSideScreen instance in the scene");
            return reading;
        }

        reading.Reached = true;
        reading.ScreenAcceptedTarget = screen.IsValidForTarget(target);
        Log?.Line($"  screen={screen.GetType().Name}, IsValidForTarget={reading.ScreenAcceptedTarget}");
        if (!reading.ScreenAcceptedTarget)
            return reading;

        screen.SetTarget(target);

        var map = ScreenField(screen, "depositObjectMap")?.GetValue(screen) as IDictionary;
        reading.ListedCount = map?.Count ?? -1;
        Log?.Line($"  depositObjectMap={reading.ListedCount}, " +
                  $"RequiresAvailableAmountToDeposit={CallBool(screen, "RequiresAvailableAmountToDeposit")}");
        if (map == null || map.Count == 0)
            return reading;

        ///false for the second argument: that is the row-enabled test. Passing true also runs
        ///AdditionalCanDepositTest, which is false until something IS selected, so it would report
        ///every row unselectable no matter what.
        var canDeposit = typeof(ReceptacleSideScreen).GetMethod("CanDepositEntity",
            BindingFlags.NonPublic | BindingFlags.Instance);

        object? firstSelectable = null;
        string firstSelectableName = "(none)";
        reading.SelectableCount = 0;
        var sample = new List<string>();
        foreach (DictionaryEntry pair in map)
        {
            bool ok = false;
            if (canDeposit != null)
            {
                try
                {
                    ok = (bool)canDeposit.Invoke(screen, new[] { pair.Value, (object)false })!;
                }
                catch (Exception e)
                {
                    sample.Add("CanDepositEntity threw " + (e.InnerException ?? e).GetType().Name);
                }
            }
            string entityName = NameOfEntity(pair.Value);
            if (ok)
            {
                reading.SelectableCount++;
                if (firstSelectable == null)
                {
                    firstSelectable = pair.Key;
                    firstSelectableName = entityName;
                }
            }
            if (sample.Count < 6)
                sample.Add($"{entityName}={ok}");
        }
        Log?.Line($"  CanDepositEntity(row): {reading.SelectableCount}/{map.Count} true; " +
                  $"sample {string.Join(", ", sample)}");

        if (firstSelectable == null)
            return reading;

        reading.ClickedTag = firstSelectableName;
        var toggleClicked = typeof(ReceptacleSideScreen).GetMethod("ToggleClicked",
            BindingFlags.NonPublic | BindingFlags.Instance);
        try
        {
            toggleClicked?.Invoke(screen, new[] { firstSelectable });
        }
        catch (Exception e)
        {
            Log?.Line($"  ToggleClicked threw {(e.InnerException ?? e).GetType().Name}: " +
                      $"{(e.InnerException ?? e).Message}");
        }

        var selectedField = ScreenField(screen, "selectedDepositObjectTag");
        var selected = selectedField?.GetValue(screen);
        if (selected is Tag selectedTag && selectedTag.IsValid)
            reading.SelectedTagAfterClick = selectedTag.ToString();
        reading.AdditionalCanDepositAfterClick = CallBool(screen, "AdditionalCanDepositTest");
        Log?.Line($"  clicked {reading.ClickedTag} -> selectedDepositObjectTag=" +
                  $"{reading.SelectedTagAfterClick}, AdditionalCanDepositTest=" +
                  $"{reading.AdditionalCanDepositAfterClick}");

        ///AdditionalCanDepositTest is three clauses AND-ed: a valid (and, with the mutations DLC,
        ///plantable) seed tag, plot.ValidPlant, and the seed being in stock in the receptacle's
        ///OWN world. Report each separately - a false from the third means the temporary building
        ///is not in the world the player's seeds are in.
        ///the second clause of AdditionalCanDepositTest: PlantablePlot.ValidPlant, which is
        ///"plantPreview == null || plantPreview.Valid" - i.e. whether the ghost plant the screen
        ///just put in the plot reports that it could live there. Read AFTER the click, because
        ///before it there is no preview and the answer is trivially true.
        object? preview = plot == null
            ? null
            : typeof(PlantablePlot).GetField("plantPreview", BindingFlags.NonPublic | BindingFlags.Instance)
                ?.GetValue(plot);
        Log?.Line($"  plot after click: ValidPlant={(plot == null ? "n/a" : plot.ValidPlant.ToString())}, " +
                  $"plantPreview={(preview == null || preview.Equals(null) ? "null" : "present")}, " +
                  $"previewValid={(preview is EntityPreview ep && !ep.Equals(null) ? ep.Valid.ToString() : "n/a")}");

        ///the first clause of AdditionalCanDepositTest with mutations on: the seed tag AND the
        ///subspecies tag have to name a plantable seed together
        var additionalAfter = ScreenField(screen, "selectedDepositObjectAdditionalTag")?.GetValue(screen);
        string plantable = "(unread)";
        if (selected is Tag seedTag)
        {
            try
            {
                plantable = PlantSubSpeciesCatalog.Instance
                    .IsValidPlantableSeed(seedTag, additionalAfter is Tag sub ? sub : Tag.Invalid)
                    .ToString();
            }
            catch (Exception e)
            {
                plantable = "threw " + e.GetType().Name;
            }
        }
        Log?.Line($"  subspecies: selectedDepositObjectAdditionalTag=" +
                  $"{(additionalAfter is Tag t2 && t2.IsValid ? t2.ToString() : "(invalid)")}, " +
                  $"receptacle.requestedEntityAdditionalFilterTag=" +
                  $"{(receptacle != null && receptacle.requestedEntityAdditionalFilterTag.IsValid ? receptacle.requestedEntityAdditionalFilterTag.ToString() : "(invalid)")}, " +
                  $"IsValidPlantableSeed={plantable}, " +
                  $"anyNonOriginalDiscovered={PlantSubSpeciesCatalog.Instance.AnyNonOriginalDiscovered}");

        int targetCell = Grid.PosToCell(target);
        var world = receptacle == null ? null : receptacle.GetMyWorld();
        string stock = "(unread)";
        if (world != null && selected is Tag chosen)
        {
            var additional = ScreenField(screen, "selectedDepositObjectAdditionalTag")?.GetValue(screen);
            try
            {
                stock = world.worldInventory
                    .GetCountWithAdditionalTag(chosen, additional is Tag extra ? extra : Tag.Invalid,
                                               world.IsModuleInterior)
                    .ToString();
            }
            catch (Exception e)
            {
                stock = "threw " + e.GetType().Name;
            }
        }
        Log?.Line($"  world: cellWorldIdx={(Grid.IsValidCell(targetCell) ? Grid.WorldIdx[targetCell].ToString() : "n/a")}, " +
                  $"GetMyWorldId={(receptacle == null ? "n/a" : receptacle.GetMyWorldId().ToString())}, " +
                  $"GetMyWorld={(world == null ? "null" : world.id.ToString())}, " +
                  $"plantMutations={DlcManager.FeaturePlantMutationsEnabled()}, " +
                  $"stockInThatWorld={stock}");

        ///what the confirm button ends up calling: ReceptacleSideScreen.CreateOrder forwards the
        ///two selected tags straight to the receptacle
        if (receptacle != null && selected is Tag confirmTag)
        {
            try
            {
                receptacle.CreateOrder(confirmTag, additionalAfter is Tag extra2 ? extra2 : Tag.Invalid);
            }
            catch (Exception e)
            {
                Log?.Line($"  CreateOrder threw {e.GetType().Name}: {e.Message}");
            }
        }

        if (receptacle != null && receptacle.requestedEntityTag.IsValid)
            reading.RequestedTagAfterConfirm = receptacle.requestedEntityTag.ToString();
        Log?.Line($"  after confirm: requestedEntityTag={reading.RequestedTagAfterConfirm}");

        return reading;
    }

    /// <summary>A <c>SelectableEntity</c>'s prefab tag, without a compile-time reference to it.</summary>
    private static string NameOfEntity(object? entity)
    {
        if (entity == null)
            return "(null)";
        var tagField = entity.GetType().GetField("tag", BindingFlags.Public | BindingFlags.Instance);
        return tagField?.GetValue(entity)?.ToString() ?? entity.GetType().Name;
    }

    private static FieldInfo? ScreenField(ReceptacleSideScreen screen, string name)
    {
        var field = typeof(ReceptacleSideScreen)
            .GetField(name, BindingFlags.NonPublic | BindingFlags.Instance);
        if (field == null)
            Log?.Line($"  (no field {name} on ReceptacleSideScreen - Klei renamed it?)");
        return field;
    }

    private static string CallBool(ReceptacleSideScreen screen, string name)
    {
        var method = typeof(ReceptacleSideScreen)
            .GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance);
        if (method == null)
            return "(no such method)";
        try
        {
            return method.Invoke(screen, null)?.ToString() ?? "(null)";
        }
        catch (Exception e)
        {
            return "threw " + (e.InnerException ?? e).GetType().Name;
        }
    }

    /// <summary>First cell in a sweep well clear of the other cases' regions whose whole
    /// footprint is inside the map and empty on the building layer. -1 if there is none.</summary>
    private static int FreeFootprintCell(BuildingDef def)
    {
        var anchor = Grid.CellToXY(AnchorCell);
        for (int dy = -28; dy >= -34; dy--)
        {
            for (int dx = -16; dx <= 16; dx++)
            {
                int cell = Grid.XYToCell(anchor.x + dx, anchor.y + dy);
                if (!Grid.IsValidCell(cell))
                    continue;

                bool clear = true;
                def.RunOnArea(cell, Orientation.Neutral, c =>
                {
                    if (!Grid.IsValidCell(c)
                        || Grid.Objects[c, (int)ObjectLayer.Building] != null
                        || Grid.Objects[c, (int)def.ObjectLayer] != null)
                        clear = false;
                });
                if (clear)
                    return cell;
            }
        }
        return -1;
    }

    /// <summary>
    /// Queues a plan for <paramref name="def"/>, opens a preconfigure session on it, walks the
    /// receptacle side screen against the temporary building and then checks what the plan kept.
    /// Yield it, then read <see cref="Reading"/>.
    /// </summary>
    private sealed class ProbeSeedPicker : IEnumerator
    {
        private readonly IEnumerator steps;

        public PickerReading Reading;

        public ProbeSeedPicker(BuildingDef def) => steps = Run(def);

        private IEnumerator Run(BuildingDef def)
        {
            int cell = FreeFootprintCell(def);
            if (cell < 0)
            {
                Log?.Line($"  REACHABILITY: no clear {def.WidthInCells}x{def.HeightInCells} spot " +
                          $"for a {def.PrefabID} plan");
                yield break;
            }

            GameObject? plan = null;
            bool savedInstant = DebugHandler.InstantBuildMode;
            DebugHandler.InstantBuildMode = false;
            try
            {
                ///a plan, not a finished building - UnderConstructionDataTransfer only exists on one
                plan = def.TryPlace(null, Grid.CellToPosCBC(cell, def.SceneLayer), Orientation.Neutral,
                                    FixtureBuilder.SelectElements(def), null);
            }
            catch (Exception e)
            {
                Log?.Line($"  REACHABILITY: TryPlace threw for {def.PrefabID}: {e.GetType().Name}: {e.Message}");
            }
            finally
            {
                DebugHandler.InstantBuildMode = savedInstant;
            }

            if (plan == null || !plan.TryGetComponent<UnderConstructionDataTransfer>(out var transfer))
            {
                Log?.Line($"  REACHABILITY: no {def.PrefabID} plan carrying an UnderConstructionDataTransfer " +
                          $"at {Grid.CellToXY(cell)}");
                yield break;
            }

            bool tornDown = false;
            try
            {
                transfer.OnSidescreenButtonPressed();
                for (int i = 0; i < 15; i++)
                    yield return null;

                var temp = UnderConstructionDataSettingHelper.TemporarySelectable;
                if (temp == null)
                {
                    Log?.Line($"  REACHABILITY: the {def.PrefabID} session spawned no temporary building");
                    yield break;
                }

                Reading = MeasurePicker(temp.gameObject, $"{def.PrefabID} (preconfigure session)");

                yield return EndPreconfigureEditing();
                tornDown = true;
                for (int i = 0; i < 3; i++)
                    yield return null;

                var stored = transfer.GetStoredData();
                Log?.Line($"  stored keys on the plan: {string.Join(", ", stored.Keys)}");
                foreach (var entry in stored)
                {
                    if (entry.Value != null && entry.Value.Contains("requestedEntityTag"))
                        Reading.StoredOnPlan = $"{entry.Key}={entry.Value}";
                }
                Log?.Line($"  plan kept: {Reading.StoredOnPlan}");
            }
            finally
            {
                if (!tornDown)
                {
                    PreconfigureCleanUp();
                    if (SelectTool.Instance != null)
                        SelectTool.Instance.Select(null);
                }
                if (plan != null)
                    plan.DeleteObject();
            }
        }

        public bool MoveNext() => steps.MoveNext();
        public void Reset() => steps.Reset();
        public object Current => steps.Current;
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

    // ---- #74: rocket modules ------------------------------------------------------

    /// <summary>
    /// A rocket module attaches to a hardpoint on the module below it. In a blueprint the module
    /// below is only a preview, so the game refuses the one above - which would make a blueprint
    /// of a whole rocket unplaceable. Each module preview therefore registers its own hardpoint,
    /// and an attach-point failure on one of those cells is ignored.
    ///
    /// Asserts the routing (a module def gets a RocketModuleVisual), that the upper module of a
    /// stacked pair is accepted on the lower one's hardpoint, that moving the blueprint drops the
    /// stale registration, and that clearing it empties the registry.
    /// </summary>
    private static IEnumerator RocketModulesStack()
    {
        var moduleDef = Assets.BuildingDefs.FirstOrDefault(d =>
            d != null && d.BuildingPreview != null && d.BuildingComplete != null
            && d.BuildingComplete.GetComponent<RocketModule>() != null
            && d.BuildingComplete.TryGetComponent<BuildingAttachPoint>(out var ap)
            && ap.points != null && ap.points.Any(pt => pt.attachableType == GameTags.Rocket));
        if (moduleDef == null)
        {
            Log?.Line("  REACHABILITY: no rocket module with a rocket hardpoint in this install " +
                      "(needs the rocketry DLC content), so module stacking is unexercised");
            yield break;
        }

        var attachPoint = moduleDef.BuildingComplete.GetComponent<BuildingAttachPoint>();
        var hardPoint = attachPoint.points.First(pt => pt.attachableType == GameTags.Rocket);
        Log?.Line($"  using {moduleDef.PrefabID} {moduleDef.WidthInCells}x{moduleDef.HeightInCells}, " +
                  $"hardpoint at offset ({hardPoint.position.x},{hardPoint.position.y})");

        var typeOf = typeof(Blueprint).Assembly.GetType("BlueprintsV2.ModAssets")!
            .GetMethod("GetVisualizerType", BindingFlags.Public | BindingFlags.Static)!;
        Assert.Equal("ROCKET", typeOf.Invoke(null, new object[] { moduleDef })!.ToString(),
            "a rocket module routes to the rocket visual");

        var registry = (System.Collections.IDictionary)AccessTools
            .Field(typeof(RocketModuleVisual), "AttachmentPoints").GetValue(null)!;

        var anchorXY = Grid.CellToXY(AnchorCell);
        var target = new Vector2I(anchorXY.x + 26, anchorXY.y + 14);
        yield return ClearRegion(target, 12, 12);

        var st = BlueprintState.CurrentStateInfo();
        st.IsPlacingSnapshot = true;
        try
        {
            ///two modules, the upper one sitting on the lower one's hardpoint
            var bp = new Blueprint("harness-rocket-stack", "");
            AddConfig(bp, moduleDef, new Vector2I(0, 0));
            AddConfig(bp, moduleDef, new Vector2I(hardPoint.position.x, hardPoint.position.y));
            bp.CacheCost();

            BlueprintState.VisualizeBlueprint(target, bp);
            for (int i = 0; i < 6; i++) yield return null;

            var visuals = LiveVisuals().OfType<RocketModuleVisual>().ToList();
            Log?.Line($"  {visuals.Count} rocket visual(s), hardpoint cells " +
                      string.Join(", ", visuals.Select(v => Grid.CellToXY(v.DirtyCell).ToString())));
            Assert.Equal(2, visuals.Count, "both modules got a rocket visual");

            var upper = visuals.OrderByDescending(v => Grid.CellToXY(v.CurrentCell).y).First();
            var lower = visuals.OrderBy(v => Grid.CellToXY(v.CurrentCell).y).First();
            Assert.True(lower.DirtyCell >= 0, "the lower module registered a hardpoint");
            Assert.Equal(Grid.CellToXY(lower.DirtyCell), Grid.CellToXY(upper.CurrentCell),
                "the upper module sits on the lower one's hardpoint");

            bool accepted = upper.ValidCell(upper.CurrentCell, out _);
            Log?.Line($"  upper module on the previewed hardpoint: accepted={accepted}");
            Assert.True(accepted, "a module is accepted on another preview's hardpoint");

            ///moving the blueprint must drop the stale registration
            int staleCell = lower.DirtyCell;
            var moved = new Vector2I(target.x + 4, target.y);
            BlueprintState.UpdateVisual(BlueprintState.PlayerId_DefaultTilePreviews, moved, false, bp);
            for (int i = 0; i < 4; i++) yield return null;

            var points = (System.Collections.Generic.HashSet<int>)registry[BlueprintState.PlayerId_DefaultTilePreviews]!;
            Log?.Line($"  after moving: {points.Count} registered point(s), stale cell still present={points.Contains(staleCell)}");
            Assert.True(!points.Contains(staleCell), "moving the blueprint unregisters the old hardpoint");
            Assert.Equal(2, points.Count, "each module registers exactly one hardpoint");

            BlueprintState.ClearVisuals();
            for (int i = 0; i < 3; i++) yield return null;
            Assert.Equal(0, points.Count, "clearing the blueprint empties the hardpoint registry");
        }
        finally
        {
            BlueprintState.ClearVisuals();
            st.IsPlacingSnapshot = false;
        }
    }

    // ---- #49 / #66: connection points are claimed too -------------------------------

    /// <summary>
    /// A replacement vis used to claim only its footprint, on its own object layer. A building's
    /// connection points - conduit ports, power connectors, radbolt ports - sit on *other* layers,
    /// so a pipe or wire already on one was left in place and the replacement came out
    /// unconnected (#49, radbolt ports from #66).
    ///
    /// Asserts the ports are registered and seated, that a bridge claims only its two ends rather
    /// than its whole span, and that a conduit sitting on a port cell is queued for deconstruction.
    /// </summary>
    private static IEnumerator ReplacementVisClaimsPortCells()
    {
        var portsField = AccessTools.Field(typeof(ReplacementVis), "portOccupations");
        var cellsField = AccessTools.Field(typeof(ReplacementVis), "occupiedCells");

        var anchorXY = Grid.CellToXY(AnchorCell);
        var vises = new List<ReplacementVis>();
        GameObject? conduit = null;
        var cfg = ModConfig();
        bool savedTech = cfg.RequireConstructable_Tech, savedMat = cfg.RequireConstructable_Material;
        cfg.RequireConstructable_Tech = false;
        cfg.RequireConstructable_Material = false;
        try
        {
            ///--- a building with a conduit port ---
            var ported = Assets.BuildingDefs.FirstOrDefault(d =>
                d != null && d.InputConduitType != ConduitType.None && d.BuildingPreview != null
                && d.BuildingComplete != null && d.BuildingComplete.GetComponent<ConduitBridgeBase>() == null);
            if (ported == null)
            {
                Log?.Line("  REACHABILITY: no def with an input conduit port in this install");
                yield break;
            }

            int cell = Grid.XYToCell(anchorXY.x + 14, anchorXY.y - 20);
            yield return ClearRegion(new Vector2I(anchorXY.x + 14, anchorXY.y - 20), 5, 5);

            ///a finished conduit sitting exactly on the port cell, which the old footprint-only
            ///tracking never looked at
            int portCell = Grid.OffsetCell(cell, Rotatable.GetRotatedCellOffset(ported.UtilityInputOffset, Orientation.Neutral));
            var portLayer = Grid.GetObjectLayerForConduitType(ported.InputConduitType);
            var conduitDef = Assets.BuildingDefs.FirstOrDefault(d =>
                d != null && d.ObjectLayer == portLayer && d.WidthInCells == 1 && d.HeightInCells == 1
                && d.BuildingComplete != null);
            if (conduitDef != null)
            {
                conduit = conduitDef.Build(portCell, Orientation.Neutral, null,
                    FixtureBuilder.SelectElements(conduitDef), 294.15f, true, GameClock.Instance.GetTime());
                for (int i = 0; i < 5; i++) yield return null;
            }

            var config = NewConfig(ported);
            var spawn = SpawnSeatedVis(config, cell);
            yield return spawn;
            vises.Add(spawn.Vis);

            var ports = ((IEnumerable<(int Cell, ObjectLayer Layer)>)portsField.GetValue(spawn.Vis)!).ToList();
            Log?.Line($"  {ported.PrefabID}: {ports.Count} port cell(s) " +
                      string.Join(", ", ports.Select(pt => $"{Grid.CellToXY(pt.Cell)}/{pt.Layer}")));
            Assert.True(ports.Any(pt => pt.Cell == portCell && pt.Layer == portLayer),
                "the input conduit port is registered on its own layer");
            Assert.True(ReferenceEquals(ReplacementVis.Visualizers[portCell, (int)portLayer], spawn.Vis),
                "seating claimed the port cell");

            if (conduit != null && conduit.TryGetComponent<Deconstructable>(out var decon))
            {
                Log?.Line($"  conduit on the port cell marked for deconstruction: {decon.IsMarkedForDeconstruction()}");
                Assert.True(decon.IsMarkedForDeconstruction(),
                    "a conduit sitting on the port cell is queued for deconstruction");
            }
            else
            {
                Log?.Line("  REACHABILITY: no 1x1 building on the port layer to put in the way");
            }

            ///--- a bridge claims its two ends, not its whole span ---
            var bridge = Assets.BuildingDefs.FirstOrDefault(d =>
                d != null && d.BuildingPreview != null && d.BuildingComplete != null
                && d.BuildingComplete.GetComponent<ConduitBridgeBase>() != null
                && d.PlacementOffsets.Length > 2);
            if (bridge == null)
            {
                Log?.Line("  REACHABILITY: no conduit bridge wider than two cells in this install");
            }
            else
            {
                int bridgeCell = Grid.XYToCell(anchorXY.x + 14, anchorXY.y - 26);
                yield return ClearRegion(new Vector2I(anchorXY.x + 14, anchorXY.y - 26), 5, 4);
                var bridgeSpawn = SpawnSeatedVis(NewConfig(bridge), bridgeCell);
                yield return bridgeSpawn;
                vises.Add(bridgeSpawn.Vis);

                var claimed = ((IEnumerable<int>)cellsField.GetValue(bridgeSpawn.Vis)!).ToList();
                Log?.Line($"  {bridge.PrefabID}: footprint {bridge.PlacementOffsets.Length} cell(s), claimed {claimed.Count}");
                Assert.Equal(2, claimed.Count, "a bridge claims only its two ends");
            }

            ///--- radbolt ports (#66) ---
            var hep = Assets.BuildingDefs.FirstOrDefault(d =>
                d != null && d.BuildingPreview != null
                && (d.UseHighEnergyParticleInputPort || d.UseHighEnergyParticleOutputPort));
            if (hep == null)
            {
                Log?.Line("  REACHABILITY: no def with a radbolt port in this install");
            }
            else
            {
                int hepCell = Grid.XYToCell(anchorXY.x + 14, anchorXY.y - 32);
                yield return ClearRegion(new Vector2I(anchorXY.x + 14, anchorXY.y - 32), 5, 5);
                var hepSpawn = SpawnSeatedVis(NewConfig(hep), hepCell);
                yield return hepSpawn;
                vises.Add(hepSpawn.Vis);

                var hepPorts = ((IEnumerable<(int Cell, ObjectLayer Layer)>)portsField.GetValue(hepSpawn.Vis)!).ToList();
                int expected = Grid.OffsetCell(hepCell, Rotatable.GetRotatedCellOffset(
                    hep.UseHighEnergyParticleInputPort ? hep.HighEnergyParticleInputOffset : hep.HighEnergyParticleOutputOffset,
                    Orientation.Neutral));
                Log?.Line($"  {hep.PrefabID}: radbolt port at {Grid.CellToXY(expected)}, tracked " +
                          string.Join(", ", hepPorts.Select(pt => $"{Grid.CellToXY(pt.Cell)}/{pt.Layer}")));
                Assert.True(hepPorts.Any(pt => pt.Cell == expected && pt.Layer == ObjectLayer.Building),
                    "the radbolt port is registered on the building layer");
            }
        }
        finally
        {
            ///DestroySelf is protected - the same reflection the re-entrancy case uses
            var destroySelf = AccessTools.Method(typeof(ReplacementVis), "DestroySelf");
            foreach (var vis in vises)
                if (vis != null)
                    destroySelf.Invoke(vis, null);
            if (conduit != null)
                UnityEngine.Object.Destroy(conduit);
            cfg.RequireConstructable_Tech = savedTech;
            cfg.RequireConstructable_Material = savedMat;
        }
    }

    private static BuildingConfig NewConfig(BuildingDef def)
    {
        var config = new BuildingConfig
        {
            Offset = new Vector2I(0, 0),
            BuildingDef = def,
            BuildingDefId = def.PrefabID,
            Orientation = Orientation.Neutral,
        };
        foreach (var tag in FixtureBuilder.SelectElements(def))
            config.SelectedElements.Add(tag);
        return config;
    }

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

    // ---- #97: the paste key ---------------------------------------

    /// <summary>
    /// The snapshot tool's paste key (Ctrl+V by default) puts a blueprint from the clipboard in
    /// hand, and falls back to the last snapshot when the clipboard holds nothing usable. Drives
    /// the same method the key does, with a real exported blueprint on the clipboard and then with
    /// junk on it.
    /// </summary>
    private static IEnumerator PasteFromClipboard()
    {
        var tool = BlueprintsV2.Tools.SnapshotTool.Instance;
        if (tool == null)
        {
            Log?.Line("  REACHABILITY: the snapshot tool has not been created in this session");
            yield break;
        }

        var source = Snapshot(TileRowTopLeft(), TileRowBottomRight());
        int expected = source.BuildingConfigurations.Count(b => !b.BuildingDisabled);
        Assert.True(expected > 0, "the source blueprint captured something");

        ///ModAssets is internal - export through it by reflection, as FixtureBuilder does
        typeof(Blueprint).Assembly.GetType("BlueprintsV2.ModAssets")!
            .GetMethod("ExportToClipboard", BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, new object[] { source });

        try
        {
            tool.PasteOrReuseLastSnapshot();
            for (int i = 0; i < 5; i++) yield return null;

            var pasted = BlueprintsV2.Tools.SnapshotTool.CurrentSnapshot;
            Assert.True(pasted != null, "the paste key put a blueprint in hand");
            int pastedCount = pasted!.BuildingConfigurations.Count(b => !b.BuildingDisabled);
            Log?.Line($"  pasted {pastedCount} building(s), source had {expected}");
            Assert.Equal(expected, pastedCount, "the pasted blueprint holds what was exported");
            Assert.True(!ReferenceEquals(pasted, source), "it came back through the clipboard, not by reference");

            ///junk on the clipboard: the key falls back to the last snapshot rather than throwing
            ///or clearing what is in hand
            UtilLibs.IO_Utils.PutToClipboard("not a blueprint");
            tool.PasteOrReuseLastSnapshot();
            for (int i = 0; i < 5; i++) yield return null;

            var afterJunk = BlueprintsV2.Tools.SnapshotTool.CurrentSnapshot;
            Log?.Line($"  after junk on the clipboard: {(afterJunk == null ? "(nothing in hand)" : afterJunk.FriendlyName)}");
            Assert.True(afterJunk != null, "junk on the clipboard falls back to a snapshot rather than emptying the hand");
        }
        finally
        {
            UtilLibs.IO_Utils.PutToClipboard(string.Empty);
            tool.DeleteBlueprint();
            BlueprintState.ClearVisuals();
        }
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

    // ---- #96: Save / Export on the snapshot state screen ---------------------------

    /// <summary>
    /// The ExportActions row is built in code by cloning the material-overrides row, because the
    /// bundle upstream ships it in (cb3991a) is not a readable AssetBundle. Checks the row is
    /// built with both buttons, that each got its own handler - upstream wired both to Save,
    /// leaving Export inert - and that saving really writes a snapshot to the blueprints folder,
    /// where upstream's Write() on a snapshot's bare-GUID path would have thrown.
    /// </summary>
    private static IEnumerator SnapshotSaveAndExportButtons()
    {
        var st = BlueprintState.CurrentStateInfo();
        st.IsPlacingSnapshot = true;
        string? writtenPath = null;
        try
        {
            StateScreenType.GetMethod("ShowScreen")!.Invoke(null, new object[] { true });
            yield return null;
            var screen = (Component?)StateScreenType.GetField("Instance")!.GetValue(null);
            Assert.True(screen != null, "the blueprint state screen was created");

            var row = screen!.transform.Find("InfoItemsContainer/ExportActions");
            Assert.True(row != null, "the screen builds an InfoItemsContainer/ExportActions row");

            var handlers = new Dictionary<string, string>();
            foreach (var name in new[] { "Save", "Export" })
            {
                var button = row!.Find(name);
                Assert.True(button != null, $"ExportActions/{name} exists");
                var fButton = button!.GetComponent<UtilLibs.UIcmp.FButton>();
                Assert.True(fButton != null, $"ExportActions/{name} is wired as a button");
                var onClick = (Delegate?)AccessTools.Field(typeof(UtilLibs.UIcmp.FButton), "OnClick").GetValue(fButton);
                Assert.True(onClick != null, $"ExportActions/{name} has a click handler");
                handlers[name] = string.Join("+", onClick!.GetInvocationList().Select(d => d.Method.Name));
            }
            Log?.Line($"  Save -> {handlers["Save"]}, Export -> {handlers["Export"]}");
            Assert.True(handlers["Save"] != handlers["Export"], "each button has its own handler");

            ///the save itself, with the naming dialog skipped
            var snapshot = Snapshot(TileRowTopLeft(), TileRowBottomRight());
            Assert.True(!snapshot.IsEmpty(), "the snapshot captured something");
            Assert.True(!snapshot.FilePath.Contains(System.IO.Path.DirectorySeparatorChar),
                "a fresh snapshot's FilePath is a bare id, not a real location");

            string name2 = "harness-snapshot-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            StateScreenType.GetMethod("SaveNamedSnapshot", BindingFlags.NonPublic | BindingFlags.Static)!
                .Invoke(null, new object[] { snapshot, name2 });
            writtenPath = snapshot.FilePath;

            Log?.Line($"  saved to {writtenPath}");
            Assert.True(System.IO.File.Exists(writtenPath), "saving writes a .blueprint file");
            ///ModAssets is internal - ask it for the blueprints folder by reflection.
            string blueprintDir = (string)typeof(Blueprint).Assembly
                .GetType("BlueprintsV2.ModAssets+BlueprintFileHandling")!
                .GetMethod("GetBlueprintDirectory", BindingFlags.Public | BindingFlags.Static)!
                .Invoke(null, null)!;
            Assert.Equal(blueprintDir, System.IO.Path.GetDirectoryName(writtenPath),
                "the file lands in the blueprints folder");
            Assert.Equal(name2, snapshot.FriendlyName, "the snapshot took the name it was given");
        }
        finally
        {
            StateScreenType.GetMethod("ShowScreen")!.Invoke(null, new object[] { false });
            st.IsPlacingSnapshot = false;
            if (writtenPath != null && System.IO.File.Exists(writtenPath))
                System.IO.File.Delete(writtenPath);
        }
    }

    // ---- #114: every dialog in the blueprints_ui bundle opens and binds ------------------

    /// <summary>
    /// Opens each screen the blueprints_ui bundle provides the way the mod does, and checks it came
    /// up whole. The bundle is rebuilt from a spec (dytterud/oni-blueprints-ui), and each screen
    /// finds its widgets by child path when it first opens - so a prefab that drifted from the
    /// layout the code expects would only show up here, on opening. The current-blueprint-state
    /// screen is left out: other cases open it already.
    ///
    /// Per screen: opening throws nothing, the instance is active, and every UI reference field the
    /// code declares non-nullable was bound by its Init. Each gets a screenshot, for the look.
    /// </summary>
    private static IEnumerator BundleDialogsOpenAndBind()
    {
        var asm = typeof(Blueprint).Assembly;
        Type Screen(string name) => asm.GetType("BlueprintsV2.UnityUI." + name)!;
        const BindingFlags statics = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

        // blueprint selector - the main browser, with its blueprint list shown
        var selector = Screen("BlueprintSelectionScreen");
        yield return OpenCheckClose("blueprint-selector", selector,
            open: () => selector.GetMethod("ShowWindow", statics)!
                .Invoke(null, new object?[] { new System.Action<Blueprint?>(_ => { }), null, true }));

        // naming dialog - with folder suggestions, so its dropdown list is populated too
        var naming = Screen("BlueprintRenamingScreen");
        yield return OpenCheckClose("naming-dialog", naming,
            open: () => naming.GetMethod("OpenNamingDialogue", statics)!
                .Invoke(null, new object?[] { "Harness", new System.Action<string>(_ => { }), new System.Action(() => { }),
                    "harness", false, new[] { "harness-folder-a", "harness-folder-b" } }));

        // icon picker
        var icons = Screen("SpriteSelectorScreen");
        yield return OpenCheckClose("icon-picker", icons,
            open: () => icons.GetMethod("ShowScreen", statics)!
                .Invoke(null, new object[] { true, new System.Action<string, Color>((_, _) => { }), new System.Action(() => { }) }),
            close: () => icons.GetMethod("ShowScreen", statics)!
                .Invoke(null, new object[] { false, new System.Action<string, Color>((_, _) => { }), new System.Action(() => { }) }));

        // note tool panel - parented into the tool parameter menu, as when the note tool is picked
        var notes = Screen("NoteToolScreen");
        yield return OpenCheckClose("note-tool-panel", notes,
            open: () => notes.GetMethod("ShowScreen", statics)!.Invoke(null, new object[] { true }),
            close: () => notes.GetMethod("ShowScreen", statics)!.Invoke(null, new object[] { false }));
    }

    /// <summary>Opens one screen, asserts it is up and bound, screenshots it, and closes it -
    /// through <paramref name="close"/> if given, else KScreen.Show(false).</summary>
    private static IEnumerator OpenCheckClose(string label, Type screenType, System.Action open, System.Action? close = null)
    {
        Component? screen = null;
        try
        {
            try
            {
                open();
            }
            catch (TargetInvocationException e)
            {
                throw new HarnessAssertException($"{label}: opening threw {e.InnerException}");
            }
            ///two frames: layout groups and content size fitters settle on the frame after activation
            yield return null;
            yield return null;

            screen = (Component?)screenType.GetField("Instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!
                .GetValue(null);
            Assert.True(screen != null, $"{label}: the screen instance exists");
            Assert.True(screen!.gameObject.activeInHierarchy, $"{label}: the screen is active");

            var unbound = UnboundUiFields(screen);
            Log?.Line($"  {label}: {screen.GetComponentsInChildren<RectTransform>(true).Length} nodes; unbound non-nullable UI fields: " +
                      (unbound.Count == 0 ? "none" : string.Join(", ", unbound)));
            CollectionAssert.SameItems(Array.Empty<string>(), unbound, $"{label}: UI fields left unbound after opening");

            yield return Screenshot.Capture("dialog-" + label, Log);
        }
        finally
        {
            if (close != null)
                close();
            else if (screen != null && screen is KScreen kscreen)
                kscreen.Show(false);
        }
    }

    /// <summary>Instance fields on the screen typed as a Unity object (a widget, component or
    /// GameObject), declared non-nullable, and still null - i.e. a child path Init did not find, or
    /// a field it never assigned. Nullability is read from the compiler's NullableAttribute /
    /// NullableContextAttribute, so an optional <c>T?</c> field is not reported.</summary>
    private static List<string> UnboundUiFields(Component screen)
    {
        var result = new List<string>();
        for (var type = screen.GetType(); type != null && type.Assembly == typeof(Blueprint).Assembly; type = type.BaseType)
        {
            foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if (!typeof(UnityEngine.Object).IsAssignableFrom(field.FieldType) || !DeclaredNonNullable(field))
                    continue;
                if ((UnityEngine.Object?)field.GetValue(screen) == null)
                    result.Add($"{type.Name}.{field.Name}");
            }
        }
        return result;
    }

    private static bool DeclaredNonNullable(FieldInfo field)
    {
        static byte? Flag(IEnumerable<CustomAttributeData> attributes, string name)
        {
            var a = attributes.FirstOrDefault(x => x.AttributeType.Name == name);
            if (a == null || a.ConstructorArguments.Count == 0)
                return null;
            var arg = a.ConstructorArguments[0].Value;
            return arg switch
            {
                byte b => b,
                IReadOnlyCollection<CustomAttributeTypedArgument> list when list.Count > 0 => (byte)list.First().Value!,
                _ => null,
            };
        }
        var own = Flag(field.CustomAttributes, "NullableAttribute");
        if (own.HasValue)
            return own.Value == 1;
        for (var t = field.DeclaringType; t != null; t = t.DeclaringType)
        {
            var context = Flag(t.CustomAttributes, "NullableContextAttribute");
            if (context.HasValue)
                return context.Value == 1;
        }
        return false;
    }

    // ---- #79: snap-to-grid ---------------------------------------------

    /// <summary>
    /// The GridSnap row is built at runtime by the screen, out of a cloned sibling row and two
    /// inputs cloned from the note tool's screen: upstream ships it in their cc28b8b bundle, which
    /// postdates their relicense, so this fork stays on its own bundles and assembles the row
    /// instead (docs/blueprints-included/asset-provenance.md). None of that is inspectable
    /// offline. This checks the screen really assembles and wires it - the toggle and both step
    /// inputs exist, the row is a sibling of the template it was cloned from rather than the
    /// template itself, and selecting a blueprint defaults the step to its footprint.
    /// </summary>
    // CurrentBlueprintStateScreen is internal - reach it by reflection.
    private static readonly Type StateScreenType =
        typeof(Blueprint).Assembly.GetType("BlueprintsV2.UnityUI.CurrentBlueprintStateScreen")!;

    private static IEnumerator GridSnapRowIsWired()
    {
        var bp = Snapshot(TileRowTopLeft(), TileRowBottomRight());
        var st = BlueprintState.CurrentStateInfo();
        st.IsPlacingSnapshot = true;
        try
        {
            StateScreenType.GetMethod("ShowScreen")!.Invoke(null, new object[] { true });
            yield return null;
            var screen = (Component?)StateScreenType.GetField("Instance")!.GetValue(null);
            Assert.True(screen != null, "the blueprint state screen was created");

            var row = screen!.transform.Find("InfoItemsContainer/GridSnap");
            Assert.True(row != null, "the screen built an InfoItemsContainer/GridSnap row");

            ///the standing guard against a post-relicense bundle being re-imported: the prefab the
            ///screen is instantiated from must NOT contain the row. ModAssets is internal - reach
            ///its loaded prefab by reflection, as the cases above do.
            var statePrefab = (GameObject)typeof(Blueprint).Assembly.GetType("BlueprintsV2.ModAssets")!
                .GetField("BlueprintInfoStateGO")!.GetValue(null)!;
            Assert.True(statePrefab.transform.Find("InfoItemsContainer/GridSnap") == null,
                "the shipped bundle has no GridSnap row - the screen builds it");

            ///and it lands directly after the row it is cloned from, rather than anywhere.
            var template = screen.transform.Find("InfoItemsContainer/ApplySettingsToExisting");
            Assert.True(template != null, "the ApplySettingsToExisting row the GridSnap row is cloned from exists");
            Assert.Equal(template!.GetSiblingIndex() + 1, row!.GetSiblingIndex(),
                "the GridSnap row sits directly after the row it was cloned from");

            ///a clone inherits its template's LocText key, which would resolve back to the
            ///template's string the moment a language is applied - silent in English.
            var label = row.Find("Label")!.GetComponent<LocText>();
            Assert.True(string.IsNullOrEmpty(label.key), "the cloned row's label does not keep the template's LocText key");
            Assert.True(row!.GetComponent<UtilLibs.UIcmp.FToggle>() != null, "the GridSnap row is wired as a toggle");
            foreach (var input in new[] { "WidthInput", "HeightInput" })
            {
                var field = row.Find(input);
                Assert.True(field != null && field.GetComponent<UtilLibs.UIcmp.FInputField2>() != null,
                    $"GridSnap/{input} exists and is wired as an input");
            }

            StateScreenType.GetMethod("SetSelectedBlueprint")!.Invoke(screen, new object[] { bp });
            var size = bp.FootprintSize();
            Log?.Line($"  footprint {size.x}x{size.y}, step {st.GridSnapX}x{st.GridSnapY}");
            Assert.Equal(size.x, st.GridSnapX, "selecting a blueprint defaults the X step to its footprint");
            Assert.Equal(size.y, st.GridSnapY, "selecting a blueprint defaults the Y step to its footprint");

            ///#117: the step fields are placed by hand, and a clone that keeps the note title's
            ///anchors spans the panel with the other hidden behind it. Wait out ShowScreen's
            ///one-frame reactivation so the rects are the laid-out ones.
            yield return null;
            Canvas.ForceUpdateCanvases();
            var rowRect = WorldRect(row);
            var widthRect = WorldRect(row.Find("WidthInput")!);
            var heightRect = WorldRect(row.Find("HeightInput")!);
            var checkRect = WorldRect(row.Find("Checkbox")!);
            Log?.Line($"  row {rowRect}, width {widthRect}, height {heightRect}, checkbox {checkRect}");            foreach (var (name, r) in new[] { ("WidthInput", widthRect), ("HeightInput", heightRect) })
            {
                Assert.True(r.width > 1f && r.height > 1f, $"GridSnap/{name} has a visible size");
                Assert.True(Contains(rowRect, r), $"GridSnap/{name} sits inside its row");
                Assert.True(!r.Overlaps(checkRect), $"GridSnap/{name} does not cover the checkbox");
            }
            Assert.True(!widthRect.Overlaps(heightRect), "the two step fields do not overlap");
            Assert.True(widthRect.xMax <= heightRect.xMin, "the width field sits left of the height field");

            ///layout is asserted above; whether it *looks* right still needs a frame to look at.
            yield return Screenshot.Capture("grid-snap-row", Log);
        }
        finally
        {
            StateScreenType.GetMethod("ShowScreen")!.Invoke(null, new object[] { false });
            st.IsPlacingSnapshot = false;
        }
    }

    private static Rect WorldRect(Transform t)
    {
        var corners = new Vector3[4];
        ((RectTransform)t).GetWorldCorners(corners);
        return Rect.MinMaxRect(corners[0].x, corners[0].y, corners[2].x, corners[2].y);
    }

    ///half a unit of slack: the fields are sized to the checkbox, which may sit flush with the row.
    private static bool Contains(Rect outer, Rect inner) =>
        inner.xMin >= outer.xMin - 0.5f && inner.xMax <= outer.xMax + 0.5f
        && inner.yMin >= outer.yMin - 0.5f && inner.yMax <= outer.yMax + 0.5f;

    /// <summary>
    /// A snap-to-grid drag places whole copies of the blueprint, one step apart, and a cursor that
    /// jumps two and a half steps in one mouse move still gets both copies in between (upstream
    /// placed none). Drives the same <c>GridSnapDrag</c> the tools use, through its internal
    /// click and move halves, against the real <c>BlueprintState.UseBlueprint</c>.
    /// </summary>
    private static IEnumerator GridSnapDragPlacesCopies()
    {
        var bp = Snapshot(TileRowTopLeft(), TileRowBottomRight());
        var size = bp.FootprintSize();
        int pieces = bp.BuildingConfigurations.Count(b => !b.BuildingDisabled);
        Assert.True(pieces > 0, "the tile-row snapshot captured something");

        var anchorXY = Grid.CellToXY(AnchorCell);
        var target = new Vector2I(anchorXY.x - 30, anchorXY.y + 8);
        var region = new List<int>();
        for (int dx = -12; dx <= 3 * size.x + 12; dx++)
            for (int dy = -6; dy <= size.y + 6; dy++)
            {
                int c = Grid.XYToCell(target.x + dx, target.y + dy);
                if (Grid.IsValidCell(c))
                    region.Add(c);
            }
        foreach (int c in region)
        {
            if (Grid.IsSolidCell(c))
                SimMessages.Dig(c, skipEvent: true);
            Grid.Reveal(c, byte.MaxValue, forceReveal: true);
        }
        for (int i = 0; i < 600 && region.Any(Grid.IsSolidCell); i++)
            yield return null;

        var before = Constructables();
        var cfg = ModConfig();
        bool savedTech = cfg.RequireConstructable_Tech, savedMat = cfg.RequireConstructable_Material;
        cfg.RequireConstructable_Tech = false;
        cfg.RequireConstructable_Material = false;
        var st = BlueprintState.CurrentStateInfo();
        st.IsPlacingSnapshot = true;
        st.ResetRotations();
        st.SnapToGrid = true;
        st.GridSnapX = size.x;
        st.GridSnapY = size.y;

        var drag = new BlueprintsV2.Tools.GridSnapDrag();
        var onPlaced = typeof(BlueprintsV2.Tools.GridSnapDrag).GetMethod("OnPlaced", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var onMoved = typeof(BlueprintsV2.Tools.GridSnapDrag).GetMethod("OnMoved", BindingFlags.NonPublic | BindingFlags.Instance)!;
        try
        {
            BlueprintState.VisualizeBlueprint(target, bp);
            for (int i = 0; i < 5; i++) yield return null;

            ///the click: place at the target and start the drag, as the tools do
            BlueprintState.UseBlueprint(BlueprintState.PlayerId_DefaultTilePreviews, target, bp);
            onPlaced.Invoke(drag, new object[] { target });
            Assert.True(drag.IsDragging, "a click with snapping on starts a drag");

            ///one mouse move two and a half steps to the right
            var cursor = new Vector2I(target.x + size.x * 5 / 2, target.y);
            BlueprintState.UpdateVisual(BlueprintState.PlayerId_DefaultTilePreviews, cursor, false, bp);
            onMoved.Invoke(drag, new object[] { cursor, bp });
            for (int i = 0; i < 15; i++) yield return null;

            var orders = Constructables().Where(c => !before.Contains(c))
                .Select(c => Grid.CellToXY(Grid.PosToCell(c.gameObject)))
                .ToList();
            Log?.Line($"  footprint {size.x}x{size.y}, {pieces} piece(s) per copy, {orders.Count} order(s): " +
                      string.Join(" ", orders.OrderBy(o => o.x).Select(o => $"({o.x},{o.y})")));

            Assert.Equal(3 * pieces, orders.Count, "three copies (click + two drag steps) of every piece");
            ///the anchor shift moves pieces relative to the target, so measure from the leftmost
            ///order - copy 0's footprint edge. Edge-to-edge copies each fill one footprint width.
            int minX = orders.Min(o => o.x);
            var perCopy = orders.GroupBy(o => (o.x - minX) / size.x)
                .ToDictionary(g => g.Key, g => g.Count());
            CollectionAssert.SameItems(new[] { 0, 1, 2 }, perCopy.Keys, "copies sit at step 0, 1 and 2");
            Assert.True(perCopy.Values.All(n => n == pieces), "each copy has every piece");
        }
        finally
        {
            drag.End();
            BlueprintState.ClearVisuals();
            st.SnapToGrid = false;
            st.IsPlacingSnapshot = false;
            cfg.RequireConstructable_Tech = savedTech;
            cfg.RequireConstructable_Material = savedMat;
        }
    }

    // ---- #68: the note toggle's tooltip names the current key ----------------------

    /// <summary>
    /// The top-left note-visibility button's tooltip used to be written once, when the screen
    /// activated, so a mid-session rebind left it advertising the old key (#68). Rebinds the
    /// action in <c>GameInputMapping.KeyBindings</c> - the table <c>GetHotkeyString</c> reads - and
    /// asserts the tooltip's text follows. The binding is restored in a <c>finally</c>.
    /// </summary>
    private static IEnumerator NoteToggleTooltipFollowsARebind()
    {
        var button = UnityEngine.Object.FindObjectsByType<MultiToggle>(FindObjectsSortMode.None)
            .FirstOrDefault(t => t.name == "toggleNoteVisibility");
        Assert.True(button != null, "the note-visibility button exists on the top-left screen");
        Assert.True(button!.TryGetComponent<ToolTip>(out var tooltip), "the button has a ToolTip");
        Assert.True(tooltip.OnToolTip != null, "the tooltip is rebuilt when shown (OnToolTip set)");

        // ModAssets.Actions is internal and PAction is PLib's, which the harness doesn't
        // reference - reach GetKAction() by reflection.
        var pAction = typeof(Blueprint).Assembly
            .GetType("BlueprintsV2.ModAssets+Actions")!
            .GetProperty("BlueprintsToggleNoteVisibility", BindingFlags.Public | BindingFlags.Static)!
            .GetValue(null)!;
        var action = (Action)pAction.GetType().GetMethod("GetKAction")!.Invoke(pAction, null)!;

        var bindings = GameInputMapping.KeyBindings;
        int index = Array.FindIndex(bindings, b => b.mAction == action);
        Assert.True(index >= 0, $"a key binding exists for {action}");

        var saved = bindings[index];
        try
        {
            ///unbound first: GetHotkeyString renders that as "[NONE]", which must not reach the
            ///tooltip.
            var unbound = saved;
            unbound.mKeyCode = KKeyCode.None;
            bindings[index] = unbound;
            string noneKey = GameUtil.GetHotkeyString(action);
            string before = tooltip.OnToolTip!();
            Log?.Line($"  unbound: \"{before}\"");
            Assert.True(!before.Contains(noneKey), $"an unbound key is not shown as [{noneKey}]");

            var rebound = saved;
            rebound.mKeyCode = KKeyCode.F9;
            bindings[index] = rebound;
            string expectedKey = GameUtil.GetHotkeyString(action);
            string after = tooltip.OnToolTip!();
            Log?.Line($"  rebound to {rebound.mKeyCode}: \"{after}\"");
            Assert.True(after.Contains(expectedKey), $"the tooltip names the new key [{expectedKey}]");
        }
        finally
        {
            bindings[index] = saved;
        }
        yield break;
    }

    // ---- #63: selecting a note must not write its own text back -------------------

    // TextNoteSideScreen and TextNote are internal - reach both by reflection.
    private static readonly Type NoteSideScreenType =
        typeof(Blueprint).Assembly.GetType("BlueprintsV2.UnityUI.TextNoteSideScreen")!;
    private static readonly Type TextNoteType =
        typeof(Blueprint).Assembly.GetType("BlueprintsV2.BlueprintData.NoteToolPlacedEntities.TextNote")!;

    private static Component CreateTextNote(int cell, string title, string text, string symbol) =>
        (Component)TextNoteType.GetMethod("Create", BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, new object[] { cell, title, text, symbol, Color.white, false })!;

    private static int noteUpdateInfoCalls;
    private static void CountNoteUpdateInfo() => noteUpdateInfoCalls++;

    /// <summary>
    /// Selecting a text note pushes the note's own title and text into the side screen's input
    /// fields. Those pushes used to come back through the fields' change handlers - the raw
    /// OnValueChanged event ignores FInputField2's DataTextUpdate guard - and write the note's
    /// values straight back to it, firing the multiplayer note sync on every selection (#63).
    ///
    /// Also covers the trap in fixing it: the spurious fire was the only thing repainting the two
    /// clear buttons on this path, so suppressing it without an explicit refresh would leave them
    /// showing the previously selected note's state.
    /// </summary>
    private static IEnumerator NoteSideScreenSelection()
    {
        var xy = Grid.CellToXY(AnchorCell);
        int cellA = Grid.XYToCell(xy.x - 7, xy.y - 3), cellB = Grid.XYToCell(xy.x - 6, xy.y - 3);
        foreach (int c in new[] { cellA, cellB })
            if (Grid.IsSolidCell(c))
                SimMessages.Dig(c, skipEvent: true);
        for (int i = 0; i < 30 && (Grid.IsSolidCell(cellA) || Grid.IsSolidCell(cellB)); i++)
            yield return null;

        var symbolMap = (System.Collections.IDictionary)AccessTools.Field(TextNoteType, "SymbolMap").GetValue(null)!;
        string symbol = symbolMap.Keys.Cast<string>().FirstOrDefault() ?? string.Empty;
        var withText = CreateTextNote(cellA, "Title A", "Text A", symbol);
        var empty = CreateTextNote(cellB, string.Empty, string.Empty, symbol);
        Assert.True(withText != null && empty != null, "created two text notes");

        var screen = (Component?)UnityEngine.Object.FindObjectsByType(NoteSideScreenType, FindObjectsSortMode.None).FirstOrDefault();
        Assert.True(screen != null, "the text-note side screen exists");
        screen!.gameObject.SetActive(true);
        for (int i = 0; i < 3; i++) yield return null;
        Assert.True((bool)AccessTools.Field(NoteSideScreenType, "spawned").GetValue(screen)!,
            "the side screen has spawned, so it pushes text into its inputs");

        var setTarget = NoteSideScreenType.GetMethod("SetTarget")!;
        var titleInput = (UtilLibs.UIcmp.FInputField2)AccessTools.Field(NoteSideScreenType, "TitleInput").GetValue(screen)!;
        var clearTitle = AccessTools.Field(NoteSideScreenType, "ClearTitle").GetValue(screen)!;
        var clearText = AccessTools.Field(NoteSideScreenType, "ClearText").GetValue(screen)!;
        var interactable = AccessTools.Field(typeof(UtilLibs.UIcmp.FButton), "interactable");
        bool Interactable(object button) => (bool)interactable.GetValue(button)!;

        var harmony = new Harmony("bpi-harness-63");
        try
        {
            harmony.Patch(AccessTools.Method(TextNoteType, "UpdateInfo"),
                prefix: new HarmonyMethod(AccessTools.Method(typeof(HarnessCases), nameof(CountNoteUpdateInfo))));

            setTarget.Invoke(screen, new object[] { withText!.gameObject });
            for (int i = 0; i < 3; i++) yield return null;

            noteUpdateInfoCalls = 0;
            setTarget.Invoke(screen, new object[] { empty!.gameObject });
            for (int i = 0; i < 3; i++) yield return null;

            Log?.Line($"  selecting a note: {noteUpdateInfoCalls} write-back(s), " +
                      $"title field \"{titleInput.Text}\", clear buttons title={Interactable(clearTitle)} text={Interactable(clearText)}");

            Assert.Equal(0, noteUpdateInfoCalls, "selecting a note writes nothing back to it");
            Assert.Equal(string.Empty, titleInput.Text, "the field shows the newly selected note's title");
            Assert.True(!Interactable(clearTitle) && !Interactable(clearText),
                "the clear buttons follow the new note rather than keeping the previous one's state");

            ///and back to the note that has text: the buttons come alive again
            setTarget.Invoke(screen, new object[] { withText.gameObject });
            for (int i = 0; i < 3; i++) yield return null;
            Assert.Equal("Title A", titleInput.Text, "the field shows the re-selected note's title");
            Assert.True(Interactable(clearTitle) && Interactable(clearText),
                "the clear buttons are enabled for a note that has text");
            Assert.Equal(0, noteUpdateInfoCalls, "still nothing written back");
        }
        finally
        {
            harmony.UnpatchAll("bpi-harness-63");
            NoteSideScreenType.GetMethod("ClearTarget")!.Invoke(screen, null);
            screen.gameObject.SetActive(false);
            BlueprintNote.ClearExistingNote(cellA);
            BlueprintNote.ClearExistingNote(cellB);
        }
    }

    // ---- #76: the back-wall requirement is checked per cell ------------------------

    /// <summary>
    /// A building that must attach to a back wall may be placed over a back wall the same blueprint
    /// brings with it - but only where the back wall really is. Asserts both halves of that
    /// carve-out through the per-cell sweep that replaced the old anchor-cell-only check: the
    /// building over the blueprint's own back wall is accepted, the same building without one is
    /// refused.
    ///
    /// Also probes what this install has: every def with <c>BuildLocationRule.OnBackWall</c> and
    /// its size, plus whether the map carries any real back wall. Both decide which of #76's
    /// defects are reachable at all, so they are logged rather than assumed.
    /// </summary>
    private static IEnumerator BackwallCoversEveryCell()
    {
        var onBackwall = Assets.BuildingDefs
            .Where(d => d != null && d.BuildLocationRule == BuildLocationRule.OnBackWall)
            .ToList();
        Log?.Line("  defs requiring a back wall: " + (onBackwall.Count == 0 ? "(none)" :
            string.Join(", ", onBackwall.Select(d => d.PrefabID + " " + d.WidthInCells + "x" + d.HeightInCells))));
        if (!onBackwall.Any(d => d.WidthInCells >= 2 || d.HeightInCells >= 2))
            Log?.Line("  REACHABILITY: every one of them is 1x1 here, so the multi-cell half of #76 " +
                      "(a building hanging off the end of a short back wall) cannot occur in this " +
                      "install - the per-cell sweep still runs, over one cell");

        var anchorXY = Grid.CellToXY(AnchorCell);
        var target = new Vector2I(anchorXY.x - 34, anchorXY.y + 12);
        int realBackwall = 0;
        for (int dx = -14; dx <= 14; dx++)
            for (int dy = -8; dy <= 8; dy++)
            {
                int c = Grid.XYToCell(target.x + dx, target.y + dy);
                if (Grid.IsValidCell(c) && BackwallManager.HasBackwall(c))
                    realBackwall++;
            }
        Log?.Line("  cells with a real back wall in the test area: " + realBackwall +
                  (realBackwall == 0
                      ? " - REACHABILITY: so #76's wrongly-rejected-over-a-real-back-wall half is unexercised"
                      : string.Empty));

        var wallDef = onBackwall.FirstOrDefault();
        var backwallDef = Assets.BuildingDefs.FirstOrDefault(d =>
            d != null && d.ObjectLayer == ObjectLayer.Backwall
            && d.WidthInCells == 1 && d.HeightInCells == 1 && d.BuildingComplete != null);

        if (wallDef == null || backwallDef == null)
        {
            Log?.Line("  REACHABILITY: this install has no OnBackWall def and/or no 1x1 back-wall " +
                      "building, so the carve-out is unexercised - the case is shape-based, so a " +
                      "DLC adding one would be covered as written");
            yield break;
        }
        Log?.Line("  using " + wallDef.PrefabID + " (" + wallDef.PlacementOffsets.Length +
                  " cell(s)) over " + backwallDef.PrefabID);

        yield return ClearRegion(target, 14, 8);

        bool? withWall = null, withoutWall = null;
        var st = BlueprintState.CurrentStateInfo();
        st.IsPlacingSnapshot = true;
        try
        {
            foreach (bool includeBackwall in new[] { false, true })
            {
                var bp = new Blueprint("harness-backwall-" + (includeBackwall ? "with" : "without"), "");
                AddConfig(bp, wallDef, new Vector2I(0, 0));
                if (includeBackwall)
                    foreach (var offset in wallDef.PlacementOffsets)
                        AddConfig(bp, backwallDef, new Vector2I(offset.x, offset.y));
                bp.CacheCost();

                BlueprintState.VisualizeBlueprint(target, bp);
                for (int i = 0; i < 6; i++) yield return null;

                var visual = LiveVisuals().OfType<BuildingVisual>()
                    .FirstOrDefault(v => v.BuildingID == wallDef.PrefabID);
                Assert.True(visual != null, "the " + wallDef.PrefabID + " visual exists");
                bool accepted = visual!.ValidCell(visual.CurrentCell, out _);
                Log?.Line("  back wall in the blueprint: " + includeBackwall + ", accepted=" + accepted);
                if (includeBackwall) withWall = accepted; else withoutWall = accepted;

                BlueprintState.ClearVisuals();
                for (int i = 0; i < 3; i++) yield return null;
            }

            Assert.True(withWall == true, "a back-wall building over the blueprint's own back wall is accepted");
            Assert.True(withoutWall == false, "the same building with no back wall under it is refused");
        }
        finally
        {
            BlueprintState.ClearVisuals();
            st.IsPlacingSnapshot = false;
        }
    }

    /// <summary>
    /// The occupancy map a blueprint's visuals register (<c>BlueprintState.OccupiedCells</c>, which
    /// is what <c>LayerOccupiedAt</c> reads back) must follow the blueprint's rotation. It used to
    /// store the complete prefab's neutral offsets, so a rotated non-square building claimed cells
    /// it does not cover (#76).
    ///
    /// Drives <c>StoreOccupiedArea</c> against a visual built directly, rather than through
    /// <c>VisualizeBlueprint</c>: the defs that show the bug are ones a synthetic blueprint does
    /// not produce visuals for here, and the store step is the whole subject of the assertion.
    /// </summary>
    private static IEnumerator RotatedOccupancyFollowsTheRotation()
    {
        var def = Assets.BuildingDefs.FirstOrDefault(d =>
            d != null && d.WidthInCells != d.HeightInCells
            && d.BuildingComplete != null && d.BuildingComplete.GetComponent<OccupyArea>() != null
            && d.BuildingPreview != null
            && d.PermittedRotations == PermittedRotations.R360);
        if (def == null)
        {
            Log?.Line("  REACHABILITY: no rotatable non-square building with an occupy area in this " +
                      "install, so rotated occupancy is unexercised");
            yield break;
        }

        var anchorXY = Grid.CellToXY(AnchorCell);
        int cell = Grid.XYToCell(anchorXY.x - 34, anchorXY.y - 14);
        yield return ClearRegion(new Vector2I(anchorXY.x - 34, anchorXY.y - 14), 6, 6);

        var config = new BuildingConfig
        {
            Offset = new Vector2I(0, 0),
            BuildingDef = def,
            BuildingDefId = def.PrefabID,
            Orientation = Orientation.Neutral,
        };
        foreach (var tag in FixtureBuilder.SelectElements(def))
            config.SelectedElements.Add(tag);

        var storeOccupiedArea = AccessTools.Method(typeof(BlueprintState), "StoreOccupiedArea");
        var clearOccupiedCells = AccessTools.Method(typeof(BlueprintState), "ClearOccupiedCells");
        var visual = new BuildingVisual(config, cell, BlueprintState.PlayerId_DefaultTilePreviews);
        try
        {
            visual.ApplyRotation(Orientation.R90, false, false);
            Assert.Equal(Orientation.R90, visual.RotatedOrientation, "the visual took the rotation");

            clearOccupiedCells.Invoke(null, new object[] { BlueprintState.PlayerId_DefaultTilePreviews });
            storeOccupiedArea.Invoke(null, new object[] { BlueprintState.PlayerId_DefaultTilePreviews, visual });

            var area = def.BuildingComplete.GetComponent<OccupyArea>();
            ///the unrotated offsets, not the OccupiedCellsOffsets property: that property rotates
            ///by the shared prefab's own Rotatable, so deriving the expectation from it would drift
            ///with the production code instead of pinning it.
            var offsets = area._UnrotatedOccupiedCellsOffsets ?? area.OccupiedCellsOffsets;
            var neutral = offsets.Select(o => Grid.OffsetCell(cell, o)).OrderBy(c => c).ToList();
            var expected = offsets
                .Select(o => Grid.OffsetCell(cell, Rotatable.GetRotatedCellOffset(o, Orientation.R90)))
                .OrderBy(c => c).ToList();
            Assert.True(expected.Count > 1, "the occupy area really covers more than one cell");
            var stored = BlueprintState.OccupiedCells[BlueprintState.PlayerId_DefaultTilePreviews][def.ObjectLayer]
                .Where(kv => kv.Value == visual).Select(kv => kv.Key).OrderBy(c => c).ToList();

            Log?.Line("  " + def.PrefabID + " " + def.WidthInCells + "x" + def.HeightInCells +
                      " rotated R90: stored " + string.Join(" ", stored.Select(c => Grid.CellToXY(c).ToString())));
            Log?.Line("  unrotated would have been " + string.Join(" ", neutral.Select(c => Grid.CellToXY(c).ToString())));

            Assert.True(!expected.SequenceEqual(neutral),
                "this def's footprint really changes under rotation, so the assertion below can fail");
            CollectionAssert.SameItems(expected, stored, "the stored cells are the rotated footprint");
        }
        finally
        {
            clearOccupiedCells.Invoke(null, new object[] { BlueprintState.PlayerId_DefaultTilePreviews });
            visual.DestroyVisualizer();
        }
    }

    /// <summary>
    /// The half of #76 that needed a real back wall: over one, <c>LayerOccupiedAt</c> used to
    /// answer "occupied" for the building's own layer too, so the carve-out never fired.
    ///
    /// The cell has to be <b>solid</b> for that to show. The game refuses a back-wall building on
    /// a buried cell even when the back wall is there (<c>CheckBackWallFoundation</c> tests
    /// <c>Grid.Solid</c> as well), which is the case this mod means to wave through - it queues
    /// build orders inside rock everywhere else, and a dupe digs it out. On an open cell with a
    /// real back wall the game simply says yes, so the old code's answer never mattered there.
    /// </summary>
    private static IEnumerator BackwallOverRealBackwall()
    {
        var wallDef = Assets.BuildingDefs.FirstOrDefault(d =>
            d != null && d.BuildLocationRule == BuildLocationRule.OnBackWall
            && d.WidthInCells == 1 && d.HeightInCells == 1 && d.BuildingPreview != null);
        if (wallDef == null)
        {
            Log?.Line("  REACHABILITY: no 1x1 OnBackWall def in this install");
            yield break;
        }

        ///deliberately not dug: solid is the precondition, see the summary. Searched rather than
        ///hard-coded, since which cells the fixture colony has already dug out is not fixed.
        var anchorXY = Grid.CellToXY(AnchorCell);
        int wallCell = Grid.InvalidCell, bareCell = Grid.InvalidCell;
        for (int dy = -30; dy <= 30 && bareCell == Grid.InvalidCell; dy++)
            for (int dx = -45; dx <= 45 && bareCell == Grid.InvalidCell; dx++)
            {
                int c = Grid.XYToCell(anchorXY.x + dx, anchorXY.y + dy);
                int neighbour = Grid.XYToCell(anchorXY.x + dx + 2, anchorXY.y + dy);
                if (!Grid.IsValidCell(c) || !Grid.IsValidCell(neighbour))
                    continue;
                if (Grid.IsSolidCell(c) && !Grid.Foundation[c]
                    && Grid.IsSolidCell(neighbour) && !Grid.Foundation[neighbour]
                    && !BackwallManager.HasBackwall(c) && !BackwallManager.HasBackwall(neighbour)
                    && Grid.Objects[c, (int)ObjectLayer.Building] == null
                    && Grid.Objects[neighbour, (int)ObjectLayer.Building] == null)
                {
                    wallCell = c;
                    bareCell = neighbour;
                }
            }

        if (wallCell == Grid.InvalidCell)
        {
            Log?.Line("  REACHABILITY: no pair of solid, back-wall-free cells near the fixture, so " +
                      "the buried-cell-with-a-back-wall case is unexercised");
            yield break;
        }
        foreach (int c in new[] { wallCell, bareCell })
            Grid.Reveal(c, byte.MaxValue, forceReveal: true);
        Log?.Line("  buried test cells: " + Grid.CellToXY(wallCell) + " and " + Grid.CellToXY(bareCell));
        Assert.True(!BackwallManager.HasBackwall(wallCell) && !BackwallManager.HasBackwall(bareCell),
            "the test cells start without a back wall");

        SimMessages.SetBackwallData(wallCell,
            (ushort)ElementLoader.GetElementIndex(SimHashes.SandStone), 200f, 293.15f);
        for (int i = 0; i < 60 && !BackwallManager.HasBackwall(wallCell); i++)
            yield return null;
        if (!BackwallManager.HasBackwall(wallCell))
        {
            Log?.Line("  REACHABILITY: SetBackwallData did not take while the sim is paused, so " +
                      "the real-back-wall half is unexercised");
            yield break;
        }

        var config = new BuildingConfig
        {
            Offset = new Vector2I(0, 0),
            BuildingDef = wallDef,
            BuildingDefId = wallDef.PrefabID,
            Orientation = Orientation.Neutral,
        };
        foreach (var tag in FixtureBuilder.SelectElements(wallDef))
            config.SelectedElements.Add(tag);

        var visual = new BuildingVisual(config, wallCell, BlueprintState.PlayerId_DefaultTilePreviews);
        try
        {
            ///pin why the cell is being refused, so this cannot pass for an unrelated reason
            wallDef.IsValidPlaceLocation(visual.Visualizer, wallCell, Orientation.Neutral, out string wallReason);
            wallDef.IsValidPlaceLocation(visual.Visualizer, bareCell, Orientation.Neutral, out string bareReason);
            Log?.Line("  the game on the back-wall cell: \"" + wallReason + "\", on the bare cell: \"" + bareReason + "\"");
            string required = global::STRINGS.UI.TOOLTIPS.HELP_BUILDLOCATION_BACK_WALL_REQUIRED.ToString();
            Assert.Equal(required, wallReason, "the game refuses the buried back-wall cell for the back-wall reason");
            Assert.Equal(required, bareReason, "the game refuses the bare cell for the back-wall reason");

            bool overWall = visual.ValidCell(wallCell, out _);
            bool overNothing = visual.ValidCell(bareCell, out _);
            Log?.Line("  over a real back wall: accepted=" + overWall + ", one cell away: accepted=" + overNothing);

            Assert.True(overWall, "a back-wall building over a real back wall is accepted");
            Assert.True(!overNothing, "the same building one cell away, with no back wall, is refused");
        }
        finally
        {
            visual.DestroyVisualizer();
            SimMessages.Dig(wallCell, skipEvent: true, backwall: true);
        }
        for (int i = 0; i < 60 && BackwallManager.HasBackwall(wallCell); i++)
            yield return null;
        Log?.Line("  back wall removed: " + !BackwallManager.HasBackwall(wallCell));
    }

    private static void AddConfig(Blueprint bp, BuildingDef def, Vector2I offset)
    {
        var config = new BuildingConfig
        {
            Offset = offset,
            BuildingDef = def,
            BuildingDefId = def.PrefabID,
            Orientation = Orientation.Neutral,
        };
        foreach (var tag in FixtureBuilder.SelectElements(def))
            config.SelectedElements.Add(tag);
        bp.BuildingConfigurations.Add(config);
    }

    /// <summary>Digs and reveals a box around <paramref name="centre"/>, as PlaceAt does for its
    /// own target.</summary>
    private static IEnumerator ClearRegion(Vector2I centre, int halfWidth, int halfHeight)
    {
        var region = new List<int>();
        for (int dx = -halfWidth; dx <= halfWidth; dx++)
            for (int dy = -halfHeight; dy <= halfHeight; dy++)
            {
                int c = Grid.XYToCell(centre.x + dx, centre.y + dy);
                if (Grid.IsValidCell(c))
                    region.Add(c);
            }
        foreach (int c in region)
        {
            if (Grid.IsSolidCell(c))
                SimMessages.Dig(c, skipEvent: true);
            Grid.Reveal(c, byte.MaxValue, forceReveal: true);
        }
        for (int i = 0; i < 600 && region.Any(Grid.IsSolidCell); i++)
            yield return null;
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

    /// <summary>
    /// The blueprints_ui bundle carries white placeholders for the sprites that are the game's own
    /// art, and <c>ModAssets.UseGameSprites</c> swaps the real ones in by name after
    /// <c>Assets.OnPrefabInit</c>. If the swap misses, the UI shows white boxes, and nothing
    /// throws. So assert it: every name was found, and no image in the five prefabs still shows a
    /// sprite the bundle itself carries under one of those names.
    /// </summary>
    private static IEnumerator GameSpritesReplaceBundleArt()
    {
        var modAssets = typeof(BlueprintsV2.BlueprintData.Blueprint).Assembly.GetType("BlueprintsV2.ModAssets")!;
        const BindingFlags statics = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        var names = new HashSet<string>((string[])modAssets.GetField("GameSpriteNames", statics)!.GetValue(null)!);
        var missing = (List<string>)modAssets.GetField("MissingGameSprites", statics)!.GetValue(null)!;
        var bundleSprites = (HashSet<Sprite>)modAssets.GetField("bundleSprites", statics)!.GetValue(null)!;

        CollectionAssert.SameItems(Array.Empty<string>(), missing, "game sprites not found at load");
        Assert.True(bundleSprites.Count > 0, "the bundle's own sprites were recorded");

        int swapped = 0;
        var stale = new List<string>();
        foreach (var field in new[] { "BlueprintSelectionScreenGO", "BlueprintInfoStateGO", "NoteToolStateScreenGO", "IconSelectorGO", "RenamingScreenGO" })
        {
            var prefab = (GameObject)modAssets.GetField(field, statics)!.GetValue(null)!;
            foreach (var image in prefab.GetComponentsInChildren<UnityEngine.UI.Image>(true))
            {
                if (image.sprite == null || !names.Contains(image.sprite.name))
                    continue;
                if (bundleSprites.Contains(image.sprite))
                    stale.Add($"{field}:{image.name}={image.sprite.name}");
                else
                    swapped++;
            }
        }
        CollectionAssert.SameItems(Array.Empty<string>(), stale, "images still showing the bundle's copy of a game sprite");
        ///the extracted spec has 44 such images across the five prefabs; well above zero is the
        ///point - it proves the loop saw the game-sprite images at all
        Assert.True(swapped > 0, $"images showing a game sprite: {swapped}");
        Log?.Line($"  game sprites swapped into {swapped} image(s)");
        yield break;
    }

    // Element identity as the stored tag hash - a Tag rebuilt from a hash on read has no
    // resolvable name, so ToString() would spuriously differ.
    private static string Describe(BuildingConfig bc) =>
        $"{bc.BuildingDef?.PrefabID ?? bc.BuildingDefId ?? "<null>"} @ ({bc.Offset.x},{bc.Offset.y}) " +
        $"o={bc.Orientation} elems=[{string.Join(",", bc.SelectedElements.Select(t => t.GetHash()))}]";
}
