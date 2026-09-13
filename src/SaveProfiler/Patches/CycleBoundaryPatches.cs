using System.Diagnostics;
using System.Reflection;
using HarmonyLib;
using SaveProfiler.Recording;

namespace SaveProfiler.Patches;

/// <summary>
/// Times the cycle-boundary work that happens <em>around</em> the save rather than inside it.
///
/// The save is not the whole hitch. A new cycle also builds the daily report and takes the
/// timelapse screenshot, and neither runs inside <c>SaveLoader.Save</c> — so neither appears in any
/// phase total, while the player feels all of it as one pause. Measured against a cycle-585 colony
/// the save came to ~3.7–3.9 s against a wristwatch reading nearer 4.2 s, and this is the gap that
/// difference points at.
///
/// Verified against the shipped assemblies:
/// <code>
/// ReportManager.OnNightTime(object data)      // builds the daily report
/// Timelapser.OnNewDay(object data)            // kicks off the screenshot
/// Timelapser.SaveScreenshot()
/// Timelapser.RenderAndPrint(int world_id)     // the actual render + encode
/// </code>
///
/// <c>Timelapser.Render()</c> is deliberately <b>not</b> patched: it returns an
/// <see cref="System.Collections.IEnumerator"/>, so a prefix/postfix pair would time the creation
/// of the coroutine — microseconds — and report it as though it were the work. A wrapper that
/// measures the wrong thing is worse than no wrapper, because it produces a number. Its body is
/// reached through <c>RenderAndPrint</c> instead, which is a real method.
///
/// These land in a bucket that survives <see cref="SaveProfileRecorder.Begin"/>, because some of
/// this runs before the save and some after the report has already been written.
/// </summary>
internal static class CycleBoundaryPatches
{
    private static readonly double TicksToMs = 1000.0 / Stopwatch.Frequency;

    private static readonly Dictionary<MethodBase, string> names = new();

    public static void Register(Harmony harmony)
    {
        Register(harmony, "ReportManager.OnNightTime", typeof(ReportManager), "OnNightTime");
        Register(harmony, "Timelapser.OnNewDay", typeof(Timelapser), "OnNewDay");
        Register(harmony, "Timelapser.SaveScreenshot", typeof(Timelapser), "SaveScreenshot");
        Register(harmony, "Timelapser.RenderAndPrint", typeof(Timelapser), "RenderAndPrint");
    }

    private static void Register(Harmony harmony, string name, Type declaringType, string methodName) =>
        PatchInstaller.TryPatch(harmony, name,
            () => PatchInstaller.WidestOverload(declaringType, methodName),
            typeof(CycleBoundaryPatches), nameof(Prefix), nameof(Postfix),
            onBound: target => names[target] = name);

    internal static void Prefix(out AmbientState __state) =>
        __state = new AmbientState(Stopwatch.GetTimestamp());

    internal static void Postfix(AmbientState __state, MethodBase __originalMethod)
    {
        if (names.TryGetValue(__originalMethod, out string? name))
            SaveProfileRecorder.AddAmbient(name, __state.ElapsedMs());
    }

    /// <summary>Readonly struct passed by <c>out</c>, so the wrapper allocates nothing — the same
    /// rule as the save phases.</summary>
    internal readonly struct AmbientState
    {
        public AmbientState(long startTimestamp) => StartTimestamp = startTimestamp;

        public long StartTimestamp { get; }

        public double ElapsedMs() => (Stopwatch.GetTimestamp() - StartTimestamp) * TicksToMs;
    }
}
