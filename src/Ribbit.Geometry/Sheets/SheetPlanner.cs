namespace Ribbit.Geometry.Sheets;

public enum SheetAction
{
    Create,
    Update,
    Skip,
    Spacer,
    Error,

    /// <summary>
    /// An existing sheet owned by another user in a workshared model. Distinct
    /// from Skip, which is a choice the user made; this one is imposed, cannot
    /// be fixed from here, and names somebody to go and ask.
    /// </summary>
    Locked,
}

public enum ExistingMode
{
    Update,
    Skip,
}

/// <summary>
/// What one Excel row resolves to. Replaces the Python's string-keyed dict,
/// whose available keys varied by action — <c>mapped_values</c> only existed on
/// create/update, <c>error</c> only on error — so every consumer had to guess
/// the shape with <c>.get()</c>.
/// </summary>
public sealed record RowPlan(
    int RowIndex,
    SheetAction Action,
    string SheetNumber,
    string SheetName,
    IReadOnlyList<(string Param, string Value)> MappedValues,
    string? Error = null,
    string? Titleblock = null,
    /// <summary>
    /// Which titleblock this row named for itself, as an index into the
    /// available types. Null means it named none, and the row falls back to the
    /// form's selection.
    /// </summary>
    int? TitleblockIndex = null,
    /// <summary>Who holds a Locked row, when the model can say.</summary>
    string? Owner = null);

public sealed record SheetMakerReport(
    int Rows, int Create, int Update, int Skip, int Spacer, int Error, bool DryRun,
    int Locked = 0);

/// <summary>
/// Everything that is the same for every row of one run. Collected because
/// PlanRow needs nine pieces of context and a nine-argument call is unreadable
/// at the call site and unsafe to reorder.
/// </summary>
public sealed record RowContext(
    string SheetNoCol,
    string SheetNameCol,
    IReadOnlyList<(string Param, string Column)> MappedPairs,
    // ISet rather than IReadOnlySet: netstandard2.0 for the Revit 2024 leg.
    ISet<string> ExistingNumbers,
    ExistingMode ExistingMode,
    bool BlankAsSpacer,
    string TitleblockColumn = "",
    IReadOnlyList<string>? TitleblockNames = null,
    ISet<string>? LockedNumbers = null,
    /// <summary>Applied to any row that does not name a titleblock of its own.</summary>
    string DefaultTitleblock = "",
    /// <summary>Sheet number -> titleblock, assigned in the window per sheet.</summary>
    IReadOnlyDictionary<string, string>? TitleblockBySheetNumber = null);

/// <summary>
/// Pure planning for SheetMaker. Ported from lib/sheetmakerlib/sheets.py's
/// <c>_plan_row</c> / <c>_build_mapped_pairs</c> and lib/sheetmakerlib/mapping.py.
///
/// Nothing here touches a Document, which is what makes the create/update/skip/
/// spacer/error decision — the part with all the real rules in it — unit
/// testable. The Revit half only carries the decisions out.
/// </summary>
public static class SheetPlanner
{
    private static readonly HashSet<char> ProhibitedChars = [.. "{}[]|;<>?`~"];

    /// <summary>
    /// Columns that are handled explicitly as the sheet number/name and must
    /// never also be written as a mapped parameter.
    /// </summary>
    private static readonly HashSet<string> ExcludedParamNames =
    [
        "sheet number", "sheet no", "number", "sheet name", "name",
    ];

    /// <summary>Returns (isValid, reason). Mirrors the source's character blocklist.</summary>
    public static (bool Ok, string Reason) ValidSheetNumber(string sheetNumber)
    {
        if (string.IsNullOrEmpty(sheetNumber))
            return (false, "Sheet Number is blank");

        List<char> bad = [.. sheetNumber.Where(ProhibitedChars.Contains).Distinct().OrderBy(c => c)];
        return bad.Count > 0
            ? (false, "Sheet Number contains prohibited character(s): " + new string([.. bad]))
            : (true, "");
    }

    /// <summary>Build {Revit Parameter: Excel Column} from the MAPPING sheet rows.</summary>
    public static Dictionary<string, string> BuildMappingDict(
        IEnumerable<XlsxRow> mappingRows,
        string paramCol = "Revit Parameter",
        string excelCol = "Excel Column")
    {
        Dictionary<string, string> mapping = [];
        foreach (XlsxRow row in mappingRows)
        {
            string revitParam = SheetText.S(row.Get(paramCol));
            string excelColumn = SheetText.S(row.Get(excelCol));
            if (revitParam.Length == 0 || excelColumn.Length == 0)
                continue;

            mapping[revitParam] = excelColumn;
        }
        return mapping;
    }

    /// <summary>
    /// The (parameter, column) pairs actually worth writing — blanks dropped,
    /// and the sheet number/name columns excluded since they are set directly.
    /// </summary>
    public static List<(string Param, string Column)> BuildMappedPairs(
        IReadOnlyDictionary<string, string> mapping)
    {
        List<(string, string)> pairs = [];
        foreach (KeyValuePair<string, string> kv in mapping)
        {
            string rp = SheetText.S(kv.Key);
            string ec = SheetText.S(kv.Value);
            if (rp.Length == 0 || ec.Length == 0)
                continue;
            if (ExcludedParamNames.Contains(SheetText.Normalize(rp)))
                continue;

            pairs.Add((rp, ec));
        }
        return pairs;
    }

