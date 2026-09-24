
using System.Collections;
using System.Diagnostics.CodeAnalysis;
using BlueprintsV2.BlueprintData;
using BlueprintsV2.ModAPI;
using BlueprintsV2.Visualizers.ReplacementVisualizers;
using UnityEngine;
using UtilLibs;
using static BlueprintsV2.BlueprintData.BlueprintState;
using static BlueprintsV2.STRINGS.UI.USEBLUEPRINTSTATECONTAINER.INFOITEMSCONTAINER;

namespace BlueprintsV2.Visualizers;

public class BuildingVisual : IVisual
{
    ///store the rotation state of the blueprint without affecting conduits/wires itself; only used by conduits
    protected Orientation BlueprintRotationStateHolder = Orientation.Neutral;

    public GameObject Visualizer { get; protected set; }
    public Vector2I Offset { get; protected set; }

    public PlanScreen.RequirementsState RequirementsState { get; protected set; }

    protected int cell;
    public int CurrentCell => cell;

    protected readonly BuildingConfig buildingConfig;

    // A BuildingVisual is only ever built for a resolved config, so the def is non-null.
    public BuildingDef BuildingDef => buildingConfig.BuildingDef!;

    public Orientation RotatedOrientation { get; protected set; }
    public bool FlippedV { get; protected set; }
    public bool FlippedH { get; protected set; }

    public string BuildingID => BuildingDef.PrefabID;
    protected ulong _playerId = BlueprintState.PlayerId_DefaultTilePreviews;
    protected KBatchedAnimController? kbac;
    [MemberNotNullWhen(true, nameof(kbac))]
    protected bool hasKbac => kbac != null;
    protected bool isTile = false;
    //protected Color? _lastColor = null;

    /// <summary>
    /// True when <see cref="Visualizer"/> is the shared, per-def placeholder from
    /// <see cref="SharedPlaceholders"/> rather than this visual's own clone. Callers must not
    /// destroy it, move it, or write per-building data onto it.
    /// </summary>
    protected bool usesSharedVisualizer = false;

    /// <summary>
    /// One reusable placeholder per <see cref="BuildingDef"/> whose preview prefab renders nothing.
    ///
    /// Tiles are the case that matters: a tile's <c>BuildingPreview</c> carries no
    /// <see cref="KBatchedAnimController"/> (verified in-game - 7 components, no children), so the
    /// clone can't draw anything; the visible art comes entirely from <c>CustomTileRenderer</c>'s
    /// block-tile atlas. The clone existed only to be handed to
    /// <c>BuildingDef.IsValidPlaceLocation</c>, which was measured to consult only the cell it is
    /// passed - not the object's position, and it accepts an inactive object. So one shared
    /// instance serves every tile of a given def instead of ~38.5us of cloning each. See
    /// docs/blueprints-included/in-game-regression-testing.md §7.
    ///
    /// Gated on the preview actually lacking an anim controller rather than on "is a tile": a def
    /// whose preview *can* render keeps its own clone, so this is safe per def by construction.
    /// </summary>
    private static readonly Dictionary<BuildingDef, GameObject?> SharedPlaceholders = new();

    /// <summary>Returns the shared placeholder for <paramref name="def"/>, or null if its preview
    /// has an anim controller and therefore has to be cloned per building as before.</summary>
    private static GameObject? GetSharedPlaceholder(BuildingDef def)
    {
        if (SharedPlaceholders.TryGetValue(def, out var cached))
            return cached;

        GameObject? placeholder = null;
        if (def.BuildingPreview != null && !def.BuildingPreview.TryGetComponent<KBatchedAnimController>(out _))
        {
            placeholder = GameUtil.KInstantiate(def.BuildingPreview, Vector3.zero, Grid.SceneLayer.Front,
                "BlueprintModSharedBuildingVisualizer", LayerMask.NameToLayer("Place"));
            placeholder.SetLayerRecursively(LayerMask.NameToLayer("Place"));
            ///left inactive on purpose - nothing renders it, and IsValidPlaceLocation was verified
            ///to behave identically for an inactive source.
        }
        SharedPlaceholders[def] = placeholder;
        return placeholder;
    }

    /// <summary>
    /// One parent per player for every visualizer that actually carries a transform, so a cursor
    /// move can be <b>one</b> transform write instead of N.
    ///
    /// A drag only ever translates the blueprint: each visual keeps the same offset from the origin,
    /// so moving the shared parent by the origin's delta moves all of them correctly and each child
    /// keeps its own local position (including the per-def z from <c>SceneLayer</c>). Rotation and
    /// flip are the exception - they change the offsets themselves - and fall back to positioning
    /// each visual, which is what <c>applyRotation</c> already gates in <c>UpdateVisual</c>.
    ///
    /// Visuals on the shared placeholder (<see cref="usesSharedVisualizer"/>) are never parented:
    /// nothing reads their position, and moving one would drag every other tile sharing it.
    /// </summary>
    private static readonly Dictionary<ulong, GameObject> VisualRoots = new();

    private static GameObject GetVisualRoot(ulong playerId)
    {
        if (VisualRoots.TryGetValue(playerId, out var existing) && existing != null)
            return existing;

        var root = new GameObject($"BlueprintsIncludedVisualRoot_{playerId}");
        root.transform.SetPosition(Vector3.zero);
        VisualRoots[playerId] = root;
        return root;
    }

    /// <summary>
    /// Shifts the whole preview by <paramref name="cellDelta"/> cells (one cell is one world unit),
    /// returning false when there is no root to move - in which case the caller positions each
    /// visual itself, exactly as before. Called once per update instead of N times.
    /// </summary>
    internal static bool TryTranslateRoot(ulong playerId, Vector2I cellDelta)
    {
        if (!VisualRoots.TryGetValue(playerId, out var root) || root == null)
            return false;

        if (cellDelta.x != 0 || cellDelta.y != 0)
            root.transform.SetPosition(root.transform.GetPosition() + new Vector3(cellDelta.x, cellDelta.y, 0f));
        return true;
    }

    /// <summary>Drops a player's root (the visuals under it are destroyed by the caller).</summary>
    internal static void DestroyVisualRoot(ulong playerId)
    {
        if (VisualRoots.TryGetValue(playerId, out var root) && root != null)
            UnityEngine.Object.Destroy(root);
        VisualRoots.Remove(playerId);
    }

