using System.IO;
using BlueprintsV2.BlueprintData;
using Xunit;

namespace BlueprintsIncluded.Tests.BlueprintData;

	/// <summary>
	/// Locks the behaviour of <see cref="Blueprint.SanitizeFolder"/> /
	/// <see cref="Blueprint.SanitizeFile"/>: both feed on-disk file and directory names, so a
	/// regression here silently mangles or collides blueprint files across Windows / Mac / Linux.
	///
	/// The logic is plain string work, but the methods are static members of <see cref="Blueprint"/>,
	/// whose field types (<c>Vector2I</c>, <c>Tag</c>) live in the reference-only
	/// <c>Assembly-CSharp-firstpass</c>. JITing anything on the type therefore needs the real
	/// game assemblies, so these are gated exactly like <c>BlueprintRoundTripTests</c>.
	/// </summary>
	public class BlueprintSanitizeTests
	{
		private static readonly char Sep = Path.DirectorySeparatorChar;

		[RequiresGameInstallFact]
		public void SanitizeFolder_EmptyString_IsUnchanged()
		{
			Assert.Equal("", Blueprint.SanitizeFolder(""));
		}

		[RequiresGameInstallFact]
		public void SanitizeFolder_NormalisesBothSeparatorsToTheSystemSeparator()
		{
			Assert.Equal($"a{Sep}b{Sep}c", Blueprint.SanitizeFolder("a/b\\c"));
		}

		[RequiresGameInstallFact]
		public void SanitizeFolder_DropsEmptyAndWhitespaceOnlySections()
		{
			Assert.Equal($"a{Sep}b", Blueprint.SanitizeFolder("a//b"));
			Assert.Equal($"a{Sep}b", Blueprint.SanitizeFolder($"a{Sep}   {Sep}b"));
		}

		[RequiresGameInstallFact]
		public void SanitizeFolder_TrimsTrailingSeparator()
		{
			Assert.Equal($"a{Sep}b", Blueprint.SanitizeFolder($"a{Sep}b{Sep}"));
		}

		[RequiresGameInstallFact]
		public void SanitizeFolder_SanitisesEachSection()
		{
			// an invalid file-name character inside a section is replaced before the section is kept
			char bad = Path.GetInvalidFileNameChars()[0];
			Assert.Equal($"na_me{Sep}sub", Blueprint.SanitizeFolder($"na{bad}me/sub"));
		}

		[RequiresGameInstallFact]
		public void SanitizeFolder_NestedPathRoundTrips()
		{
			Assert.Equal($"one{Sep}two{Sep}three", Blueprint.SanitizeFolder("one/two/three"));
		}

		[RequiresGameInstallFact]
		public void SanitizeFile_ReplacesEveryDisallowedCharacterWithUnderscore()
		{
			var invalid = Path.GetInvalidFileNameChars();
			var name = "a" + string.Concat(invalid) + "b";
			var expected = "a" + new string('_', invalid.Length) + "b";
			Assert.Equal(expected, Blueprint.SanitizeFile(name));
		}

		[RequiresGameInstallFact]
		public void SanitizeFile_KeepsAllowedCharactersAndTrims()
		{
			Assert.Equal("my blueprint 1", Blueprint.SanitizeFile("  my blueprint 1  "));
		}

		[RequiresGameInstallFact]
		public void SanitizeFile_StripsLeadingDotUnderscore()
		{
			// macOS/exFAT AppleDouble files are "._name" — a blueprint must not collide with them.
			Assert.Equal("name", Blueprint.SanitizeFile("._name"));
		}

		[RequiresGameInstallTheory]
		[InlineData("")]
		[InlineData("   ")]
		public void SanitizeFile_EmptyOrWhitespace_BecomesUnnamed(string input)
		{
			Assert.Equal("unnamed", Blueprint.SanitizeFile(input));
		}
	}
