using HarmonyLib;
using KMod;
using PeterHan.PLib.Core;
using PeterHan.PLib.Options;
using SaveProfiler.Patches;
using SaveProfiler.Recording;
using UtilLibs;

namespace SaveProfiler;

public class Mod : UserMod2
{
    public override void OnLoad(Harmony harmony)
    {
        SgtLogger.LogVersion(this, harmony);
        PUtil.InitLibrary();
        new POptions().RegisterOptions(this, typeof(Config));

        // Read before the patches are installed: whether per-component attribution is on decides
        // whether that patch is applied at all, and a patch is not something to toggle per save.
        SaveProfileRecorder.AttributionEnabled = Config.Instance.ComponentAttribution;

        base.OnLoad(harmony);

        // Manual patching, after base.OnLoad has done PatchAll for the attribute-based ones. The
        // measurement targets are overloaded, so they are resolved by name rather than by a pinned
        // parameter list - see PatchInstaller for why that distinction is load-bearing here.
        PatchInstaller.Apply(harmony);
    }

    public override void OnAllModsLoaded(Harmony harmony, IReadOnlyList<KMod.Mod> mods)
    {
        base.OnAllModsLoaded(harmony, mods);

        // Recorded in every report. Another mod on the save path means the numbers describe that
        // mod as much as the game - Fast Save in particular replaces the serialization measured
        // here - and a report that did not name them would be silently misleading.
        ReportWriter.ActiveMods = mods
            .Where(m => m.IsActive())
            .Select(m => m.staticID)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();
    }
}