    public BuildingVisual(BuildingConfig buildingConfig, int cell, ulong playerId)
    {
        this._playerId = playerId;
        Offset = buildingConfig.Offset;
        RotatedOrientation = buildingConfig.Orientation;
        this.buildingConfig = buildingConfig;
        this.cell = cell;

        Vector3 positionCbc = Grid.CellToPosCBC(cell, BuildingDef.SceneLayer);

        var shared = GetSharedPlaceholder(BuildingDef);
        if (shared != null)
        {
            ///Non-rendering preview: reuse the shared placeholder and skip the clone entirely.
            ///No positioning (nothing reads it), no ApplyAdditionalBuildingData (it writes onto the
            ///object, which would be meaningless last-write-wins on a shared one - the real
            ///building still gets its data via ApplyBuildingData at placement time).
            Visualizer = shared;
            usesSharedVisualizer = true;
            ApplyColorIfChanged(cell);
            UpdateRequirementsState();
            return;
        }

        Visualizer = GameUtil.KInstantiate(BuildingDef.BuildingPreview, positionCbc, Grid.SceneLayer.Front, "BlueprintModBuildingVisualizer", LayerMask.NameToLayer("Place"));
        Visualizer.transform.SetPosition(positionCbc);
        ///has to happen before the visualizer is activated;
        ///the kanim batch set a controller ends up in is chosen on registration (which happens on activation),
        ///and a controller that is not flagged as always visible at that point lands in a spatially culled batch set of its spawn chunk.
        ///always visible controllers never re-register on chunk change, so it would then disappear as soon as that chunk scrolls off screen.
        bool hasBatchedAnimController = Visualizer.TryGetComponent<KBatchedAnimController>(out var batchedAnimController);
        if (hasBatchedAnimController)
        {
            batchedAnimController.visibilityType = KAnimControllerBase.VisibilityType.Always;
            batchedAnimController.isMovable = true;
            batchedAnimController.Offset = BuildingDef.GetVisualizerOffset();
        }

        Visualizer.SetActive(true);

        if (Visualizer.TryGetComponent<Rotatable>(out var rotatable))
        {
            rotatable.SetOrientation(RotatedOrientation);
        }
        ModAPI.API_Methods.ApplyAdditionalBuildingData(Visualizer, buildingConfig, _playerId);

        if (hasBatchedAnimController)
        {
            //batchedAnimController.TintColour = GetVisualizerColor(cell);

            batchedAnimController.SetLayer(LayerMask.NameToLayer("Place"));
            batchedAnimController.Play("place");
            kbac = batchedAnimController;
        }
        else
        {
            Visualizer.SetLayerRecursively(LayerMask.NameToLayer("Place"));
        }
        ///parented after activation and after Play: the batch a controller lands in is chosen on
        ///registration, and worldPositionStays keeps the position it was just given.
        Visualizer.transform.SetParent(GetVisualRoot(_playerId).transform, worldPositionStays: true);

        ApplyColorIfChanged(cell);
        UpdateRequirementsState();
    }

    ///relevant for rendering tiles in the multiplayer mod integration
    public ulong GetPlayerId()
    {
        return _playerId;
    }
    public virtual bool IsPlaceable(int cellParam)
    {
        return HasTech() && AllowedInWorld() && ValidCell(cellParam, out bool needsToReplace);
    }
    public virtual void ForceRedraw() => MoveVisualizer(cell, true);
    public virtual void MoveVisualizer(int cellParam, bool forceRedraw = false)
        => MoveVisualizerCore(cellParam, forceRedraw, applyColor: true);

    /// <summary><see cref="MoveVisualizer"/>, with the colour step made optional for callers that
    /// already run a <see cref="RefreshColor"/> pass afterwards - recomputing a colour that pass
    /// immediately overwrites is the single most expensive redundancy in the per-frame update
    /// (see BlueprintState.UpdateVisual). Kept off the public <see cref="IVisual"/> surface so the
    /// interface other mods implement does not change shape.</summary>
    /// <param name="moveTransform">false when the shared parent has already been translated for this
    /// update (see <see cref="TryTranslateRoot"/>) and writing each visual's position again would
    /// only recompute what the parent move already did.</param>
    internal virtual void MoveVisualizerCore(int cellParam, bool forceRedraw, bool applyColor, bool moveTransform = true)
    {
        if (cell != cellParam || forceRedraw)
        {
            ///a shared placeholder renders nothing and nothing reads its position, so moving it
            ///would just be one visual stomping on another's.
            if (moveTransform && !usesSharedVisualizer)
                Visualizer.transform.SetPosition(Grid.CellToPosCBC(cellParam, BuildingDef.SceneLayer));
            if (applyColor)
                ApplyColorIfChanged(cellParam);
            cell = cellParam;
        }
    }
    public virtual void RefreshColor()
    {
        ApplyColorIfChanged(cell);
    }

    private Tag[] GetConstructionElements()
    {
        var ingredients = BuildingDef.CraftRecipe.Ingredients;
        var elements = new List<Tag>(buildingConfig.SelectedElements.Count);
        for (int i = 0; i < ingredients.Count; ++i)
        {
            var ingredient = ingredients[i];
            Tag selectedElement;
            if (i < buildingConfig.SelectedElements.Count)
            {
                selectedElement = buildingConfig.SelectedElements[i];
            }
            else
            {
                //should never happen, just in case to prevent crash.
                selectedElement = ModAssets.GetFirstAvailableMaterial(ingredient.tag, ingredient.amount);
            }
            var key = BlueprintSelectedMaterial.GetBlueprintSelectedMaterial(selectedElement, ingredient.tag, BuildingDef.PrefabID);

            if (ModAssets.TryGetReplacementTag(key, out var replacement))
            {
                selectedElement = replacement;
            }
            elements.Add(selectedElement);
        }

        return elements.ToArray();
    }
    #region replace experiment
    //private bool ViableReplacementCandidate(GameObject toReplace)
    //{
    //	if (toReplace.TryGetComponent<BuildingComplete>(out var component))
    //	{
    //		return (component.Def.Replaceable && BuildingDef.CanReplace(toReplace) && (component.Def != BuildingDef || GetConstructionElements()[0] != component.GetComponent<PrimaryElement>().Element.tag));
    //	}
    //	return false;
    //}

    //bool ReplacementLayerOccupied(int cellParam)
    //{
    //	var def = BuildingDef;
    //	var objOnLayer = Grid.Objects[cellParam, (int)def.ReplacementLayer];

    //	if (objOnLayer != null && objOnLayer != Visualizer)
    //		return true;
    //	if (def.EquivalentReplacementLayers != null)
    //	{
    //		foreach (ObjectLayer replacementLayer in def.EquivalentReplacementLayers)
    //		{
    //			objOnLayer = Grid.Objects[cellParam, (int)replacementLayer];
    //			if (objOnLayer != null && objOnLayer != Visualizer)
    //				return true;
    //		}
    //	}
    //	return false;
    //}
    #endregion
    public virtual void ApplyBuildingData(GameObject building, bool includeTime = true)
    {
        bool isPlanned = building.TryGetComponent<BuildingUnderConstruction>(out var buildingUnderConstruction);
        bool isComplete = building.TryGetComponent<BuildingComplete>(out var buildingComplete);

        var def = BuildingDef;

        if (isPlanned && buildingUnderConstruction.Def != def)
            return;
        if (isComplete && buildingComplete.Def != def)
            return;

        if (building.TryGetComponent<Rotatable>(out var rotatable))
        {
            rotatable.SetOrientation(RotatedOrientation);
        }
        ModAPI.API_Methods.ApplyAdditionalBuildingData(building, buildingConfig, _playerId);

        if (Visualizer.TryGetComponent<KBatchedAnimController>(out var kbac))
        {
            kbac.TintColour = ModAssets.BLUEPRINTS_COLOR_INVALIDPLACEMENT;
            if (isPlanned)
                kbac.Play("place");
        }

        if (isPlanned && ToolMenu.Instance != null && BlueprintState.CurrentStateInfo(_playerId).UseToolPriority)
        {
            building.FindOrAddComponent<Prioritizable>().SetMasterPriority(ToolMenu.Instance.PriorityScreen.GetLastSelectedPriority());
        }
        if (isComplete && includeTime)
            buildingComplete.SetCreationTime(GameClock.Instance.GetTime());
        UpdateConduitConnectionBits(building);
    }

