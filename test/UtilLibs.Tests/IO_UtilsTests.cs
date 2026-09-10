using System;
using System.IO;
using UtilLibs;
using Xunit;

namespace UtilLibs.Tests
{
	/// <summary>
	/// Locks the JSON-on-disk helpers in <see cref="IO_Utils"/>: every mod config, blueprint
	/// index and dumped debug file goes through them, and each entry point swallows its
	/// exceptions and reports a <see cref="bool"/>, so a regression is silent at runtime.
	///
	/// Runs offline. <see cref="IO_Utils"/> only reaches Klei types from
	/// <c>ModsFolder</c> / <c>ConfigsFolder</c> / <c>DumpToFile</c> (none of which are touched
	/// here), and its logging goes through <see cref="SgtLogger"/>, which writes to
	/// <see cref="Console"/> rather than <c>UnityEngine.Debug</c>.
	///
	/// <c>DeepClone</c> is deliberately not covered: it uses <c>BinaryFormatter</c>, which
	/// compiles for <c>netstandard2.1</c> but throws on the net8.0 test runtime.
	/// </summary>
	public sealed class IO_UtilsTests : IDisposable
	{
		private sealed class Payload
		{
			public string Name { get; set; } = "";
			public int Count { get; set; }
		}

		private readonly string dir;

		public IO_UtilsTests()
		{
			dir = Path.Combine(Path.GetTempPath(), "BlueprintsIncluded.Tests", Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(dir);
		}

		public void Dispose()
		{
			try
			{
				Directory.Delete(dir, recursive: true);
			}
			catch (IOException)
			{
				// a leaked temp directory must not fail the run
			}
		}

		private string InDir(string name) => Path.Combine(dir, name);

		private string WriteRaw(string name, string content)
		{
			string path = InDir(name);
			File.WriteAllText(path, content);
			return path;
		}

		[Fact]
		public void WriteToFile_ThenReadFromFile_RoundTripsThePayload()
		{
			string path = InDir("payload.json");

			Assert.True(IO_Utils.WriteToFile(new Payload { Name = "blueprint", Count = 7 }, path));
			Assert.True(IO_Utils.ReadFromFile<Payload>(path, out var restored));

			Assert.Equal("blueprint", restored.Name);
			Assert.Equal(7, restored.Count);
		}

		[Fact]
		public void WriteToFile_ReturnsFalse_WhenTheParentDirectoryDoesNotExist()
		{
			string path = Path.Combine(dir, "missing-subdirectory", "payload.json");

			Assert.False(IO_Utils.WriteToFile(new Payload { Name = "x", Count = 1 }, path));
			Assert.False(File.Exists(path));
		}

		[Fact]
		public void ReadFromFile_MissingFile_ReturnsFalseAndDefault()
		{
			Assert.False(IO_Utils.ReadFromFile<Payload>(InDir("absent.json"), out var restored));
			Assert.Null(restored);
		}

		[Fact]
		public void ReadFromFile_MalformedJson_ReturnsFalseAndDefault()
		{
			string path = WriteRaw("broken.json", "{ this is not json");

			Assert.False(IO_Utils.ReadFromFile<Payload>(path, out var restored));
			Assert.Null(restored);
		}

		[Fact]
		public void ReadFromFile_MatchingForcedExtension_Reads()
		{
			string path = WriteRaw("config.json", "{\"Name\":\"ok\",\"Count\":1}");

			Assert.True(IO_Utils.ReadFromFile<Payload>(path, out var restored, forceExtensionTo: ".json"));
			Assert.Equal("ok", restored.Name);
		}

		[Fact]
		public void ReadFromFile_MismatchedForcedExtension_ReturnsFalse()
		{
			string path = WriteRaw("config.txt", "{\"Name\":\"ok\",\"Count\":1}");

			Assert.False(IO_Utils.ReadFromFile<Payload>(path, out var restored, forceExtensionTo: ".json"));
			Assert.Null(restored);
		}

		/// <summary>
		/// The guard reads <c>!Exists || (extensionMismatch &amp;&amp; !isAppleDoubleFile)</c> -
		/// <c>&amp;&amp;</c> binds tighter than <c>||</c>, so an AppleDouble sidecar ("._name")
		/// escapes the extension check entirely and is parsed as JSON. Pinned because the
		/// intent reads as the opposite: those files are macOS metadata and should be skipped.
		/// </summary>
		[Fact]
		public void ReadFromFile_AppleDoubleName_BypassesTheForcedExtensionCheck()
		{
			string path = WriteRaw("._config.txt", "{\"Name\":\"read anyway\",\"Count\":2}");

			Assert.True(IO_Utils.ReadFromFile<Payload>(path, out var restored, forceExtensionTo: ".json"));
			Assert.Equal("read anyway", restored.Name);
		}

		[Fact]
		public void DeleteFile_RemovesAnExistingFile()
		{
			string path = WriteRaw("doomed.json", "{}");

			Assert.True(IO_Utils.DeleteFile(path));
			Assert.False(File.Exists(path));
		}

		/// <summary>
		/// <c>FileInfo.Delete</c> is a no-op on a missing file, so "delete" reports success for
		/// something that was never there - callers cannot use the result to detect a stale path.
		/// </summary>
		[Fact]
		public void DeleteFile_ReturnsTrue_ForAMissingFile()
		{
			Assert.True(IO_Utils.DeleteFile(InDir("never-existed.json")));
		}

		[Fact]
		public void DeleteFile_ReturnsFalse_WhenThePathIsADirectory()
		{
			string path = InDir("a-directory");
			Directory.CreateDirectory(path);

			Assert.False(IO_Utils.DeleteFile(path));
			Assert.True(Directory.Exists(path));
		}

		[Theory]
		[InlineData("._Icon", true)]
		[InlineData("._blueprint.json", true)]
		[InlineData("_blueprint.json", false)]
		[InlineData(".blueprint", false)]
		[InlineData("blueprint._json", false)]
		public void MacFile_MatchesOnlyTheAppleDoublePrefix(string fileName, bool expected)
		{
			var fileInfo = new FileInfo(InDir(fileName));

			Assert.Equal(expected, IO_Utils.MacFile(fileInfo));
			Assert.Equal(!expected, IO_Utils.NotAMacFile(fileInfo));
		}
	}
}
