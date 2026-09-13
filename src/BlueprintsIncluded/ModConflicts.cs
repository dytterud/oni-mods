namespace BlueprintsV2;

/// <summary>
/// Names and tests for the two ways another blueprint mod can collide with this one. Deliberately
/// free of any game type so the decision below is reachable from an ordinary offline unit test -
/// the walk over <c>KMod.Mod</c> that feeds it lives in <see cref="Mod.OnAllModsLoaded"/>.
///
/// <para>Both collisions come from the same fact: this mod is a fork of upstream Blueprints
/// Expanded, so the two carry the same <c>BlueprintsV2</c> root namespace and Harmony-patch the
/// same game methods. ONI has no declarative "incompatible with" field - mod_info.yaml gates on DLC
/// and nothing else, and Steam Workshop has no enforced conflicts list - so nothing stops a player
/// enabling both, and the result is two copies of every patch.</para>
/// </summary>
public static class ModConflicts
{
    /// <summary>This mod's <c>staticID</c>, i.e. its <c>AssemblyName</c>, as written into mod.yaml
    /// by the shared build targets.</summary>
    public const string OurStaticId = "BlueprintsIncluded";

    /// <summary>How this mod names itself in the conflict dialog.</summary>
    public const string OurModName = "Blueprints Included";

    /// <summary>
    /// Assembly name of upstream Blueprints Expanded, matched as a substring of
    /// <c>Assembly.FullName</c> by <c>CompatibilityNotifications.CheckAndAddIncompatibles</c>.
    ///
    /// <para><b>This is not a namespace, and must not be turned into one.</b> This fork kept the
    /// <c>BlueprintsV2</c> root namespace for save and translation compatibility, so every type in
    /// our own assembly is called <c>BlueprintsV2.Something</c>. <c>Assembly.FullName</c> never
    /// contains namespaces - ours reads <c>BlueprintsIncluded, Version=…</c> - which is the only
    /// reason this check does not match us. Rewriting it to look at type names would make the mod
    /// report a conflict with itself, on every launch, for every player.</para>
    /// </summary>
    public const string UpstreamAssemblyName = "BlueprintsV2";

    /// <summary>Upstream's display name, as it appears to the player in the Mods screen.</summary>
    public const string UpstreamModName = "Blueprints Expanded";

    /// <summary>
    /// What a second copy of this mod is called in the dialog. Kept under 40 characters:
    /// <c>CompatibilityNotifications.AddIncompatibleToList</c> truncates past that, since it is
    /// built for bulleted mod titles rather than sentences.
    /// </summary>
    public const string DuplicateCopyName = "another copy of Blueprints Included";

    /// <summary>
    /// True when <paramref name="activeStaticIds"/> holds our own <c>staticID</c> more than once -
    /// a Workshop copy running beside a local dev build, say. The assembly scan used for the
    /// upstream conflict cannot see this case: both copies are the same assembly under the same
    /// name, so it finds one match either way.
    /// </summary>
    /// <param name="activeStaticIds">The staticIDs of the mods that actually loaded. Null is
    /// treated as "nothing loaded", not as an error - a detection helper must never be the reason
    /// startup fails.</param>
    /// <param name="ourStaticId">Overridable for tests; production always passes the default.</param>
    public static bool HasDuplicateCopy(IEnumerable<string>? activeStaticIds, string ourStaticId = OurStaticId)
    {
        if (activeStaticIds == null)
            return false;

        int seen = 0;
        foreach (string? id in activeStaticIds)
        {
            // OrdinalIgnoreCase, because a staticID is an identifier compared across sources that
            // do not agree on casing (mod.yaml, the Workshop, a hand-edited mods.json), and getting
            // this wrong fails silently in the direction of never warning.
            if (id != null && string.Equals(id, ourStaticId, System.StringComparison.OrdinalIgnoreCase) && ++seen > 1)
                return true;
        }

        return false;
    }
}
