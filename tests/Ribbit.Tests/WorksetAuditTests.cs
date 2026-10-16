using Ribbit.Geometry.Clash;
using Xunit;

namespace Ribbit.Tests;

/// <summary>
/// The rules behind a Federated set's "Worksets left out" and the audit that
/// suggests them. Names are testproject's real worksets (2026-10-04): the audit
/// has to read THEM right, not names invented to pass.
/// </summary>
public class WorksetAuditTests
{
    // ---------------------------------------------------------------- matching

    [Theory]
    [InlineData("MEP_ZZ_Typical", true)]
    [InlineData("mep_zz_typical", true)]
    [InlineData("  MEP_ZZ_Typical  ", true)]
    [InlineData("MEP_ZZ_Typical2", false)]
    [InlineData("MEP_ZZ_MEPServices", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Exclusion_IsByName_TrimmedAndIgnoringCase(string? workset, bool excluded)
        => Assert.Equal(excluded, WorksetAudit.IsExcluded(["MEP_ZZ_Typical"], workset));

    [Fact]
    public void AnElementOnNoWorkset_IsNeverExcluded_EvenByABlankEntry()
        => Assert.False(WorksetAudit.IsExcluded(["", "  "], ""));

    [Fact]
    public void Normalize_TrimsDropsBlanksAndDuplicates_KeepingFirstSpelling()
        => Assert.Equal(
            ["MEP_ZZ_Typical", "BWIC"],
            WorksetAudit.Normalize([" MEP_ZZ_Typical", "", null, "mep_zz_typical", "BWIC", "bwic "]));

    // -------------------------------------------------------------- tokenizing

    [Theory]
    [InlineData("MEP_ZZ_MEPServices_Hidden", "mep zz mep services hidden")]
    [InlineData("MEP_ZZ_PlantReplacement", "mep zz plant replacement")]
    [InlineData("MEP_ZZ_ScopeBoxes", "mep zz scope boxes")]
    [InlineData("MEP_03_BWIC", "mep 03 bwic")]
    [InlineData("MEP_ZZ_Levels&Grids", "mep zz levels grids")]
    [InlineData("Workset1", "workset 1")]
    public void Tokenize_SplitsSeparatorsCamelCaseAndAcronyms(string name, string expected)
        => Assert.Equal(expected, string.Join(" ", WorksetAudit.Tokenize(name)));

    // -------------------------------------------------------------- suggesting

    /// <summary>testproject's 16 worksets, with their real hidden-by-default flags.</summary>
    [Theory]
    [InlineData("MEP_ZZ_Typical", true, true)]
    [InlineData("MEP_ZZ_MEPServices_Hidden", true, true)]
    [InlineData("MEP_01_BWIC", true, true)]
    [InlineData("MEP_Roof_BWIC", true, true)]
    [InlineData("MEP_ZZ_PlantReplacement", true, false)]   // flagged, NOT ticked - may be real plant
    [InlineData("MEP_ZZ_MEPServices_SW", true, false)]     // only "hidden by default"
    [InlineData("MEP_ZZ_ScopeBoxes", true, false)]
    [InlineData("MEP_ZZ_ReferencePlanes", true, false)]
    public void TheFlaggedWorksets_AndOnlyClearNonCoordinationOnesStartTicked(
        string name, bool hidden, bool preTick)
    {
        WorksetSuggestion? s = WorksetAudit.Suggest(name, hidden);

        Assert.NotNull(s);
        Assert.Equal(preTick, s!.PreTick);
    }

    [Theory]
    [InlineData("MEP_ZZ_MEPServices")]
    [InlineData("Workset1")]
    [InlineData("MEP_ZZ_LinkedRevit")]
    [InlineData("MEP_ZZ_Levels&Grids")]
    [InlineData("MEP_ZZ_Template")]      // "template" must not read as "temp"
    [InlineData("MEP_ZZ_Attempted")]
    public void ALiveVisibleWorkset_IsNotFlagged(string name)
        => Assert.Null(WorksetAudit.Suggest(name, hiddenByDefault: false));

    [Fact]
    public void HiddenByDefault_IsFlaggedOnItsOwn_ButNeverTicked()
    {
        WorksetSuggestion? s = WorksetAudit.Suggest("MEP_ZZ_MEPServices_SW", hiddenByDefault: true);

        Assert.Equal("hidden by default", s?.Reason);
        Assert.False(s!.PreTick);
    }

    // ----------------------------------------------------------- opening ticks

    private static readonly WorksetUsage[] Usage =
    [
        new("MEP_ZZ_MEPServices", ["host"], 500, false),
        new("MEP_ZZ_Typical", ["host"], 40, true),
        new("MEP_01_BWIC", ["host"], 0, true),               // nothing of the set on it
        new("MEP_ZZ_PlantReplacement", ["host"], 12, true),
    ];

    [Fact]
    public void ASetWithNoExclusions_OpensWithTheSuggestions_ThatCostItSomething()
        => Assert.Equal(["MEP_ZZ_Typical"], WorksetAudit.InitialTicks(Usage, []));

    [Fact]
    public void ASetThatAlreadyLeavesOut_OpensWithExactlyThat_NotTheSuggestions()
        => Assert.Equal(
            ["MEP_ZZ_PlantReplacement"],
            WorksetAudit.InitialTicks(Usage, ["MEP_ZZ_PlantReplacement"]));

    // ------------------------------------------------------------------- stale

    [Fact]
    public void Stale_IsWhatNoSearchedModelHas_IgnoringCase()
        => Assert.Equal(
            ["MEP_ZZ_Gone"],
            WorksetAudit.Stale(["mep_zz_typical", "MEP_ZZ_Gone"], ["MEP_ZZ_Typical", "Workset1"]));
}
