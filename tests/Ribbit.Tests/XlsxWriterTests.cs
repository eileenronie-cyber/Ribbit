using System.IO;
using System.IO.Compression;
using System.Xml.Linq;
using Ribbit.Geometry.Sheets;
using Xunit;

namespace Ribbit.Tests;

/// <summary>
/// The writer is only worth anything if its files read back exactly and open in
/// Excel as text. Every test here writes a real file and reads it back through
/// <see cref="XlsxWorkbook"/> or straight out of the zip.
/// </summary>
public class XlsxWriterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ribbit_w_" + Guid.NewGuid().ToString("N"));

    public XlsxWriterTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* best effort */ }
    }

    private string PathFor(string name = "book.xlsx") => Path.Combine(_dir, name);

    private static XlsxWriter Table(params IReadOnlyList<string>[] rows)
    {
        XlsxWriter book = new();
        XlsxSheet s = book.AddSheet("Sheets");
        s.Columns.Add(new XlsxColumn("Sheet Number"));
        s.Columns.Add(new XlsxColumn("Sheet Name"));
        s.Columns.Add(new XlsxColumn("Locked") { Muted = true });
        foreach (IReadOnlyList<string> r in rows)
            s.Rows.Add(r);
        return book;
    }

    private static XDocument Part(string path, string part)
    {
        using ZipArchive zip = ZipFile.OpenRead(path);
        ZipArchiveEntry entry = zip.GetEntry(part) ?? throw new Xunit.Sdk.XunitException("no part " + part);
        using Stream s = entry.Open();
        return XDocument.Load(s);
    }

    private static readonly XNamespace Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    [Fact]
    public void Values_ReadBackExactly_IncludingTheOnesExcelWouldMangle()
    {
        string path = PathFor();
        Table(
            ["01", "Ground floor - plan", "x"],
            ["1-2", "  leading and trailing  ", ""],
            ["A-101", "Ünïcödé ✓ & <tags>", "y"]).Save(path);

        using XlsxWorkbook wb = XlsxWorkbook.Open(path);
        List<XlsxRow> rows = wb.ReadWorksheet("Sheets");

        Assert.Equal(3, rows.Count);
        Assert.Equal("01", rows[0].Get("Sheet Number"));
        Assert.Equal("1-2", rows[1].Get("Sheet Number"));
        Assert.Equal("  leading and trailing  ", rows[1].Get("Sheet Name"));
        Assert.Equal("", rows[1].Get("Locked"));
        Assert.Equal("Ünïcödé ✓ & <tags>", rows[2].Get("Sheet Name"));
        Assert.Equal([2, 3, 4], rows.Select(r => r.RowNumber));
    }

    // "01" typed into a General cell becomes 1. The column-wide text format is
    // what keeps a value typed LATER as text, so it is pinned here.
    [Fact]
    public void EveryColumn_IsFormattedAsText_AndMutedColumnsAreGrey()
    {
        string path = PathFor();
        Table(["01", "a", "b"]).Save(path);

        XDocument styles = Part(path, "xl/styles.xml");
        List<XElement> xfs = [.. styles.Root!.Element(Main + "cellXfs")!.Elements(Main + "xf")];

        XDocument sheet = Part(path, "xl/worksheets/sheet1.xml");
        List<XElement> cols = [.. sheet.Root!.Element(Main + "cols")!.Elements(Main + "col")];
        Assert.Equal(3, cols.Count);

        foreach (XElement col in cols)
        {
            XElement xf = xfs[(int)col.Attribute("style")!];
            Assert.Equal("49", (string)xf.Attribute("numFmtId")!);   // "@", Text
        }

        Assert.NotEqual("0", (string)xfs[(int)cols[2].Attribute("style")!].Attribute("fillId")!);
        Assert.Equal("0", (string)xfs[(int)cols[0].Attribute("style")!].Attribute("fillId")!);
    }

    [Fact]
    public void EveryPart_IsWellFormed_AndDeclaredInContentTypes()
    {
        string path = PathFor();
        XlsxWriter book = Table(["A1", "One", ""]);
        XlsxSheet hidden = book.AddSheet("_lists");
        hidden.Visibility = XlsxVisibility.VeryHidden;
        hidden.Columns.Add(new XlsxColumn("Titleblock"));
        hidden.Rows.Add(["A1 : Metric"]);
        book.DefineName("Lists", hidden, 1, 2, 2);
        book.Sheets[0].Columns[1].ListName = "Lists";
        book.Save(path);

        using ZipArchive zip = ZipFile.OpenRead(path);
        foreach (ZipArchiveEntry e in zip.Entries)
        {
            using Stream s = e.Open();
            XDocument.Load(s);   // throws on malformed XML
        }

        XDocument types = Part(path, "[Content_Types].xml");
        XNamespace t = "http://schemas.openxmlformats.org/package/2006/content-types";
        HashSet<string> declared = [.. types.Root!.Elements(t + "Override").Select(o => (string)o.Attribute("PartName")!)];
        foreach (ZipArchiveEntry e in zip.Entries.Where(e => e.FullName.StartsWith("xl/") && !e.FullName.Contains("_rels")))
            Assert.Contains("/" + e.FullName, declared);
    }

    [Fact]
    public void HiddenSheets_AreMarked_AndStillReadable()
    {
        string path = PathFor();
        XlsxWriter book = Table(["A1", "One", ""]);
        XlsxSheet h = book.AddSheet("_hidden");
        h.Visibility = XlsxVisibility.Hidden;
        h.Columns.Add(new XlsxColumn("Key"));
        h.Rows.Add(["v"]);
        XlsxSheet vh = book.AddSheet("_veryhidden");
        vh.Visibility = XlsxVisibility.VeryHidden;
        vh.Columns.Add(new XlsxColumn("Key"));
        vh.Rows.Add(["w"]);
        book.Save(path);

        XDocument wbXml = Part(path, "xl/workbook.xml");
        Dictionary<string, string?> state = wbXml.Root!.Element(Main + "sheets")!.Elements(Main + "sheet")
            .ToDictionary(s => (string)s.Attribute("name")!, s => (string?)s.Attribute("state"));
        Assert.Null(state["Sheets"]);
        Assert.Equal("hidden", state["_hidden"]);
        Assert.Equal("veryHidden", state["_veryhidden"]);

        using XlsxWorkbook wb = XlsxWorkbook.Open(path);
        Assert.Equal(["Sheets", "_hidden", "_veryhidden"], wb.SheetNames);
        Assert.Equal("w", wb.ReadWorksheet("_veryhidden")[0].Get("Key"));
    }

    [Fact]
    public void ListColumn_GetsADropdown_FedByADefinedName()
    {
        string path = PathFor();
        XlsxWriter book = Table(["A1", "One", ""]);
        XlsxSheet lists = book.AddSheet("_lists");
        lists.Visibility = XlsxVisibility.VeryHidden;
        lists.Columns.Add(new XlsxColumn("Titleblock"));
        lists.Rows.Add(["A"]);
        lists.Rows.Add(["B"]);
        book.DefineName("Tb", lists, 1, 2, 3);
        book.Sheets[0].Columns[2].ListName = "Tb";
        book.Save(path);

        XElement dv = Part(path, "xl/worksheets/sheet1.xml").Root!
            .Element(Main + "dataValidations")!.Element(Main + "dataValidation")!;
        Assert.Equal("list", (string)dv.Attribute("type")!);
        Assert.StartsWith("C2:C", (string)dv.Attribute("sqref")!);
        Assert.Equal("Tb", (string)dv.Element(Main + "formula1")!);

        XElement name = Part(path, "xl/workbook.xml").Root!.Element(Main + "definedNames")!
            .Elements(Main + "definedName").Single(n => (string)n.Attribute("name")! == "Tb");
        Assert.Equal("'_lists'!$A$2:$A$3", name.Value);
    }

    [Fact]
    public void HeaderRow_IsFrozen_AndFiltered()
    {
        string path = PathFor();
        XlsxWriter book = Table(["A1", "One", ""]);
        book.Sheets[0].FreezeColumns = 2;
        book.Save(path);

        XElement root = Part(path, "xl/worksheets/sheet1.xml").Root!;
        XElement pane = root.Element(Main + "sheetViews")!.Element(Main + "sheetView")!.Element(Main + "pane")!;
        Assert.Equal("frozen", (string)pane.Attribute("state")!);
        Assert.Equal("1", (string)pane.Attribute("ySplit")!);
        Assert.Equal("2", (string)pane.Attribute("xSplit")!);
        Assert.Equal("A1:C2", (string)root.Element(Main + "autoFilter")!.Attribute("ref")!);
    }

    [Fact]
    public void ControlCharacters_AreDropped_NotFatal()
    {
        string path = PathFor();
        Table(["A1", "bad\u0001name\u0007", "tab\there"]).Save(path);

        using XlsxWorkbook wb = XlsxWorkbook.Open(path);
        XlsxRow row = wb.ReadWorksheet("Sheets")[0];
        Assert.Equal("badname", row.Get("Sheet Name"));
        Assert.Equal("tab\there", row.Get("Locked"));
    }

    [Fact]
    public void Save_ReplacesAnExistingFile_AndLeavesNoTempBehind()
    {
        string path = PathFor();
        Table(["A1", "first", ""]).Save(path);
        Table(["A1", "second", ""]).Save(path);

        using (XlsxWorkbook wb = XlsxWorkbook.Open(path))
            Assert.Equal("second", wb.ReadWorksheet("Sheets")[0].Get("Sheet Name"));

        Assert.Equal(["book.xlsx"], Directory.GetFiles(_dir).Select(Path.GetFileName));
    }

    // Excel holds an open workbook with a write handle. The old ZipFile.OpenRead
    // asked for FileShare.Read and was refused, so the file had to be closed in
    // Excel before Revit could read it.
    [Fact]
    public void Reader_Opens_AWorkbookSomeoneElseHasOpenForWriting()
    {
        string path = PathFor();
        Table(["A1", "One", ""]).Save(path);

        using FileStream excel = new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read | FileShare.Delete);
        using XlsxWorkbook wb = XlsxWorkbook.Open(path);
        Assert.Equal("One", wb.ReadWorksheet("Sheets")[0].Get("Sheet Name"));
    }

    [Theory]
    [InlineData(1, "A")]
    [InlineData(26, "Z")]
    [InlineData(27, "AA")]
    [InlineData(52, "AZ")]
    [InlineData(702, "ZZ")]
    [InlineData(703, "AAA")]
    public void ColumnLetters_CountLikeExcel(int column, string letters)
        => Assert.Equal(letters, XlsxWriter.ColumnLetters(column));

    [Theory]
    [InlineData("Sheets", true)]
    [InlineData("", false)]
    [InlineData("a/b", false)]
    [InlineData("[x]", false)]
    [InlineData("1234567890123456789012345678901", true)]
    [InlineData("12345678901234567890123456789012", false)]
    public void SheetNames_FollowExcelsRules(string name, bool ok)
        => Assert.Equal(ok, XlsxWriter.SheetNameProblem(name).Length == 0);

    [Fact]
    public void FirstSheet_MustBeVisible()
    {
        XlsxWriter book = new();
        book.AddSheet("_x").Visibility = XlsxVisibility.Hidden;
        Assert.Throws<InvalidOperationException>(() => book.Save(PathFor()));
    }

    // ------------------------------------------------ reader: the new skip mode

    [Fact]
    public void Reader_SkipMode_ReadsPastBlankRows_WithTheirRealRowNumbers()
    {
        string path = PathFor();
        Table(["A1", "One", ""], ["", "", ""], ["A3", "Three", ""], ["  ", "", ""], ["A5", "Five", ""]).Save(path);

        using XlsxWorkbook wb = XlsxWorkbook.Open(path);

        List<XlsxRow> stop = wb.ReadWorksheet("Sheets");
        Assert.Equal(["A1"], stop.Select(r => r.Get("Sheet Number")));

        List<XlsxRow> all = wb.ReadWorksheet("Sheets", stopAtBlankRow: false);
        Assert.Equal(["A1", "A3", "A5"], all.Select(r => r.Get("Sheet Number")));
        Assert.Equal([2, 4, 6], all.Select(r => r.RowNumber));
    }

    [Fact]
    public void Reader_ReadTable_ReportsHeaders_EvenWithNoRows()
    {
        string path = PathFor();
        Table().Save(path);

        using XlsxWorkbook wb = XlsxWorkbook.Open(path);
        (List<string> headers, List<XlsxRow> rows) = wb.ReadTable("Sheets");
        Assert.Equal(["Sheet Number", "Sheet Name", "Locked"], headers);
        Assert.Empty(rows);
    }
}
