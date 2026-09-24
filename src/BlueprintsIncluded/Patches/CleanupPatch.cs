using BlueprintsV2.BlueprintData;
using BlueprintsV2.Tools;
using BlueprintsV2.UnityUI;
using HarmonyLib;

namespace BlueprintsV2.Patches;

internal class CleanupPatch
{
    [HarmonyPatch(typeof(Game), "DestroyInstances")]
    public static class GameDestroyInstances
    {
        public static void Postfix()
        {
            CreateBlueprintTool.DestroyInstance();
            CreateNoteTool.DestroyInstance();
            UseBlueprintTool.DestroyInstance();
            SnapshotTool.DestroyInstance();
            MultiToolParameterMenu.DestroyInstance();
            ModAssets.SelectedBlueprint = null;
            ModAssets.SelectedFolder = null;
            ModAssets.BLUEPRINTS_AUTOFILE_WATCHER.Dispose();
            CurrentBlueprintStateScreen.DestroyInstance();
            NoteToolScreen.DestroyInstance();
            SpriteSelectorScreen.DestroyInstance();
            BlueprintSelectionScreen.DestroyInstance();
            BlueprintRenamingScreen.DestroyInstance();
            ///a preconfigure session that is still open when the colony is torn down never gets its
            ///SelectObject event, and its button latch is a process-lifetime static (#109)
            UnderConstructionDataSettingHelper.ResetSessionState();
            ///note visibility is not saved; the next colony starts with its notes shown
            BlueprintState.ResetNoteVisibility();
        }
    }
}
