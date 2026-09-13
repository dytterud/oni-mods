using System.Collections;
using System.IO;
using System.Text;
using BlueprintsV2.BlueprintData;
using UtilLibs;

namespace BlueprintsV2.Harness.Perf;

/// <summary>
/// The export half of the clipboard round trip, and the decompress step that begins the paste
/// half - the counterparts to <see cref="PerfRunner"/>'s <c>deserialize</c> / <c>full-import</c>.
///
/// <para><c>ModAssets.ExportToClipboard</c> is two steps: <c>Blueprint.WriteJsonString</c> then
/// <c>CompressString</c> (gzip + base64). Both are timed separately as well as together, because
/// they scale differently - serialization is linear in building count, compression in the
/// resulting character count - and a combined number alone would not say which one to attack.
/// <c>IO_Utils.PutToClipboard</c> is deliberately excluded: it is Unity/OS cost this mod cannot
/// change, and it is the same boundary <c>full-import</c> already draws on the import side.</para>
///
/// <para>Sizes come from <see cref="PerfRunner.Sizes"/> so <c>serialize</c> reads directly against
/// <c>deserialize</c> at the same N.</para>
/// </summary>
internal static class ExportPerf
{
    // Serialization and compression are pure CPU over in-memory data - no GameObjects, no Grid, no
    // frame-straddling work - so they tolerate far more iterations than the placement sweeps.
    private const int Warmup = 3, Iterations = 10;

    public static IEnumerator Run(PerfReport report, HarnessLog log)
    {
        foreach (int n in PerfRunner.Sizes)
        {
            var bp = SyntheticBlueprint.Build(n);
            string json = Serialize(bp);
            string compressed = json.CompressString();
            log.Line($"export N={n}: json {json.Length:N0} chars -> compressed {compressed.Length:N0} chars " +
                     $"({(double)compressed.Length / json.Length:P0} of original)");

            // Fidelity before speed: a timing for a step that silently drops data is worse than no
            // timing at all. DecompressString swallows its exceptions and returns string.Empty, so
            // a failure here would otherwise surface only as a suspiciously fast decompress.
            VerifyRoundTrip(log, n, json, compressed);

            yield return PerfRunner.TimeOp(report, log, "serialize", n, Warmup, Iterations,
                () => { _ = Serialize(bp); });

            yield return PerfRunner.TimeOp(report, log, "compress", n, Warmup, Iterations,
                () => { _ = json.CompressString(); });

            // The whole ExportToClipboard body bar the clipboard write. Timed as well as its two
            // halves so the sum can be checked against the whole - if they diverge, something
            // between them (the intermediate string, GC pressure) is costing more than either step.
            yield return PerfRunner.TimeOp(report, log, "export", n, Warmup, Iterations,
                () => { _ = Serialize(bp).CompressString(); });

            yield return PerfRunner.TimeOp(report, log, "decompress", n, Warmup, Iterations,
                () => { _ = compressed.DecompressString(); });
        }
    }

    /// <summary>Mirrors <c>ModAssets.ExportToClipboard</c>'s serialization step. That method reuses
    /// one shared <c>StringBuilder</c>; a fresh one per call is used here so the timing includes the
    /// buffer growth a first export pays rather than measuring an already-warm buffer.</summary>
    private static string Serialize(Blueprint bp)
    {
        var sb = new StringBuilder();
        using (var sw = new StringWriter(sb))
            bp.WriteJsonString(sw);
        return sb.ToString();
    }

    private static void VerifyRoundTrip(HarnessLog log, int n, string json, string compressed)
    {
        string roundTripped = compressed.DecompressString();
        if (roundTripped == json)
            return;

        log.Line($"  !! export-N{n}: compress/decompress round trip did NOT return the original " +
                 $"({json.Length:N0} chars in, {roundTripped.Length:N0} out) - the decompress timing " +
                 "below is measuring a path that loses data");
    }
}
