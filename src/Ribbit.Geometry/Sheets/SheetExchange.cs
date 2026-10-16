using System.Globalization;

namespace Ribbit.Geometry.Sheets;

/// <summary>How a parameter's value is written as text and read back.</summary>
public enum ValueKind
{
    Text,
    Integer,
    YesNo,

    /// <summary>A measurable value, written with the project's own units.</summary>
    Number,
}

public enum ColumnRole
{
    Number,
    Name,
    Titleblock,
    Parameter,
}

/// <summary>
/// One column of an exported sheet list. <see cref="Header"/> is what the
/// column is called in Excel and the key everything else is matched on;
/// <see cref="Parameter"/> is the Revit parameter behind it (empty for the
/// fixed number/name/titleblock columns).
/// </summary>
public sealed record ExchangeColumn(
    string Header, ColumnRole Role, string Parameter, ValueKind Kind, bool ReadOnly = false)
{
    public static ExchangeColumn SheetNumber { get; } = new("Sheet Number", ColumnRole.Number, "", ValueKind.Text);
    public static ExchangeColumn SheetName { get; } = new("Sheet Name", ColumnRole.Name, "", ValueKind.Text);
    public static ExchangeColumn Titleblock { get; } = new("Titleblock", ColumnRole.Titleblock, "", ValueKind.Text);
}

/// <summary>A parameter chosen for export, as the model describes it.</summary>
public sealed record ExchangeParameter(string Name, ValueKind Kind, bool ReadOnly);

/// <summary>Where and when an exchange file was made.</summary>
public sealed record ExchangeMeta(
    string ModelKey,
    string ModelTitle,
    string ExportedAt,
    string ExportedBy,
    string Revit,
    int Version = ExchangeFile.FormatVersion,
    string DataSheet = ExchangeFile.DataSheetName);

/// <summary>One sheet as exported: its id and its value per column header.</summary>
public sealed record ExchangeSheet(string Id, IReadOnlyDictionary<string, string> Values);

/// <summary>One row of the Sheets tab as read back.</summary>
public sealed record ExchangeRow(int ExcelRow, string Id, XlsxRow Cells)
{
    public string Get(string header) => Cells.Get(header);
}

/// <summary>
/// The sheet exchange workbook: a sheet list EXPORTED FROM REVIT, edited in
/// Excel, and imported back. Replaces SheetMaker's hand-built mapping, because
/// here the columns ARE the parameters - nothing has to be matched up.
///
/// Tabs:
///   Sheets            the list people edit: number, name, titleblock, one
///                     column per chosen parameter, and the Ribbit ID last
///   How to use        plain instructions, for whoever is sent the file
///   _ribbit           model, time, user, format version        (very hidden)
///   _ribbit_columns   each column's parameter, kind, read-only (very hidden)
///   _ribbit_base      every value AS EXPORTED, by Ribbit ID    (very hidden)
///   _ribbit_lists     the titleblock dropdown's source         (very hidden)
///
/// The base copy is what makes the import three-way: a value that differs
/// from the base was changed in Excel; a model value that differs from it was
/// changed in Revit since. Without it, "blank" could not mean "cleared", and a
/// colleague's edit in Revit would be silently overwritten by a stale file.
/// </summary>
public sealed class ExchangeFile
{
    public const int FormatVersion = 1;
    public const string FormatName = "ribbit-sheet-exchange";

    public const string DataSheetName = "Sheets";
    public const string HowToSheet = "How to use";
    public const string MetaSheet = "_ribbit";
    public const string ColumnsSheet = "_ribbit_columns";
    public const string BaseSheet = "_ribbit_base";
    public const string ListsSheet = "_ribbit_lists";

    public const string IdHeader = "Ribbit ID";
    public const string TitleblockListName = "RibbitTitleblocks";

    public ExchangeMeta Meta { get; }

    /// <summary>The columns the file was exported with, in order.</summary>
    public IReadOnlyList<ExchangeColumn> Columns { get; }

