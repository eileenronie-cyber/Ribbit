using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Ribbit.Native.Revit;
using Ribbit.Native.Settings;
using Ribbit.Native.Ui;

namespace Ribbit.Native.Tools;

/// <summary>
/// Room Tag Checker (was "Room Tag Checker v3.dyn"): tag the linked rooms a
/// plan view is missing, and delete the extra tags on rooms that have several.
/// The window shows the plan; Apply carries out exactly that plan, as one undo step.
/// </summary>
internal static class RoomTagTool
{
    internal static System.Windows.Window? TestOwner { get; set; }
    internal static RtResult? LastResult { get; private set; }

    public static void Run(UIDocument uidoc) => Run(uidoc.Document);

    internal static void Run(Document doc)
    {
        RevitLinks.EnsureInitialized();
        LastResult = null;
        RtScan scan = RoomTagEngine.Scan(doc);
        if (scan.Views.Count == 0)
        {
            RibbitUi.Alert(scan.Description, RoomTagWindow.DialogTitle);
            return;
        }

        RoomTagWindow window = new(scan, RoomTagSettings.Load(), (pairs, opt) => RoomTagEngine.Plan(doc, pairs, opt));
        if (TestOwner is not null)
            window.Owner = TestOwner;
        bool ok = window.ShowDialog() == true;
        window.SettingsToSave().Save();
        if (!ok || window.Plan is null)
            return;

        RtResult result = RoomTagEngine.Apply(doc, window.Plan, window.Options);
        LastResult = result;
        RibbitUi.ShowReport($"{RoomTagWindow.DialogTitle} - {result.Placed} tagged, {result.Deleted} duplicate(s) deleted", Report(window.Plan, result));
    }

    private static string Report(RtPlan plan, RtResult r)
    {
        StringBuilder sb = new();
        List<IReadOnlyList<string>> rows = [];
        rows.AddRange(r.Skipped.Concat(plan.Skipped).Select(s => (IReadOnlyList<string>)new[] { "<span class=\"skip\">skipped</span>", ReportHtml.Escape(s) }));
        rows.AddRange(r.Tagged.Select(s => (IReadOnlyList<string>)new[] { "<span class=\"ok\">tagged</span>", ReportHtml.Escape(s) }));
        if (r.Deleted > 0)
            rows.AddRange(plan.Deletes.Select(d => (IReadOnlyList<string>)new[] { "<span class=\"warn\">deleted</span>", ReportHtml.Escape($"{d.ViewName}  |  {d.Room}  -  tag {d.TagId} (kept {d.KeepId})") }));
        ReportHtml.Table(sb, "Rooms", ["Result", "View  |  room"], rows);
        if (r.Log.Count > 0 || plan.Log.Count > 0)
            ReportHtml.Note(sb, string.Join("<br>", r.Log.Concat(plan.Log).Select(ReportHtml.Escape)));
        ReportHtml.Note(sb, "<code>" + string.Join("<br>", plan.Report.Select(l => ReportHtml.Escape(l).Replace("  ", "&nbsp;&nbsp;"))) + "</code>");
        if (r.Placed + r.Deleted > 0)
            ReportHtml.Note(sb, "Ctrl+Z in Revit undoes the whole run.");
        return sb.ToString();
    }
}
