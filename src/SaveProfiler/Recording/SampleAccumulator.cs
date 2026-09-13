namespace SaveProfiler.Recording;

/// <summary>
/// Call count, elapsed time and byte totals for one measured thing - a save phase, or every
/// component of one type.
///
/// Pure BCL on purpose, like <c>BlueprintPaths</c> in the sibling mod: the committed
/// <c>./lib</c> assemblies are reference-only and the CLR refuses to load them for execution, so
/// any type that touches Klei or Unity cannot be exercised by a unit test. Keeping the arithmetic
/// here - and keeping the Harmony shims free of arithmetic - is what makes the numbers testable
/// offline.
///
/// Thread-safe because <c>SaveLoader.CompressContents</c> is not guaranteed to run on the main
/// thread: another mod (Fast Save's Background Save, for one) may move it, and a torn read there
/// would be a silent wrong number rather than a crash.
/// </summary>
public sealed class SampleAccumulator
{
    /// <summary>
    /// How many individual samples to keep for the median. Phases are called once or twice per
    /// save; component types are called hundreds of thousands of times, and retaining every one
    /// would cost more memory than the thing being measured. Past the cap the count, total, min
    /// and max stay exact and only the median stops being available.
    /// </summary>
    public const int DefaultRetainedSamples = 512;

    private readonly object gate = new();
    private readonly List<double> retained = [];
    private readonly int retainLimit;

    private long calls;
    private double totalMs;
    private long totalBytes;
    private double minMs = double.MaxValue;
    private double maxMs = double.MinValue;
    private bool overflowed;

    public SampleAccumulator(int retainLimit = DefaultRetainedSamples) => this.retainLimit = retainLimit;

    /// <summary>Records one call. <paramref name="bytes"/> is a stream-position delta, so it is
    /// never negative; an unmeasured byte count is passed as 0 and the caller says so separately.</summary>
    public void Add(double elapsedMs, long bytes)
    {
        lock (gate)
        {
            calls++;
            totalMs += elapsedMs;
            totalBytes += bytes;

            if (elapsedMs < minMs) minMs = elapsedMs;
            if (elapsedMs > maxMs) maxMs = elapsedMs;

            if (retained.Count < retainLimit)
                retained.Add(elapsedMs);
            else
                overflowed = true;
        }
    }

    public Snapshot Snap()
    {
        lock (gate)
        {
            return new Snapshot(
                calls,
                totalMs,
                totalBytes,
                calls == 0 ? 0 : minMs,
                calls == 0 ? 0 : maxMs,
                Median(retained),
                // A median over a truncated sample is a median of the first N calls, not of the
                // run - which for a save is the first N objects, not a random draw. Say so rather
                // than publishing a number whose meaning quietly changed.
                overflowed);
        }
    }

    /// <summary>Median of <paramref name="values"/>, or 0 when there are none. Sorts a copy -
    /// the caller's list is live under the lock and callers read it again.</summary>
    internal static double Median(IReadOnlyList<double> values)
    {
        if (values.Count == 0)
            return 0;

        var sorted = values.ToList();
        sorted.Sort();

        int mid = sorted.Count / 2;
        return sorted.Count % 2 == 1
            ? sorted[mid]
            : (sorted[mid - 1] + sorted[mid]) / 2;
    }

    public readonly struct Snapshot
    {
        public Snapshot(long calls, double totalMs, long totalBytes, double minMs, double maxMs,
            double medianMs, bool medianTruncated)
        {
            Calls = calls;
            TotalMs = totalMs;
            TotalBytes = totalBytes;
            MinMs = minMs;
            MaxMs = maxMs;
            MedianMs = medianMs;
            MedianTruncated = medianTruncated;
        }

        public long Calls { get; }
        public double TotalMs { get; }
        public long TotalBytes { get; }
        public double MinMs { get; }
        public double MaxMs { get; }
        public double MedianMs { get; }

        /// <summary>The median covers only the first <see cref="DefaultRetainedSamples"/> calls.</summary>
        public bool MedianTruncated { get; }

        public double AvgUsPerCall => Calls == 0 ? 0 : TotalMs * 1000.0 / Calls;
        public double AvgBytesPerCall => Calls == 0 ? 0 : (double)TotalBytes / Calls;
    }
}
