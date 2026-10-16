using System.Globalization;
using Autodesk.Revit.DB;
using Ribbit.Geometry.Sheets;

namespace Ribbit.Native.Revit;

/// <summary>What one import row actually did once Revit had its say.</summary>
internal sealed record AppliedRow(
    ImportRow Row,
    ViewSheet? Sheet,
    /// <summary>Values Revit refused, one line each; the rest of the row still went in.</summary>
    IReadOnlyList<string> Refused,
    /// <summary>The row failed outright (nothing of it was written), or null.</summary>
    string? Failure);

internal sealed record ApplyOutcome(IReadOnlyList<AppliedRow> Rows)
{
    public int Created => Rows.Count(r => r.Failure is null && r.Row.Action == ImportAction.Create);
    public int Updated => Rows.Count(r => r.Failure is null && r.Row.Action == ImportAction.Update);
    public int Failed => Rows.Count(r => r.Failure is not null);
    public int Refused => Rows.Sum(r => r.Refused.Count);
}

/// <summary>
/// The Revit half of the sheet exchange: read the model into an export, read
/// it again as "now" for the import, and carry out an <see cref="ImportPlan"/>.
/// Every decision is made in <see cref="SheetImportPlanner"/>; this only reads
/// and writes.
///
/// Values travel as TEXT in one agreed form per kind (<see cref="Text"/>), and
/// the same function reads the model at export and at import - so an
/// untouched value can never look changed just because it was read twice.
/// </summary>
internal static class SheetExchangeEngine
{
    /// <summary>
    /// Which model a file came from. The Project Information element's id
    /// survives detaching, Save As and copies of the same project, which is
    /// the "same model" the import cares about.
    /// </summary>
    public static string ModelKey(Document doc)
    {
        try
        {
            return doc.ProjectInformation?.UniqueId ?? "";
        }
        catch (Autodesk.Revit.Exceptions.ApplicationException)
        {
            return "";
        }
    }

    /// <summary>Every sheet, in drawing-list order (natural sort on the number).</summary>
    public static List<ViewSheet> AllSheets(Document doc)
    {
        List<ViewSheet> sheets = [.. new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>()];
        sheets.Sort((a, b) => NaturalSort.Comparer.Compare(a.SheetNumber, b.SheetNumber));
        return sheets;
    }

    /// <summary>The exchange columns for the parameters chosen, keys and uncarriable kinds left out.</summary>
    public static List<ExchangeColumn> Columns(IEnumerable<SheetParameterInfo> chosen, bool includeTitleblock)
        => ExchangeFile.MakeColumns(
            chosen.Where(p => p.Kind is not null && !p.IsKey)
                  .Select(p => new ExchangeParameter(p.Name, p.Kind!.Value, p.ReadOnly)),
            includeTitleblock);

    /// <summary>Titleblock names in the SAME order as the symbols - the planner's indices point into both.</summary>
    public static List<string> TitleblockNames(IReadOnlyList<FamilySymbol> symbols)
        => [.. symbols.Select(SheetMakerEngine.TitleblockDisplayName)];

    /// <summary>A parameter's value in the exchange's text form. Blank when it has none.</summary>
    public static string Text(Parameter? p, ValueKind kind)
    {
        if (p is null)
            return "";

        try
        {
            switch (kind)
            {
                case ValueKind.Text:
                    return p.AsString() ?? "";
                case ValueKind.Integer:
                    return p.HasValue ? p.AsInteger().ToString(CultureInfo.InvariantCulture) : "";
                case ValueKind.YesNo:
                    return p.HasValue ? (p.AsInteger() == 1 ? "Yes" : "No") : "";
                case ValueKind.Number:
                    return p.HasValue ? p.AsValueString() ?? "" : "";
            }
        }
        catch (Autodesk.Revit.Exceptions.ApplicationException)
        {
            // an unreadable value exports as blank rather than failing the list
        }
        return "";
    }

    /// <summary>
    /// The titleblock placed on a sheet, if any. The first, if someone placed two.
    ///
    /// NOT a view-scoped collector (<c>new FilteredElementCollector(doc, sheet.Id)</c>):
    /// that makes Revit GENERATE GRAPHICS for the sheet to decide what is in it -
    /// the journal showed ~0.6 s and 2,000+ elements per sheet on testproject,
    /// which made a whole-model export crawl. A titleblock is owned by its sheet,
    /// so the owner-view filter answers without drawing anything.
    /// </summary>
    public static FamilyInstance? TitleblockOn(Document doc, ViewSheet sheet)
        => new FilteredElementCollector(doc)
            .OfCategory(BuiltInCategory.OST_TitleBlocks)
            .WhereElementIsNotElementType()
            .WherePasses(new ElementOwnerViewFilter(sheet.Id))
            .OfType<FamilyInstance>()
            .FirstOrDefault();

