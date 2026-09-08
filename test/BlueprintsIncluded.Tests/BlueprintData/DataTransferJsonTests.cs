using System;
using System.Collections.Generic;
using BlueprintsV2.BlueprintData;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace BlueprintsIncluded.Tests.BlueprintData
{
	/// <summary>
	/// Locks the behaviour of <see cref="EmbeddedJson"/>: values embedded in the per-building
	/// <see cref="JObject"/> must round-trip as real nested nodes, and the reader must still
	/// accept the legacy "escaped JSON string" form that existing blueprint files contain.
	///
	/// Uses only BCL / POCO shapes, so no ONI install is required.
	/// </summary>
	public class DataTransferJsonTests
	{
		private sealed class ColorDto
		{
			public float r, g, b, a;

			public override bool Equals(object? obj) =>
				obj is ColorDto o && o.r == r && o.g == g && o.b == b && o.a == a;

			public override int GetHashCode() => (r, g, b, a).GetHashCode();
		}

		// each case: (build a legacy string container, build a new nested container, assert the value)
		private static void AssertRoundTrips<T>(T payload, Func<T, T, bool> equal)
		{
			string expectedJson = JsonConvert.SerializeObject(payload);

			// legacy form: the value was JsonConvert.SerializeObject'd into a string first
			var legacy = new JObject { ["k"] = JsonConvert.SerializeObject(payload) };
			T? fromLegacy = EmbeddedJson.To<T>(legacy["k"]);
			Assert.NotNull(fromLegacy);
			Assert.Equal(expectedJson, JsonConvert.SerializeObject(fromLegacy));
			Assert.True(equal(payload, fromLegacy));

			// new form: real nested node, pushed through a write-to-file / read-from-file cycle
			var nested = new JObject { ["k"] = EmbeddedJson.From(payload) };
			Assert.NotEqual(JTokenType.String, nested["k"]!.Type);
			var reparsed = JObject.Parse(nested.ToString(Formatting.None));
			T? fromNested = EmbeddedJson.To<T>(reparsed["k"]);
			Assert.NotNull(fromNested);
			Assert.Equal(expectedJson, JsonConvert.SerializeObject(fromNested));
			Assert.True(equal(payload, fromNested));
		}

		[Fact]
		public void RoundTrips_ListOfTuples()
		{
			var payload = new List<System.Tuple<int, int>>
			{
				System.Tuple.Create(1, 2),
				System.Tuple.Create(-3, 4),
			};
			AssertRoundTrips(payload, (a, b) => a.Count == b.Count && a[0].Equals(b[0]) && a[1].Equals(b[1]));
		}

		[Fact]
		public void RoundTrips_ArrayOfObjects()
		{
			var payload = new[]
			{
				new ColorDto { r = 0.1f, g = 0.2f, b = 0.3f, a = 1f },
				new ColorDto { r = 1f, g = 0f, b = 0.5f, a = 0.25f },
			};
			AssertRoundTrips(payload, (a, b) => a.Length == b.Length && a[0].Equals(b[0]) && a[1].Equals(b[1]));
		}

		[Fact]
		public void RoundTrips_ListOfKeyValuePairs()
		{
			var payload = new List<KeyValuePair<int, int>> { new(1, 10), new(2, 20) };
			AssertRoundTrips(payload, (a, b) => JsonConvert.SerializeObject(a) == JsonConvert.SerializeObject(b));
		}

		[Fact]
		public void RoundTrips_Dictionary()
		{
			var payload = new Dictionary<string, int> { ["x"] = 1, ["y"] = 2 };
			AssertRoundTrips(payload, (a, b) => a["x"] == b["x"] && a["y"] == b["y"]);
		}

		[Fact]
		public void From_NullProducesJsonNull()
		{
			Assert.Equal(JTokenType.Null, EmbeddedJson.From(null).Type);
		}

		[Fact]
		public void To_NullOrJsonNullReturnsDefault()
		{
			Assert.Null(EmbeddedJson.To<List<int>>(null));
			Assert.Null(EmbeddedJson.To<List<int>>(JValue.CreateNull()));
		}
	}
}
