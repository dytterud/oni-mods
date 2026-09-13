using System.Reflection;
using SaveProfiler.Patches;
using Xunit;

namespace SaveProfiler.Tests.Patches;

/// <summary>
/// Pins the shape of every method the profiler patches.
///
/// The shims inject arguments positionally (<c>__0</c>, <c>__1</c>, …), so their correctness rests
/// entirely on these signatures. If a game update reorders, renames or drops one of these methods,
/// the affected row stops being recorded — and docs/perf-method.md is emphatic that a row reading
/// zero is indistinguishable from a code path that never ran. That is a failure worth catching in
/// a test run rather than in a report nobody realises is wrong.
///
/// These are change-detectors, not correctness tests. A failure here means "the game moved, go
/// look", not "the profiler is broken".
///
/// Gated: resolving these needs the real assemblies to load, which the reference-only <c>./lib</c>
/// copies cannot do. Every assertion touches Klei types inside the method body and never in an
/// attribute argument — xUnit resolves attribute arguments at discovery, before the gate can skip
/// anything, so a Klei value in an <c>[InlineData]</c> would fail the offline build outright.
/// </summary>
public class PatchTargetResolutionTests
{
    private static ParameterInfo[] Widest(Type declaringType, string methodName)
    {
        MethodBase? target = PatchInstaller.WidestOverload(declaringType, methodName);
        Assert.NotNull(target);
        return target!.GetParameters();
    }

    [RequiresGameInstallFact]
    public void SaveLoaderSave_WidestOverloadIsTheRootAndExposesIsAutoSaveAtIndexOne()
    {
        MethodBase? target = PatchInstaller.WidestOverload(typeof(SaveLoader), nameof(SaveLoader.Save));
        Assert.NotNull(target);

        var p = target!.GetParameters();
        Assert.Equal(3, p.Length);
        Assert.Equal(typeof(string), p[0].ParameterType);
        // RootPostfix reads this as __1 to tell an autosave from a manual save.
        Assert.Equal(typeof(bool), p[1].ParameterType);
        Assert.Equal(typeof(bool), p[2].ParameterType);

        // RootPostfix reads __result as the path actually written.
        Assert.Equal(typeof(string), ((MethodInfo)target).ReturnType);
    }

    [RequiresGameInstallFact]
    public void SaveLoaderSave_StillHasTheInnerBinaryWriterOverload()
    {
        MethodInfo? inner = typeof(SaveLoader).GetMethod(
            nameof(SaveLoader.Save),
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static,
            binder: null,
            types: [typeof(BinaryWriter)],
            modifiers: null);

        Assert.NotNull(inner);
    }

    [RequiresGameInstallFact]
    public void CompressContents_ExposesTheUncompressedLengthAtIndexTwo()
    {
        var p = Widest(typeof(SaveLoader), "CompressContents");

        Assert.Equal(3, p.Length);
        Assert.Equal(typeof(BinaryWriter), p[0].ParameterType);
        Assert.Equal(typeof(byte[]), p[1].ParameterType);
        // CompressPostfix reads this as __2 — the only place the pre-compression size is available.
        Assert.Equal(typeof(int), p[2].ParameterType);
    }

    [RequiresGameInstallFact]
    public void SimSave_TakesTheWriterFirst()
    {
        var p = Widest(typeof(Sim), nameof(Sim.Save));

        Assert.Equal(3, p.Length);
        Assert.Equal(typeof(BinaryWriter), p[0].ParameterType);
    }

    [RequiresGameInstallFact]
    public void SaveManagerSave_TakesOnlyTheWriter()
    {
        var p = Widest(typeof(SaveManager), nameof(SaveManager.Save));

        Assert.Single(p);
        Assert.Equal(typeof(BinaryWriter), p[0].ParameterType);
    }

    [RequiresGameInstallFact]
    public void GameSave_TakesOnlyTheWriter()
    {
        var p = Widest(typeof(Game), nameof(Game.Save));

        Assert.Single(p);
        Assert.Equal(typeof(BinaryWriter), p[0].ParameterType);
    }

    [RequiresGameInstallFact]
    public void SaveWithoutTransform_ExposesTheWriterAtIndexZero()
    {
        var p = Widest(typeof(SaveLoadRoot), "SaveWithoutTransform");

        // ObjectPrefix/ObjectPostfix read this as __0 and diff its stream position to get the
        // per-object byte count.
        Assert.Single(p);
        Assert.Equal(typeof(BinaryWriter), p[0].ParameterType);
    }

    [RequiresGameInstallFact]
    public void SerializeTypeless_TakesTheObjectThenTheWriter()
    {
        var p = Widest(typeof(KSerialization.Serializer), "SerializeTypeless");

        // ComponentPostfix reads __0 for the type name and __1 for the byte count. Swapping these
        // would attribute every component's cost to the wrong name.
        Assert.Equal(2, p.Length);
        Assert.Equal(typeof(object), p[0].ParameterType);
        Assert.Equal(typeof(BinaryWriter), p[1].ParameterType);
    }

    [RequiresGameInstallFact]
    public void TheGapCandidates_AllStillExist()
    {
        // Added after the first real run left 1,092 ms unaccounted for inside SaveLoader.Save.
        // Only their existence is pinned - whether they actually run inside the save is a question
        // for a report, not for a test.
        Assert.NotNull(PatchInstaller.WidestOverload(typeof(SaveLoader), "PrepSaveFile"));
        Assert.NotNull(PatchInstaller.WidestOverload(typeof(Timelapser), "SaveColonyPreview"));
        Assert.NotNull(PatchInstaller.WidestOverload(typeof(Timelapser), "SaveScreenshot"));
    }

    [RequiresGameInstallFact]
    public void TheCycleBoundaryTargets_AllStillExist()
    {
        // Work outside SaveLoader.Save that the player still feels as part of the same hitch.
        Assert.NotNull(PatchInstaller.WidestOverload(typeof(ReportManager), "OnNightTime"));
        Assert.NotNull(PatchInstaller.WidestOverload(typeof(Timelapser), "OnNewDay"));
        Assert.NotNull(PatchInstaller.WidestOverload(typeof(Timelapser), "RenderAndPrint"));
    }

    [RequiresGameInstallFact]
    public void TimelapserRender_IsACoroutineAndIsDeliberatelyNotPatched()
    {
        // Render() returns an IEnumerator, so a prefix/postfix pair would time the construction of
        // the coroutine rather than the work, and report microseconds as though they were the
        // screenshot. RenderAndPrint is patched instead. If this ever stops returning an
        // IEnumerator, that decision is worth revisiting.
        var render = PatchInstaller.WidestOverload(typeof(Timelapser), "Render") as MethodInfo;

        Assert.NotNull(render);
        Assert.True(typeof(System.Collections.IEnumerator).IsAssignableFrom(render!.ReturnType));
    }

    [RequiresGameInstallFact]
    public void WidestOverload_ReturnsNullForAMethodThatIsNotThere()
    {
        // The contract PatchInstaller.TryPatch relies on to record an unresolved target instead of
        // throwing during mod load.
        Assert.Null(PatchInstaller.WidestOverload(typeof(SaveLoader), "NoSuchMethodExists"));
    }
}
