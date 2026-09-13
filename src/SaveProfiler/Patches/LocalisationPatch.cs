using HarmonyLib;
using UtilLibs;

namespace SaveProfiler.Patches;

class LocalisationPatch
{
    [HarmonyPatch(typeof(Localization), "Initialize")]
    public class Localization_Initialize_Patch
    {
        public static void Postfix()
        {
            LocalisationUtil.Translate(typeof(STRINGS), true);
        }
    }
}
