using BlueprintsV2.BlueprintData;
using Newtonsoft.Json;
using Xunit;

namespace BlueprintsIncluded.Tests.BlueprintData
{
	public class Vector2IConverterTests
	{
		private static readonly JsonSerializerSettings Settings = new()
		{
			Converters = { new Vector2IConverter() },
		};

		[RequiresGameInstallTheory]
		[InlineData(0, 0)]
		[InlineData(3, -7)]
		[InlineData(-100, 250)]
		public void RoundTrips(int x, int y)
		{
			var original = new Vector2I(x, y);

			string json = JsonConvert.SerializeObject(original, Settings);
			var restored = JsonConvert.DeserializeObject<Vector2I>(json, Settings);

			Assert.Equal(original.x, restored.x);
			Assert.Equal(original.y, restored.y);
		}

		[RequiresGameInstallFact]
		public void WritesXAndYProperties()
		{
			string json = JsonConvert.SerializeObject(new Vector2I(4, 9), Settings);

			Assert.Equal("{\"x\":4,\"y\":9}", json);
		}

		[RequiresGameInstallFact]
		public void MissingComponentsDefaultToZero()
		{
			var restored = JsonConvert.DeserializeObject<Vector2I>("{\"x\":5}", Settings);

			Assert.Equal(5, restored.x);
			Assert.Equal(0, restored.y);
		}
	}
}
