using System;
using System.Collections.Generic;
using System.Reflection;
using BlueprintsV2.BlueprintData;
using Newtonsoft.Json.Linq;
using UnityEngine;
using Xunit;

namespace BlueprintsIncluded.Tests.ModAPI;

/// <summary>
/// Proves the reflectable API is actually reachable by the consumer it was written for.
///
/// <para>The API surface tests next door assert the methods exist with the right signatures. That
/// is necessary and was not sufficient: upstream's Rocketry Expanded resolves the type as
/// <c>Type.GetType("BlueprintsV2.ModAPI.API_Methods, BlueprintsV2")</c>, and this fork's assembly
/// is called <c>BlueprintsIncluded</c>, so the lookup returned null and every call no-opped
/// silently. These tests make the lookup itself, verbatim, rather than trusting that a correct
/// signature implies a reachable one.</para>
///
/// <para>Game-gated: resolving the type loads it, and its members name ONI types.</para>
/// </summary>
public class UpstreamAssemblyAliasTests
{
    /// <summary>Exactly the string Rocketry Expanded passes.</summary>
    private const string UpstreamQualifiedName = "BlueprintsV2.ModAPI.API_Methods, BlueprintsV2";

    private static void InstallAlias()
    {
        var alias = typeof(BuildingConfig).Assembly.GetType("BlueprintsV2.ModAPI.UpstreamAssemblyAlias");
        Assert.True(alias != null, "the alias type exists");
        alias!.GetMethod("Install", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, null);
    }

    [RequiresGameInstallFact]
    public void TheLookupRocketryExpandedMakes_FindsOurApi()
    {
        InstallAlias();

        var type = Type.GetType(UpstreamQualifiedName);

        Assert.NotNull(type);
        // resolved to *our* assembly, not some other BlueprintsV2
        Assert.Equal("BlueprintsIncluded", type!.Assembly.GetName().Name);
    }

    [RequiresGameInstallFact]
    public void TheDelegateRocketryExpandedBinds_ResolvesThroughTheAlias()
    {
        InstallAlias();

        var type = Type.GetType(UpstreamQualifiedName);
        Assert.NotNull(type);

        // the two members RE binds, with the parameter types it binds them by
        var apply = type!.GetMethod("ApplyAdditionalBuildingData", BindingFlags.Public | BindingFlags.Static,
            binder: null,
            types: new[] { typeof(GameObject), typeof(BuildingDef), typeof(Dictionary<string, JObject>) },
            modifiers: null);
        var getAll = type.GetMethod("GetAllAdditionalBuildingData", BindingFlags.Public | BindingFlags.Static,
            binder: null, types: new[] { typeof(GameObject) }, modifiers: null);

        Assert.NotNull(apply);
        Assert.NotNull(getAll);
    }

    [RequiresGameInstallFact]
    public void TheAliasAnswersForNothingElse()
    {
        InstallAlias();

        // a name we do not claim must still fail to resolve - the handler is scoped to one
        // assembly simple name, not a catch-all that would mask genuine load failures
        Assert.Null(Type.GetType("Whatever.Type, NotAnAssemblyThisModProvides"));
    }

    [RequiresGameInstallFact]
    public void InstallingTwice_IsHarmless()
    {
        InstallAlias();
        InstallAlias();

        Assert.NotNull(Type.GetType(UpstreamQualifiedName));
    }
}
