using Ribbit.Geometry.Sheets;
using Xunit;

namespace Ribbit.Tests;

/// <summary>
/// The form's live preview. Its job is to agree with what the engine will
/// actually do, so these pin the two things that could silently drift from it:
/// the counts, and the column mismatches that produce no exception at all.
/// </summary>
public class SheetPreviewTests
{
    private static XlsxRow Row(params (string Header, string Value)[] cells)
    {
        Dictionary<string, string> d = [];
        foreach ((string header, string value) in cells)
            d[SheetText.Normalize(header)] = value;
        return new XlsxRow(d);
    }

    private static XlsxRow Sheet(string number, string name = "A name")
        => Row(("Sheet Number", number), ("Sheet Name", name));

    private static SheetPreview Build(
        List<XlsxRow> rows,
        Dictionary<string, string>? mapping = null,
        HashSet<string>? existing = null,
        ExistingMode mode = ExistingMode.Update,
        bool blankAsSpacer = true,
        HashSet<string>? sheetParameters = null)
        => SheetPreviewer.Build(
            rows, mapping ?? [], existing ?? [], mode, blankAsSpacer, sheetParameters);

    // --------------------------------------------------------------- counts

    [Fact]
    public void NewNumbers_CountAsCreates()
    {
        SheetPreview p = Build([Sheet("A-101"), Sheet("A-102")]);

        Assert.Equal(2, p.Counts.Rows);
        Assert.Equal(2, p.Counts.Create);
        Assert.Equal(0, p.Counts.Update);
    }

    [Fact]
    public void ExistingNumber_IsAnUpdate_OrASkip_PerTheMode()
    {
        List<XlsxRow> rows = [Sheet("A-101"), Sheet("A-102")];
        HashSet<string> existing = ["A-101"];

        Assert.Equal(1, Build(rows, existing: existing, mode: ExistingMode.Update).Counts.Update);
        Assert.Equal(1, Build(rows, existing: existing, mode: ExistingMode.Skip).Counts.Skip);
    }

    [Fact]
    public void BlankNumber_FollowsTheSpacerOption()
    {
        List<XlsxRow> rows = [Sheet(""), Sheet("A-101")];

        Assert.Equal(1, Build(rows, blankAsSpacer: true).Counts.Spacer);
        Assert.Equal(1, Build(rows, blankAsSpacer: false).Counts.Error);
    }

    // The engine claims a number as it creates it, so the SECOND row carrying it
    // is an update rather than a second create. A preview that planned each row
    // against the untouched model would report two creates and then produce one.
    [Fact]
    public void ARepeatedNumberInTheWorkbook_UpdatesTheRowItJustCreated()
    {
        SheetPreview p = Build([Sheet("A-101"), Sheet("A-101")]);

        Assert.Equal(1, p.Counts.Create);
        Assert.Equal(1, p.Counts.Update);
    }

    // Planning must not write back into the model's own set — the form re-plans
    // on every keystroke and toggle, so a leaked create would turn into an
    // update the next time round.
    [Fact]
    public void Planning_DoesNotMutateTheCallersExistingNumbers()
    {
        HashSet<string> existing = ["A-100"];

        Build([Sheet("A-101")], existing: existing);

        Assert.Equal(["A-100"], existing);
    }

    [Fact]
    public void NoRows_IsNotAnError()
    {
        SheetPreview p = Build([]);

        Assert.Equal(0, p.Counts.Rows);
        Assert.Empty(p.MissingColumns);
        Assert.True(p.NumberColumnPresent);
    }

    // --------------------------------------------------------------- columns

    // The failure this whole band exists for: MAPPING points the sheet number at
    // a column the data tab does not have, so every row fails "Sheet Number is
    // blank" and nothing says why.
    [Fact]
    public void KeyColumnMissingFromTheDataSheet_IsReported()
    {
        SheetPreview p = Build(
            [Sheet("A-101")],
            mapping: new Dictionary<string, string> { ["Sheet Number"] = "Sheet No" });

        Assert.Equal("Sheet No", p.NumberColumn);
        Assert.False(p.NumberColumnPresent);
        Assert.True(p.NameColumnPresent);
    }

