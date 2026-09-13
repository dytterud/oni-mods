using HarmonyLib;
using UnityEngine;

namespace BlueprintsV2.BlueprintData;

/// <summary>
/// Finds a component belonging to another mod, by type name, without paying
/// <c>GameObject.GetComponent(string)</c> for it on every call.
///
/// Unity's string overload resolves a type from a string every time, and when nothing matches it
/// is pathologically slow. Measured on the capture path (docs §7): <b>~203 µs per call</b>, against
/// 1.0-12.4 µs for the typed <c>TryGetComponent&lt;T&gt;</c> handlers sitting beside it in the same
/// per-building loop. Three such lookups accounted for 94% of that loop and roughly 57% of the cost
/// of capturing a building - every one of them returning null, because the mods that define those
/// components were not installed.
///
/// The fix is available precisely because of *why* they return null. These lookups exist in
/// fallback handlers that <c>API_Methods.RegisterExtraData</c> registers when Aki's decor mods are
/// absent, and if no loaded assembly defines a type by that name then no <c>GameObject</c> can
/// carry one - ever. Loaded assemblies do not change after mod load, so a name that resolves to
/// nothing resolves to nothing for the life of the process and the whole lookup can be skipped.
/// When the mod *is* present the type resolves once and the call becomes the typed overload, which
/// is the fast one.
///
/// Resolution is lazy rather than done at registration: handlers run long after
/// <c>OnAllModsLoaded</c>, so deferring to first use removes any question of resolving a name
/// before the assembly defining it has loaded.
/// </summary>
internal static class ModComponentLookup
{
    ///negative results are cached too - "this mod is not installed" is the case worth making cheap,
    ///and it is the common one.
    static readonly Dictionary<string, Type?> resolvedTypes = [];

    /// <summary>
    /// The component of the named type on <paramref name="gameObject"/>, or null if the type is not
    /// loaded at all or the object does not carry it.
    ///
    /// Note this uses <c>GetComponent(Type)</c> once resolved, which matches assignable subclasses
    /// where the string overload matched the exact class name. For the mod components this is used
    /// for the two are the same set, and the typed behaviour is the more useful of the two.
    /// </summary>
    internal static Component? Find(GameObject gameObject, string typeName)
    {
        if (!resolvedTypes.TryGetValue(typeName, out var type))
        {
            type = ResolveComponentType(typeName);
            resolvedTypes[typeName] = type;
        }

        ///the type does not exist in this process, so nothing can be carrying it.
        if (type == null)
            return null;

        return gameObject.GetComponent(type);
    }

    /// <summary>
    /// The <see cref="Component"/>-derived type with this name, or null.
    ///
    /// Deliberately not <c>AccessTools.TypeByName</c>: its last resort is
    /// <c>AllTypes().FirstOrDefault(t =&gt; t.Name == name)</c> - first short-name match across every
    /// loaded assembly, in whatever order they happen to be enumerated. A name like "Backwall" is
    /// generic enough that an unrelated assembly could own one, and binding to the wrong type here
    /// fails *silently*: <c>GetComponent(wrongType)</c> simply never matches, so the mod's data
    /// would stop round-tripping with no error anywhere. Restricting the search to component types
    /// matches what <c>GetComponent(string)</c> could have returned in the first place, since
    /// nothing else can sit on a GameObject.
    /// </summary>
    static Type? ResolveComponentType(string typeName)
    {
        var componentType = typeof(Component);
        foreach (var candidate in AccessTools.AllTypes())
        {
            if (candidate.Name == typeName && componentType.IsAssignableFrom(candidate))
                return candidate;
        }
        return null;
    }
}
