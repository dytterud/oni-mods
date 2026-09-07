using System.Linq;
using UtilLibs;
using Xunit;

namespace UtilLibs.Tests
{
	public class ExtensionsTests
	{
		[Theory]
		[InlineData("")]
		[InlineData("hello world")]
		[InlineData("{\"blueprintVersion\":7,\"buildings\":[]}")]
		public void CompressString_RoundTrips(string original)
		{
			string restored = original.CompressString().DecompressString();

			Assert.Equal(original, restored);
		}

		[Fact]
		public void CompressString_ProducesDifferentPayloadForDifferentInput()
		{
			Assert.NotEqual("aaaa".CompressString(), "bbbb".CompressString());
		}

		[Fact]
		public void DecompressString_ReturnsEmptyOnGarbage()
		{
			Assert.Equal(string.Empty, "not-base64-!!".DecompressString());
		}

		[Fact]
		public void DistinctBy_KeepsFirstOfEachKey()
		{
			var input = new[]
			{
				(Id: 1, Name: "a"),
				(Id: 2, Name: "b"),
				(Id: 1, Name: "c"),
			};

			var result = input.DistinctBy(x => x.Id).ToList();

			Assert.Equal(new[] { "a", "b" }, result.Select(x => x.Name));
		}
	}
}
