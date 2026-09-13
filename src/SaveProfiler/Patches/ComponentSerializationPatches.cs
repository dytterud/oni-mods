using System.Diagnostics;
using HarmonyLib;
using SaveProfiler.Recording;

namespace SaveProfiler.Patches;

/// <summary>
/// Attributes save cost to the individual components being written.
///
/// The byte counts come free from the format. <c>SaveLoadRoot.SaveWithoutTransform</c> writes each
/// component as <c>&lt;type name&gt;&lt;int32 byte length&gt;&lt;data&gt;</c>, backpatching the
/// length by seeking - so a stream-position delta across one serialization <em>is</em> that
/// component's on-disk size, with nothing to parse and no second pass.
///
/// Two levels, verified against the shipped assemblies:
/// <code>
/// SaveLoadRoot.SaveWithoutTransform(BinaryWriter writer)            // once per saved GameObject
///   KSerialization.Serializer.SerializeTypeless(object obj, BinaryWriter writer)   // per component
/// </code>
///
/// The per-component level is <b>opt-in</b> (<see cref="Config.ComponentAttribution"/>). It fires
/// hundreds of thousands of times in a mature colony and a Harmony wrapper costs roughly 0.2 µs,
/// so leaving it on would inflate the very phase totals it sits inside. The report says which mode
/// produced it and refuses to let the two be compared - the same discipline as the sibling
/// harness's <c>perf-attribution</c> sentinel, and for the same reason.
/// </summary>
internal static class ComponentSerializationPatches
{
    private const string ObjectPhase = "SaveLoadRoot.SaveWithoutTransform";

    private static readonly double TicksToMs = 1000.0 / Stopwatch.Frequency;

    public static void Register(Harmony harmony)
    {
        PatchInstaller.TryPatch(harmony, "SaveLoadRoot.SaveWithoutTransform",
            () => PatchInstaller.WidestOverload(typeof(SaveLoadRoot), "SaveWithoutTransform"),
            typeof(ComponentSerializationPatches), nameof(ObjectPrefix), nameof(ObjectPostfix));

        if (!SaveProfileRecorder.AttributionEnabled)
            return;

        PatchInstaller.TryPatch(harmony, "KSerialization.Serializer.SerializeTypeless",
            () => PatchInstaller.WidestOverload(typeof(KSerialization.Serializer), "SerializeTypeless"),
            typeof(ComponentSerializationPatches), nameof(ComponentPrefix), nameof(ComponentPostfix));
    }

    // ---------------------------------------------------------------- per GameObject

    /// <summary><c>__0</c> is the writer. Its stream position is the only cheap source of a byte
    /// count; <c>BinaryWriter</c> itself does not track one.</summary>
    internal static void ObjectPrefix(BinaryWriter __0, out ComponentState __state)
    {
        SaveProfileRecorder.EnterPhase(ObjectPhase);
        __state = ComponentState.Start(__0);
    }

    internal static void ObjectPostfix(BinaryWriter __0, ComponentState __state)
    {
        // Only while a save is in flight. Nothing stops another mod serializing a SaveLoadRoot
        // into its own file, and folding that into the next report would attribute another mod's
        // work to the game's save path.
        if (!SaveProfileRecorder.Recording)
        {
            // Still pop, or the stack drifts and every later phase is mis-parented.
            SaveProfileRecorder.PopPhase(ObjectPhase);
            return;
        }

        SaveProfileRecorder.ExitPhase(ObjectPhase, __state.ElapsedMs(), __state.BytesWritten(__0));
    }

    // ---------------------------------------------------------------- per component

    /// <summary><c>__0</c> is the object being serialized, <c>__1</c> the writer.</summary>
    internal static void ComponentPrefix(BinaryWriter __1, out ComponentState __state)
    {
        SaveProfileRecorder.EnterComponent();
        __state = ComponentState.Start(__1);
    }

    internal static void ComponentPostfix(object __0, BinaryWriter __1, ComponentState __state)
    {
        // Recorded before leaving the nesting level: ExitComponent is what makes the next sibling
        // an outermost call again, and the depth decides which bucket this one lands in.
        // ExitComponent still runs when we are not recording, so the depth stays balanced with
        // ComponentPrefix rather than drifting upwards and mis-bucketing every later component.
        if (SaveProfileRecorder.Recording)
        {
            SaveProfileRecorder.AddComponent(
                __0?.GetType().Name ?? "(null)", __state.ElapsedMs(), __state.BytesWritten(__1));
        }

        SaveProfileRecorder.ExitComponent();
    }

    /// <summary>
    /// Readonly struct so the wrapper allocates nothing - the same rule as
    /// <see cref="SavePhasePatches.PhaseState"/>, and it matters more here: this runs once per
    /// component, and an allocation in the wrapper would be charged to the code under test.
    /// </summary>
    internal readonly struct ComponentState
    {
        private ComponentState(long startTimestamp, long startPosition)
        {
            StartTimestamp = startTimestamp;
            StartPosition = startPosition;
        }

        public long StartTimestamp { get; }
        public long StartPosition { get; }

        public static ComponentState Start(BinaryWriter writer) =>
            new(Stopwatch.GetTimestamp(), Position(writer));

        public double ElapsedMs() => (Stopwatch.GetTimestamp() - StartTimestamp) * TicksToMs;

        /// <summary>Bytes written since <see cref="Start"/>. Never negative: the length backpatch
        /// seeks backwards and then restores the position, so a postfix that happened to observe
        /// the seek would otherwise report a negative size.</summary>
        public long BytesWritten(BinaryWriter writer) => Math.Max(0, Position(writer) - StartPosition);

        /// <summary>The stream may be closed or non-seekable by the time a postfix runs - a mod
        /// backgrounding part of the save is enough. A missing byte count is worth losing; an
        /// exception thrown out of a postfix would take the player's save with it.</summary>
        private static long Position(BinaryWriter writer)
        {
            try
            {
                var stream = writer?.BaseStream;
                return stream is { CanSeek: true } ? stream.Position : 0;
            }
            catch (Exception)
            {
                return 0;
            }
        }
    }
}
