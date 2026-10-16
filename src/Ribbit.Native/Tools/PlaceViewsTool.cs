using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Ribbit.Native.Revit;
using Ribbit.Native.Settings;
using Ribbit.Native.Ui;

namespace Ribbit.Native.Tools;

/// <summary>
/// Place Views on Sheets (now the "Placing Views to Sheets v3.dyn" design: views
/// first, sheets ranked by how well their names match, a change guard around the
/// write). Replaces the sheet-first pyRevit port - one window instead of a sheet
/// list plus a pairing form, and no view has to be matched by eye.
/// </summary>
internal static class PlaceViewsTool
{
    /// <summary>Tests only: owner for the window, so a test can drive it.</summary>
    internal static System.Windows.Window? TestOwner { get; set; }

    /// <summary>The last run's result (tests read it).</summary>
    internal static PlaceRunResult? LastResult { get; private set; }

    public static void Run(UIDocument uidoc) => Run(uidoc.Document);

    internal static void Run(Document doc)
    {
        RevitLinks.EnsureInitialized();
        LastResult = null;

        (List<PlaceViewRow> views, List<PlaceSheetRow> sheets, int placed) = PlaceViewsEngine.Collect(doc);
        if (sheets.Count == 0)
        {
            RibbitUi.Alert("This model has no sheets to place views on.", PlaceViewsWindow.DialogTitle);
            return;
        }
        if (views.Count == 0)
        {
            RibbitUi.Alert($"Every view that can go on a sheet is already on one ({placed} placed).", PlaceViewsWindow.DialogTitle);
            return;
        }

        PlaceViewsWindow window = new(views, sheets, placed, PlaceViewsSettings.Load());
        if (TestOwner is not null)
            window.Owner = TestOwner;
        bool ok = window.ShowDialog() == true;
        window.SettingsToSave().Save();
        if (!ok || window.Placement is null)
            return;

        PlaceRunResult result = PlaceViewsEngine.Place(doc, window.Pairs, window.Placement);
        LastResult = result;
        RibbitUi.ShowReport(Heading(result), Report(result));
    }

    private static string Heading(PlaceRunResult r)
        => r.GuardViolations.Count > 0 ? $"{PlaceViewsWindow.DialogTitle} - ROLLED BACK by the change guard"
            : r.Error is not null ? $"{PlaceViewsWindow.DialogTitle} - rolled back"
            : $"{PlaceViewsWindow.DialogTitle} - {r.Placed} placed, {r.Rows.Count - r.Placed} skipped";

    private static string Report(PlaceRunResult r)
    {
        StringBuilder sb = new();
        if (r.GuardViolations.Count > 0)
        {
            ReportHtml.Note(sb, "<strong>Nothing was placed.</strong> Placing these views changed something outside what "
                                + "placing a view should touch, so the whole run was rolled back. The offending elements:<br><code>"
                                + string.Join("<br>", r.GuardViolations.Select(ReportHtml.Escape)) + "</code>");
        }
        else if (r.Error is not null)
        {
            ReportHtml.Note(sb, "<strong>Nothing was placed.</strong> " + ReportHtml.Escape(r.Error));
        }

        List<IReadOnlyList<string>> rows = [.. r.Rows
            .OrderBy(x => x.Placed ? 1 : 0)
            .Select(x => (IReadOnlyList<string>)new[]
            {
                x.Placed ? "<span class=\"ok\">placed</span>" : "<span class=\"skip\">skipped</span>",
                ReportHtml.Link(x.View, new ElementId(x.ViewId)),
                ReportHtml.Link(x.Sheet, new ElementId(x.SheetId)),
                ReportHtml.Escape(x.Why.Length > 0 ? x.Why : "-"),
            })];
        ReportHtml.Table(sb, "Views", ["Result", "View", "Sheet", "Note"], rows);

        if (r.InternalRecords.Count > 0)
            ReportHtml.Note(sb, $"Change guard: {r.InternalRecords.Count} of Revit's internal view/sheet records were updated by the "
                                + "placement (expected; listed for the record):<br><code>"
                                + string.Join("<br>", r.InternalRecords.Take(40).Select(ReportHtml.Escape))
                                + (r.InternalRecords.Count > 40 ? "<br>..." : "") + "</code>");
        ReportHtml.Note(sb, "Ctrl+Z in Revit undoes the whole run.");
        return sb.ToString();
    }
}
