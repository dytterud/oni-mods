using BlueprintsV2;
using Xunit;

namespace BlueprintsIncluded.Tests;

/// <summary>
/// Locks <see cref="ModConflicts.HasDuplicateCopy"/>, the test behind the "a second copy of this
/// mod is loaded" warning. Deliberately not gated on a game install: the helper takes plain strings
/// precisely so this decision is reachable offline, while the <c>KMod.Mod</c> walk that supplies
/// them stays in <c>Mod.OnAllModsLoaded</c>.
///
/// The case that matters most is <see cref="OneCopy_IsNotADuplicate"/>. A false positive here does
/// not degrade quietly — it shows every ordinary player a conflict dialog at every launch.
/// </summary>
public class ModConflictsTests
{
    private const string Ours = ModConflicts.OurStaticId;

    [Fact]
    public void OneCopy_IsNotADuplicate()
    {
        Assert.False(ModConflicts.HasDuplicateCopy(new[] { Ours }));
    }

    [Fact]
    public void OneCopyAmongOtherMods_IsNotADuplicate()
    {
        Assert.False(ModConflicts.HasDuplicateCopy(
            new[] { "BlueprintsV2", "PlanningTool", Ours, "ONI_Together" }));
    }

    [Fact]
    public void TwoCopies_IsADuplicate()
    {
        Assert.True(ModConflicts.HasDuplicateCopy(new[] { Ours, Ours }));
    }

    // The real shape of this: a Workshop install and a local dev build, surrounded by unrelated
    // mods. Both carry the same staticID from mod.yaml even though their folders differ.
    [Fact]
    public void TwoCopiesAmongOtherMods_IsADuplicate()
    {
        Assert.True(ModConflicts.HasDuplicateCopy(
            new[] { "PlanningTool", Ours, "BlueprintsV2", Ours }));
    }

    // staticIDs cross sources that do not agree on casing - mod.yaml, the Workshop, a hand-edited
    // mods.json - and an ordinal comparison would miss the duplicate without ever saying so.
    [Fact]
    public void CasingDoesNotHideADuplicate()
    {
        Assert.True(ModConflicts.HasDuplicateCopy(new[] { Ours, "blueprintsincluded" }));
    }

    [Theory]
    [InlineData("BlueprintsV2")]        // upstream: a different mod, found by the assembly scan
    [InlineData("BlueprintsIncludedHarness")]  // the dev harness: a prefix match, not our id
    public void ADifferentModIsNotACopyOfUs(string otherStaticId)
    {
        Assert.False(ModConflicts.HasDuplicateCopy(new[] { Ours, otherStaticId }));
    }

    // Detection runs during mod load; it must never be the reason startup fails.
    [Fact]
    public void NoMods_IsNotADuplicate()
    {
        Assert.False(ModConflicts.HasDuplicateCopy(new string[0]));
    }

    [Fact]
    public void ANullListIsTreatedAsNothingLoaded()
    {
        Assert.False(ModConflicts.HasDuplicateCopy(null));
    }

    [Fact]
    public void ANullEntryIsSkippedRatherThanThrowing()
    {
        Assert.True(ModConflicts.HasDuplicateCopy(new[] { Ours, null, Ours }!));
    }
}
