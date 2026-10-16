using System.IO;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Ribbit.Geometry.Sheets;
using Ribbit.Native.Revit;
using Ribbit.Native.Settings;
using Ribbit.Native.Ui;

namespace Ribbit.Native.Tools;

/// <summary>
/// Export Sheets - write the model's sheets, with the parameters chosen here,
/// to an Excel sheet list. Edited in Excel and brought back with Sheet Maker,
/// which recognises the file and shows every change before applying it.
/// </summary>
internal static class SheetExportTool
{
    private const string DialogTitle = "Export Sheets";

    /// <summary>Tests only: answers the save dialog (null = Cancel). Unset, the real dialog shows.</summary>
    internal static Func<string?>? TargetHook { get; set; }

    /// <summary>Tests only: owner for the window, so a test can find and drive it.</summary>
    internal static System.Windows.Window? TestOwner { get; set; }

    public static void Run(UIDocument uidoc)
        => Run(uidoc.Document,
            [.. uidoc.Selection.GetElementIds()],
            uidoc.Application.Application.VersionNumber,
            User(uidoc));

    /// <summary>The whole button, from the window on. Needs only the document and what was selected.</summary>
    internal static void Run(Document doc, IReadOnlyCollection<ElementId> selection, string revitVersion, string user)
    {
        SheetMakerSettings saved = SheetMakerSettings.Load();

        List<FamilySymbol> titleblocks = SheetMakerEngine.CollectTitleblockSymbols(doc);
        List<SheetParameterInfo> catalog = SheetParameterCatalog.Collect(doc, titleblocks);
        List<ExportableSheet> sheets =
        [
            .. SheetExchangeEngine.AllSheets(doc)
                .Select(s => new ExportableSheet(s.Id, s.UniqueId, s.SheetNumber, s.Name, s.IsPlaceholder))
        ];
        List<ElementId> selected =
        [
            .. selection.Where(id => doc.GetElement(id) is ViewSheet)
        ];

        SheetExportWindow window = new(catalog, sheets, selected, saved, SheetSets(doc));
        if (TestOwner is not null)
            window.Owner = TestOwner;
        if (window.ShowDialog() != true || window.Result is null)
            return;

        SheetExportRequest request = window.Result;
        saved.ExportParameters = [.. request.Parameters.Select(p => p.Name)];
        saved.ExportTitleblock = request.IncludeTitleblock;
        saved.ExportSheetIds = [.. request.Sheets.Select(id => doc.GetElement(id)?.UniqueId).OfType<string>()];
        saved.OpenAfterExport = request.OpenAfter;
        saved.Save();

        string? path = PickTarget(doc, saved);
        if (path is null)
            return;

        int count;
        try
        {
            count = Export(doc, request, path, revitVersion, user);
        }
        catch (IOException ex)
        {
            RibbitUi.Alert(
                $"Could not save the sheet list:\n{path}\n\n{ex.Message}\n\n"
                + "If it is open in Excel, close it there first, or save under another name.",
                DialogTitle);
            return;
        }
        catch (UnauthorizedAccessException ex)
        {
            RibbitUi.Alert($"Not allowed to save there:\n{path}\n\n{ex.Message}", DialogTitle);
            return;
        }

        saved.ExportPath = path;
        saved.Save();

        if (request.OpenAfter)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
                return;
            }
            catch (Exception)
            {
                // no Excel, or no association: say where it went instead
            }
        }

        RibbitUi.Notify(
            $"Exported {count} sheet(s) to:\n{path}\n\nEdit it in Excel, save, then use Sheet Maker to bring the changes back.",
            DialogTitle);
    }

    /// <summary>
    /// Writes the export. Separate from <see cref="Run"/> so the in-Revit test
    /// exports through exactly this code, without a save dialog.
    /// </summary>
    internal static int Export(Document doc, SheetExportRequest request, string path, string revitVersion, string user)
    {
        HashSet<ElementId> wanted = [.. request.Sheets];
        List<ViewSheet> sheets = [.. SheetExchangeEngine.AllSheets(doc).Where(s => wanted.Contains(s.Id))];
        List<ExchangeColumn> columns = SheetExchangeEngine.Columns(request.Parameters, request.IncludeTitleblock);
        List<string> titleblocks = SheetExchangeEngine.TitleblockNames(SheetMakerEngine.CollectTitleblockSymbols(doc));

        SheetExchangeEngine.BuildExport(doc, sheets, columns, titleblocks, revitVersion, user).Save(path);
        return sheets.Count;
    }

    /// <summary>The saved sheet sets (Print Setup > View/Sheet Set), with the sheets in each.</summary>
    internal static List<ExportSheetSet> SheetSets(Document doc)
    {
        List<ExportSheetSet> sets = [];
        foreach (ViewSheetSet set in new FilteredElementCollector(doc).OfClass(typeof(ViewSheetSet)).Cast<ViewSheetSet>())
        {
            List<ElementId> ids = [];
            foreach (View v in set.Views)
            {
                if (v is ViewSheet)
                    ids.Add(v.Id);
            }
            if (ids.Count > 0)
                sets.Add(new ExportSheetSet(set.Name, ids));
        }
        sets.Sort((a, b) => NaturalSort.Comparer.Compare(a.Name, b.Name));
        return sets;
    }

    internal static string User(UIDocument uidoc)
    {
        string u = uidoc.Application.Application.Username ?? "";
        return u.Length > 0 ? u : Environment.UserName;
    }

    private static string? PickTarget(Document doc, SheetMakerSettings saved)
    {
        if (TargetHook is not null)
            return TargetHook();

        string folder = "";
        if (saved.ExportPath.Length > 0)
            folder = Path.GetDirectoryName(saved.ExportPath) ?? "";
        if ((folder.Length == 0 || !Directory.Exists(folder)) && doc.PathName.Length > 0)
            folder = Path.GetDirectoryName(doc.PathName) ?? "";

        string name = string.Concat((doc.Title + " - Sheets").Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));

        Microsoft.Win32.SaveFileDialog dlg = new()
        {
            Title = "Save the sheet list",
            Filter = "Excel Workbook (*.xlsx)|*.xlsx",
            DefaultExt = ".xlsx",
            FileName = name + ".xlsx",
            OverwritePrompt = true,
        };
        if (folder.Length > 0 && Directory.Exists(folder))
            dlg.InitialDirectory = folder;

        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }
}
