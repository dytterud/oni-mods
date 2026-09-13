using System.Globalization;
using System.Text;
using Newtonsoft.Json.Linq;

namespace SaveProfiler.Recording;

/// <summary>
/// One completed measurement of one save, and its rendering to Markdown and JSON.
///
/// Pure BCL and Newtonsoft only - no Klei, no Unity - so the arithmetic and the wording are
/// reachable from the offline unit tests. Everything that has to ask the game a question
/// (the save path, the mod list, whether this was an autosave) is passed in as data.
///
/// The phases form a <em>tree</em>, not a partition, and each one records the phase it ran inside
/// (measured at runtime, not declared here). Totals therefore double-count down the tree, so the
/// two numbers that mean anything are:
///
/// <list type="bullet">
/// <item><see cref="SelfMs"/> — a phase's own time, with its direct children subtracted.</item>
/// <item><see cref="UnaccountedMs"/> — the root's time minus its <em>direct</em> children only.</item>
/// </list>
///
/// The first real run of this profiler got that wrong: <see cref="UnaccountedMs"/> subtracted every
/// phase rather than the direct children, the sum exceeded the total, the clamp reported 0.0 ms,
/// and a genuine 1,092 ms — 28% of the save, more than compression cost — was hidden behind a
/// zero. docs/perf-method.md: "when the parts do not add up to the whole, something is unaccounted
/// for, and it is occasionally the larger half."
/// </summary>
public sealed class SaveProfileReport
{
    /// <summary>The phase that wraps the whole operation. Everything else nests inside it.</summary>
    public const string RootPhase = "SaveLoader.Save";

    /// <summary><paramref name="Parent"/> is the phase this one ran inside, or null for the root
    /// and for anything that began on a thread with no phase open.</summary>
    public sealed record PhaseRow(string Name, string? Parent, SampleAccumulator.Snapshot Snap);

    public sealed record TypeRow(string TypeName, SampleAccumulator.Snapshot Snap);

    /// <summary><c>System.DateTime</c> spelled out: Assembly-CSharp declares its own
    /// <c>DateTime</c> in the global namespace, which wins over the implicit <c>using System</c>
    /// and has neither <c>UtcNow</c> nor a format-provider <c>ToString</c>.</summary>
    public System.DateTime TimestampUtc { get; set; } = System.DateTime.UtcNow;
    public string SavePath { get; set; } = "";
    public string ColonyName { get; set; } = "";
    public bool IsAutoSave { get; set; }
    public string GameVersion { get; set; } = "";

    /// <summary>Per-component attribution was on for this run. Reports with it on and off are
    /// not comparable and the rendering says so.</summary>
    public bool ComponentAttribution { get; set; }

    public long UncompressedBytes { get; set; }
    public long CompressedBytes { get; set; }

    public PhaseRow? Root { get; set; }
    public List<PhaseRow> Phases { get; } = [];
    public List<TypeRow> Types { get; } = [];
    public List<string> ActiveMods { get; } = [];

    /// <summary>Patch targets that could not be bound. These are exactly the rows that will read
    /// zero for a reason that has nothing to do with the code under test, so they are named
    /// loudly rather than left to look like a path that never ran.</summary>
    public List<string> UnresolvedTargets { get; } = [];

    public double TotalMs => Root?.Snap.TotalMs ?? 0;

    /// <summary>The phases that ran directly inside <paramref name="name"/>, not the whole subtree.</summary>
    public IEnumerable<PhaseRow> DirectChildren(string? name) =>
        Phases.Where(p => string.Equals(p.Parent, name, StringComparison.Ordinal));

    /// <summary>
    /// A phase's own time: its total minus the phases that ran directly inside it. This is the
    /// column to read — a parent's total is mostly its children's.
    /// </summary>
    public double SelfMs(PhaseRow phase) =>
        Math.Max(0, phase.Snap.TotalMs - DirectChildren(phase.Name).Sum(c => c.Snap.TotalMs));

    /// <summary>
    /// The root's time minus its <b>direct</b> children only — the part of the save no phase
    /// accounts for. Subtracting every phase instead would double-count the tree; see the type
    /// remarks for what that cost the first real run.
    /// </summary>
    public double UnaccountedMs =>
        Math.Max(0, TotalMs - DirectChildren(RootPhase).Sum(p => p.Snap.TotalMs));

    public double PercentOfTotal(double ms) => TotalMs <= 0 ? 0 : ms / TotalMs * 100.0;

    public double CompressionRatio =>
        UncompressedBytes <= 0 ? 0 : (double)CompressedBytes / UncompressedBytes;

    // ---------------------------------------------------------------- Markdown

