using Ribbit.Geometry.Sheets;
using Xunit;

namespace Ribbit.Tests;

public class ViewSheetMatcherTests
{
    private static readonly List<SheetName> Sheets =
    [
        new("M-101", "LEVEL 1 POWER PLAN"),
        new("M-102", "LEVEL 2 POWER PLAN"),
        new("M-201", "DRAINAGE LEVEL 00 OVERALL"),
        new("M-202", "DRAINAGE LEVEL 00 SHEET 1"),
        new("M-301", "SECTIONS"),
        new("M-401", "LEAK DETECTION ZONING DC BUILDING LEVEL 00"),
        new("M-501", "SCHEDULES"),
    ];

    private static readonly ViewSheetMatcher M = new(Sheets);

    [Theory]
    [InlineData("L1 - POWER", "lvl1")]
    [InlineData("LVL 01", "lvl1")]
    [InlineData("Level 1", "lvl1")]
    [InlineData("1st floor", "lvl1")]
    [InlineData("FLR-03", "lvl3")]
    [InlineData("Level_02", "lvl2")]
    public void Storey_spellings_become_one_token(string text, string token)
        => Assert.Contains(token, ViewSheetMatcher.Tokens(text));

    [Fact]
    public void Plural_s_is_dropped_but_not_from_ss_or_short_words()
    {
        HashSet<string> t = ViewSheetMatcher.Tokens("SECTIONS GLASS BUS AREAS");
        Assert.Contains("section", t);
        Assert.Contains("glass", t);
        Assert.Contains("bus", t);
        Assert.Contains("area", t);
    }

    [Fact]
    public void A_storey_alias_finds_its_sheet()
        => Assert.Equal(0, M.Suggestion("L1 - POWER"));

    [Fact]
    public void Level_2_is_not_level_1()
        => Assert.Equal(1, M.Suggestion("Power Plan - Level 2"));

    [Fact]
    public void Sharing_only_a_number_is_no_match()
        => Assert.Equal(0.0, M.Score("SECTION 1", 0));

    [Fact]
    public void Overall_and_sheet_1_are_told_apart()
    {
        Assert.Equal(2, M.Suggestion("Drainage Level 00 Overall"));
        Assert.Equal(3, M.Suggestion("Drainage Level 00 Sheet 1"));
    }

    [Fact]
    public void A_view_sharing_only_common_words_is_not_suggested_onto_a_long_sheet_name()
        => Assert.NotEqual(5, M.Suggestion("GA Plan - DC Building Level 00"));

    [Fact]
    public void An_identical_name_wins_outright()
    {
        Assert.True(M.Score("Sections", 4) > 1.0);
        Assert.Equal(4, M.Suggestion("sections "));
    }

    [Fact]
    public void A_tie_suggests_nothing()
    {
        ViewSheetMatcher twin = new([new("A1", "POWER PLAN"), new("A2", "POWER PLAN")]);
        Assert.Null(twin.Suggestion("Power"));
        // ...but both are ranked, in sheet-number order
        List<(double Score, int Sheet)> r = twin.Ranked("Power");
        Assert.Equal([0, 1], r.Select(x => x.Sheet));
    }

    [Fact]
    public void Ranked_ties_go_in_natural_sheet_number_order()
    {
        ViewSheetMatcher twin = new([new("A10", "POWER"), new("A2", "POWER")]);
        Assert.Equal([1, 0], twin.Ranked("power").Select(x => x.Sheet));
    }

    [Fact]
    public void A_weak_match_is_below_the_floor()
        => Assert.Null(M.Suggestion("Detail Power Riser Diagram Typical"));

    [Fact]
    public void Nothing_in_common_scores_zero_and_is_not_ranked()
    {
        Assert.Empty(M.Ranked("Xyzzy"));
        Assert.Empty(M.Ranked(""));
    }

    [Fact]
    public void Rare_words_weigh_more()
    {
        // "zoning" is on one sheet, "level" on five: matching the rare one scores higher
        double rare = M.Score("Zoning", 5);
        double common = M.Score("Level", 5);
        Assert.True(rare > common, $"{rare} vs {common}");
    }

    [Theory]
    [InlineData(0.0, 0)]
    [InlineData(0.5, 2)]   // 2.5 rounds to even, as Python does
    [InlineData(0.61, 3)]
    [InlineData(1.0, 5)]
    [InlineData(1.9, 5)]
    public void Stars(double score, int filled)
    {
        string s = ViewSheetMatcher.Stars(score);
        Assert.Equal(5, s.Length);
        Assert.Equal(filled, s.Count(c => c == (char)0x25A0));
    }

    [Fact]
    public void Suggesting_for_a_big_project_is_quick()
    {
        List<SheetName> sheets = [.. Enumerable.Range(1, 500).Select(i => new SheetName($"M-{i:000}", $"LEVEL {i % 40} {(i % 3 == 0 ? "POWER" : "LIGHTING")} PLAN SHEET {i}"))];
        ViewSheetMatcher m = new(sheets);
        System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
        int suggested = Enumerable.Range(1, 500).Count(i => m.Suggestion($"L{i % 40} - {(i % 3 == 0 ? "Power" : "Lighting")} - Sheet {i}") is not null);
        sw.Stop();
        Assert.True(sw.ElapsedMilliseconds < 3000, $"{sw.ElapsedMilliseconds} ms");
        Assert.True(suggested > 400, $"{suggested} of 500 suggested");
    }

    [Fact]
    public void No_sheets_no_suggestion()
        => Assert.Null(new ViewSheetMatcher([]).Suggestion("Level 1"));
}
