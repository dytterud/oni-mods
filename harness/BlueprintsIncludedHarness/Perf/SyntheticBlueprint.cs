using BlueprintsV2.BlueprintData;
using UnityEngine;

namespace BlueprintsV2.Harness.Perf;

/// <summary>
/// Builds an N-building <see cref="Blueprint"/> entirely in code (no placement, no live Grid) for
/// import-perf testing. Every building is a single-ingredient BuildableRaw Tile selected with
/// Sandstone, so <c>BuildingConfig.SanitizeSelectedTags</c> never has anything to rewrite - the
/// timed operations measure the import pipeline's own cost, not a material substitution.
/// </summary>
internal static class SyntheticBlueprint
{
    /// <summary>Cell width of a generated blueprint's row - shared with <see cref="Perf.PerfRunner"/>
    /// so its placement-perf dig region lines up with the same layout.</summary>
    internal const int RowWidth = 100;

    /// <summary>Builds a blueprint with <paramref name="count"/> Tile buildings, laid out
    /// <see cref="RowWidth"/> wide. Used directly (in-memory) for placement-perf and via
    /// <see cref="BuildJson"/> (serialized) for import-perf.</summary>
    internal static Blueprint Build(int count) => Build(count, "Tile", "PerfSynthetic");

    /// <summary>
    /// <see cref="Build(int)"/> with the building type and name chosen by the caller.
    ///
    /// <paramref name="buildingId"/> matters for the selection-screen preview sweep: the preview
    /// picks a visualizer per building from <c>ModAssets.GetVisualizerType</c>, and a Tile takes
    /// the cheap <c>Vis_TilePreview</c> (one <c>Image</c>) path while everything else takes
    /// <c>Vis_BuildingPreview</c> (a full <c>KBatchedAnimController</c> per building) - so an
    /// all-Tile blueprint would measure the wrong half of that branch. "Ladder" is the 1x1
    /// raw-mineral building used for the anim-backed side (its LadderTile TileLayer excludes it
    /// from the tile branch).
    ///
    /// <paramref name="name"/> must be unique per blueprint when several go into one folder:
    /// <see cref="Blueprint"/>'s identity is its FilePath, which is derived from the name, so
    /// same-named blueprints collapse into one entry in the folder's HashSet.
    /// </summary>
    internal static Blueprint Build(int count, string buildingId, string name)
    {
        var def = Assets.GetBuildingDef(buildingId);
        var sandstone = ElementLoader.FindElementByHash(SimHashes.SandStone).tag;

        var bp = new Blueprint(name, "");
        for (int i = 0; i < count; i++)
        {
            var bc = new BuildingConfig
            {
                Offset = new Vector2I(i % RowWidth, i / RowWidth),
                BuildingDef = def,
                BuildingDefId = buildingId,
                Orientation = Orientation.Neutral,
            };
            bc.SelectedElements.Add(sandstone);
            bp.BuildingConfigurations.Add(bc);
        }
        // Dimensions are otherwise 0 until computed - placement reads Blueprint.Dimensions
        // (import's JSON round-trip never touches it, so BuildJson didn't need this).
        bp.CacheCost();
        return bp;
    }

    /// <summary>Builds a blueprint with <paramref name="count"/> Tile buildings and returns its
    /// on-disk JSON form - the input every timed import operation runs against.</summary>
    public static string BuildJson(int count)
    {
        var bp = Build(count);
        var sb = new System.Text.StringBuilder();
        using (var sw = new System.IO.StringWriter(sb))
            bp.WriteJsonString(sw);
        return sb.ToString();
    }
}
