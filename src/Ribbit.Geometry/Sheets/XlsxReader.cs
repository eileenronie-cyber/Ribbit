using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace Ribbit.Geometry.Sheets;

/// <summary>Message is already phrased for the user; the caller shows it as-is.</summary>
public sealed class SheetMakerError(string message) : Exception(message);

public static class SheetText
{
    /// <summary>Safe stringify + trim.</summary>
    public static string S(object? x) => x?.ToString()?.Trim() ?? "";

    /// <summary>Collapse whitespace + lowercase, for tolerant header/column matching.</summary>
    public static string Normalize(string? x) => Regex.Replace(S(x), @"\s+", " ").ToLowerInvariant();
}

/// <summary>
/// One data row, keyed by NORMALIZED header.
///
/// The Python normalized on every lookup — a regex substitution per candidate
/// key, per mapped parameter, per row. Normalizing once at read time turns that
/// O(rows x params x columns) into an O(1) dictionary hit.
/// </summary>
public sealed class XlsxRow(
    Dictionary<string, string> byNormalizedHeader,
    IReadOnlyList<string>? originalHeaders = null,
    int rowNumber = 0)
{
    private readonly Dictionary<string, string> _cells = byNormalizedHeader;

    /// <summary>
    /// The row number Excel shows for this row, or 0 when not known. Needed once
    /// blank rows are skipped rather than ending the read: the index into the
    /// rows read no longer says where a row sits in the workbook.
    /// </summary>
    public int RowNumber { get; } = rowNumber;

    /// <summary>
    /// The headers AS WRITTEN in the workbook, shared by every row of a sheet.
    /// <see cref="Columns"/> is normalized for matching; this is what a person
    /// picking a column in the form needs to recognise.
    /// </summary>
    public IReadOnlyList<string> OriginalHeaders { get; } =
        originalHeaders ?? [.. byNormalizedHeader.Keys];

    /// <summary>Case/whitespace-insensitive column lookup.</summary>
    public string Get(string column, string fallback = "")
        => _cells.TryGetValue(SheetText.Normalize(column), out string? v) ? v : fallback;

    /// <summary>
    /// The NORMALIZED headers this row was read against — every row in a
    /// worksheet carries the same set. Lets a caller tell "the column is empty"
    /// apart from "there is no such column", which <see cref="Get"/> cannot.
    /// </summary>
    public IReadOnlyCollection<string> Columns => _cells.Keys;

    public bool HasAnyValue => _cells.Values.Any(v => v.Length > 0);
}

/// <summary>
/// Minimal, dependency-free .xlsx/.xlsm reader. Ported from
/// lib/sheetmakerlib/xlsx_reader.py.
///
/// Reads the handful of OOXML parts actually needed (xl/workbook.xml, the
/// workbook rels, xl/sharedStrings.xml and the target worksheet) straight out of
/// the zip with <see cref="ZipArchive"/> and <see cref="XmlReader"/>. No NuGet
/// package — no ClosedXML, no EPPlus — so deployment stays a single DLL, exactly
/// the property the Python version was written to preserve.
///
/// THREE THINGS THIS DOES THAT THE PYTHON DID NOT:
///   - The workbook is opened ONCE and both worksheets read from it. The Python
///     reopened the zip and re-parsed the whole shared-strings table per sheet.
///   - Worksheet XML is STREAMED, and reading stops at the first fully-blank
///     row. The Python built a full DOM of the sheet first, so a workbook with
///     50 data rows and stray formatting out to row 10,000 parsed all 10,000.
///   - Headers are normalized once (see <see cref="XlsxRow"/>).
///
/// Known limitation, carried over deliberately: numeric date cells read back as
/// their raw serial number, since styles/number-formats are not consulted. Not
/// an issue for the columns this tool reads, which are always text.
/// </summary>
public sealed class XlsxWorkbook : IDisposable
{
    private const string MainNs = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string RelNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    private readonly ZipArchive _zip;
    private List<string>? _sharedStrings;

    /// <summary>Worksheet/tab names, in tab order.</summary>
    public List<string> SheetNames { get; }

