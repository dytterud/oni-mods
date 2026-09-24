using System.Collections;
using BlueprintsV2.BlueprintData;
using KSerialization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UtilLibs;
using static Rendering.BlockTileRenderer;

namespace BlueprintsV2.Visualizers.ReplacementVisualizers;


public class ReplacementVis : KMonoBehaviour
{
    [Serialize]
    public bool TryReplacing = true;
    [Serialize]
    protected string buildingDefId = "";
    [Serialize]
    protected Orientation orientation;
    [Serialize]
    protected int conduitFlags = -1;
    [Serialize]
    protected Dictionary<string, string> buildingDataSerialized = new();
    [Serialize]
    protected Tag[] selectedElements = [];

    protected BuildingDef def = null!;
    [Serialize]
    protected int cell;

    protected HashSet<int> occupiedCells = new();
    /// the building's connection points (conduit, power and radbolt ports), each on the layer its
    /// port lives on rather than the def's own object layer; recomputed with occupiedCells
    protected List<(int Cell, ObjectLayer Layer)> portOccupations = [];
    protected Extents extents;
    protected static Dictionary<Deconstructable, ReplacementVis> queuedDeconstructablesGlobal = new();


    public static VisLayerIndexer Visualizers = new();


    [MyCmpGet] protected KBatchedAnimController? kbac;
    /// one partitioner entry per watched object layer: the def's own, plus every port layer
    private readonly List<HandleVector<int>.Handle> partitionerEntries = [];
    /// the one-off next-frame check SeatVis schedules; kept so UnseatVis can call it off
    private SchedulerHandle scheduledSpawnCheck;
    [MyCmpReq] KSelectable selectable = null!;
    [MyCmpReq] InfoDescription description = null!;
    [MyCmpGet] protected VisualizerRotatable? visRot;
    [MyCmpGet] protected SpriteRenderer tileSpriteRenderer = null!;

    List<int> subs = [];
    /// the running DelayedPlacementCheck; doubles as the guard against starting a second one, so
    /// it stays set until FinalizePlacementCheck has returned
    Coroutine? check = null;
    bool replacementInProgress = false;
    /// set once TryPlacingQueuedBP has actually built something. The vis is torn down on that
    /// same frame, but a partitioner callback can still land before the teardown finishes, when
    /// replacementInProgress is already false again - this is what stops a second placement.
    bool placementSuccessful = false;
    bool markedForDeletion = false;
    HashSet<ObjectLayer> layersToReplace = null!;

    public void Configure(int cell, BuildingConfig building, Orientation orientation, IEnumerable<Tag> elements, int flags, ulong playerId = BlueprintState.PlayerId_DefaultTilePreviews)
    {
        this.cell = cell;
        this.buildingDefId = building.BuildingDef!.PrefabID;
        this.orientation = orientation;
        this.conduitFlags = flags;
        this.selectedElements = elements.ToArray();
        this.buildingDataSerialized = new();

        if (BlueprintState.CurrentStateInfo(playerId).ApplyBlueprintSettings && building.AdditionalBuildingData != null)
        {
            foreach (var data in building.AdditionalBuildingData)
                buildingDataSerialized[data.Key] = JsonConvert.SerializeObject(data.Value);
        }

        InitDefAndAnim();
    }
    void InitDefAndAnim()
    {
        def = Assets.GetBuildingDef(buildingDefId);
        if (def == null)
            return;
        if (kbac != null)
        {
            kbac.SwapAnims(def.AnimFiles);
            if (visRot != null)
            {
                visRot.Init(def, this.orientation);
                visRot.UpdateRotation();
            }
        }
        layersToReplace = [def.ObjectLayer];
        if (def.TileLayer != ObjectLayer.NumLayers)
            layersToReplace.Add(def.TileLayer);
        if (def.ReplacementLayer != ObjectLayer.NumLayers)
            layersToReplace.Add(def.ReplacementLayer);
        if (def.ReplacementCandidateLayers != null && def.ReplacementCandidateLayers.Any())
            foreach (var layer in def.ReplacementCandidateLayers)
                layersToReplace.Add(layer);
    }
    private void OnRefreshUserMenu(object data)
    {
        Game.Instance.userMenu.AddButton(this.gameObject, new KIconButtonMenu.ButtonInfo("action_cancel", global::STRINGS.UI.USERMENUACTIONS.CANCELCONSTRUCTION.NAME, DestroySelf, tooltipText: global::STRINGS.UI.USERMENUACTIONS.CANCELCONSTRUCTION.TOOLTIP));
    }

