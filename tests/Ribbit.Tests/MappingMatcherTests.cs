using Ribbit.Geometry.Sheets;
using Xunit;

namespace Ribbit.Tests;

/// <summary>
/// Auto-matching exists so the mapping no longer has to be authored inside the
/// workbook. The names are taken from a real office titleblock, where every
/// parameter carries a ABC_TB_ID_ prefix the spreadsheet column does not.
/// </summary>
public class MappingMatcherTests
{
    [Fact]
    public void ExactNamesMatch_IgnoringCaseAndPunctuation()
    {
        Dictionary<string, string> m = MappingMatcher.AutoMatch(
            ["Sub-Discipline"], ["sub discipline"]);

        Assert.Equal("sub discipline", m["Sub-Discipline"]);
    }

    // The case this was built for.
    [Fact]
    public void PrefixedParameter_MatchesTheBareColumn()
    {
        Dictionary<string, string> m = MappingMatcher.AutoMatch(
            ["ABC_TB_ID_Project", "ABC_TB_ID_Originator"], ["Project", "Originator"]);

        Assert.Equal("Project", m["ABC_TB_ID_Project"]);
        Assert.Equal("Originator", m["ABC_TB_ID_Originator"]);
    }

    [Fact]
    public void ColumnContainingTheTail_MatchesAsALastResort()
    {
        Dictionary<string, string> m = MappingMatcher.AutoMatch(
            ["ABC_TB_ID_Originator"], ["Originator Code"]);

        Assert.Equal("Originator Code", m["ABC_TB_ID_Originator"]);
    }

    // A wrong guess writes real data into the wrong field and looks deliberate,
    // so an ambiguous tier must decline rather than take the first candidate.
    [Fact]
    public void TwoEquallyGoodColumns_MatchNeither()
    {
        Dictionary<string, string> m = MappingMatcher.AutoMatch(
            ["ABC_TB_ID_Project"], ["Project Name", "Project Number"]);

        Assert.Empty(m);
    }

    [Fact]
    public void NothingResembling_IsLeftUnmatched()
        => Assert.Empty(MappingMatcher.AutoMatch(["ABC_TB_ID_Phase"], ["Author", "Scale"]));

    // A column already claimed by a stronger tier must not be handed out again.
    [Fact]
    public void OneColumnIsNeverSharedByTwoParameters()
    {
        Dictionary<string, string> m = MappingMatcher.AutoMatch(
            ["Project", "ABC_TB_ID_Project"], ["Project"]);

        Assert.Single(m);
        Assert.Equal("Project", m["Project"]);   // tier 1 wins it
    }

    // Guards tier 3 against a short tail matching half the spreadsheet.
    [Fact]
    public void AVeryShortTail_DoesNotFuzzyMatch()
        => Assert.Empty(MappingMatcher.AutoMatch(["ABC_TB_ID_No"], ["Notes"]));
}
