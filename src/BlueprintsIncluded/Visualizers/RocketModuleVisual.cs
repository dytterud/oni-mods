using BlueprintsV2.BlueprintData;

namespace BlueprintsV2.Visualizers;

/// <summary>
/// A rocket module in a blueprint. Modules stack: each one attaches to a hardpoint on the module
/// below it, so a blueprint holding a whole rocket has to let a module sit on the hardpoint of
/// another module that is itself only a preview - the game refuses that, because the module below
/// does not exist yet.
///
/// Every module preview therefore registers where its own hardpoint is, and an "attach point"
/// placement failure on one of those cells is ignored. Ported from upstream 41d510c, with its
/// follow-ups in a3375d0 and 70de2bc.
/// </summary>
public class RocketModuleVisual : BuildingVisual, ICleanableVisual
{
    ///per player: in multiplayer each player drags their own blueprint, and one player's stack of
    ///previews must not make another player's module placeable.
    private static readonly Dictionary<ulong, HashSet<int>> AttachmentPoints = new();

    private readonly BuildingAttachPoint.HardPoint hardPoint;
    private readonly bool hasHardpoint;

    public RocketModuleVisual(BuildingConfig buildingConfig, int cell, ulong playerId)
        : base(buildingConfig, cell, playerId)
    {
        if (BuildingDef.BuildingComplete.TryGetComponent<BuildingAttachPoint>(out var attachPoint))
        {
            foreach (var point in attachPoint.points)
            {
                if (point.attachableType != GameTags.Rocket)
                    continue;
                hasHardpoint = true;
                hardPoint = point;
                break;
            }
        }
        ///after the fields are set, because the base constructor has already run its placement
        ///checks by now - this is what upstream's CreateVisualizer override works around, and the
        ///lazy PointsFor below is why no such hook is needed here.
        RegisterHardPoint(cell);
    }

    public int DirtyCell { get; private set; } = -1;

    ///a module is flipped, never rotated, and never placed inside a rocket's interior world
    public override PermittedRotations GetAllowedRotations() => PermittedRotations.FlipH;
    public override bool AllowedForRotation(Orientation rotation, bool flippedX, bool flippedY) => false;
    public override bool AllowedInWorld() => !(ClusterManager.Instance?.activeWorld?.IsModuleInterior ?? true);

    /// <summary>Drops this module's hardpoint registration. Upstream removes <c>cell</c> here
    /// rather than <see cref="DirtyCell"/>, so its hardpoints were never actually unregistered and
    /// stale attach points piled up as the cursor moved.</summary>
    public void Clean()
    {
        if (!hasHardpoint || DirtyCell < 0)
            return;
        PointsFor(_playerId).Remove(DirtyCell);
        DirtyCell = -1;
    }

    private void RegisterHardPoint(int cellParam)
    {
        if (!hasHardpoint)
            return;
        DirtyCell = Grid.OffsetCell(cellParam, hardPoint.position);
        if (Grid.IsValidCell(DirtyCell))
            PointsFor(_playerId).Add(DirtyCell);
    }

    ///MoveVisualizerCore, not MoveVisualizer: this fork moves visuals through the core overload
    ///(BlueprintState.UpdateVisual calls it directly), so an override of the public one would be
    ///bypassed on every cursor move.
    internal override void MoveVisualizerCore(int cellParam, bool forceRedraw, bool applyColor, bool moveTransform = true)
    {
        base.MoveVisualizerCore(cellParam, forceRedraw, applyColor, moveTransform);
        Clean();
        RegisterHardPoint(cellParam);
    }

    /// <summary>
    /// Ignores "must attach to a rocket" on a cell where this blueprint's own modules put a
    /// hardpoint. <c>HELP_BUILDLOCATION_OCCUPIED</c> was ignored here too until upstream's 70de2bc
    /// took it back out: it let a module land on a cell something else already held.
    /// </summary>
    protected override bool IgnorableFailReason(int cellParam, string failReason)
    {
        if (base.IgnorableFailReason(cellParam, failReason))
            return true;

        return failReason == AttachmentFailure
            && AttachmentPoints.TryGetValue(_playerId, out var points)
            && points.Contains(cellParam);
    }

    ///built once: the string is a format of a game string and a tag, and this is asked per visual
    ///per cursor move.
    private static string? attachmentFailure;
    private static string AttachmentFailure => attachmentFailure ??=
        string.Format(global::STRINGS.UI.TOOLTIPS.HELP_BUILDLOCATION_ATTACHPOINT, GameTags.Rocket);

    private static HashSet<int> PointsFor(ulong playerId)
    {
        if (!AttachmentPoints.TryGetValue(playerId, out var points))
            AttachmentPoints[playerId] = points = new HashSet<int>();
        return points;
    }

    /// <summary>Drops every registration for a player, for when their blueprint is cleared rather
    /// than moved. Without it a cell stays "attachable" for the rest of the session.</summary>
    internal static void ClearAttachmentPoints(ulong playerId)
    {
        if (AttachmentPoints.TryGetValue(playerId, out var points))
            points.Clear();
    }
}
