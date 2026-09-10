using System.Reflection;
using System.Runtime.CompilerServices;
using BlueprintsV2.BlueprintData;
using BlueprintsV2.Visualizers;
using Xunit;

namespace BlueprintsIncluded.Tests.Visualizers;

	/// <summary>
	/// Locks <see cref="BuildingVisual.RotateUtilityConnectionFlags"/>, the shift that decides which
	/// connection-stub art a conduit shows in a blueprint preview.
	///
	/// The contract that matters is the first test: the stored flags are <b>absolute</b>
	/// (world-space, captured per grid cell), so an <em>unrotated</em> blueprint must render them
	/// untouched. That regressed - the shift used to subtract the captured building's own
	/// <c>Orientation</c>, which is meaningless for a world-space mask and left rotated utility
	/// buildings drawing the wrong stubs until something forced a refresh (issue #4).
	///
	/// Gated on a real install: <c>UtilityConnections</c> and <see cref="BuildingVisual"/> come from
	/// the reference-only game assemblies. The method itself is static and free of game state, which
	/// is the whole reason it can be tested at all - a <see cref="BuildingVisual"/> needs a live
	/// colony to construct.
	///
	/// <para><b>Every <c>InlineData</c> here passes plain <c>int</c>s, never a Klei enum value.</b>
	/// xUnit resolves attribute arguments during *discovery*, before the install gate can skip
	/// anything, so a boxed <c>Orientation</c> in an attribute fails the offline build outright
	/// with a <c>FileNotFoundException</c> / <c>BadImageFormatException</c> instead of skipping -
	/// the third outcome CLAUDE.md warns about. Cast inside the test body instead.</para>
	/// </summary>
	public class UtilityConnectionRotationTests
	{
		private const int Left = (int)UtilityConnections.Left;
		private const int Right = (int)UtilityConnections.Right;
		private const int Up = (int)UtilityConnections.Up;
		private const int Down = (int)UtilityConnections.Down;

		private static int Rotate(int flags, int steps, bool flippedH = false, bool flippedV = false)
			=> BuildingVisual.RotateUtilityConnectionFlags(flags, steps, flippedH, flippedV);

		/// <summary>
		/// The regression guard for issue #4. An unrotated blueprint applies no shift at all - the
		/// captured building's own orientation must never enter this.
		/// </summary>
		[RequiresGameInstallTheory]
		[InlineData(0)]
		[InlineData(1)]  // Left
		[InlineData(3)]  // Left | Right
		[InlineData(12)] // Up | Down
		[InlineData(15)] // all four
		public void NoRotationAndNoFlips_LeavesTheFlagsUnchanged(int flags)
		{
			Assert.Equal(flags, Rotate(flags, 0));
		}

		[RequiresGameInstallFact]
		public void EmptyFlags_StayEmpty()
		{
			Assert.Equal(0, Rotate(0, -1));
			Assert.Equal(0, Rotate(0, 2, flippedH: true, flippedV: true));
		}

		[RequiresGameInstallTheory]
		[InlineData(-1)]
		[InlineData(1)]
		public void AQuarterTurn_TurnsAHorizontalRunVertical(int steps)
		{
			Assert.Equal(Up | Down, Rotate(Left | Right, steps));
		}

		[RequiresGameInstallTheory]
		[InlineData(-1)]
		[InlineData(1)]
		public void AQuarterTurn_TurnsAVerticalRunHorizontal(int steps)
		{
			Assert.Equal(Left | Right, Rotate(Up | Down, steps));
		}

		/// <summary>Pins the direction, which the symmetrical runs above cannot distinguish.</summary>
		[RequiresGameInstallFact]
		public void QuarterTurns_RotateASingleStubInOppositeDirections()
		{
			Assert.Equal(Up, Rotate(Left, -1));
			Assert.Equal(Down, Rotate(Left, 1));
		}

		[RequiresGameInstallTheory]
		[InlineData(Left)]
		[InlineData(Left | Up)]
		[InlineData(Left | Right | Down)]
		public void OppositeQuarterTurns_AreInverses(int flags)
		{
			Assert.Equal(flags, Rotate(Rotate(flags, -1), 1));
			Assert.Equal(flags, Rotate(Rotate(flags, 1), -1));
		}

		[RequiresGameInstallTheory]
		[InlineData(-4)]
		[InlineData(4)]
		public void FourQuarterTurns_ReturnToTheOriginal(int steps)
		{
			Assert.Equal(Left | Up, Rotate(Left | Up, steps));
		}

		[RequiresGameInstallFact]
		public void TwoQuarterTurns_MirrorBothAxes()
		{
			Assert.Equal(Right | Down, Rotate(Left | Up, 2));
			Assert.Equal(Right | Down, Rotate(Left | Up, -2));
		}

		[RequiresGameInstallFact]
		public void FlippedH_SwapsLeftAndRight_AndLeavesTheVerticalAxisAlone()
		{
			Assert.Equal(Right, Rotate(Left, 0, flippedH: true));
			Assert.Equal(Left, Rotate(Right, 0, flippedH: true));
			Assert.Equal(Up | Down, Rotate(Up | Down, 0, flippedH: true));
		}

		[RequiresGameInstallFact]
		public void FlippedV_SwapsUpAndDown_AndLeavesTheHorizontalAxisAlone()
		{
			Assert.Equal(Down, Rotate(Up, 0, flippedV: true));
			Assert.Equal(Up, Rotate(Down, 0, flippedV: true));
			Assert.Equal(Left | Right, Rotate(Left | Right, 0, flippedV: true));
		}

		[RequiresGameInstallFact]
		public void BothFlips_MirrorBothAxes()
		{
			Assert.Equal(Right | Down, Rotate(Left | Up, 0, flippedH: true, flippedV: true));
		}

		/// <summary>Flips are applied after the rotation, not before.</summary>
		[RequiresGameInstallFact]
		public void FlipsApplyAfterTheRotation()
		{
			// Left --(-1)--> Up, then flipping vertically gives Down
			Assert.Equal(Down, Rotate(Left, -1, flippedV: true));
			// flipping first would give Left --flipV--> Left --(-1)--> Up
			Assert.NotEqual(Up, Rotate(Left, -1, flippedV: true));
		}

		#region The instance method — the actual regression guard for issue #4

		/// <summary>
		/// Builds a <see cref="BuildingVisual"/> <em>without running its constructor</em>, which
		/// would need a live colony (it clones a preview prefab, reads the Grid, and registers an
		/// anim controller). <see cref="GetRotatedUtilityConnectionFlags"/> reads only three fields,
		/// all of which this sets or leaves at their zero value, so the bypass is sound here and
		/// nowhere else — do not reuse this helper for anything that touches the visualizer.
		///
		/// <para>An uninitialized instance leaves <c>BlueprintRotationStateHolder</c> at
		/// <c>Orientation.Neutral</c> (0) and both flips false, i.e. an unrotated blueprint.</para>
		/// </summary>
		private static BuildingVisual UnconstructedVisual(Orientation capturedOrientation)
		{
			var visual = (BuildingVisual)RuntimeHelpers.GetUninitializedObject(typeof(BuildingVisual));
			typeof(BuildingVisual)
				.GetField("buildingConfig", BindingFlags.NonPublic | BindingFlags.Instance)!
				.SetValue(visual, new BuildingConfig { Orientation = capturedOrientation });
			return visual;
		}

		private static void SetBlueprintRotation(BuildingVisual visual, Orientation rotation) =>
			typeof(BuildingVisual)
				.GetField("BlueprintRotationStateHolder", BindingFlags.NonPublic | BindingFlags.Instance)!
				.SetValue(visual, rotation);

		/// <summary>
		/// Issue #4, stated directly: an unrotated blueprint must hand back the stored world-space
		/// mask untouched, no matter which way the captured building happened to face.
		///
		/// This is the test that fails on the old code, where the shift was
		/// <c>capturedOrientation - blueprintRotation</c> — a bridge captured at R90 had its stubs
		/// rotated a quarter turn on a blueprint that was never rotated at all.
		/// </summary>
		[RequiresGameInstallTheory]
		[InlineData(0)] // Neutral
		[InlineData(1)] // R90
		[InlineData(2)] // R180
		[InlineData(3)] // R270
		public void AnUnrotatedBlueprint_IgnoresTheCapturedBuildingsOrientation(int captured)
		{
			var visual = UnconstructedVisual((Orientation)captured);

			Assert.Equal(Left, visual.GetRotatedUtilityConnectionFlags(Left));
			Assert.Equal(Left | Right, visual.GetRotatedUtilityConnectionFlags(Left | Right));
			Assert.Equal(Up | Down, visual.GetRotatedUtilityConnectionFlags(Up | Down));
		}

		/// <summary>
		/// The same independence once the blueprint <em>is</em> rotated: the shift tracks the
		/// blueprint's own rotation alone, so all four captured orientations agree.
		/// </summary>
		[RequiresGameInstallTheory]
		[InlineData(0)] // Neutral
		[InlineData(1)] // R90
		[InlineData(2)] // R180
		[InlineData(3)] // R270
		public void ARotatedBlueprint_ShiftsByItsOwnRotationAlone(int captured)
		{
			var visual = UnconstructedVisual((Orientation)captured);
			SetBlueprintRotation(visual, Orientation.R90);

			// one quarter turn, the same shift the static takes at rotationSteps: -1
			Assert.Equal(Rotate(Left, -1), visual.GetRotatedUtilityConnectionFlags(Left));
			Assert.Equal(Up, visual.GetRotatedUtilityConnectionFlags(Left));
		}

		#endregion
	}
