using Ribbit.Geometry.Sheets;
using Xunit;

namespace Ribbit.Tests;

/// <summary>
/// Part Sheets Duplicator's level detection and name construction. This is the
/// logic that decides which views get created, and it shipped with no tests —
/// its own docstring records that an earlier version "fails silently whenever
/// two picks land on the same level".
/// </summary>
public class ViewNamingTests
{
    // ------------------------------------------------------- level numbers

    [Theory]
    [InlineData("Sh - 12 - Level 01, Phase 1", "1")]
    [InlineData("Floor 3 plan", "3")]
    [InlineData("LVL 07 overall", "7")]
    [InlineData("Flr 12", "12")]
    public void ExplicitLevelTokens_AreFound(string text, string expected)
        => Assert.Equal(expected, ViewNaming.NormalizeLevelName(text));

    [Fact]
    public void LeadingZeros_AreStripped()
        => Assert.Equal("1", ViewNaming.NormalizeLevelName("Level 01"));

    // The after-comma fallback exists so ", 01" reads as a level; the explicit
    // token has to win when both are present.
    [Fact]
    public void AnExplicitToken_BeatsTheAfterCommaFallback()
        => Assert.Equal("5", ViewNaming.NormalizeLevelName("Sh - 58 - Level 5, 09"));

    // The documented false positive the after-comma rule guards against.
    [Fact]
    public void ADashedNumber_IsNotReadAsALevel()
        => Assert.Null(ViewNaming.NormalizeLevelName("Sh - 58 - Part Plan"));

    [Fact]
    public void AfterCommaDigits_AreUsedWhenNoExplicitToken()
        => Assert.Equal("9", ViewNaming.NormalizeLevelName("Part Plan, 09"));

    // The 1..60 window keeps a job code or a year out of the level slot.
    [Fact]
    public void AfterCommaDigits_OutsideTheStoreyRange_AreIgnored()
        => Assert.Null(ViewNaming.NormalizeLevelName("Part Plan, 99"));

    [Fact]
    public void NoLevelAtAll_ReturnsNull()
        => Assert.Null(ViewNaming.NormalizeLevelName("Site Plan"));

    // --------------------------------------------------------- named levels

    // The whole reason phrases are literal rather than bucketed: a fixed "ROOF"
    // bucket cannot tell these two apart, and they are different levels.
    [Fact]
    public void RoofLevelAndRoofGantry_AreDistinctKeys()
    {
        string? roof = ViewNaming.NormalizeLevelKey("Roof Level");
        string? gantry = ViewNaming.NormalizeLevelKey("Roof Gantry Level");

        Assert.Equal("roof", roof);
        Assert.Equal("roof gantry", gantry);
        Assert.NotEqual(roof, gantry);
    }

    [Theory]
    [InlineData("Ground Floor", "ground")]
    [InlineData("Basement", "basement")]
    [InlineData("Mezzanine Level", "mezzanine")]
    public void NamedLevels_ResolveToTheirPhrase(string text, string expected)
        => Assert.Equal(expected, ViewNaming.NormalizeLevelKey(text));

    // A numbered level must resolve to a NUMBER key, never a phrase - the two
    // key spaces have to stay separate or a lookup crosses them.
    [Fact]
    public void ANumberedLevel_PrefersTheNumericKey()
        => Assert.Equal("2", ViewNaming.NormalizeLevelKey("Level 02"));

    [Fact]
    public void ATrailingGenericWord_IsStrippedOnlyWhenOthersRemain()
    {
        // "level" alone is the only word, so it survives.
        Assert.Equal("level", ViewNaming.NormalizeLevelKey("Level"));
        // Here it is a suffix on a real name, so it goes.
        Assert.Equal("plant", ViewNaming.NormalizeLevelKey("Plant Level"));
    }

    // Rows from the real workbook. "Plant" in the drawing TITLE used to win,
    // keying the row by the whole title - a level card of its own, with no
    // floor plan to match it.
    [Theory]
    [InlineData("Sh - 70 - MEP Primary Plant Layout, DC Building 1, Roof Level, Overview", "roof")]
    [InlineData("Sh - 70 - MEP Primary Plant Layout, DC Building 1, Roof Level, Sheet 1 of 3", "roof")]
    [InlineData("Sh - 80 - Plant Replacement Layout, DC Building 1, Level 01, Overall", "1")]
    [InlineData("Drainage Layout, Level 00, Overall", "0")]
    [InlineData("Fire Protection Layout, Level RF, Sheet 3 of 4", "level rf")]
    [InlineData("Plant Room", "plant room")]
    public void RealWorksheetRows_KeyByTheirLevel(string text, string expected)
        => Assert.Equal(expected, ViewNaming.NormalizeLevelKey(text));

    [Fact]
    public void ATitleWithAWeakLevelWordAndNoLevel_HasNoLevel()
        => Assert.Null(ViewNaming.NormalizeLevelKey("Sh - 80 - Plant Replacement Layout - Sitewide Overall"));

