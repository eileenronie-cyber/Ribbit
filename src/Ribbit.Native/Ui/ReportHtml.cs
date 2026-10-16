using System.Net;
using System.Text;
using Autodesk.Revit.DB;

namespace Ribbit.Native.Ui;

/// <summary>
/// Builds the HTML the report layers hand to <see cref="ReportWindow"/>.
///
/// This exists so the ported report code keeps the shape it already has in
/// Python — build up rows and print them — instead of being rewritten as WPF
/// view-models. A report layer's job is to return a body string; this wraps it
/// in a styled document.
/// </summary>
internal static class ReportHtml
{
    public static string Escape(string text) => WebUtility.HtmlEncode(text);

    /// <summary>
    /// A clickable element link — the replacement for output.linkify(). Clicking
    /// selects and zooms to the elements in Revit.
    /// </summary>
    public static string Link(string text, params ElementId[] ids)
        => $"<a href=\"{RevitLinks.HrefFor(ids)}\">{Escape(text)}</a>";

    public static string Link(string text, IEnumerable<ElementId> ids)
        => $"<a href=\"{RevitLinks.HrefFor(ids)}\">{Escape(text)}</a>";

    /// <summary>
    /// A titled table. Cells are emitted raw, so callers escape their own text —
    /// that is what lets a cell hold a <see cref="Link"/> or a status span.
    /// </summary>
    public static void Table(
        StringBuilder sb, string title, IReadOnlyList<string> columns,
        IReadOnlyList<IReadOnlyList<string>> rows)
    {
        sb.Append("<h2>").Append(Escape(title)).Append("</h2>");
        sb.Append("<table><thead><tr>");
        foreach (string c in columns)
            sb.Append("<th>").Append(Escape(c)).Append("</th>");
        sb.Append("</tr></thead><tbody>");

        foreach (IReadOnlyList<string> row in rows)
        {
            sb.Append("<tr>");
            foreach (string cell in row)
                sb.Append("<td>").Append(cell).Append("</td>");
            sb.Append("</tr>");
        }

        sb.Append("</tbody></table>");
    }

    /// <summary>A callout block — the equivalent of pyRevit's "> " blockquote notes.</summary>
    public static void Note(StringBuilder sb, string html)
        => sb.Append("<div class=\"summary\">").Append(html).Append("</div>");

    /// <summary>Wraps a report body in a full, self-contained HTML document.</summary>
    public static string Document(string title, string bodyHtml)
    {
        StringBuilder sb = new();
        sb.Append("<!doctype html><html><head><meta charset=\"utf-8\">");
        sb.Append("<title>").Append(Escape(title)).Append("</title><style>");
        sb.Append(Css);
        sb.Append("</style></head><body>");
        sb.Append("<h1>").Append(Escape(title)).Append("</h1>");
        sb.Append(bodyHtml);
        sb.Append("</body></html>");
        return sb.ToString();
    }

    /// <summary>
    /// Follows the viewer's OS theme, since Revit 2024+ has a dark UI and a
    /// blazing white report next to it is exactly what the pyRevit output
    /// window got criticised for.
    /// </summary>
    private const string Css = """
        :root { color-scheme: light dark; }
        body {
          font-family: 'Segoe UI', system-ui, sans-serif;
          font-size: 13px; line-height: 1.5;
          margin: 0; padding: 20px 24px;
          background: #ffffff; color: #1a1a1a;
        }
        h1 { font-size: 19px; margin: 0 0 4px; font-weight: 600; }
        h2 { font-size: 15px; margin: 22px 0 6px; font-weight: 600; }
        table { border-collapse: collapse; margin: 10px 0 18px; width: 100%; }
        th, td { text-align: left; padding: 5px 10px; border-bottom: 1px solid #e2e2e2; }
        th { font-weight: 600; border-bottom: 2px solid #c8c8c8; white-space: nowrap; }
        td.num { text-align: right; font-variant-numeric: tabular-nums; }
        tr:hover td { background: #f5f7fa; }
        a { color: #0a66c2; text-decoration: none; }
        a:hover { text-decoration: underline; }
        .ok    { color: #1a7f37; font-weight: 600; }
        .warn  { color: #9a6700; font-weight: 600; }
        .skip  { color: #b3261e; font-weight: 600; }
        .muted { color: #6a6a6a; }
        .summary { margin: 8px 0 16px; padding: 10px 14px;
                   background: #f2f5f8; border-left: 3px solid #0a66c2; }
        code { font-family: Consolas, monospace; font-size: 12px; }
        @media (prefers-color-scheme: dark) {
          body { background: #1f1f1f; color: #e6e6e6; }
          th, td { border-bottom-color: #3a3a3a; }
          th { border-bottom-color: #555; }
          tr:hover td { background: #2a2d31; }
          a { color: #6cb6ff; }
          .ok { color: #4ac26b; } .warn { color: #d4a72c; } .skip { color: #ff7b72; }
          .muted { color: #9a9a9a; }
          .summary { background: #24282c; border-left-color: #6cb6ff; }
        }
        """;
}
