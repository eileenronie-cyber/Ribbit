using System.IO;
using Ribbit.Geometry.Sheets;
using Xunit;

namespace Ribbit.Tests;

/// <summary>
/// Builds a real exchange workbook, lets a test edit the Sheets tab the way a
/// person would in Excel, saves it, and reads it back - so the planner is
/// always fed a file that went through the writer and the reader.
/// </summary>
internal sealed class ExchangeFixture : IDisposable
{
    public const string ModelKey = "model-key-1";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ribbit_x_" + Guid.NewGuid().ToString("N"));

    public List<ExchangeColumn> Columns { get; }
    public List<ModelSheet> Model { get; } = [];
    public List<string> Titleblocks { get; } = ["A1 Metric : A1", "A3 Metric : A3"];

    /// <summary>The Sheets tab's rows, as they will be saved; edit these.</summary>
    public List<List<string>> Rows { get; } = [];

    public ExchangeFixture(bool titleblock = true, params ExchangeParameter[] parameters)
    {
        Directory.CreateDirectory(_dir);
        Columns = ExchangeFile.MakeColumns(
            parameters.Length > 0
                ? parameters
                : [new ExchangeParameter("Drawn By", ValueKind.Text, false),
                   new ExchangeParameter("Revision Count", ValueKind.Integer, false),
                   new ExchangeParameter("Issued", ValueKind.YesNo, false),
                   new ExchangeParameter("Current Revision", ValueKind.Text, true)],
            titleblock);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* best effort */ }
    }

    /// <summary>A sheet in the model; values are given for the parameter columns in order.</summary>
    public ModelSheet Sheet(string id, string number, string name, string titleblock = "A1 Metric : A1", params string[] values)
    {
        Dictionary<string, string> v = new(StringComparer.OrdinalIgnoreCase);
        List<ExchangeColumn> ps = [.. Columns.Where(c => c.Role == ColumnRole.Parameter)];
        for (int i = 0; i < ps.Count; i++)
            v[ps[i].Header] = i < values.Length ? values[i] : "";
        ModelSheet s = new(id, number, name, titleblock, v);
        Model.Add(s);
        return s;
    }

    public int Col(string header) => Columns.FindIndex(c => c.Header == header);

    /// <summary>
    /// "Export": snapshot the model as it is NOW into the base copy and the
    /// Sheets rows. Change the model afterwards to simulate edits in Revit.
    /// </summary>
    public XlsxWriter Export(string modelKey = ModelKey)
    {
        List<ExchangeSheet> sheets = [];
        foreach (ModelSheet s in Model)
        {
            Dictionary<string, string> v = new(StringComparer.OrdinalIgnoreCase);
            foreach (ExchangeColumn c in Columns)
            {
                v[c.Header] = c.Role switch
                {
                    ColumnRole.Number => s.Number,
                    ColumnRole.Name => s.Name,
                    ColumnRole.Titleblock => s.Titleblock,
                    _ => s.Values.TryGetValue(c.Header, out string? x) ? x : "",
                };
            }
            sheets.Add(new ExchangeSheet(s.Id, v));
        }

        XlsxWriter book = ExchangeFile.Build(
            new ExchangeMeta(modelKey, "Test Model", "2026-09-30 07:00", "tester", "2026"),
            Columns, sheets, Titleblocks);

        Rows.Clear();
        foreach (IReadOnlyList<string> r in book.Sheets[0].Rows)
            Rows.Add([.. r]);
        return book;
    }

    public void Set(int row, string header, string value) => Rows[row][Col(header)] = value;

    public void SetId(int row, string id) => Rows[row][Columns.Count] = id;

    /// <summary>A row added at the bottom in Excel: no Ribbit ID.</summary>
    public List<string> AddRow(string number, string name = "", string titleblock = "")
    {
        List<string> r = [.. Enumerable.Repeat("", Columns.Count + 1)];
        r[Col("Sheet Number")] = number;
        r[Col("Sheet Name")] = name;
        if (Col("Titleblock") >= 0)
            r[Col("Titleblock")] = titleblock;
        Rows.Add(r);
        return r;
    }

    /// <summary>Save the edited rows and read the file back, as the import will.</summary>
    public ExchangeFile Reload(XlsxWriter book)
    {
        book.Sheets[0].Rows.Clear();
        foreach (List<string> r in Rows)
            book.Sheets[0].Rows.Add(r);

        string path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".xlsx");
        book.Save(path);
        using XlsxWorkbook wb = XlsxWorkbook.Open(path);
        return ExchangeFile.Read(wb);
    }

    public ImportPlan Plan(XlsxWriter book, ImportOptions? options = null, string modelKey = ModelKey)
        => SheetImportPlanner.Plan(Reload(book), modelKey, Model, Titleblocks, options);

    /// <summary>Replace a model sheet (simulating an edit in Revit after the export).</summary>
    public void Revit(string id, Func<ModelSheet, ModelSheet> edit)
    {
        int i = Model.FindIndex(s => s.Id == id);
        Model[i] = edit(Model[i]);
    }

    public static ModelSheet WithValue(ModelSheet s, string header, string value)
    {
        Dictionary<string, string> v = new(s.Values, StringComparer.OrdinalIgnoreCase) { [header] = value };
        return s with { Values = v };
    }
}