    /// <summary>
    /// Every sheet's titleblock from ONE pass over the model, keyed by sheet -
    /// for reading many sheets at once. The first per sheet, like <see cref="TitleblockOn"/>.
    /// </summary>
    public static Dictionary<ElementId, FamilyInstance> TitleblocksBySheet(Document doc)
    {
        Dictionary<ElementId, FamilyInstance> map = [];
        foreach (FamilyInstance fi in new FilteredElementCollector(doc)
                     .OfCategory(BuiltInCategory.OST_TitleBlocks)
                     .WhereElementIsNotElementType()
                     .OfType<FamilyInstance>()
                     .OrderBy(f => f.Id.Value))
        {
            if (fi.OwnerViewId != ElementId.InvalidElementId && !map.ContainsKey(fi.OwnerViewId))
                map[fi.OwnerViewId] = fi;
        }
        return map;
    }

    /// <summary>
    /// The parameter a column reads and writes: the SHEET's first, then its
    /// titleblock's - the same order <see cref="SheetMakerEngine"/> has always
    /// written in.
    /// </summary>
    private static Parameter? Find(ViewSheet sheet, Lazy<FamilyInstance?> titleblock, string name)
        => sheet.LookupParameter(name) ?? titleblock.Value?.LookupParameter(name);

    /// <summary>Every column's value for one sheet, keyed by header.</summary>
    public static Dictionary<string, string> Read(
        Document doc, ViewSheet sheet, IReadOnlyList<ExchangeColumn> columns,
        IReadOnlyDictionary<ElementId, FamilyInstance>? titleblocks = null)
    {
        Lazy<FamilyInstance?> tb = new(() => titleblocks is null
            ? TitleblockOn(doc, sheet)
            : titleblocks.TryGetValue(sheet.Id, out FamilyInstance? fi) ? fi : null);
        Dictionary<string, string> values = new(StringComparer.OrdinalIgnoreCase);

        foreach (ExchangeColumn c in columns)
        {
            values[c.Header] = c.Role switch
            {
                ColumnRole.Number => sheet.SheetNumber ?? "",
                ColumnRole.Name => sheet.Name ?? "",
                ColumnRole.Titleblock => tb.Value is { } fi
                    ? SheetMakerEngine.TitleblockDisplayName(fi.Symbol)
                    : "",
                _ => Text(Find(sheet, tb, c.Parameter), c.Kind),
            };
        }

        return values;
    }

    /// <summary>The workbook for these sheets, ready to save.</summary>
    public static XlsxWriter BuildExport(
        Document doc,
        IReadOnlyList<ViewSheet> sheets,
        IReadOnlyList<ExchangeColumn> columns,
        IReadOnlyList<string> titleblockNames,
        string revitVersion,
        string user)
    {
        Dictionary<ElementId, FamilyInstance> tbs = TitleblocksBySheet(doc);
        List<ExchangeSheet> rows = [.. sheets.Select(s => new ExchangeSheet(s.UniqueId, Read(doc, s, columns, tbs)))];

        ExchangeMeta meta = new(
            ModelKey(doc),
            doc.Title,
            DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
            user,
            revitVersion);

        return ExchangeFile.Build(meta, columns, rows, titleblockNames);
    }

    /// <summary>The model as the planner needs it: every sheet, with the file's columns read NOW.</summary>
    public static List<ModelSheet> Snapshot(Document doc, IReadOnlyList<ExchangeColumn> columns)
    {
        Dictionary<string, string> locked = SheetMakerEngine.LockedSheetOwners(doc);
        Dictionary<ElementId, FamilyInstance> tbs = TitleblocksBySheet(doc);
        List<ModelSheet> model = [];

        foreach (ViewSheet s in AllSheets(doc))
        {
            Dictionary<string, string> values = Read(doc, s, columns, tbs);
            string number = s.SheetNumber ?? "";
            bool isLocked = locked.TryGetValue(number, out string? owner);

            model.Add(new ModelSheet(
                s.UniqueId,
                number,
                s.Name ?? "",
                values.TryGetValue(ExchangeColumn.Titleblock.Header, out string? tb)
                    ? tb
                    : tbs.TryGetValue(s.Id, out FamilyInstance? fi) ? SheetMakerEngine.TitleblockDisplayName(fi.Symbol) : "",
                values,
                isLocked,
                owner ?? ""));
        }

        return model;
    }

