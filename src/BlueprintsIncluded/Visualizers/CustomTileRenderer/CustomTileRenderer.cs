using BlueprintsV2.BlueprintData;
using BlueprintsV2.BlueprintData.OniTogether_Integration;
using HarmonyLib;
using Rendering;
using UnityEngine;
using UtilLibs;

namespace BlueprintsV2.Visualizers;

internal class CustomTileRenderer : BlockTileRenderer
{
    static int
        //OnPlayerJoin = -1,
        OnPlayerLeave = -1, OnPlayerCursorMade = -1;
    static readonly Dictionary<ulong, CustomTileRenderer> customTileRenderers = [];
    /// <summary>
    /// update id for this specific player
    /// </summary>
    public ulong PlayerId = BlueprintState.PlayerId_DefaultTilePreviews;

    static void OnPlayerLeft(object boxedId)
    {
        if (boxedId is not Boxed<ulong> b)
        {
            SgtLogger.warning(boxedId + " was not as expected an ulong, instead it was " + boxedId.GetType());
            return;
        }

        ulong playerId = Boxed<ulong>.Unbox(boxedId);
        SgtLogger.l("Player Left: " + playerId);

        if (!customTileRenderers.TryGetValue(playerId, out var renderer))
        {
            SgtLogger.warning($"player {playerId} left but had no tile renderer!");
            return;
        }
        renderer.FreeResources();
        customTileRenderers.Remove(playerId);
        BlueprintState.RemoveCachesForPlayer(playerId);
        TileVisual.OnPlayerRemoved(playerId);
    }
    //static void OnPlayerCursorCreated(object boxedId)
    //{
    //	if (boxedId is not Boxed<ulong> b)
    //	{
    //		SgtLogger.warning(boxedId + " was not as expected an ulong, instead it was " + boxedId.GetType());
    //		return;
    //	}

    //	ulong playerId = Boxed<ulong>.Unbox(boxedId);
    //	SgtLogger.l("Player Cursor created: " + playerId);
    //	BlueprintState.CachePlayerColor(playerId);
    //}

    /// <summary>
    /// Using the create cursor event as that triggers for all types of players for all others, regardless if its client or host
    /// </summary>
    /// <param name="boxedId"></param>
    static void OnPlayerJoined(object boxedId)
    {
        if (boxedId is not Boxed<ulong> b)
        {
            SgtLogger.warning(boxedId + " was not as expected an ulong, instead it was " + boxedId.GetType());
            return;
        }

        ulong playerId = Boxed<ulong>.Unbox(boxedId);
        SgtLogger.l("Player Joined: " + playerId);

        if (customTileRenderers.TryGetValue(playerId, out var renderer))
        {
            SgtLogger.warning($"player {playerId} joined but had a tile renderer already cached!");
            return;
        }
        renderer = World.Instance.gameObject.AddComponent<CustomTileRenderer>();
        renderer.PlayerId = playerId;
        customTileRenderers.Add(playerId, renderer);
        BlueprintState.AddCachesForPlayer(playerId);
        BlueprintState.CachePlayerColor(playerId);
        TileVisual.OnPlayerAdded(playerId);
    }

    [HarmonyPatch(typeof(World), nameof(World.OnPrefabInit))]
    public class World_OnPrefabInit_Patch
    {
        public static void Postfix(World __instance)
        {
            SgtLogger.l("World.OnPrefabInit");
            customTileRenderers[BlueprintState.PlayerId_DefaultTilePreviews] = __instance.gameObject.AddComponent<CustomTileRenderer>();

            var replacementRenderer = __instance.gameObject.AddComponent<CustomTileRenderer>();
            replacementRenderer.PlayerId = BlueprintState.PlayerId_ReplacementTiles;
            customTileRenderers[BlueprintState.PlayerId_ReplacementTiles] = replacementRenderer;
        }
    }

    [HarmonyPatch(typeof(Game), nameof(Game.OnPrefabInit))]
    public class Game_OnPrefabInit_Patch
    {
        public static void Postfix()
        {
            //OnPlayerJoin = Game.Instance.Subscribe(MP_Mod_Hashes.OnPlayerJoined, OnPlayerJoined);
            OnPlayerLeave = Game.Instance.Subscribe(MP_Mod_Hashes.OnPlayerLeft, OnPlayerLeft);
            OnPlayerCursorMade = Game.Instance.Subscribe(MP_Mod_Hashes.OnPlayerCursorCreated, OnPlayerJoined);
        }
    }

