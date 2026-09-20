using System.Reflection.Emit;
using BlueprintsV2.ModAPI;
using HarmonyLib;
using UnityEngine;
using UtilLibs;

namespace BlueprintsV2.BlueprintData;

public static class UnderConstructionDataSettingHelper
{
    public static List<string> ComponentsToIgnore =
        [
        nameof(BuildingEnabledButton)
			//,nameof(Prioritizable)
			//,API_Consts.ConduitFlagID

			];

    static GameObject? temporaryTargetBuilding;
    static UnderConstructionDataTransfer? lastSelected;
    public static KSelectable? TemporarySelectable { get; private set; }

    public static bool HasDataTransferComponents(BuildingUnderConstruction? building)
    {
        if (building == null)
            return false;
        if (!API_Methods.AllowedByRules(building.Def))
            return false;
        var completeVersion = building.Def.BuildingComplete;
        if (completeVersion.HasTag(API_Consts.SkipPreconfiguration))
            return false;

        try
        {
            var data = API_Methods.GetAdditionalBuildingData(completeVersion);
            foreach (var entry in ComponentsToIgnore)
                data.Remove(entry);
            SgtLogger.l("Checking if " + completeVersion.name + " has data transfer components, found " + data.Count + " entries:");
            foreach (var entry in data)
            {
                SgtLogger.l(entry.Key);
            }

            return data.Any();
        }
        catch
        {
            return false;
        }
;
    }
    public static void StartEditingUnderConstructionData(UnderConstructionDataTransfer origin)
    {
        lastSelected = origin;
        var def = origin.building.Def;
        if (temporaryTargetBuilding != null)
        {
            UnityEngine.Object.Destroy(temporaryTargetBuilding);
            temporaryTargetBuilding = null;
        }

        var world = origin.GetMyWorld();
        var worldOffset = world.WorldOffset;

        int cell = Grid.XYToCell(worldOffset.X, worldOffset.Y);

        cell += Mathf.CeilToInt((def.WidthInCells / 2f)); //spawn it close to the origin, but dont let it clip into negative cell indicies


        ///A building that occupies cells replaces the world-border wall it is spawned inside, and
        ///destroying it again leaves vacuum where the wall was - a hole straight out of the map
        ///(#80). Remember what the wall was made of first; CleanUp puts it back.
        borrowedBorderCells.Clear();
        def.RunOnArea(cell, Orientation.Neutral, borderCell =>
        {
            if (Grid.IsValidCell(borderCell) && Grid.Element[borderCell]?.id == SimHashes.Unobtanium)
                borrowedBorderCells.Add(new BorderCell(borderCell, Grid.Mass[borderCell], Grid.Temperature[borderCell]));
        });

        temporaryTargetBuilding = def.Create(Grid.CellToPos(cell), null, [SimHashes.Unobtanium.CreateTag()], null, 100, def.BuildingComplete);
        temporaryTargetBuilding.GetComponent<DataTransferCleanup>().SetInUse();
        TemporarySelectable = temporaryTargetBuilding.GetComponent<KSelectable>();
        //prevent "build outside start biome" achievment from triggering
        temporaryTargetBuilding.GetComponent<KPrefabID>().AddTag(GameTags.TemplateBuilding);

        if (temporaryTargetBuilding.TryGetComponent<KBatchedAnimController>(out var kbac))
            kbac.animScale = 0;

        //hide deconstruction button
        if (temporaryTargetBuilding.TryGetComponent<Deconstructable>(out var decon))
            decon.allowDeconstruction = false;

        //1 frame delay to properly load the extra buttons on the menu screen.
        //UIScheduler, not GameScheduler: the game scheduler runs off the sim clock and does not
        //tick while the game is paused, so the callback would sit queued until the player
        //unpaused and then fire against a building that had long since been cleaned up.
        var target = temporaryTargetBuilding;
        UIScheduler.Instance.ScheduleNextFrame("preconfigure select", (_) =>
        {
            if (target == null) //destroyed while the callback was pending
            {
                //only tidy up if a newer edit session has not already taken over
                if (ReferenceEquals(target, temporaryTargetBuilding))
                    CleanUp();
                return;
            }
            UnderConstructionDataTransfer.TransferDataTo(target, origin.GetStoredData());
            Game.Instance.Trigger((int)GameHashes.SelectObject, target);
        });
    }
    public static void HandleDeselection(DataTransferCleanup data)
    {
        if (lastSelected != null)
        {
            var buildingSettingData = API_Methods.GetAdditionalBuildingData(data.gameObject);
            foreach (var entries in buildingSettingData)
                lastSelected.SetDataToApply(entries.Key, entries.Value);
        }
        CleanUp();
    }

    internal static void CleanUp()
    {
        if (temporaryTargetBuilding != null)
            UnityEngine.Object.Destroy(temporaryTargetBuilding);
        TemporarySelectable = null;
        UnderConstructionDataTransfer.SelectButtonUnlocked = true;
        RefillBorrowedBorderCells();
    }

    ///the world-border cells the temporary building was spawned into, with what they were made of
    ///before it took them over.
    private readonly record struct BorderCell(int Cell, float Mass, float Temperature);
    private static readonly List<BorderCell> borrowedBorderCells = new();

