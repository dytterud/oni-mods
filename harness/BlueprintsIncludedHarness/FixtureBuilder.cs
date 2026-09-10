using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace BlueprintsV2.Harness;

/// <summary>
/// Places <see cref="FixtureLayout.Buildings"/> into the loaded colony as finished buildings, so
/// the capture case has a known, real-<c>BuildingDef</c> set to assert on. Runs once per harness
/// run (place-every-run - no pre-built save to keep in sync).
/// </summary>
internal static class FixtureBuilder
{
    /// <summary>Places the spec; returns the PrefabIDs that actually went down (skips + logs any
    /// that fail so one bad id doesn't sink the run). Throws only if too few placed to test.</summary>
    public static List<string> PlaceAll(int anchorCell, HarnessLog log)
    {
        var placed = new List<string>();

        foreach (var spec in FixtureLayout.Buildings)
        {
            var def = Assets.GetBuildingDef(spec.PrefabId);
            if (def == null)
            {
                log.Line($"  SKIP {spec.PrefabId}: unknown BuildingDef");
                continue;
            }

            int cell = FixtureLayout.CellOf(spec, anchorCell);
            if (!Grid.IsValidCell(cell))
            {
                log.Line($"  SKIP {spec.PrefabId}: cell {cell} invalid");
                continue;
            }

            // Clear the footprint so placement on the starting-area terrain succeeds.
            def.RunOnArea(cell, spec.Orientation, c =>
            {
                if (Grid.IsSolidCell(c))
                    SimMessages.Dig(c, skipEvent: true);
            });

            var elements = SelectElements(def);
            GameObject? go;
            try
            {
                go = def.Build(cell, spec.Orientation, resource_storage: null, elements,
                               temperature: 293.15f, playsound: false, timeBuilt: 0f);
            }
            catch (Exception e)
            {
                log.Line($"  SKIP {spec.PrefabId}: Build threw {e.GetType().Name}: {e.Message}");
                continue;
            }
            if (go == null)
            {
                log.Line($"  SKIP {spec.PrefabId}: Build returned null at {Grid.CellToXY(cell)}");
                continue;
            }

            log.Line($"  placed {spec.PrefabId} at {Grid.CellToXY(cell)} [{string.Join(",", elements)}]");
            placed.Add(spec.PrefabId);
        }

        if (placed.Count < 2)
            throw new HarnessAssertException($"only {placed.Count} fixture building(s) placed - see harness.log");

        return placed;
    }

    internal static List<Tag> SelectElements(BuildingDef def)
    {
        // First material the mod itself considers valid for each ingredient category, so the
        // JSON round-trip's SanitizeSelectedTags has nothing legitimate to change. Falls back to
        // Sandstone.
        var sandstone = ElementLoader.FindElementByHash(SimHashes.SandStone).tag;
        var elements = new List<Tag>();
        foreach (var category in def.MaterialCategory)
        {
            var valid = ValidMaterials(category);
            elements.Add(valid.Count > 0 ? valid[0] : sandstone);
        }
        return elements;
    }

    // ModAssets.GetValidMaterials is public but on an internal class - reach it by reflection.
    private static readonly MethodInfo GetValidMaterialsMethod =
        typeof(BlueprintsV2.BlueprintData.Blueprint).Assembly
            .GetType("BlueprintsV2.ModAssets")!
            .GetMethod("GetValidMaterials", BindingFlags.Public | BindingFlags.Static)!;

    private static List<Tag> ValidMaterials(Tag category)
    {
        var result = (System.Collections.IEnumerable)GetValidMaterialsMethod.Invoke(null, new object[] { category, true })!;
        return result.Cast<Tag>().ToList();
    }
}