    // BroadcastIndex (the Python's positional "Nth selected titleblock goes to
    // the Nth row, last one repeating") was DELETED 2026-08-18. It was invisible
    // in the data and silently wrong the moment a row was inserted in Excel;
    // titleblocks are now chosen per sheet in the window, or named by a data
    // column, with one default underneath. See RowContext.

    /// <summary>
    /// Decide what one row means. <paramref name="existingNumbers"/> carries only
    /// the sheet NUMBERS already in the model — the Revit elements themselves stay
    /// on the far side of this boundary, which is what keeps planning pure.
    /// </summary>
    public static RowPlan PlanRow(int rowIndex, XlsxRow row, RowContext ctx)
    {
        string sn = SheetText.S(row.Get(ctx.SheetNoCol));
        string nm = SheetText.S(row.Get(ctx.SheetNameCol));

        if (sn.Length == 0 && ctx.BlankAsSpacer)
            return new RowPlan(rowIndex, SheetAction.Spacer, sn, nm, []);

        (bool ok, string why) = ValidSheetNumber(sn);
        if (!ok)
            return new RowPlan(rowIndex, SheetAction.Error, sn, nm, [], Error: why);

        List<(string, string)> mappedValues = [];
        foreach ((string param, string column) in ctx.MappedPairs)
            mappedValues.Add((param, SheetText.S(row.Get(column))));

        if (ctx.ExistingNumbers.Contains(sn))
        {
            // Ownership outranks the update/skip choice: the user asking for an
            // update does not make somebody else's checkout go away.
            if (ctx.LockedNumbers is not null && ctx.LockedNumbers.Contains(sn))
                return new RowPlan(rowIndex, SheetAction.Locked, sn, nm, []);

            return ctx.ExistingMode == ExistingMode.Skip
                ? new RowPlan(rowIndex, SheetAction.Skip, sn, nm, [])
                : new RowPlan(rowIndex, SheetAction.Update, sn, nm, mappedValues);
        }

        // Only creates place a titleblock, so an unresolvable name matters only
        // here - an update leaves whatever titleblock the sheet already has.
        int? titleblockIndex = null;
        if (ctx.TitleblockNames is { Count: > 0 })
        {
            // Most specific wins: a sheet assigned in the window, else the data
            // column, else the one default. Each level is a deliberate statement
            // about FEWER sheets than the level under it.
            string wanted = "";

            if (ctx.TitleblockBySheetNumber is not null
                && ctx.TitleblockBySheetNumber.TryGetValue(sn, out string? assigned))
            {
                wanted = SheetText.S(assigned);
            }

            if (wanted.Length == 0 && ctx.TitleblockColumn.Length > 0)
                wanted = SheetText.S(row.Get(ctx.TitleblockColumn));

            if (wanted.Length == 0)
                wanted = SheetText.S(ctx.DefaultTitleblock);

            if (wanted.Length > 0)
            {
                titleblockIndex = TitleblockResolver.Resolve(wanted, ctx.TitleblockNames);

                // Named but unmatched is an ERROR, not a quiet fallback. Falling
                // back would put a real titleblock on a real drawing that
                // something explicitly said should be a different one.
                if (titleblockIndex is null)
                {
                    return new RowPlan(rowIndex, SheetAction.Error, sn, nm, [],
                        Error: $"No titleblock type matches '{wanted}'");
                }
            }
        }

        return new RowPlan(
            rowIndex, SheetAction.Create, sn, nm, mappedValues,
            TitleblockIndex: titleblockIndex);
    }

    /// <summary>
    /// The row number as EXCEL shows it, from a zero-based index into the rows
    /// read. The header sits on <paramref name="startRow"/> and data begins
    /// directly below it.
    ///
    /// Worth the helper because a report that numbers its own rows from 1 sends
    /// the reader to the wrong line of the workbook — the one place they go to
    /// fix the error it just told them about.
    /// </summary>
    public static int ExcelRow(int rowIndex, int startRow = 1) => startRow + 1 + rowIndex;

    /// <summary>The sheet-number and sheet-name columns, honouring any override in MAPPING.</summary>
    public static (string NumberCol, string NameCol) KeyColumns(
        IReadOnlyDictionary<string, string> mapping)
    {
        string numberCol = mapping.TryGetValue("Sheet Number", out string? n) ? n : "Sheet Number";
        string nameCol = mapping.TryGetValue("Sheet Name", out string? m) ? m : "Sheet Name";
        return (numberCol, nameCol);
    }

    public static SheetMakerReport Summarize(IReadOnlyList<RowPlan> plans, bool dryRun) => new(
        Rows: plans.Count,
        Create: plans.Count(p => p.Action == SheetAction.Create),
        Update: plans.Count(p => p.Action == SheetAction.Update),
        Skip: plans.Count(p => p.Action == SheetAction.Skip),
        Spacer: plans.Count(p => p.Action == SheetAction.Spacer),
        Error: plans.Count(p => p.Action == SheetAction.Error),
        DryRun: dryRun,
        Locked: plans.Count(p => p.Action == SheetAction.Locked));
}