    public string ToMarkdown()
    {
        var sb = new StringBuilder();
        var inv = CultureInfo.InvariantCulture;

        sb.Append("# Save profile — ").Append(IsAutoSave ? "autosave" : "manual save").AppendLine();
        sb.AppendLine();
        sb.Append("- **When (UTC):** ").AppendLine(TimestampUtc.ToString("yyyy-MM-dd HH:mm:ss", inv));
        sb.Append("- **Colony:** ").AppendLine(Empty(ColonyName));
        sb.Append("- **Save file:** ").AppendLine(Empty(SavePath));
        sb.Append("- **Game version:** ").AppendLine(Empty(GameVersion));
        sb.Append("- **Total:** ").Append(Ms(TotalMs)).AppendLine(" ms");

        if (UncompressedBytes > 0)
        {
            sb.Append("- **Size:** ").Append(Mb(UncompressedBytes)).Append(" MB uncompressed");
            if (CompressedBytes > 0)
                sb.Append(" → ").Append(Mb(CompressedBytes)).Append(" MB compressed (")
                  .Append((CompressionRatio * 100).ToString("F1", inv)).Append("%)");
            sb.AppendLine();
        }

        sb.Append("- **Component attribution:** ").AppendLine(ComponentAttribution ? "ON" : "off");
        sb.AppendLine();

        AppendCaveats(sb);
        AppendUnresolved(sb);
        AppendPhases(sb);
        AppendTypes(sb);
        AppendMods(sb);

        return sb.ToString();
    }

    private void AppendCaveats(StringBuilder sb)
    {
        sb.AppendLine("> These numbers are from one run on one machine, with this mod list. There is");
        sb.AppendLine("> no noise floor here to compare against, so treat a single figure as an order of");
        sb.AppendLine("> magnitude, not a measurement you can difference against another run.");

        if (ComponentAttribution)
        {
            sb.AppendLine(">");
            sb.AppendLine("> **Component attribution was ON.** Every component serialization carries a Harmony");
            sb.AppendLine("> wrapper in this run, which costs time inside the very totals above. Do not compare");
            sb.AppendLine("> this report's phase timings against a report recorded with attribution off.");
        }

        sb.AppendLine();
    }

    private void AppendUnresolved(StringBuilder sb)
    {
        if (UnresolvedTargets.Count == 0)
            return;

        sb.Append("## ⚠ ").Append(UnresolvedTargets.Count).AppendLine(" patch target(s) did not resolve");
        sb.AppendLine();
        sb.AppendLine("These rows read zero because the method could not be found, **not** because the code");
        sb.AppendLine("never ran. Any total below is missing their time.");
        sb.AppendLine();
        foreach (string target in UnresolvedTargets)
            sb.Append("- `").Append(target).AppendLine("`");
        sb.AppendLine();
    }

    private void AppendPhases(StringBuilder sb)
    {
        sb.AppendLine("## Phases");
        sb.AppendLine();
        sb.AppendLine("A tree, not a partition: each phase's **total** includes everything nested inside it,");
        sb.AppendLine("so the totals column deliberately sums to more than the save. **Self** is the phase's");
        sb.AppendLine("own time with its direct children subtracted — that is the column to read.");
        sb.AppendLine();
        sb.AppendLine("| Phase | Calls | Total ms | Self ms | Self % |");
        sb.AppendLine("|---|---:|---:|---:|---:|");

        AppendPhaseTree(sb, RootPhase, depth: 0);

        sb.Append("| ").Append(Indent(1)).Append("*unaccounted* | | | ").Append(Ms(UnaccountedMs))
          .Append(" | ").Append(Pct(PercentOfTotal(UnaccountedMs))).AppendLine(" |");
        sb.Append("| **total** | | | **").Append(Ms(TotalMs)).AppendLine("** | **100.0** |");
        sb.AppendLine();

        // Anything whose parent never appeared - a phase that began on a thread with no phase open,
        // which is what a mod moving work off the main thread looks like from in here.
        var orphans = Phases
            .Where(p => p.Parent != null && p.Parent != RootPhase && Phases.All(q => q.Name != p.Parent))
            .ToList();

        if (orphans.Count > 0)
        {
            sb.AppendLine("Recorded under a phase that is not in this tree, so they are not subtracted");
            sb.AppendLine("from anything above:");
            sb.AppendLine();
            foreach (var orphan in orphans)
                sb.Append("- `").Append(orphan.Name).Append("` (inside `").Append(orphan.Parent).AppendLine("`)");
            sb.AppendLine();
        }
    }

    private void AppendPhaseTree(StringBuilder sb, string? parent, int depth)
    {
        foreach (var phase in DirectChildren(parent).OrderByDescending(p => p.Snap.TotalMs))
        {
            double self = SelfMs(phase);
            sb.Append("| ").Append(Indent(depth + 1)).Append('`').Append(phase.Name).Append("` | ")
              .Append(phase.Snap.Calls)
              .Append(" | ").Append(Ms(phase.Snap.TotalMs))
              .Append(" | ").Append(Ms(self))
              .Append(" | ").Append(Pct(PercentOfTotal(self)))
              .AppendLine(" |");

            AppendPhaseTree(sb, phase.Name, depth + 1);
        }
    }

