using System.IO;
using System.Reflection;
using System.Text;
using Autodesk.Revit.UI;
using Ribbit.Native.Ui;

namespace Ribbit.Native.Tools;

/// <summary>
/// About Ribbit. Ported from _Ribbit.tab/About.panel/Ribbit.pushbutton/script.py.
///
/// The Python was a mascot button — a toast that said "Ribbit!" and nothing
/// else. It now also answers "who made this and which build am I running?",
/// which is the question that actually gets asked of a shared add-in, and the
/// one a bare toast could not.
///
/// The publisher is read from the ASSEMBLY, not hard-coded here, so it can never
/// drift from what the compiled DLL claims about itself in Windows' file
/// properties or Revit's Add-In Manager.
/// </summary>
internal static class AboutTool
{
    private const string DialogTitle = "Ribbit";

    public static void Run()
    {
        Assembly asm = Assembly.GetExecutingAssembly();

        string product = Meta<AssemblyProductAttribute>(asm, a => a.Product, "Ribbit");
        string company = Meta<AssemblyCompanyAttribute>(asm, a => a.Company, "");
        string copyright = Meta<AssemblyCopyrightAttribute>(asm, a => a.Copyright, "");
        string version = Meta<AssemblyInformationalVersionAttribute>(
            asm, a => a.InformationalVersion, asm.GetName().Version?.ToString() ?? "");

        // A build stamp beats a version number when several people are running
        // hand-built copies: it says WHICH build, not which release.
        string built;
        try
        {
            built = File.GetLastWriteTime(asm.Location).ToString("yyyy-MM-dd HH:mm");
        }
        catch (Exception)
        {
            built = "unknown";
        }

        StringBuilder sb = new();

        ReportHtml.Note(sb,
            "<strong>Ribbit.</strong> That is the sound a frog makes; the add-in is named for it.");

        List<IReadOnlyList<string>> rows =
        [
            new[] { "Product", ReportHtml.Escape(product) },
            new[] { "Publisher", ReportHtml.Escape(company.Length > 0 ? company : "-") },
            new[] { "Version", ReportHtml.Escape(version.Length > 0 ? version : "-") },
            new[] { "This build", ReportHtml.Escape(built) },
            new[] { "Copyright", ReportHtml.Escape(copyright.Length > 0 ? copyright : "-") },
        ];

        ReportHtml.Table(sb, "About this add-in", ["", ""], rows);

        sb.Append("<h2>Tools</h2><ul>");
        sb.Append("<li><strong>Ribbit</strong> - MEP routing and alignment, sprinkler drops, numbered notes, room tags, "
                  + "branch slope, and sheet and view automation (placing views, copying view filters).</li>");
        sb.Append("<li><strong>Ribbit CD</strong> - clash detection against links, structure, "
                  + "and the host model itself, with a status ledger.</li>");
        sb.Append("</ul>");

        RibbitUi.ShowReport($"About {DialogTitle}", sb.ToString());
    }

    /// <summary>One assembly attribute, or a fallback when it is absent.</summary>
    private static string Meta<T>(Assembly asm, Func<T, string?> read, string fallback)
        where T : Attribute
    {
        try
        {
            T? attr = asm.GetCustomAttribute<T>();
            string? value = attr is null ? null : read(attr);
            return string.IsNullOrWhiteSpace(value) ? fallback : value!;
        }
        catch (Exception)
        {
            return fallback;
        }
    }
}