    private readonly Dictionary<string, string> _pathByName;

    private XlsxWorkbook(ZipArchive zip)
    {
        _zip = zip;
        (SheetNames, _pathByName) = ReadWorkbookParts();
    }

    public static XlsxWorkbook Open(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            throw new SheetMakerError("Excel file path is empty.");
        if (!File.Exists(filePath))
            throw new SheetMakerError($"Excel file not found:\n{filePath}");

        ZipArchive zip;
        try
        {
            // Shared for read AND write: Excel keeps a workbook it has open
            // locked for writing, and ZipFile.OpenRead (FileShare.Read) is refused
            // outright by that. Saving in Excel and reading here without closing
            // the workbook is the ordinary way to use the sheet exchange.
            FileStream fs = new(filePath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            try
            {
                zip = new ZipArchive(fs, ZipArchiveMode.Read, leaveOpen: false);
            }
            catch
            {
                fs.Dispose();
                throw;
            }
        }
        catch (Exception ex)
        {
            throw new SheetMakerError(
                $"Could not open '{filePath}' as an Excel workbook:\n{ex.Message}");
        }

        try
        {
            return new XlsxWorkbook(zip);
        }
        catch
        {
            zip.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Tab names, or an empty list if the file cannot be read at all.
    ///
    /// This is the SILENT contract the settings form needs while the user is
    /// mid-edit in the path box. The Python had two `list_sheet_names`
    /// definitions — a silent one and a throwing one — and the throwing one
    /// shadowed it, so tabbing out of a half-typed path raised inside a WPF
    /// event handler. Both behaviours exist here, deliberately separated:
    /// this one for the form, <see cref="Open"/> for Run.
    /// </summary>
    public static List<string> TryListSheetNames(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return [];

        try
        {
            using XlsxWorkbook wb = Open(filePath!);
            return wb.SheetNames;
        }
        catch (Exception)
        {
            return [];
        }
    }

    private (List<string> Names, Dictionary<string, string> PathByName) ReadWorkbookParts()
    {
        List<string> names = [];
        Dictionary<string, string> ridByName = [];

        using (XmlReader reader = OpenPart("xl/workbook.xml")
                                 ?? throw new SheetMakerError(
                                     "This file has no xl/workbook.xml - it is not an Excel workbook."))
        {
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element
                    || reader.LocalName != "sheet"
                    || reader.NamespaceURI != MainNs)
                {
                    continue;
                }

                string? name = reader.GetAttribute("name");
                string? rid = reader.GetAttribute("id", RelNs);
                if (string.IsNullOrEmpty(name))
                    continue;

                names.Add(name!);
                if (!string.IsNullOrEmpty(rid))
                    ridByName[name!] = rid!;
            }
        }

        // Resolve each r:id to its part path via the workbook rels.
        Dictionary<string, string> targetByRid = [];
        using (XmlReader? rels = OpenPart("xl/_rels/workbook.xml.rels"))
        {
            if (rels is not null)
            {
                while (rels.Read())
                {
                    if (rels.NodeType != XmlNodeType.Element || rels.LocalName != "Relationship")
                        continue;

                    string? id = rels.GetAttribute("Id");
                    string? target = rels.GetAttribute("Target");
                    if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(target))
                        targetByRid[id!] = target!;
                }
            }
        }

        Dictionary<string, string> pathByName = [];
        foreach (KeyValuePair<string, string> kv in ridByName)
        {
            if (!targetByRid.TryGetValue(kv.Value, out string? target))
                continue;

            pathByName[kv.Key] = target.StartsWith("/", StringComparison.Ordinal)
                ? target.TrimStart('/')
                : "xl/" + target;
        }

        return (names, pathByName);
    }

    private XmlReader? OpenPart(string path)
    {
        ZipArchiveEntry? entry = _zip.GetEntry(path);
        if (entry is null)
            return null;

        // The stream is owned by the reader; closing the reader closes it.
        return XmlReader.Create(entry.Open(), new XmlReaderSettings
        {
            IgnoreWhitespace = true,
            IgnoreComments = true,
            DtdProcessing = DtdProcessing.Prohibit,
            CloseInput = true,
        });
    }