    public override void OnSpawn()
    {
        base.OnSpawn();
        InitDefAndAnim();
        SgtLogger.l("Spawning ID: " + buildingDefId);
        var elementTag = selectedElements.Any() ? selectedElements[0] : SimHashes.COMPOSITION.CreateTag();
        var element = ElementLoader.GetElement(elementTag);
        string elementName = element != null ? element.nameUpperCase : global::STRINGS.UI.ELEMENTAL.AGE.UNKNOWN;

        string nameWithMaterial = StringFormatter.Replace(StringFormatter.Replace(global::STRINGS.UI.TOOLS.GENERIC.BUILDING_HOVER_NAME_FMT, "{Name}", def.Name), "{Element}", elementName);
        selectable.SetName(global::STRINGS.UI.ROLES_SCREEN.SLOTS.ASSIGNMENT_PENDING + " " + nameWithMaterial);


        description.description = STRINGS.BLUEPRINTS_FORCE_REPLACER.PENDING_INFO + "\n\n" + def.Desc + "\n\n" + def.Effect;

        if (def == null)
        {
            SgtLogger.warning("Could not find building def for id " + buildingDefId);
            Destroy(this.gameObject);
            return;
        }
        UpdateVisualState();
        SeatVis();


        subs.Add(Subscribe((int)GameHashes.RefreshUserMenu, OnRefreshUserMenu));
        subs.Add(Subscribe((int)GameHashes.Cancel, OnCancel));
    }
    public override void OnCleanUp()
    {
        DestroySelf();
        foreach (var sub in subs)
            Unsubscribe(sub);
        if (check != null)
            StopCoroutine(check);
        base.OnCleanUp();
    }
    protected virtual void DestroySelf()
    {
        if (markedForDeletion) return;
        UnseatVis();
        markedForDeletion = true;
        UnityEngine.Object.Destroy(gameObject);
    }
    protected virtual void UnseatVis()
    {
        ///a vis unseated in the frame it was seated must not have the seating check fire at it later
        if (scheduledSpawnCheck.IsValid)
            scheduledSpawnCheck.ClearScheduler();

        foreach (var occupiedCell in occupiedCells)
        {
            Visualizers[occupiedCell, (int)def.ObjectLayer] = null;
        }
        foreach (var (portCell, portLayer) in portOccupations)
        {
            ///only release a port slot this vis still holds; a later vis may have taken it over
            if (Visualizers[portCell, (int)portLayer] == this)
                Visualizers[portCell, (int)portLayer] = null;
        }

        if (TryReplacing)
        {
            RefreshPendingDeconstructs(false);
            for (int i = 0; i < partitionerEntries.Count; i++)
            {
                var entry = partitionerEntries[i];
                GameScenePartitioner.Instance.Free(ref entry);
            }
            partitionerEntries.Clear();
        }
    }
    protected virtual void SeatVis()
    {
        DetermineOccupiedCells();
        foreach (var occupiedCell in occupiedCells)
            Claim(occupiedCell, def.ObjectLayer);
        foreach (var (portCell, portLayer) in portOccupations)
            Claim(portCell, portLayer);

        if (TryReplacing)
        {
            RefreshPendingDeconstructs(true);
            ///met something it may not deconstruct and already tore itself down - registering
            ///now would leave entries behind that nothing ever frees
            if (markedForDeletion)
                return;

            ///ports sit on other object layers than the building itself, so a pipe or wire laid on
            ///one only shows up on that layer's partitioner - watch each distinct layer involved
            var watched = new Extents(extents.x - 1, extents.y - 1, extents.width + 2, extents.height + 2);
            var watchedLayers = new HashSet<ObjectLayer> { def.ObjectLayer };
            foreach (var (_, portLayer) in portOccupations)
                watchedLayers.Add(portLayer);
            foreach (var layer in watchedLayers)
            {
                partitionerEntries.Add(GameScenePartitioner.Instance.Add("ReplacementVis.ReCheckPlacement", gameObject, watched,
                    GameScenePartitioner.Instance.objectLayers[(int)layer], OnPreoccupiedCellChanged));
            }
            scheduledSpawnCheck = GameScheduler.Instance.ScheduleNextFrame("ReplacementVisInitialCheck", OnPreoccupiedCellChanged);
        }
    }
    /// registers this vis at (cell, layer), evicting any other vis that held that slot
    void Claim(int targetCell, ObjectLayer layer)
    {
        var existingVisOnCell = Visualizers[targetCell, (int)layer];
        if (existingVisOnCell != null && existingVisOnCell != this)
        {
            existingVisOnCell.DestroySelf();
        }
        Visualizers[targetCell, (int)layer] = this;
    }
    void RefreshPendingDeconstructs(bool deconstruct)
    {
        foreach (var cell in occupiedCells)
        {
            foreach (var layer in layersToReplace)
                DoDeconstrucThingsAt(cell, layer, deconstruct);

            if (deconstruct)
            {
                ///DoDeconstrucThingsAt destroys the vis on meeting something it may not deconstruct
                if (markedForDeletion)
                    return;
                CancelPlannedOccupyingBuildings(cell);
            }
        }

        ///A pipe or wire on a port does not block placement, so the building is usually queued
        ///while whatever sits on its ports is still waiting for a dupe. Undoing those deconstructs
        ///once the building is placed would leave the old utility in place and the claim would
        ///have achieved nothing - they are only called off when the vis goes away unplaced.
        if (!deconstruct && placementSuccessful)
            return;

        foreach (var (portCell, portLayer) in portOccupations)
        {
            ///a port conflicts only on its own layer, so nothing else at that cell is touched
            var occupant = Grid.Objects[portCell, (int)portLayer];
            if (occupant != null && occupant.TryGetComponent<Constructable>(out _))
            {
                ///only planned, nothing to deconstruct - cancel the plan instead
                if (deconstruct)
                    occupant.Trigger((int)GameHashes.Cancel);
                continue;
            }
            DoDeconstrucThingsAt(portCell, portLayer, deconstruct);
            if (deconstruct && markedForDeletion)
                return;
        }
    }

