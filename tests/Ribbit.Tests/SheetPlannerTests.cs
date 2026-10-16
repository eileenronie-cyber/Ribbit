using Ribbit.Geometry.Sheets;
using Xunit;

namespace Ribbit.Tests;

/// <summary>
/// SheetMaker shipped with no tests. These pin the row-planning rules the
/// Python's <c>_plan_row</c> encoded, which are now testable because planning no
/// longer touches a Document.
/// </summary>
public class SheetPlannerTests
{
    private static XlsxRow Row(params (string Header, string Value)[] cells)
    {
        Dictionary<string, string> d = [];
        foreach ((string header, string value) in cells)
            d[SheetText.Normalize(header)] = value;
        return new XlsxRow(d);
    }

    private static readonly List<(string Param, string Column)> NoPairs = [];

    private static RowPlan Plan(
        XlsxRow row,
        ISet<string>? existing = null,
        ExistingMode mode = ExistingMode.Update,
        bool blankAsSpacer = true,
        List<(string Param, string Column)>? pairs = null)
        => SheetPlanner.PlanRow(0, row, new RowContext(
            "Sheet Number", "Sheet Name",
            pairs ?? NoPairs, existing ?? new HashSet<string>(), mode, blankAsSpacer));

    // --------------------------------------------------------------- lookup

    // The whole point of normalizing headers once: lookup still has to be
    // case- and whitespace-insensitive.
    [Fact]
    public void ColumnLookup_IsCaseAndWhitespaceInsensitive()
    {
        XlsxRow row = Row(("  SHEET   Number ", "A-101"));

        Assert.Equal("A-101", row.Get("sheet number"));
        Assert.Equal("A-101", row.Get("Sheet Number"));
    }

    // --------------------------------------------------------------- spacer

    [Fact]
    public void BlankSheetNumber_IsASpacer_WhenSpacersAreOn()
        => Assert.Equal(SheetAction.Spacer,
            Plan(Row(("Sheet Number", ""), ("Sheet Name", "")), blankAsSpacer: true).Action);

    // With spacers off, a blank number is an error rather than being silently
    // dropped - it falls through to the sheet-number validation.
    [Fact]
    public void BlankSheetNumber_IsAnError_WhenSpacersAreOff()
    {
        RowPlan plan = Plan(Row(("Sheet Number", ""), ("Sheet Name", "")), blankAsSpacer: false);

        Assert.Equal(SheetAction.Error, plan.Action);
        Assert.Contains("blank", plan.Error);
    }

    // ----------------------------------------------------------- validation

    [Theory]
    [InlineData("A{101")]
    [InlineData("A]101")]
    [InlineData("A|101")]
    [InlineData("A;101")]
    [InlineData("A<101")]
    [InlineData("A`101")]
    public void ProhibitedCharactersInSheetNumber_AreAnError(string number)
    {
        RowPlan plan = Plan(Row(("Sheet Number", number), ("Sheet Name", "Plan")));

        Assert.Equal(SheetAction.Error, plan.Action);
        Assert.Contains("prohibited character", plan.Error);
    }

    [Fact]
    public void ProhibitedCharacters_AreReportedSortedAndDeduplicated()
    {
        RowPlan plan = Plan(Row(("Sheet Number", "A}}{101|"), ("Sheet Name", "Plan")));

        Assert.Equal(SheetAction.Error, plan.Action);
        Assert.EndsWith("{|}", plan.Error);
    }

    [Fact]
    public void AnOrdinarySheetNumber_IsValid()
        => Assert.Equal(SheetAction.Create,
            Plan(Row(("Sheet Number", "A-101"), ("Sheet Name", "Level 1"))).Action);

    // ------------------------------------------------------ existing sheets

    [Fact]
    public void ExistingSheet_IsUpdated_InUpdateMode()
    {
        RowPlan plan = Plan(
            Row(("Sheet Number", "A-101"), ("Sheet Name", "Level 1")),
            existing: new HashSet<string> { "A-101" },
            mode: ExistingMode.Update);

        Assert.Equal(SheetAction.Update, plan.Action);
    }

    [Fact]
    public void ExistingSheet_IsSkipped_InSkipMode()
    {
        RowPlan plan = Plan(
            Row(("Sheet Number", "A-101"), ("Sheet Name", "Level 1")),
            existing: new HashSet<string> { "A-101" },
            mode: ExistingMode.Skip);

        Assert.Equal(SheetAction.Skip, plan.Action);
    }

    // A skipped row carries no mapped values - nothing is written to it, so
    // computing them would be wasted work AND misleading in the report.
    [Fact]
    public void SkippedRow_CarriesNoMappedValues()
    {
        RowPlan plan = Plan(
            Row(("Sheet Number", "A-101"), ("Discipline", "ARCH")),
            existing: new HashSet<string> { "A-101" },
            mode: ExistingMode.Skip,
            pairs: [("Discipline", "Discipline")]);

        Assert.Empty(plan.MappedValues);
    }

    // ------------------------------------------------------- mapped columns

    [Fact]
    public void MappedValues_AreReadThroughTheirExcelColumn()
    {
        RowPlan plan = Plan(
            Row(("Sheet Number", "A-101"), ("DISC", "ARCH")),
            pairs: [("Discipline", "DISC")]);

        Assert.Equal(("Discipline", "ARCH"), plan.MappedValues.Single());
    }

