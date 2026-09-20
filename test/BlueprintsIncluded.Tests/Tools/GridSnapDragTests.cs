using BlueprintsV2.Tools;
using Xunit;

namespace BlueprintsIncluded.Tests.Tools;

	/// <summary>
	/// Locks the grid-snap drag's lattice logic, including the four ways it deliberately differs
	/// from upstream a3375d0: fast drags fill the skipped steps, off-axis wobble snaps rather than
	/// stalling, a point is placed once per drag, and a step below 1 places nothing.
	/// </summary>
	public class GridSnapDragTests
	{
		private static GridSnapDrag StartedAt(int x, int y)
		{
			var drag = new GridSnapDrag();
			drag.Begin(x, y);
			return drag;
		}

		[Fact]
		public void Nothing_is_placed_until_the_cursor_is_nearer_the_next_point()
		{
			var drag = StartedAt(10, 10);
			Assert.Empty(drag.Advance(11, 10, 4, 4));
			Assert.Empty(drag.Advance(11, 10, 4, 4)); // 1 of 4: still nearest the origin
			Assert.Equal(new[] { (14, 10) }, drag.Advance(12, 10, 4, 4)); // halfway rounds away from the origin
		}

		[Fact]
		public void A_fast_drag_fills_every_skipped_step()
		{
			// upstream placed only when the cursor landed exactly on a multiple, so a mouse event
			// that jumped from 10 to 23 placed nothing at all.
			var drag = StartedAt(10, 10);
			Assert.Equal(new[] { (14, 10), (18, 10), (22, 10) }, drag.Advance(23, 10, 4, 4));
		}

		[Fact]
		public void Off_axis_wobble_snaps_instead_of_stalling()
		{
			var drag = StartedAt(10, 10);
			Assert.Equal(new[] { (15, 10) }, drag.Advance(15, 11, 5, 5)); // y is one cell off the row
		}

		[Fact]
		public void A_point_is_placed_once_per_drag()
		{
			var drag = StartedAt(0, 0);
			Assert.Equal(new[] { (3, 0), (6, 0) }, drag.Advance(6, 0, 3, 3));
			Assert.Empty(drag.Advance(0, 0, 3, 3)); // back over 3 and the origin
			Assert.Equal(new[] { (-3, 0) }, drag.Advance(-3, 0, 3, 3));
		}

		[Fact]
		public void A_diagonal_drag_walks_the_lattice_line()
		{
			var drag = StartedAt(0, 0);
			Assert.Equal(new[] { (2, 3), (4, 6) }, drag.Advance(4, 6, 2, 3));
		}

		[Fact]
		public void A_drag_starting_at_zero_zero_works()
		{
			// upstream used default(Vector3) as "not dragging", so this never placed.
			var drag = StartedAt(0, 0);
			Assert.True(drag.IsDragging);
			Assert.Equal(new[] { (2, 0) }, drag.Advance(2, 0, 2, 2));
		}

		[Theory]
		[InlineData(0, 3)]
		[InlineData(3, 0)]
		[InlineData(-1, 3)]
		public void A_step_below_one_places_nothing(int stepX, int stepY)
		{
			var drag = StartedAt(0, 0);
			Assert.Empty(drag.Advance(9, 9, stepX, stepY));
		}

		[Fact]
		public void Nothing_is_placed_before_begin_or_after_end()
		{
			var drag = new GridSnapDrag();
			Assert.Empty(drag.Advance(9, 0, 3, 3));
			drag.Begin(0, 0);
			drag.End();
			Assert.False(drag.IsDragging);
			Assert.Empty(drag.Advance(9, 0, 3, 3));
		}

		[Fact]
		public void A_new_drag_forgets_the_last_one()
		{
			var drag = StartedAt(0, 0);
			drag.Advance(3, 0, 3, 3);
			drag.End();
			drag.Begin(0, 0);
			Assert.Equal(new[] { (3, 0) }, drag.Advance(3, 0, 3, 3));
		}

		[Theory]
		[InlineData(0, 4, 0)]
		[InlineData(1, 4, 0)]
		[InlineData(2, 4, 4)]
		[InlineData(-1, 4, 0)]
		[InlineData(-2, 4, -4)]
		[InlineData(-6, 4, -8)]
		[InlineData(7, 1, 7)]
		public void RoundToStep_is_nearest_and_symmetric(int delta, int step, int expected)
		{
			Assert.Equal(expected, GridSnapDrag.RoundToStep(delta, step));
		}
	}