    void CancelPlannedOccupyingBuildings(int cell)
    {
        for (int i = 0; i < (int)ObjectLayer.NumLayers; i++)
        {
            var existingItem = Grid.Objects[cell, i];
            if (existingItem == null
                || !existingItem.TryGetComponent<Building>(out var building)
                || !existingItem.TryGetComponent<Constructable>(out var constructable))
                continue;

            var existingDef = building.Def;

            if (layersToReplace.Contains(existingDef.ObjectLayer)
            || layersToReplace.Contains(existingDef.TileLayer) //defaults to numlayers which is never in the hashset
            || layersToReplace.Contains(existingDef.ReplacementLayer)
            || (existingDef.ReplacementCandidateLayers != null && existingDef.ReplacementCandidateLayers.Any(layer => layersToReplace.Contains(layer)))
                )
                existingItem.Trigger((int)GameHashes.Cancel);
        }
    }

    void DoDeconstrucThingsAt(int cell, ObjectLayer layer, bool deconstruct)
    {
        var existingBuilding = Grid.Objects[cell, (int)layer];
        if (existingBuilding == null)
            return;

        if (existingBuilding.TryGetComponent<Deconstructable>(out var decon))
        {
            if (!decon.allowDeconstruction && deconstruct)
            {
                this.DestroySelf();
                return;
            }
            if (deconstruct && decon.allowDeconstruction)
            {
                queuedDeconstructablesGlobal[decon] = this;
                //Sandbox QueueDecon needs to be instant, but isnt without DebugInstaBuild, so it needs to be forced
                if (BlueprintState.InstantBuild && !DebugHandler.InstantBuildMode)
                    decon.OnCompleteWork(null);
                else
                    decon.QueueDeconstruction();
            }
            else if (!deconstruct)
            {
                if (queuedDeconstructablesGlobal.TryGetValue(decon, out var registered) && registered == this)
                    decon.CancelDeconstruction();
            }
        }
    }