    /// <summary>The headers actually on the Sheets tab now, as written.</summary>
    public IReadOnlyList<string> Headers { get; }

    public IReadOnlyList<ExchangeRow> Rows { get; }

    /// <summary>Ribbit ID -> (header -> value as exported).</summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Base { get; }

    private ExchangeFile(
        ExchangeMeta meta,
        IReadOnlyList<ExchangeColumn> columns,
        IReadOnlyList<string> headers,
        IReadOnlyList<ExchangeRow> rows,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> baseValues)
    {
        Meta = meta;
        Columns = columns;
        Headers = headers;
        Rows = rows;
        Base = baseValues;
    }

    /// <summary>
    /// The columns for an export: number and name, the titleblock if wanted,
    /// then the chosen parameters in the order given. A parameter whose name
    /// would collide with a fixed column (or another parameter, ignoring case
    /// and spacing) gets " (parameter)" added so every header stays unique.
    /// </summary>
    public static List<ExchangeColumn> MakeColumns(
        IEnumerable<ExchangeParameter> parameters, bool includeTitleblock)
    {
        List<ExchangeColumn> columns = [ExchangeColumn.SheetNumber, ExchangeColumn.SheetName];
        if (includeTitleblock)
            columns.Add(ExchangeColumn.Titleblock);

        HashSet<string> taken = [.. columns.Select(c => SheetText.Normalize(c.Header)), SheetText.Normalize(IdHeader)];

        foreach (ExchangeParameter p in parameters)
        {
            string name = SheetText.S(p.Name);
            if (name.Length == 0)
                continue;

            string header = name;
            if (taken.Contains(SheetText.Normalize(header)))
                header = name + " (parameter)";
            if (!taken.Add(SheetText.Normalize(header)))
                continue;   // the same parameter chosen twice

            columns.Add(new ExchangeColumn(header, ColumnRole.Parameter, name, p.Kind, p.ReadOnly));
        }

        return columns;
    }

    /// <summary>The workbook for an export, ready to Save.</summary>
    public static XlsxWriter Build(
        ExchangeMeta meta,
        IReadOnlyList<ExchangeColumn> columns,
        IReadOnlyList<ExchangeSheet> sheets,
        IReadOnlyList<string> titleblocks)
    {
        XlsxWriter book = new();

        // --- the list people edit ----------------------------------------------
        XlsxSheet data = book.AddSheet(DataSheetName);
        data.FreezeColumns = 2;
        foreach (ExchangeColumn c in columns)
        {
            data.Columns.Add(new XlsxColumn(c.Header)
            {
                Width = c.Role switch
                {
                    ColumnRole.Number => 14,
                    ColumnRole.Name => 40,
                    ColumnRole.Titleblock => 34,
                    _ => Math.Max(14, Math.Min(40, c.Header.Length + 4)),
                },
                Muted = c.ReadOnly,
                ListName = c.Role == ColumnRole.Titleblock && titleblocks.Count > 0 ? TitleblockListName : null,
            });
        }
        data.Columns.Add(new XlsxColumn(IdHeader) { Width = 40, Muted = true });

        foreach (ExchangeSheet s in sheets)
        {
            List<string> row = [.. columns.Select(c => Value(s, c.Header))];
            row.Add(s.Id);
            data.Rows.Add(row);
        }

        // --- instructions --------------------------------------------------------
        XlsxSheet how = book.AddSheet(HowToSheet);
        foreach (string line in HowToLines(meta))
            how.Rows.Add([line]);

        // --- bookkeeping -----------------------------------------------------------
        XlsxSheet metaSheet = book.AddSheet(MetaSheet);
        metaSheet.Visibility = XlsxVisibility.VeryHidden;
        metaSheet.Columns.Add(new XlsxColumn("Key") { Width = 16 });
        metaSheet.Columns.Add(new XlsxColumn("Value") { Width = 50 });
        foreach ((string key, string value) in MetaPairs(meta))
            metaSheet.Rows.Add([key, value]);

        XlsxSheet colSheet = book.AddSheet(ColumnsSheet);
        colSheet.Visibility = XlsxVisibility.VeryHidden;
        foreach (string h in new[] { "Header", "Role", "Parameter", "Kind", "Read Only" })
            colSheet.Columns.Add(new XlsxColumn(h));
        foreach (ExchangeColumn c in columns)
            colSheet.Rows.Add([c.Header, c.Role.ToString(), c.Parameter, c.Kind.ToString(), c.ReadOnly ? "Yes" : "No"]);

        XlsxSheet baseSheet = book.AddSheet(BaseSheet);
        baseSheet.Visibility = XlsxVisibility.VeryHidden;
        baseSheet.Columns.Add(new XlsxColumn(IdHeader));
        foreach (ExchangeColumn c in columns)
            baseSheet.Columns.Add(new XlsxColumn(c.Header));
        foreach (ExchangeSheet s in sheets)
        {
            List<string> row = [s.Id];
            row.AddRange(columns.Select(c => Value(s, c.Header)));
            baseSheet.Rows.Add(row);
        }

        XlsxSheet lists = book.AddSheet(ListsSheet);
        lists.Visibility = XlsxVisibility.VeryHidden;
        lists.Columns.Add(new XlsxColumn("Titleblock") { Width = 40 });
        foreach (string t in titleblocks)
            lists.Rows.Add([t]);
        if (titleblocks.Count > 0)
            book.DefineName(TitleblockListName, lists, 1, 2, titleblocks.Count + 1);

        return book;
    }

