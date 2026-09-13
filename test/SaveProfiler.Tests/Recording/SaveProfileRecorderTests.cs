using SaveProfiler.Recording;
using Xunit;

namespace SaveProfiler.Tests.Recording;

/// <summary>
/// Locks the recorder's bookkeeping: what a new recording clears, and which bucket a component
/// lands in.
///
/// The nesting rule is the one worth a test. A nested serialization runs inside its parent's
/// measured window, so attributing both to their own type would count the inner time twice and
/// quietly inflate whichever type happens to contain others. Sending nested calls to their own
/// bucket keeps them visible without double-counting.
///
/// Ungated — <see cref="SaveProfileRecorder"/> is pure BCL. It is static, so every test opens with
/// <c>Begin()</c> to reset it rather than relying on the order xUnit happens to run them in.
/// </summary>
public class SaveProfileRecorderTests
{
    private static SampleAccumulator.Snapshot TypeSnap(SaveProfileReport report, string name) =>
        report.Types.Single(t => t.TypeName == name).Snap;

    /// <summary>Open and immediately close a leaf phase.</summary>
    private static void Phase(string name, double ms, long bytes = 0)
    {
        SaveProfileRecorder.EnterPhase(name);
        SaveProfileRecorder.ExitPhase(name, ms, bytes);
    }

    [Fact]
    public void Begin_OpensARecording()
    {
        SaveProfileRecorder.Begin();
        Assert.True(SaveProfileRecorder.Recording);

        SaveProfileRecorder.End();
        Assert.False(SaveProfileRecorder.Recording);
    }

    [Fact]
    public void Begin_ClearsWhatThePreviousSaveRecorded()
    {
        SaveProfileRecorder.Begin();
        Phase("Sim.Save", 5);
        SaveProfileRecorder.EnterComponent();
        SaveProfileRecorder.AddComponent("Storage", 1, 10);
        SaveProfileRecorder.ExitComponent();

        SaveProfileRecorder.Begin();
        var report = SaveProfileRecorder.BuildReport();

        Assert.Empty(report.Phases);
        Assert.Empty(report.Types);
    }

    [Fact]
    public void BuildReport_SeparatesTheRootFromTheNestedPhases()
    {
        SaveProfileRecorder.Begin();
        SaveProfileRecorder.EnterPhase(SaveProfileReport.RootPhase);
        Phase("Sim.Save", 30);
        SaveProfileRecorder.ExitPhase(SaveProfileReport.RootPhase, 100);

        var report = SaveProfileRecorder.BuildReport();

        Assert.NotNull(report.Root);
        Assert.Equal(100, report.Root!.Snap.TotalMs);
        var child = Assert.Single(report.Phases);
        Assert.Equal("Sim.Save", child.Name);
        Assert.Equal(SaveProfileReport.RootPhase, child.Parent);
        Assert.Equal(70, report.UnaccountedMs, 9);
    }

    [Fact]
    public void RepeatedPhaseCalls_AccumulateUnderOneName()
    {
        SaveProfileRecorder.Begin();
        Phase("SaveLoadRoot.SaveWithoutTransform", 1, 10);
        Phase("SaveLoadRoot.SaveWithoutTransform", 3, 20);

        var phase = Assert.Single(SaveProfileRecorder.BuildReport().Phases);

        Assert.Equal(2, phase.Snap.Calls);
        Assert.Equal(4, phase.Snap.TotalMs, 9);
        Assert.Equal(30, phase.Snap.TotalBytes);
    }

    [Fact]
    public void AnOutermostComponent_IsAttributedToItsOwnType()
    {
        SaveProfileRecorder.Begin();

        SaveProfileRecorder.EnterComponent();
        SaveProfileRecorder.AddComponent("PrimaryElement", 2, 40);
        SaveProfileRecorder.ExitComponent();

        var report = SaveProfileRecorder.BuildReport();

        Assert.Equal(40, TypeSnap(report, "PrimaryElement").TotalBytes);
        Assert.DoesNotContain(report.Types, t => t.TypeName == SaveProfileRecorder.NestedBucket);
    }

    [Fact]
    public void ANestedComponent_GoesToTheNestedBucketNotItsParentsType()
    {
        SaveProfileRecorder.Begin();

        SaveProfileRecorder.EnterComponent();          // outermost
        SaveProfileRecorder.EnterComponent();          // nested inside it
        SaveProfileRecorder.AddComponent("Inner", 1, 5);
        SaveProfileRecorder.ExitComponent();
        SaveProfileRecorder.AddComponent("Outer", 9, 90);
        SaveProfileRecorder.ExitComponent();

        var report = SaveProfileRecorder.BuildReport();

        Assert.Equal(90, TypeSnap(report, "Outer").TotalBytes);
        Assert.Equal(5, TypeSnap(report, SaveProfileRecorder.NestedBucket).TotalBytes);
        // The nested call's own type never appears: its time is already inside Outer's.
        Assert.DoesNotContain(report.Types, t => t.TypeName == "Inner");
    }

    [Fact]
    public void SiblingComponents_AreBothOutermost()
    {
        SaveProfileRecorder.Begin();

        SaveProfileRecorder.EnterComponent();
        SaveProfileRecorder.AddComponent("First", 1, 10);
        SaveProfileRecorder.ExitComponent();

        SaveProfileRecorder.EnterComponent();
        SaveProfileRecorder.AddComponent("Second", 1, 20);
        SaveProfileRecorder.ExitComponent();

        var report = SaveProfileRecorder.BuildReport();

        Assert.Equal(10, TypeSnap(report, "First").TotalBytes);
        Assert.Equal(20, TypeSnap(report, "Second").TotalBytes);
        Assert.DoesNotContain(report.Types, t => t.TypeName == SaveProfileRecorder.NestedBucket);
    }

