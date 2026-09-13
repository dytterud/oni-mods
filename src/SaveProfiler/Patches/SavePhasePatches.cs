using System.Diagnostics;
using System.Reflection;
using HarmonyLib;
using SaveProfiler.Recording;

namespace SaveProfiler.Patches;

/// <summary>
/// Times the save pipeline's phases. Verified against the shipped assemblies:
///
/// <code>
/// SaveLoader.Save(string filename, bool isAutoSave, bool updateSavePointer) -> string   // the root
///   Sim.Save(BinaryWriter, int x, int y)
///   SaveManager.Save(BinaryWriter)          // the per-component reflection walk
///   Game.Save(BinaryWriter)
///   SaveLoader.Save(BinaryWriter)           // the inner overload
///   SaveLoader.CompressContents(BinaryWriter, byte[] uncompressed, int length)
/// </code>
///
/// Arguments are injected positionally (<c>__0</c>, <c>__1</c>, …) rather than by name, because a
/// parameter name is a private detail a game update may rename without changing behaviour, while
/// the position is part of the call. A shape change makes Harmony throw at patch time, which
/// <see cref="PatchInstaller.TryPatch"/> turns into a named unresolved target - loud, not silent.
/// That is the failure mode docs/perf-method.md insists on: a row reading zero must never be
/// mistakable for a path that never ran.
///
/// <c>Stopwatch.GetTimestamp()</c>, never <c>Stopwatch.StartNew()</c>: a <c>Stopwatch</c> is a
/// class, and a child phase's prefix runs inside its parent's measured window, so starting one
/// would charge the child's allocation to the parent. <see cref="PhaseState"/> is a readonly
/// struct passed by <c>out</c> for the same reason - the wrapper allocates nothing.
/// </summary>
internal static class SavePhasePatches
{
    private static readonly Dictionary<MethodBase, string> phaseNames = new();

    public static void Register(Harmony harmony)
    {
        // The root: the widest Save overload is the one taking (filename, isAutoSave,
        // updateSavePointer) and returning the written path. Everything else nests inside it.
        PatchInstaller.TryPatch(harmony, "SaveLoader.Save(string,bool,bool)",
            () => PatchInstaller.WidestOverload(typeof(SaveLoader), nameof(SaveLoader.Save)),
            typeof(SavePhasePatches), nameof(RootPrefix), nameof(RootPostfix));

        // The inner Save(BinaryWriter) overload, timed separately: sharing one name with the root
        // would count nearly the whole operation twice.
        PatchInstaller.TryPatch(harmony, "SaveLoader.Save(BinaryWriter)",
            () => AccessTools.Method(typeof(SaveLoader), nameof(SaveLoader.Save), [typeof(BinaryWriter)]),
            typeof(SavePhasePatches), nameof(PhasePrefix), nameof(PhasePostfix),
            onBound: target => phaseNames[target] = "SaveLoader.Save (inner)");

        RegisterPhase(harmony, "Sim.Save", typeof(Sim), nameof(Sim.Save));

        // Candidates for the gap the first real run found: 1,092 ms sat inside the root but
        // outside both the inner save and compression. The outer Save also preps the file, rotates
        // old autosaves and writes the colony preview image, and none of that was named. These are
        // registered as ordinary phases, so if they turn out to run outside the save instead they
        // simply never appear rather than being mis-attributed.
        RegisterPhase(harmony, "SaveLoader.PrepSaveFile", typeof(SaveLoader), "PrepSaveFile");
        // SaveColonyPreview stays here - it does run inside the save (measured at 0.0 ms).
        // SaveScreenshot moved to CycleBoundaryPatches: it recorded zero calls in every run,
        // because it happens at the cycle boundary rather than in the save.
        RegisterPhase(harmony, "Timelapser.SaveColonyPreview", typeof(Timelapser), "SaveColonyPreview");
        RegisterPhase(harmony, "SaveManager.Save", typeof(SaveManager), nameof(SaveManager.Save));
        RegisterPhase(harmony, "Game.Save", typeof(Game), nameof(Game.Save));

        // Its own shim: the postfix reads the uncompressed length off argument 2, which is the
        // only place the pre-compression size is available without re-measuring the file.
        PatchInstaller.TryPatch(harmony, "SaveLoader.CompressContents",
            () => PatchInstaller.WidestOverload(typeof(SaveLoader), "CompressContents"),
            typeof(SavePhasePatches), nameof(PhasePrefix), nameof(CompressPostfix),
            onBound: target => phaseNames[target] = "SaveLoader.CompressContents");
    }

    private static void RegisterPhase(Harmony harmony, string name, Type declaringType, string methodName) =>
        PatchInstaller.TryPatch(harmony, name,
            () => PatchInstaller.WidestOverload(declaringType, methodName),
            typeof(SavePhasePatches), nameof(PhasePrefix), nameof(PhasePostfix),
            onBound: target => phaseNames[target] = name);

    // ---------------------------------------------------------------- the root

    internal static void RootPrefix(out PhaseState __state)
    {
        SaveProfileRecorder.Begin();
        SaveProfileRecorder.EnterPhase(SaveProfileReport.RootPhase);
        __state = new PhaseState(Stopwatch.GetTimestamp());
    }

    /// <summary><c>__1</c> is <c>isAutoSave</c>; <c>__result</c> is the path actually written.</summary>
    internal static void RootPostfix(PhaseState __state, bool __1, string? __result)
    {
        SaveProfileRecorder.ExitPhase(SaveProfileReport.RootPhase, __state.ElapsedMs());
        SaveProfileRecorder.End();
        ReportWriter.WriteIfEnabled(__result ?? "", __1);
    }

    // ---------------------------------------------------------------- the children

    /// <summary>Pushes before timing starts, so a phase started inside this one records this one
    /// as its parent. <c>__originalMethod</c> is injected into prefixes as well as postfixes,
    /// which is what lets one shared pair serve every target.</summary>
    internal static void PhasePrefix(out PhaseState __state, MethodBase __originalMethod)
    {
        if (phaseNames.TryGetValue(__originalMethod, out string? name))
            SaveProfileRecorder.EnterPhase(name);

        __state = new PhaseState(Stopwatch.GetTimestamp());
    }

    internal static void PhasePostfix(PhaseState __state, MethodBase __originalMethod)
    {
        if (phaseNames.TryGetValue(__originalMethod, out string? name))
            SaveProfileRecorder.ExitPhase(name, __state.ElapsedMs());
    }

    /// <summary><c>__2</c> is the uncompressed byte count being fed to the compressor.</summary>
    internal static void CompressPostfix(PhaseState __state, MethodBase __originalMethod, int __2)
    {
        if (phaseNames.TryGetValue(__originalMethod, out string? name))
            SaveProfileRecorder.ExitPhase(name, __state.ElapsedMs(), __2);
    }

    /// <summary>Readonly struct, so the timing wrapper allocates nothing - see the type remarks.</summary>
    internal readonly struct PhaseState
    {
        private static readonly double TicksToMs = 1000.0 / Stopwatch.Frequency;

        public PhaseState(long startTimestamp) => StartTimestamp = startTimestamp;

        public long StartTimestamp { get; }

        public double ElapsedMs() => (Stopwatch.GetTimestamp() - StartTimestamp) * TicksToMs;
    }
}
