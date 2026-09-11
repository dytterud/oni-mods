using System;
using System.IO;
using System.Linq;
using HarmonyLib;
using KMod;
using UnityEngine;

namespace BlueprintsV2.Harness;

/// <summary>
/// Dev-only regression-test harness. Does nothing unless activated (see <see cref="HarnessGate"/>).
/// When active it hooks <c>MainMenu.OnSpawn</c>, loads the committed fixture colony, waits for the
/// sim, and either runs the assertion cases in <see cref="HarnessCases"/> (writing JUnit XML) or -
/// in perf mode - the benchmarks in <see cref="Perf.PerfRunner"/> (writing perf.json), then quits.
/// </summary>
public sealed class HarnessMod : UserMod2
{
    public override void OnLoad(Harmony harmony)
    {
        if (!HarnessGate.Active)
        {
            Debug.Log($"[BPI-Harness] not activated (no {HarnessGate.EnvVar}=1/perf and no sentinel at {HarnessGate.SentinelPath}); staying dormant.");
            return;
        }

        Debug.Log($"[BPI-Harness] activated, mode={HarnessGate.Mode}.");
        Directory.CreateDirectory(HarnessGate.OutputDir);
        HarnessGate.HarmonyInstance = harmony;
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

/// <summary>The harness's two modes. <c>Run</c> is the regression pass (assertion cases, JUnit
/// output); <c>Perf</c> is the opt-in benchmark pass (docs/in-game-regression-testing.md §7).</summary>
internal enum HarnessMode
{
    None,
    Run,
    Perf,
}

/// <summary>Activation signal + well-known paths. The launcher writes the sentinel before starting ONI.</summary>
internal static class HarnessGate
{
    public const string EnvVar = "BPI_HARNESS";

    public static string OutputDir { get; } = Path.Combine(Path.GetTempPath(), "bpi-harness");
    public static string SentinelPath { get; } = Path.Combine(OutputDir, "run");
    public static string ResultsPath { get; } = Path.Combine(OutputDir, "results.xml");
    public static string PerfPath { get; } = Path.Combine(OutputDir, "perf.json");
    public static string LogPath { get; } = Path.Combine(OutputDir, "harness.log");

    /// <summary>Absolute path the launcher copies the fixture save to (avoids the local-vs-cloud
    /// save-folder ambiguity; <c>LoadScreen.DoLoad</c> takes a full path).</summary>
    public static string FixturePath { get; } = Path.Combine(OutputDir, "poc-colony.sav");

    /// <summary>Set by <see cref="HarnessMod.OnLoad"/> so perf instrumentation can apply patches.</summary>
    public static Harmony? HarmonyInstance { get; set; }

    /// <summary>
    /// The sentinel's first line selects the mode ("perf" / "perf-attribution", anything else =
    /// "run"); the rest of the file is just a human-readable timestamp. BPI_HARNESS=1 / =perf /
    /// =perf-attribution works the same way without a sentinel, for a quick manual launch.
    /// </summary>
    public static HarnessMode Mode => FirstLine() switch
    {
        "perf" or PerfAttribution => HarnessMode.Perf,
        "1" or "run" => HarnessMode.Run,
        null => HarnessMode.None,
        _ => HarnessMode.Run,
    };

    /// <summary>
    /// Turns on <c>PerfInstrumentation</c>'s per-visual hotspots: they measure methods whose bodies
    /// are cheaper than the Harmony wrapper around them, so they are honest about <i>call counts and
    /// allocation</i> but inflate every timing in the run. A run with this on must never have its
    /// medians compared against one without it (docs §7).
    /// </summary>
    public static bool Attribution =>
        string.Equals(FirstLine(), PerfAttribution, StringComparison.OrdinalIgnoreCase);

    private const string PerfAttribution = "perf-attribution";

    /// <summary>The env var if set, else the sentinel's first line, else null for "not active".</summary>
    private static string? FirstLine()
    {
        string? env = Environment.GetEnvironmentVariable(EnvVar);
        if (!string.IsNullOrWhiteSpace(env))
            return env!.Trim().ToLowerInvariant();

        if (File.Exists(SentinelPath))
            return (File.ReadLines(SentinelPath).FirstOrDefault() ?? "").Trim().ToLowerInvariant();

        return null;
    }

    public static bool Active => Mode != HarnessMode.None;
}