    private static string Indent(int depth) =>
        depth <= 0 ? "" : string.Concat(Enumerable.Repeat("&nbsp;&nbsp;", depth)) + "↳ ";

    private void AppendTypes(StringBuilder sb)
    {
        if (Types.Count == 0)
        {
            if (ComponentAttribution)
            {
                sb.AppendLine("## Component types");
                sb.AppendLine();
                sb.AppendLine("Attribution was on but nothing was recorded — the per-component target did not bind.");
                sb.AppendLine();
            }
            return;
        }

        sb.Append("## Component types (").Append(Types.Count).AppendLine(" seen)");
        sb.AppendLine();
        sb.AppendLine("| Type | Calls | Total ms | % of save | µs/call | Bytes | Bytes/call |");
        sb.AppendLine("|---|---:|---:|---:|---:|---:|---:|");

        foreach (var row in Types.OrderByDescending(t => t.Snap.TotalMs))
        {
            var s = row.Snap;
            sb.Append("| `").Append(row.TypeName).Append("` | ").Append(s.Calls)
              .Append(" | ").Append(Ms(s.TotalMs))
              .Append(" | ").Append(Pct(PercentOfTotal(s.TotalMs)))
              .Append(" | ").Append(s.AvgUsPerCall.ToString("F2", CultureInfo.InvariantCulture))
              .Append(" | ").Append(s.TotalBytes)
              .Append(" | ").Append(s.AvgBytesPerCall.ToString("F0", CultureInfo.InvariantCulture))
              .AppendLine(" |");
        }

        sb.AppendLine();
    }

    private void AppendMods(StringBuilder sb)
    {
        if (ActiveMods.Count == 0)
            return;

        sb.Append("## Active mods (").Append(ActiveMods.Count).AppendLine(")");
        sb.AppendLine();
        sb.AppendLine("Another mod patching the save path changes what was measured. Fast Save in particular");
        sb.AppendLine("replaces the serialization this report attributes.");
        sb.AppendLine();
        foreach (string mod in ActiveMods)
            sb.Append("- ").AppendLine(mod);
        sb.AppendLine();
    }

    // ---------------------------------------------------------------- JSON

    /// <summary>The machine-readable half. Same fields as the Markdown, shaped like the harness's
    /// <c>perf.json</c> so the two can be read by the same eye.</summary>
    public string ToJson()
    {
        var phases = new JArray();
        foreach (var phase in Phases.OrderByDescending(p => p.Snap.TotalMs))
        {
            JObject row = SnapJson(phase.Name, phase.Snap);
            row["parent"] = phase.Parent;
            row["selfMs"] = SelfMs(phase);
            phases.Add(row);
        }

        var types = new JArray();
        foreach (var row in Types.OrderByDescending(t => t.Snap.TotalMs))
            types.Add(SnapJson(row.TypeName, row.Snap));

        var root = new JObject
        {
            ["timestampUtc"] = TimestampUtc.ToString("o", CultureInfo.InvariantCulture),
            ["colony"] = ColonyName,
            ["savePath"] = SavePath,
            ["isAutoSave"] = IsAutoSave,
            ["gameVersion"] = GameVersion,
            ["componentAttribution"] = ComponentAttribution,
            ["totalMs"] = TotalMs,
            ["unaccountedMs"] = UnaccountedMs,
            ["uncompressedBytes"] = UncompressedBytes,
            ["compressedBytes"] = CompressedBytes,
            ["phases"] = phases,
            ["types"] = types,
            ["unresolvedTargets"] = new JArray(UnresolvedTargets),
            ["activeMods"] = new JArray(ActiveMods),
        };

        return root.ToString();
    }

    private static JObject SnapJson(string name, SampleAccumulator.Snapshot s) => new()
    {
        ["name"] = name,
        ["calls"] = s.Calls,
        ["totalMs"] = s.TotalMs,
        ["medianMs"] = s.MedianMs,
        ["medianTruncated"] = s.MedianTruncated,
        ["minMs"] = s.MinMs,
        ["maxMs"] = s.MaxMs,
        ["totalBytes"] = s.TotalBytes,
    };

    // ---------------------------------------------------------------- formatting

    private static string Empty(string value) => string.IsNullOrEmpty(value) ? "(unknown)" : value;

    private static string Ms(double value) => value.ToString("F1", CultureInfo.InvariantCulture);

    private static string Pct(double value) => value.ToString("F1", CultureInfo.InvariantCulture);

    private static string Mb(long bytes) =>
        (bytes / (1024.0 * 1024.0)).ToString("F1", CultureInfo.InvariantCulture);
}