    [Fact]
    public void MappedColumnMissingFromTheDataSheet_IsReported()
    {
        SheetPreview p = Build(
            [Sheet("A-101")],
            mapping: new Dictionary<string, string>
            {
                ["Drawn By"] = "Author",
                ["Revision"] = "Rev",
            });

        Assert.Equal(2, p.MappedParameters);
        Assert.Equal(["Author", "Rev"], p.MissingColumns);
    }

    [Fact]
    public void MappedColumnPresentOnTheDataSheet_IsNotReported()
    {
        SheetPreview p = Build(
            [Row(("Sheet Number", "A-101"), ("Sheet Name", "Plan"), ("Author", "JS"))],
            mapping: new Dictionary<string, string> { ["Drawn By"] = "Author" });

        Assert.Equal(1, p.MappedParameters);
        Assert.Empty(p.MissingColumns);
    }

    // ------------------------------------------------------- sheet parameters

    // The Revit-side twin of a missing column, and just as quiet:
    // SetParamIfChanged returns without a word when the parameter is not there.
    [Fact]
    public void MappedParameterMissingFromSheets_IsReported()
    {
        SheetPreview p = Build(
            [Row(("Sheet Number", "A-101"), ("Sheet Name", "Plan"), ("Author", "JS"))],
            mapping: new Dictionary<string, string> { ["Drawn By"] = "Author" },
            sheetParameters: ["Sheet Issue Date"]);

        Assert.Equal(["Drawn By"], p.MissingParameters);
    }

    [Fact]
    public void MappedParameterOnSheets_IsNotReported()
    {
        SheetPreview p = Build(
            [Row(("Sheet Number", "A-101"), ("Sheet Name", "Plan"), ("Author", "JS"))],
            mapping: new Dictionary<string, string> { ["Drawn By"] = "Author" },
            sheetParameters: ["drawn by"]);   // Revit lookups are case-insensitive

        Assert.Empty(p.MissingParameters);
    }

    // A model that cannot answer must not accuse every mapping of being wrong.
    [Fact]
    public void UnknownSheetParameters_SkipsTheCheckEntirely()
    {
        SheetPreview p = Build(
            [Row(("Sheet Number", "A-101"), ("Sheet Name", "Plan"), ("Author", "JS"))],
            mapping: new Dictionary<string, string> { ["Drawn By"] = "Author" },
            sheetParameters: null);

        Assert.Empty(p.MissingParameters);
    }

    // ------------------------------------------------------------- error rows

    // Reported as EXCEL rows, so they can be found in the workbook: index 0 is
    // the row under the header, which Excel calls row 2.
    [Fact]
    public void ErrorRows_AreReportedAsExcelRowNumbers()
    {
        SheetPreview p = Build(
            [Sheet("A-101"), Sheet("bad{number}"), Sheet("A-103")],
            blankAsSpacer: false);

        Assert.Equal([3], p.ErrorRows);
    }

    [Fact]
    public void ExcelRow_CountsFromTheRowBelowTheHeader()
    {
        Assert.Equal(2, SheetPlanner.ExcelRow(0));
        Assert.Equal(3, SheetPlanner.ExcelRow(1));
    }

    // Sheet Number/Name are set directly, never written as mapped parameters -
    // so they must not also be counted as one, or the band overstates the work.
    [Fact]
    public void TheKeyColumns_AreNotCountedAsMappedParameters()
    {
        SheetPreview p = Build(
            [Sheet("A-101")],
            mapping: new Dictionary<string, string>
            {
                ["Sheet Number"] = "Sheet Number",
                ["Sheet Name"] = "Sheet Name",
            });

        Assert.Equal(0, p.MappedParameters);
        Assert.Empty(p.MissingColumns);
    }
}