    IEnumerator DelayedPlacementCheck()
    {
        yield return null;
        ///The scene partitioner dispatches synchronously, so a Grid.Objects write made while
        ///finalizing re-enters OnPreoccupiedCellChanged on this same call stack. `check` is that
        ///callback's guard, so it is only lowered once the finalize step has fully returned.
        try
        {
            if (!placementSuccessful)
                FinalizePlacementCheck();
        }
        finally
        {
            check = null;
        }
    }
    void FinalizePlacementCheck()
    {
        if (placementSuccessful || markedForDeletion || !TryReplacing || replacementInProgress)
            return;
        if (TryPlacingQueuedBP())
            DestroySelf();
        else if (!markedForDeletion)
            UpdateVisualState();
    }
    void OnPreoccupiedCellChanged(object data)
    {
        if (placementSuccessful || check != null || replacementInProgress || markedForDeletion)
            return;
        check = StartCoroutine(DelayedPlacementCheck());
    }



    //IEnumerator RemovePlacerLogicPortsVis(List<ILogicUIElement> inputs, List<ILogicUIElement> outputs)
    //{
    //	yield return null;
    //	foreach (var input in inputs)
    //	{
    //		Game.Instance.logicCircuitManager.RemoveVisElem(input);
    //	}
    //	foreach (var output in outputs)
    //	{
    //		Game.Instance.logicCircuitManager.RemoveVisElem(output);
    //	}
    //}

    //IEnumerator DeleteVisDelayed(GameObject go)
    //{
    //	yield return null;
    //	if (go.TryGetComponent<LogicPorts>(out var ports))
    //		ports.DestroyVisualizers();
    //	UnityEngine.Object.Destroy(go);
    //}

    bool TryPlacingQueuedBP()
    {
        replacementInProgress = true;

        ///a pipe, wire or plan may have moved onto a claimed cell since seating; deal with it now
        ///rather than build next to it. Meeting something it may not deconstruct destroys the vis.
        RefreshPendingDeconstructs(true);
        if (markedForDeletion)
        {
            replacementInProgress = false;
            return false;
        }

        Vector3 posCbc = Grid.CellToPosCBC(cell, Grid.SceneLayer.Building);
        GameObject builtItem;

        if (!BlueprintState.InstantBuild)
        {
            var placer = Util.KInstantiate(def.BuildingPreview, Grid.CellToPosCBC(cell, def.SceneLayer));
            //Global.Instance.StartCoroutine(RemovePlacerLogicPortsVis(ports.inputPorts != null ? [.. ports.inputPorts] : [], ports.outputPorts != null ?[.. ports.outputPorts] : []));
            builtItem = def.TryPlace(placer, posCbc, orientation, selectedElements, null);
            if (placer.TryGetComponent<LogicPorts>(out var ports))
                ports.DestroyVisualizers();
            //Global.Instance.StartCoroutine(DeleteVisDelayed(placer));
            //else
            UnityEngine.Object.Destroy(placer);
        }
        else
        {
            builtItem = def.Build(cell, orientation, null, selectedElements, ModAssets.GetSpawnTemperature(def, selectedElements), null, false, GameClock.Instance.GetTime());
        }
        ///latch before the in-progress flag drops, so no window exists where both are clear
        if (builtItem != null)
            placementSuccessful = true;
        replacementInProgress = false;
        if (builtItem == null)
            return false;

        ApplyExtraDataToBuilt(builtItem);

        if (builtItem.TryGetComponent<UnderConstructionDataTransfer>(out var dataTransfer))
        {
            foreach (var kvp in buildingDataSerialized)
            {
                dataTransfer.SetDataToApply(kvp.Key, kvp.Value);
            }
        }
        else
        {
            foreach (var kvp in buildingDataSerialized)
            {
                ModAPI.API_Methods.TryApplyingStoredData(builtItem, kvp.Key, JObject.Parse(kvp.Value));
            }
        }
        return true;
    }
    protected virtual void ApplyExtraDataToBuilt(GameObject built)
    {

    }

