using System.Reflection;
using System.Text;
using BlueprintsV2.BlueprintData;
using Xunit;

namespace BlueprintsIncluded.Tests.BlueprintData;

	/// <summary>
	/// Locks <see cref="Blueprint"/>'s identity: equality, <c>==</c> / <c>!=</c> and
	/// <see cref="object.GetHashCode"/> are all defined purely by <c>FilePath</c>. That makes the
	/// on-disk path the primary key for every collection of blueprints - most importantly the
	/// <c>HashSet&lt;Blueprint&gt;</c> backing <see cref="BlueprintFolder"/>, whose de-duplication
	/// tests (<c>BlueprintFolderTests</c>) depend on this contract without asserting it.
	///
	/// The consequence worth knowing about is covered below: <c>FilePath</c> defaults to the
	/// empty string, so blueprints that have never been given a location are all equal to each
	/// other and collapse into one entry in a set. Callers that hold unsaved blueprints must
	/// assign a path first - <see cref="Blueprint.SetRandomSnapshotId"/> is what the snapshot
	/// tool uses for exactly this reason.
	///
	/// Gated like <c>BlueprintRoundTripTests</c>: <see cref="Blueprint"/>'s field types
	/// (<c>Vector2I</c>, <c>Tag</c>) live in the reference-only <c>Assembly-CSharp-firstpass</c>,
	/// so JITing anything on the type needs the real game assemblies.
	///
	/// Blueprints are built through the <c>StringBuilder("{}")</c> constructor (a JSON parse, no
	/// Klei calls); <c>FilePath</c> has a private setter, and the test project has no
	/// <c>InternalsVisibleTo</c>, so it is assigned by reflection.
	/// </summary>
	public class BlueprintEqualityTests
	{
		private static readonly MethodInfo SetFilePath = typeof(Blueprint)
			.GetProperty(nameof(Blueprint.FilePath))!
			.GetSetMethod(nonPublic: true)!;

		private static Blueprint NewBlueprint(string? filePath = null)
		{
			var blueprint = new Blueprint(new StringBuilder("{}"));
			if (filePath != null)
				SetFilePath.Invoke(blueprint, new object[] { filePath });
			return blueprint;
		}

		[RequiresGameInstallFact]
		public void Equality_IsByFilePathOnly()
		{
			var a = NewBlueprint("/blueprints/base.blueprint");
			var b = NewBlueprint("/blueprints/base.blueprint");

			// everything else about them differs
			a.FriendlyName = "Original";
			b.FriendlyName = "A completely different name";
			b.UserDescription = "and a description";

			Assert.True(a.Equals(b));
			Assert.True(a == b);
			Assert.False(a != b);
			Assert.Equal(a.GetHashCode(), b.GetHashCode());
		}

		[RequiresGameInstallFact]
		public void Equality_DistinguishesDifferentFilePaths()
		{
			var a = NewBlueprint("/blueprints/one.blueprint");
			var b = NewBlueprint("/blueprints/two.blueprint");

			// ...even when everything else matches
			a.FriendlyName = b.FriendlyName = "Same Name";

			Assert.False(a.Equals(b));
			Assert.False(a == b);
			Assert.True(a != b);
		}

		[RequiresGameInstallFact]
		public void Equality_IsCaseSensitive_EvenOnWindows()
		{
			var lower = NewBlueprint("/blueprints/base.blueprint");
			var upper = NewBlueprint("/blueprints/BASE.blueprint");

			Assert.False(lower == upper);
		}

		[RequiresGameInstallFact]
		public void Equals_IsFalse_ForNullAndForOtherTypes()
		{
			var blueprint = NewBlueprint("/blueprints/one.blueprint");

			Assert.False(blueprint.Equals(null));
			Assert.False(blueprint.Equals((object)"/blueprints/one.blueprint"));
		}

		/// <summary>
		/// <c>operator ==</c> is null-propagating (<c>a?.FilePath == b?.FilePath</c>) rather than
		/// delegating to <see cref="Blueprint.Equals(Blueprint)"/>, so two nulls compare equal
		/// while <c>Equals(null)</c> is false. Standard for the pattern, but the two are not
		/// interchangeable at a null.
		/// </summary>
		[RequiresGameInstallFact]
		public void OperatorEquals_TreatsTwoNullsAsEqual_AndNeverThrows()
		{
			Blueprint? nothing = null;
			var blueprint = NewBlueprint("/blueprints/one.blueprint");

			Assert.True(nothing == null);
			Assert.False(blueprint == null);
			Assert.False(null == blueprint);
			Assert.True(blueprint != null);
		}

		/// <summary>
		/// The headline consequence: <c>FilePath</c> starts as <c>""</c>, so every blueprint that
		/// has not been saved or given a snapshot id is equal to every other one.
		/// </summary>
		[RequiresGameInstallFact]
		public void UnsavedBlueprints_ShareTheDefaultFilePath_AndCompareEqual()
		{
			var a = NewBlueprint();
			var b = NewBlueprint();

			Assert.Equal("", a.FilePath);
			Assert.True(a == b);
			Assert.Equal(a.GetHashCode(), b.GetHashCode());
		}

		[RequiresGameInstallFact]
		public void AHashSet_CollapsesUnsavedBlueprints_UntilTheyAreGivenAnId()
		{
			var a = NewBlueprint();
			var b = NewBlueprint();

			Assert.Single(new HashSet<Blueprint> { a, b });

			a.SetRandomSnapshotId();
			b.SetRandomSnapshotId();

			Assert.Equal(2, new HashSet<Blueprint> { a, b }.Count);
		}

		[RequiresGameInstallFact]
		public void SetRandomSnapshotId_ChangesIdentity()
		{
			var blueprint = NewBlueprint("/blueprints/one.blueprint");
			var twin = NewBlueprint("/blueprints/one.blueprint");

			blueprint.SetRandomSnapshotId();

			Assert.False(blueprint == twin);
		}
	}