    [HarmonyPatch(typeof(World), nameof(World.OnLoadLevel))]
    public class World_OnLoadLevel_Patch
    {
        public static void Postfix(World __instance)
        {
            //if (OnPlayerJoin != -1)
            //{
            //	Game.Instance?.Unsubscribe(OnPlayerJoin);
            //	OnPlayerJoin = -1;
            //}
            if (OnPlayerLeave != -1)
            {
                Game.Instance?.Unsubscribe(OnPlayerLeave);
                OnPlayerLeave = -1;
            }
            if (OnPlayerCursorMade != -1)
            {
                Game.Instance?.Unsubscribe(OnPlayerCursorMade);
                OnPlayerCursorMade = -1;
            }

            foreach (var r in customTileRenderers.Values)
                r.FreeResources();
            customTileRenderers.Clear();
            SgtLogger.l("World.OnLoadLevel");
        }
    }

    /// <summary>
    /// Redraws one cell of a preview tile layer.
    ///
    /// <para>This used to follow the rebuild with a <c>Grid.Objects[cell, tile_layer]</c> lookup and
    /// a <c>GetComponentInChildren&lt;KAnimGraphTileVisualizer&gt;().Refresh()</c> on whatever real
    /// building sat there. That branch could never fire, and the attribution run in
    /// <c>docs/blueprints-included/in-game-regression-testing.md</c> §7 measured it: <b>0 calls to
    /// <c>KAnimGraphTileVisualizer.Refresh</c> against 76,482 calls to this method</b>, in a sweep
    /// reporting 1000/1000 cells occupied. It is not the grid lookup that comes back empty - that
    /// one succeeds - it is the component lookup, which walked the transform hierarchy on every
    /// refreshed cell and always missed.</para>
    ///
    /// <para>It misses by construction. This renderer is only reached from <c>TileVisual</c>, which
    /// is only built for <see cref="VisualizerType.TILE"/>, and
    /// <c>ModAssets.GetVisualizerType</c> routes every def with an <c>IHaveUtilityNetworkMgr</c> to
    /// <c>UtilityVisual</c> instead - which is every base-game owner of a
    /// <c>KAnimGraphTileVisualizer</c> (wires, logic wires, gas/liquid/solid conduits, travel
    /// tubes). What is left arrives on <c>FoundationTile</c> / <c>ReplacementTile</c>, where a
    /// finished tile draws from the block-tile atlas and carries no anim-graph visualizer at all.
    /// Removed rather than short-circuited, so the method does not read as if it still had a job to
    /// do there.</para>
    /// </summary>
    public static void RefreshCellInternal(ulong playerId, int cell, ObjectLayer tile_layer)
    {
        if (Game.IsQuitting() || !Grid.IsValidCell(cell))
        {
            return;
        }
        if (!customTileRenderers.TryGetValue(playerId, out var r))
            return;
        r.Rebuild(tile_layer, cell);
    }

    /// <summary>
    /// Cells whose art is stale, collected instead of refreshed while a <see cref="BeginBatch"/>
    /// scope is open. A tile's connection art depends on its four neighbours, so every seat and
    /// unseat dirties a five-cell cross per layer - and in a solid block of tiles those crosses
    /// overlap almost completely. Refreshing as we go meant a 2000-tile blueprint issued ~40,000
    /// <see cref="RefreshCellInternal"/> calls per cursor step (harness attribution run: 3.0M calls
    /// against 150k re-seats, docs §7) to touch a few thousand distinct cells.
    ///
    /// Deferring loses nothing: a refresh only ever reads the <i>current</i>
    /// <c>ActiveTileVisuals</c> map, every mutation of that map dirties the cells it can affect, and
    /// the flush happens inside the same frame - so each cell is refreshed once, from the finished
    /// state, instead of once per neighbour that moved past it.
    /// </summary>
    static readonly HashSet<(ulong PlayerId, int Cell, ObjectLayer Layer)> pendingRefreshes = [];
    static int batchDepth;

    /// <summary>
    /// What <c>TileVisual.ActiveTileVisuals</c> held at each cell a batch has touched, captured the
    /// first time that cell changed. The flush compares it against what the map holds now and only
    /// touches the renderer where the two differ - see <see cref="NoteCellChanged"/>.
    /// </summary>
    static readonly Dictionary<(ulong PlayerId, int Cell), BuildingDef?> cellStateAtBatchStart = [];

