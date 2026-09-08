
using System.Diagnostics.CodeAnalysis;
using BlueprintsV2.BlueprintData;
using UnityEngine;
using static BlueprintsV2.BlueprintData.BlueprintState;

namespace BlueprintsV2.Visualizers
{

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

        public override void MoveVisualizer(int cellParam, bool forceRedraw)
        {
            if (cellParam != cell || forceRedraw)
            {
                Visualizer.transform.SetPosition(Grid.CellToPosCBC(cellParam, BuildingDef.SceneLayer));
                UpdateGrid(cellParam);
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
                    CustomTileRenderer.RemoveTileBlock(_playerId, BuildingDef, false, SimHashes.Void, DirtyCell);
                    ActiveTileVisuals[_playerId].Remove(DirtyCell);
                    CustomTileRenderer.RefreshCell(_playerId, DirtyCell, BuildingDef.TileLayer, BuildingDef.ReplacementLayer);
                }
            }
            DirtyCell = -1;
            seated = false;
        }
        private void UpdateGrid(int cellParam)
        {
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
                CustomTileRenderer.AddTileBlock(_playerId, LayerMask.NameToLayer("Overlay"), BuildingDef, false, SimHashes.Void, cellParam);
                ActiveTileVisuals[_playerId][cellParam] = this.BuildingDef;
                CustomTileRenderer.RefreshCell(_playerId, cellParam, BuildingDef.TileLayer, BuildingDef.ReplacementLayer);
                DirtyCell = cellParam;
                seated = true;
            }
        }
        public override bool AllowedForRotation(Orientation rotation, bool flippedX, bool flippedY) => true;
    }

}
