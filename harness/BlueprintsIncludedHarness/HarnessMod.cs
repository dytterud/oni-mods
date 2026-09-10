using System;
using System.IO;
using HarmonyLib;
using KMod;
using UnityEngine;

namespace BlueprintsV2.Harness;

/// <summary>
/// Dev-only regression-test harness. Does nothing unless activated (see <see cref="HarnessGate"/>).
/// When active it hooks <c>MainMenu.OnSpawn</c>, loads the committed fixture colony, waits for the
/// sim, runs the assertion cases in <see cref="HarnessCases"/>, writes JUnit XML to the output
/// directory and quits the game.
/// </summary>
public sealed class HarnessMod : UserMod2
{
    public override void OnLoad(Harmony harmony)
    {
        if (!HarnessGate.Active)
        {
            Debug.Log($"[BPI-Harness] not activated (no {HarnessGate.EnvVar}=1 and no sentinel at {HarnessGate.SentinelPath}); staying dormant.");
            return;
        }

        Debug.Log("[BPI-Harness] activated.");
        Directory.CreateDirectory(HarnessGate.OutputDir);
        ExceptionSweep.Start();
        base.OnLoad(harmony); // applies the MainMenu patch below
    }

    // OnSpawn is protected (KScreen.OnSpawn override) - patch by string, like UtilLibs does.
    [HarmonyPatch(typeof(MainMenu), "OnSpawn")]
    private static class MainMenu_OnSpawn_Patch
    {
        private static bool started;

        private static void Postfix(MainMenu __instance)
        {
            if (started)
                return;
            started = true;

            var go = new GameObject("BpiHarnessRunner");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.AddComponent<HarnessRunner>();
        }
    }
}

/// <summary>Activation signal + well-known paths. The launcher writes the sentinel before starting ONI.</summary>
internal static class HarnessGate
{
    public const string EnvVar = "BPI_HARNESS";

    public static string OutputDir { get; } = Path.Combine(Path.GetTempPath(), "bpi-harness");
    public static string SentinelPath { get; } = Path.Combine(OutputDir, "run");
    public static string ResultsPath { get; } = Path.Combine(OutputDir, "results.xml");
    public static string LogPath { get; } = Path.Combine(OutputDir, "harness.log");

    /// <summary>Absolute path the launcher copies the fixture save to (avoids the local-vs-cloud
    /// save-folder ambiguity; <c>LoadScreen.DoLoad</c> takes a full path).</summary>
    public static string FixturePath { get; } = Path.Combine(OutputDir, "poc-colony.sav");

    public static bool Active =>
        Environment.GetEnvironmentVariable(EnvVar) == "1" || File.Exists(SentinelPath);
}