    // Sheet number/name are set directly, so they must never also be written as
    // a mapped parameter - that is what the exclusion list is for.
    [Theory]
    [InlineData("Sheet Number")]
    [InlineData("Sheet No")]
    [InlineData("Number")]
    [InlineData("Sheet Name")]
    [InlineData("Name")]
    [InlineData("  SHEET   NAME  ")]
    public void KeyColumns_AreExcludedFromMappedPairs(string param)
    {
        Dictionary<string, string> mapping = new() { [param] = "SomeColumn" };

        Assert.Empty(SheetPlanner.BuildMappedPairs(mapping));
    }

    [Fact]
    public void BuildMappedPairs_DropsBlankSides()
    {
        Dictionary<string, string> mapping = new()
        {
            ["Discipline"] = "DISC",
            ["Blank Column"] = "",
            [" "] = "Orphan",
        };

        Assert.Equal(("Discipline", "DISC"), SheetPlanner.BuildMappedPairs(mapping).Single());
    }

    [Fact]
    public void BuildMappingDict_SkipsRowsMissingEitherSide()
    {
        List<XlsxRow> rows =
        [
            Row(("Revit Parameter", "Discipline"), ("Excel Column", "DISC")),
            Row(("Revit Parameter", "Orphan"), ("Excel Column", "")),
            Row(("Revit Parameter", ""), ("Excel Column", "Stray")),
        ];

        Dictionary<string, string> mapping = SheetPlanner.BuildMappingDict(rows);

        Assert.Equal("DISC", Assert.Contains("Discipline", mapping));
        Assert.Single(mapping);
    }

    [Fact]
    public void KeyColumns_FallBackToTheDefaultNames()
    {
        (string numberCol, string nameCol) = SheetPlanner.KeyColumns(new Dictionary<string, string>());

        Assert.Equal("Sheet Number", numberCol);
        Assert.Equal("Sheet Name", nameCol);
    }

    [Fact]
    public void KeyColumns_HonourAnOverrideFromTheMappingSheet()
    {
        Dictionary<string, string> mapping = new() { ["Sheet Number"] = "DWG_NO" };

        Assert.Equal("DWG_NO", SheetPlanner.KeyColumns(mapping).NumberCol);
    }

    // ------------------------------------------------- titleblock precedence
    // The positional broadcast these tests used to cover was deleted with it.

    private static readonly List<string> Types = ["Office : A1", "Office : A3"];

    private static RowPlan PlanTitleblock(
        string sheetNumber,
        string defaultTitleblock = "",
        string column = "",
        string columnValue = "",
        Dictionary<string, string>? assigned = null)
        => SheetPlanner.PlanRow(
            0,
            Row(("Sheet Number", sheetNumber), ("Sheet Name", "N"), ("TB", columnValue)),
            new RowContext(
                "Sheet Number", "Sheet Name", NoPairs, new HashSet<string>(),
                ExistingMode.Update, true, column, Types, null,
                defaultTitleblock, assigned));

    [Fact]
    public void NothingChosen_MeansNoTitleblock()
        => Assert.Null(PlanTitleblock("A-101").TitleblockIndex);

    [Fact]
    public void TheDefault_AppliesWhenNothingElseNamesOne()
        => Assert.Equal(1, PlanTitleblock("A-101", defaultTitleblock: "Office : A3").TitleblockIndex);

    [Fact]
    public void TheDataColumn_BeatsTheDefault()
        => Assert.Equal(0, PlanTitleblock(
            "A-101", defaultTitleblock: "Office : A3", column: "TB", columnValue: "A1")
            .TitleblockIndex);

    // The whole point of assigning in the window: it must win over both.
    [Fact]
    public void APerSheetAssignment_BeatsTheColumnAndTheDefault()
        => Assert.Equal(1, PlanTitleblock(
            "A-101",
            defaultTitleblock: "Office : A1",
            column: "TB",
            columnValue: "A1",
            assigned: new Dictionary<string, string> { ["A-101"] = "Office : A3" })
            .TitleblockIndex);

    [Fact]
    public void AnAssignmentForAnotherSheet_IsIgnored()
        => Assert.Equal(0, PlanTitleblock(
            "A-101",
            defaultTitleblock: "Office : A1",
            assigned: new Dictionary<string, string> { ["A-999"] = "Office : A3" })
            .TitleblockIndex);

    // Never a silent fallback - the wrong titleblock on a real drawing is worse.
    [Fact]
    public void AnUnmatchedName_IsARowError()
    {
        RowPlan plan = PlanTitleblock("A-101", defaultTitleblock: "Office : A0");

        Assert.Equal(SheetAction.Error, plan.Action);
        Assert.Contains("A0", plan.Error);
    }

    // ------------------------------------------------------------- summary

    [Fact]
    public void Summarize_CountsEveryAction()
    {
        List<RowPlan> plans =
        [
            new(0, SheetAction.Create, "A-101", "", []),
            new(1, SheetAction.Create, "A-102", "", []),
            new(2, SheetAction.Update, "A-103", "", []),
            new(3, SheetAction.Skip, "A-104", "", []),
            new(4, SheetAction.Spacer, "", "", []),
            new(5, SheetAction.Error, "A{105", "", [], Error: "bad"),
        ];

        SheetMakerReport report = SheetPlanner.Summarize(plans, dryRun: true);

        Assert.Equal(6, report.Rows);
        Assert.Equal(2, report.Create);
        Assert.Equal(1, report.Update);
        Assert.Equal(1, report.Skip);
        Assert.Equal(1, report.Spacer);
        Assert.Equal(1, report.Error);
        Assert.True(report.DryRun);
    }
}
