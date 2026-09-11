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

    public static void RefreshCellInternal(ulong playerId, int cell, ObjectLayer tile_layer)
    {
        if (Game.IsQuitting() || !Grid.IsValidCell(cell))
        {
            return;
        }
        if (!customTileRenderers.TryGetValue(playerId, out var r))
            return;
        r.Rebuild(tile_layer, cell);

        GameObject gameObject = Grid.Objects[cell, (int)tile_layer];
        if (gameObject != null)
        {
            KAnimGraphTileVisualizer componentInChildren = gameObject.GetComponentInChildren<KAnimGraphTileVisualizer>();
            if (componentInChildren != null)
            {
                componentInChildren.Refresh();
            }
        }
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

    /// <summary>Opens a batch (re-entrant). <b>Must</b> be paired with <see cref="EndBatch"/> in a
    /// finally - an unclosed batch would leave the art stale until the next flush.</summary>
    public static void BeginBatch() => batchDepth++;

    public static void EndBatch()
    {
        if (batchDepth > 0)
            batchDepth--;
        if (batchDepth > 0 || pendingRefreshes.Count == 0)
            return;

        foreach (var (playerId, cell, layer) in pendingRefreshes)
            RefreshCellInternal(playerId, cell, layer);
        pendingRefreshes.Clear();
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
