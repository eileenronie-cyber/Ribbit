using Ribbit.Geometry.Sheets;
using Xunit;

namespace Ribbit.Tests;

/// <summary>
/// The import's rules, each on a real exported-edited-reread file. Names read
/// as the rule; the fixture's model is the model "now", and the export is
/// taken from it at the moment <c>Export()</c> is called.
/// </summary>
public class SheetImportPlannerTests
{
    private static ExchangeFixture TwoSheets()
    {
        ExchangeFixture fx = new();
        fx.Sheet("id-1", "A101", "Plan", "A1 Metric : A1", "JT", "3", "Yes", "B");
        fx.Sheet("id-2", "A102", "Sections", "A1 Metric : A1", "RT", "0", "No", "");
        return fx;
    }

    private static ImportRow RowFor(ImportPlan plan, string id) => plan.Rows.Single(r => r.SheetId == id);

    // ------------------------------------------------------------ nothing to do

    [Fact]
    public void AnUntouchedExport_ChangesNothing()
    {
        using ExchangeFixture fx = TwoSheets();
        ImportPlan plan = fx.Plan(fx.Export());

        Assert.True(plan.SameModel);
        Assert.All(plan.Rows, r => Assert.Equal(ImportAction.Unchanged, r.Action));
        Assert.Equal(0, plan.Counts.Values);
        Assert.Equal(0, plan.Counts.Actionable);
        Assert.Empty(plan.FileNotes);
    }

    // ------------------------------------------------------------ three-way

    [Fact]
    public void AValueChangedInExcel_IsWritten()
    {
        using ExchangeFixture fx = TwoSheets();
        XlsxWriter book = fx.Export();
        fx.Set(0, "Drawn By", "MK");

        ImportRow row = RowFor(fx.Plan(book), "id-1");

        Assert.Equal(ImportAction.Update, row.Action);
        CellChange c = Assert.Single(row.Changes);
        Assert.Equal(("Drawn By", "JT", "MK", false), (c.Column.Header, c.From, c.To, c.BothChanged));
        Assert.Equal(ImportAction.Unchanged, RowFor(fx.Plan(book), "id-2").Action);
    }

    [Fact]
    public void AnEmptiedCell_ClearsTheValue()
    {
        using ExchangeFixture fx = TwoSheets();
        XlsxWriter book = fx.Export();
        fx.Set(0, "Drawn By", "");

        CellChange c = Assert.Single(RowFor(fx.Plan(book), "id-1").Changes);
        Assert.Equal(("JT", ""), (c.From, c.To));
    }

    [Fact]
    public void AChangeMadeInRevitAfterTheExport_IsKept_WhenExcelDidNotTouchIt()
    {
        using ExchangeFixture fx = TwoSheets();
        XlsxWriter book = fx.Export();
        fx.Revit("id-1", s => ExchangeFixture.WithValue(s, "Drawn By", "Colleague"));

        ImportRow row = RowFor(fx.Plan(book), "id-1");
        Assert.Equal(ImportAction.Unchanged, row.Action);
        Assert.Empty(row.Conflicts);
    }

    [Fact]
    public void ChangedOnBothSides_IsAConflict_AndRevitsValueIsKept()
    {
        using ExchangeFixture fx = TwoSheets();
        XlsxWriter book = fx.Export();
        fx.Revit("id-1", s => ExchangeFixture.WithValue(s, "Drawn By", "Colleague"));
        fx.Set(0, "Drawn By", "Me");

        ImportPlan plan = fx.Plan(book);
        ImportRow row = RowFor(plan, "id-1");

        Assert.Equal(ImportAction.Unchanged, row.Action);
        Assert.Empty(row.Changes);
        CellChange c = Assert.Single(row.Conflicts);
        Assert.Equal(("Colleague", "Me"), (c.From, c.To));
        Assert.Equal(1, plan.Counts.Conflicts);
    }

