using BlueprintsV2.BlueprintData;

namespace BlueprintsV2.Visualizers;

/// <summary>
/// Preview of a Spaced Out rocket module.
///
/// A module is placed by attaching it to the hardpoint of the module under it, and the game only
/// knows about hardpoints of real buildings. In a blueprint holding a stack, the module under
/// every upper module is itself just a preview, so without help the whole stack above the bottom
/// module reads as unplaceable. Each module preview therefore announces the hardpoint it would
/// offer once built, and a module standing on such an announced cell has its "must attach to a
/// rocket" failure waived - and only that failure.
/// </summary>
public class RocketModuleVisual : BuildingVisual, ICleanableVisual
{
    /// <summary>
    /// Hardpoint cells the previewed modules currently offer, per player: in multiplayer each
    /// player drags their own blueprint, and one player's preview must not make a cell attachable
    /// for another. The per-player sets are emptied, never replaced, so a reference to one stays
    /// live across a clear.
    /// </summary>
    private static readonly Dictionary<ulong, HashSet<int>> AttachmentPoints = [];

    /// <summary>
    /// The rocket hardpoint's offset for each module def seen so far, or null for a module that
    /// offers none (a nose cone, say). Read on every move, so resolved once per def.
    /// </summary>
    private static readonly Dictionary<BuildingDef, CellOffset?> HardpointOffsets = [];

    ///the game's reason for refusing a module that is not on a rocket hardpoint. Compared once per
    ///visual per cursor move, so formatted once rather than on every comparison; built lazily
    ///because the localised strings are only final once the game has loaded its translations.
    private static string? attachFailReason;
    private static string AttachFailReason =>
        attachFailReason ??= string.Format(global::STRINGS.UI.TOOLTIPS.HELP_BUILDLOCATION_ATTACHPOINT, GameTags.Rocket);

    /// <summary>The hardpoint cell this visual currently has registered, or -1 for none.</summary>
    public int DirtyCell { get; private set; } = -1;

    public RocketModuleVisual(BuildingConfig buildingConfig, int cell, ulong playerId) : base(buildingConfig, cell, playerId)
    {
        ///the base constructor has already coloured this visual - and so run the waiver - before
        ///this body. That is fine: the waiver reads only static state and the player id, which
        ///the base sets first. Registering here, rather than waiting for the first move, lets any
        ///module created after this one already see its hardpoint during its own first colouring.
        Register(cell);
    }

    /// <summary>Drops every hardpoint a player's previews registered, for when their blueprint is
    /// cleared - otherwise those cells would stay attachable for the rest of the session.</summary>
    internal static void ClearAttachmentPoints(ulong playerId)
    {
        if (AttachmentPoints.TryGetValue(playerId, out var points))
            points.Clear();
    }

    private static CellOffset? GetHardpointOffset(BuildingDef def)
    {
        if (HardpointOffsets.TryGetValue(def, out var cached))
            return cached;

        CellOffset? offset = null;
        if (def.BuildingComplete != null
            && def.BuildingComplete.TryGetComponent<BuildingAttachPoint>(out var attachPoint)
            && attachPoint.points != null)
        {
            foreach (var point in attachPoint.points)
            {
                if (point.attachableType == GameTags.Rocket)
                {
                    offset = point.position;
                    break;
                }
            }
        }
        HardpointOffsets[def] = offset;
        return offset;
    }

    private static bool IsRegisteredHardpoint(ulong playerId, int cell)
        => AttachmentPoints.TryGetValue(playerId, out var points) && points.Contains(cell);

    /// <summary>Moves this visual's registration to the hardpoint it offers when standing at
    /// <paramref name="anchorCell"/>.</summary>
    private void Register(int anchorCell)
    {
        Clean();

        if (GetHardpointOffset(BuildingDef) is not CellOffset offset)
            return;

        ///plain arithmetic, so it can land off the grid for a module near the edge
        int hardpoint = Grid.OffsetCell(anchorCell, offset);
        if (!Grid.IsValidCell(hardpoint))
            return;

        if (!AttachmentPoints.TryGetValue(_playerId, out var points))
            AttachmentPoints[_playerId] = points = [];
        points.Add(hardpoint);
        DirtyCell = hardpoint;
    }

    public void Clean()
    {
        if (DirtyCell == -1)
            return;
        if (AttachmentPoints.TryGetValue(_playerId, out var points))
            points.Remove(DirtyCell);
        DirtyCell = -1;
    }

    ///re-registers unconditionally, not only when the cell changes: BlueprintState cleans every
    ///cleanable visual at the start of each update, so a visual that stays put still has to put
    ///its hardpoint back. Registered before the base call so the colour it may apply already sees
    ///this update's hardpoints.
    internal override void MoveVisualizerCore(int cellParam, bool forceRedraw, bool applyColor, bool moveTransform = true)
    {
        Register(cellParam);
        base.MoveVisualizerCore(cellParam, forceRedraw, applyColor, moveTransform);
    }

    ///only the attach-point failure is waived, and only on a previewed hardpoint: a module must
    ///still not land on a cell something else already holds.
    protected override bool IgnorableFailReason(int cellParam, string failReason)
    {
        if (base.IgnorableFailReason(cellParam, failReason))
            return true;
        return failReason == AttachFailReason && IsRegisteredHardpoint(_playerId, cellParam);
    }

    ///a module stack is vertical by nature: it can be mirrored, never turned or stood on its head.
    public override PermittedRotations GetAllowedRotations() => PermittedRotations.FlipH;

    public override bool AllowedForRotation(Orientation rotation, bool flippedX, bool flippedY)
        => rotation == Orientation.Neutral && !flippedY;

    ///modules are built on a launch pad or another module, never inside a rocket's interior.
    public override bool AllowedInWorld()
    {
        if (!base.AllowedInWorld())
            return false;
        var world = ClusterManager.Instance.activeWorld;
        return world == null || !world.IsModuleInterior;
    }
}