public class SheetExchangeTests
{
    [Fact]
    public void Export_ReadsBack_MetaColumnsBaseAndRows()
    {
        using ExchangeFixture fx = new();
        fx.Sheet("id-1", "A101", "Plan", "A1 Metric : A1", "JT", "3", "Yes", "B");
        fx.Sheet("id-2", "A102", "Sections", "A3 Metric : A3", "", "0", "No", "");
        XlsxWriter book = fx.Export();

        ExchangeFile file = fx.Reload(book);

        Assert.Equal(ExchangeFixture.ModelKey, file.Meta.ModelKey);
        Assert.Equal("Test Model", file.Meta.ModelTitle);
        Assert.Equal("tester", file.Meta.ExportedBy);
        Assert.Equal(ExchangeFile.DataSheetName, file.Meta.DataSheet);

        Assert.Equal(fx.Columns, file.Columns);
        Assert.Equal(
            ["Sheet Number", "Sheet Name", "Titleblock", "Drawn By", "Revision Count", "Issued", "Current Revision", "Ribbit ID"],
            file.Headers);

        Assert.Equal(2, file.Rows.Count);
        Assert.Equal("id-1", file.Rows[0].Id);
        Assert.Equal(2, file.Rows[0].ExcelRow);
        Assert.Equal("JT", file.Rows[0].Get("Drawn By"));
        Assert.Equal("A3 Metric : A3", file.Rows[1].Get("Titleblock"));

        Assert.Equal("Plan", file.Base["id-1"]["Sheet Name"]);
        Assert.Equal("No", file.Base["id-2"]["Issued"]);
    }

    [Fact]
    public void Export_HidesItsBookkeeping_AndKeepsTheSheetsTabFirst()
    {
        using ExchangeFixture fx = new();
        fx.Sheet("id-1", "A101", "Plan");
        XlsxWriter book = fx.Export();

        Assert.Equal(ExchangeFile.DataSheetName, book.Sheets[0].Name);
        Assert.Equal(XlsxVisibility.Visible, book.Sheets.Single(s => s.Name == ExchangeFile.HowToSheet).Visibility);
        Assert.All(book.Sheets.Where(s => s.Name.StartsWith("_ribbit")),
            s => Assert.Equal(XlsxVisibility.VeryHidden, s.Visibility));

        // read-only parameter columns and the id are grey; the titleblock has its dropdown
        XlsxSheet data = book.Sheets[0];
        Assert.True(data.Columns.Single(c => c.Header == "Current Revision").Muted);
        Assert.True(data.Columns.Single(c => c.Header == ExchangeFile.IdHeader).Muted);
        Assert.False(data.Columns.Single(c => c.Header == "Drawn By").Muted);
        Assert.Equal(ExchangeFile.TitleblockListName, data.Columns.Single(c => c.Header == "Titleblock").ListName);
    }

    [Fact]
    public void MakeColumns_KeepsHeadersUnique()
    {
        List<ExchangeColumn> cols = ExchangeFile.MakeColumns(
        [
            new ExchangeParameter("Sheet Name", ValueKind.Text, false),     // collides with the fixed column
            new ExchangeParameter("Drawn By", ValueKind.Text, false),
            new ExchangeParameter("drawn  by", ValueKind.Text, false),      // same one again, other spelling
            new ExchangeParameter("Ribbit ID", ValueKind.Text, false),      // collides with the id column
            new ExchangeParameter("", ValueKind.Text, false),
        ], includeTitleblock: false);

        Assert.Equal(
            ["Sheet Number", "Sheet Name", "Sheet Name (parameter)", "Drawn By", "drawn  by (parameter)", "Ribbit ID (parameter)"],
            cols.Select(c => c.Header));
        Assert.Equal("Sheet Name", cols[2].Parameter);
    }

    [Fact]
    public void Read_RefusesAWorkbookThatIsNotAnExport()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ribbit_{Guid.NewGuid():N}.xlsx");
        try
        {
            XlsxWriter book = new();
            XlsxSheet s = book.AddSheet("Data");
            s.Columns.Add(new XlsxColumn("Sheet Number"));
            book.Save(path);

            Assert.False(ExchangeFile.IsExchangeFile(path));
            using XlsxWorkbook wb = XlsxWorkbook.Open(path);
            Assert.Throws<SheetMakerError>(() => ExchangeFile.Read(wb));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Read_RefusesANewerFormat()
    {
        using ExchangeFixture fx = new();
        fx.Sheet("id-1", "A101", "Plan");
        XlsxWriter book = fx.Export();
        XlsxSheet meta = book.Sheets.Single(s => s.Name == ExchangeFile.MetaSheet);
        int i = meta.Rows.FindIndex(r => r[0] == "Version");
        meta.Rows[i] = ["Version", "99"];

        SheetMakerError e = Assert.Throws<SheetMakerError>(() => fx.Reload(book));
        Assert.Contains("newer Ribbit", e.Message);
    }

    [Fact]
    public void Read_FindsTheSheetsTab_EvenIfTheMetaNamesAnotherOne()
    {
        using ExchangeFixture fx = new();
        fx.Sheet("id-1", "A101", "Plan");
        XlsxWriter book = fx.Export();
        XlsxSheet meta = book.Sheets.Single(s => s.Name == ExchangeFile.MetaSheet);
        int i = meta.Rows.FindIndex(r => r[0] == "Data Sheet");
        meta.Rows[i] = ["Data Sheet", "Renamed"];

        ExchangeFile file = fx.Reload(book);
        Assert.Equal(ExchangeFile.DataSheetName, file.Meta.DataSheet);
        Assert.Single(file.Rows);
    }
}
