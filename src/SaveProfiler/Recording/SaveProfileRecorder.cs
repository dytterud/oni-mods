namespace SaveProfiler.Recording;

/// <summary>
/// The live state of one in-progress measurement: which phases have been timed, and how much each
/// component type cost.
///
/// Klei-free by construction. The Harmony shims in <c>Patches/</c> read a timestamp and a stream
/// position and hand both here; every decision about what those numbers mean is made in this
/// namespace, where a unit test can reach it.
///
/// Static because the thing being measured is a global, one-at-a-time operation. Lock-guarded
/// because <c>SaveLoader.CompressContents</c> is not guaranteed to stay on the main thread - Fast
/// Save's Background Save moves part of the work, and another mod could move more.
/// </summary>
public static class SaveProfileRecorder
{
    /// <summary>Bucket for component serializations that happened inside another one. Counting
    /// these into their parent's type would double-count the nested time; giving them their own
    /// row keeps them visible instead of discarded.</summary>
    public const string NestedBucket = "«nested serializations»";

    private static readonly object gate = new();
    private static readonly Dictionary<string, SampleAccumulator> phases = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, SampleAccumulator> types = new(StringComparer.Ordinal);

    /// <summary>
    /// Work measured <em>outside</em> <c>SaveLoader.Save</c> — the rest of the cycle boundary.
    ///
    /// Deliberately not cleared by <see cref="Begin"/>. Some of this runs before the save and some
    /// after it (the timelapse is a coroutine, so its frames land once the save has already
    /// returned and the report has already been written). Clearing per-save would drop whichever
    /// half fell on the wrong side. It is cleared when a report claims it instead, so each report
    /// covers the window since the previous one.
    /// </summary>
    private static readonly Dictionary<string, SampleAccumulator> ambient = new(StringComparer.Ordinal);
    private static readonly List<string> unresolved = [];

    [ThreadStatic]
    private static int componentDepth;

    /// <summary>
    /// The phases currently open on this thread, innermost last. Nesting is <em>measured</em> from
    /// this rather than declared from a table, so the report describes the call tree that actually
    /// ran — including one another mod has reordered.
    ///
    /// Thread-static, and a phase that starts on a thread with an empty stack is recorded as a
    /// direct child of the root. That is the honest answer for a mod that has moved part of the
    /// save off the main thread: we know it ran inside the save, and we no longer know what it ran
    /// inside of.
    /// </summary>
    [ThreadStatic]
    private static Stack<string>? phaseStack;

    private static readonly Dictionary<string, string?> phaseParents = new(StringComparer.Ordinal);

    /// <summary>True between the root phase's prefix and its postfix. The component shims check
    /// this so a stray serialization outside a save (a mod writing its own file, say) is not
    /// folded into the next report.</summary>
    public static bool Recording { get; private set; }

    public static bool AttributionEnabled { get; set; }

    /// <summary>Clears everything and opens a recording. Called from the root phase's prefix.</summary>
    public static void Begin()
    {
        lock (gate)
        {
            phases.Clear();
            phaseParents.Clear();
            types.Clear();
            Recording = true;
        }
        componentDepth = 0;
        phaseStack = null;
    }

    public static void End()
    {
        lock (gate)
            Recording = false;
    }

    /// <summary>Opens a phase on this thread. Pair with <see cref="ExitPhase"/>.</summary>
    public static void EnterPhase(string name) => (phaseStack ??= new Stack<string>()).Push(name);

    /// <summary>
    /// Closes a phase without recording it — for a shim whose prefix opened a phase that turned out
    /// not to belong in this report. Recording a zero-millisecond sample instead would invent a
    /// call that never counted, and leaving the stack unpopped would mis-parent everything after it.
    /// </summary>
    public static void PopPhase(string name)
    {
        var stack = phaseStack;
        if (stack is { Count: > 0 } && stack.Peek() == name)
            stack.Pop();
    }

