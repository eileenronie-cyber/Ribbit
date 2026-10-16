using System.IO;
using System.IO.Compression;
using System.Text;
using Ribbit.Geometry.Sheets;
using Xunit;

namespace Ribbit.Tests;

/// <summary>
/// Round-trips the reader against real .xlsx bytes built in-test, so the OOXML
/// parsing is exercised rather than mocked. Each test writes a temp file and
/// deletes it.
/// </summary>
public class XlsxReaderTests : IDisposable
{
    private readonly List<string> _temp = [];

    public void Dispose()
    {
        foreach (string path in _temp)
        {
            try { File.Delete(path); } catch (IOException) { /* best effort */ }
        }
    }

    /// <summary>
    /// Writes a minimal but genuine workbook: one sheet, optional shared strings.
    /// <paramref name="sheetXml"/> is the inner &lt;sheetData&gt; content.
    /// </summary>
    private string MakeWorkbook(string sheetName, string sheetDataXml, params string[] sharedStrings)
    {
        string path = Path.Combine(Path.GetTempPath(), $"ribbit_{Guid.NewGuid():N}.xlsx");
        _temp.Add(path);

        using FileStream fs = File.Create(path);
        using ZipArchive zip = new(fs, ZipArchiveMode.Create);

        void Write(string entry, string content)
        {
            using StreamWriter w = new(zip.CreateEntry(entry).Open(), new UTF8Encoding(false));
            w.Write(content);
        }

        Write("xl/workbook.xml",
            "<?xml version=\"1.0\"?>"
            + "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" "
            + "xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">"
            + $"<sheets><sheet name=\"{sheetName}\" sheetId=\"1\" r:id=\"rId1\"/></sheets>"
            + "</workbook>");

        Write("xl/_rels/workbook.xml.rels",
            "<?xml version=\"1.0\"?>"
            + "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">"
            + "<Relationship Id=\"rId1\" Target=\"worksheets/sheet1.xml\" "
            + "Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\"/>"
            + "</Relationships>");

        if (sharedStrings.Length > 0)
        {
            StringBuilder sst = new();
            sst.Append("<?xml version=\"1.0\"?><sst xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">");
            foreach (string s in sharedStrings)
                sst.Append($"<si><t>{s}</t></si>");
            sst.Append("</sst>");
            Write("xl/sharedStrings.xml", sst.ToString());
        }

        Write("xl/worksheets/sheet1.xml",
            "<?xml version=\"1.0\"?>"
            + "<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">"
            + $"<sheetData>{sheetDataXml}</sheetData></worksheet>");

        return path;
    }

    private static string Row(int r, params string[] cells)
        => $"<row r=\"{r}\">{string.Join("", cells)}</row>";

    /// <summary>An inline-string cell, which avoids needing the shared table.</summary>
    private static string Str(string reference, string value)
        => $"<c r=\"{reference}\" t=\"inlineStr\"><is><t>{value}</t></is></c>";

    /// <summary>A shared-string cell, by index.</summary>
    private static string Shared(string reference, int index)
        => $"<c r=\"{reference}\" t=\"s\"><v>{index}</v></c>";

    private static string Num(string reference, string raw)
        => $"<c r=\"{reference}\"><v>{raw}</v></c>";

    // ------------------------------------------------------------ tab names

    [Fact]
    public void SheetNames_AreReadInTabOrder()
    {
        string path = MakeWorkbook("SHEET_DATA", Row(1, Str("A1", "Sheet Number")));

        using XlsxWorkbook wb = XlsxWorkbook.Open(path);

        Assert.Equal(["SHEET_DATA"], wb.SheetNames);
    }

    // The settings form needs the SILENT contract while the path box is
    // mid-edit - this is the bug the Python's duplicate definition introduced.
    [Fact]
    public void TryListSheetNames_ReturnsEmptyForAMissingFile_RatherThanThrowing()
        => Assert.Empty(XlsxWorkbook.TryListSheetNames(@"C:\nope\not-a-real-file.xlsx"));

    [Fact]
    public void TryListSheetNames_ReturnsEmptyForAnEmptyPath()
        => Assert.Empty(XlsxWorkbook.TryListSheetNames(""));

    [Fact]
    public void Open_ThrowsForAMissingFile()
        => Assert.Throws<SheetMakerError>(() => XlsxWorkbook.Open(@"C:\nope\not-a-real-file.xlsx"));

    [Fact]
    public void ReadWorksheet_ThrowsForAnUnknownTab()
    {
        string path = MakeWorkbook("SHEET_DATA", Row(1, Str("A1", "Sheet Number")));
        using XlsxWorkbook wb = XlsxWorkbook.Open(path);

        Assert.Throws<SheetMakerError>(() => wb.ReadWorksheet("MAPPING"));
    }

    // ---------------------------------------------------------------- cells

    [Fact]
    public void InlineAndSharedStrings_BothRead()
    {
        string path = MakeWorkbook("D",
            Row(1, Str("A1", "Sheet Number"), Str("B1", "Sheet Name"))
            + Row(2, Str("A2", "A-101"), Shared("B2", 0)),
            "Ground Floor Plan");

        using XlsxWorkbook wb = XlsxWorkbook.Open(path);
        List<XlsxRow> rows = wb.ReadWorksheet("D");

        Assert.Equal("A-101", rows.Single().Get("Sheet Number"));
        Assert.Equal("Ground Floor Plan", rows.Single().Get("Sheet Name"));
    }

    // A whole number must read back the way Excel displays it - "915004", not
    // "915004.0", which would go straight into a sheet number.
    [Fact]
    public void WholeNumbers_LoseTheTrailingPointZero()
    {
        string path = MakeWorkbook("D",
            Row(1, Str("A1", "Sheet Number"))
            + Row(2, Num("A2", "915004")));

        using XlsxWorkbook wb = XlsxWorkbook.Open(path);

        Assert.Equal("915004", wb.ReadWorksheet("D").Single().Get("Sheet Number"));
    }

    [Fact]
    public void FractionalNumbers_KeepTheirDecimals()
    {
        string path = MakeWorkbook("D",
            Row(1, Str("A1", "Value")) + Row(2, Num("A2", "12.5")));

        using XlsxWorkbook wb = XlsxWorkbook.Open(path);

        Assert.Equal("12.5", wb.ReadWorksheet("D").Single().Get("Value"));
    }

    // Excel saves what a person TYPES with up to 17 digits: typing 1.1 is stored
    // as 1.1000000000000001 (seen with the installed Excel, 2026-10-10). Read
    // back as Excel shows it - 15 significant digits - or "1.1" typed into a Mark
    // would be written into the model as "1.1000000000000001".
    [Theory]
    [InlineData("1.1000000000000001", "1.1")]
    [InlineData("2.6749999999999998", "2.675")]
    [InlineData("1.1499999999999999", "1.15")]
    [InlineData("0.30000000000000004", "0.3")]
    [InlineData("1.0000000000000001E-5", "0.00001")]
    [InlineData("-3.3", "-3.3")]
    [InlineData("1234.56", "1234.56")]
    [InlineData("1E+20", "100000000000000000000")]
    public void Numbers_read_back_the_way_Excel_shows_them(string raw, string shown)
    {
        string path = MakeWorkbook("D", Row(1, Str("A1", "Value")) + Row(2, Num("A2", raw)));

        using XlsxWorkbook wb = XlsxWorkbook.Open(path);

        Assert.Equal(shown, wb.ReadWorksheet("D").Single().Get("Value"));
    }

    // ----------------------------------------------------------- structure

    [Fact]
    public void HeadersStopAtTheFirstBlankHeaderCell()
    {
        // C1 is populated but B1 is missing, so C must not become a column.
        string path = MakeWorkbook("D",
            Row(1, Str("A1", "Sheet Number"), Str("C1", "Stray"))
            + Row(2, Str("A2", "A-101"), Str("C2", "ignored")));

        using XlsxWorkbook wb = XlsxWorkbook.Open(path);
        XlsxRow row = wb.ReadWorksheet("D").Single();

        Assert.Equal("A-101", row.Get("Sheet Number"));
        Assert.Equal("", row.Get("Stray"));
    }

    // The documented stop condition: reading ends at the first row that is blank
    // across every header column, so trailing junk far below is never reached.
    [Fact]
    public void ReadingStopsAtTheFirstBlankRow()
    {
        string path = MakeWorkbook("D",
            Row(1, Str("A1", "Sheet Number"))
            + Row(2, Str("A2", "A-101"))
            + Row(3, Str("A3", ""))
            + Row(4, Str("A4", "A-102")));

        using XlsxWorkbook wb = XlsxWorkbook.Open(path);
        List<XlsxRow> rows = wb.ReadWorksheet("D");

        Assert.Single(rows);
        Assert.Equal("A-101", rows[0].Get("Sheet Number"));
    }

    // A GAP in row numbers is an empty row too - Excel simply omits the element.
    // The Python's DOM walk would have skipped straight over it and kept going.
    [Fact]
    public void AGapInRowNumbers_AlsoStopsReading()
    {
        string path = MakeWorkbook("D",
            Row(1, Str("A1", "Sheet Number"))
            + Row(2, Str("A2", "A-101"))
            + Row(9000, Str("A9000", "A-999")));

        using XlsxWorkbook wb = XlsxWorkbook.Open(path);
        List<XlsxRow> rows = wb.ReadWorksheet("D");

        Assert.Single(rows);
    }

    [Fact]
    public void MissingHeaderRow_IsAnError()
    {
        string path = MakeWorkbook("D", Row(5, Str("A5", "orphan")));

        using XlsxWorkbook wb = XlsxWorkbook.Open(path);

        Assert.Throws<SheetMakerError>(() => wb.ReadWorksheet("D"));
    }

    // Both worksheets come off ONE open workbook - the Python reopened the zip
    // and re-parsed the whole shared-strings table for each.
    [Fact]
    public void OneOpenWorkbook_ServesRepeatedWorksheetReads()
    {
        string path = MakeWorkbook("D",
            Row(1, Str("A1", "Sheet Number")) + Row(2, Str("A2", "A-101")));

        using XlsxWorkbook wb = XlsxWorkbook.Open(path);

        Assert.Single(wb.ReadWorksheet("D"));
        Assert.Single(wb.ReadWorksheet("D"));
    }
}
