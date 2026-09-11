using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace BlueprintsV2.Harness.Perf;

/// <summary>Collects <see cref="PerfRunner"/> results before they're written to disk.</summary>
internal sealed class PerfReport
{
    public sealed record OpResult(string Op, int N, int Iterations, double MedianMs, double P95Ms, AllocStats? Alloc);

    public List<OpResult> Results { get; } = new();

    public void Add(string op, int n, int iterations, double medianMs, double p95Ms, AllocStats? alloc) =>
        Results.Add(new OpResult(op, n, iterations, medianMs, p95Ms, alloc));
}

/// <summary>Writes <c>perf.json</c>: the per-operation size sweep plus the
/// <see cref="PerfInstrumentation"/> hotspot totals (docs/in-game-regression-testing.md §7).</summary>
internal static class PerfWriter
{
    public static void Write(string path, PerfReport report,
        IReadOnlyList<(string Name, PerfInstrumentation.Accumulator.Snapshot Snap)> hotspotSnapshots)
    {
        var operations = new JArray();
        foreach (var opGroup in report.Results.GroupBy(r => r.Op))
        {
            var results = new JArray();
            foreach (var r in opGroup.OrderBy(r => r.N))
            {
                var result = new JObject
                {
                    ["n"] = r.N,
                    ["iterations"] = r.Iterations,
                    ["medianMs"] = r.MedianMs,
                    ["p95Ms"] = r.P95Ms,
                };
                // Omitted entirely when the op didn't sample (a caller driving its own loop), so
                // "not measured" is distinguishable from "measured zero" - which is the whole
                // question this counter set exists to answer.
                if (r.Alloc != null)
                    result["alloc"] = AllocJson(r.Alloc);
                results.Add(result);
            }
            operations.Add(new JObject { ["name"] = opGroup.Key, ["results"] = results });
        }

        var hotspots = new JArray();
        foreach (var (name, snap) in hotspotSnapshots)
            hotspots.Add(HotspotJson(name, snap));

        var root = new JObject { ["operations"] = operations, ["hotspots"] = hotspots };
        System.IO.File.WriteAllText(path, root.ToString());
    }

    /// <summary>Medians of the per-iteration deltas; a <c>null</c> byte field means the counter
    /// isn't available on this runtime (docs §7), not that nothing was allocated.</summary>
    private static JObject AllocJson(AllocStats a) => new()
    {
        ["managedBytes"] = a.ManagedBytes,
        ["nativeBytes"] = a.NativeBytes,
        ["gen0Poisoned"] = a.PoisonedIterations,
        ["iterations"] = a.TotalIterations,
    };

    private static JObject HotspotJson(string name, PerfInstrumentation.Accumulator.Snapshot s) => new()
    {
        ["name"] = name,
        ["totalCalls"] = s.Calls,
        ["totalMs"] = s.TotalMs,
        ["avgUsPerCall"] = s.AvgUsPerCall,
        ["totalBytes"] = s.TotalBytes,
        ["avgBytesPerCall"] = s.AvgBytesPerCall,
    };
}