    // ------------------------------------------------------------- phases

    [Fact]
    public void APhaseIsAppendedWhenMissing()
        => Assert.Equal("Part Plan, Phase 1",
            ViewNaming.EnsurePhaseAppended("Part Plan", "Phase 1"));

    [Fact]
    public void AnExistingPhaseIsNotDuplicated()
        => Assert.Equal("Part Plan, Phase 1",
            ViewNaming.EnsurePhaseAppended("Part Plan, Phase 1", "Phase 1"));

    [Fact]
    public void ABlankPhaseLeavesTheNameAlone()
        => Assert.Equal("Part Plan", ViewNaming.EnsurePhaseAppended("Part Plan", ""));

    [Fact]
    public void TrailingCommasAndSpaces_AreTrimmedBeforeAppending()
        => Assert.Equal("Part Plan, Phase 2",
            ViewNaming.EnsurePhaseAppended("Part Plan , ", "Phase 2"));

    // ------------------------------------------------------- parent naming

    [Fact]
    public void SheetNOfM_BecomesOverall()
        => Assert.Equal("Level 01 Overall, Phase 1",
            ViewNaming.ParentNameFromFpv("Level 01 Sheet 2 of 5", "Phase 1"));

    [Fact]
    public void ANameWithoutASheetToken_IsLeftAlone()
        => Assert.Equal("Level 01 Overall, Phase 1",
            ViewNaming.ParentNameFromFpv("Level 01 Overall", "Phase 1"));

    [Theory]
    [InlineData("Sheet 1 of 3", true)]
    [InlineData("SHEET 12 OF 30", true)]
    [InlineData("Sheet of 3", false)]
    [InlineData("Overall", false)]
    public void SheetTokenDetection(string text, bool expected)
        => Assert.Equal(expected, ViewNaming.HasSheetToken(text));

    // --------------------------------------------------------- MakeUnique

    [Fact]
    public void AFreeNameIsReturnedUnchanged()
        => Assert.Equal("Plan A", ViewNaming.MakeUnique("Plan A", new HashSet<string>(), new HashSet<string>()));

    [Fact]
    public void AClashingNameGetsASuffix()
        => Assert.Equal("Plan A (1)",
            ViewNaming.MakeUnique("Plan A", new HashSet<string> { "Plan A" }, new HashSet<string>()));

    [Fact]
    public void SuffixesEscalateUntilOneIsFree()
        => Assert.Equal("Plan A (3)",
            ViewNaming.MakeUnique(
                "Plan A",
                new HashSet<string> { "Plan A", "Plan A (1)" },
                new HashSet<string> { "Plan A (2)" }));

    // ---------------------------------------------------- LevelKeySummary

    private static XlsxRow Row(string fpv)
        => new(new Dictionary<string, string>
        {
            [SheetText.Normalize("Floor Plan View Name")] = fpv,
        });

    [Fact]
    public void LevelKeySummary_GroupsAndCountsByLevel()
    {
        List<XlsxRow> rows =
        [
            Row("Level 01 Sheet 1 of 2"),
            Row("Level 01 Sheet 2 of 2"),
            Row("Level 02 Overall"),
            Row("Site Plan"),          // no level - dropped
        ];

        List<ViewNaming.LevelKeyEntry> summary = ViewNaming.LevelKeySummary(rows);

        Assert.Equal(2, summary.Count);
        Assert.Equal("1", summary[0].Key);
        Assert.Equal(2, summary[0].Count);
        Assert.Equal("Level 01 Sheet 1 of 2", summary[0].Example);
        Assert.Equal("2", summary[1].Key);
        Assert.Equal(1, summary[1].Count);
    }

    [Fact]
    public void LevelKeySummary_IsSortedForAStableUiOrder()
    {
        List<XlsxRow> rows = [Row("Level 03"), Row("Level 01"), Row("Roof Level")];

        List<string> keys = [.. ViewNaming.LevelKeySummary(rows).Select(e => e.Key)];

        Assert.Equal(["1", "3", "roof"], keys);
    }

    [Theory]
    [InlineData("1", "Level 01")]
    [InlineData("12", "Level 12")]
    [InlineData("roof", "Roof")]
    [InlineData("level rf", "Level RF")]
    [InlineData("roof gantry", "Roof Gantry")]
    public void LevelTitle_IsHowAPersonWritesTheLevel(string key, string expected)
        => Assert.Equal(expected, ViewNaming.LevelTitle(key));

    // Ordinal order put Level 10 between Level 1 and Level 2.
    [Fact]
    public void LevelKeySummary_PutsNumberedLevelsInNumberOrder()
    {
        List<XlsxRow> rows = [Row("Level 10"), Row("Basement Level"), Row("Level 2"), Row("Level 1")];
        Assert.Equal(["1", "2", "10", "basement"], ViewNaming.LevelKeySummary(rows).Select(e => e.Key));
    }
}