    /// <summary>
    /// Loaded once per workbook, on first worksheet read. Streamed rather than
    /// DOM-parsed: on a real project workbook this part is often the largest.
    /// </summary>
    private List<string> SharedStrings()
    {
        if (_sharedStrings is not null)
            return _sharedStrings;

        _sharedStrings = [];

        using XmlReader? reader = OpenPart("xl/sharedStrings.xml");
        if (reader is null)
            return _sharedStrings;

        StringBuilder current = new();

        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "si")
                continue;

            // <si/> with no children is a legitimate empty shared string.
            if (reader.IsEmptyElement)
            {
                _sharedStrings.Add("");
                continue;
            }

            current.Clear();

            // Read the <si> as its own subtree. ReadElementContentAsString
            // already advances past the element it consumed, so driving the
            // loop with Read() would step over the very next node - which for a
            // bare <t> is the closing </si>, losing the string entirely.
            using (XmlReader si = reader.ReadSubtree())
            {
                si.Read();
                while (!si.EOF)
                {
                    // Concatenates every <t>, covering both a bare <t> and the
                    // run-split <r><t>..</t></r> form Excel emits for mixed
                    // formatting within one cell.
                    if (si.NodeType == XmlNodeType.Element && si.LocalName == "t")
                        current.Append(si.ReadElementContentAsString());
                    else
                        si.Read();
                }
            }