    [Fact]
    public void ExitBelowZero_IsIgnoredSoTheDepthCannotGoNegative()
    {
        SaveProfileRecorder.Begin();
        SaveProfileRecorder.ExitComponent();
        SaveProfileRecorder.ExitComponent();

        // Still treated as outermost, not as nested.
        SaveProfileRecorder.EnterComponent();
        SaveProfileRecorder.AddComponent("Storage", 1, 10);
        SaveProfileRecorder.ExitComponent();

        Assert.Equal("Storage", Assert.Single(SaveProfileRecorder.BuildReport().Types).TypeName);
    }

    [Fact]
    public void NestingIsMeasured_SoEachPhaseKnowsWhatItRanInside()
    {
        // The real shape: root > inner > SaveManager > SaveWithoutTransform, with compression a
        // sibling of inner directly under the root.
        SaveProfileRecorder.Begin();
        SaveProfileRecorder.EnterPhase(SaveProfileReport.RootPhase);
        SaveProfileRecorder.EnterPhase("inner");
        SaveProfileRecorder.EnterPhase("SaveManager.Save");
        Phase("SaveWithoutTransform", 700);
        SaveProfileRecorder.ExitPhase("SaveManager.Save", 750);
        SaveProfileRecorder.ExitPhase("inner", 800);
        Phase("CompressContents", 150);
        SaveProfileRecorder.ExitPhase(SaveProfileReport.RootPhase, 1000);

        var report = SaveProfileRecorder.BuildReport();
        string? ParentOf(string n) => report.Phases.Single(p => p.Name == n).Parent;

        Assert.Equal(SaveProfileReport.RootPhase, ParentOf("inner"));
        Assert.Equal(SaveProfileReport.RootPhase, ParentOf("CompressContents"));
        Assert.Equal("inner", ParentOf("SaveManager.Save"));
        Assert.Equal("SaveManager.Save", ParentOf("SaveWithoutTransform"));

        // The bug the first real run exposed: subtracting every phase gives 1000 - 2400 -> clamped
        // to 0, hiding the gap. Only the direct children count.
        Assert.Equal(50, report.UnaccountedMs, 9);

        // Self time strips the nested children back out.
        Assert.Equal(50, report.SelfMs(report.Phases.Single(p => p.Name == "SaveManager.Save")), 9);
        Assert.Equal(50, report.SelfMs(report.Phases.Single(p => p.Name == "inner")), 9);
        Assert.Equal(700, report.SelfMs(report.Phases.Single(p => p.Name == "SaveWithoutTransform")), 9);
    }

    [Fact]
    public void ARecursivePhase_ParentsToItsNearestDifferentAncestorNotToItself()
    {
        // SaveLoadRoot.SaveWithoutTransform really does re-enter itself. Before this was handled,
        // the innermost call finished first and claimed itself as its parent, which made the row
        // unreachable from the root: 2,021 ms across 21,516 calls vanished from a real report, and
        // SaveManager.Save absorbed the time as though it had no children at all.
        SaveProfileRecorder.Begin();
        SaveProfileRecorder.EnterPhase(SaveProfileReport.RootPhase);
        SaveProfileRecorder.EnterPhase("SaveManager.Save");

        SaveProfileRecorder.EnterPhase("SaveWithoutTransform");
        SaveProfileRecorder.EnterPhase("SaveWithoutTransform");   // nested in itself
        SaveProfileRecorder.ExitPhase("SaveWithoutTransform", 10); // the inner one finishes first
        SaveProfileRecorder.ExitPhase("SaveWithoutTransform", 40);

        SaveProfileRecorder.ExitPhase("SaveManager.Save", 50);
        SaveProfileRecorder.ExitPhase(SaveProfileReport.RootPhase, 100);

        var report = SaveProfileRecorder.BuildReport();
        var row = report.Phases.Single(p => p.Name == "SaveWithoutTransform");

        Assert.Equal("SaveManager.Save", row.Parent);
        Assert.NotEqual("SaveWithoutTransform", row.Parent);

        // And it is reachable from the root again, which is the property that actually broke.
        Assert.Contains(row, report.DirectChildren("SaveManager.Save"));
        Assert.Contains("SaveWithoutTransform", report.ToMarkdown());
    }

    [Fact]
    public void PopPhase_ClosesAPhaseWithoutInventingACall()
    {
        SaveProfileRecorder.Begin();
        SaveProfileRecorder.EnterPhase(SaveProfileReport.RootPhase);

        SaveProfileRecorder.EnterPhase("abandoned");
        SaveProfileRecorder.PopPhase("abandoned");

        // Nothing recorded for it...
        Phase("Sim.Save", 10);
        SaveProfileRecorder.ExitPhase(SaveProfileReport.RootPhase, 100);

        var report = SaveProfileRecorder.BuildReport();
        Assert.DoesNotContain(report.Phases, p => p.Name == "abandoned");

        // ...and the stack was left balanced, so the next phase is still a child of the root.
        Assert.Equal(SaveProfileReport.RootPhase, report.Phases.Single(p => p.Name == "Sim.Save").Parent);
    }

    [Fact]
    public void UnresolvedTargets_AreRecordedOnceAndSurviveANewRecording()
    {
        SaveProfileRecorder.AddUnresolved("Sim.Save");
        SaveProfileRecorder.AddUnresolved("Sim.Save");

        // A target that failed to bind at load failed for every save this session, so Begin must
        // not clear it - the report would otherwise stop warning after the first save.
        SaveProfileRecorder.Begin();

        Assert.Contains("Sim.Save", SaveProfileRecorder.BuildReport().UnresolvedTargets);
        Assert.Single(SaveProfileRecorder.Unresolved, t => t == "Sim.Save");
    }
}