    int RotatedCell(CellOffset offset) => Grid.OffsetCell(cell, Rotatable.GetRotatedCellOffset(offset, orientation));

    void DetermineOccupiedCells()
    {
        occupiedCells.Clear();
        if (TryGetBridgeEnds(out var firstEnd, out var secondEnd))
        {
            occupiedCells.Add(RotatedCell(firstEnd));
            occupiedCells.Add(RotatedCell(secondEnd));
        }
        else
        {
            foreach (var offset in def.PlacementOffsets)
                occupiedCells.Add(RotatedCell(offset));
        }

        DeterminePortOccupations();

        ///the watched area spans the footprint and every port, so a utility laid on a port after
        ///seating still reaches OnPreoccupiedCellChanged
        Grid.CellToXY(cell, out int minX, out int minY);
        int maxX = minX, maxY = minY;
        foreach (int watchedCell in occupiedCells.Concat(portOccupations.Select(port => port.Cell)))
        {
            Grid.CellToXY(watchedCell, out int cx, out int cy);
            minX = Math.Min(minX, cx);
            minY = Math.Min(minY, cy);
            maxX = Math.Max(maxX, cx);
            maxY = Math.Max(maxY, cy);
        }
        extents.x = minX;
        extents.y = minY;
        extents.width = maxX - minX + 1;
        extents.height = maxY - minY + 1;
    }

    /// <summary>
    /// A bridge passes over whatever lies under its span and only conflicts where it connects, so
    /// its footprint narrows to its two end cells. Never applies to a building that fills its
    /// cells, or to a 1x1 one.
    /// </summary>
    bool TryGetBridgeEnds(out CellOffset firstEnd, out CellOffset secondEnd)
    {
        firstEnd = secondEnd = default;
        var complete = def.BuildingComplete;
        if (complete == null
            || (def.WidthInCells == 1 && def.HeightInCells == 1)
            || complete.GetComponent<SimCellOccupier>() != null)
            return false;

        if (complete.GetComponent<ConduitBridgeBase>() != null)
        {
            firstEnd = def.UtilityInputOffset;
            secondEnd = def.UtilityOutputOffset;
            return true;
        }
        if (complete.TryGetComponent<UtilityNetworkLink>(out var link))
        {
            firstEnd = link.link1;
            secondEnd = link.link2;
            return true;
        }
        return false;
    }

    void DeterminePortOccupations()
    {
        portOccupations.Clear();

        void AddPort(CellOffset offset, ObjectLayer layer)
        {
            int portCell = RotatedCell(offset);
            if (!Grid.IsValidCell(portCell) || portOccupations.Contains((portCell, layer)))
                return;
            portOccupations.Add((portCell, layer));
        }

        if (def.InputConduitType != ConduitType.None)
            AddPort(def.UtilityInputOffset, Grid.GetObjectLayerForConduitType(def.InputConduitType));
        if (def.OutputConduitType != ConduitType.None)
            AddPort(def.UtilityOutputOffset, Grid.GetObjectLayerForConduitType(def.OutputConduitType));
        if (def.RequiresPowerInput)
            AddPort(def.PowerInputOffset, ObjectLayer.WireConnectors);
        if (def.RequiresPowerOutput)
            AddPort(def.PowerOutputOffset, ObjectLayer.WireConnectors);
        ///there is no radbolt conduit layer; a radbolt joint plate sits on the building layer
        if (def.UseHighEnergyParticleInputPort)
            AddPort(def.HighEnergyParticleInputOffset, ObjectLayer.Building);
        if (def.UseHighEnergyParticleOutputPort)
            AddPort(def.HighEnergyParticleOutputOffset, ObjectLayer.Building);
    }

