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
    ///the building's connection points - conduit ports, power connectors, radbolt ports - with the
    ///layer each sits on. Separate from occupiedCells because a port claims a *different* layer at
    ///its cell than the building's own. A value tuple, not Klei's global Tuple<,>, which shadows
    ///System.Tuple here and carries .first/.second.
    protected HashSet<(int Cell, ObjectLayer Layer)> portOccupations = new();
    protected Extents extents;
    protected static Dictionary<Deconstructable, ReplacementVis> queuedDeconstructablesGlobal = new();


    public static VisLayerIndexer Visualizers = new();


    [MyCmpGet] protected KBatchedAnimController? kbac;
    private HandleVector<int>.Handle partitionerEntry;
    [MyCmpReq] KSelectable selectable = null!;
    [MyCmpReq] InfoDescription description = null!;
    [MyCmpGet] protected VisualizerRotatable? visRot;
    [MyCmpGet] protected SpriteRenderer tileSpriteRenderer = null!;

    List<int> subs = [];
    Coroutine? check = null;
    bool replacementInProgress = false;
    bool markedForDeletion = false;
    ///<summary>Latched once <see cref="TryPlacingQueuedBP"/> has actually built something.
    ///The vis is destroyed on the same frame it places, but a partitioner callback can still
    ///arrive before that teardown completes - and by then <see cref="replacementInProgress"/>
    ///is back to false, so without this latch the callback places the building a second time.</summary>
    bool placementSuccessful = false;
    ///<summary>Handle for the next-frame kick scheduled in <see cref="SeatVis"/>, so
    ///<see cref="UnseatVis"/> can cancel it. A vis unseated in the same frame it was seated
    ///would otherwise still get a placement check fired at it afterwards.</summary>
    SchedulerHandle scheduledSpawnCheck = default;
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
        foreach (var occupiedCell in occupiedCells)
        {
            Visualizers[occupiedCell, (int)def.ObjectLayer] = null;
        }
        foreach (var (portCell, portLayer) in portOccupations)
        {
            Visualizers[portCell, (int)portLayer] = null;
        }

        if (TryReplacing)
        {
            RefreshPendingDeconstructs(false);
            GameScenePartitioner.Instance.Free(ref this.partitionerEntry);
            scheduledSpawnCheck.ClearScheduler();
        }
    }
    protected virtual void SeatVis()
    {
        DetermineOccupiedCells();
        foreach (var occupiedCell in occupiedCells)
        {
            var existingVisOnCell = Visualizers[occupiedCell, (int)def.ObjectLayer];
            if (existingVisOnCell != null && existingVisOnCell != this)
            {
                existingVisOnCell.DestroySelf();
            }
            Visualizers[occupiedCell, (int)def.ObjectLayer] = this;
        }
        foreach (var (portCell, portLayer) in portOccupations)
        {
            var existingVisOnPort = Visualizers[portCell, (int)portLayer];
            if (existingVisOnPort != null && existingVisOnPort != this)
            {
                existingVisOnPort.DestroySelf();
            }
            Visualizers[portCell, (int)portLayer] = this;
        }
        if (TryReplacing)
        {
            RefreshPendingDeconstructs(true);
            partitionerEntry = GameScenePartitioner.Instance.Add("ReplacementVis.ReCheckPlacement", (object)this.gameObject, new Extents(this.extents.x - 1, this.extents.y - 1, this.extents.width + 2, this.extents.height + 2), GameScenePartitioner.Instance.objectLayers[(int)def.ObjectLayer], OnPreoccupiedCellChanged);
            scheduledSpawnCheck = GameScheduler.Instance.ScheduleNextFrame("ReplacementVisInitialCheck", OnPreoccupiedCellChanged);
        }
    }
    void RefreshPendingDeconstructs(bool deconstruct)
    {
        foreach (var cell in occupiedCells)
        {
            foreach (var layer in layersToReplace)
                DoDeconstrucThingsAt(cell, layer, deconstruct);

            if (deconstruct)
                CancelPlannedOccupyingBuildings(cell);
        }

        foreach (var (portCell, portLayer) in portOccupations)
        {
            DoDeconstrucThingsAt(portCell, portLayer, deconstruct);
            ///a port cell only conflicts on its own layer, so unlike a footprint cell this cancels
            ///just what sits there - a planned wire bridge or conduit in the way of the connection.
            if (deconstruct && !markedForDeletion)
            {
                var existing = Grid.Objects[portCell, (int)portLayer];
                if (existing != null && existing.TryGetComponent<Constructable>(out _))
                    existing.Trigger((int)GameHashes.Cancel);
            }
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
        FinalizePlacementCheck();
        ///Cleared here rather than on entry to FinalizePlacementCheck, so the re-entrancy guard
        ///stays latched for the whole check.
        ///
        ///<para>It matters because the scene partitioner dispatches <b>synchronously</b>: Grid's
        ///ObjectLayerIndexer setter calls GameScenePartitioner.TriggerEvent inline, which invokes
        ///the callback on the same stack. So every Grid.Objects write inside FinalizePlacementCheck
        ///can re-enter OnPreoccupiedCellChanged before the method has returned, and with the guard
        ///already down that starts a second DelayedPlacementCheck against a half-finished
        ///one.</para>
        check = null;
    }
    void FinalizePlacementCheck()
    {
        if (!TryReplacing || replacementInProgress || placementSuccessful)
            return;
        if (TryPlacingQueuedBP())
        {
            UnseatVis();
            DestroySelf();
        }
        else
            UpdateVisualState();
    }
    void OnPreoccupiedCellChanged(object data)
    {
        if (check != null || replacementInProgress || markedForDeletion || placementSuccessful)
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

        ///re-assert against whatever moved in since seating - including the port cells, which a
        ///conduit can be laid across at any time (upstream c8cbdae). DoDeconstrucThingsAt destroys
        ///this vis when it meets something it may not deconstruct, so bail rather than building
        ///into a cell that is still blocked.
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
        replacementInProgress = false;
        if (builtItem == null)
            return false;

        //only latched on an actual build, so a vis whose cell has not cleared yet
        //keeps retrying on later callbacks
        placementSuccessful = true;

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

    /// <summary>
    /// A bridge conflicts only at its two ends - the span between them passes over whatever is
    /// there - so replacing one should clear those two cells and leave the rest alone. Ported from
    /// upstream f64b19d; its two early-outs are upstream's, and they keep the narrowing off
    /// anything that really fills its footprint (a cell occupier) or has only one cell anyway.
    /// </summary>
    static bool DefIsBridge(BuildingDef def, out CellOffset input, out CellOffset output)
    {
        input = default;
        output = default;

        if (def.BuildingComplete.TryGetComponent<SimCellOccupier>(out _)
            || (def.WidthInCells == 1 && def.HeightInCells == 1))
            return false;

        if (def.BuildingComplete.TryGetComponent<ConduitBridgeBase>(out _))
        {
            input = def.UtilityInputOffset;
            output = def.UtilityOutputOffset;
            return true;
        }
        if (def.BuildingComplete.TryGetComponent<UtilityNetworkLink>(out var networkLink))
        {
            input = networkLink.link1;
            output = networkLink.link2;
            return true;
        }
        return false;
    }

    void DetermineOccupiedCells()
    {
        occupiedCells.Clear();
        portOccupations.Clear();

        if (DefIsBridge(def, out var bridgeInput, out var bridgeOutput))
        {
            occupiedCells.Add(OffsetRotated(bridgeInput));
            occupiedCells.Add(OffsetRotated(bridgeOutput));
        }
        else
        {
            foreach (var offset in def.PlacementOffsets)
                occupiedCells.Add(OffsetRotated(offset));
        }

        ///the connection points. Each claims its own layer at its cell, which the footprint set
        ///never covered, so a pipe or wire already sitting on a port used to be left in place and
        ///the replacement came out unconnected.
        if (def.InputConduitType != ConduitType.None)
            portOccupations.Add((OffsetRotated(def.UtilityInputOffset), Grid.GetObjectLayerForConduitType(def.InputConduitType)));
        if (def.OutputConduitType != ConduitType.None)
            portOccupations.Add((OffsetRotated(def.UtilityOutputOffset), Grid.GetObjectLayerForConduitType(def.OutputConduitType)));
        if (def.RequiresPowerInput)
            portOccupations.Add((OffsetRotated(def.PowerInputOffset), ObjectLayer.WireConnectors));
        if (def.RequiresPowerOutput)
            portOccupations.Add((OffsetRotated(def.PowerOutputOffset), ObjectLayer.WireConnectors));
        ///radbolt ports (upstream c8cbdae). ObjectLayer.Building rather than a port layer of their
        ///own: that is where a radbolt joint plate sits, and there is no HEP conduit layer.
        if (def.UseHighEnergyParticleInputPort)
            portOccupations.Add((OffsetRotated(def.HighEnergyParticleInputOffset), ObjectLayer.Building));
        if (def.UseHighEnergyParticleOutputPort)
            portOccupations.Add((OffsetRotated(def.HighEnergyParticleOutputOffset), ObjectLayer.Building));

        ///a port outside the footprint still has to be watched, or a pipe laid there after seating
        ///would not trigger the re-check
        portOccupations.RemoveWhere(port => !Grid.IsValidCell(port.Cell));

        Grid.CellToXY(cell, out int x, out int y);
        int val1_1 = x;
        int val1_2 = y;
        foreach (int placementCell in occupiedCells)
        {
            int val2_1 = 0;
            int val2_2 = 0;
            ref int local1 = ref val2_1;
            ref int local2 = ref val2_2;
            Grid.CellToXY(placementCell, out local1, out local2);
            x = Math.Min(x, val2_1);
            y = Math.Min(y, val2_2);
            val1_1 = Math.Max(val1_1, val2_1);
            val1_2 = Math.Max(val1_2, val2_2);
        }
        foreach (var (portCell, _) in portOccupations)
        {
            Grid.CellToXY(portCell, out int portX, out int portY);
            x = Math.Min(x, portX);
            y = Math.Min(y, portY);
            val1_1 = Math.Max(val1_1, portX);
            val1_2 = Math.Max(val1_2, portY);
        }

        this.extents.x = x;
        this.extents.y = y;
        this.extents.width = val1_1 - x + 1;
        this.extents.height = val1_2 - y + 1;
    }

    int OffsetRotated(CellOffset offset) =>
        Grid.OffsetCell(cell, Rotatable.GetRotatedCellOffset(offset, orientation));

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