    // ---------------------------------------------------------------- apply

    /// <summary>
    /// Carry out a plan. The caller owns the transaction. A row Revit refuses
    /// outright is reported and the rest go on - one bad drawing should not
    /// discard a correct batch - but anything thrown past this is the caller's
    /// cue to roll the whole lot back.
    /// </summary>
    public static ApplyOutcome Apply(
        Document doc,
        ImportPlan plan,
        IReadOnlyList<FamilySymbol> titleblocks,
        Action<int, int>? progress = null)
    {
        List<ImportRow> updates = [.. plan.Rows.Where(r => r.Action == ImportAction.Update)];
        List<ImportRow> creates = [.. plan.Rows.Where(r => r.Action == ImportAction.Create)];
        int total = updates.Count + creates.Count, done = 0;

        Dictionary<ImportRow, ViewSheet?> sheetOf = [];
        foreach (ImportRow r in updates)
            sheetOf[r] = doc.GetElement(r.SheetId) as ViewSheet;

        Dictionary<ImportRow, string> failed = [];
        Dictionary<ImportRow, List<string>> refused = [];
        foreach (ImportRow r in plan.Rows)
            refused[r] = [];

        foreach (ImportRow r in updates.Where(r => sheetOf[r] is null))
            failed[r] = "The sheet is no longer in the model";

        // --- 1. renumbering, in two passes -------------------------------------
        // Every moving sheet first steps onto a number nobody uses, THEN onto
        // its new one - so swaps and chains (A->B, B->C) never collide midway.
        List<(ImportRow Row, ViewSheet Sheet, string From, string To)> moves = [];
        foreach (ImportRow r in updates.Where(r => !failed.ContainsKey(r)))
        {
            CellChange? n = r.Changes.FirstOrDefault(c => c.Column.Role == ColumnRole.Number);
            if (n is not null)
                moves.Add((r, sheetOf[r]!, sheetOf[r]!.SheetNumber, n.To));
        }

        foreach ((ImportRow row, ViewSheet sheet, string _, string _) in moves)
        {
            try
            {
                // Plain letters and digits: Revit refuses {}[]|;<>?`~ in a sheet
                // number, and a first version of this used "~" and failed every
                // renumber (caught by the in-Revit suite).
                sheet.SheetNumber = "RIBBIT-MOVING-" + Guid.NewGuid().ToString("N").Substring(0, 10);
            }
            catch (Exception ex)
            {
                failed[row] = "Revit refused to renumber it: " + ex.Message;
            }
        }

        foreach ((ImportRow row, ViewSheet sheet, string from, string to) in moves)
        {
            if (failed.ContainsKey(row))
                continue;
            try
            {
                sheet.SheetNumber = to;
            }
            catch (Exception ex)
            {
                string back;
                try
                {
                    sheet.SheetNumber = from;
                    back = $"it keeps {from}";
                }
                catch (Exception)
                {
                    back = $"it is left numbered {sheet.SheetNumber} - renumber it by hand";
                }
                failed[row] = $"Revit refused sheet number {to} ({ex.Message}); {back}";
            }
        }

        // --- 2. everything else on existing sheets ---------------------------------
        foreach (ImportRow r in updates)
        {
            progress?.Invoke(done++, total);
            if (failed.ContainsKey(r))
                continue;

            ViewSheet sheet = sheetOf[r]!;
            Lazy<FamilyInstance?> tb = new(() => TitleblockOn(doc, sheet));
            WriteValues(doc, sheet, tb, r, titleblocks, refused[r]);
        }

        // --- 3. new sheets ----------------------------------------------------------
        Dictionary<ImportRow, ViewSheet> created = [];
        foreach (ImportRow r in creates)
        {
            progress?.Invoke(done++, total);
            ViewSheet? sheet = null;
            try
            {
                FamilySymbol? symbol = r.TitleblockIndex is int i && i < titleblocks.Count ? titleblocks[i] : null;
                if (symbol is null)
                {
                    sheet = ViewSheet.CreatePlaceholder(doc);
                }
                else
                {
                    if (!symbol.IsActive)
                        symbol.Activate();
                    sheet = ViewSheet.Create(doc, symbol.Id);
                }

                sheet.SheetNumber = r.Number;
            }
            catch (Exception ex)
            {
                // A sheet Revit made but would not number is not the sheet the
                // row asked for - left in, it would be a stray with Revit's own
                // number that nobody requested.
                if (sheet is not null)
                {
                    try { doc.Delete(sheet.Id); } catch (Exception) { /* reported below either way */ }
                }
                failed[r] = "Revit refused to create it: " + ex.Message;
                continue;
            }

            created[r] = sheet;
            ViewSheet made = sheet;
            Lazy<FamilyInstance?> tb = new(() => TitleblockOn(doc, made));
            WriteValues(doc, made, tb, r, titleblocks, refused[r]);
        }

        progress?.Invoke(total, total);

        List<AppliedRow> rows = [];
        foreach (ImportRow r in plan.Rows)
        {
            ViewSheet? sheet = r.Action == ImportAction.Create
                ? (created.TryGetValue(r, out ViewSheet? c) ? c : null)
                : r.SheetId.Length > 0 ? doc.GetElement(r.SheetId) as ViewSheet : null;
            rows.Add(new AppliedRow(r, sheet, refused[r], failed.TryGetValue(r, out string? f) ? f : null));
        }

        return new ApplyOutcome(rows);
    }

