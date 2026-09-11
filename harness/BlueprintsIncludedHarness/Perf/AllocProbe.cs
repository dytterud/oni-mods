using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine.Profiling;

namespace BlueprintsV2.Harness.Perf;

/// <summary>
/// One iteration's allocation deltas (bytes), plus the gen-0 collections that happened inside it.
/// A delta can legitimately be <b>negative</b> - a collection inside the body (see
/// <see cref="Gen0Collections"/>, which <see cref="AllocStats"/> filters on) or Unity releasing a
/// native buffer both shrink the number. <c>null</c> means the counter is unavailable on this
/// runtime, which is a different statement from a measured <c>0</c>.
/// </summary>
internal readonly struct AllocSample
{
    public AllocSample(long managedBytes, long? nativeBytes, int gen0Collections)
    {
        ManagedBytes = managedBytes;
        NativeBytes = nativeBytes;
        Gen0Collections = gen0Collections;
    }

    /// <summary><c>GC.GetTotalMemory(false)</c> delta - used managed heap.</summary>
    public long ManagedBytes { get; }

    /// <summary><c>Profiler.GetTotalAllocatedMemoryLong()</c> delta - Unity's native side, where
    /// the <c>GameObject</c>s the placement and dialog sweeps build actually live. The managed
    /// counter cannot see those at all.</summary>
    public long? NativeBytes { get; }

    public int Gen0Collections { get; }
}

/// <summary>Per-op aggregate of <see cref="AllocSample"/>s: medians over the clean samples.</summary>
internal sealed class AllocStats
{
    private AllocStats(long managedBytes, long? nativeBytes, int poisonedIterations, int totalIterations)
    {
        ManagedBytes = managedBytes;
        NativeBytes = nativeBytes;
        PoisonedIterations = poisonedIterations;
        TotalIterations = totalIterations;
    }

    public long ManagedBytes { get; }
    public long? NativeBytes { get; }

    /// <summary>How many iterations saw a gen-0 collection inside the measured body and were
    /// therefore excluded. Equal to <see cref="TotalIterations"/> means nothing could be excluded
    /// and the medians below are over the poisoned samples themselves - read them as noise. This is
    /// not hypothetical: the dialog sweep's largest cold opens poison every iteration.</summary>
    public int PoisonedIterations { get; }

    public int TotalIterations { get; }

    /// <summary>
    /// Medians (not means) of the samples with no gen-0 collection in them: heap deltas are spiky
    /// and one collection-poisoned iteration can be arbitrarily negative, which a mean would smear
    /// across the whole op. If <i>every</i> sample is poisoned, the medians are taken over all of
    /// them anyway - reporting a suspect number that <see cref="PoisonedIterations"/> flags beats
    /// reporting nothing at all.
    /// </summary>
    public static AllocStats? From(IReadOnlyList<AllocSample> samples)
    {
        if (samples.Count == 0)
            return null;

        int poisoned = samples.Count(s => s.Gen0Collections > 0);
        var clean = poisoned == samples.Count
            ? samples.ToList()
            : samples.Where(s => s.Gen0Collections == 0).ToList();

        return new AllocStats(
            Median(clean, s => s.ManagedBytes),
            clean.All(s => s.NativeBytes == null) ? null : Median(clean, s => s.NativeBytes ?? 0),
            poisoned,
            samples.Count);
    }

    /// <summary>Reuses <see cref="PerfRunner.Percentile"/> so a byte median and a millisecond
    /// median are the same statistic, computed the same way.</summary>
    private static long Median(List<AllocSample> samples, Func<AllocSample, long> field)
    {
        var values = samples.Select(s => (double)field(s)).ToList();
        values.Sort();
        return (long)PerfRunner.Percentile(values, 0.5);
    }

    public string Describe() =>
        $"managed={Kb(ManagedBytes)} native={Kb(NativeBytes)} gc0={PoisonedIterations}/{TotalIterations}";

    private static string Kb(long? bytes) => bytes == null ? "n/a" : $"{bytes.Value / 1024.0:F1}KB";
}

/// <summary>
/// Brackets one measured body with the allocation counters that actually work on ONI's runtime
/// (docs/in-game-regression-testing.md §7):
/// <list type="bullet">
/// <item><c>GC.GetTotalMemory(false)</c> - the managed heap. Coarse but live, and it scales with N
/// on every sweep.</item>
/// <item><c>Profiler.GetTotalAllocatedMemoryLong()</c> - Unity's native memory, which is where the
/// cost of the <c>GameObject</c>-building paths (<c>use</c>, the dialog's cold opens) lives; the
/// managed counter reads those as near-zero.</item>
/// </list>
/// <c>GC.GetAllocatedBytesForCurrentThread</c> is <b>not</b> sampled: it reads 0 at every N under
/// Mono's Boehm GC (measured, §7). Neither is <c>Profiler.GetMonoUsedSizeLong</c>, which returned
/// byte-for-byte the same number as <c>GC.GetTotalMemory</c> in every row of a full sweep.
///
/// <c>GC.CollectionCount(0)</c> brackets the body too: a collection inside it makes the heap delta
/// meaningless (possibly negative), so <see cref="AllocStats"/> can drop that sample. No forced
/// <c>GC.Collect</c> between iterations - that would change the heap state each body sees and make
/// the timings incomparable with §7's existing baselines. The collection count is the guard instead.
///
/// A counter that throws (a release player can strip profiler APIs rather than returning 0) is
/// disabled once, for the rest of the run, and reports <c>null</c> - one dead API must not take the
/// whole sweep down.
/// </summary>
internal readonly struct AllocProbe
{
    private static bool profilerDead;

    private readonly long managed;
    private readonly long? native;
    private readonly int gen0;

    private AllocProbe(long managed, long? native, int gen0)
    {
        this.managed = managed;
        this.native = native;
        this.gen0 = gen0;
    }

    public static AllocProbe Begin() => new(
        GC.GetTotalMemory(false),
        NativeBytes(),
        GC.CollectionCount(0));

    public AllocSample End() => new(
        GC.GetTotalMemory(false) - managed,
        NativeBytes() - native,
        GC.CollectionCount(0) - gen0);

    private static long? NativeBytes()
    {
        if (profilerDead)
            return null;
        try
        {
            return Profiler.GetTotalAllocatedMemoryLong();
        }
        catch (Exception)
        {
            profilerDead = true;
            return null;
        }
    }
}
