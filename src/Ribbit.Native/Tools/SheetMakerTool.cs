using System.IO;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Ribbit.Geometry.Sheets;
using Ribbit.Native.Revit;
using Ribbit.Native.Settings;
using Ribbit.Native.Ui;

namespace Ribbit.Native.Tools;

/// <summary>
/// SheetMaker — create/update sheets from an Excel workbook. Ported from
/// _Ribbit.tab/Sheets.panel/SheetMaker.pushbutton/script.py.
///
/// Reads a data worksheet (one row per sheet) and a mapping worksheet
/// (Revit Parameter | Excel Column), then creates or updates ViewSheets
/// accordingly, applying a chosen titleblock type — or a placeholder if none is
/// chosen — and writing every mapped parameter.
///
/// The Python bundle was pinned to IronPython solely because its CPython3 engine
/// had no WPFWindow for this eight-field form, and that forced a dependency-free
/// Excel reader since IronPython has no openpyxl. The reader survives here
/// because it turned out to be the right design anyway: no NuGet package, single
/// DLL deployment.
/// </summary>
internal static class SheetMakerTool
{
    private const string DialogTitle = "SheetMaker";

    /// <summary>Below this many rows the run finishes faster than the dialog appears.</summary>
    private const int ProgressRowThreshold = 25;

    /// <summary>Tests only: answers the file picker (null = Cancel). Unset, the real dialog shows.</summary>
    internal static Func<string?>? PickHook { get; set; }

    /// <summary>Tests only: owner for the tool's windows, so a test can find and drive them.</summary>
    internal static System.Windows.Window? TestOwner { get; set; }

    public static void Run(UIDocument uidoc) => Run(uidoc.Document);

    /// <summary>The whole button, from the file picker on. Needs only the document.</summary>
    internal static void Run(Document doc)
    {
        RevitLinks.EnsureInitialized();
        SheetMakerSettings saved = SheetMakerSettings.Load();

        string? path = PickWorkbook(saved);
        if (path is null)
            return;

        // A sheet list exported by Ribbit carries its own columns and an export
        // copy, so it needs no mapping at all. Any other workbook goes to the
        // mapping form, exactly as before.
        if (ExchangeFile.IsExchangeFile(path))
        {
            RunImport(doc, path, saved);
            return;
        }

        saved.FilePath = path;
        RunMapped(doc, saved);
    }

    /// <summary>The workbook to read, starting where the last one was. Null: cancelled.</summary>
    private static string? PickWorkbook(SheetMakerSettings saved)
    {
        if (PickHook is not null)
            return PickHook();

        string last = SheetMakerSettings.CleanPath(saved.FilePath);
        if (last.Length == 0)
            last = saved.ExportPath;

        Microsoft.Win32.OpenFileDialog dlg = new()
        {
            Title = "Sheet Maker - pick the Excel sheet list",
            Filter = "Excel Workbooks (*.xlsx;*.xlsm)|*.xlsx;*.xlsm|All Files (*.*)|*.*",
        };

        string? folder = last.Length > 0 ? Path.GetDirectoryName(last) : null;
        if (folder is not null && Directory.Exists(folder))
        {
            dlg.InitialDirectory = folder;
            if (File.Exists(last))
                dlg.FileName = Path.GetFileName(last);
        }

        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }

    /// <summary>Read a Ribbit sheet list. Throws <see cref="SheetMakerError"/> with a reason.</summary>
    internal static ExchangeFile ReadExchange(string path)
    {
        using XlsxWorkbook book = XlsxWorkbook.Open(path);
        return ExchangeFile.Read(book);
    }

