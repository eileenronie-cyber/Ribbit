using System.IO;
using System.IO.Compression;
using System.Xml.Linq;
using Ribbit.Geometry.Clash;

namespace Ribbit.Geometry.Sheets;

/// <summary>One text to put in one cell: the row whose key column holds <see cref="Key"/>, the column headed <see cref="Header"/>.</summary>
public sealed class CellEdit
{
    public CellEdit(string key, string header, string text)
    {
        Key = key;
        Header = header;
        Text = text;
    }

    public string Key { get; }

    public string Header { get; }

    public string Text { get; }
}

/// <summary>
/// A row to ADD: a copy of the row whose key column holds <see cref="SourceKey"/> - its cells, styles
/// and key included, as copy and paste in Excel makes it - with some cells given new text.
/// </summary>
public sealed class RowCopy
{
    public RowCopy(string sourceKey, IReadOnlyList<(string Header, string Text)> values)
    {
        SourceKey = sourceKey;
        Values = values;
    }

    public string SourceKey { get; }

    public IReadOnlyList<(string Header, string Text)> Values { get; }
}

public enum CellEditOutcome
{
    Written,
    RowNotFound,
    ColumnNotFound,
}

/// <summary>
/// Writes text into cells of an existing workbook, in place: the cells only -
/// every other sheet, style, protection and hidden part is left as it was.
///
/// Works on a file as Excel leaves it, not only as the add-in wrote it: the
/// sheet is found through the workbook's relationships (not its position), and
/// headers and keys may be shared strings. A cell is written as an inline
/// string with its style kept (the style is what makes it unlocked). A formula
/// in an overwritten cell goes, and with it Excel's calculation index, which
/// Excel rebuilds - a stale index is what makes Excel offer to "repair" a file.
///
/// The file is replaced atomically: a failure (the file open in Excel, a full
/// disk) leaves it exactly as it was.
/// </summary>
public static class XlsxCellEditor
{
    private static readonly XNamespace Ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace RelNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace PkgRelNs = "http://schemas.openxmlformats.org/package/2006/relationships";
    private static readonly XNamespace TypesNs = "http://schemas.openxmlformats.org/package/2006/content-types";

    /// <summary>One outcome per edit, in order. Throws only for a file that is not a workbook or cannot be written.</summary>
    public static IReadOnlyList<CellEditOutcome> Apply(
        string path, string sheetName, string keyHeader, IReadOnlyList<CellEdit> edits)
        => Apply(path, sheetName, keyHeader, edits, []).Edits;

