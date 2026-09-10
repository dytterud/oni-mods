using UnityEngine;

namespace BlueprintsV2.Harness;

/// <summary>
/// The fixed set of buildings the harness places (via <see cref="FixtureBuilder"/>) into the
/// fixture colony, and the capture rectangle the assertion case selects over them.
///
/// All build from raw minerals - Basalt for the tile/ladder pieces, Aluminum Ore for the
/// generator - so the selected element survives the JSON round-trip unchanged.
/// <c>ManualGenerator</c> carries a <c>Prioritizable</c> for the data-transfer case.
/// Offsets are cells from the active Printing Pod.
/// </summary>
internal static class FixtureLayout
{
    internal readonly record struct Spec(string PrefabId, int Dx, int Dy, Orientation Orientation);

    public static readonly Spec[] Buildings =
    {
        new("Tile",            -8, -5, Orientation.Neutral),
        new("InsulationTile",  -5, -5, Orientation.Neutral),
        new("Ladder",          -2, -5, Orientation.Neutral),
        new("ManualGenerator",  1, -6, Orientation.Neutral),
    };

    /// <summary>Extra cells added around the buildings' bounding box for the capture rectangle.</summary>
    public const int CapturePadding = 2;

    /// <summary>Absolute cell for a spec, given the anchor (Printing Pod) cell.</summary>
    public static int CellOf(Spec s, int anchorCell) => Grid.OffsetCell(anchorCell, s.Dx, s.Dy);

    /// <summary>
    /// Capture rectangle as (topLeft, bottomRight) in the convention
    /// <see cref="BlueprintsV2.BlueprintData.BlueprintState.CreateBlueprint"/> expects:
    /// topLeft = (minX, maxY), bottomRight = (maxX, minY).
    /// </summary>
    public static (Vector2I topLeft, Vector2I bottomRight) CaptureRect(int anchorCell)
    {
        int minX = int.MaxValue, maxX = int.MinValue, minY = int.MaxValue, maxY = int.MinValue;
        foreach (var s in Buildings)
        {
            var xy = Grid.CellToXY(CellOf(s, anchorCell));
            minX = Mathf.Min(minX, xy.x); maxX = Mathf.Max(maxX, xy.x);
            minY = Mathf.Min(minY, xy.y); maxY = Mathf.Max(maxY, xy.y);
        }
        int p = CapturePadding;
        return (new Vector2I(minX - p, maxY + p), new Vector2I(maxX + p, minY - p));
    }
}
