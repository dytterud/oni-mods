using System.Reflection;
using System.Text;
using BlueprintsV2.BlueprintData;
using Xunit;

namespace BlueprintsIncluded.Tests.BlueprintData;

	/// <summary>
	/// Locks the pure collection behaviour of <see cref="BlueprintFolder"/>: de-duplication,
	/// insertion-order tracking, index lookup, and the deliberately-inverted next/previous
	/// navigation (<c>SelectNext</c> walks the backing list <em>backwards</em>).
	///
	/// Gated like <c>BlueprintRoundTripTests</c>: constructing a <see cref="Blueprint"/> pulls in
	/// <c>Vector2I</c> / <c>Tag</c> from the reference-only <c>Assembly-CSharp-firstpass</c>, so
	/// these run only against a real ONI install and skip in the offline build.
	///
	/// <see cref="Blueprint"/> instances are built through the <c>StringBuilder("{}")</c>
	/// constructor (JSON parse only, no Klei calls) and given a unique <c>FilePath</c> via
	/// <see cref="Blueprint.SetRandomSnapshotId"/> so that <see cref="Blueprint"/>'s
	/// FilePath-based equality keeps them distinct. No ONI install is required.
	///
	/// The folder-deletion branch of <see cref="BlueprintFolder.RemoveBlueprint"/> (touches the
	/// filesystem and <c>ModAssets</c> globals) is out of scope — every case here either keeps
	/// the folder non-empty or passes <c>deleteIfEmpty: false</c>.
	///
	/// <c>BlueprintFolder.SelectNext</c> / <c>SelectPrev</c> read and write the internal
	/// <c>ModAssets.SelectedBlueprint</c> static; the test project has no
	/// <c>InternalsVisibleTo</c>, so it is reached by reflection.
	/// </summary>
	public class BlueprintFolderTests
	{
		private static readonly FieldInfo SelectedBlueprintField = typeof(Blueprint).Assembly
			.GetType("BlueprintsV2.ModAssets")!
			.GetField("SelectedBlueprint", BindingFlags.Public | BindingFlags.Static)!;

		private static Blueprint? SelectedBlueprint
		{
			get => (Blueprint?)SelectedBlueprintField.GetValue(null);
			set => SelectedBlueprintField.SetValue(null, value);
		}

		private static Blueprint NewBlueprint()
		{
			var bp = new Blueprint(new StringBuilder("{}"));
			bp.SetRandomSnapshotId();
			return bp;
		}

		private static bool InvokeHasNext(BlueprintFolder folder, Blueprint? bp) =>
			(bool)typeof(BlueprintFolder)
				.GetMethod("HasNextBlueprint", BindingFlags.NonPublic | BindingFlags.Instance)!
				.Invoke(folder, new object?[] { bp })!;

		private static bool InvokeHasPrev(BlueprintFolder folder, Blueprint? bp) =>
			(bool)typeof(BlueprintFolder)
				.GetMethod("HasPrevBlueprint", BindingFlags.NonPublic | BindingFlags.Instance)!
				.Invoke(folder, new object?[] { bp })!;

		private static void InvokeSelectNext(BlueprintFolder folder) =>
			typeof(BlueprintFolder).GetMethod("SelectNext", BindingFlags.NonPublic | BindingFlags.Instance)!
				.Invoke(folder, null);

		private static void InvokeSelectPrev(BlueprintFolder folder) =>
			typeof(BlueprintFolder).GetMethod("SelectPrev", BindingFlags.NonPublic | BindingFlags.Instance)!
				.Invoke(folder, null);

		[RequiresGameInstallFact]
		public void AddBlueprint_IsIdempotent()
		{
			var folder = new BlueprintFolder("f");
			var bp = NewBlueprint();

			folder.AddBlueprint(bp);
			folder.AddBlueprint(bp);

			Assert.Equal(1, folder.BlueprintCount);
			Assert.True(folder.HasBlueprints);
			Assert.True(folder.ContainsBlueprint(bp));
		}

		[RequiresGameInstallFact]
		public void AddBlueprint_PreservesInsertionOrder()
		{
			var folder = new BlueprintFolder("f");
			var a = NewBlueprint();
			var b = NewBlueprint();
			var c = NewBlueprint();

			folder.AddBlueprint(a);
			folder.AddBlueprint(b);
			folder.AddBlueprint(c);

			Assert.Equal(0, folder.GetBlueprintIndex(a));
			Assert.Equal(1, folder.GetBlueprintIndex(b));
			Assert.Equal(2, folder.GetBlueprintIndex(c));
		}

		[RequiresGameInstallFact]
		public void GetBlueprintIndex_ReturnsMinusOne_ForNullOrAbsent()
		{
			var folder = new BlueprintFolder("f");
			folder.AddBlueprint(NewBlueprint());

			Assert.Equal(-1, folder.GetBlueprintIndex(null));
			Assert.Equal(-1, folder.GetBlueprintIndex(NewBlueprint()));
		}

		[RequiresGameInstallFact]
		public void RemoveBlueprint_WithoutDeleteIfEmpty_JustEmptiesTheFolder()
		{
			var folder = new BlueprintFolder("f");
			var a = NewBlueprint();
			var b = NewBlueprint();
			folder.AddBlueprint(a);
			folder.AddBlueprint(b);

			folder.RemoveBlueprint(a, deleteIfEmpty: false);

			Assert.Equal(1, folder.BlueprintCount);
			Assert.False(folder.ContainsBlueprint(a));
			Assert.Equal(0, folder.GetBlueprintIndex(b));

			folder.RemoveBlueprint(b, deleteIfEmpty: false);
			Assert.Equal(0, folder.BlueprintCount);
			Assert.False(folder.HasBlueprints);
		}

		[RequiresGameInstallFact]
		public void HasNext_IsFalseAtTheFrontOfTheList_TrueFurtherBack()
		{
			var folder = new BlueprintFolder("f");
			var first = NewBlueprint();
			var last = NewBlueprint();
			folder.AddBlueprint(first);
			folder.AddBlueprint(last);

			// "Next" walks the list towards index 0, so the first-added blueprint has no next.
			Assert.False(InvokeHasNext(folder, first));
			Assert.True(InvokeHasNext(folder, last));
		}

		[RequiresGameInstallFact]
		public void HasPrev_IsFalseAtTheBackOfTheList_TrueFurtherForward()
		{
			var folder = new BlueprintFolder("f");
			var first = NewBlueprint();
			var last = NewBlueprint();
			folder.AddBlueprint(first);
			folder.AddBlueprint(last);

			Assert.True(InvokeHasPrev(folder, first));
			Assert.False(InvokeHasPrev(folder, last));
		}

		[RequiresGameInstallFact]
		public void HasNext_HasPrev_AreFalse_ForNullOrAbsentBlueprint()
		{
			var folder = new BlueprintFolder("f");
			folder.AddBlueprint(NewBlueprint());

			Assert.False(InvokeHasNext(folder, null));
			Assert.False(InvokeHasPrev(folder, null));
			Assert.False(InvokeHasNext(folder, NewBlueprint()));
			Assert.False(InvokeHasPrev(folder, NewBlueprint()));
		}

		[RequiresGameInstallFact]
		public void SelectNext_MovesTowardsIndexZero_AndStopsAtTheEdge()
		{
			var folder = new BlueprintFolder("f");
			var a = NewBlueprint();
			var b = NewBlueprint();
			folder.AddBlueprint(a);
			folder.AddBlueprint(b);

			SelectedBlueprint = b;
			InvokeSelectNext(folder);
			Assert.Same(a, SelectedBlueprint);

			// already at index 0 — no next, selection unchanged
			InvokeSelectNext(folder);
			Assert.Same(a, SelectedBlueprint);
		}

		[RequiresGameInstallFact]
		public void SelectPrev_MovesAwayFromIndexZero_AndStopsAtTheEdge()
		{
			var folder = new BlueprintFolder("f");
			var a = NewBlueprint();
			var b = NewBlueprint();
			folder.AddBlueprint(a);
			folder.AddBlueprint(b);

			SelectedBlueprint = a;
			InvokeSelectPrev(folder);
			Assert.Same(b, SelectedBlueprint);

			InvokeSelectPrev(folder);
			Assert.Same(b, SelectedBlueprint);
		}

		[RequiresGameInstallFact]
		public void Equality_IsByNameOnly()
		{
			var a = new BlueprintFolder("shared");
			var b = new BlueprintFolder("shared");
			var c = new BlueprintFolder("other");
			a.AddBlueprint(NewBlueprint()); // contents must not affect equality

			Assert.True(a.Equals(b));
			Assert.True(a == b);
			Assert.False(a != b);
			Assert.False(a == c);
			Assert.True(a != c);
			Assert.Equal(a.GetHashCode(), b.GetHashCode());
		}

		[RequiresGameInstallFact]
		public void Equality_HandlesNull()
		{
			var a = new BlueprintFolder("f");

			Assert.False(a.Equals(null));
			Assert.False(a == null);
			Assert.False(null == a);
			Assert.True(a != null);
		}
	}
