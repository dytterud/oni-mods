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
    private const int RowWidth = 100;

    /// <summary>Builds a blueprint with <paramref name="count"/> Tile buildings and returns its
    /// on-disk JSON form - the input every timed import operation runs against.</summary>
    public static string BuildJson(int count)
    {
        var def = Assets.GetBuildingDef("Tile");
        var sandstone = ElementLoader.FindElementByHash(SimHashes.SandStone).tag;

        var bp = new Blueprint("PerfSynthetic", "");
        for (int i = 0; i < count; i++)
        {
            var bc = new BuildingConfig
            {
                Offset = new Vector2I(i % RowWidth, i / RowWidth),
                BuildingDef = def,
                BuildingDefId = "Tile",
                Orientation = Orientation.Neutral,
            };
            bc.SelectedElements.Add(sandstone);
            bp.BuildingConfigurations.Add(bc);
        }

        var sb = new System.Text.StringBuilder();
        using (var sw = new System.IO.StringWriter(sb))
            bp.WriteJsonString(sw);
        return sb.ToString();
    }
}
