using Newtonsoft.Json.Linq;
using SaveProfiler.Recording;
using Xunit;

namespace SaveProfiler.Tests.Recording;

/// <summary>
/// Locks what a report claims, which matters more than it sounds: these strings are the whole
/// output of the mod, and a reader acts on them.
///
/// Two of these are about refusing to mislead rather than about arithmetic — that the nested
/// phases are never presented as a partition of the total, and that a target which failed to bind
/// is named instead of quietly reading zero. docs/perf-method.md is explicit that a hotspot
/// reading zero calls looks identical to a code path that never ran, and that four published
/// conclusions had to be retracted for believing something plausible instead of measuring it.
///
/// Ungated: <see cref="SaveProfileReport"/> is pure BCL plus Newtonsoft, deliberately.
/// </summary>
public class SaveProfileReportTests
{
    private static SampleAccumulator.Snapshot Snap(double totalMs, long calls = 1, long bytes = 0) =>
        new(calls, totalMs, bytes, totalMs, totalMs, totalMs, medianTruncated: false);

    private static SaveProfileReport WithPhases(double rootMs, params (string Name, double Ms)[] children)
    {
        var report = new SaveProfileReport
        {
            Root = new SaveProfileReport.PhaseRow(SaveProfileReport.RootPhase, null, Snap(rootMs)),
        };
        foreach (var (name, ms) in children)
            report.Phases.Add(new SaveProfileReport.PhaseRow(name, SaveProfileReport.RootPhase, Snap(ms)));
        return report;
    }

    [Fact]
    public void UnaccountedTime_IsTheRootMinusTheMeasuredChildren()
    {
        var report = WithPhases(1000, ("Sim.Save", 200), ("SaveManager.Save", 500));

        Assert.Equal(1000, report.TotalMs);
        Assert.Equal(300, report.UnaccountedMs, 9);
    }

    [Fact]
    public void ChildrenAreNested_SoAFullyAccountedSaveLeavesNothingOver()
    {
        var report = WithPhases(1000, ("Sim.Save", 400), ("SaveManager.Save", 600));

        Assert.Equal(0, report.UnaccountedMs, 9);
    }

    [Fact]
    public void OverlappingChildren_NeverProduceNegativeUnaccountedTime()
    {
        // Children can legitimately exceed the root: SaveLoader.Save(BinaryWriter) nests inside
        // the root overload, so its time is counted in both. A negative remainder in a report
        // would read as a measurement error rather than as nesting.
        var report = WithPhases(1000, ("SaveLoader.Save (inner)", 900), ("SaveManager.Save", 800));

        Assert.Equal(0, report.UnaccountedMs);
    }

    [Fact]
    public void PercentOfTotal_IsZeroWhenNothingWasMeasured()
    {
        // The root patch failing to bind leaves TotalMs at zero; dividing by it would render NaN
        // into every row of the table.
        var report = new SaveProfileReport();

        Assert.Equal(0, report.TotalMs);
        Assert.Equal(0, report.PercentOfTotal(50));
        Assert.Equal(0, report.CompressionRatio);
    }

    [Fact]
    public void PercentOfTotal_IsRelativeToTheRoot()
    {
        var report = WithPhases(200, ("Sim.Save", 50));

        Assert.Equal(25.0, report.PercentOfTotal(50), 9);
    }

    [Fact]
    public void Markdown_ShowsTheUnaccountedRowRatherThanNormalisingItAway()
    {
        string md = WithPhases(1000, ("Sim.Save", 200)).ToMarkdown();

        Assert.Contains("unaccounted", md);
        Assert.Contains("800.0", md);
        Assert.Contains("the totals column deliberately sums to more than the save", md);
        // The column a reader should act on.
        Assert.Contains("Self ms", md);
    }

    [Fact]
    public void Markdown_NamesEveryTargetThatFailedToBind()
    {
        var report = WithPhases(100);
        report.UnresolvedTargets.Add("Sim.Save");

        string md = report.ToMarkdown();

        Assert.Contains("did not resolve", md);
        Assert.Contains("Sim.Save", md);
        Assert.Contains("**not** because the code", md);
    }

    [Fact]
    public void Markdown_HasNoUnresolvedSectionWhenEverythingBound()
    {
        Assert.DoesNotContain("did not resolve", WithPhases(100).ToMarkdown());
    }

    [Fact]
    public void Markdown_WarnsWhenAttributionWasOnThatTheRunIsNotComparable()
    {
        var report = WithPhases(100);
        report.ComponentAttribution = true;

        string md = report.ToMarkdown();

        Assert.Contains("Component attribution was ON", md);
        Assert.Contains("Do not compare", md);
    }

    [Fact]
    public void Markdown_OmitsThatWarningWhenAttributionWasOff()
    {
        Assert.DoesNotContain("Do not compare", WithPhases(100).ToMarkdown());
    }

    [Fact]
    public void Markdown_SaysSoWhenAttributionWasOnButNothingBound()
    {
        var report = WithPhases(100);
        report.ComponentAttribution = true;

        Assert.Contains("the per-component target did not bind", report.ToMarkdown());
    }

    [Fact]
    public void Markdown_OrdersComponentTypesByCost()
    {
        var report = WithPhases(100);
        report.Types.Add(new SaveProfileReport.TypeRow("Cheap", Snap(1)));
        report.Types.Add(new SaveProfileReport.TypeRow("Expensive", Snap(50)));

        string md = report.ToMarkdown();

        Assert.True(md.IndexOf("Expensive", StringComparison.Ordinal)
                  < md.IndexOf("Cheap", StringComparison.Ordinal));
    }

    [Fact]
    public void Json_CarriesTheSameFactsAsTheMarkdown()
    {
        var report = WithPhases(1000, ("Sim.Save", 200));
        report.ColonyName = "Sandbox";
        report.IsAutoSave = true;
        report.UncompressedBytes = 2048;
        report.CompressedBytes = 1024;
        report.UnresolvedTargets.Add("Game.Save");
        report.Types.Add(new SaveProfileReport.TypeRow("PrimaryElement", Snap(7, calls: 3, bytes: 90)));

        var json = JObject.Parse(report.ToJson());

        Assert.Equal("Sandbox", (string?)json["colony"]);
        Assert.True((bool)json["isAutoSave"]!);
        Assert.Equal(1000.0, (double)json["totalMs"]!);
        Assert.Equal(800.0, (double)json["unaccountedMs"]!);
        Assert.Equal(2048, (long)json["uncompressedBytes"]!);
        Assert.Equal("Game.Save", (string?)json["unresolvedTargets"]![0]);
        Assert.Equal("Sim.Save", (string?)json["phases"]![0]!["name"]);
        Assert.Equal("PrimaryElement", (string?)json["types"]![0]!["name"]);
        Assert.Equal(3, (long)json["types"]![0]!["calls"]!);
        Assert.Equal(90, (long)json["types"]![0]!["totalBytes"]!);
    }

    [Fact]
    public void CompressionRatio_IsCompressedOverUncompressed()
    {
        var report = WithPhases(100);
        report.UncompressedBytes = 1000;
        report.CompressedBytes = 250;

        Assert.Equal(0.25, report.CompressionRatio, 9);
    }
}