    [Fact]
    public void ChangedOnBothSides_WritesExcels_WhenAskedTo()
    {
        using ExchangeFixture fx = TwoSheets();
        XlsxWriter book = fx.Export();
        fx.Revit("id-1", s => ExchangeFixture.WithValue(s, "Drawn By", "Colleague"));
        fx.Set(0, "Drawn By", "Me");

        ImportRow row = RowFor(fx.Plan(book, new ImportOptions(ExcelWinsConflicts: true)), "id-1");

        Assert.Equal(ImportAction.Update, row.Action);
        CellChange c = Assert.Single(row.Changes);
        Assert.Equal(("Colleague", "Me", true), (c.From, c.To, c.BothChanged));
        Assert.Empty(row.Conflicts);
    }

    [Fact]
    public void ChangedOnBothSides_ToTheSameValue_IsNotAConflict()
    {
        using ExchangeFixture fx = TwoSheets();
        XlsxWriter book = fx.Export();
        fx.Revit("id-1", s => ExchangeFixture.WithValue(s, "Drawn By", "MK"));
        fx.Set(0, "Drawn By", "MK");

        ImportRow row = RowFor(fx.Plan(book), "id-1");
        Assert.Equal(ImportAction.Unchanged, row.Action);
        Assert.Empty(row.Conflicts);
    }

    // ------------------------------------------------------------ numbers

    [Fact]
    public void ChangingTheNumber_RenumbersTheSameSheet()
    {
        using ExchangeFixture fx = TwoSheets();
        XlsxWriter book = fx.Export();
        fx.Set(0, "Sheet Number", "A201");

        ImportRow row = RowFor(fx.Plan(book), "id-1");
        Assert.Equal(ImportAction.Update, row.Action);
        Assert.Equal("A201", row.Number);
        Assert.Equal(("A101", "A201"), (row.Changes[0].From, row.Changes[0].To));
    }

    [Fact]
    public void SwappingTwoNumbers_IsAllowed()
    {
        using ExchangeFixture fx = TwoSheets();
        XlsxWriter book = fx.Export();
        fx.Set(0, "Sheet Number", "A102");
        fx.Set(1, "Sheet Number", "A101");

        ImportPlan plan = fx.Plan(book);
        Assert.Equal(("A102", ImportAction.Update), (RowFor(plan, "id-1").Number, RowFor(plan, "id-1").Action));
        Assert.Equal(("A101", ImportAction.Update), (RowFor(plan, "id-2").Number, RowFor(plan, "id-2").Action));
    }

    [Fact]
    public void RenumberingOntoASheetThatKeepsItsNumber_IsAnError()
    {
        using ExchangeFixture fx = TwoSheets();
        XlsxWriter book = fx.Export();
        fx.Set(0, "Sheet Number", "A102");

        ImportPlan plan = fx.Plan(book);
        ImportRow row = RowFor(plan, "id-1");
        Assert.Equal(ImportAction.Error, row.Action);
        Assert.Contains("already used", row.Reason);
        Assert.Empty(row.Changes);
        Assert.Equal(ImportAction.Unchanged, RowFor(plan, "id-2").Action);
    }

    [Fact]
    public void RenumberingOntoASheetNotInTheFile_IsAnError()
    {
        using ExchangeFixture fx = TwoSheets();
        XlsxWriter book = fx.Export();
        fx.Sheet("id-3", "A999", "Not exported");
        fx.Set(0, "Sheet Number", "A999");

        Assert.Equal(ImportAction.Error, RowFor(fx.Plan(book), "id-1").Action);
    }

    [Theory]
    [InlineData("")]
    [InlineData("A[1]")]
    public void ABadNumber_FailsTheWholeRow(string number)
    {
        using ExchangeFixture fx = TwoSheets();
        XlsxWriter book = fx.Export();
        fx.Set(0, "Sheet Number", number);
        fx.Set(0, "Drawn By", "MK");

        ImportPlan plan = fx.Plan(book);
        ImportRow row = plan.Rows.Single(r => r.ExcelRow == 2);
        Assert.Equal(ImportAction.Error, row.Action);
        Assert.Empty(row.Changes);
    }

    // ------------------------------------------------------------ new rows

