using Ribbit.Geometry.Clash;
using Xunit;

namespace Ribbit.Tests;

public class ClashSearchNamingTests
{
    // THE unit trap in this tool: UnitParse reads a bare number as FEET, but the
    // pad box means MILLIMETRES. Without the " mm" suffix, "25" becomes a 25-FOOT
    // clash pad and essentially every element in the model reports as clashing.
    [Fact]
    public void ABareNumberInThePadBoxMeansMillimetres()
    {
        string? normalized = ClashSearchNaming.NormalizePadText("25");
        double feet = UnitParse.ToFeet(normalized)!.Value;

        Assert.Equal(25.0 / 304.8, feet, 1e-9);
    }

    [Fact]
    public void WithoutNormalizingABareNumberWouldBeFeet()
        => Assert.Equal(25.0, UnitParse.ToFeet("25")!.Value, 1e-9);

    [Theory]
    [InlineData("25", "25 mm")]
    [InlineData(" 2.5 ", "2.5 mm")]
    [InlineData("-3", "-3 mm")]
    public void BareNumbersGetTheMillimetreSuffix(string input, string expected)
        => Assert.Equal(expected, ClashSearchNaming.NormalizePadText(input));

    // An explicit unit must be left exactly as typed.
    [Theory]
    [InlineData("25mm")]
    [InlineData("2ft")]
    [InlineData("1m")]
    [InlineData("6\"")]
    public void AnExplicitUnitIsLeftAlone(string input)
        => Assert.Equal(input, ClashSearchNaming.NormalizePadText(input));

    [Fact]
    public void NullPadTextStaysNull()
        => Assert.Null(ClashSearchNaming.NormalizePadText(null));

    // ------------------------------------------------------------- Set A label

    [Fact]
    public void ATypedKeywordWinsOverPickedValues()
        => Assert.Equal("fire_protection", ClashSearchNaming.SetALabel(
            "Fire Protection", [("System Type", "Chilled Water")]));

    // A picker-only search is a normal way to use this tool, so it must not
    // produce a filename that says nothing.
    [Fact]
    public void WithNoKeywordTheFirstPickedValueIsUsed()
        => Assert.Equal("chilled_water", ClashSearchNaming.SetALabel(
            "", [("System Type", "Chilled Water")]));

    [Fact]
    public void SeveralPickedValuesGetACount()
        => Assert.Equal("chilled_water_plus2", ClashSearchNaming.SetALabel(
            "", [("System Type", "Chilled Water"), ("System Type", "Hot Water"), ("Description", "X")]));

    [Fact]
    public void NothingAtAllIsNone()
        => Assert.Equal("none", ClashSearchNaming.SetALabel("", []));

    // Windows' path limit is closer than it looks once the model title, tag,
    // date, categories and counter are all in the name too.
    [Fact]
    public void AVeryLongPickedValueIsTruncated()
    {
        string label = ClashSearchNaming.SetALabel(
            "", [("System Type", new string('a', 200))]);

        Assert.True(label.Length <= 24, $"expected <= 24 chars, got {label.Length}");
    }

    // --------------------------------------------------------------- report tag

    [Fact]
    public void TheTagJoinsKeywordAndCategories()
        => Assert.Equal("ductvsstructural_framing",
            ClashSearchNaming.ReportTag("duct", ["Structural Framing"]));

    [Fact]
    public void SeveralCategoriesAreJoinedWithPlus()
        => Assert.Equal("ductvsstructural_framing+walls",
            ClashSearchNaming.ReportTag("duct", ["Structural Framing", "Walls"]));

    [Fact]
    public void NoCategoriesReadsAsNone()
        => Assert.Equal("ductvsnone", ClashSearchNaming.ReportTag("duct", []));

    // ---------------------------------------------------- link report tag

    // Clash Detect Link searches BOTH sides by keyword, so its tag pairs two
    // search labels and records how many links were searched.
    [Fact]
    public void TheLinkTagPairsBothKeywordsWithTheLinkCount()
        => Assert.Equal("52vs57_2links",
            ClashSearchNaming.LinkReportTag("52", [], "57", [], 2));

    [Fact]
    public void AnEmptySetBKeywordReadsAsNone()
        => Assert.Equal("52vsnone_1links",
            ClashSearchNaming.LinkReportTag("52", [], "", [], 1));

    // A picker-only search on either side still names what it looked for,
    // rather than degrading to "nonevsnone".
    [Fact]
    public void PickedValuesNameEachSideWhenTheKeywordBoxIsEmpty()
        => Assert.Equal("chilled_watervsdomestic_cold_plus1_3links",
            ClashSearchNaming.LinkReportTag(
                "", [("System Type", "Chilled Water")],
                "", [("System Type", "Domestic Cold"), ("Description", "Anything")],
                3));

    // A typed keyword beats picks on its own side only - the two sides are
    // labelled independently.
    [Fact]
    public void ATypedKeywordBeatsPicksOnItsOwnSideOnly()
        => Assert.Equal("ductvschilled_water_1links",
            ClashSearchNaming.LinkReportTag(
                "duct", [("System Type", "Ignored")],
                "", [("System Type", "Chilled Water")],
                1));
}
