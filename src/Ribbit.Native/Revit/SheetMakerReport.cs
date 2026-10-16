using System.Text;
using Ribbit.Geometry.Sheets;
using Ribbit.Native.Ui;

namespace Ribbit.Native.Revit;

/// <summary>
/// SheetMaker's row-by-row report. Ported from script.py's <c>print_report</c>.
///
/// Named ...Html rather than SheetMakerReport because that name already belongs
/// to the counts record in <see cref="Ribbit.Geometry.Sheets"/>, which this file
/// consumes.
/// </summary>
internal static class SheetMakerReportHtml
{
    private static string ActionLabel(SheetAction action) => action switch
    {
        SheetAction.Create => "create",
        SheetAction.Update => "update",
        SheetAction.Skip => "skip",
        SheetAction.Spacer => "spacer",
        SheetAction.Locked => "locked",
        _ => "error",
    };

    private static string ActionClass(SheetAction action) => action switch
    {
        SheetAction.Create or SheetAction.Update => "ok",
        SheetAction.Skip or SheetAction.Spacer or SheetAction.Locked => "warn",
        _ => "skip",
    };

    public static string Build(IReadOnlyList<SheetRowResult> results, SheetMakerReport report)
    {
        StringBuilder sb = new();

        if (report.DryRun)
        {
            ReportHtml.Note(sb,
                "<strong>Dry run - nothing was changed.</strong> This is what SheetMaker would do. "
                + "Untick 'Dry run' and run again to apply it.");
        }

        sb.Append("<ul>");
        sb.Append($"<li><strong>{report.Rows}</strong> row(s) read</li>");
        sb.Append($"<li><strong>{report.Create}</strong> to create, "
                  + $"<strong>{report.Update}</strong> to update, "
                  + $"<strong>{report.Skip}</strong> skipped</li>");
        sb.Append($"<li><strong>{report.Spacer}</strong> spacer row(s), "
                  + $"<strong>{report.Error}</strong> error(s)</li>");

        if (report.Locked > 0)
        {
            sb.Append($"<li><strong>{report.Locked}</strong> sheet(s) checked out by another "
                      + "user and left alone</li>");
        }

        sb.Append("</ul>");

        List<IReadOnlyList<string>> rows = [];
        foreach (SheetRowResult r in results)
        {
            RowPlan p = r.Plan;
            rows.Add(new[]
            {
                SheetPlanner.ExcelRow(p.RowIndex).ToString(),
                ReportHtml.Escape(p.SheetNumber),
                ReportHtml.Escape(p.SheetName),
                $"<span class=\"{ActionClass(p.Action)}\">{ActionLabel(p.Action)}</span>",
                ReportHtml.Escape(p.Titleblock ?? ""),
                r.Sheet is null ? "" : ReportHtml.Link(r.Sheet.Id.ToString(), r.Sheet.Id),
                ReportHtml.Escape(p.Error ?? ""),
            });
        }

        // "Excel row", not "#": these are the workbook's own row numbers, so a
        // reported error can be found in the file without counting.
        ReportHtml.Table(sb, "Row-by-row results",
            ["Excel row", "Sheet Number", "Sheet Name", "Action", "Titleblock", "Sheet", "Error"],
            rows);

        if (report.Error > 0)
        {
            ReportHtml.Note(sb,
                $"{report.Error} row(s) could not be processed - see the Error column. Every other "
                + "row was handled normally; SheetMaker does not roll back on a bad row, because "
                + "one malformed sheet number should not discard a correct batch.");
        }

        return sb.ToString();
    }
}
