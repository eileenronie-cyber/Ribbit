using System.Text;
using Ribbit.Geometry.Sheets;
using Ribbit.Native.Ui;

namespace Ribbit.Native.Revit;

/// <summary>What an import did, row by row, with a link to each sheet.</summary>
internal static class SheetImportReport
{
    public static string Build(string path, ImportPlan plan, ApplyOutcome outcome)
    {
        StringBuilder sb = new();
        ImportCounts c = plan.Counts;

        sb.Append("<ul>");
        sb.Append($"<li>From <strong>{ReportHtml.Escape(System.IO.Path.GetFileName(path))}</strong> - {c.Rows} row(s)</li>");
        sb.Append($"<li><strong>{outcome.Created}</strong> created, <strong>{outcome.Updated}</strong> updated, "
                  + $"<strong>{c.Unchanged}</strong> unchanged</li>");
        if (c.Skip + c.Locked + c.Error + outcome.Failed > 0)
        {
            sb.Append($"<li><strong>{c.Skip}</strong> left alone, <strong>{c.Locked}</strong> checked out, "
                      + $"<strong>{c.Error + outcome.Failed}</strong> not imported</li>");
        }
        if (c.Conflicts > 0)
            sb.Append($"<li><strong>{c.Conflicts}</strong> value(s) changed in both Revit and Excel - Revit's kept</li>");
        if (c.Problems + outcome.Refused > 0)
            sb.Append($"<li><strong>{c.Problems + outcome.Refused}</strong> value(s) not written - see Notes</li>");
        sb.Append("</ul>");

        foreach (string note in plan.FileNotes)
            ReportHtml.Note(sb, ReportHtml.Escape(note));

        List<IReadOnlyList<string>> rows = [];
        foreach (AppliedRow a in outcome.Rows.Where(a =>
                     a.Row.Action != ImportAction.Unchanged || a.Row.Conflicts.Count > 0 || a.Row.Problems.Count > 0))
        {
            ImportLine line = SheetImportWindow.ToLine(a.Row);
            List<string> notes = [.. a.Refused];
            if (a.Failure is not null)
                notes.Insert(0, a.Failure);

            string action = a.Failure is not null ? "failed" : line.Action;
            string cls = a.Failure is not null || a.Row.Action == ImportAction.Error ? "skip"
                : line.Tone == "ok" ? "ok" : "warn";

            rows.Add(new[]
            {
                line.Row,
                a.Sheet is null ? ReportHtml.Escape(line.Sheet) : ReportHtml.Link(line.Sheet, a.Sheet.Id),
                $"<span class=\"{cls}\">{ReportHtml.Escape(action)}</span>",
                ReportHtml.Escape(line.Details).Replace("\n", "<br>"),
                ReportHtml.Escape(string.Join("\n", notes)).Replace("\n", "<br>"),
            });
        }

        ReportHtml.Table(sb, "Rows with something to report",
            ["Excel row", "Sheet", "Action", "What", "Notes"], rows);

        return sb.ToString();
    }
}
