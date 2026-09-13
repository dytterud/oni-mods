using System.Collections.Generic;
using System.Linq;
using System.Text;
using BlueprintsV2.BlueprintData;
using Newtonsoft.Json.Linq;
using UnityEngine;
using Xunit;

namespace BlueprintsIncluded.Tests.BlueprintData;

/// <summary>
/// Pins the on-disk blueprint schema, because staying interchangeable with upstream
/// **Blueprints Expanded** is a stated design goal (see the mod README): a blueprint written by
/// either mod must open in the other, and the two read the same folder.
///
/// <see cref="BlueprintRoundTripTests"/> round-trips *values* through this mod's own writer and
/// reader, which agree with each other by construction — it would pass just as happily if a key
/// were renamed, a field added, or the version bumped, because both halves would move together.
/// Nothing else looks at the wire format at all, so a well-meaning change could diverge from
/// upstream silently and only be discovered by a player whose library stopped opening.
///
/// These tests are deliberately *change-detectors*, not correctness tests. Failing one does not
/// mean the code is wrong; it means the format moved, and that the move has to be a decision -
/// checked against upstream and written down in the README's compatibility section and the
/// CHANGELOG - rather than a side effect. Update the constants below only as part of making that
/// decision.
/// </summary>
public class BlueprintSchemaCompatibilityTests
{
    /// <summary>The schema version written at the fork point and unchanged since. Upstream writes
    /// the same number; a blueprint claiming a higher one is not readable by upstream.</summary>
    private const int ExpectedSchemaVersion = 3;

    /// <summary>
    /// Every top-level key this mod may emit. Optional keys (description, icon, tint) are omitted
    /// when empty, so the assertion is a subset check against this set rather than equality -
    /// what matters is that nothing *new* appears, since an unknown key is what upstream would
    /// have to tolerate.
    /// </summary>
    private static readonly HashSet<string> AllowedTopLevelKeys = new()
    {
        "blueprintVersion",
        "friendlyname",
        "userdesc",
        "buildings",
        "digcommands",
        "worldNotes",
        "planningtoolmod_shapecollection",
        "metadata",
        "icon",
        "icontint",
    };

    private static JObject WriteAndParse(Blueprint blueprint)
    {
        var builder = new StringBuilder();
        using (var writer = new System.IO.StringWriter(builder))
            blueprint.WriteJsonString(writer);
        return JObject.Parse(builder.ToString());
    }

    private static Blueprint PopulatedBlueprint()
    {
        var blueprint = new Blueprint(new StringBuilder("{}"))
        {
            FriendlyName = "Schema Canary",
            UserDescription = "description",
            IconId = "icon_id",
            IconTintHex = "FF8800FF",
        };
        blueprint.DigLocations.Add(new Vector2I(1, 2));
        blueprint.Metadata["origin"] = "unit-test";
        return blueprint;
    }

    [RequiresGameInstallFact]
    public void WritesTheSchemaVersionUpstreamExpects()
    {
        var written = WriteAndParse(PopulatedBlueprint());

        Assert.Equal(ExpectedSchemaVersion, written.Value<int>("blueprintVersion"));
    }

    /// <summary>
    /// The load-bearing one. A key added here is a key upstream has never seen: it would be
    /// ignored on read (so data silently lost on a round trip through upstream) or rejected,
    /// depending on how upstream's parser treats unknowns. Either way it is a compatibility
    /// decision, not an implementation detail.
    /// </summary>
    [RequiresGameInstallFact]
    public void WritesNoKeyOutsideTheAgreedSchema()
    {
        var written = WriteAndParse(PopulatedBlueprint());

        var unexpected = written.Properties()
            .Select(property => property.Name)
            .Where(name => !AllowedTopLevelKeys.Contains(name))
            .ToList();

        Assert.True(unexpected.Count == 0,
            "blueprint JSON gained top-level key(s) upstream does not know about: " +
            string.Join(", ", unexpected) +
            ". If this is intended, check it against upstream's reader, then update " +
            nameof(AllowedTopLevelKeys) + ", the README's compatibility section and the CHANGELOG.");
    }

    /// <summary>
    /// <c>Blueprint.ContentRevision</c> exists purely to invalidate the selection screen's cached
    /// preview and is bumped on every <c>CacheCost()</c>. It is in-memory state, and writing it
    /// would put a meaningless, constantly-changing field into every saved file. Asserted because
    /// it is public and the kind of thing a later change might plausibly decide to persist.
    /// </summary>
    [RequiresGameInstallFact]
    public void DoesNotPersistInMemoryPresentationState()
    {
        var blueprint = PopulatedBlueprint();
        blueprint.CacheCost();

        var written = WriteAndParse(blueprint);

        Assert.True(written.Property("ContentRevision") == null,
            "ContentRevision is presentation state and must not reach the file format");
    }
}