    private static string Value(ExchangeSheet s, string header)
        => s.Values.TryGetValue(header, out string? v) ? v ?? "" : "";

    private static IEnumerable<(string, string)> MetaPairs(ExchangeMeta m)
    {
        yield return ("Format", FormatName);
        yield return ("Version", m.Version.ToString(CultureInfo.InvariantCulture));
        yield return ("Data Sheet", m.DataSheet);
        yield return ("Model", m.ModelTitle);
        yield return ("Model Key", m.ModelKey);
        yield return ("Exported", m.ExportedAt);
        yield return ("Exported By", m.ExportedBy);
        yield return ("Revit", m.Revit);
    }

    private static IEnumerable<string> HowToLines(ExchangeMeta m)
    {
        yield return "Ribbit sheet list";
        yield return $"Exported from '{m.ModelTitle}' on {m.ExportedAt} by {m.ExportedBy}.";
        yield return "";
        yield return "Edit the Sheets tab, save, then in Revit: Ribbit > Sheets > Sheet Maker and pick this file.";
        yield return "Revit shows every change before anything is written, and writes nothing until you press Apply.";
        yield return "";
        yield return "- Change any white cell. Grey columns are read-only in Revit and are ignored.";
        yield return "- Emptying a cell clears that value in Revit.";
        yield return "- Change a Sheet Number to renumber the sheet. Swapping two numbers is fine.";
        yield return "- Add a row (leave its Ribbit ID empty) to create a new sheet. Blank cells on a new row keep Revit's default.";
        yield return "- Pick the Titleblock from the dropdown.";
        yield return "- Deleting a row never deletes a sheet - that sheet is just left alone.";
        yield return "- Do not edit the Ribbit ID column: it is how each row finds its sheet, even after it is renumbered.";
        yield return "  If you copy a row to make a new sheet, clear the Ribbit ID on the copy (Ribbit also does this for you when it can tell).";
        yield return "- If someone changes the same value in Revit after this export, Revit keeps theirs unless you choose otherwise when importing.";
    }

    /// <summary>Does this workbook carry the exchange bookkeeping?</summary>
    public static bool IsExchange(XlsxWorkbook book)
        => book.SheetNames.Any(n => string.Equals(n, MetaSheet, StringComparison.OrdinalIgnoreCase));

