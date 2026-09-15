using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BlueprintsV2.BlueprintData;
using Newtonsoft.Json.Linq;
using UnityEngine;
using Xunit;

namespace BlueprintsIncluded.Tests.ModAPI;

/// <summary>
/// Locks the shape of the API surface external mods reach by reflection.
///
/// This is the one part of the mod where a signature *is* the contract. Rocketry Expanded looks
/// these up by name and parameter types at runtime, so renaming a parameter type, dropping the
/// redundant three-argument overload, or making a method non-static breaks the integration
/// silently - no compile error here, and none there either, just settings that quietly stop
/// transferring. A test is the only thing that makes that loud.
///
/// Everything is resolved by name rather than by reference, because <c>API_Methods</c> is
/// <c>internal</c> (this project deliberately avoids InternalsVisibleTo) - and because a reflecting
/// caller sees exactly this much, so the test fails for the same reasons the real consumer would.
///
/// Game-gated: the signatures name <c>GameObject</c> and <c>BuildingDef</c>.
/// </summary>
public class ReflectableApiSurfaceTests
{
    private const string ApiMethodsTypeName = "BlueprintsV2.ModAPI.API_Methods";
    private const string ApplyMethodName = "ApplyAdditionalBuildingData";

    private static Type ApiMethods()
    {
        var type = typeof(BuildingConfig).Assembly.GetType(ApiMethodsTypeName);
        Assert.True(type != null, $"{ApiMethodsTypeName} exists - external mods resolve it by this name");
        return type!;
    }

    private static MethodInfo? FindApplyOverload(params Type[] parameterTypes) =>
        ApiMethods()
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .FirstOrDefault(m => m.Name == ApplyMethodName
                                 && m.GetParameters().Select(p => p.ParameterType).SequenceEqual(parameterTypes));

    [RequiresGameInstallFact]
    public void ApplyAdditionalBuildingData_KeepsTheBuildingConfigOverload()
    {
        Assert.NotNull(FindApplyOverload(typeof(GameObject), typeof(BuildingConfig), typeof(ulong)));
    }

    [RequiresGameInstallFact]
    public void ApplyAdditionalBuildingData_HasTheThreeArgumentReflectableOverload()
    {
        // Redundant with the four-argument form by design: it exists so a reflecting caller can
        // bind without supplying the optional player id. Dropping it would defeat the surface.
        Assert.NotNull(FindApplyOverload(
            typeof(GameObject), typeof(BuildingDef), typeof(Dictionary<string, JObject>)));
    }

    [RequiresGameInstallFact]
    public void ApplyAdditionalBuildingData_HasTheFourArgumentReflectableOverload()
    {
        Assert.NotNull(FindApplyOverload(
            typeof(GameObject), typeof(BuildingDef), typeof(Dictionary<string, JObject>), typeof(ulong)));
    }

    [RequiresGameInstallFact]
    public void GetAllAdditionalBuildingData_IsPublicStaticAndReturnsTheDataDictionary()
    {
        var method = ApiMethods().GetMethod(
            "GetAllAdditionalBuildingData", BindingFlags.Public | BindingFlags.Static);

        Assert.NotNull(method);
        Assert.Equal(typeof(Dictionary<string, JObject>), method!.ReturnType);
        Assert.Equal(new[] { typeof(GameObject) },
            method.GetParameters().Select(p => p.ParameterType).ToArray());
    }

    [RequiresGameInstallFact]
    public void GetDataDeserialized_ExistsForGetAllAdditionalBuildingDataToUse()
    {
        // Internal rather than public, matching upstream: reflection ignores accessibility, and
        // the intended external entry point is GetAllAdditionalBuildingData rather than this.
        var method = typeof(UnderConstructionDataTransfer).GetMethod(
            "GetDataDeserialized", BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.NotNull(method);
        Assert.Equal(typeof(Dictionary<string, JObject>), method!.ReturnType);
        Assert.Empty(method.GetParameters());
    }
}