    /// <summary>Name, titleblock type and parameters for one row. Numbers are done before this.</summary>
    private static void WriteValues(
        Document doc,
        ViewSheet sheet,
        Lazy<FamilyInstance?> tb,
        ImportRow row,
        IReadOnlyList<FamilySymbol> titleblocks,
        List<string> refused)
    {
        foreach (CellChange c in row.Changes)
        {
            try
            {
                switch (c.Column.Role)
                {
                    case ColumnRole.Number:
                        break;   // done already

                    case ColumnRole.Name:
                        if (sheet.Name != c.To)
                            sheet.Name = c.To;
                        break;

                    case ColumnRole.Titleblock:
                        if (row.Action == ImportAction.Create)
                            break;   // created with it
                        string? why = ChangeTitleblock(tb.Value, row.TitleblockIndex, titleblocks);
                        if (why is not null)
                            refused.Add($"Titleblock: {why}");
                        break;

                    default:
                        string? problem = WriteParameter(sheet, tb, c.Column, c.To);
                        if (problem is not null)
                            refused.Add($"{c.Column.Header}: {problem}");
                        break;
                }
            }
            catch (Exception ex)
            {
                refused.Add($"{c.Column.Header}: Revit refused '{c.To}' ({ex.Message})");
            }
        }
    }

    private static string? ChangeTitleblock(FamilyInstance? placed, int? index, IReadOnlyList<FamilySymbol> titleblocks)
    {
        if (placed is null)
            return "the sheet has no titleblock to change";
        if (index is not int i || i >= titleblocks.Count)
            return "no such titleblock type";

        FamilySymbol symbol = titleblocks[i];
        if (placed.GetTypeId() == symbol.Id)
            return null;
        if (!symbol.IsActive)
            symbol.Activate();
        placed.ChangeTypeId(symbol.Id);
        return null;
    }

    /// <summary>
    /// Writes one value to the sheet, or failing that its titleblock. Only when
    /// it differs, so an import that changes nothing leaves nothing dirty.
    /// Returns why it could not be written, or null.
    /// </summary>
    private static string? WriteParameter(ViewSheet sheet, Lazy<FamilyInstance?> tb, ExchangeColumn column, string value)
    {
        Parameter? p = Writable(sheet.LookupParameter(column.Parameter))
                       ?? Writable(tb.Value?.LookupParameter(column.Parameter));
        if (p is null)
            return "no writable parameter of that name on the sheet or its titleblock";

        if (SheetImportPlanner.Same(column.Kind, Text(p, column.Kind), value))
            return null;

        switch (column.Kind)
        {
            case ValueKind.Text:
                return p.Set(value) ? null : $"Revit refused '{value}'";

            case ValueKind.Integer:
                return SheetImportPlanner.TryParseInteger(value, out int n) && p.Set(n)
                    ? null : $"Revit refused '{value}'";

            case ValueKind.YesNo:
                return SheetImportPlanner.TryParseYesNo(value, out bool yes) && p.Set(yes ? 1 : 0)
                    ? null : $"Revit refused '{value}'";

            case ValueKind.Number:
                // In the project's units, the way it was exported - "1200 mm",
                // or a bare "1200" meaning the project's length unit.
                return p.SetValueString(value)
                    ? null
                    : $"'{value}' is not a value Revit can read in this project's units";
        }

        return "unsupported kind";
    }

    private static Parameter? Writable(Parameter? p) => p is { IsReadOnly: false } ? p : null;
}