    /// <summary>
    /// Shifts this visual's stored connection flags to match the blueprint's current rotation.
    /// </summary>
    public int GetRotatedUtilityConnectionFlags(int plannedFlags)
        => RotateUtilityConnectionFlags(plannedFlags, -(int)BlueprintRotationStateHolder, FlippedH, FlippedV);

    /// <summary>
    /// Rotates a <see cref="UtilityConnections"/> bitmask by <paramref name="rotationSteps"/>
    /// quarter turns, then applies the flips.
    ///
    /// <para>The stored flags are <b>absolute</b> (world-space):
    /// <see cref="BlueprintState.CreateBlueprint"/> captures them with
    /// <c>GetNetworkManager().GetConnections(cell, false)</c>, which is keyed on a grid cell and
    /// takes no orientation - the bits say which neighbouring <em>cells</em> a conduit reaches,
    /// not which way the building faces. So the only shift a placed blueprint needs is its own
    /// rotation; the captured building's <see cref="BuildingConfig.Orientation"/> must not enter
    /// this, and used to (see the git history and issue #4).</para>
    ///
    /// <para>Kept static and free of game state so the shift can be unit-tested directly -
    /// a <see cref="BuildingVisual"/> cannot be constructed outside a live colony.</para>
    /// </summary>
    /// <param name="plannedFlags">the stored, world-space connection bits</param>
    /// <param name="rotationSteps">signed quarter turns to rotate by</param>
    /// <param name="flippedH">mirror left/right after rotating</param>
    /// <param name="flippedV">mirror up/down after rotating</param>
    public static int RotateUtilityConnectionFlags(int plannedFlags, int rotationSteps, bool flippedH, bool flippedV)
    {
        var shiftable = new List<bool>(4)
        {
            (plannedFlags & (int)UtilityConnections.Left) != 0,  //left
            (plannedFlags & (int)UtilityConnections.Right) != 0, //right
            (plannedFlags & (int)UtilityConnections.Up) != 0,    //up
            (plannedFlags & (int)UtilityConnections.Down) != 0   //down
        };

        if (rotationSteps > 0)
        {
            for (int i = 0; i < rotationSteps; i++)
            {
                //no bit shifting possible because those arent sorted...
                shiftable = [
                    shiftable[2],
                    shiftable[3],
                    shiftable[1],
                    shiftable[0],
                ];
            }
        }
        else if (rotationSteps < 0)
        {
            for (int i = 0; i < -rotationSteps; ++i)
            {
                shiftable = [
                    shiftable[3],
                    shiftable[2],
                    shiftable[0],
                    shiftable[1],
                ];
            }
        }
        if (flippedH)
        {
            bool left = shiftable[0];
            bool right = shiftable[1];
            shiftable[0] = right;
            shiftable[1] = left;
        }
        if (flippedV)
        {
            bool up = shiftable[2];
            bool down = shiftable[3];
            shiftable[2] = down;
            shiftable[3] = up;
        }

        BitArray bitField = new BitArray(shiftable.ToArray()); //BitArray takes a bool[]
        byte[] bytes = new byte[1];
        bitField.CopyTo(bytes, 0);

        return bytes[0];
    }

    void UpdateConduitConnectionBits(GameObject go)
    {
        if (BuildingDef.BuildingComplete.GetComponent<IHaveUtilityNetworkMgr>() != null
            && go.TryGetComponent<KAnimGraphTileVisualizer>(out var vis)
            && buildingConfig.GetConduitFlags(out var flags))
        {
            var newConnections = (UtilityConnections)GetRotatedUtilityConnectionFlags(flags);
            if (vis.Connections != newConnections)
            {
                UtilityConnections neew = vis.Connections | newConnections;

                vis.UpdateConnections(neew);
                vis.Refresh();
            }
        }
    }

    protected GameObject? CreateFinishedBuildingInternal(int cellParam, Vector3 positionCbc)
    {
        var def = BuildingDef;
        var selectedElements = GetConstructionElements();
        var finishedBuilding = def.Create(positionCbc, null, selectedElements, def.CraftRecipe, ModAssets.GetSpawnTemperature(def, selectedElements), def.BuildingComplete);

        if (finishedBuilding == null)
        {
            SgtLogger.warning("failed to place finished building " + def.PrefabID);
            return null;
        }
        ApplyBuildingData(finishedBuilding);

        def.MarkArea(cellParam, RotatedOrientation, def.ObjectLayer, finishedBuilding);
        if (def.IsTilePiece && def.BlockTileAtlas != null)
        {
            def.MarkArea(cellParam, RotatedOrientation, def.TileLayer, finishedBuilding);
            def.RunOnArea(cellParam, RotatedOrientation, cell0 => TileVisualizer.RefreshCell(cell0, def.TileLayer, def.ReplacementLayer));
        }

        if (finishedBuilding.TryGetComponent<Deconstructable>(out var decon))
        {
            decon.constructionElements = selectedElements;
        }

        finishedBuilding.SetActive(true);
        return finishedBuilding;
    }

    public virtual bool PlaceFinishedBuilding(int cellParam)
    {
        Vector3 positionCbc = Grid.CellToPosCBC(cellParam, BuildingDef.SceneLayer);
        var def = BuildingDef;

        GameObject? building = null;

        if (CanReplaceExistingBuilding(cellParam, out var replacementCandidate)
            && BlueprintState.InstantBuild)
        {
            return InstantBuildReplace(cellParam, positionCbc, replacementCandidate);
        }
        else
            building = CreateFinishedBuildingInternal(cellParam, positionCbc);

        if (building == null)
        {
            SgtLogger.warning("failed to place finished building " + def.PrefabID);
            return false;
        }
        ApplyBuildingData(building);
        return true;
    }