    /// <summary>
    /// Records what <paramref name="cell"/> held before the caller changes it, so the flush can tell
    /// a real change from a round trip. Called by <see cref="TileVisual"/> immediately <b>before</b>
    /// it mutates the map.
    ///
    /// The point is the round trip. On a plain cursor move every tile unseats itself at the top of
    /// the update and re-seats one cell over, so an interior cell of a translating blueprint is
    /// vacated by one tile and re-filled by its neighbour <i>with the same def</i> - the map ends
    /// exactly as it started, and the block removal, the re-add and both five-cell refresh crosses
    /// were pure churn. Only the leading and trailing edges of the blueprint actually change, which
    /// is why this turns O(N) renderer work into O(perimeter).
    ///
    /// Outside a batch this records nothing and the caller takes the immediate path, unchanged.
    /// </summary>
    public static bool NoteCellChanged(ulong playerId, int cell, BuildingDef? defBefore)
    {
        if (batchDepth == 0)
            return false;

        var key = (playerId, cell);
        if (!cellStateAtBatchStart.ContainsKey(key))
            cellStateAtBatchStart[key] = defBefore;
        return true;
    }

    /// <summary>Opens a batch (re-entrant). <b>Must</b> be paired with <see cref="EndBatch"/> in a
    /// finally - an unclosed batch would leave the art stale until the next flush.</summary>
    public static void BeginBatch() => batchDepth++;

    public static void EndBatch()
    {
        if (batchDepth > 0)
            batchDepth--;
        if (batchDepth > 0)
            return;

        ApplyCellChanges();

        foreach (var (playerId, cell, layer) in pendingRefreshes)
            RefreshCellInternal(playerId, cell, layer);
        pendingRefreshes.Clear();
    }

    /// <summary>
    /// Reconciles the renderer with the map: for every cell a batch touched, compares what it held
    /// when the batch started against what it holds now, and only where those differ removes the old
    /// block, adds the new one and dirties the five-cell cross. A cell that ended as it started -
    /// the common case for the interior of a blueprint that merely moved - costs nothing.
    ///
    /// Runs while <see cref="batchDepth"/> is already 0 so the Add/Remove calls below take the
    /// immediate path for their own bookkeeping, with their refreshes still landing in
    /// <see cref="pendingRefreshes"/> for the flush that follows.
    /// </summary>
    static void ApplyCellChanges()
    {
        if (cellStateAtBatchStart.Count == 0)
            return;

        batchDepth++;   // keep the refreshes these queue in the batch that is about to flush
        try
        {
            foreach (var ((playerId, cell), before) in cellStateAtBatchStart)
            {
                TileVisual.HasTileAt(playerId, cell, out var after);
                if (before == after)
                    continue;

                if (before != null)
                {
                    RemoveTileBlock(playerId, before, false, SimHashes.Void, cell);
                    RefreshCell(playerId, cell, before.TileLayer, before.ReplacementLayer);
                }
                if (after != null)
                {
                    AddTileBlock(playerId, LayerMask.NameToLayer("Overlay"), after, false, SimHashes.Void, cell);
                    RefreshCell(playerId, cell, after.TileLayer, after.ReplacementLayer);
                }
            }
        }
        finally
        {
            cellStateAtBatchStart.Clear();
            batchDepth--;
        }
    }

    public static void RefreshCell(ulong playerId, int cell, ObjectLayer tile_layer)
    {
        if (tile_layer == ObjectLayer.NumLayers)
            return;

        if (batchDepth > 0)
        {
            pendingRefreshes.Add((playerId, cell, tile_layer));
            pendingRefreshes.Add((playerId, Grid.CellAbove(cell), tile_layer));
            pendingRefreshes.Add((playerId, Grid.CellBelow(cell), tile_layer));
            pendingRefreshes.Add((playerId, Grid.CellLeft(cell), tile_layer));
            pendingRefreshes.Add((playerId, Grid.CellRight(cell), tile_layer));
            return;
        }

        RefreshCellInternal(playerId, cell, tile_layer);
        RefreshCellInternal(playerId, Grid.CellAbove(cell), tile_layer);
        RefreshCellInternal(playerId, Grid.CellBelow(cell), tile_layer);
        RefreshCellInternal(playerId, Grid.CellLeft(cell), tile_layer);
        RefreshCellInternal(playerId, Grid.CellRight(cell), tile_layer);
    }

