using System.Text;
using BlueprintsV2.BlueprintData.NoteToolPlacedEntities;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using Xunit;

namespace BlueprintsIncluded.Tests.BlueprintData;

	/// <summary>
	/// Round-trips <see cref="BlueprintNoteData"/> through its hand-written
	/// <c>WriteDataJson</c> / <c>ReadDataJson</c> pair. Needs the Klei/Unity types
	/// (<c>Vector2I</c>, <c>Color</c>, <c>Util.ColorFromHex</c>, <c>SimHashes</c>) at
	/// runtime, so it is gated on a real install.
	/// </summary>
	public class BlueprintNoteDataRoundTripTests
	{
		private static BlueprintNoteData RoundTrip(BlueprintNoteData source)
		{
			var sb = new StringBuilder();
			using (var sw = new StringWriter(sb))
			using (var jw = new JsonTextWriter(sw))
			{
				source.WriteDataJson(jw);
			}

			var restored = new BlueprintNoteData();
			Assert.True(restored.ReadDataJson(JObject.Parse(sb.ToString())));
			return restored;
		}

		[RequiresGameInstallFact]
		public void TextNoteRoundTrips()
		{
			var source = BlueprintNoteData.CreateTextNote(
				new Vector2I(3, 4), "A title", "Some body text", "symbol_id", Color.red);

			var restored = RoundTrip(source);

			Assert.Equal(BlueprintNoteData.NoteType.Text, restored.Type);
			Assert.Equal(new Vector2I(3, 4), restored.Location);
			Assert.Equal("A title", restored.Title);
			Assert.Equal("Some body text", restored.Text);
			Assert.Equal("symbol_id", restored.Symbol);
			Assert.Equal(source.SymbolTint.r, restored.SymbolTint.r, 2);
			Assert.Equal(source.SymbolTint.g, restored.SymbolTint.g, 2);
			Assert.Equal(source.SymbolTint.b, restored.SymbolTint.b, 2);
		}

		[RequiresGameInstallFact]
		public void ElementNoteRoundTrips()
		{
			var source = BlueprintNoteData.CreateElementNote(
				new Vector2I(7, 2), SimHashes.Oxygen, 123.5f, 296.15f);

			var restored = RoundTrip(source);

			Assert.Equal(BlueprintNoteData.NoteType.Element, restored.Type);
			Assert.Equal(new Vector2I(7, 2), restored.Location);
			Assert.Equal(SimHashes.Oxygen, restored.ElementId);
			Assert.Equal(123.5f, restored.ElementMass, 3);
			Assert.Equal(296.15f, restored.ElementTemperature, 2);
		}

		[RequiresGameInstallFact]
		public void GetCloneCopiesEveryField()
		{
			var source = BlueprintNoteData.CreateElementNote(
				new Vector2I(1, 1), SimHashes.Water, 42f, 300f);

			var clone = typeof(BlueprintNoteData)
				.GetMethod("GetClone", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
				.Invoke(source, null) as BlueprintNoteData;

			Assert.NotNull(clone);
			Assert.NotSame(source, clone);
			Assert.Equal(source.Location, clone!.Location);
			Assert.Equal(source.Type, clone.Type);
			Assert.Equal(source.ElementId, clone.ElementId);
			Assert.Equal(source.ElementMass, clone.ElementMass);
			Assert.Equal(source.ElementTemperature, clone.ElementTemperature);
		}
	}