    public virtual bool PlacePlannedBuilding(int cellParam)
    {
        var def = BuildingDef;
        var orientation = RotatedOrientation;
        Vector3 positionCbc = Grid.CellToPosCBC(cellParam, def.SceneLayer);
        GameObject? building = null;

        if (CanReplaceExistingBuilding(cellParam, out var replacementCandidate)
            && !BlueprintState.InstantBuild)
        {
            building = def.TryReplaceTile(Visualizer, positionCbc, orientation, this.GetConstructionElements());
            Grid.Objects[cell, (int)def.ReplacementLayer] = building;
        }
        else
            building = def.Instantiate(positionCbc, orientation, this.GetConstructionElements());

        if (building == null)
        {
            SgtLogger.warning("failed to place planned building " + def.PrefabID);
            return false;
        }
        ApplyBuildingData(building);

        building.SetActive(true);
        return true;
    }
    protected virtual bool InstantBuildReplace(int cell, Vector3 pos, GameObject tile)
    {
        var def = BuildingDef;
        var buildingOrientation = RotatedOrientation;
        var selectedElements = GetConstructionElements();

        if (def.PlacementOffsets.Length > 1)
            def.RunOnArea(cell, buildingOrientation, (offset_cell =>
            {
                if (offset_cell == cell)
                    return;
                GameObject neighborTile = def.GetReplacementCandidate(offset_cell);
                if (neighborTile == null)
                    return;
                if (neighborTile.TryGetComponent<SimCellOccupier>(out var sco))
                    sco.DestroySelf((() => UnityEngine.Object.Destroy(neighborTile)));
                else
                    UnityEngine.Object.Destroy(neighborTile);
            }));
        if (!tile.TryGetComponent<SimCellOccupier>(out var sco))
        {
            UnityEngine.Object.Destroy(tile);
            return CreateFinishedBuildingInternal(cell, pos);
        }
        sco.DestroySelf(() =>
        {
            UnityEngine.Object.Destroy(tile);
            var builtTile = CreateFinishedBuildingInternal(cell, pos);
        });
        return true;
    }

    ///this has issues with tiles and conduits; dont use.
    protected virtual bool CanReplaceExistingBuilding(int cell, [NotNullWhen(true)] out GameObject? replacementCandidate)
    {

        replacementCandidate = null;
        var def = BuildingDef;
        bool replacementLayerOccupied = false;
        return false;

        // Disabled: has issues with tiles and conduits. Left in place for reference.
#pragma warning disable CS0162 // Unreachable code detected
        if (ValidCell(cell, out bool isReplacement) && isReplacement)
        {
            replacementCandidate = def.GetReplacementCandidate(cell);
            def.RunOnArea(cell, RotatedOrientation, (offset_cell =>
            {
                if (!def.IsReplacementLayerOccupied(offset_cell))
                    return;
                replacementLayerOccupied = true;
            }));
        }
        else
            return false;

        if (replacementLayerOccupied || replacementCandidate == null)
            return false;

        bool allowedToReplace = false;
        if (replacementCandidate.TryGetComponent<BuildingComplete>(out var repBuildingComplete))
        {
            Tag primaryReplaceElement = replacementCandidate.GetComponent<PrimaryElement>().Element.tag;
            if (primaryReplaceElement == SimHashes.StableSnow.CreateTag())
                primaryReplaceElement = SimHashes.Snow.CreateTag();

            allowedToReplace = repBuildingComplete.Def.Replaceable && def.CanReplace(replacementCandidate) && (repBuildingComplete.Def != def || GetConstructionElements()[0] != primaryReplaceElement);
        }

        return allowedToReplace;
#pragma warning restore CS0162

    }

    public virtual bool TryForceRebuild(int cellParam)
    {
        var visType = ModAssets.GetVisualizerType(BuildingDef);
        var prefab = visType switch
        {
            VisualizerType.TILE => Assets.GetPrefab(ReplacementVisualizerMultiEntityConfig.TILE_ID),
            VisualizerType.UTILITY => Assets.GetPrefab(ReplacementVisualizerMultiEntityConfig.UTILITY_ID),
            _ => Assets.GetPrefab(ReplacementVisualizerMultiEntityConfig.BUILDING_ID),
        };
        var def = BuildingDef;
        var orientation = RotatedOrientation;
        Vector3 positionCbc = Grid.CellToPosCBC(cellParam, def.SceneLayer);
        var overrider = Util.KInstantiate(prefab, positionCbc);
        var vis = overrider.GetComponent<ReplacementVis>();

        int flags = -1;
        if (buildingConfig.GetConduitFlags(out var conduitFlags))
            flags = GetRotatedUtilityConnectionFlags(conduitFlags);

        vis.Configure(cellParam, buildingConfig, RotatedOrientation, this.GetConstructionElements(), flags, _playerId);
        vis.gameObject.SetActive(true);
        return true;
    }

    public virtual bool TryReconstructExistingBuilding(int cellParam)
    {
        if (CanRebuildWithMaterial(cellParam, out var reconstructable))
        {
            reconstructable.RequestReconstruct(buildingConfig.SelectedElements[0]);
            ApplyBuildingData(reconstructable.gameObject, false);
            return true;
        }
        else if (reconstructable != null && reconstructable.gameObject != null)
        {
            ApplyBuildingData(reconstructable.gameObject, false);
        }
        return false;
    }

