using SaveProfiler.Recording;
using Xunit;

namespace SaveProfiler.Tests.Recording;

/// <summary>
/// Locks the arithmetic every number in a report is derived from.
///
/// Ungated: <see cref="SampleAccumulator"/> is pure BCL with no Klei or Unity types, which is the
/// whole reason it lives apart from the Harmony shims. A wrong median here would be a wrong number
/// in a report that someone then makes an optimisation decision on, and nothing about the game is
/// needed to catch it.
/// </summary>
public class SampleAccumulatorTests
{
    [Fact]
    public void NoSamples_ReportsZeroesRatherThanSentinels()
    {
        var snap = new SampleAccumulator().Snap();

        Assert.Equal(0, snap.Calls);
        Assert.Equal(0, snap.TotalMs);
        Assert.Equal(0, snap.TotalBytes);
        // Not double.MaxValue / double.MinValue, which is what the running min and max start at.
        Assert.Equal(0, snap.MinMs);
        Assert.Equal(0, snap.MaxMs);
        Assert.Equal(0, snap.MedianMs);
        Assert.Equal(0, snap.AvgUsPerCall);
        Assert.Equal(0, snap.AvgBytesPerCall);
    }

    [Fact]
    public void OneSample_IsItsOwnMedianMinAndMax()
    {
        var acc = new SampleAccumulator();
        acc.Add(4.0, 100);

        var snap = acc.Snap();

        Assert.Equal(1, snap.Calls);
        Assert.Equal(4.0, snap.MedianMs);
        Assert.Equal(4.0, snap.MinMs);
        Assert.Equal(4.0, snap.MaxMs);
        Assert.Equal(100, snap.TotalBytes);
    }

    [Fact]
    public void OddCount_MedianIsTheMiddleValue()
    {
        var acc = new SampleAccumulator();
        // Deliberately out of order: the accumulator must sort, not assume arrival order.
        foreach (double ms in new[] { 5.0, 1.0, 3.0 })
            acc.Add(ms, 0);

        Assert.Equal(3.0, acc.Snap().MedianMs);
    }

    [Fact]
    public void EvenCount_MedianIsTheMeanOfTheMiddleTwo()
    {
        var acc = new SampleAccumulator();
        foreach (double ms in new[] { 8.0, 2.0, 4.0, 6.0 })
            acc.Add(ms, 0);

        Assert.Equal(5.0, acc.Snap().MedianMs);
    }

    [Fact]
    public void TotalsAndAverages_AreExactAcrossManyCalls()
    {
        var acc = new SampleAccumulator();
        for (int i = 0; i < 10; i++)
            acc.Add(2.0, 50);

        var snap = acc.Snap();

        Assert.Equal(10, snap.Calls);
        Assert.Equal(20.0, snap.TotalMs, 9);
        Assert.Equal(500, snap.TotalBytes);
        Assert.Equal(2000.0, snap.AvgUsPerCall, 9);
        Assert.Equal(50.0, snap.AvgBytesPerCall, 9);
    }

    [Fact]
    public void PastTheRetainLimit_CountsStayExactAndTheMedianAdmitsItIsTruncated()
    {
        var acc = new SampleAccumulator(retainLimit: 3);
        foreach (double ms in new[] { 1.0, 1.0, 1.0, 99.0, 99.0 })
            acc.Add(ms, 10);

        var snap = acc.Snap();

        // The cheap aggregates never degrade...
        Assert.Equal(5, snap.Calls);
        Assert.Equal(201.0, snap.TotalMs, 9);
        Assert.Equal(50, snap.TotalBytes);
        Assert.Equal(1.0, snap.MinMs);
        Assert.Equal(99.0, snap.MaxMs);

        // ...but the median now covers only the first three samples, and says so rather than
        // passing itself off as a median of the run.
        Assert.True(snap.MedianTruncated);
        Assert.Equal(1.0, snap.MedianMs);
    }

    [Fact]
    public void WithinTheRetainLimit_TheMedianIsNotFlaggedTruncated()
    {
        var acc = new SampleAccumulator(retainLimit: 10);
        acc.Add(1.0, 0);
        acc.Add(3.0, 0);

        Assert.False(acc.Snap().MedianTruncated);
    }

    [Fact]
    public void ZeroRetainLimit_KeepsTotalsButOffersNoMedian()
    {
        // This is how per-component accumulators are built: hundreds of thousands of calls, where
        // retaining every sample would cost more memory than the thing being measured.
        var acc = new SampleAccumulator(retainLimit: 0);
        acc.Add(1.0, 7);
        acc.Add(3.0, 7);

        var snap = acc.Snap();

        Assert.Equal(2, snap.Calls);
        Assert.Equal(4.0, snap.TotalMs, 9);
        Assert.Equal(14, snap.TotalBytes);
        Assert.Equal(0, snap.MedianMs);
        Assert.True(snap.MedianTruncated);
    }
}
