using System.Reflection;
using HarmonyLib;
using SaveProfiler.Recording;
using UtilLibs;

namespace SaveProfiler.Patches;

/// <summary>
/// Binds every measurement point, and reports what it could not bind.
///
/// Manual <see cref="Harmony.Patch"/> rather than <c>[HarmonyPatch]</c> attributes, deliberately:
/// several targets are overloaded, and an attribute has to pin a parameter list. A pinned list
/// stops matching the moment a game update adds an optional parameter, and the symptom is a row
/// reading zero calls - indistinguishable from a code path that never ran. docs/perf-method.md
/// records that exact failure costing two commits before anyone noticed, so targets are resolved
/// by name here and anything that fails to bind is named loudly in the log and in the report.
///
/// One target failing never aborts the rest: a renamed method should cost that one row's numbers,
/// not every other row's too.
///
/// Public rather than internal so the tests can assert that the real targets still resolve to the
/// shapes the shims are written against - this project cannot use <c>InternalsVisibleTo</c>,
/// because PolySharp's internal polyfills then collide with the real BCL types in the net8.0
/// test assembly.
/// </summary>
public static class PatchInstaller
{
    private static bool applied;

    public static void Apply(Harmony harmony)
    {
        if (applied)
            return;
        applied = true;

        SavePhasePatches.Register(harmony);
        ComponentSerializationPatches.Register(harmony);

        var unresolved = SaveProfileRecorder.Unresolved;
        if (unresolved.Count > 0)
        {
            SgtLogger.warning(
                $"SaveProfiler: {unresolved.Count} target(s) DID NOT RESOLVE - their rows will read " +
                $"0 calls, which is not the same as 'never called': {string.Join(", ", unresolved)}");
        }
        else
        {
            SgtLogger.l("SaveProfiler: all targets resolved");
        }
    }

    /// <summary>
    /// Patches one target, recording it as unresolved instead of throwing when it cannot be found.
    /// </summary>
    public static void TryPatch(
        Harmony harmony,
        string name,
        Func<MethodBase?> resolve,
        Type shimHost,
        string prefix,
        string postfix,
        Action<MethodBase>? onBound = null)
    {
        MethodBase? target;
        try
        {
            target = resolve();
        }
        catch (Exception e)
        {
            SaveProfileRecorder.AddUnresolved($"{name} ({e.GetType().Name})");
            return;
        }

        if (target == null)
        {
            SaveProfileRecorder.AddUnresolved(name);
            return;
        }

        try
        {
            harmony.Patch(target,
                prefix: new HarmonyMethod(shimHost, prefix),
                postfix: new HarmonyMethod(shimHost, postfix));
            onBound?.Invoke(target);
        }
        catch (Exception e)
        {
            SaveProfileRecorder.AddUnresolved($"{name} (patch failed: {e.GetType().Name})");
        }
    }

    /// <summary>
    /// The overload of <paramref name="methodName"/> with the most parameters.
    ///
    /// Preferred over a pinned parameter list for the reason in the type remarks. Returns null
    /// when the name matches nothing, so the caller records it as unresolved.
    /// </summary>
    public static MethodBase? WidestOverload(Type declaringType, string methodName) =>
        AccessTools.GetDeclaredMethods(declaringType)
            .Where(m => m.Name == methodName)
            .OrderByDescending(m => m.GetParameters().Length)
            .FirstOrDefault();

    /// <summary>Every overload of <paramref name="methodName"/>, widest first.</summary>
    public static IReadOnlyList<MethodBase> AllOverloads(Type declaringType, string methodName) =>
        AccessTools.GetDeclaredMethods(declaringType)
            .Where(m => m.Name == methodName)
            .OrderByDescending(m => m.GetParameters().Length)
            .Cast<MethodBase>()
            .ToList();
}
