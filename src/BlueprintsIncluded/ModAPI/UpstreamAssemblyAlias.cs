using System.Reflection;
using UtilLibs;

namespace BlueprintsV2.ModAPI;

/// <summary>
/// Makes this fork answer to upstream's assembly name, so mods that integrate with Blueprints
/// Expanded by reflection find our API instead of nothing.
///
/// <para>The concrete case, and the reason this exists: upstream's Rocketry Expanded resolves the
/// blueprint-data API with
/// <c>Type.GetType("BlueprintsV2.ModAPI.API_Methods, BlueprintsV2")</c>. The type name already
/// matches us - this fork kept the <c>BlueprintsV2</c> root namespace - but the assembly simple
/// name does not: ours is <c>BlueprintsIncluded</c>. Without this, the lookup returns null, RE logs
/// "BlueprintsV2 types not found", and every call silently no-ops. The reflectable API added for
/// exactly that consumer would be dead code.</para>
///
/// <para><b>Why an alias rather than renaming the assembly.</b> <c>AssemblyName</c> is also written
/// into <c>mod.yaml</c> as this mod's <c>staticID</c>, and
/// <see cref="ModConflicts.UpstreamAssemblyName"/> detects upstream by matching "BlueprintsV2" as a
/// substring of <c>Assembly.FullName</c> - a check that works <i>only</i> because our own
/// FullName does not contain it. Renaming would collide our staticID with upstream's and make the
/// mod report a conflict with itself on every launch. This keeps both intact.</para>
///
/// <para><b>Scope.</b> The handler answers for the simple name <c>BlueprintsV2</c> and nothing
/// else, and only when the runtime has already failed to find that assembly by itself -
/// <c>AssemblyResolve</c> is a last-resort event. Upstream's real assembly is never shadowed: if it
/// were loaded, the runtime would resolve it without ever raising the event, and in any case the
/// two cannot run together (see <see cref="ModConflicts"/>).</para>
/// </summary>
internal static class UpstreamAssemblyAlias
{
    private static bool installed;

    /// <summary>
    /// Registers the resolver. Safe to call more than once; only the first call installs anything.
    /// </summary>
    public static void Install()
    {
        if (installed)
            return;
        installed = true;

        System.AppDomain.CurrentDomain.AssemblyResolve += ResolveUpstreamName;
        SgtLogger.l($"answering to '{ModConflicts.UpstreamAssemblyName}' for mods that integrate by reflection");
    }

    private static Assembly? ResolveUpstreamName(object sender, ResolveEventArgs args)
    {
        ///args.Name is a full display name ("BlueprintsV2, Version=1.0.0.0, Culture=..."), so
        ///compare the simple name rather than the whole string - a consumer that qualifies its
        ///lookup with a version would otherwise miss.
        string requested;
        try
        {
            requested = new AssemblyName(args.Name).Name;
        }
        catch
        {
            ///a malformed name is not ours to interpret; let the runtime carry on failing
            return null;
        }

        if (!string.Equals(requested, ModConflicts.UpstreamAssemblyName, System.StringComparison.Ordinal))
            return null;

        return typeof(UpstreamAssemblyAlias).Assembly;
    }
}
