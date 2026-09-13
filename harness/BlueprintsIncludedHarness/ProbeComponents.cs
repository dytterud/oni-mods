using UnityEngine;

namespace BlueprintsV2.Harness;

/// <summary>
/// A component that exists only so a lookup by type *name* can succeed. Nothing attaches it to a
/// real building; the harness puts it on throwaway GameObjects.
///
/// <c>ModComponentLookup</c> resolves mod component types by name and caches the result. Every
/// production caller of it names a type belonging to Aki's decor mods, so in this fixture - which
/// has no mods beyond ours - the name never resolves and only the "type absent" branch runs. A
/// player who *has* those mods takes the other branch, and nothing here could reach it. This type
/// makes it reachable with no external dependency: it is a component, so the
/// <c>Component</c>-restricted scan finds it, and its name is unique enough not to collide with the
/// game's or another mod's.
/// </summary>
public class HarnessProbeComponent : MonoBehaviour
{
}

/// <summary>
/// Subclass of <see cref="HarnessProbeComponent"/>, used to pin one known behaviour difference.
///
/// The fix replaced <c>GetComponent(string)</c> with <c>GetComponent(Type)</c> once the name is
/// resolved, and the typed overload matches types *assignable to* the requested one - a superset of
/// the string overload's exact-class-name matching. That difference is only observable when a
/// GameObject carries a subclass and not the base, which is what this type is for. It is asserted
/// rather than merely noted so the behaviour we actually ship is nailed down.
/// </summary>
public class HarnessProbeComponentSubclass : HarnessProbeComponent
{
}