    /// <summary>
    /// Puts the world-border wall back after the temporary building has been destroyed. Destroying
    /// a cell-occupying building empties its cells, so without this the border is left with a hole
    /// in it (#80).
    /// </summary>
    static void RefillBorrowedBorderCells()
    {
        if (borrowedBorderCells.Count == 0)
            return;

        var cells = borrowedBorderCells.ToArray();
        borrowedBorderCells.Clear();

        ///next frame, because the destroy above empties the cells on its way out and would
        ///overwrite a refill done right here. UIScheduler rather than GameScheduler: the preconfigure
        ///screen is normally open with the game paused, and the sim clock does not tick then.
        UIScheduler.Instance.ScheduleNextFrame("preconfigure border refill", _ =>
        {
            foreach (var borrowed in cells)
                SimMessages.ReplaceElement(borrowed.Cell, SimHashes.Unobtanium,
                    CellEventLogger.Instance.DebugTool, borrowed.Mass, borrowed.Temperature);
        });
    }


    /// <summary>
    /// turn off status items on the temp building
    /// </summary>
    [HarmonyPatch(typeof(KSelectable), nameof(KSelectable.AddStatusItem))]
    public class KSelectable_AddStatusItem_Patch
    {
        public static bool Prefix(KSelectable __instance, ref Guid __result)
        {
            if (TemporarySelectable != __instance)
                return true;

            __result = Guid.Empty;
            return false;
        }
    }
    /// <summary>
    /// override the prioritizable check to allow the temporary target building to be prioritized.
    /// </summary>
    [HarmonyPatch(typeof(Prioritizable), nameof(Prioritizable.IsPrioritizable))]
    public class Prioritizable_IsPrioritizable_Patch
    {
        public static bool Prefix(Prioritizable __instance, ref bool __result)
        {
            if (__instance.gameObject == temporaryTargetBuilding)
            {
                __result = true;
                return false;
            }
            return true;
        }
    }

    /// <summary>
    /// Prevents the ComplexFabricatorSideScreen from showing the temporary target building as a valid target since recipes arent configurable and it crashes the soldering station.
    /// </summary>
    [HarmonyPatch(typeof(ComplexFabricatorSideScreen), nameof(ComplexFabricatorSideScreen.IsValidForTarget))]
    public class ComplexFabricatorSideScreen_IsValidForTarget_Patch
    {
        public static bool Prefix(GameObject target) => target != temporaryTargetBuilding;
    }
    /// <summary>
    /// dont show copy setting button on temp building
    /// </summary>
    [HarmonyPatch(typeof(CopyBuildingSettings), nameof(CopyBuildingSettings.OnRefreshUserMenu))]
    public class CopyBuildingSettings_OnRefreshUserMenu_Patch
    {
        public static bool Prefix(CopyBuildingSettings __instance) => __instance.gameObject != temporaryTargetBuilding;
    }

    /// <summary>
    /// dont show temp building in hover cards
    /// </summary>
    [HarmonyPatch(typeof(SelectToolHoverTextCard), nameof(SelectToolHoverTextCard.UpdateHoverElements))]
    public class SelectToolHoverTextCard_UpdateHoverElements_Patch
    {
        public static void Prefix(List<KSelectable> hoverObjects)
        {
            if (TemporarySelectable != null)
                hoverObjects.Remove(TemporarySelectable);
        }
    }

    /// <summary>
    /// Prevents the ModularConduitPortTiler from updating endcaps if the visualizers are not initialized as that would crash the game.
    /// this happens if 2 or more launchpads are placed via blueprint
    /// </summary>
    [HarmonyPatch(typeof(ModularConduitPortTiler), nameof(ModularConduitPortTiler.UpdateEndCaps))]
    public class ModularConduitPortTiler_UpdateEndCaps_Patch
    {
        public static bool Prefix(ModularConduitPortTiler __instance)
        {
            bool hasVisualizersInitialized = true;
            if (__instance.manageLeftCap)
            {
                if (__instance.leftCapDefault == null || __instance.leftCapConduit == null || __instance.leftCapLaunchpad == null)
                    hasVisualizersInitialized = false;
            }
            if (__instance.manageRightCap)
            {
                if (__instance.rightCapDefault == null || __instance.rightCapConduit == null || __instance.rightCapLaunchpad == null)
                    hasVisualizersInitialized = false;
            }
            return hasVisualizersInitialized;
        }
    }



    [HarmonyPatch(typeof(ReceptacleSideScreen), nameof(ReceptacleSideScreen.Initialize))]
    public class ReceptacleSideScreen_Initialize_Patch
    {
        /// <summary>
        /// prevents that stupid assert in ReceptacleSideScreen.Initialize that crashes the game for no reason on preconfiguring sometimes for some people due to race conditions
        /// </summary>
        /// <param name="_"></param>
        /// <param name="orig"></param>
        /// <returns></returns>
        public static IEnumerable<CodeInstruction> Transpiler(ILGenerator _, IEnumerable<CodeInstruction> orig)
        {
            var m_Assert = AccessTools.Method(typeof(Debug), nameof(Debug.Assert), [typeof(bool), typeof(object)]);

            foreach (var ci in orig)
            {
                if (ci.Calls(m_Assert))
                {
                    yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(ReceptacleSideScreen_Initialize_Patch), nameof(DoNotCrashThisScreenWithThatStoopidAssert)));
                }
                else
                    yield return ci;

            }
        }

        private static void DoNotCrashThisScreenWithThatStoopidAssert(bool entityCountCorrect, object crashMsg)
        {
            if (entityCountCorrect)
                return;
            SgtLogger.warning((string)crashMsg);
        }
    }
}
