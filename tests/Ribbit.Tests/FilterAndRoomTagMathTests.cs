using Ribbit.Geometry.Rooms;
using Ribbit.Geometry.Views;
using Xunit;

namespace Ribbit.Tests;

public class FilterOverridesTests
{
    private static FilterOverrideRow Row(
        bool visible = true, bool enabled = true, bool halftone = false, int transparency = 0,
        LineOverride? proj = null, LineOverride? cut = null, PatternOverride? sf = null, PatternOverride? cb = null,
        string? detail = null)
        => new("F", "uid", visible, enabled, halftone, transparency,
            proj ?? LineOverride.None, cut ?? LineOverride.None,
            sf ?? PatternOverride.None, PatternOverride.None, PatternOverride.None, cb ?? PatternOverride.None, detail);

    [Fact]
    public void A_filter_with_nothing_on_it_says_so()
    {
        Assert.Equal("no overrides", Row().Describe());
        Assert.False(Row().HasOverrides);
    }

    [Fact]
    public void Hidden_and_disabled_lead_and_are_not_overrides()
    {
        FilterOverrideRow r = Row(visible: false, enabled: false);
        Assert.Equal("hidden, disabled", r.Describe());
        Assert.False(r.HasOverrides);
    }

    [Fact]
    public void Every_override_is_named_in_order()
    {
        FilterOverrideRow r = Row(halftone: true, transparency: 50,
            proj: new LineOverride(new Rgb(255, 0, 0), null, 3),
            cut: new LineOverride(null, new LinePatternRef("Dash", "u1"), null),
            sf: new PatternOverride(new FillPatternRef("Solid fill", "Drafting", "u2"), new Rgb(1, 2, 3), true),
            cb: new PatternOverride(new FillPatternRef("Diagonal", "Model", "u3"), null, true),
            detail: "Fine");
        Assert.Equal("line colour, line pattern, line weight, fill, background, 50% transparent, halftone, detail level", r.Describe());
    }

    [Fact]
    public void Cut_settings_alone_count()
        => Assert.True(Row(cut: new LineOverride(new Rgb(0, 0, 255), null, null)).HasOverrides);

    [Fact]
    public void The_solid_line_pattern_needs_no_element()
    {
        FilterOverrideRow r = Row(proj: new LineOverride(null, new LinePatternRef(LinePatternRef.SolidName, null), null),
            cut: new LineOverride(null, new LinePatternRef("Hidden", "h"), null));
        Assert.Equal(["Hidden"], r.LinePatterns().Select(p => p.Name));
    }

    [Fact]
    public void Fill_patterns_are_listed_from_all_four_slots()
    {
        FilterOverrideRow r = Row(sf: new PatternOverride(new FillPatternRef("A", "Drafting", "a"), null, true),
            cb: new PatternOverride(new FillPatternRef("B", "Model", "b"), null, false));
        Assert.Equal(["A", "B"], r.FillPatterns().Select(p => p.Name));
    }
}

public class TemplateLabelsTests
{
    [Theory]
    [InlineData("FloorPlan", "Floor Plan")]
    [InlineData("ThreeD", "3D")]
    [InlineData("CeilingPlan", "Ceiling Plan")]
    [InlineData("Section", "Section")]
    public void Pretty_type(string raw, string pretty) => Assert.Equal(pretty, TemplateLabels.PrettyType(raw));

    [Fact]
    public void Plans_then_sections_then_3d_then_the_rest_then_name()
    {
        List<TemplateEntry> got = TemplateLabels.Build([
            new(1, "DraftingView", "a"), new(2, "ThreeD", "b"), new(3, "Section", "z"),
            new(4, "FloorPlan", "Zeta"), new(5, "FloorPlan", "alpha"), new(6, "CeilingPlan", "c"),
        ]);
        Assert.Equal([6, 5, 4, 3, 2, 1], got.Select(t => t.Id));
        Assert.Equal("Floor Plan  |  alpha", got[1].Label);
    }

    [Fact]
    public void The_same_name_as_a_plan_and_a_section_are_two_labels_and_a_true_repeat_is_numbered()
    {
        List<TemplateEntry> got = TemplateLabels.Build([
            new(1, "FloorPlan", "COORD"), new(2, "Section", "COORD"), new(3, "FloorPlan", "COORD"),
        ]);
        Assert.Equal(["Floor Plan  |  COORD", "Floor Plan  |  COORD  (1)", "Section  |  COORD"], got.Select(t => t.Label));
        Assert.Equal(got.Count, got.Select(t => t.Label).Distinct().Count());
    }
}

public class RoomTagMathTests
{
    private static readonly List<Pt2> Square = [new(0, 0), new(10, 0), new(10, 10), new(0, 10)];

    // An L: the 10x10 square with its top-right 5x5 quarter cut away.
    private static readonly List<Pt2> L = [new(0, 0), new(10, 0), new(10, 5), new(5, 5), new(5, 10), new(0, 10)];

    [Fact]
    public void Inside_and_outside_a_square()
    {
        Assert.True(RoomTagMath.InPolygon(5, 5, Square));
        Assert.False(RoomTagMath.InPolygon(15, 5, Square));
        Assert.False(RoomTagMath.InPolygon(-1, -1, Square));
    }

    [Fact]
    public void The_cut_away_part_of_an_L_shaped_crop_is_outside_though_its_bounding_box_holds_it()
    {
        Assert.False(RoomTagMath.InPolygon(8, 8, L));
        Assert.True(RoomTagMath.InPolygon(2, 8, L));
        Assert.True(RoomTagMath.InPolygon(8, 2, L));
    }

    [Fact]
    public void Any_loop_of_a_split_crop()
    {
        List<Pt2> far = [new(100, 100), new(110, 100), new(110, 110), new(100, 110)];
        Assert.True(RoomTagMath.InAnyLoop(105, 105, [Square, far]));
        Assert.False(RoomTagMath.InAnyLoop(50, 50, [Square, far]));
        Assert.False(RoomTagMath.InAnyLoop(5, 5, [new List<Pt2> { new(0, 0), new(10, 0) }]));
    }

    [Fact]
    public void Near_is_planar_and_strict()
    {
        Assert.True(RoomTagMath.NearAny(new(0, 0), [new(0.6, 0.6)], 1.0));
        Assert.False(RoomTagMath.NearAny(new(0, 0), [new(1.0, 0)], 1.0));
        Assert.False(RoomTagMath.NearAny(new(0, 0), [], 1.0));
    }

    [Fact]
    public void Levels_pair_by_name_only_when_the_names_agree()
    {
        Dictionary<long, int> p = RoomTagMath.AutoPair(
            [(10, "Level 1"), (20, " LEVEL 2 "), (30, "ZZ_00_Level 00 (Ground)")],
            ["level 2", "Level 1", "DC_00_Level 0.50 (Ground)", "Level 1"]);
        Assert.Equal(1, p[10]);
        Assert.Equal(0, p[20]);
        Assert.False(p.ContainsKey(30));
    }

    [Fact]
    public void The_oldest_tag_is_kept()
    {
        (long keep, List<long> extras) = RoomTagMath.SplitDuplicates([905, 101, 333]);
        Assert.Equal(101, keep);
        Assert.Equal([333, 905], extras);
    }

    [Fact]
    public void Unique_labels()
    {
        HashSet<string> used = [];
        Assert.Equal("A", RoomTagMath.UniqueLabel("A", used));
        Assert.Equal("A (2)", RoomTagMath.UniqueLabel("A", used));
        Assert.Equal("A (3)", RoomTagMath.UniqueLabel("A", used));
    }
}
