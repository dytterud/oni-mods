namespace BlueprintsV2.Tools;

/// <summary>
/// The "Snap to Grid" drag: once a blueprint has been placed by a click, dragging places it again
/// on a lattice anchored at that click, spaced by the step. Pure integer logic, no game types, so
/// it is unit-testable offline; the tools own the game side (cursor to cell, the actual
/// placement).
///
/// Ported from upstream a3375d0 with its behaviour changed where it was wrong:
/// <list type="bullet">
/// <item>Upstream placed only when the cursor landed <i>exactly</i> on a multiple of the step from
/// the last placement. Mouse-move events skip cells on a fast drag, so it silently missed
/// placements, and a one-cell wobble off the drag axis stopped placement until the cursor came
/// back. This snaps the cursor to the <i>nearest</i> lattice point and fills every lattice point
/// between the previous one and it.</item>
/// <item>Upstream re-placed on a lattice point the drag had already covered whenever the cursor
/// doubled back. Each point is placed once per drag here.</item>
/// <item>Upstream marked "not dragging" with <c>default(Vector3)</c>, so a click at world (0,0)
/// could never start a drag. <see cref="IsDragging"/> is an explicit flag.</item>
/// <item>A step below 1 (upstream's fields defaulted to 0, and the snapshot tool divided by it)
/// places nothing.</item>
/// </list>
/// </summary>
public sealed class GridSnapDrag
{
    private int originX, originY, lastX, lastY;
    private readonly HashSet<(int X, int Y)> placed = new();

    public bool IsDragging { get; private set; }

    /// <summary>Starts a drag at the cell the click already placed on.</summary>
    public void Begin(int x, int y)
    {
        placed.Clear();
        originX = lastX = x;
        originY = lastY = y;
        placed.Add((x, y));
        IsDragging = true;
    }

    public void End()
    {
        IsDragging = false;
        placed.Clear();
    }

    /// <summary>
    /// The lattice points to place for a cursor now at (<paramref name="cursorX"/>,
    /// <paramref name="cursorY"/>), in drag order, each at most once per drag. Empty when not
    /// dragging, when a step is below 1, or when the cursor still snaps to the last point.
    /// </summary>
    public List<(int X, int Y)> Advance(int cursorX, int cursorY, int stepX, int stepY)
    {
        var result = new List<(int X, int Y)>();
        if (!IsDragging || stepX < 1 || stepY < 1)
            return result;

        int targetX = originX + RoundToStep(cursorX - originX, stepX);
        int targetY = originY + RoundToStep(cursorY - originY, stepY);
        if (targetX == lastX && targetY == lastY)
            return result;

        ///walk the lattice line from the last point to the target, one lattice step at a time
        ///along the longer axis, so a drag that jumped several steps between two mouse events
        ///still places each one in between.
        int dx = (targetX - lastX) / stepX, dy = (targetY - lastY) / stepY;
        int n = Math.Max(Math.Abs(dx), Math.Abs(dy));
        for (int i = 1; i <= n; i++)
        {
            int px = lastX + RoundDiv(i * dx, n) * stepX;
            int py = lastY + RoundDiv(i * dy, n) * stepY;
            if (placed.Add((px, py)))
                result.Add((px, py));
        }
        lastX = targetX;
        lastY = targetY;
        return result;
    }

    /// <summary>The click half, shared by both placing tools: starts a drag if snapping is on.</summary>
    internal void OnPlaced(Vector2I cell)
    {
        if (BlueprintData.BlueprintState.CurrentStateInfo().SnapToGrid)
            Begin(cell.x, cell.y);
        else
            End();
    }

    /// <summary>The mouse-move half: places the blueprint on each lattice point the cursor has
    /// reached. <paramref name="snapshot"/> is the snapshot tool's blueprint, null for the
    /// blueprint tool, as <c>BlueprintState.UseBlueprint</c> takes it.</summary>
    internal void OnMoved(Vector2I cursor, BlueprintData.Blueprint? snapshot)
    {
        if (!IsDragging)
            return;
        var info = BlueprintData.BlueprintState.CurrentStateInfo();
        if (!info.SnapToGrid)
        {
            End();
            return;
        }
        var (stepX, stepY) = info.ScreenGridStep;
        foreach (var (x, y) in Advance(cursor.x, cursor.y, stepX, stepY))
            BlueprintData.BlueprintState.UseBlueprint(BlueprintData.BlueprintState.PlayerId_DefaultTilePreviews, new Vector2I(x, y), snapshot);
    }

    /// <summary><paramref name="delta"/> rounded to the nearest multiple of <paramref name="step"/>,
    /// halves away from zero, symmetric for negative deltas.</summary>
    public static int RoundToStep(int delta, int step) => RoundDiv(delta, step) * step;

    private static int RoundDiv(int numerator, int denominator)
    {
        int q = (Math.Abs(numerator) * 2 + denominator) / (denominator * 2);
        return numerator < 0 ? -q : q;
    }
}
