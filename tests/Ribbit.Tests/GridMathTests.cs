using Ribbit.Geometry.Grids;
using Xunit;

namespace Ribbit.Tests;

public class GridMathTests
{
    private static readonly ViewRect Box = new(0, 0, 10, 10);

    [Fact]
    public void Line_through_the_box_crosses_it()
        => Assert.True(GridMath.CrossesRectangle(-5, 5, 15, 5, Box));

    [Fact]
    public void Line_beside_the_box_does_not_cross_it()
        => Assert.False(GridMath.CrossesRectangle(-5, 12, 15, 12, Box));

    [Fact]
    public void Line_through_a_corner_counts_as_crossing()
        => Assert.True(GridMath.CrossesRectangle(-5, -5, 20, 20, Box));

    [Fact]
    public void Degenerate_line_never_crosses()
        => Assert.False(GridMath.CrossesRectangle(5, 5, 5, 5, Box));

    [Theory]
    // drawn left-to-right: End0 is on the left
    [InlineData(0, 0, 10, 0, true, true, true)]
    [InlineData(0, 0, 10, 0, true, false, false)]
    // drawn right-to-left: End0 is on the right
    [InlineData(10, 0, 0, 0, true, true, false)]
    // vertical, drawn bottom-up: End0 is at the bottom
    [InlineData(0, 0, 0, 10, false, true, false)]
    [InlineData(0, 10, 0, 0, false, true, true)]
    public void Bubble_side_is_measured_per_grid(
        double ax, double ay, double bx, double by, bool horizontal, bool wantLeftOrTop, bool end0)
        => Assert.Equal(end0,
            GridMath.BubbleOnEnd0(ax, ay, bx, by, horizontal, wantLeftOrTop, wantLeftOrTop));

    [Fact]
    public void Offsets_follow_the_datum_direction()
    {
        SideOffsets o = new(Left: 1, Right: 2, Top: 3, Bottom: 4);
        Assert.Equal((1.0, 2.0), GridMath.EndOffsets(0, 0, 10, 0, true, o));   // pointing right
        Assert.Equal((2.0, 1.0), GridMath.EndOffsets(10, 0, 0, 0, true, o));   // pointing left
        Assert.Equal((4.0, 3.0), GridMath.EndOffsets(0, 0, 0, 10, false, o));  // pointing up
        Assert.Equal((3.0, 4.0), GridMath.EndOffsets(0, 10, 0, 0, false, o));  // pointing down
    }

    [Fact]
    public void Stretch_range_spans_corners_plus_offsets()
    {
        (double, double)? r = GridMath.StretchRange([2, -3, 7, 0], 1, 2);
        Assert.Equal((-4.0, 9.0), r);
    }

    [Fact]
    public void Negative_offsets_that_collapse_the_line_give_null()
        => Assert.Null(GridMath.StretchRange([0, 10], -6, -6));

    [Fact]
    public void A_grid_past_the_crop_is_cut_at_its_edges()
    {
        // runs from x=-10 to x=20 across a 0..10 box: cut at 1/3 and 2/3
        (double Low, double High)? t = GridMath.ClipToRect(-10, 5, 20, 5, Box);
        Assert.NotNull(t);
        Assert.Equal(1.0 / 3, t!.Value.Low, 9);
        Assert.Equal(2.0 / 3, t.Value.High, 9);
    }

    [Fact]
    public void An_end_inside_the_crop_keeps_its_place()
    {
        (double Low, double High)? t = GridMath.ClipToRect(5, 5, 20, 5, Box);
        Assert.Equal(0, t!.Value.Low, 9);
        Assert.Equal(1.0 / 3, t.Value.High, 9);
    }

    [Fact]
    public void A_grid_that_misses_the_crop_cannot_be_clipped()
        => Assert.Null(GridMath.ClipToRect(-10, 15, 20, 15, Box));

    [Fact]
    public void Two_tight_bubbles_are_pushed_to_the_gap()
    {
        Dictionary<int, double> s = GridMath.SpacingShifts([0, 1], 3);
        Assert.Single(s);
        Assert.Equal(2, s[1], 9);
    }

    [Fact]
    public void A_run_of_three_resolves_in_one_pass()
    {
        Dictionary<int, double> s = GridMath.SpacingShifts([0, 1, 2], 3);
        Assert.Equal(2, s[1], 9);   // 1 -> 3
        Assert.Equal(4, s[2], 9);   // 2 -> 6
    }

