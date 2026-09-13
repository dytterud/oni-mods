using System;
using System.Collections;
using System.Diagnostics;
using System.Reflection;
using BlueprintsV2.BlueprintData;
using UnityEngine;
using BlueprintsV2.Harness;

namespace BlueprintsV2.Harness.Perf;

/// <summary>
/// Answers one question the main create-path measurement could not: <b>is the cached lookup still
/// faster when the type actually resolves?</b>
///
/// The create sweep measured <c>GetComponent(string)</c> at ~203 µs/call and the cached form at
/// roughly the typed-lookup floor - but only for names no loaded assembly defines, which is the
/// pathological case. With Aki's decor mods installed the string overload would resolve its type
/// and take a different, probably much cheaper path, so the 96% figure from that sweep cannot be
/// carried over. Rather than install those mods, this defines its own component type and times all
/// four combinations directly:
///
/// <list type="bullet">
/// <item><c>string-resolves</c> - <c>GetComponent(string)</c>, type exists. What a Decor Pack user
/// paid before the fix.</item>
/// <item><c>cached-resolves</c> - <c>ModComponentLookup.Find</c>, type exists. What they pay
/// after. <b>This pair is the answer.</b></item>
/// <item><c>string-absent</c> / <c>cached-absent</c> - the same pair for a name nothing defines,
/// reproducing the create-path finding in isolation as a control.</item>
/// </list>
///
/// Timed on a throwaway GameObject rather than a building: the question is the lookup mechanism,
/// and holding the object constant across all four arms keeps component count out of it.
/// </summary>
internal static class ComponentLookupPerf
{
    /// <summary>Matches <see cref="HarnessProbeComponent"/>. Resolvable by short name.</summary>
    private const string ResolvableName = nameof(HarnessProbeComponent);

    /// <summary>Deliberately not a type anywhere. The name is nonsense so it cannot start
    /// accidentally resolving if some future mod is added to the dev folder.</summary>
    private const string AbsentName = "HarnessNoSuchComponent_9d3f1a";

    private const int Warmup = 200, Iterations = 2000;

    /// <summary>Reached by name for the same reason the rest of the harness does it - the type is
    /// internal to the mod. Null means the production fix is not present, in which case there is
    /// nothing to compare and the sweep says so rather than reporting a misleading zero.</summary>
    private static readonly MethodInfo? FindMethod =
        typeof(Blueprint).Assembly.GetType("BlueprintsV2.BlueprintData.ModComponentLookup")
            ?.GetMethod("Find", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);

    public static IEnumerator Run(PerfReport report, HarnessLog log)
    {
        if (FindMethod == null)
        {
            log.Line("component-lookup: ModComponentLookup.Find not found - skipping (is the cached " +
                     "lookup still in the mod?)");
            yield break;
        }

        var host = new GameObject("HarnessComponentLookupProbe");
        host.AddComponent<HarnessProbeComponent>();

        // Correctness first: a timing is meaningless if the two forms disagree about what they find.
        // This is the only place the resolving branch is checked at all, since the fixture has no
        // mod components in it.
        var viaString = host.GetComponent(ResolvableName);
        var viaCache = FindMethod.Invoke(null, new object[] { host, ResolvableName }) as Component;
        var absentViaString = host.GetComponent(AbsentName);
        var absentViaCache = FindMethod.Invoke(null, new object[] { host, AbsentName }) as Component;

        log.Line($"component-lookup agreement: resolvable string={(viaString != null)} " +
                 $"cached={(viaCache != null)} (same instance: {ReferenceEquals(viaString, viaCache)}); " +
                 $"absent string={(absentViaString != null)} cached={(absentViaCache != null)}");
        if (viaCache == null || !ReferenceEquals(viaString, viaCache) || absentViaCache != null)
        {
            log.Line("  *** component-lookup: the cached form DISAGREES with GetComponent(string) - " +
                     "the timings below are not comparing equivalent operations, and the production " +
                     "lookup is wrong for the case where the type resolves.");
        }

        object[] resolvableArgs = { host, ResolvableName };
        object[] absentArgs = { host, AbsentName };

        yield return TimeLookup(report, log, "component-lookup-string-resolves",
            () => host.GetComponent(ResolvableName));
        yield return TimeLookup(report, log, "component-lookup-cached-resolves",
            () => FindMethod.Invoke(null, resolvableArgs));
        yield return TimeLookup(report, log, "component-lookup-string-absent",
            () => host.GetComponent(AbsentName));
        yield return TimeLookup(report, log, "component-lookup-cached-absent",
            () => FindMethod.Invoke(null, absentArgs));

        UnityEngine.Object.Destroy(host);
    }

    /// <summary>
    /// Times <paramref name="body"/> over <see cref="Iterations"/> calls and reports the mean
    /// microseconds per call, recorded as a one-point "sweep" at N=1.
    ///
    /// Per-call rather than per-batch because these are sub-microsecond operations that a single
    /// Stopwatch reading cannot resolve - the batch is the measurement, and the division is the
    /// answer. Note the cached arms carry a <c>MethodInfo.Invoke</c> the real call site does not
    /// (the method is internal to the mod), so their true cost is *lower* than reported; that only
    /// makes the comparison conservative in the direction being argued.
    /// </summary>
    private static IEnumerator TimeLookup(PerfReport report, HarnessLog log, string opName, Func<object?> body)
    {
        for (int i = 0; i < Warmup; i++)
            body();
        yield return null;

        var sw = Stopwatch.StartNew();
        for (int i = 0; i < Iterations; i++)
            body();
        sw.Stop();

        double usPerCall = sw.Elapsed.TotalMilliseconds * 1000.0 / Iterations;
        log.Line($"  {opName}: {usPerCall:F3}us/call over {Iterations} calls");
        PerfRunner.Record(report, log, opName, 1, Iterations,
            new System.Collections.Generic.List<double> { sw.Elapsed.TotalMilliseconds }, null);
        yield return null;
    }
}
