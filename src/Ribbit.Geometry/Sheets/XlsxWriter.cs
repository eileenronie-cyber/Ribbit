using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Xml;

namespace Ribbit.Geometry.Sheets;

public enum XlsxVisibility
{
    Visible,
    Hidden,

    /// <summary>Hidden where Excel's own Unhide menu cannot reach it.</summary>
    VeryHidden,
}

/// <summary>How a column's cells are stored.</summary>
public enum XlsxColumnKind
{
    /// <summary>Text, whatever it looks like (the default: "01" stays "01").</summary>
    Text,

    /// <summary>
    /// A real number. The cell strings in <see cref="XlsxSheet.Rows"/> are the
    /// invariant-culture decimal text of the value ("76.2"); writing one that is
    /// not a number throws, because a number column must never hold garbage.
    /// </summary>
    Number,
}

/// <summary>One column of a table sheet: its header, width and look.</summary>
public sealed class XlsxColumn(string header)
{
    public string Header { get; } = header;

    /// <summary>In Excel's own unit (characters of the default font).</summary>
    public double Width { get; set; } = 16;

    /// <summary>Grey fill: shown for reference, not meant to be edited.</summary>
    public bool Muted { get; set; }

    /// <summary>A defined name whose cells become this column's dropdown, or null.</summary>
    public string? ListName { get; set; }

    /// <summary>Text (default) or Number. See <see cref="XlsxColumnKind"/>.</summary>
    public XlsxColumnKind Kind { get; set; } = XlsxColumnKind.Text;

    /// <summary>
    /// Cells a person may edit even when the sheet is <see cref="XlsxSheet.Protected"/>.
    /// Has no visible effect on an unprotected sheet - every cell is editable there.
    /// </summary>
    public bool Unlocked { get; set; }

    /// <summary>
    /// A distinct (warm) fill, for values the add-in GENERATED rather than read.
    /// It is the visible half of "mark the source of every value": a person
    /// scanning the sheet can tell a proposal from a fact at a glance. Implies
    /// an unlocked text column.
    /// </summary>
    public bool Proposed { get; set; }
}

/// <summary>
/// One worksheet. With <see cref="Columns"/> set it is a TABLE: row 1 is a bold
/// frozen header with a filter, and every column is formatted as TEXT.
/// </summary>
public sealed class XlsxSheet(string name)
{
    public string Name { get; } = name;

    public XlsxVisibility Visibility { get; set; } = XlsxVisibility.Visible;

    public List<XlsxColumn> Columns { get; } = [];

    /// <summary>Data rows, below the header row.</summary>
    public List<IReadOnlyList<string>> Rows { get; } = [];

    /// <summary>Columns kept in view when scrolling sideways.</summary>
    public int FreezeColumns { get; set; }

    /// <summary>
    /// Turns sheet protection on, with NO password: locked cells cannot be
    /// edited until Review > Unprotect Sheet. It is there to stop an accidental
    /// edit of a key or a read-only value, not to keep anyone out. Filtering and
    /// resizing columns stay allowed.
    /// </summary>
    public bool Protected { get; set; }
}

/// <summary>
/// Minimal, dependency-free .xlsx WRITER - the counterpart of
/// <see cref="XlsxWorkbook"/>, and for the same reason: no NuGet package, so the
/// add-in stays a single DLL.
///
/// Writes only what the sheet exchange needs, and every one of those parts is
/// there because Excel behaves worse without it:
///   - EVERY CELL IS TEXT (number format "@"), including the empty cells of each
///     column. Without it Excel turns a typed sheet number "01" into 1, and
///     "1-2" into a date, and there is no way to tell afterwards.
///   - The header row is frozen and filtered, so a 300-sheet list can be sorted
///     and searched without losing the headings.
///   - A column can carry a dropdown fed from a named range, which is how a
///     titleblock is picked rather than typed.
///   - Sheets can be hidden, "very hidden" included, for bookkeeping a person
///     should not edit.
/// Strings are written inline (Excel converts them to its shared table on its
/// first save; <see cref="XlsxWorkbook"/> reads both).
/// </summary>
public sealed class XlsxWriter
{
    private const string MainNs = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string RelNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string PkgRelNs = "http://schemas.openxmlformats.org/package/2006/relationships";
    private const string TypesNs = "http://schemas.openxmlformats.org/package/2006/content-types";