    [Fact]
    public void A_bubble_read_back_a_hair_short_of_the_gap_is_not_moved_again()
    {
        // Revit hands a placed bubble back a fraction of a mm off: with the read-back
        // tolerance it still counts as spaced, fixed or not.
        Assert.Empty(GridMath.SpacingShifts([0, 3 - 1e-5], 3, [1], tolerance: 1e-3));
        Assert.Empty(GridMath.SpacingShifts([0, 3 - 1e-5], 3, tolerance: 1e-3));
    }

    [Fact]
    public void Without_that_tolerance_the_same_pair_reads_as_a_clash()
        => Assert.NotEmpty(GridMath.SpacingShifts([0, 3 - 1e-5], 3, [1]));

    [Fact]
    public void Spaced_bubbles_do_not_move()
        => Assert.Empty(GridMath.SpacingShifts([0, 5, 10], 3));

    [Fact]
    public void A_fixed_bubble_is_spaced_around_not_moved()
    {
        // index 1 already has a leader at 1; index 0 at 0 goes to the slot before it
        Dictionary<int, double> s = GridMath.SpacingShifts([0, 1], 3, [1]);
        Assert.False(s.ContainsKey(1));
        Assert.Equal(-2, s[0], 9);
    }

    [Theory]
    [InlineData("3000", 3000.0)]
    [InlineData("", 0.0)]
    [InlineData("  -250 ", -250.0)]
    [InlineData("12,5", 12.5)]
    public void Mm_parses(string text, double expected) => Assert.Equal(expected, GridMath.ParseMm(text));

    [Fact]
    public void Bad_mm_is_null_not_zero() => Assert.Null(GridMath.ParseMm("3ooo"));

    [Theory]
    [InlineData("3,000")]      // meant as 3000, would have been 3 mm
    [InlineData("-12,500")]
    [InlineData("3,000.5")]
    [InlineData("1,2,3")]
    public void A_thousands_separator_is_refused_not_read_as_a_decimal(string text)
        => Assert.Null(GridMath.ParseMm(text));

    [Theory]
    [InlineData("1,25", 1.25)]
    [InlineData("3000,5", 3000.5)]
    public void A_decimal_comma_still_reads(string text, double expected)
        => Assert.Equal(expected, GridMath.ParseMm(text));

    [Fact]
    public void Pretty_mm_drops_the_point_zero()
    {
        Assert.Equal("3000", GridMath.PrettyMm(3000.0));
        Assert.Equal("12.5", GridMath.PrettyMm(12.5));
    }
}

public class GridSearchTests
{
    private static List<string> Find(string query, params string[] rows)
        => GridSearch.Filter(query, rows.Select(r => (r, r)));

    [Fact]
    public void Sheet_6_is_not_sheet_16_or_61()
        => Assert.Equal(["Sheet 6", "Sheet 06"],
            Find("Sheet 6", "Sheet 6", "Sheet 16", "Sh - 61 Sheet 1", "Sheet 06"));

    [Fact]
    public void A_word_ending_in_a_number_is_whole()
        => Assert.Equal(["L1 power"], Find("L1", "L1 power", "L10 power"));

    [Fact]
    public void Loose_reading_applies_only_when_strict_finds_nothing()
        => Assert.Equal(["Power L1 101"], Find("power 101", "Power L1 101", "Lighting L1 101"));

    [Fact]
    public void Empty_query_matches_everything()
        => Assert.Equal(3, Find("", "a", "b", "c").Count);
}

public class GridStepsTests
{
    [Fact]
    public void Preset_replaces_only_what_it_holds()
    {
        GridSteps screen = new() { LeftMm = 100, GapMm = 500 };
        GridSteps merged = screen.With(new GridPreset { Bubbles = [false, false] });
        Assert.Equal(100, merged.LeftMm);
        Assert.False(merged.BubbleLeft);
        Assert.False(merged.BubbleTop);
        Assert.Equal(500, merged.GapMm);
    }

    [Fact]
    public void A_three_step_preset_turns_step_4_off()
    {
        GridSteps merged = new GridSteps { Spread = true }.With(new GridPreset { Steps = [true, false, true] });
        Assert.False(merged.Spread);
        Assert.False(merged.Align);
    }

    [Fact]
    public void Step_4_needs_a_gap()
        => Assert.NotNull(new GridSteps { Spread = true, GapMm = 0 }.Problem(false));

    [Fact]
    public void Hide_only_run_needs_no_step()
    {
        GridSteps none = new() { Convert = false, Align = false, Bubbles = false, Spread = false };
        Assert.Null(none.Problem(hideOnlyAllowed: true));
        Assert.NotNull(none.Problem(hideOnlyAllowed: false));
    }
}