    /// <summary>
    /// The edits, then the copies (each appended after the last row). One outcome per edit and per
    /// copy, in order: a copy whose source row is not there is RowNotFound, a value whose column is
    /// not there leaves that cell as copied and makes the copy ColumnNotFound.
    /// </summary>
    public static (IReadOnlyList<CellEditOutcome> Edits, IReadOnlyList<CellEditOutcome> Copies) Apply(
        string path, string sheetName, string keyHeader, IReadOnlyList<CellEdit> edits, IReadOnlyList<RowCopy> copies)
    {
        CellEditOutcome[] copied = new CellEditOutcome[copies.Count];
        MemoryStream buffer = new();
        using (FileStream source = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            source.CopyTo(buffer);

        CellEditOutcome[] outcomes = new CellEditOutcome[edits.Count];
        bool formulaRemoved = false;

        using (ZipArchive zip = new(buffer, ZipArchiveMode.Update, leaveOpen: true))
        {
            string sheetPath = SheetPath(zip, sheetName)
                               ?? throw new SheetMakerError($"The workbook has no '{sheetName}' sheet.");
            ZipArchiveEntry entry = zip.GetEntry(sheetPath)
                                    ?? throw new SheetMakerError($"The workbook's '{sheetName}' sheet is missing its part.");

            XDocument sheet;
            using (Stream s = entry.Open())
                sheet = XDocument.Load(s);

            List<string> shared = SharedStrings(zip);
            XElement data = sheet.Root?.Element(Ns + "sheetData")
                            ?? throw new SheetMakerError($"The '{sheetName}' sheet has no cells.");
            List<XElement> rows = [.. data.Elements(Ns + "row")];

            // Headers: the first row.
            Dictionary<string, int> columnOf = new(StringComparer.Ordinal);
            if (rows.Count > 0)
            {
                foreach ((XElement c, int index) in Cells(rows[0]))
                {
                    string text = TextOf(c, shared);
                    if (text.Length > 0 && !columnOf.ContainsKey(text))
                        columnOf[text] = index;
                }
            }

            // Rows by key (the first row holding a key wins; callers refuse duplicated keys).
            Dictionary<string, XElement> rowOf = new(StringComparer.Ordinal);
            if (columnOf.TryGetValue(keyHeader, out int keyColumn))
            {
                foreach (XElement row in rows.Skip(1))
                {
                    XElement? keyCell = Cells(row).FirstOrDefault(x => x.Index == keyColumn).Cell;
                    string key = keyCell is null ? "" : TextOf(keyCell, shared).Trim();
                    if (key.Length > 0 && !rowOf.ContainsKey(key))
                        rowOf[key] = row;
                }
            }

            for (int i = 0; i < edits.Count; i++)
            {
                CellEdit edit = edits[i];
                if (!columnOf.TryGetValue(edit.Header, out int column))
                {
                    outcomes[i] = CellEditOutcome.ColumnNotFound;
                    continue;
                }

                if (!rowOf.TryGetValue(edit.Key, out XElement? row))
                {
                    outcomes[i] = CellEditOutcome.RowNotFound;
                    continue;
                }

                XElement cell = CellAt(row, column, rows);
                formulaRemoved |= cell.Element(Ns + "f") is not null;

                // Keep the style (s): it is what unlocks the cell. Everything else is the new text.
                Write(cell, edit.Text);
                outcomes[i] = CellEditOutcome.Written;
            }

            // Copies: after the last row, numbered on from it, every cell moved to the new row.
            int last = rows.Select(r => int.TryParse((string?)r.Attribute("r"), out int n) ? n : 0).DefaultIfEmpty(0).Max();
            for (int i = 0; i < copies.Count; i++)
            {
                if (!rowOf.TryGetValue(copies[i].SourceKey, out XElement? source))
                {
                    copied[i] = CellEditOutcome.RowNotFound;
                    continue;
                }

                int number = ++last;
                XElement row = new(source);
                row.SetAttributeValue("r", number);
                foreach ((XElement c, int index) in Cells(row).ToList())
                {
                    c.SetAttributeValue("r", ColumnName(index) + number);
                    if (c.Element(Ns + "f") is { } formula)
                        formula.Remove();   // a copied formula would point at the wrong row; its value stays
                }

                copied[i] = CellEditOutcome.Written;
                foreach ((string header, string text) in copies[i].Values)
                {
                    if (columnOf.TryGetValue(header, out int column))
                        Write(CellAt(row, column, rows), text);
                    else
                        copied[i] = CellEditOutcome.ColumnNotFound;
                }

                data.Add(row);
                rows.Add(row);
            }

            if (copies.Count > 0 && copied.Any(o => o != CellEditOutcome.RowNotFound))
                StretchDimension(sheet, last);

            if (outcomes.Any(o => o == CellEditOutcome.Written) || copied.Any(o => o != CellEditOutcome.RowNotFound))
            {
                entry.Delete();
                using (Stream w = zip.CreateEntry(sheetPath, CompressionLevel.Optimal).Open())
                    sheet.Save(w);

                if (formulaRemoved)
                    DropCalcChain(zip);
            }
        }

        if (outcomes.Any(o => o == CellEditOutcome.Written) || copied.Any(o => o != CellEditOutcome.RowNotFound))
        {
            string temp = AtomicFile.TempPathFor(path);
            File.WriteAllBytes(temp, buffer.ToArray());
            AtomicFile.Commit(temp, path);
        }

        return (outcomes, copied);
    }

    /// <summary>Text into a cell, as an inline string. The style (s) is kept: it is what unlocks the cell.</summary>
    private static void Write(XElement cell, string text)
    {
        cell.RemoveNodes();
        cell.SetAttributeValue("t", "inlineStr");
        cell.Add(new XElement(Ns + "is", new XElement(Ns + "t",
            new XAttribute(XNamespace.Xml + "space", "preserve"), text)));
    }

    /// <summary>The sheet's stated extent ("A1:F12") grown to its new last row, so Excel reads the added rows.</summary>
    private static void StretchDimension(XDocument sheet, int lastRow)
    {
        XAttribute? reference = sheet.Root?.Element(Ns + "dimension")?.Attribute("ref");
        if (reference is null)
            return;

        string[] ends = reference.Value.Split(':');
        string to = ends[ends.Length - 1];
        string letters = new string(to.TakeWhile(char.IsLetter).ToArray());
        reference.Value = (ends.Length > 1 ? ends[0] : "A1") + ":" + letters + lastRow;
    }

    /// <summary>The cells of a row with their column index (from "r", or by position when a cell has none).</summary>
    private static IEnumerable<(XElement Cell, int Index)> Cells(XElement row)
    {
        int next = 0;
        foreach (XElement c in row.Elements(Ns + "c"))
        {
            string? r = (string?)c.Attribute("r");
            int index = r is null ? next : ColumnIndex(r);
            next = index + 1;
            yield return (c, index);
        }
    }

    /// <summary>
    /// The row's cell in a column, inserted in column order when the row has none there. A new
    /// cell takes the style of the column's other data cells - an unlocked column stays unlocked
    /// for a person who goes on editing by hand.
    /// </summary>
    private static XElement CellAt(XElement row, int column, List<XElement> rows)
    {
        foreach ((XElement c, int index) in Cells(row))
        {
            if (index == column)
                return c;
        }

        string rowNumber = (string?)row.Attribute("r") ?? "";
        XElement cell = new(Ns + "c", new XAttribute("r", ColumnName(column) + rowNumber));

        string? style = rows.Skip(1)
            .Select(r => Cells(r).FirstOrDefault(x => x.Index == column).Cell)
            .Select(c => (string?)c?.Attribute("s"))
            .FirstOrDefault(s => s is not null);
        if (style is not null)
            cell.SetAttributeValue("s", style);

        XElement? before = Cells(row).Where(x => x.Index < column).Select(x => x.Cell).LastOrDefault();
        if (before is null)
            row.AddFirst(cell);
        else
            before.AddAfterSelf(cell);

        return cell;
    }

    private static string TextOf(XElement cell, List<string> shared)
    {
        string? type = (string?)cell.Attribute("t");
        if (type == "inlineStr")
            return string.Concat(cell.Element(Ns + "is")?.Descendants(Ns + "t").Select(t => t.Value) ?? []);

        string value = cell.Element(Ns + "v")?.Value ?? "";
        if (type == "s")
            return int.TryParse(value, out int i) && i >= 0 && i < shared.Count ? shared[i] : "";

        return value;
    }

    private static List<string> SharedStrings(ZipArchive zip)
    {
        List<string> list = [];
        ZipArchiveEntry? entry = zip.GetEntry("xl/sharedStrings.xml");
        if (entry is null)
            return list;

        XDocument d;
        using (Stream s = entry.Open())
            d = XDocument.Load(s);

        foreach (XElement si in d.Root?.Elements(Ns + "si") ?? [])
        {
            // Plain (<t>) or rich text (<r><t>): the text is all the t's, phonetic runs excluded.
            list.Add(string.Concat(si.Elements(Ns + "t").Select(t => t.Value)
                .Concat(si.Elements(Ns + "r").SelectMany(r => r.Elements(Ns + "t")).Select(t => t.Value))));
        }

        return list;
    }

    /// <summary>The sheet's part, through the workbook's relationships - Excel numbers parts as it likes.</summary>
    private static string? SheetPath(ZipArchive zip, string name)
    {
        ZipArchiveEntry? wb = zip.GetEntry("xl/workbook.xml");
        ZipArchiveEntry? rels = zip.GetEntry("xl/_rels/workbook.xml.rels");
        if (wb is null || rels is null)
            return null;

        XDocument w, r;
        using (Stream s = wb.Open())
            w = XDocument.Load(s);
        using (Stream s = rels.Open())
            r = XDocument.Load(s);

        string? rid = w.Root?.Element(Ns + "sheets")?.Elements(Ns + "sheet")
            .FirstOrDefault(x => (string?)x.Attribute("name") == name)?.Attribute(RelNs + "id")?.Value;
        if (rid is null)
            return null;

        string? target = r.Root?.Elements(PkgRelNs + "Relationship")
            .FirstOrDefault(x => (string?)x.Attribute("Id") == rid)?.Attribute("Target")?.Value;
        if (target is null)
            return null;

        return target.StartsWith("/", StringComparison.Ordinal) ? target.TrimStart('/') : "xl/" + target;
    }

    /// <summary>Removes Excel's calculation chain and every reference to it; Excel rebuilds it on open.</summary>
    private static void DropCalcChain(ZipArchive zip)
    {
        zip.GetEntry("xl/calcChain.xml")?.Delete();

        Rewrite(zip, "xl/_rels/workbook.xml.rels", d =>
            d.Root?.Elements(PkgRelNs + "Relationship")
                .Where(x => ((string?)x.Attribute("Target") ?? "").EndsWith("calcChain.xml", StringComparison.OrdinalIgnoreCase))
                .Remove());

        Rewrite(zip, "[Content_Types].xml", d =>
            d.Root?.Elements(TypesNs + "Override")
                .Where(x => ((string?)x.Attribute("PartName") ?? "").EndsWith("calcChain.xml", StringComparison.OrdinalIgnoreCase))
                .Remove());
    }

    private static void Rewrite(ZipArchive zip, string part, Action<XDocument> change)
    {
        ZipArchiveEntry? entry = zip.GetEntry(part);
        if (entry is null)
            return;

        XDocument d;
        using (Stream s = entry.Open())
            d = XDocument.Load(s);
        change(d);
        entry.Delete();
        using Stream w = zip.CreateEntry(part, CompressionLevel.Optimal).Open();
        d.Save(w);
    }

    /// <summary>"A1" is column 0, "B7" is 1, "AA2" is 26.</summary>
    internal static int ColumnIndex(string reference)
    {
        int n = 0;
        foreach (char ch in reference)
        {
            if (!char.IsLetter(ch))
                break;
            n = n * 26 + (char.ToUpperInvariant(ch) - 'A' + 1);
        }

        return n - 1;
    }

    internal static string ColumnName(int index)
    {
        string name = "";
        for (int n = index + 1; n > 0; n = (n - 1) / 26)
            name = (char)('A' + (n - 1) % 26) + name;
        return name;
    }
}
