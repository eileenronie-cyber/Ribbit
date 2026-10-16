using System.Net;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Ribbit.Native.Revit;
using Ribbit.Native.Ui;

namespace Ribbit.Native.Tools;

/// <summary>
/// Adjust Grids. Ported from the Dynamo graph Grid_v23.dyn (V23) - its two
/// Python nodes, the WinForms picker ("Pick views and grids, then configure")
/// and the engine ("Adjust grids to the crop box"), are now
/// <see cref="GridAdjustWindow"/> and <see cref="GridAdjustEngine"/>. The
/// graph's three Watch nodes (Adjusted / Skipped / Log) became one report.
///
/// What moved between the two nodes as twenty positional inputs and a JSON
/// string is now a typed <see cref="GridAdjustChoice"/>; the UniqueIds are
/// resolved back to elements here, once, exactly as the engine node did.
/// </summary>
internal static class GridAdjustTool
{
    private const string DialogTitle = "Adjust Grids";

    public static void Run(UIDocument uidoc)
    {
        Document doc = uidoc.Document;
        RevitLinks.EnsureInitialized();

        if (doc.IsFamilyDocument)
        {
            RibbitUi.Alert("Adjust Grids works on a project, not a family.", DialogTitle);
            return;
        }

        GridAdjustWindow window = new(doc);
        if (window.ShowDialog() != true || window.Result is null)
            return;

        GridAdjustChoice choice = window.Result;
        List<string> preLog = [];

        List<View> views = [];
        HashSet<long> seen = [];
        foreach (string uid in choice.ViewUids)
        {
            if (doc.GetElement(uid) is View v && seen.Add(RevitCompat.IdKey(v.Id)))
                views.Add(v);
        }

        View? copySource = null;
        if (choice.CopySourceUid.Length > 0)
        {
            copySource = doc.GetElement(choice.CopySourceUid) as View;
            if (copySource is null)
            {
                RibbitUi.Alert("The view to copy from is no longer in the model - nothing was changed.", DialogTitle);
                return;
            }
        }

        Element? chosenScope = null;
        if (choice.ChosenScopeUid.Length > 0)
        {
            chosenScope = doc.GetElement(choice.ChosenScopeUid);
            if (chosenScope is null)
                preLog.Add("The scope box chosen in the grids list is no longer in the model - views without one "
                           + "of their own are stretched to their crop box.");
        }

        GridAdjustRequest request = new()
        {
            Views = views,
            PickedGrids = [.. choice.GridUids.Select(doc.GetElement).OfType<Grid>()],
            Base = choice.Base,
            Plan = choice.Plan,
            CopySource = copySource,
            HideOthers = choice.HideOthers,
            ShowSet = choice.ShowSet,
            All2d = choice.All2d,
            ToScope = choice.ToScope,
            ChosenScope = chosenScope,
        };

        GridAdjustResult result;
        try
        {
            result = GridAdjustEngine.Run(doc, request);
        }
        catch (Exception ex)
        {
            RibbitUi.Alert($"Adjust Grids stopped: {ex.Message}", DialogTitle);
            return;
        }

        result.Log.InsertRange(0, preLog);
        RibbitUi.ShowReport(DialogTitle, BuildReport(result));
    }

    private static string BuildReport(GridAdjustResult r)
    {
        StringBuilder sb = new();

        sb.Append("<ul>");
        foreach (string line in r.Log)
            sb.Append("<li>").Append(WebUtility.HtmlEncode(line)).Append("</li>");
        sb.Append("</ul>");

        if (r.ExtraElbows.Count > 0)
        {
            ReportHtml.Note(sb,
                $"<strong>{r.ExtraElbows.Count} elbow(s) to remove by hand.</strong> These views had an elbow the "
                + "source view does not have. Revit's API cannot remove an elbow, so each is listed: open the view, "
                + "select the grid (click its name here), and drag the bubble back onto the grid line.");
            ReportHtml.Table(sb, $"Elbows to remove by hand ({r.ExtraElbows.Count})", ["Grid", "View", "At its"],
                [.. r.ExtraElbows.Select(x => (IReadOnlyList<string>)
                    [ReportHtml.Link(x.GridName, x.GridId), ReportHtml.Escape(x.ViewName), x.End])]);
        }

        ReportHtml.Table(sb, $"Adjusted ({r.Adjusted.Count})", ["View", "What"],
            [.. r.Adjusted.Select(Split)]);

        if (r.Skipped.Count > 0)
            ReportHtml.Table(sb, $"Skipped ({r.Skipped.Count})", ["What", "Why"],
                [.. r.Skipped.Select(s =>
                {
                    int i = s.IndexOf("  -  ", StringComparison.Ordinal);
                    return (IReadOnlyList<string>)(i < 0
                        ? [ReportHtml.Escape(s), ""]
                        : [ReportHtml.Escape(s.Substring(0, i)), $"<span class=\"warn\">{ReportHtml.Escape(s.Substring(i + 5))}</span>"]);
                })]);

        return sb.ToString();
    }

    private static IReadOnlyList<string> Split(string line)
    {
        int i = line.IndexOf("      ", StringComparison.Ordinal);
        return i < 0
            ? [ReportHtml.Escape(line), ""]
            : [ReportHtml.Escape(line.Substring(0, i)), ReportHtml.Escape(line.Substring(i).Trim())];
    }
}
