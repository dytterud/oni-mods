using BlueprintsV2.UnityUI;
using HarmonyLib;

namespace BlueprintsV2.Patches;

internal class TopLeftControlScreen_Patches
{
    /// <summary>
    /// The screen is rebuilt on each colony load, so the note visibility button is added every
    /// time it activates rather than once.
    /// </summary>
    [HarmonyPatch(typeof(TopLeftControlScreen), nameof(TopLeftControlScreen.OnActivate))]
    public class TopLeftControlScreen_OnActivate_Patch
    {
        public static void Postfix(TopLeftControlScreen __instance) => NoteVisibilityButton.Create(__instance);
    }
}
