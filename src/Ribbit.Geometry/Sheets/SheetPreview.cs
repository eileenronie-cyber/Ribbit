namespace Ribbit.Geometry.Sheets;

/// <summary>
/// What the current form settings would do, worked out without touching the
/// document — the counts, the columns the key fields resolved to, and the silent
/// mismatches that otherwise only show up as a run that did nothing.
/// </summary>
public sealed record SheetPreview(
    SheetMakerReport Counts,
    string NumberColumn,
    string NameColumn,
    bool NumberColumnPresent,
    bool NameColumnPresent,
    int MappedParameters,
    IReadOnlyList<string> MissingColumns,
    IReadOnlyList<string> MissingParameters,
    IReadOnlyList<int> ErrorRows,
    bool TitleblockColumnPresent,
    IReadOnlyList<string> LockedSheets);

/// <summary>
/// Plans every row the way <c>SheetMakerEngine.Run</c> will, so the form can
/// show the outcome before anything is committed.
///
/// This exists because SheetMaker's failures are quiet ones. The mapping decides
/// which data columns are read AND which Revit parameters are written, and a
/// mistake on either side throws nothing:
///   - point the sheet number at a column the data tab does not have and no row
///     yields a number at all;
///   - name a Revit parameter that neither the sheet nor its titleblock carries
///     and the write is skipped silently, every row, for ever.
/// Both used to be discoverable only by running the tool and noticing the model
/// had not changed.
/// </summary>
public static class SheetPreviewer
{
    /// <param name="knownSheetParameters">
    /// Parameter names a sheet or its titleblock actually has. Null means "not
    /// known" — the check is skipped rather than guessed at, so a caller that
    /// cannot answer never produces a false accusation.
    ///
    /// Matched case-insensitively HERE rather than relying on the set's own
    /// comparer: Revit's own <c>LookupParameter</c> ignores case, and a caller
    /// passing an ordinary <c>HashSet</c> would otherwise have every
    /// differently-cased parameter reported as missing.
    /// </param>
    public static SheetPreview Build(
        IReadOnlyList<XlsxRow> dataRows,
        IReadOnlyDictionary<string, string> mapping,
        ISet<string> existingNumbers,
        ExistingMode existingMode,
        bool blankAsSpacer,
        ISet<string>? knownSheetParameters = null,
        string titleblockColumn = "",
        IReadOnlyList<string>? titleblockNames = null,
        ISet<string>? lockedNumbers = null,
        string defaultTitleblock = "",
        IReadOnlyDictionary<string, string>? titleblockBySheetNumber = null)
    {
        (string numberCol, string nameCol) = SheetPlanner.KeyColumns(mapping);
        List<(string Param, string Column)> pairs = SheetPlanner.BuildMappedPairs(mapping);

        HashSet<string>? known = knownSheetParameters is null
            ? null
            : new HashSet<string>(knownSheetParameters, StringComparer.OrdinalIgnoreCase);

        // A copy, because planning a create claims that sheet number for the
        // rows after it — exactly as the engine does — and a preview must not
        // leave that mark on the caller's set.
        HashSet<string> claimed = [.. existingNumbers];

        RowContext ctx = new(
            numberCol, nameCol, pairs, claimed, existingMode, blankAsSpacer,
            titleblockColumn, titleblockNames, lockedNumbers,
            defaultTitleblock, titleblockBySheetNumber);

        List<RowPlan> plans = [];
        List<int> errorRows = [];
        List<string> locked = [];

        for (int i = 0; i < dataRows.Count; i++)
        {
            RowPlan plan = SheetPlanner.PlanRow(i, dataRows[i], ctx);

            if (plan.Action == SheetAction.Create)
                claimed.Add(plan.SheetNumber);
            else if (plan.Action == SheetAction.Error)
                errorRows.Add(SheetPlanner.ExcelRow(i));
            else if (plan.Action == SheetAction.Locked && !locked.Contains(plan.SheetNumber))
                locked.Add(plan.SheetNumber);

            plans.Add(plan);
        }

        // Every row carries the full header set, so the first one answers for
        // the worksheet. With no rows there are no headers to check against, and
        // "nothing to do" is the only message worth showing anyway.
        HashSet<string> headers = dataRows.Count > 0
            ? [.. dataRows[0].Columns]
            : [];

        bool Present(string column) =>
            dataRows.Count == 0 || headers.Contains(SheetText.Normalize(column));

        List<string> missingColumns = [];
        List<string> missingParameters = [];

        foreach ((string param, string column) in pairs)
        {
            if (!Present(column) && !missingColumns.Contains(column))
                missingColumns.Add(column);

            if (known is not null
                && !known.Contains(param)
                && !missingParameters.Contains(param))
            {
                missingParameters.Add(param);
            }
        }

        return new SheetPreview(
            SheetPlanner.Summarize(plans, dryRun: true),
            numberCol,
            nameCol,
            Present(numberCol),
            Present(nameCol),
            pairs.Count,
            missingColumns,
            missingParameters,
            errorRows,
            titleblockColumn.Length == 0 || Present(titleblockColumn),
            locked);
    }
}
