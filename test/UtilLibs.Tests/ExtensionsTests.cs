using System;
using System.Linq;
using System.Text;
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

		// A blueprint's clipboard payload is one of these strings, and a real one is far larger
		// than the cases above - big enough that the decompressor's read can come back short.
		[Theory]
		[InlineData(50_000)]
		[InlineData(400_000)]
		public void CompressString_RoundTripsALargePayload(int length)
		{
			// Repeating text compresses hard, which is realistic here (blueprint JSON is highly
			// repetitive) and keeps the compressed form small while the decompressed form is not.
			var original = new StringBuilder(length);
			while (original.Length < length)
				original.Append("{\"buildingdef\":\"Tile\",\"offset\":{\"x\":1,\"y\":2}},");

			string restored = original.ToString().CompressString().DecompressString();

			Assert.Equal(original.ToString(), restored);
		}

		// The four-byte length prefix comes off the clipboard, so it is unvalidated input. These
		// three cases all used to size the output buffer directly from it: too large NUL-padded the
		// result, too small truncated it, and int.MaxValue asked for a 2GB allocation that threw and
		// was swallowed into an empty string. The payload itself is intact in every case, so the
		// only correct answer is the original text.
		[Theory]
		[InlineData(1000)]      // longer than the real payload -> used to NUL-pad
		[InlineData(3)]         // shorter -> used to truncate
		[InlineData(int.MaxValue)]  // hostile -> used to attempt a 2GB allocation
		public void DecompressString_IgnoresAWrongLengthPrefix(int claimedLength)
		{
			const string original = "hello clipboard";
			byte[] payload = Convert.FromBase64String(original.CompressString());
			Buffer.BlockCopy(BitConverter.GetBytes(claimedLength), 0, payload, 0, 4);

			string restored = Convert.ToBase64String(payload).DecompressString();

			Assert.Equal(original, restored);
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