    /// <summary>Silent: false for anything that cannot be opened.</summary>
    public static bool IsExchangeFile(string? path)
    {
        List<string> names = XlsxWorkbook.TryListSheetNames(path);
        return names.Any(n => string.Equals(n, MetaSheet, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Read an exchange workbook back. Throws <see cref="SheetMakerError"/> with a reason.</summary>
    public static ExchangeFile Read(XlsxWorkbook book)
    {
        if (!IsExchange(book))
            throw new SheetMakerError("This workbook was not exported by Ribbit (it has no _ribbit tab).");

        Dictionary<string, string> pairs = new(StringComparer.OrdinalIgnoreCase);
        foreach (XlsxRow r in book.ReadWorksheet(MetaSheet))
            pairs[SheetText.S(r.Get("Key"))] = SheetText.S(r.Get("Value"));

        string Pair(string key) => pairs.TryGetValue(key, out string? v) ? v : "";

        if (!string.Equals(Pair("Format"), FormatName, StringComparison.Ordinal))
            throw new SheetMakerError("The _ribbit tab is not a Ribbit sheet list - was it edited?");

        int version = int.TryParse(Pair("Version"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : 0;
        if (version < 1)
            throw new SheetMakerError("The _ribbit tab has no format version - was it edited?");
        if (version > FormatVersion)
        {
            throw new SheetMakerError(
                $"This sheet list was made by a newer Ribbit (format {version}; this one reads up to {FormatVersion}). "
                + "Update Ribbit, or export the list again from this version.");
        }

        List<ExchangeColumn> columns = [];
        foreach (XlsxRow r in book.ReadWorksheet(ColumnsSheet))
        {
            string header = SheetText.S(r.Get("Header"));
            if (header.Length == 0)
                continue;
            if (!Enum.TryParse(SheetText.S(r.Get("Role")), out ColumnRole role)
                || !Enum.TryParse(SheetText.S(r.Get("Kind")), out ValueKind kind))
            {
                throw new SheetMakerError($"The _ribbit_columns tab has an unreadable entry for '{header}'.");
            }
            columns.Add(new ExchangeColumn(header, role, SheetText.S(r.Get("Parameter")), kind,
                string.Equals(SheetText.S(r.Get("Read Only")), "Yes", StringComparison.OrdinalIgnoreCase)));
        }

        Dictionary<string, IReadOnlyDictionary<string, string>> baseValues = new(StringComparer.OrdinalIgnoreCase);
        foreach (XlsxRow r in book.ReadWorksheet(BaseSheet))
        {
            string id = SheetText.S(r.Get(IdHeader));
            if (id.Length == 0)
                continue;
            Dictionary<string, string> values = new(StringComparer.OrdinalIgnoreCase);
            foreach (ExchangeColumn c in columns)
                values[c.Header] = r.Get(c.Header);
            baseValues[id] = values;
        }

        // The tab may have been renamed; the one the export named wins, then any
        // tab that is not ours.
        string dataName = Pair("Data Sheet");
        if (!book.SheetNames.Contains(dataName))
        {
            dataName = book.SheetNames.FirstOrDefault(n =>
                    !n.StartsWith("_ribbit", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(n, HowToSheet, StringComparison.OrdinalIgnoreCase))
                ?? throw new SheetMakerError("The workbook has no Sheets tab left to read.");
        }

        (List<string> headers, List<XlsxRow> rows) = book.ReadTable(dataName, stopAtBlankRow: false);

        List<ExchangeRow> exchangeRows =
        [
            .. rows.Select(r => new ExchangeRow(r.RowNumber, SheetText.S(r.Get(IdHeader)), r))
        ];

        ExchangeMeta meta = new(
            Pair("Model Key"), Pair("Model"), Pair("Exported"), Pair("Exported By"), Pair("Revit"),
            version, dataName);

        return new ExchangeFile(meta, columns, headers, exchangeRows, baseValues);
    }
}
