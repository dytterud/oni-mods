
using BlueprintsV2.BlueprintData;
using UnityEngine;

namespace BlueprintsV2.Visualizers;


public sealed class UtilityVisual : BuildingVisual
{

    readonly IUtilityNetworkMgr? _networkMgr;

    public UtilityVisual(BuildingConfig buildingConfig, int cell, ulong playerId) : base(buildingConfig, cell, playerId)
    {
        _networkMgr = BuildingDef.BuildingComplete.GetComponent<IHaveUtilityNetworkMgr>()?.GetNetworkManager();
        UpdateConnectionVis();
    }

    public override void ApplyRotation(Orientation rotation, bool flippedX, bool flippedY)
    {
        BlueprintRotationStateHolder = rotation;
        base.ApplyRotation(rotation, flippedX, flippedY);
        UpdateConnectionVis();
    }

    /// <summary>
    /// Picks the connection-stub animation for the current rotation. Both the constructor and
    /// every rotation go through here, so the two cannot drift apart - they used to, with the
    /// constructor drawing the unrotated flags.
    /// </summary>
    void UpdateConnectionVis(bool built = false)
    {
        ///hasKbac is false when this visual reuses the shared placeholder (BuildingVisual returns
        ///before assigning kbac), which is exactly when an animation must not be played: the
        ///object is shared between buildings, so it would be last-write-wins.
        if (hasKbac && _networkMgr != null && buildingConfig.GetConduitFlags(out var flags))
        {
            string animation = _networkMgr.GetVisualizerString((UtilityConnections)GetRotatedUtilityConnectionFlags(flags));
            if (!built)
                animation += "_place";

            if (kbac.HasAnimation(animation))
                kbac.Play(animation);
        }
    }
    internal override void MoveVisualizerCore(int cellParam, bool forceRedraw, bool applyColor, bool moveTransform = true)
    {
        if (cellParam != cell || forceRedraw)
        {
            ///see BuildingVisual.usesSharedVisualizer - normally false here (a utility preview
            ///renders, so it keeps its own clone), guarded for consistency. moveTransform is false
            ///when the shared parent has already carried this visual to its new cell.
            if (moveTransform && !usesSharedVisualizer)
                Visualizer.transform.SetPosition(Grid.CellToPosCBC(cellParam, Grid.SceneLayer.Building));
            cell = cellParam;
            if (applyColor)
                ApplyColorIfChanged(cell);
        }
    }
    public override void RefreshColor()
    {
        ApplyColorIfChanged(cell);
    }
    public override bool AllowedForRotation(Orientation rotation, bool flippedX, bool flippedY) => true;
}