    [Fact]
    public void ARowWithoutAnId_CreatesASheet_WithOnlyItsFilledCells()
    {
        using ExchangeFixture fx = TwoSheets();
        XlsxWriter book = fx.Export();
        List<string> r = fx.AddRow("A103", "Details", "A3 Metric : A3");
        r[fx.Col("Drawn By")] = "MK";

        ImportRow row = fx.Plan(book).Rows.Single(x => x.Action == ImportAction.Create);
        Assert.Equal(("A103", "Details", 4), (row.Number, row.Name, row.ExcelRow));
        Assert.Equal(1, row.TitleblockIndex);
        Assert.Equal(["Sheet Number", "Sheet Name", "Drawn By"], row.Changes.Select(c => c.Column.Header));
    }

    [Fact]
    public void ANewRow_WithNoTitleblock_UsesTheDefault_OrAPlaceholder()
    {
        using ExchangeFixture fx = TwoSheets();
        XlsxWriter book = fx.Export();
        fx.AddRow("A103", "Details");

        Assert.Equal(1, fx.Plan(book, new ImportOptions("A3 Metric : A3")).Rows.Single(x => x.Action == ImportAction.Create).TitleblockIndex);
        Assert.Null(fx.Plan(book).Rows.Single(x => x.Action == ImportAction.Create).TitleblockIndex);
    }

    [Fact]
    public void ANewRow_NamingAnUnknownTitleblock_IsAnError_NotAGuess()
    {
        using ExchangeFixture fx = TwoSheets();
        XlsxWriter book = fx.Export();
        fx.AddRow("A103", "Details", "B0 Imperial");

        ImportRow row = fx.Plan(book, new ImportOptions("A3 Metric : A3")).Rows.Single(r => r.ExcelRow == 4);
        Assert.Equal(ImportAction.Error, row.Action);
        Assert.Contains("B0 Imperial", row.Reason);
    }

    [Fact]
    public void ANewRow_WithAnExistingNumber_UpdatesThatSheet_AndBlankMeansLeaveIt()
    {
        using ExchangeFixture fx = TwoSheets();
        fx.Sheet("id-3", "A999", "Not exported", "A1 Metric : A1", "JT", "1", "No", "");
        XlsxWriter book = fx.Export();
        fx.Rows.RemoveAt(2);                             // it was not in this export
        List<string> r = fx.AddRow("A999", "");          // blank name: leave it
        r[fx.Col("Drawn By")] = "MK";

        ImportRow row = RowFor(fx.Plan(book), "id-3");
        Assert.Equal(ImportAction.Update, row.Action);
        Assert.Equal(["Drawn By"], row.Changes.Select(c => c.Column.Header));
        Assert.Equal("Not exported", row.Name);
    }

    [Fact]
    public void ANewRow_TakingANumberFreedByARenumber_CreatesASheet()
    {
        using ExchangeFixture fx = TwoSheets();
        XlsxWriter book = fx.Export();
        fx.Set(0, "Sheet Number", "A201");
        fx.AddRow("A101", "New plan");

        ImportPlan plan = fx.Plan(book);
        Assert.Equal(ImportAction.Update, RowFor(plan, "id-1").Action);
        ImportRow created = plan.Rows.Single(r => r.ExcelRow == 4);
        Assert.Equal((ImportAction.Create, "A101"), (created.Action, created.Number));
    }

    [Fact]
    public void TwoNewRows_WithTheSameNumber_AreBothErrors()
    {
        using ExchangeFixture fx = TwoSheets();
        XlsxWriter book = fx.Export();
        fx.AddRow("A103", "One");
        fx.AddRow("A103", "Two");

        ImportPlan plan = fx.Plan(book);
        Assert.All(plan.Rows.Where(r => r.ExcelRow >= 4), r =>
        {
            Assert.Equal(ImportAction.Error, r.Action);
            Assert.Contains("used twice", r.Reason);
        });
    }

    [Fact]
    public void ANewRow_AndAnExportedRow_ForTheSameSheet_TheNewOneFails()
    {
        using ExchangeFixture fx = TwoSheets();
        XlsxWriter book = fx.Export();
        fx.AddRow("A101", "Duplicate");

        ImportPlan plan = fx.Plan(book);
        ImportRow dup = plan.Rows.Single(r => r.ExcelRow == 4);
        Assert.Equal(ImportAction.Error, dup.Action);
        Assert.Contains("row 2", dup.Reason);
    }

