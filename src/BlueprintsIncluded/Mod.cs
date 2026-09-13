
using BlueprintsV2.BlueprintData.PlanningToolMod_Integration;
using BlueprintsV2.ModAPI;
using HarmonyLib;
using KMod;
using ONI_Together_API.Networking;
using PeterHan.PLib.Core;
using PeterHan.PLib.Options;
using UtilLibs;
using static BlueprintsV2.ModAssets;

namespace BlueprintsV2;

public class Mod : UserMod2
{
    public override void OnLoad(Harmony harmony)
    {
        SgtLogger.LogVersion(this, harmony);
        ModAssets.LoadAssets();
        PUtil.InitLibrary();
        new POptions().RegisterOptions(this, typeof(Config));
        base.OnLoad(harmony);

        ModAssets.RegisterActions();
        SgtLogger.l("Loading Mod Assets...");

        BlueprintFileHandling.AttachFileWatcher();

    }
    public override void OnAllModsLoaded(Harmony harmony, IReadOnlyList<KMod.Mod> mods)
    {
        base.OnAllModsLoaded(harmony, mods);
        API_Methods.RegisterExtraData();
        PlanningTool_Integration.Initialize();
        PacketRegistryAPI.AutoRegisterAll();
        WarnAboutConflictingMods(mods);
    }

    /// <summary>
    /// Queues a main-menu warning when upstream Blueprints Expanded, or a second copy of this mod,
    /// is loaded alongside this one. Both would apply the same Harmony patches twice - see
    /// <see cref="ModConflicts"/> for why the two cases need different detection.
    ///
    /// <para>Warns rather than prevents, deliberately: by the time OnAllModsLoaded runs both mods
    /// have already patched, so nothing done here can rescue the current session. The dialog is
    /// drained and shown by UtilLibs' MainMenu.OnSpawn patch, which pools messages from every mod
    /// using it so a player running several gets one dialog rather than a queue of them.</para>
    ///
    /// <para><b>Measured 2026-09-13, and not what was expected:</b> against the current upstream
    /// the dialog is never seen. Running both, upstream's SpritePatch prefix throws inside
    /// Assets.OnPrefabInit, that cascades, and ONI's own crash handler takes over <i>before</i>
    /// MainMenu exists - the log for that run contains no MainMenu line at all, so the queued
    /// message is never drained. The game's crash screen names both mods itself, which is most of
    /// what the dialog would have said.</para>
    ///
    /// <para>So the reliable half of this is the log line - <c>incompatible mod found:
    /// BlueprintsV2</c> - which turns "random NullReferenceException at startup" into a named cause
    /// when someone posts their Player.log. The dialog stays as the path for a conflict that does
    /// <i>not</i> crash during asset load; don't assume a player ever saw it.</para>
    /// </summary>
    private static void WarnAboutConflictingMods(IReadOnlyList<KMod.Mod> mods)
    {
        CompatibilityNotifications.CheckAndAddIncompatibles(
            ModConflicts.UpstreamAssemblyName, ModConflicts.OurModName, ModConflicts.UpstreamModName);

        // IsActive rather than "enabled": a mod that is switched on but never loaded has applied no
        // patches and is nothing to warn about. False positives matter more than false negatives
        // here - this dialog would otherwise greet ordinary players at every launch.
        if (ModConflicts.HasDuplicateCopy(mods.Where(m => m.IsActive()).Select(m => m.staticID)))
            CompatibilityNotifications.AddIncompatibleToList(ModConflicts.OurModName, ModConflicts.DuplicateCopyName);
    }
}
