using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using BlueprintsV2.BlueprintData;
using BlueprintsV2.BlueprintData.NoteToolPlacedEntities;
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