    // ------------------------------------------------------------ copied rows

    [Fact]
    public void ACopiedRow_WithANewNumber_BecomesANewSheet_AndTheOriginalStays()
    {
        using ExchangeFixture fx = TwoSheets();
        XlsxWriter book = fx.Export();
        List<string> copy = [.. fx.Rows[0]];
        copy[fx.Col("Sheet Number")] = "A101a";
        fx.Rows.Add(copy);

        ImportPlan plan = fx.Plan(book);
        Assert.Equal(ImportAction.Unchanged, RowFor(plan, "id-1").Action);
        ImportRow created = plan.Rows.Single(r => r.ExcelRow == 4);
        Assert.Equal((ImportAction.Create, "A101a"), (created.Action, created.Number));
        Assert.Contains(created.Notes, n => n.Contains("Copied from row 2"));
        Assert.Contains(created.Changes, c => c.Column.Header == "Drawn By" && c.To == "JT");
    }

    [Fact]
    public void CopiedRows_ThatCannotBeToldApart_AreErrors()
    {
        using ExchangeFixture fx = TwoSheets();
        XlsxWriter book = fx.Export();
        List<string> copy = [.. fx.Rows[0]];
        copy[fx.Col("Sheet Number")] = "A101b";
        fx.Rows.Add(copy);
        fx.Set(0, "Sheet Number", "A101a");

        ImportPlan plan = fx.Plan(book);
        Assert.All(plan.Rows.Where(r => r.ExcelRow is 2 or 4), r =>
        {
            Assert.Equal(ImportAction.Error, r.Action);
            Assert.Contains("same Ribbit ID", r.Reason);
        });
    }

    // ------------------------------------------------------------ ids and models

    [Fact]
    public void ASheetDeletedAfterTheExport_IsNotCreatedAgain()
    {
        using ExchangeFixture fx = TwoSheets();
        XlsxWriter book = fx.Export();
        fx.Model.RemoveAll(s => s.Id == "id-2");

        ImportRow row = fx.Plan(book).Rows.Single(r => r.ExcelRow == 3);
        Assert.Equal(ImportAction.Skip, row.Action);
        Assert.Contains("deleted", row.Reason);
    }

    [Fact]
    public void AFileFromAnotherModel_MatchesByNumber_AndBlankLeavesValues()
    {
        using ExchangeFixture fx = TwoSheets();
        XlsxWriter book = fx.Export(modelKey: "other-model");
        fx.Model.Clear();
        fx.Sheet("x-1", "A101", "Plan", "A1 Metric : A1", "Someone", "3", "Yes", "B");
        fx.Set(1, "Drawn By", "");                       // blank: must not clear anything

        ImportPlan plan = fx.Plan(book);
        Assert.False(plan.SameModel);
        Assert.Contains(plan.FileNotes, n => n.Contains("not this model"));

        ImportRow row = RowFor(plan, "x-1");
        Assert.Equal(ImportAction.Update, row.Action);
        Assert.Equal(("Drawn By", "Someone", "JT"), (row.Changes.Single().Column.Header, row.Changes.Single().From, row.Changes.Single().To));
        Assert.Equal(ImportAction.Create, plan.Rows.Single(r => r.ExcelRow == 3).Action);
    }

    // The ids ARE the model's sheets: a key that does not match (missing, or
    // changed) must not turn a genuine export into a by-number, no-clear import.
    [Fact]
    public void AFileWhoseIdsAreThisModelsSheets_IsThisModel_WhateverItsKey()
    {
        using ExchangeFixture fx = TwoSheets();
        XlsxWriter book = fx.Export(modelKey: "");
        fx.Set(0, "Drawn By", "");

        ImportPlan plan = fx.Plan(book);
        Assert.True(plan.SameModel);
        Assert.Empty(plan.FileNotes);
        CellChange c = Assert.Single(RowFor(plan, "id-1").Changes);
        Assert.Equal(("JT", ""), (c.From, c.To));
    }
    [Fact]
    public void ARowWithNothingButItsId_LeavesItsSheetAlone()
    {
        using ExchangeFixture fx = TwoSheets();
        XlsxWriter book = fx.Export();
        for (int c = 0; c < fx.Columns.Count; c++)
            fx.Rows[0][c] = "";

        ImportRow row = fx.Plan(book).Rows.Single(r => r.ExcelRow == 2);
        Assert.Equal(ImportAction.Skip, row.Action);
    }

