
using System.Diagnostics.CodeAnalysis;
using BlueprintsV2.BlueprintData;
using UnityEngine;
using static BlueprintsV2.BlueprintData.BlueprintState;

namespace BlueprintsV2.Visualizers;


public class TileVisual : BuildingVisual, ICleanableVisual
{
    readonly static Dictionary<ulong, Dictionary<int, BuildingDef>> ActiveTileVisuals = [];
    public static bool HasTileAt(ulong playerId, int cell, [NotNullWhen(true)] out BuildingDef? visual)
    {
        visual = null;
        return ActiveTileVisuals.TryGetValue(playerId, out var dict) && dict.TryGetValue(cell, out visual);
    }
    public static void RegisterReplacementVis(int cell, BuildingDef def)
    {
        if (!ActiveTileVisuals.ContainsKey(BlueprintState.PlayerId_ReplacementTiles))
            ActiveTileVisuals[PlayerId_ReplacementTiles] = new();

        ActiveTileVisuals[PlayerId_ReplacementTiles][cell] = def;

        CustomTileRenderer.AddTileBlock(PlayerId_ReplacementTiles, LayerMask.NameToLayer("Overlay"), def, false, SimHashes.Void, cell);
    }
    public static void UnregisterReplacementVis(int cell, BuildingDef def)
    {
        if (!ActiveTileVisuals.ContainsKey(BlueprintState.PlayerId_ReplacementTiles))
            ActiveTileVisuals[PlayerId_ReplacementTiles] = new();

        if (ActiveTileVisuals[PlayerId_ReplacementTiles].TryGetValue(cell, out var exiting))
            ActiveTileVisuals[PlayerId_ReplacementTiles].Remove(cell);

        CustomTileRenderer.RemoveTileBlock(PlayerId_ReplacementTiles, def, false, SimHashes.Void, cell);
    }

    public static void OnPlayerAdded(ulong playerId)
    {
        ActiveTileVisuals[playerId] = [];
    }
    public static void OnPlayerRemoved(ulong playerId)
    {
        ActiveTileVisuals.Remove(playerId);
    }

    public override PermittedRotations GetAllowedRotations() => BlueprintTransformationInfo.All;
    public override void ApplyRotation(Orientation rotation, bool flippedX, bool flippedY)
    {
        ///tiles dont rotate
    }

    public int DirtyCell { get; private set; } = -1;

    private readonly bool hasReplacementLayer;
    private bool seated = false;

    public TileVisual(BuildingConfig buildingConfig, int cell, ulong playerId) : base(buildingConfig, cell, playerId)
    {
        if (!ActiveTileVisuals.ContainsKey(playerId))
            ActiveTileVisuals[playerId] = [];

        hasReplacementLayer = BuildingDef.ReplacementLayer != ObjectLayer.NumLayers;
        VisualsUtilities.SetTileColor(_playerId, cell, GetVisualizerColor(cell), buildingConfig);
        this.cell = -1;
        DirtyCell = cell;
        isTile = BuildingDef.isKAnimTile && BuildingDef.BlockTileAtlas;
        UpdateGrid(cell);
    }

    public override void ForceRedraw()
    {
        ApplyColorIfChanged(cell);
    }

    public override void ApplyColorIfChanged(int cellParam)
    {
        if (isTile)
        {
            VisualsUtilities.SetTileColor(_playerId, cellParam, GetVisualizerColor(cellParam), buildingConfig);
            return;
        }
    }

    internal override void MoveVisualizerCore(int cellParam, bool forceRedraw, bool applyColor, bool moveTransform = true)
    {
        if (cellParam != cell || forceRedraw)
        {
            ///see BuildingVisual.usesSharedVisualizer - a shared placeholder is never rendered or
            ///read by position, so moving it would only stomp on other tiles sharing it.
            if (moveTransform && !usesSharedVisualizer)
                Visualizer.transform.SetPosition(Grid.CellToPosCBC(cellParam, BuildingDef.SceneLayer));
            UpdateGrid(cellParam);
            if (applyColor)
                ApplyColorIfChanged(cellParam);
            cell = cellParam;
        }
    }

    public void Clean()
    {
        if (!Grid.IsValidBuildingCell(DirtyCell) || !seated)
        {
            return;
        }

        if (DirtyCell != -1 && Grid.IsValidBuildingCell(DirtyCell))
        {

            if (ActiveTileVisuals[_playerId].TryGetValue(DirtyCell, out var vis) && vis == this.BuildingDef)
            {
                ///inside a batch the renderer only wants to know the cell changed: it compares the
                ///cell's before/after def when the batch flushes, so an unseat that a neighbour's
                ///reseat immediately undoes - every interior cell of a blueprint that just moved -
                ///costs nothing at all. Outside one, this is the original immediate path.
                bool deferred = CustomTileRenderer.NoteCellChanged(_playerId, DirtyCell, BuildingDef);
                ActiveTileVisuals[_playerId].Remove(DirtyCell);
                if (!deferred)
                {
                    CustomTileRenderer.RemoveTileBlock(_playerId, BuildingDef, false, SimHashes.Void, DirtyCell);
                    CustomTileRenderer.RefreshCell(_playerId, DirtyCell, BuildingDef.TileLayer, BuildingDef.ReplacementLayer);
                }
            }
        }
        DirtyCell = -1;
        seated = false;
    }
    private void UpdateGrid(int cellParam)
    {
        if (seated && DirtyCell == cellParam)
        {
            // Already correctly registered at this exact cell - a forced redraw that didn't
            // actually move this tile (e.g. VisualizeBlueprint's own post-placement redraw, or
            // a rotation that doesn't affect this particular tile) shouldn't pay for an
            // unregister-then-re-register cycle through Clean()/AddTileBlock/RefreshCell. Perf
            // harness measured this as a real chunk of visualize's cost - see docs §7.
            return;
        }
        Clean();
        if (seated)
            return;
        if (Grid.IsValidBuildingCell(cellParam) && isTile)
        {
            if (ActiveTileVisuals[_playerId].TryGetValue(cellParam, out var existing))
            {
                //SgtLogger.warning(_playerId + ": there is already a tilevisual in " + cellParam);
                return;
            }
            //bool replacing = hasReplacementLayer && CanReplace(cell);
            ///see Clean(): deferred inside a batch, immediate outside one.
            bool deferred = CustomTileRenderer.NoteCellChanged(_playerId, cellParam, defBefore: null);
            ActiveTileVisuals[_playerId][cellParam] = this.BuildingDef;
            if (!deferred)
            {
                CustomTileRenderer.AddTileBlock(_playerId, LayerMask.NameToLayer("Overlay"), BuildingDef, false, SimHashes.Void, cellParam);
                CustomTileRenderer.RefreshCell(_playerId, cellParam, BuildingDef.TileLayer, BuildingDef.ReplacementLayer);
            }
            DirtyCell = cellParam;
            seated = true;
        }
    }
    public override bool AllowedForRotation(Orientation rotation, bool flippedX, bool flippedY) => true;
}