    /// <summary>
    /// Finds a matching building already occupying <paramref name="cellParam"/>.
    ///
    /// <paramref name="includePlanned"/> widens the match from completed buildings to any
    /// <see cref="Building"/>, so blueprint data can also be transferred onto a building that is
    /// only queued for construction. Pass <c>false</c> where the caller goes on to do something
    /// that is only meaningful on a finished building.
    /// </summary>
    public virtual bool SameBuildingAlreadyFinishedInPlace(int cellParam, [NotNullWhen(true)] out Building? building, bool excludeConduits, bool includePlanned)
    {
        building = null;
        var def = BuildingDef;
        var existingBuilding = Grid.Objects[cellParam, (int)def.ObjectLayer];
        if (existingBuilding != null && existingBuilding.TryGetComponent<Building>(out building))
        {
            if (building is not BuildingComplete && !includePlanned)
                return false;

            //is same def AND the building cell is aligned with the visualizer cell (aka the building is in the exact same spot as the vis.)
            if (building.Def == def && Grid.PosToCell(existingBuilding) == cellParam)
            {
                ///a same-def building facing another way is a different placement: treating it as
                ///"already here" would skip it, or push this entry's settings (and orientation) onto it
                if (!FacesAsPlaced(building))
                    return false;

                if (excludeConduits)
                    return !building.TryGetComponent<IHaveUtilityNetworkMgr>(out _);

                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Whether <paramref name="existing"/> faces the orientation this visual would place it in.
    /// Only a disagreement counts: a def without a <see cref="Rotatable"/> has no orientation to
    /// compare, and neither does one whose orientation is purely cosmetic
    /// (<see cref="OrientationIsCosmetic"/>).
    /// </summary>
    private bool FacesAsPlaced(Building existing)
    {
        var def = BuildingDef;
        if (OrientationIsCosmetic(def) || !def.BuildingComplete.TryGetComponent<Rotatable>(out _))
            return true;

        ///read from the instance, not the prefab; a planned building lacking one cannot disagree either
        if (!existing.TryGetComponent<Rotatable>(out var existingRotatable))
            return true;

        return existingRotatable.Orientation == RotatedOrientation;
    }

    /// <summary>
    /// One-cell back-wall buildings (drywall and the like) carry a <see cref="Rotatable"/> only for
    /// visual variety; their rotation says nothing about placement. Decided
    /// by layer and footprint so any such def, modded or DLC, is covered without naming it.
    /// </summary>
    private static bool OrientationIsCosmetic(BuildingDef def)
        => def.ObjectLayer == ObjectLayer.Backwall && def.WidthInCells == 1 && def.HeightInCells == 1;

    public virtual bool CanApplyConduitSettings(int cellParam)
    {
        if (!SameBuildingAlreadyFinishedInPlace(cellParam, out var otherConduit, false, includePlanned: true))
            return false;
        if (otherConduit.TryGetComponent<IHaveUtilityNetworkMgr>(out var mng) && buildingConfig.GetConduitFlags(out var ownFlags))
        {
            var manager = mng.GetNetworkManager();
            var current = (int)manager.GetDisplayConnections(cellParam);
            return current != ownFlags;
        }
        return false;
    }

    public virtual bool CanForceRebuild(int cellParam)
    {
        bool allowed = BlueprintState.CurrentStateInfo(_playerId).ForceBuild && AllowedInWorld() && HasTech();
        if (!allowed)
            return false;

        if (SameBuildingAlreadyFinishedInPlace(cellParam, out var bc, false, includePlanned: true))
        {
            if (bc.TryGetComponent<PrimaryElement>(out var e) && e.Element.tag == GetConstructionElements()[0])
                return false;
        }
        return allowed;
    }

    public virtual bool CanRebuildWithMaterial(int cellParam, [NotNullWhen(true)] out Reconstructable? reconstructable)
    {
        reconstructable = null;
        var def = BuildingDef;
        if (SameBuildingAlreadyFinishedInPlace(cellParam, out var bc, false, includePlanned: false))
        {
            if (bc.Def == def
                && bc.TryGetComponent<Reconstructable>(out reconstructable)
                && reconstructable.AllowReconstruct
                && bc.TryGetComponent<PrimaryElement>(out var primaryElement)
                && primaryElement.Element.tag != GetConstructionElements()[0])
            {
                return true;
            }
        }
        return false;
    }

    public virtual bool TryUse(int cellParam)
    {
        if (!Grid.IsValidCell(cellParam))
            return false;
        if (BlueprintState.InstantBuild && ValidCell(cellParam, out _) && AllowedInWorld()) //sandbox insta build
        {
            BuildingDef.RunOnArea(cell, RotatedOrientation, offset_cell =>
            {
                if (Grid.IsSolidCell(offset_cell) && !Grid.Foundation[offset_cell])
                    SimMessages.Dig(offset_cell, skipEvent: true, backwall: false);
            });

            if (BuildingDef.ObjectLayer == ObjectLayer.Building)
                BuildingDef.RunOnArea(cell, RotatedOrientation, offset_cell =>
                {
                    if (!Uprootable.CanUproot(Grid.Objects[offset_cell, (int)this.BuildingDef.ObjectLayer], out Uprootable uprootable))
                        return;
                    uprootable.CompleteWork(null!);
                });
            else if (BuildingDef.ObjectLayer == ObjectLayer.Backwall)
                BuildingDef.RunOnArea(cell, RotatedOrientation, offset_cell =>
                {
                    if (!BackwallManager.HasBackwall(offset_cell))
                        return;
                    SimMessages.Dig(offset_cell, skipEvent: true, backwall: true);
                });
            return PlaceFinishedBuilding(cellParam);
        }
        else if (IsPlaceable(cellParam)) //regular placing
        {
            return PlacePlannedBuilding(cellParam);
        }
        else if (CanForceRebuild(cellParam)) //force rebuild over existing
        {
            return TryForceRebuild(cellParam);
        }
        //else if (BlueprintState.ForceBuild && CanRebuildWithMaterial(cellParam, out _)) //force rebuild with new materials
        //{
        //	return TryReconstructExistingBuilding(cellParam);
        //}
        else if (CurrentStateInfo(_playerId).ApplySettingsToExistingBuildings && (SameBuildingAlreadyFinishedInPlace(cellParam, out var bc, true, includePlanned: true) || CanApplyConduitSettings(cellParam))) //apply building settings to existing, incl. conduits via CanApplyConduitSettings
        {
            ///bc is null when the first operand short-circuited to false and it was
            ///CanApplyConduitSettings that matched: that call passes excludeConduits: true, so a
            ///conduit never sets bc. Fall back to the object in the cell rather than
            ///dereferencing null - reachable today with apply-settings on, over an existing
            ///conduit of the same def whose connections differ from the blueprint's.
            var target = bc?.gameObject ?? Grid.Objects[cellParam, (int)BuildingDef.ObjectLayer];
            if (target == null)
                return false;

            ApplyBuildingData(target, false);
            if (buildingConfig.HasAnyBuildingData)
            {
                PopFXManager.Instance.SpawnFX(ModAssets.BLUEPRINTS_APPLY_SETTINGS_SPRITE, STRINGS.UI.TOOLS.USE_TOOL.SETTINGS_APPLIED, null, offset: Grid.CellToPos(cellParam), Config.Instance.FXTime);
            }
            return true;
        }

        return false;
    }
    //public virtual void ClearTilePreview(int cell)
    //{
    //	var def = BuildingDef;

    //	if (!Grid.IsValidBuildingCell(cell) || !def.IsTilePiece)
    //		return;
    //	GameObject tileLayerObject = Grid.Objects[cell, (int)def.TileLayer];
    //	if (Visualizer == tileLayerObject)
    //		Grid.Objects[cell, (int)def.TileLayer] = null;
    //	if (!def.isKAnimTile)
    //		return;
    //	GameObject replacementLayerObject = null;
    //	if (def.ReplacementLayer != ObjectLayer.NumLayers)
    //		replacementLayerObject = Grid.Objects[cell, (int)def.ReplacementLayer];
    //	if (tileLayerObject != null && !tileLayerObject.TryGetComponent<Constructable>(out _) || !(replacementLayerObject == null) && !replacementLayerObject != Visualizer)
    //		return;
    //	Grid.Objects[cell, (int)def.ReplacementLayer] = null;

    //	CustomTileRenderer.RemoveTileBlock(GetPlayerId(), def, false, SimHashes.Void, cell);
    //	CustomTileRenderer.RemoveTileBlock(GetPlayerId(), def, true, SimHashes.Void, cell);
    //	CustomTileRenderer.RefreshCell(GetPlayerId(), cell, def.TileLayer, def.ReplacementLayer);
    //}

    /// <summary>
    /// experiment to allow replace building over stuff based on regular build tool, not in use.
    /// </summary>
    /// <param name="cellParam"></param>
    /// <returns></returns>
    //public virtual bool TryBuild(int cellParam)
    //{
    //	ClearTilePreview(cellParam);
    //	Vector3 posCbc = Grid.CellToPosCBC(cellParam, Grid.SceneLayer.Building);
    //	GameObject builtItem = null;
    //	var def = BuildingDef;
    //	var buildingOrientation = RotatedOrientation;
    //	var selectedElements = GetConstructionElements();
    //	var visualizer = Visualizer;

    //	SgtLogger.l("Visualizer test");
    //	SgtLogger.Assert("Visualizer was null", visualizer);

    //	bool instantBuild = DebugHandler.InstantBuildMode || Game.Instance.SandboxModeActive && SandboxToolParameterMenu.instance.settings.InstantBuild;

    //	if (Grid.Objects[cellParam, (int)def.TileLayer] == Visualizer)
    //		Grid.Objects[cellParam, (int)def.TileLayer] = null;

    //	if (Grid.Objects[cellParam, (int)def.ObjectLayer] == Visualizer)
    //		Grid.Objects[cellParam, (int)def.ObjectLayer] = null;

    //	if (Grid.Objects[cellParam, (int)def.ReplacementLayer] == Visualizer)
    //		Grid.Objects[cellParam, (int)def.ReplacementLayer] = null;

    //	if (!instantBuild)
    //	{
    //		builtItem = def.TryPlace(visualizer, posCbc, buildingOrientation, selectedElements, null);
    //	}
    //	else if (def.IsValidBuildLocation(visualizer, posCbc, buildingOrientation) && def.IsValidPlaceLocation(visualizer, posCbc, buildingOrientation, out string _))
    //	{
    //		builtItem = def.Build(cell, buildingOrientation, null, selectedElements, ModAssets.GetSpawnTemperature(def, selectedElements), null, false, GameClock.Instance.GetTime());
    //	}

    //	if (builtItem == null && def.ReplacementLayer != ObjectLayer.NumLayers)
    //	{
    //		GameObject replacementCandidate = def.GetReplacementCandidate(cell);
    //		if (replacementCandidate != null && !def.IsReplacementLayerOccupied(cell))
    //		{
    //			BuildingComplete component = replacementCandidate.GetComponent<BuildingComplete>();
    //			if (component != null && component.Def.Replaceable && def.CanReplace(replacementCandidate) && (component.Def != def
    //						|| selectedElements[0] != replacementCandidate.GetComponent<PrimaryElement>().Element.tag))
    //			{
    //				if (!instantBuild)
    //				{
    //					builtItem = def.TryReplaceTile(visualizer, posCbc, buildingOrientation, selectedElements, null);
    //					Grid.Objects[cell, (int)def.ReplacementLayer] = builtItem;
    //				}
    //				else if (def.IsValidBuildLocation(visualizer, posCbc, buildingOrientation, true) && def.IsValidPlaceLocation(visualizer, posCbc, buildingOrientation, true, out string _))
    //					builtItem = InstantBuildReplace(cell, posCbc, replacementCandidate);
    //			}
    //		}
    //	}

    //	SgtLogger.Assert("builtItem", builtItem);
    //	PostProcessBuild(instantBuild, posCbc, builtItem);
    //	return builtItem != null;
    //}
    //private GameObject InstantBuildReplace(int cell, Vector3 pos, GameObject tile)
    //{
    //	var def = BuildingDef;
    //	var buildingOrientation = RotatedOrientation;
    //	var selectedElements = GetConstructionElements();

    //	if (!tile.TryGetComponent<SimCellOccupier>(out var SCO))
    //	{
    //		UnityEngine.Object.Destroy(tile);
    //		return def.Build(cell, buildingOrientation, null, selectedElements, ModAssets.GetSpawnTemperature(def, selectedElements), null, false, GameClock.Instance.GetTime());
    //	}
    //	SCO.DestroySelf(() =>
    //	{
    //		UnityEngine.Object.Destroy(tile);
    //		PostProcessBuild(true, pos, def.Build(cell, buildingOrientation, null, selectedElements, ModAssets.GetSpawnTemperature(def, selectedElements), null, false, GameClock.Instance.GetTime()));
    //	});
    //	return null;
    //}

    //private void PostProcessBuild(bool instantBuild, Vector3 pos, GameObject builtItem)
    //{
    //	if (builtItem == null)
    //		return;
    //	if (!instantBuild)
    //	{
    //		Prioritizable component = builtItem.GetComponent<Prioritizable>();
    //		if (component != null)
    //		{
    //			if (ToolMenu.Instance != null)
    //				component.SetMasterPriority(ToolMenu.Instance.PriorityScreen.GetLastSelectedPriority());
    //		}
    //	}
    //	ModAPI.API_Methods.ApplyAdditionalBuildingData(builtItem, buildingConfig);

    //	if (Visualizer.TryGetComponent<KBatchedAnimController>(out var kbac))
    //	{
    //		kbac.TintColour = ModAssets.BLUEPRINTS_COLOR_INVALIDPLACEMENT;
    //		kbac.Play("place");
    //	}
    //	UpdateConduitConnectionBits(builtItem);
    //}

    #region per-def memo

    ///Whether a building is buildable here, has its tech, and what its requirements state is are
    ///all functions of the BuildingDef alone - but they are asked once per *visual*, and a large
    ///blueprint holds thousands of visuals across a few dozen defs. They are not cacheable for the
    ///process lifetime the way ModAssets.ValidMaterialsCache is (tech completes, materials run
    ///out), so the memo is opened and closed around one UpdateVisual pass by BeginDefMemo /
    ///EndDefMemo. Outside that window every call computes fresh, which is what placement
    ///(TryUse/IsPlaceable) and the UI rely on.
    static readonly Dictionary<BuildingDef, bool> memoAllowedInWorld = [];
    static readonly Dictionary<BuildingDef, bool> memoHasTech = [];
    static readonly Dictionary<BuildingDef, PlanScreen.RequirementsState> memoRequirementsState = [];
    ///depth rather than a bool so a nested open cannot close the outer scope's memo early. Nothing
    ///nests today; this just keeps that from becoming a silent correctness bug if something ever
    ///does, since a prematurely closed memo fails safe (slower) but a prematurely *opened* one
    ///would not.
    static int defMemoDepth;
    static bool defMemoOpen => defMemoDepth > 0;

    internal static void BeginDefMemo()
    {
        if (defMemoDepth == 0)
        {
            memoAllowedInWorld.Clear();
            memoHasTech.Clear();
            memoRequirementsState.Clear();
        }
        defMemoDepth++;
    }

    internal static void EndDefMemo()
    {
        if (defMemoDepth > 0)
            defMemoDepth--;
    }

    #endregion

    public virtual bool AllowedInWorld()
    {
        var def = BuildingDef;
        if (!defMemoOpen)
            return API_Methods.IsBuildable(def);

        if (!memoAllowedInWorld.TryGetValue(def, out bool allowed))
            memoAllowedInWorld[def] = allowed = API_Methods.IsBuildable(def);
        return allowed;
    }

    public virtual bool HasTech()
    {
        var def = BuildingDef;
        if (!defMemoOpen)
            return HasTechUncached(def);

        if (!memoHasTech.TryGetValue(def, out bool hasTech))
            memoHasTech[def] = hasTech = HasTechUncached(def);
        return hasTech;
    }

    static bool HasTechUncached(BuildingDef def) =>
        BlueprintState.InstantBuild || !Config.Instance.RequireConstructable_Tech || Db.Get().TechItems.IsTechItemComplete(def.PrefabID);

    public virtual bool ValidCell(int cellParam, out bool replacement)
    {
        replacement = false;
        if (Grid.IsValidCellInWorld(cellParam, ClusterManager.Instance.activeWorldId)
            && Grid.IsVisible(cellParam))
        {
            bool IsValidPlaceLocation = BuildingDef.IsValidPlaceLocation(Visualizer, cellParam, RotatedOrientation, out string failReason);

            bool validCell = (IsValidPlaceLocation || IgnorableFailReason(cellParam, failReason));


            //replacement = BuildingDef.IsValidReplaceLocation(pos, RotatedOrientation, BuildingDef.ReplacementLayer, BuildingDef.ObjectLayer);
            //if (replacement)
            //	replacement = BuildingDef.GetReplacementCandidate(cellParam) != null;


            return (validCell || replacement);
        }
        return false;
    }

    /// <summary>
    /// Placement failures this mod lets through, because the blueprint itself supplies what the
    /// game says is missing: the surrounding tiles are in the same blueprint and are not built yet.
    /// </summary>
    protected virtual bool IgnorableFailReason(int cellParam, string failReason)
    {
        if (failReason == global::STRINGS.UI.TOOLTIPS.HELP_BUILDLOCATION_WALL
            || failReason == global::STRINGS.UI.TOOLTIPS.HELP_BUILDLOCATION_CORNER
            || failReason == global::STRINGS.UI.TOOLTIPS.HELP_BUILDLOCATION_CORNER_FLOOR)
            return true;

        ///"attach to backwall" buildings may be placed over a backwall the blueprint brings with
        ///it, but must not replace one already placed - that would put down a non-cancelable
        ///visualizer. Every cell the building covers is checked, not just the one it is anchored
        ///at: a wider building used to pass while hanging off the end of a one-cell backwall (#76).
        ///
        ///Hand-rolled rather than def.RunOnArea: the callback would capture this, the cell and a
        ///result flag, i.e. one allocation per visual per cursor move on a path that already
        ///counts bytes (docs §7), and RunOnArea cannot stop at the first bare cell. These are the
        ///same cells it would visit - the game's own CheckBackWallFoundation walks the same set.
        ///
        ///Unlike the game's check this ignores solidity: a buried cell with a backwall behind it
        ///still counts, which matches the mod placing build orders inside rock everywhere else.
        if (failReason == global::STRINGS.UI.TOOLTIPS.HELP_BUILDLOCATION_BACK_WALL_REQUIRED)
        {
            var def = BuildingDef;
            int activeWorld = ClusterManager.Instance.activeWorldId;
            foreach (var offset in def.PlacementOffsets)
            {
                int coveredCell = Grid.OffsetCell(cellParam, Rotatable.GetRotatedCellOffset(offset, RotatedOrientation));
                ///bounds-checked per cell: Grid.OffsetCell is plain arithmetic and HasBackwall
                ///reads unmanaged memory, so an off-grid cell is a bad read rather than an
                ///exception - and a cell that wrapped into the next world could answer yes for a
                ///backwall that is nowhere near this building.
                if (!Grid.IsValidCellInWorld(coveredCell, activeWorld)
                    || !BlueprintState.HasBackwallAt(this, coveredCell)
                    || BlueprintState.LayerOccupiedAt(this, def.ObjectLayer, coveredCell))
                    return false;
            }
            return true;
        }

        return false;
    }

    public virtual void UpdateRequirementsState()
    {
        var def = BuildingDef;
        if (!defMemoOpen)
        {
            API_Methods.BuildableStateValid(def, out var freshState);
            RequirementsState = freshState;
            return;
        }

        if (!memoRequirementsState.TryGetValue(def, out var state))
        {
            API_Methods.BuildableStateValid(def, out state);
            memoRequirementsState[def] = state;
        }
        RequirementsState = state;
    }
    public virtual void ApplyColorIfChanged(int cellParam)
    {
        Color newColor = GetVisualizerColor(cellParam);

        //if (_lastColor.HasValue && newColor == _lastColor.Value)
        //	return;

        //_lastColor = newColor;

        if (hasKbac)
            kbac.TintColour = newColor;
    }

    public Color GetVisualizerColor(int cellParam)
    {
        Color playerColor = default;
        if (!LocalPlayerId(_playerId) && IsMultiplayerVisualizer(_playerId, ref playerColor))
            return playerColor;
        var stateInfo = BlueprintState.CurrentStateInfo(_playerId);
        UpdateRequirementsState();
        if (CanForceRebuild(cellParam))// && CanRebuildWithMaterial(cellParam, out _))
        {
            return ModAssets.BLUEPRINTS_COLOR_VALIDPLACEMENT;
        }
        else if (SameBuildingAlreadyFinishedInPlace(cellParam, out _, false, includePlanned: true))
        {
            if ((buildingConfig.HasAnyBuildingData || CanApplyConduitSettings(cellParam)) && stateInfo.ApplySettingsToExistingBuildings)
            {
                return ModAssets.BLUEPRINTS_COLOR_CAN_APPLY_SETTINGS;
            }
            else
                return ModAssets.BLUEPRINTS_COLOR_INVISIBLE;
        }
        else if (!ValidCell(cellParam, out _))
        {
            return ModAssets.BLUEPRINTS_COLOR_INVALIDPLACEMENT;
        }
        else if (!HasTech())
        {
            return ModAssets.BLUEPRINTS_COLOR_NOTECH;
        }
        else if (RequirementsState == PlanScreen.RequirementsState.Materials && Config.Instance.RequireConstructable_Material)
            return ModAssets.BLUEPRINTS_COLOR_NOMATERIALS;
        else if (!AllowedInWorld())
            return ModAssets.BLUEPRINTS_COLOR_NOTALLOWEDINWORLD;
        else
        {
            return ModAssets.BLUEPRINTS_COLOR_VALIDPLACEMENT;
        }
    }

    public virtual PermittedRotations GetAllowedRotations()
    {
        var def = BuildingDef;
        if (def.isKAnimTile)
            return BlueprintTransformationInfo.All;
        else if (def.WidthInCells == 1 && def.HeightInCells == 1 &&
            (def.ObjectLayer == ObjectLayer.Backwall || def.PermittedRotations == PermittedRotations.R360 || def.BuildLocationRule == BuildLocationRule.Anywhere || def.BuildLocationRule == BuildLocationRule.NotInTiles))
            return BlueprintTransformationInfo.All;
        else if (def.WidthInCells % 2 == 1 || def.PermittedRotations == PermittedRotations.FlipH)
            return PermittedRotations.FlipH;
        else if (def.BuildingComplete.TryGetComponent<Door>(out _))
            return PermittedRotations.FlipH;

        return PermittedRotations.Unrotatable;
    }
    public virtual bool AllowedForRotation(Orientation rotation, bool flippedX, bool flippedY)
    {
        var allowed = GetAllowedRotations();
        switch (allowed)
        {
            case BlueprintTransformationInfo.All:
                return true;
            case PermittedRotations.Unrotatable:
                return false;
            case PermittedRotations.FlipH:
                return (rotation == Orientation.Neutral || rotation == Orientation.FlipH) && !flippedY;
        }
        return false;
    }
    public virtual void ApplyRotation(Orientation rotation, bool flippedX, bool flippedY)
    {
        var allowedRotations = GetAnimRotations();
        if (allowedRotations == PermittedRotations.Unrotatable)
            return;

        var def = BuildingDef;
        Orientation targetRotation = buildingConfig.Orientation;
        if (Visualizer.TryGetComponent<Rotatable>(out var rotatable))
        {
            if (allowedRotations == PermittedRotations.FlipV)
            {
                targetRotation = (targetRotation == Orientation.FlipV ^ flippedY) ? Orientation.FlipV : Orientation.Neutral;
                //ApplyEvenDimensionOffset(flippedX, flippedY, false, BuildingDef.HeightInCells % 2 == 0);
            }
            else if (allowedRotations == PermittedRotations.FlipH)
            {
                targetRotation = (targetRotation == Orientation.FlipH ^ flippedX) ? Orientation.FlipH : Orientation.Neutral;
                //ApplyEvenDimensionOffset(flippedX, flippedY, BuildingDef.WidthInCells % 2 == 0, false);
            }
            else if (allowedRotations == PermittedRotations.R360)
            {
                int currentRota = (int)targetRotation;
                int rotationOrientation = (int)rotation;

                currentRota = (currentRota + rotationOrientation) % 4;

                bool widthLarger1 = def.WidthInCells > 1;
                bool heightLarger1 = def.HeightInCells > 1;

                bool rotaFlipX = widthLarger1 && !heightLarger1 && (currentRota % 2 == 0) || heightLarger1 && !widthLarger1 && (currentRota % 2 != 0);
                bool rotaFlipY = heightLarger1 && !widthLarger1 && (currentRota % 2 == 0) || widthLarger1 && !heightLarger1 && (currentRota % 2 != 0);

                if (flippedX && rotaFlipX)
                    currentRota += 2;
                if (flippedY && rotaFlipY)
                    currentRota += 2;

                currentRota = currentRota % 4;


                ///this would be the proper flip logic if drywalls had unified orientation - but they dont
                //int flipModX = 4;
                //if (flippedX)
                //{
                //	if (currentRota % 2 == 0)
                //	{
                //		flipModX += 1;
                //	}
                //	else
                //	{
                //		flipModX -= 1;
                //	}
                //}
                //currentRota = (currentRota+  flipModX) % 4;
                //int flipModY = 4;

                //if (flippedY)
                //{
                //	if (currentRota % 2 == 0)
                //	{
                //		flipModY -= 1;
                //	}
                //	else
                //	{
                //		flipModY += 1;
                //	}
                //}
                //currentRota = (currentRota + flipModY) % 4;

                targetRotation = (Orientation)currentRota;

                //SgtLogger.l(flippedX+"-"+ def.Tag.ToString() + " - r360; old: " + buildingConfig.Orientation + ", rotated: " + targetRotation);
                //ApplyEvenDimensionOffset(flippedX, flippedY, (currentRota % 2 != 0 && def.HeightInCells % 2 == 0), (currentRota % 2 == 0 && def.WidthInCells % 2 == 0));
            }
            //else if (allowedRotations == PermittedRotations.R90)
            //{
            //	bool isRotated = baseOrientation == Orientation.R90;
            //	if (isRotated)
            //	{
            //	}

            //	var rotationOrientation = (int)rotation;
            //	switch (rotation)
            //	{
            //		case Orientation.Neutral:
            //		case Orientation.R90:
            //			rotationOrientation = (int)rotation;
            //			break;
            //		case Orientation.R180:
            //			rotationOrientation = (int)Orientation.Neutral;
            //			flippedY = !flippedY;
            //			break;
            //		case Orientation.R270:
            //			rotationOrientation = (int)Orientation.R90;
            //			flippedY = !flippedY;
            //			flippedX = !flippedX;
            //			break;
            //	}
            //	if (isRotated)
            //		rotationOrientation++;

            //	rotationOrientation = rotationOrientation % 2;
            //	baseOrientation = (Orientation)rotationOrientation;
            //}
            rotatable.SetOrientation(targetRotation);

            if (BuildingDef.PermittedRotations == PermittedRotations.R90)
            {
                //if the door has an even number of cells, it will need to have its offset adjusted by one, axis depending on the natural state of the door

                //bool evenWidth = def.WidthInCells % 2 == 0 && def.HeightInCells == 1;
                //bool evenHeight = def.HeightInCells % 2 == 0 && def.WidthInCells == 1;

                //bunker doors are rotated in their natural, so they need reversing of the rotation state
                bool isRotatedToHorizontal = def.WidthInCells > 1 ? rotatable.Orientation == Orientation.Neutral : rotatable.Orientation == Orientation.R90;
                bool isRotatedToVertical = !isRotatedToHorizontal;

                //SgtLogger.l(def.PrefabID + ": rotationstate: " + rotatable.orientation + ", ishorizontal: " + isRotatedToHorizontal);
                ApplyEvenDimensionOffset(flippedX, flippedY, isRotatedToHorizontal, isRotatedToVertical);
            }
        }
        FlippedV = flippedY;
        FlippedH = flippedX;
        RotatedOrientation = targetRotation;


        //if (BuildingDef.WidthInCells % 2 == 0 && flippedX != wasFlippedX)
        //{
        //	wasFlippedX = flippedX;

        //	Offset = new(Offset.X + (flippedX ? -1 : 1), Offset.Y);
        //	//MoveVisualizer(cell, true);
        //}
        //int height = BuildingDef.HeightInCells;
        //if (height > 1 && flippedY != wasFlippedY)
        //{
        //	wasFlippedY = flippedY;
        //	int offsetCells = height - 1;


        //	Offset = new(Offset.X, Offset.Y + (flippedY ? offsetCells : -offsetCells));
        //	//MoveVisualizer(cell, true);
        //}
    }

    void ApplyEvenDimensionOffset(bool flippedX, bool flippedY, bool isAffectedH, bool isAffectedV)
    {
        int xOffset = 0, yOffset = 0;
        if (FlippedH != flippedX && isAffectedH)
        {
            xOffset = flippedX ? 1 : -1;
        }
        if (FlippedV != flippedY && isAffectedV)
        {
            yOffset = flippedY ? -1 : 1;
        }
        //SgtLogger.l(BuildingDef.Tag + $": flippedX: {flippedX} flippedY: {flippedY}, offsets: ({xOffset},{yOffset})");
        Offset = new(Offset.X + xOffset, Offset.Y + yOffset);
    }


    public virtual PermittedRotations GetAnimRotations()
    {
        var allowedRotations = BuildingDef.PermittedRotations;
        if (BuildingDef.isKAnimTile)
            return PermittedRotations.R360;

        bool higherThan1 = BuildingDef.HeightInCells > 1,
              widerThan1 = BuildingDef.WidthInCells > 1;

        if (higherThan1 && !widerThan1 && allowedRotations == PermittedRotations.Unrotatable)
            return PermittedRotations.FlipH;


        return allowedRotations;
    }

    public void DestroyVisualizer()
    {
        ///the shared placeholder outlives every visual that borrowed it - destroying it here would
        ///pull it out from under every other tile of the same def (and every later one).
        if (usesSharedVisualizer)
            return;

        if (Visualizer.TryGetComponent<LogicPorts>(out var ports))
        {
            ports.DestroyVisualizers();
        }
        UnityEngine.Object.Destroy(Visualizer);
    }
    public void SpawnDestroyedByForceTransformFx()
    {
        PopFXManager.Instance.SpawnFX(Assets.GetSprite("icon_action_cancel"), string.Format(FORCETRANSFORMATIONTOGGLE.FX_TEXT, BuildingDef.Name), null, offset: Grid.CellToPos(cell), Config.Instance.FXTime);
    }
}