    // ------------------------------------------------------------ value rules

    [Fact]
    public void AReadOnlyColumn_IsNeverWritten()
    {
        using ExchangeFixture fx = TwoSheets();
        XlsxWriter book = fx.Export();
        fx.Set(0, "Current Revision", "C");

        ImportRow row = RowFor(fx.Plan(book), "id-1");
        Assert.Equal(ImportAction.Unchanged, row.Action);
        Assert.Contains(row.Notes, n => n.Contains("read-only"));
    }

    [Theory]
    [InlineData("Revision Count", "three", "whole number")]
    [InlineData("Revision Count", "", "cannot clear")]
    [InlineData("Issued", "maybe", "Yes or No")]
    [InlineData("Issued", "", "cannot clear")]
    [InlineData("Sheet Name", "", "cannot be blank")]
    [InlineData("Titleblock", "Nope", "No titleblock type matches")]
    [InlineData("Titleblock", "", "cannot be removed")]
    public void AValueRevitCannotTake_IsAProblem_AndTheRestOfTheRowStillApplies(string header, string value, string reason)
    {
        using ExchangeFixture fx = TwoSheets();
        XlsxWriter book = fx.Export();
        fx.Set(0, header, value);
        fx.Set(0, "Drawn By", "MK");

        ImportRow row = RowFor(fx.Plan(book), "id-1");
        Assert.Equal(ImportAction.Update, row.Action);
        Assert.Equal(["Drawn By"], row.Changes.Select(c => c.Column.Header));
        CellProblem p = Assert.Single(row.Problems);
        Assert.Equal(header, p.Column.Header);
        Assert.Contains(reason, p.Reason);
    }

    [Theory]
    [InlineData("Issued", "yes")]
    [InlineData("Issued", "TRUE")]
    [InlineData("Issued", "1")]
    [InlineData("Revision Count", "3.0")]
    [InlineData("Revision Count", " 3 ")]
    public void AnotherSpellingOfTheSameValue_IsNotAChange(string header, string value)
    {
        using ExchangeFixture fx = TwoSheets();
        XlsxWriter book = fx.Export();
        fx.Set(0, header, value);

        Assert.Equal(ImportAction.Unchanged, RowFor(fx.Plan(book), "id-1").Action);
    }

    [Fact]
    public void YesNoAndIntegers_AreWrittenInOneSpelling()
    {
        using ExchangeFixture fx = TwoSheets();
        XlsxWriter book = fx.Export();
        fx.Set(0, "Issued", "n");
        fx.Set(0, "Revision Count", "4.0");

        ImportRow row = RowFor(fx.Plan(book), "id-1");
        Assert.Equal("No", row.Changes.Single(c => c.Column.Header == "Issued").To);
        Assert.Equal("4", row.Changes.Single(c => c.Column.Header == "Revision Count").To);
    }

    [Fact]
    public void LineEndings_DoNotCountAsAChange()
    {
        using ExchangeFixture fx = TwoSheets();
        fx.Revit("id-1", s => ExchangeFixture.WithValue(s, "Drawn By", "two\r\nlines"));
        XlsxWriter book = fx.Export();
        fx.Set(0, "Drawn By", "two\nlines");

        Assert.Equal(ImportAction.Unchanged, RowFor(fx.Plan(book), "id-1").Action);
    }

    [Fact]
    public void ChangingTheTitleblock_ResolvesTheType()
    {
        using ExchangeFixture fx = TwoSheets();
        XlsxWriter book = fx.Export();
        fx.Set(0, "Titleblock", "A3");     // a unique type name is enough

        ImportRow row = RowFor(fx.Plan(book), "id-1");
        Assert.Equal(ImportAction.Update, row.Action);
        Assert.Equal(1, row.TitleblockIndex);
    }