    // cellXfs indices in the styles part below.
    private const int StyleText = 1;
    private const int StyleHeader = 2;
    private const int StyleMuted = 3;
    private const int StyleMutedHeader = 4;

    // Appended ONLY when a sheet needs them (numbers, unlocked cells, protection),
    // so a workbook that uses none of it is byte-for-byte what it always was.
    private const int StyleNumber = 5;
    private const int StyleNumberUnlocked = 6;
    private const int StyleTextUnlocked = 7;
    private const int StyleMutedNumber = 8;
    private const int StyleProposed = 9;
    private const int StyleProposedHeader = 10;

    /// <summary>The style a column's body cells use.</summary>
    private static int ColumnStyle(XlsxColumn? col)
    {
        if (col is null)
            return StyleText;

        if (col.Proposed)
            return StyleProposed;

        bool number = col.Kind == XlsxColumnKind.Number;
        if (col.Unlocked)
            return number ? StyleNumberUnlocked : StyleTextUnlocked;
        if (col.Muted)
            return number ? StyleMutedNumber : StyleMuted;
        return number ? StyleNumber : StyleText;
    }

    private bool NeedsExtendedStyles
        => _sheets.Any(s => s.Protected
                            || s.Columns.Any(c => c.Kind == XlsxColumnKind.Number || c.Unlocked || c.Proposed));

    private bool NeedsProposedStyles => _sheets.Any(s => s.Columns.Any(c => c.Proposed));

    /// <summary>How far down a dropdown reaches - well past any drawing list.</summary>
    public const int ValidationRows = 5000;

    private readonly List<XlsxSheet> _sheets = [];
    private readonly List<(string Name, string Reference)> _names = [];

    public IReadOnlyList<XlsxSheet> Sheets => _sheets;

