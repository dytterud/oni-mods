using System.Text;
using BlueprintsV2.BlueprintData;
using BlueprintsV2.BlueprintData.NoteToolPlacedEntities;
using UnityEngine;
using Xunit;

namespace BlueprintsIncluded.Tests.BlueprintData
{
	/// <summary>
	/// Round-trips a <see cref="Blueprint"/> through <c>WriteJsonString</c> →
	/// <c>new Blueprint(StringBuilder)</c> for the parts that do not require a running
	/// game: name, description, icon, dig locations, world notes, metadata.
	///
	/// Building configurations are deliberately excluded — <c>BuildingConfig.WriteJson</c>
	/// early-returns unless <c>Assets.GetBuildingDef</c> resolves, which needs a loaded
	/// colony, not just the game assemblies. Gated on a real install for the Klei types
	/// this still touches (<c>Vector2I</c>, <c>Color</c>, <c>SimHashes</c>).
	/// </summary>
	public class BlueprintRoundTripTests
	{
		[RequiresGameInstallFact]
		public void RoundTripsNameIconDigLocationsNotesAndMetadata()
		{
			var source = new Blueprint(new StringBuilder("{}"))
			{
				FriendlyName = "Smoke Test Blueprint",
				UserDescription = "a description with spaces",
				IconId = "some_icon_id",
				IconTintHex = "FF8800FF",
			};
			source.DigLocations.Add(new Vector2I(1, 2));
			source.DigLocations.Add(new Vector2I(3, 0));
			source.Metadata["origin"] = "unit-test";
			source.Metadata["schema"] = "3";

			var textNote = BlueprintNoteData.CreateTextNote(
				new Vector2I(2, 2), "Note title", "Note body", "sym", Color.white);
			source.WorldNotes[textNote.Location] = textNote;
			var elementNote = BlueprintNoteData.CreateElementNote(
				new Vector2I(4, 1), SimHashes.Oxygen, 100f, 296f);
			source.WorldNotes[elementNote.Location] = elementNote;

			var sb = new StringBuilder();
			using (var sw = new StringWriter(sb))
			{
				source.WriteJsonString(sw);
			}
			var restored = new Blueprint(sb);

			Assert.Equal("Smoke Test Blueprint", restored.FriendlyName);
			Assert.Equal("a description with spaces", restored.UserDescription);
			Assert.Equal("some_icon_id", restored.IconId);
			Assert.Equal("FF8800FF", restored.IconTintHex);

			Assert.Equal(2, restored.DigLocations.Count);
			Assert.Contains(new Vector2I(1, 2), restored.DigLocations);
			Assert.Contains(new Vector2I(3, 0), restored.DigLocations);

			Assert.Equal("unit-test", restored.Metadata["origin"]);
			Assert.Equal("3", restored.Metadata["schema"]);

			Assert.Equal(2, restored.WorldNotes.Count);
			Assert.Equal(BlueprintNoteData.NoteType.Text, restored.WorldNotes[new Vector2I(2, 2)].Type);
			Assert.Equal("Note body", restored.WorldNotes[new Vector2I(2, 2)].Text);
			Assert.Equal(BlueprintNoteData.NoteType.Element, restored.WorldNotes[new Vector2I(4, 1)].Type);
			Assert.Equal(SimHashes.Oxygen, restored.WorldNotes[new Vector2I(4, 1)].ElementId);
		}

		[RequiresGameInstallFact]
		public void EmptyBlueprintRoundTripsToEmpty()
		{
			var source = new Blueprint(new StringBuilder("{}")) { FriendlyName = "Empty" };

			var sb = new StringBuilder();
			using (var sw = new StringWriter(sb))
			{
				source.WriteJsonString(sw);
			}
			var restored = new Blueprint(sb);

			Assert.Equal("Empty", restored.FriendlyName);
			Assert.Empty(restored.BuildingConfigurations);
			Assert.Empty(restored.DigLocations);
			Assert.Empty(restored.WorldNotes);
		}
	}
}