    protected virtual void UpdateVisualState()
    {
        if (kbac == null)
        {
            this.gameObject.SetLayerRecursively(LayerMask.NameToLayer("Place"));
            return;
        }

        kbac.SetLayer(LayerMask.NameToLayer("Place"));
        if (def.isKAnimTile && conduitFlags >= 0)
        {
            if (PlayUtilityAnim(kbac))
                return;
        }
        else
            kbac.Play("place");
    }
    protected bool PlayUtilityAnim(KBatchedAnimController targetKbac, bool built = false)
    {
        IUtilityNetworkMgr utilityNetworkManager = def.BuildingComplete.GetComponent<IHaveUtilityNetworkMgr>().GetNetworkManager();
        string animation = utilityNetworkManager.GetVisualizerString((UtilityConnections)conduitFlags);
        if (!built)
            animation += "_place";

        if (utilityNetworkManager != null && targetKbac.HasAnimation(animation))
        {
            targetKbac.Play(animation);
            return true;
        }
        return false;
    }

    public static Bits GetConnectionBits(BuildingDef def, int cell, int query_layer)
    {
        var pos = Grid.CellToPosCCC(cell, Grid.SceneLayer.TileMain);
        return GetConnectionBits(def, (int)pos.x, (int)pos.y, query_layer);
    }
    public static Bits GetConnectionBits(BuildingDef def, int x, int y, int query_layer)
    {
        Bits bits = (Bits)0;
        Tag tileTag = def.PrefabID;
        if (tileTag == null || tileTag == Tag.Invalid)
            return bits;

        if (y > 0)
        {
            int num = (y - 1) * Grid.WidthInCells + x;
            if (x > 0 && MatchesDef(Visualizers[num - 1, query_layer], tileTag))
            {
                bits |= Bits.DownLeft;
            }

            if (MatchesDef(Visualizers[num, query_layer], tileTag))
            {
                bits |= Bits.Down;
            }

            if (x < Grid.WidthInCells - 1 && MatchesDef(Visualizers[num + 1, query_layer], tileTag))
            {
                bits |= Bits.DownRight;
            }
        }

        int num2 = y * Grid.WidthInCells + x;
        if (x > 0 && MatchesDef(Visualizers[num2 - 1, query_layer], tileTag))
        {
            bits |= Bits.Left;
        }

        if (x < Grid.WidthInCells - 1 && MatchesDef(Visualizers[num2 + 1, query_layer], tileTag))
        {
            bits |= Bits.Right;
        }

        if (y < Grid.HeightInCells - 1)
        {
            int num3 = (y + 1) * Grid.WidthInCells + x;
            if (x > 0 && MatchesDef(Visualizers[num3 - 1, query_layer], tileTag))
            {
                bits |= Bits.UpLeft;
            }

            if (MatchesDef(Visualizers[num3, query_layer], tileTag))
            {
                bits |= Bits.Up;
            }

            if (x < Grid.WidthInCells + 1 && MatchesDef(Visualizers[num3 + 1, query_layer], tileTag))
            {
                bits |= Bits.UpRight;
            }
        }

        return bits;
    }
    public static bool MatchesDef(ReplacementVis? other, Tag target)
    {
        return other != null && other.buildingDefId == target;
    }

    internal static void CancelToolTriggered(int cell)
    {
        for (int i = 0; i < 45; i++)
        {
            var vis = Visualizers[cell, i];
            if (vis != null)
            {
                vis.DestroySelf();
            }
        }
    }
    public virtual void OnCancel(object _)
    {
        DestroySelf();
    }
}