    public XlsxSheet AddSheet(string name)
    {
        string problem = SheetNameProblem(name);
        if (problem.Length > 0)
            throw new ArgumentException(problem, nameof(name));
        if (_sheets.Any(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException($"There is already a worksheet called '{name}'.", nameof(name));

        XlsxSheet sheet = new(name);
        _sheets.Add(sheet);
        return sheet;
    }

    /// <summary>
    /// A workbook-level name for a block of one column, rows
    /// <paramref name="firstRow"/>..<paramref name="lastRow"/> (1-based).
    /// </summary>
    public void DefineName(string name, XlsxSheet sheet, int column, int firstRow, int lastRow)
    {
        string col = ColumnLetters(column);
        string quoted = "'" + sheet.Name.Replace("'", "''") + "'";
        _names.Add((name, $"{quoted}!${col}${firstRow}:${col}${Math.Max(firstRow, lastRow)}"));
    }

    /// <summary>Why Excel would refuse this worksheet name, or "".</summary>
    public static string SheetNameProblem(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "A worksheet name cannot be blank.";
        if (name.Length > 31)
            return $"'{name}' is longer than Excel's 31-character limit for a worksheet name.";
        if (name.IndexOfAny([':', '\\', '/', '?', '*', '[', ']']) >= 0)
            return $"'{name}' contains a character Excel does not allow in a worksheet name.";
        return "";
    }

    /// <summary>1 -> A, 26 -> Z, 27 -> AA.</summary>
    public static string ColumnLetters(int column)
    {
        if (column < 1)
            throw new ArgumentOutOfRangeException(nameof(column));

        StringBuilder sb = new();
        while (column > 0)
        {
            int rem = (column - 1) % 26;
            sb.Insert(0, (char)('A' + rem));
            column = (column - 1) / 26;
        }
        return sb.ToString();
    }

    /// <summary>
    /// Writes the workbook to <paramref name="path"/>. Written to a temporary
    /// file beside it first, so a failure part-way never leaves a half-written
    /// workbook where a good one used to be.
    /// </summary>
    public void Save(string path)
    {
        if (_sheets.Count == 0)
            throw new InvalidOperationException("A workbook needs at least one worksheet.");
        if (_sheets[0].Visibility != XlsxVisibility.Visible)
            throw new InvalidOperationException("The first worksheet must be visible.");

        string full = Path.GetFullPath(path);
        string folder = Path.GetDirectoryName(full) ?? ".";
        string temp = Path.Combine(folder, "." + Path.GetFileName(full) + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp");

        try
        {
            using (FileStream fs = new(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (ZipArchive zip = new(fs, ZipArchiveMode.Create))
            {
                WriteParts(zip);
            }

            if (File.Exists(full))
                File.Replace(temp, full, null);
            else
                File.Move(temp, full);
        }
        finally
        {
            try
            {
                if (File.Exists(temp))
                    File.Delete(temp);
            }
            catch (IOException)
            {
                // best effort: a stray dot-file is harmless
            }
        }
    }

    private void WriteParts(ZipArchive zip)
    {
        Part(zip, "[Content_Types].xml", w =>
        {
            w.WriteStartElement("Types", TypesNs);
            Default(w, "rels", "application/vnd.openxmlformats-package.relationships+xml");
            Default(w, "xml", "application/xml");
            Override(w, "/xl/workbook.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml");
            Override(w, "/xl/styles.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml");
            for (int i = 0; i < _sheets.Count; i++)
                Override(w, $"/xl/worksheets/sheet{i + 1}.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml");
            w.WriteEndElement();

            static void Default(XmlWriter w, string ext, string type)
            {
                w.WriteStartElement("Default", TypesNs);
                w.WriteAttributeString("Extension", ext);
                w.WriteAttributeString("ContentType", type);
                w.WriteEndElement();
            }

            static void Override(XmlWriter w, string part, string type)
            {
                w.WriteStartElement("Override", TypesNs);
                w.WriteAttributeString("PartName", part);
                w.WriteAttributeString("ContentType", type);
                w.WriteEndElement();
            }
        });

        Part(zip, "_rels/.rels", w =>
        {
            w.WriteStartElement("Relationships", PkgRelNs);
            Rel(w, "rId1", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument", "xl/workbook.xml");
            w.WriteEndElement();
        });

        Part(zip, "xl/_rels/workbook.xml.rels", w =>
        {
            w.WriteStartElement("Relationships", PkgRelNs);
            for (int i = 0; i < _sheets.Count; i++)
                Rel(w, $"rId{i + 1}", RelNs + "/worksheet", $"worksheets/sheet{i + 1}.xml");
            Rel(w, $"rId{_sheets.Count + 1}", RelNs + "/styles", "styles.xml");
            w.WriteEndElement();
        });

        Part(zip, "xl/workbook.xml", WriteWorkbook);
        bool extended = NeedsExtendedStyles;
        bool proposed = NeedsProposedStyles;
        Part(zip, "xl/styles.xml", w => WriteStyles(w, extended, proposed));

        for (int i = 0; i < _sheets.Count; i++)
        {
            XlsxSheet sheet = _sheets[i];
            bool first = i == 0;
            Part(zip, $"xl/worksheets/sheet{i + 1}.xml", w => WriteSheet(w, sheet, first));
        }
    }

    private static void Rel(XmlWriter w, string id, string type, string target)
    {
        w.WriteStartElement("Relationship", PkgRelNs);
        w.WriteAttributeString("Id", id);
        w.WriteAttributeString("Type", type);
        w.WriteAttributeString("Target", target);
        w.WriteEndElement();
    }

    private static void Part(ZipArchive zip, string name, Action<XmlWriter> body)
    {
        using Stream s = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
        using XmlWriter w = XmlWriter.Create(s, new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(false),
            // Characters XML cannot carry are dropped in Clean(), not thrown on.
            CheckCharacters = true,
        });
        w.WriteStartDocument(standalone: true);
        body(w);
        w.WriteEndDocument();
    }

    private void WriteWorkbook(XmlWriter w)
    {
        w.WriteStartElement("workbook", MainNs);
        w.WriteAttributeString("xmlns", "r", null, RelNs);

        w.WriteStartElement("bookViews", MainNs);
        w.WriteStartElement("workbookView", MainNs);
        w.WriteAttributeString("activeTab", "0");
        w.WriteEndElement();
        w.WriteEndElement();

        w.WriteStartElement("sheets", MainNs);
        for (int i = 0; i < _sheets.Count; i++)
        {
            w.WriteStartElement("sheet", MainNs);
            w.WriteAttributeString("name", _sheets[i].Name);
            w.WriteAttributeString("sheetId", (i + 1).ToString(CultureInfo.InvariantCulture));
            if (_sheets[i].Visibility == XlsxVisibility.Hidden)
                w.WriteAttributeString("state", "hidden");
            else if (_sheets[i].Visibility == XlsxVisibility.VeryHidden)
                w.WriteAttributeString("state", "veryHidden");
            w.WriteAttributeString("id", RelNs, $"rId{i + 1}");
            w.WriteEndElement();
        }
        w.WriteEndElement();

        // A filter needs its hidden _FilterDatabase name - it is what Excel
        // itself writes, and what it looks for to know the range is filtered.
        List<(string Name, string Reference, int? Local)> names = [.. _names.Select(n => (n.Name, n.Reference, (int?)null))];
        for (int i = 0; i < _sheets.Count; i++)
        {
            XlsxSheet s = _sheets[i];
            if (s.Columns.Count == 0)
                continue;
            string quoted = "'" + s.Name.Replace("'", "''") + "'";
            names.Add(("_xlnm._FilterDatabase",
                $"{quoted}!$A$1:${ColumnLetters(s.Columns.Count)}${Math.Max(1, s.Rows.Count + 1)}", i));
        }

        if (names.Count > 0)
        {
            w.WriteStartElement("definedNames", MainNs);
            foreach ((string name, string reference, int? local) in names)
            {
                w.WriteStartElement("definedName", MainNs);
                w.WriteAttributeString("name", name);
                if (local is not null)
                {
                    w.WriteAttributeString("localSheetId", local.Value.ToString(CultureInfo.InvariantCulture));
                    w.WriteAttributeString("hidden", "1");
                }
                w.WriteString(reference);
                w.WriteEndElement();
            }
            w.WriteEndElement();
        }

        w.WriteEndElement();
    }

    private static void WriteStyles(XmlWriter w, bool extended, bool proposed)
    {
        w.WriteStartElement("styleSheet", MainNs);

        w.WriteStartElement("fonts", MainNs);
        w.WriteAttributeString("count", "2");
        Font(w, bold: false);
        Font(w, bold: true);
        w.WriteEndElement();

        w.WriteStartElement("fills", MainNs);
        w.WriteAttributeString("count", proposed ? "5" : "4");
        Fill(w, "none", null);
        Fill(w, "gray125", null);
        Fill(w, "solid", "FFDDE6F3");   // header
        Fill(w, "solid", "FFEDEDED");   // muted
        if (proposed)
            Fill(w, "solid", "FFFCE4D6");   // generated by the add-in, not read from the model
        w.WriteEndElement();

        w.WriteStartElement("borders", MainNs);
        w.WriteAttributeString("count", "1");
        w.WriteStartElement("border", MainNs);
        foreach (string side in new[] { "left", "right", "top", "bottom", "diagonal" })
        {
            w.WriteStartElement(side, MainNs);
            w.WriteEndElement();
        }
        w.WriteEndElement();
        w.WriteEndElement();

        w.WriteStartElement("cellStyleXfs", MainNs);
        w.WriteAttributeString("count", "1");
        Xf(w, 0, 0, 0, inCellXfs: false);
        w.WriteEndElement();

        // 0 default, 1 text, 2 header, 3 muted text, 4 muted header.
        // 49 is Excel's built-in "@" (Text) number format.
        // 5 number, 6 number unlocked, 7 text unlocked, 8 muted number: only when extended.
        w.WriteStartElement("cellXfs", MainNs);
        w.WriteAttributeString("count", extended ? (proposed ? "11" : "9") : "5");
        Xf(w, 0, 0, 0, inCellXfs: true);
        Xf(w, 49, 0, 0, inCellXfs: true);
        Xf(w, 49, 1, 2, inCellXfs: true);
        Xf(w, 49, 0, 3, inCellXfs: true);
        Xf(w, 49, 1, 3, inCellXfs: true);
        if (extended)
        {
            Xf(w, 0, 0, 0, inCellXfs: true);                    // 5 number, locked
            Xf(w, 0, 0, 0, inCellXfs: true, unlocked: true);    // 6 number, unlocked
            Xf(w, 49, 0, 0, inCellXfs: true, unlocked: true);   // 7 text, unlocked
            Xf(w, 0, 0, 3, inCellXfs: true);                    // 8 muted number
            if (proposed)
            {
                Xf(w, 49, 0, 4, inCellXfs: true, unlocked: true);   // 9 proposed text: warm fill, editable
                Xf(w, 49, 1, 4, inCellXfs: true);                   // 10 proposed header
            }
        }
        w.WriteEndElement();

        w.WriteStartElement("cellStyles", MainNs);
        w.WriteAttributeString("count", "1");
        w.WriteStartElement("cellStyle", MainNs);
        w.WriteAttributeString("name", "Normal");
        w.WriteAttributeString("xfId", "0");
        w.WriteAttributeString("builtinId", "0");
        w.WriteEndElement();
        w.WriteEndElement();

        w.WriteEndElement();

        static void Font(XmlWriter w, bool bold)
        {
            w.WriteStartElement("font", MainNs);
            if (bold)
            {
                w.WriteStartElement("b", MainNs);
                w.WriteEndElement();
            }
            Val(w, "sz", "11");
            Val(w, "name", "Calibri");
            Val(w, "family", "2");
            w.WriteEndElement();
        }

        static void Fill(XmlWriter w, string pattern, string? rgb)
        {
            w.WriteStartElement("fill", MainNs);
            w.WriteStartElement("patternFill", MainNs);
            w.WriteAttributeString("patternType", pattern);
            if (rgb is not null)
            {
                w.WriteStartElement("fgColor", MainNs);
                w.WriteAttributeString("rgb", rgb);
                w.WriteEndElement();
                w.WriteStartElement("bgColor", MainNs);
                w.WriteAttributeString("indexed", "64");
                w.WriteEndElement();
            }
            w.WriteEndElement();
            w.WriteEndElement();
        }

        static void Xf(XmlWriter w, int numFmt, int font, int fill, bool inCellXfs, bool unlocked = false)
        {
            w.WriteStartElement("xf", MainNs);
            w.WriteAttributeString("numFmtId", numFmt.ToString(CultureInfo.InvariantCulture));
            w.WriteAttributeString("fontId", font.ToString(CultureInfo.InvariantCulture));
            w.WriteAttributeString("fillId", fill.ToString(CultureInfo.InvariantCulture));
            w.WriteAttributeString("borderId", "0");
            if (inCellXfs)
            {
                w.WriteAttributeString("xfId", "0");
                if (numFmt != 0) w.WriteAttributeString("applyNumberFormat", "1");
                if (font != 0) w.WriteAttributeString("applyFont", "1");
                if (fill != 0) w.WriteAttributeString("applyFill", "1");
                if (unlocked) w.WriteAttributeString("applyProtection", "1");
            }

            if (unlocked)
            {
                // Takes effect only on a protected sheet; harmless on any other.
                w.WriteStartElement("protection", MainNs);
                w.WriteAttributeString("locked", "0");
                w.WriteEndElement();
            }

            w.WriteEndElement();
        }

        static void Val(XmlWriter w, string element, string value)
        {
            w.WriteStartElement(element, MainNs);
            w.WriteAttributeString("val", value);
            w.WriteEndElement();
        }
    }

    private static void WriteSheet(XmlWriter w, XlsxSheet sheet, bool first)
    {
        bool table = sheet.Columns.Count > 0;
        int width = table ? sheet.Columns.Count : Math.Max(1, sheet.Rows.Select(r => r.Count).DefaultIfEmpty(1).Max());
        int lastRow = sheet.Rows.Count + (table ? 1 : 0);

        w.WriteStartElement("worksheet", MainNs);
        w.WriteAttributeString("xmlns", "r", null, RelNs);

        w.WriteStartElement("dimension", MainNs);
        w.WriteAttributeString("ref", $"A1:{ColumnLetters(width)}{Math.Max(1, lastRow)}");
        w.WriteEndElement();

        w.WriteStartElement("sheetViews", MainNs);
        w.WriteStartElement("sheetView", MainNs);
        if (first)
            w.WriteAttributeString("tabSelected", "1");
        w.WriteAttributeString("workbookViewId", "0");
        if (table)
        {
            int xSplit = Math.Min(sheet.FreezeColumns, width);
            w.WriteStartElement("pane", MainNs);
            if (xSplit > 0)
                w.WriteAttributeString("xSplit", xSplit.ToString(CultureInfo.InvariantCulture));
            w.WriteAttributeString("ySplit", "1");
            w.WriteAttributeString("topLeftCell", ColumnLetters(xSplit + 1) + "2");
            w.WriteAttributeString("activePane", xSplit > 0 ? "bottomRight" : "bottomLeft");
            w.WriteAttributeString("state", "frozen");
            w.WriteEndElement();
        }
        w.WriteEndElement();
        w.WriteEndElement();

        w.WriteStartElement("sheetFormatPr", MainNs);
        w.WriteAttributeString("defaultRowHeight", "15");
        w.WriteEndElement();

        // Column-wide TEXT format: what makes a value TYPED into an empty cell
        // later stay text too, not just the ones written here.
        w.WriteStartElement("cols", MainNs);
        for (int c = 0; c < width; c++)
        {
            XlsxColumn? col = table ? sheet.Columns[c] : null;
            w.WriteStartElement("col", MainNs);
            string n = (c + 1).ToString(CultureInfo.InvariantCulture);
            w.WriteAttributeString("min", n);
            w.WriteAttributeString("max", n);
            w.WriteAttributeString("width", (col?.Width ?? 16).ToString("0.##", CultureInfo.InvariantCulture));
            w.WriteAttributeString("style", ColumnStyle(col).ToString(CultureInfo.InvariantCulture));
            w.WriteAttributeString("customWidth", "1");
            w.WriteEndElement();
        }
        w.WriteEndElement();

        w.WriteStartElement("sheetData", MainNs);
        int r = 1;
        if (table)
        {
            WriteRow(w, r++, [.. sheet.Columns.Select(c => c.Header)],
                c => sheet.Columns[c].Proposed ? StyleProposedHeader
                    : sheet.Columns[c].Muted ? StyleMutedHeader : StyleHeader);
        }
        foreach (IReadOnlyList<string> row in sheet.Rows)
        {
            WriteRow(w, r++, row,
                c => table && c < sheet.Columns.Count ? ColumnStyle(sheet.Columns[c]) : StyleText,
                c => table && c < sheet.Columns.Count && sheet.Columns[c].Kind == XlsxColumnKind.Number);
        }
        w.WriteEndElement();

        // After sheetData and before autoFilter: the order the schema requires.
        if (sheet.Protected)
        {
            w.WriteStartElement("sheetProtection", MainNs);
            w.WriteAttributeString("sheet", "1");
            w.WriteAttributeString("objects", "1");
            w.WriteAttributeString("scenarios", "1");
            w.WriteAttributeString("formatColumns", "0");   // 0 = allowed: resize columns
            w.WriteAttributeString("autoFilter", "0");      // 0 = allowed: filter
            w.WriteEndElement();
        }

        if (table)
        {
            w.WriteStartElement("autoFilter", MainNs);
            w.WriteAttributeString("ref", $"A1:{ColumnLetters(width)}{Math.Max(1, lastRow)}");
            w.WriteEndElement();

            List<(int Column, string List)> lists = [];
            for (int c = 0; c < sheet.Columns.Count; c++)
            {
                if (!string.IsNullOrEmpty(sheet.Columns[c].ListName))
                    lists.Add((c + 1, sheet.Columns[c].ListName!));
            }

            if (lists.Count > 0)
            {
                w.WriteStartElement("dataValidations", MainNs);
                w.WriteAttributeString("count", lists.Count.ToString(CultureInfo.InvariantCulture));
                foreach ((int column, string list) in lists)
                {
                    string letters = ColumnLetters(column);
                    w.WriteStartElement("dataValidation", MainNs);
                    w.WriteAttributeString("type", "list");
                    w.WriteAttributeString("allowBlank", "1");
                    w.WriteAttributeString("showInputMessage", "1");
                    w.WriteAttributeString("showErrorMessage", "1");
                    w.WriteAttributeString("errorTitle", "Not in this model");
                    w.WriteAttributeString("error", "Pick one of the titleblock types loaded in the model.");
                    w.WriteAttributeString("sqref", $"{letters}2:{letters}{ValidationRows}");
                    w.WriteElementString("formula1", MainNs, list);
                    w.WriteEndElement();
                }
                w.WriteEndElement();
            }
        }

        w.WriteStartElement("pageMargins", MainNs);
        w.WriteAttributeString("left", "0.7");
        w.WriteAttributeString("right", "0.7");
        w.WriteAttributeString("top", "0.75");
        w.WriteAttributeString("bottom", "0.75");
        w.WriteAttributeString("header", "0.3");
        w.WriteAttributeString("footer", "0.3");
        w.WriteEndElement();

        w.WriteEndElement();
    }

    private static void WriteRow(
        XmlWriter w, int r, IReadOnlyList<string> cells, Func<int, int> style, Func<int, bool>? isNumber = null)
    {
        string rn = r.ToString(CultureInfo.InvariantCulture);
        w.WriteStartElement("row", MainNs);
        w.WriteAttributeString("r", rn);
        for (int c = 0; c < cells.Count; c++)
        {
            string value = Clean(cells[c]);
            w.WriteStartElement("c", MainNs);
            w.WriteAttributeString("r", ColumnLetters(c + 1) + rn);
            w.WriteAttributeString("s", style(c).ToString(CultureInfo.InvariantCulture));

            if (value.Length > 0 && isNumber is not null && isNumber(c))
            {
                // A real number: no t attribute, a <v> holding the invariant text.
                // Anything that is not one is a bug in the caller - fail loudly
                // rather than put "n/a" into a column Excel will treat as numeric.
                if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double number)
                    || double.IsNaN(number) || double.IsInfinity(number))
                {
                    throw new FormatException(
                        $"Cell {ColumnLetters(c + 1)}{rn} is in a Number column but holds '{value}'.");
                }

                w.WriteElementString("v", MainNs, number.ToString("R", CultureInfo.InvariantCulture));
            }
            else if (value.Length > 0)
            {
                w.WriteAttributeString("t", "inlineStr");
                w.WriteStartElement("is", MainNs);
                w.WriteStartElement("t", MainNs);
                if (value.Trim().Length != value.Length)
                    w.WriteAttributeString("xml", "space", null, "preserve");
                w.WriteString(value);
                w.WriteEndElement();
                w.WriteEndElement();
            }
            w.WriteEndElement();
        }
        w.WriteEndElement();
    }

    /// <summary>
    /// Drops the control characters XML 1.0 cannot carry at all. A Revit value
    /// can hold one (pasted from somewhere), and one would otherwise fail the
    /// whole export.
    /// </summary>
    internal static string Clean(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return "";

        StringBuilder? sb = null;
        for (int i = 0; i < value!.Length; i++)
        {
            char ch = value[i];
            bool ok = ch is '\t' or '\n' or '\r' || (ch >= 0x20 && ch != 0xFFFE && ch != 0xFFFF);
            if (char.IsHighSurrogate(ch))
                ok = i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]);
            else if (char.IsLowSurrogate(ch))
                ok = i > 0 && char.IsHighSurrogate(value[i - 1]);

            if (!ok)
            {
                sb ??= new StringBuilder(value.Substring(0, i));
                continue;
            }
            sb?.Append(ch);
        }
        return sb?.ToString() ?? value;
    }
}