    /// <summary>The titleblock most sheets already use: the sensible default for new ones.</summary>
    internal static string MostUsedTitleblock(IEnumerable<ModelSheet> model)
        => model.Where(s => s.Titleblock.Length > 0)
                .GroupBy(s => s.Titleblock)
                .OrderByDescending(g => g.Count())
                .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.Key)
                .FirstOrDefault() ?? "";

    /// <summary>A Ribbit sheet list: show every change, then apply it on the user's say-so.</summary>
    private static void RunImport(Document doc, string path, SheetMakerSettings saved)
    {
        ExchangeFile file;
        try
        {
            file = ReadExchange(path);
        }
        catch (Exception ex)
        {
            RibbitUi.Alert($"Could not read the sheet list:\n{path}\n\n{ex.Message}", DialogTitle);
            return;
        }

        List<FamilySymbol> titleblocks = SheetMakerEngine.CollectTitleblockSymbols(doc);
        List<string> names = SheetExchangeEngine.TitleblockNames(titleblocks);
        string suggested = MostUsedTitleblock(SheetExchangeEngine.Snapshot(doc, [ExchangeColumn.Titleblock]));

        SheetImportWindow window = new(
            path, file, ReadExchange,
            columns => SheetExchangeEngine.Snapshot(doc, columns),
            SheetExchangeEngine.ModelKey(doc), names, saved, suggested);
        if (TestOwner is not null)
            window.Owner = TestOwner;

        if (window.ShowDialog() != true || window.Plan is null)
            return;

        ImportPlan plan = window.Plan;
        saved.FilePath = path;
        saved.ImportDefaultTitleblock = window.Options.DefaultTitleblock;
        saved.ImportExcelWins = window.Options.ExcelWinsConflicts;
        saved.Save();

        int work = plan.Counts.Actionable;
        using ProgressDialog? progress = work >= ProgressRowThreshold
            ? new ProgressDialog("Sheet Maker - sheet {0} of {1}", cancellable: false)
            : null;

        ApplyOutcome outcome;
        using (Transaction t = new(doc, "Sheet Maker: import sheet list"))
        {
            // Warnings are cleared and a genuine error rolls the whole import
            // back with its reason, instead of a Revit dialog mid-run.
            ErrorCollector errors = ErrorCollector.Start(t);
            try
            {
                outcome = SheetExchangeEngine.Apply(doc, plan, titleblocks, (i, n) => progress?.Update(i, n));
            }
            catch (Exception ex)
            {
                t.RollBack();
                RibbitUi.Alert($"Rolled back - nothing was created or changed.\n\nUnexpected error: {ex.Message}", DialogTitle);
                return;
            }

            if (t.Commit() != TransactionStatus.Committed)
            {
                RibbitUi.Alert(
                    "Revit rolled the import back - nothing was created or changed.\n\n"
                    + string.Join("\n", errors.Errors.DefaultIfEmpty("No reason was given.")),
                    DialogTitle);
                return;
            }
        }

        RibbitUi.ShowReport($"{DialogTitle} - import results", SheetImportReport.Build(path, plan, outcome));
        RibbitUi.Notify(
            $"Created {outcome.Created}, updated {outcome.Updated} sheet(s)."
            + (outcome.Failed + outcome.Refused > 0
                ? $"\n\n{outcome.Failed} row(s) and {outcome.Refused} value(s) Revit would not take - see the report."
                : ""),
            DialogTitle);
    }

    /// <summary>Any other workbook: the mapping form, as SheetMaker has always worked.</summary>
    private static void RunMapped(Document doc, SheetMakerSettings saved)
    {
        List<FamilySymbol> titleblocks = SheetMakerEngine.CollectTitleblockSymbols(doc);

        // The form plans the workbook live, and create-vs-update turns on which
        // sheet numbers are already in the model.
        List<string> existingNumbers = [.. SheetMakerEngine.ExistingSheetsByNumber(doc).Keys];

        // Discovered from the model, so the mapping can be checked against what
        // sheets and titleblocks actually carry.
        List<SheetParameterInfo> catalog = SheetParameterCatalog.Collect(doc, titleblocks);
        List<string> locked = [.. SheetMakerEngine.LockedSheetOwners(doc).Keys];

        SheetMakerWindow window = new(titleblocks, saved, existingNumbers, catalog, locked);
        if (TestOwner is not null)
            window.Owner = TestOwner;
        if (window.ShowDialog() != true || window.Result is null)
            return;

        SheetMakerChoice choice = window.Result;

        string? problem = Validate(choice);
        if (problem is not null)
        {
            RibbitUi.Alert(problem, DialogTitle);
            return;
        }

        saved.FilePath = choice.FilePath;
        saved.DataSheet = choice.DataSheet;
        saved.MappingSheet = choice.MappingSheet;
        saved.UpdateExisting = choice.UpdateExisting;
        saved.BlankAsSpacer = choice.BlankAsSpacer;
        saved.DryRun = choice.DryRun;
        saved.TitleblockColumn = choice.TitleblockColumn;
        saved.Mapping = choice.Mapping;
        saved.DefaultTitleblock = choice.DefaultTitleblock;
        saved.TitleblockBySheet = choice.TitleblockBySheet;
        saved.Save();

        // The mapping comes from the WINDOW now, not the workbook - the form is
        // where it was built and checked against the model, so re-reading the
        // MAPPING sheet here would silently discard every edit just made.
        Dictionary<string, string> mapping = choice.Mapping;

        List<XlsxRow> rows;
        try
        {
            using XlsxWorkbook workbook = XlsxWorkbook.Open(choice.FilePath);
            rows = workbook.ReadWorksheet(choice.DataSheet);
        }
        catch (SheetMakerError ex)
        {
            RibbitUi.Alert(ex.Message, DialogTitle);
            return;
        }
        catch (Exception ex)
        {
            RibbitUi.Alert($"Could not read the workbook:\n\n{ex.Message}", DialogTitle);
            return;
        }

        if (rows.Count == 0)
        {
            RibbitUi.Alert(
                $"'{choice.DataSheet}' has no data rows below the header row - nothing to do.",
                DialogTitle);
            return;
        }

        ExistingMode existingMode = choice.UpdateExisting ? ExistingMode.Update : ExistingMode.Skip;

        List<SheetRowResult> results;
        SheetMakerReport report;

        if (choice.DryRun)
        {
            // No transaction at all: a dry run must never be able to write, which
            // is the whole reason the source's version of this was broken.
            (results, report) = SheetMakerEngine.Run(
                doc, rows, mapping, choice.Titleblocks, existingMode,
                dryRun: true, blankAsSpacer: choice.BlankAsSpacer,
                titleblockColumn: choice.TitleblockColumn,
                defaultTitleblock: choice.DefaultTitleblock,
                titleblockBySheet: choice.TitleblockBySheet);
        }
        else
        {
            // Each create activates a titleblock symbol and writes every mapped
            // parameter, so a few hundred rows is long enough for Revit to look
            // hung. Below the threshold the dialog would be a flash of nothing.
            using ProgressDialog? progress = rows.Count >= ProgressRowThreshold
                ? new ProgressDialog("SheetMaker - row {0} of {1}", cancellable: false)
                : null;

            using Transaction t = new(doc, "SheetMaker: Create/Update Sheets");
            t.Start();
            try
            {
                (results, report) = SheetMakerEngine.Run(
                    doc, rows, mapping, choice.Titleblocks, existingMode,
                    dryRun: false, blankAsSpacer: choice.BlankAsSpacer,
                    progress: (i, n) => progress?.Update(i, n),
                    titleblockColumn: choice.TitleblockColumn,
                defaultTitleblock: choice.DefaultTitleblock,
                titleblockBySheet: choice.TitleblockBySheet);
                t.Commit();
            }
            catch (Exception ex)
            {
                t.RollBack();
                RibbitUi.Alert(
                    $"Rolled back - nothing was created or changed.\n\nUnexpected error: {ex.Message}",
                    DialogTitle);
                return;
            }
        }

        string heading = report.DryRun
            ? $"{DialogTitle} - dry run (no changes were made)"
            : $"{DialogTitle} - results";

        RibbitUi.ShowReport(heading, SheetMakerReportHtml.Build(results, report));

        RibbitUi.Notify(
            report.Error > 0
                ? $"SheetMaker finished with {report.Error} error(s). See the report window."
                : $"SheetMaker finished.\n\nCreated: {report.Create}   Updated: {report.Update}   "
                  + $"Skipped: {report.Skip}   Spacers: {report.Spacer}",
            DialogTitle);
    }

    private static string? Validate(SheetMakerChoice choice)
    {
        if (choice.FilePath.Length == 0)
            return "Please choose an Excel workbook.";
        if (!File.Exists(choice.FilePath))
            return $"File not found:\n{choice.FilePath}";
        if (choice.DataSheet.Length == 0)
            return "Please choose a Data worksheet.";

        // No check on the Mapping worksheet: the mapping is built in the window
        // now, and a run with none still creates sheets with number and name.
        return null;
    }
}