    [Fact]
    public void ACheckedOutSheet_IsLeftAlone_OnlyWhenThereIsSomethingToWrite()
    {
        using ExchangeFixture fx = TwoSheets();
        XlsxWriter book = fx.Export();
        fx.Revit("id-1", s => s with { Locked = true, LockedBy = "colleague" });
        fx.Revit("id-2", s => s with { Locked = true, LockedBy = "colleague" });
        fx.Set(0, "Drawn By", "MK");

        ImportPlan plan = fx.Plan(book);
        Assert.Equal(ImportAction.Locked, RowFor(plan, "id-1").Action);
        Assert.Contains("colleague", RowFor(plan, "id-1").Reason);
        Assert.Equal(ImportAction.Unchanged, RowFor(plan, "id-2").Action);
    }

    // A checked-out sheet does not move, so the number it would have freed is
    // still taken: a new row asking for it must not be planned as a create.
    [Fact]
    public void ANumberACheckedOutSheetWouldFree_IsStillTaken()
    {
        using ExchangeFixture fx = TwoSheets();
        XlsxWriter book = fx.Export();
        fx.Revit("id-1", s => s with { Locked = true });
        fx.Set(0, "Sheet Number", "A201");
        fx.AddRow("A101", "Wants the old number");

        ImportPlan plan = fx.Plan(book);
        Assert.Equal(ImportAction.Locked, RowFor(plan, "id-1").Action);
        ImportRow newRow = plan.Rows.Single(r => r.ExcelRow == 4);
        Assert.NotEqual(ImportAction.Create, newRow.Action);
        Assert.Equal(ImportAction.Error, newRow.Action);
    }
    // ------------------------------------------------------------ the tab itself

    [Fact]
    public void ADeletedColumn_IsLeftAlone_AndSaidSo()
    {
        using ExchangeFixture fx = TwoSheets();
        XlsxWriter book = fx.Export();
        int col = fx.Col("Drawn By");
        book.Sheets[0].Columns.RemoveAt(col);
        foreach (List<string> r in fx.Rows)
            r.RemoveAt(col);

        ImportPlan plan = fx.Plan(book);
        Assert.Contains(plan.FileNotes, n => n.Contains("'Drawn By' is no longer"));
        Assert.All(plan.Rows, r => Assert.Equal(ImportAction.Unchanged, r.Action));
    }

    [Fact]
    public void AnAddedColumn_IsIgnored_AndSaidSo()
    {
        using ExchangeFixture fx = TwoSheets();
        XlsxWriter book = fx.Export();
        book.Sheets[0].Columns.Add(new XlsxColumn("My notes"));
        foreach (List<string> r in fx.Rows)
            r.Add("note");

        ImportPlan plan = fx.Plan(book);
        Assert.Contains(plan.FileNotes, n => n.Contains("'My notes' was not in the export"));
        Assert.All(plan.Rows, r => Assert.Equal(ImportAction.Unchanged, r.Action));
    }

    // A blank row in the middle used to END the read, silently dropping every
    // sheet below it.
    [Fact]
    public void ABlankRowInTheMiddle_DoesNotHideTheRowsBelowIt()
    {
        using ExchangeFixture fx = TwoSheets();
        XlsxWriter book = fx.Export();
        fx.Rows.Insert(1, [.. Enumerable.Repeat("", fx.Columns.Count + 1)]);
        fx.Set(2, "Drawn By", "MK");

        ImportPlan plan = fx.Plan(book);
        Assert.Equal(2, plan.Rows.Count);
        ImportRow row = RowFor(plan, "id-2");
        Assert.Equal((ImportAction.Update, 4), (row.Action, row.ExcelRow));
    }

    [Fact]
    public void Counts_AddUp()
    {
        using ExchangeFixture fx = TwoSheets();
        XlsxWriter book = fx.Export();
        fx.Set(0, "Drawn By", "MK");
        fx.Set(0, "Sheet Name", "Plan 2");
        fx.AddRow("A103", "New");
        fx.AddRow("A1[", "Bad");

        ImportCounts c = fx.Plan(book).Counts;
        Assert.Equal((4, 1, 1, 1, 1), (c.Rows, c.Create, c.Update, c.Unchanged, c.Error));
        Assert.Equal(4, c.Values);   // two on the update, number + name on the create
        Assert.Equal(2, c.Actionable);
    }
}
