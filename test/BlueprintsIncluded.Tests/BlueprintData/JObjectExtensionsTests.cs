using BlueprintsV2.BlueprintData;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace BlueprintsIncluded.Tests.BlueprintData;

	/// <summary>
	/// Locks the behaviour of <see cref="JObjectExtensions"/>: the read helpers must be a 1:1
	/// replacement for the <c>GetValue</c> / null-check / <c>Value&lt;T&gt;()</c> pattern that
	/// <see cref="DataTransferHelpers"/> used to repeat for every stored field.
	///
	/// Uses only BCL / POCO shapes, so no ONI install is required.
	/// </summary>
	public class JObjectExtensionsTests
	{
		private sealed class ColorDto
		{
			public float r, g, b, a;

			public override bool Equals(object? obj) =>
				obj is ColorDto o && o.r == r && o.g == g && o.b == b && o.a == a;

			public override int GetHashCode() => (r, g, b, a).GetHashCode();
		}

		[Fact]
		public void TryGet_MissingKey_ReturnsFalseAndDefault()
		{
			var obj = new JObject { ["present"] = 1 };

			Assert.False(obj.TryGet<int>("absent", out var value));
			Assert.Equal(0, value);
		}

		[Fact]
		public void TryGet_NullReceiver_ReturnsFalse()
		{
			JObject obj = null!;

			Assert.False(obj.TryGet<int>("anything", out var value));
			Assert.Equal(0, value);
		}

		[Fact]
		public void TryGet_PresentValues_ConvertPerType()
		{
			var obj = new JObject
			{
				["i"] = 42,
				["f"] = 1.5f,
				["b"] = true,
				["s"] = "hello",
			};

			Assert.True(obj.TryGet<int>("i", out var i));
			Assert.Equal(42, i);
			Assert.True(obj.TryGet<float>("f", out var f));
			Assert.Equal(1.5f, f);
			Assert.True(obj.TryGet<bool>("b", out var b));
			Assert.True(b);
			Assert.True(obj.TryGet<string>("s", out var s));
			Assert.Equal("hello", s);
		}

		[Fact]
		public void TryGet_CoercesStringToNumber_LikeNewtonsoft()
		{
			var obj = new JObject { ["n"] = "5" };

			Assert.True(obj.TryGet<int>("n", out var n));
			Assert.Equal(5, n);
		}

		[Fact]
		public void GetOr_ReturnsFallbackWhenMissing_ValueWhenPresent()
		{
			var obj = new JObject { ["here"] = 7 };

			Assert.Equal(7, obj.GetOr("here", -1));
			Assert.Equal(-1, obj.GetOr("gone", -1));
		}

		[Fact]
		public void TryGetEmbedded_RoundTripsNestedNode()
		{
			var payload = new List<KeyValuePair<int, int>> { new(1, 10), new(2, 20) };
			var obj = new JObject { ["k"] = EmbeddedJson.From(payload) };

			var reparsed = JObject.Parse(obj.ToString(Formatting.None));
			Assert.True(reparsed.TryGetEmbedded<List<KeyValuePair<int, int>>>("k", out var result));
			Assert.Equal(JsonConvert.SerializeObject(payload), JsonConvert.SerializeObject(result));
		}

		[Fact]
		public void TryGetEmbedded_DecodesLegacyEscapedStringForm()
		{
			var payload = new[]
			{
				new ColorDto { r = 0.1f, g = 0.2f, b = 0.3f, a = 1f },
				new ColorDto { r = 1f, g = 0f, b = 0.5f, a = 0.25f },
			};
			// legacy blueprints stored the value as an escaped JSON string
			var obj = new JObject { ["k"] = JsonConvert.SerializeObject(payload) };

			Assert.True(obj.TryGetEmbedded<ColorDto[]>("k", out var result));
			Assert.Equal(2, result.Length);
			Assert.Equal(payload[0], result[0]);
			Assert.Equal(payload[1], result[1]);
		}

		[Fact]
		public void TryGetEmbedded_MissingKey_ReturnsFalseAndDefault()
		{
			var obj = new JObject();

			Assert.False(obj.TryGetEmbedded<List<int>>("absent", out var result));
			Assert.Null(result);
		}
	}