    public static void RefreshCell(ulong playerId, int cell, ObjectLayer tile_layer, ObjectLayer replacement_layer)
    {
        RefreshCell(playerId, cell, tile_layer);
        RefreshCell(playerId, cell, replacement_layer);
    }



    public static void AddTileBlock(ulong playerId, int renderLayer, BuildingDef def, bool isReplacement, SimHashes element, int cell, bool isBlueprint = true)
    {
        if (customTileRenderers.TryGetValue(playerId, out var r))
        {
            r.AddBlock(renderLayer, def, isReplacement, element, cell, isBlueprint);
        }
        else
        {
            SgtLogger.warning("Tried adding tile block for " + playerId + ", but there was no valid tile renderer for it!");
        }
    }
    public static void RemoveTileBlock(ulong playerId, BuildingDef def, bool isReplacement, SimHashes element, int cell)
    {
        if (customTileRenderers.TryGetValue(playerId, out var r))
        {
            r.RemoveBlock(def, isReplacement, element, cell);
        }
        else
        {
            SgtLogger.warning("Tried removing tile block for " + playerId + ", but there was no valid tile renderer for it!");
        }
    }

    public void GetCachedCellColor(ref Color current, int cell, SimHashes element)
    {
        if (BlueprintState.ColoredCells.TryGetValue(PlayerId, out var cache) && cache.TryGetValue(cell, out var data))
        {
            current = data;
        }
    }

    public override Bits GetConnectionBits(int x, int y, int query_layer)
    {
        ///dont connect to existing things, looks weird
        //var realTileBits = base.GetConnectionBits(x, y, query_layer);
        //realTileBits |= GetVisualizerConnectionBits(x, y, query_layer);
        //return realTileBits;
        return GetVisualizerConnectionBits(x, y, query_layer);
    }
    public override Bits GetDecorConnectionBits(int x, int y, int query_layer)
    {
        ///dont connect to existing things, looks weird
        //var realTileBits = base.GetDecorConnectionBits(x, y, query_layer);
        //realTileBits |= GetVisualizerConnectionBits(x, y, query_layer);
        return GetVisualizerConnectionBits(x, y, query_layer);
    }
    bool MatchesDefVis(int cell, BuildingDef def)
    {
        if (!TileVisual.HasTileAt(PlayerId, cell, out var vis))
            return false;
        return vis == def;
    }

    public virtual Bits GetVisualizerConnectionBits(int x, int y, int query_layer)
    {
        Bits bits = (Bits)0;
        int cell = y * Grid.WidthInCells + x;
        if (!TileVisual.HasTileAt(PlayerId, cell, out var def))
            return bits;

        if (y > 0)
        {
            int cellDown = (y - 1) * Grid.WidthInCells + x;
            if (x > 0 && MatchesDefVis(cellDown - 1, def))
            {
                bits |= Bits.DownLeft;
            }

            if (MatchesDefVis(cellDown, def))
            {
                bits |= Bits.Down;
            }

            if (x < Grid.WidthInCells - 1 && MatchesDefVis(cellDown + 1, def))
            {
                bits |= Bits.DownRight;
            }
        }

        if (x > 0 && MatchesDefVis(cell - 1, def))
        {
            bits |= Bits.Left;
        }

        if (x < Grid.WidthInCells - 1 && MatchesDefVis(cell + 1, def))
        {
            bits |= Bits.Right;
        }

        if (y < Grid.HeightInCells - 1)
        {
            int cellAbove = (y + 1) * Grid.WidthInCells + x;
            if (x > 0 && MatchesDefVis(cellAbove - 1, def))
            {
                bits |= Bits.UpLeft;
            }

            if (MatchesDefVis(cellAbove, def))
            {
                bits |= Bits.Up;
            }

            ///- 1, not + 1: x never reaches WidthInCells, so a "+ 1" guard is always true and the
            ///rightmost column would look one cell past the end of the row above - which is the
            ///first cell of the row after that, making an edge tile connect to an unrelated tile on
            ///the far left. Every other diagonal/horizontal guard here already uses - 1.
            if (x < Grid.WidthInCells - 1 && MatchesDefVis(cellAbove + 1, def))
            {
                bits |= Bits.UpRight;
            }
        }

        return bits;
    }
}