    /// <summary>
    /// Closes the phase <paramref name="name"/> and records it against whatever phase encloses it.
    ///
    /// The stack is popped before the parent is read, so a phase's parent is the phase it nests
    /// inside rather than itself.
    /// </summary>
    public static void ExitPhase(string name, double elapsedMs, long bytes = 0)
    {
        var stack = phaseStack;
        if (stack is { Count: > 0 } && stack.Peek() == name)
            stack.Pop();

        string? parent = NearestDifferentAncestor(stack, name);

        lock (gate)
        {
            Accumulator(phases, name, SampleAccumulator.DefaultRetainedSamples).Add(elapsedMs, bytes);

            // First writer wins, except that a self-parent never does. A recursive phase whose
            // innermost call finished first would otherwise claim itself as its parent, and a
            // self-parented row is unreachable from the root - it silently vanishes from the tree.
            // That is exactly what happened to SaveLoadRoot.SaveWithoutTransform, which turns out
            // to re-enter itself: 2,021 ms across 21,516 calls disappeared out of a 3,265 ms
            // report, and the row above it absorbed the time as if it had no children.
            if (!phaseParents.ContainsKey(name) || phaseParents[name] == name)
                phaseParents[name] = parent;
        }
    }

    /// <summary>
    /// The innermost open phase that is not <paramref name="name"/> itself.
    ///
    /// A phase that recurses appears several times on the stack; its parent is the first frame
    /// below all of them, not the copy of itself directly underneath.
    /// </summary>
    private static string? NearestDifferentAncestor(Stack<string>? stack, string name)
    {
        if (stack == null)
            return null;

        foreach (string frame in stack)
        {
            if (!string.Equals(frame, name, StringComparison.Ordinal))
                return frame;
        }

        return null;
    }

    /// <summary>Records work outside the save. See <see cref="ambient"/> for why this survives
    /// <see cref="Begin"/>.</summary>
    public static void AddAmbient(string name, double elapsedMs)
    {
        lock (gate)
            Accumulator(ambient, name, SampleAccumulator.DefaultRetainedSamples).Add(elapsedMs, 0);
    }

    /// <summary>
    /// Records one component serialization against its type.
    ///
    /// Only the outermost call is attributed to <paramref name="typeName"/>: a nested one runs
    /// inside its parent's measured window, so adding both would count the inner time twice. The
    /// nested calls land in <see cref="NestedBucket"/> so the report can show that they happened
    /// and how much they cost.
    /// </summary>
    public static void AddComponent(string typeName, double elapsedMs, long bytes)
    {
        string bucket = componentDepth > 1 ? NestedBucket : typeName;

        lock (gate)
        {
            // 0 retained samples: this is called once per component - hundreds of thousands of
            // times in a mature colony - and the median is not worth the memory. Count, total,
            // min and max stay exact.
            Accumulator(types, bucket, retainLimit: 0).Add(elapsedMs, bytes);
        }
    }

    /// <summary>Enters a component serialization; returns the resulting nesting depth.</summary>
    public static int EnterComponent() => ++componentDepth;

    public static void ExitComponent()
    {
        if (componentDepth > 0)
            componentDepth--;
    }

    /// <summary>Records a patch target that could not be bound. Kept across recordings - a target
    /// that failed to resolve at load failed for every save this session.</summary>
    public static void AddUnresolved(string target)
    {
        lock (gate)
        {
            if (!unresolved.Contains(target))
                unresolved.Add(target);
        }
    }

    public static IReadOnlyList<string> Unresolved
    {
        get { lock (gate) return unresolved.ToList(); }
    }

    /// <summary>Snapshots everything recorded so far into a report. The caller fills in the
    /// metadata it had to ask the game for.</summary>
    public static SaveProfileReport BuildReport()
    {
        var report = new SaveProfileReport { ComponentAttribution = AttributionEnabled };

        lock (gate)
        {
            foreach (var (name, acc) in phases)
            {
                phaseParents.TryGetValue(name, out string? parent);
                var row = new SaveProfileReport.PhaseRow(name, parent, acc.Snap());
                if (name == SaveProfileReport.RootPhase)
                    report.Root = row;
                else
                    report.Phases.Add(row);
            }

            foreach (var (name, acc) in types)
                report.Types.Add(new SaveProfileReport.TypeRow(name, acc.Snap()));

            foreach (var (name, acc) in ambient)
                report.Ambient.Add(new SaveProfileReport.TypeRow(name, acc.Snap()));

            // Claimed, so the next report measures the next window rather than re-reporting this
            // one's totals on top of its own.
            ambient.Clear();

            report.UnresolvedTargets.AddRange(unresolved);
        }

        return report;
    }

    private static SampleAccumulator Accumulator(
        Dictionary<string, SampleAccumulator> into, string name, int retainLimit)
    {
        if (!into.TryGetValue(name, out var acc))
            into[name] = acc = new SampleAccumulator(retainLimit);
        return acc;
    }
}
