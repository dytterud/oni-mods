using SaveProfiler.Recording;
using Xunit;

namespace SaveProfiler.Tests.Recording;

/// <summary>
/// Locks which reports get deleted. Takes plain strings precisely so this decision is reachable
/// without a filesystem — the directory listing and the deleting stay in <c>ReportWriter</c>,
/// which needs the game.
/// </summary>
public class ReportRotationTests
{
    private static readonly string[] NewestFirst = ["e", "d", "c", "b", "a"];

    [Fact]
    public void FewerThanTheLimit_DeletesNothing()
    {
        Assert.Empty(ReportRotation.ToDelete(["a", "b"], keep: 5));
    }

    [Fact]
    public void ExactlyTheLimit_DeletesNothing()
    {
        Assert.Empty(ReportRotation.ToDelete(NewestFirst, keep: 5));
    }

    [Fact]
    public void OverTheLimit_DeletesTheOldestAndKeepsExactlyTheLimit()
    {
        var doomed = ReportRotation.ToDelete(NewestFirst, keep: 2);

        Assert.Equal(["c", "b", "a"], doomed);
    }

    [Fact]
    public void KeepOne_LeavesOnlyTheNewest()
    {
        Assert.Equal(["d", "c", "b", "a"], ReportRotation.ToDelete(NewestFirst, keep: 1));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ZeroOrNegative_KeepsEverything(int keep)
    {
        // A config typo should not silently erase the reports someone was collecting, so a
        // non-positive limit means "keep them all" rather than "keep none".
        Assert.Empty(ReportRotation.ToDelete(NewestFirst, keep));
    }

    [Fact]
    public void EmptyInput_IsNotAnError()
    {
        Assert.Empty(ReportRotation.ToDelete([], keep: 3));
    }
}