            _sharedStrings.Add(current.ToString());
        }

        return _sharedStrings;
    }

    private static int ColumnLettersToIndex(string reference)
    {
        int idx = 0;
        foreach (char ch in reference)
        {
            if (ch is >= 'A' and <= 'Z')
                idx = idx * 26 + (ch - 'A' + 1);
            else if (ch is >= 'a' and <= 'z')
                idx = idx * 26 + (ch - 'a' + 1);
            else
                break;   // digits start the row part
        }
        return idx;
    }

    /// <summary>
    /// Read one worksheet into rows keyed by its header row.
    ///
    /// Headers are read across <paramref name="startRow"/> from
    /// <paramref name="startCol"/> until the first blank header cell; data rows
    /// are read the same way until the first row blank across every header
    /// column.
    /// </summary>
    /// <param name="stopAtBlankRow">
    /// True (the SheetMaker data-tab contract): the first blank row ends the
    /// table. False: blank rows are skipped and reading goes on to the end of the
    /// sheet - for a list a person edits, where deleting one row's contents must
    /// not silently drop every row after it.
    /// </param>
    public List<XlsxRow> ReadWorksheet(
        string sheetName, int startRow = 1, int startCol = 1, bool stopAtBlankRow = true)
        => ReadTable(sheetName, startRow, startCol, stopAtBlankRow).Rows;

    /// <summary>
    /// <see cref="ReadWorksheet"/> plus the header row as written - which a table
    /// with no data rows cannot otherwise report, since the headers travel on
    /// the rows.
    /// </summary>
    public (List<string> Headers, List<XlsxRow> Rows) ReadTable(
        string sheetName, int startRow = 1, int startCol = 1, bool stopAtBlankRow = true)
    {
        if (!_pathByName.TryGetValue(sheetName, out string? sheetPath))
            throw new SheetMakerError($"Worksheet '{sheetName}' not found in this workbook.");

        List<string> shared = SharedStrings();

        using XmlReader? reader = OpenPart(sheetPath);
        if (reader is null)
            throw new SheetMakerError($"Worksheet '{sheetName}' is missing its data part.");

        List<string>? headers = null;
        List<XlsxRow> result = [];

        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "row")
                continue;

            if (!int.TryParse(reader.GetAttribute("r"), out int rowNum))
                continue;
            if (rowNum < startRow)
                continue;

            Dictionary<int, string> cells = ReadRowCells(reader, shared);

            if (rowNum == startRow)
            {
                headers = [];
                int col = startCol;
                while (cells.TryGetValue(col, out string? value) && value.Length > 0)
                {
                    headers.Add(value.Trim());
                    col++;
                }

                if (headers.Count == 0)
                {
                    throw new SheetMakerError(
                        $"No headers found in worksheet '{sheetName}' (row {startRow}).");
                }
                continue;
            }

            if (headers is null)
                continue;

            // A gap in row numbers means a genuinely empty row, which is the
            // documented stop condition just as much as an all-blank one.
            int expected = startRow + 1 + result.Count;
            if (stopAtBlankRow && rowNum > expected)
                break;

            Dictionary<string, string> byHeader = [];
            bool hasData = false;
            for (int i = 0; i < headers.Count; i++)
            {
                cells.TryGetValue(startCol + i, out string? value);
                value ??= "";
                // Whitespace counts as blank only in the new skip mode; the
                // SheetMaker contract keeps its exact old stop condition.
                if (stopAtBlankRow ? value.Length > 0 : value.Trim().Length > 0)
                    hasData = true;
                byHeader[SheetText.Normalize(headers[i])] = value;
            }

            if (!hasData)
            {
                if (stopAtBlankRow)
                    break;
                continue;
            }

            result.Add(new XlsxRow(byHeader, headers, rowNum));
        }

        if (headers is null)
        {
            throw new SheetMakerError(
                $"No headers found in worksheet '{sheetName}' (row {startRow}).");
        }

        return (headers, result);
    }

    private static Dictionary<int, string> ReadRowCells(XmlReader reader, List<string> shared)
    {
        Dictionary<int, string> cells = [];
        if (reader.IsEmptyElement)
            return cells;

        using XmlReader row = reader.ReadSubtree();
        row.Read();   // position on <row>

        while (row.Read())
        {
            if (row.NodeType != XmlNodeType.Element || row.LocalName != "c")
                continue;

            string? cellRef = row.GetAttribute("r");
            string? cellType = row.GetAttribute("t");
            if (string.IsNullOrEmpty(cellRef))
                continue;

            int col = ColumnLettersToIndex(cellRef!);
            if (col <= 0)
                continue;

            cells[col] = ReadCellValue(row, cellType, shared);
        }

        return cells;
    }

    private static string ReadCellValue(XmlReader cell, string? cellType, List<string> shared)
    {
        if (cell.IsEmptyElement)
            return "";

        string raw = "";
        string inline = "";

        using XmlReader sub = cell.ReadSubtree();
        sub.Read();

        // Driven by EOF rather than Read(), for the same reason as the shared
        // strings above: ReadElementContentAsString has already moved on.
        while (!sub.EOF)
        {
            if (sub.NodeType == XmlNodeType.Element && sub.LocalName == "v")
                raw = sub.ReadElementContentAsString();
            else if (sub.NodeType == XmlNodeType.Element && sub.LocalName == "t")
                inline += sub.ReadElementContentAsString();
            else
                sub.Read();
        }

        if (cellType == "inlineStr")
            return inline;

        if (raw.Length == 0)
            return "";

        switch (cellType)
        {
            case "s":
                return int.TryParse(raw, out int idx) && idx >= 0 && idx < shared.Count
                    ? shared[idx]
                    : "";
            case "b":
                return raw == "1" ? "True" : "False";
            case "str":
                return raw;
        }

        // Plain number (t absent or "n"): read back the way Excel SHOWS it. Excel
        // saves a typed 1.1 as "1.1000000000000001" (17 digits) but displays 15;
        // decimal keeps at most 15 significant digits from a double, which is
        // that display - "1.1", "915004" (no ".0"), "0.00001" (no exponent).
        if (double.TryParse(raw, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double f)
            && !double.IsInfinity(f) && !double.IsNaN(f))
        {
            return Math.Abs(f) < 7.9e27
                ? ((decimal)f).ToString(System.Globalization.CultureInfo.InvariantCulture)
                : f.ToString("G15", System.Globalization.CultureInfo.InvariantCulture);
        }

        return raw;
    }

    public void Dispose() => _zip.Dispose();
}
