using System.Globalization;

namespace Ribbit.Geometry.Sheets;

/// <summary>
/// One sheet as the model holds it NOW. <see cref="Values"/> is keyed by the
/// exchange column header and holds the current value of every parameter
/// column, as text in the same form the export writes.
/// </summary>
public sealed record ModelSheet(
    string Id,
    string Number,
    string Name,
    string Titleblock,
    IReadOnlyDictionary<string, string> Values,
    bool Locked = false,
    string LockedBy = "");

public sealed record ImportOptions(
    /// <summary>For a new row that names no titleblock. Empty: a placeholder sheet.</summary>
    string DefaultTitleblock = "",
    /// <summary>
    /// When a value was changed in Revit AND in Excel since the export, write
    /// Excel's. Off (the default), Revit's is kept and the clash is listed.
    /// </summary>
    bool ExcelWinsConflicts = false);

public enum ImportAction
{
    Create,
    Update,
    Unchanged,

    /// <summary>Left alone on purpose, with a reason.</summary>
    Skip,

    /// <summary>Would change, but the sheet is checked out by someone else.</summary>
    Locked,

    Error,
}

/// <summary>One value to write. <see cref="From"/> is the model's value now.</summary>
public sealed record CellChange(ExchangeColumn Column, string From, string To, bool BothChanged = false);

/// <summary>A value that will NOT be written, and why.</summary>
public sealed record CellProblem(ExchangeColumn Column, string Value, string Reason);

public sealed record ImportRow(
    int ExcelRow,
    ImportAction Action,
    /// <summary>The sheet this row writes to; empty for a create or an unmatched row.</summary>
    string SheetId,
    /// <summary>The sheet number the row ends up with.</summary>
    string Number,
    string Name,
    IReadOnlyList<CellChange> Changes,
    /// <summary>Changed in Revit and in Excel; Revit's value is being kept.</summary>
    IReadOnlyList<CellChange> Conflicts,
    IReadOnlyList<CellProblem> Problems,
    IReadOnlyList<string> Notes,
    /// <summary>Why an Error/Skip/Locked row does nothing.</summary>
    string? Reason = null,
    /// <summary>Titleblock to create with, or to change to; index into the model's titleblock names.</summary>
    int? TitleblockIndex = null);

public sealed record ImportCounts(
    int Rows, int Create, int Update, int Unchanged, int Skip, int Locked, int Error,
    int Values, int Conflicts, int Problems)
{
    public int Actionable => Create + Update;
}

public sealed record ImportPlan(
    IReadOnlyList<ImportRow> Rows,
    IReadOnlyList<string> FileNotes,
    bool SameModel,
    ImportCounts Counts);

/// <summary>
/// Decides what importing an exchange workbook will do, without touching a
/// document - so every rule below is unit tested.
///
/// A row FINDS ITS SHEET by the Ribbit ID the export wrote, not by its number,
/// which is what lets a number be changed. A row without one (added in Excel)
/// is matched by number instead, and creates a sheet when nothing has it.
///
/// Each value is judged THREE-WAY against the copy taken at export:
///   Excel = export             untouched in Excel: nothing to do
///   Revit = export             changed in Excel only: write it
///   Excel = Revit              both already agree: nothing to do
///   otherwise                  changed on both sides: a CONFLICT, and Revit's
///                              value is kept unless the user says otherwise
/// Because every exported cell started filled in, an emptied cell means
/// "clear this". A row matched by number has no export copy, so there a blank
/// cell simply means "leave it".
///
/// Never deletes a sheet: a row missing from the file leaves its sheet alone.
/// </summary>
public static class SheetImportPlanner
{
    private sealed class Draft
    {
        public ExchangeRow Row = null!;
        public int ExcelRow;
        public string Id = "";
        public ModelSheet? Target;
        public bool Keyed;
        public ImportAction Action = ImportAction.Unchanged;
        public string Number = "";
        public string Name = "";
        public string? Reason;
        public int? TitleblockIndex;
        public List<CellChange> Changes = [];
        public List<CellChange> Conflicts = [];
        public List<CellProblem> Problems = [];
        public List<string> Notes = [];

        public bool Settled => Action is ImportAction.Error or ImportAction.Skip;

        public void Fail(string reason)
        {
            Action = ImportAction.Error;
            Reason = reason;
            Changes.Clear();
            Conflicts.Clear();
            TitleblockIndex = null;
        }

        public string NewNumber()
            => Changes.FirstOrDefault(c => c.Column.Role == ColumnRole.Number)?.To ?? Number;
    }

    public static ImportPlan Plan(
        ExchangeFile file,
        string modelKey,
        IReadOnlyList<ModelSheet> model,
        IReadOnlyList<string> titleblockNames,
        ImportOptions? options = null)
    {
        options ??= new ImportOptions();
        List<string> fileNotes = [];

        Dictionary<string, ModelSheet> byId = new(StringComparer.OrdinalIgnoreCase);
        foreach (ModelSheet s in model)
            byId[s.Id] = s;

        // The same model by its key - or by its sheets: a Ribbit ID is a sheet's
        // own unique id, so a file whose ids are found here was exported from
        // these very sheets, whatever the key says.
        bool sameModel = (file.Meta.ModelKey.Length > 0
                          && string.Equals(file.Meta.ModelKey, modelKey, StringComparison.OrdinalIgnoreCase))
                         || file.Rows.Any(r => r.Id.Length > 0 && byId.ContainsKey(r.Id));
        if (!sameModel)
        {
            fileNotes.Add(
                $"This list was exported from '{file.Meta.ModelTitle}', not this model. Rows are matched "
                + "by sheet number, and blank cells leave values as they are.");
        }

        // --- which exported columns are still on the tab ----------------------------
        HashSet<string> onTab = [.. file.Headers.Select(SheetText.Normalize)];
        List<ExchangeColumn> columns = [];
        foreach (ExchangeColumn c in file.Columns)
        {
            if (onTab.Contains(SheetText.Normalize(c.Header)))
                columns.Add(c);
            else if (c.Role != ColumnRole.Number)
                fileNotes.Add($"Column '{c.Header}' is no longer on the {file.Meta.DataSheet} tab - its values are left as they are.");
        }

        HashSet<string> known = [.. file.Columns.Select(c => SheetText.Normalize(c.Header)), SheetText.Normalize(ExchangeFile.IdHeader)];
        foreach (string h in file.Headers)
        {
            if (!known.Contains(SheetText.Normalize(h)))
                fileNotes.Add($"Column '{h}' was not in the export and is ignored. To bring in another parameter, export the list again with it ticked.");
        }

        if (!onTab.Contains(SheetText.Normalize(ExchangeFile.IdHeader)))
        {
            fileNotes.Add($"The {ExchangeFile.IdHeader} column is gone, so every row is matched by sheet number.");
        }

        ExchangeColumn? numberCol = columns.FirstOrDefault(c => c.Role == ColumnRole.Number);
        List<Draft> drafts = [.. file.Rows.Select(r => new Draft
        {
            Row = r,
            ExcelRow = r.ExcelRow,
            Id = r.Id,
            Number = SheetText.S(numberCol is null ? "" : r.Get(numberCol.Header)),
            Name = SheetText.S(r.Get(ExchangeColumn.SheetName.Header)),
        })];

        if (numberCol is null)
        {
            fileNotes.Add($"The Sheet Number column is gone from the {file.Meta.DataSheet} tab, so nothing can be imported.");
            foreach (Draft d in drafts)
                d.Fail("No Sheet Number column");
            return Finish(drafts, fileNotes, sameModel);
        }

        // --- 1. rows holding nothing but an id: a deliberate "leave it" ----------------
        foreach (Draft d in drafts)
        {
            bool empty = columns.All(c => SheetText.S(d.Row.Get(c.Header)).Length == 0);
            if (empty)
            {
                d.Action = ImportAction.Skip;
                d.Reason = d.Id.Length > 0
                    ? "Row is empty apart from its Ribbit ID - its sheet is left alone"
                    : "Row has nothing in the exported columns";
            }
        }

        // --- 2. resolve ids, and settle copied rows that carry the same one -------------
        foreach (Draft d in drafts.Where(d => !d.Settled && d.Id.Length > 0))
        {
            if (byId.TryGetValue(d.Id, out ModelSheet? s))
            {
                d.Target = s;
                d.Keyed = true;
            }
            else if (sameModel)
            {
                d.Action = ImportAction.Skip;
                d.Reason = "This sheet was deleted from the model after the export. To create it again, clear its Ribbit ID";
            }
            else
            {
                d.Notes.Add("Its Ribbit ID is from another model - matched by sheet number");
                d.Id = "";
            }
        }

        foreach (IGrouping<string, Draft> group in drafts
                     .Where(d => d.Keyed)
                     .GroupBy(d => d.Target!.Id, StringComparer.OrdinalIgnoreCase)
                     .Where(g => g.Count() > 1))
        {
            // A copied row keeps the id of the row it was copied from. The row
            // still showing the ORIGINAL number is the original; the others are
            // new sheets. With no single such row it cannot be told apart.
            string originalNumber = BaseValue(file, group.Key, ExchangeColumn.SheetNumber.Header) ?? group.First().Target!.Number;
            List<Draft> originals = [.. group.Where(d => Same(ValueKind.Text, d.Number, originalNumber))];

            if (originals.Count == 1)
            {
                foreach (Draft copy in group.Where(d => d != originals[0]))
                {
                    copy.Keyed = false;
                    copy.Target = null;
                    copy.Id = "";
                    copy.Notes.Add($"Copied from row {originals[0].ExcelRow} - its Ribbit ID is ignored and it is treated as a new row");
                }
            }
            else
            {
                string rows = string.Join(", ", group.Select(d => d.ExcelRow));
                foreach (Draft d in group)
                    d.Fail($"The same Ribbit ID is on rows {rows}. Clear it on the copies");
            }
        }

        // --- 3. rows tied to a sheet by id: three-way, per value -----------------------
        foreach (Draft d in drafts.Where(d => d.Keyed && !d.Settled))
        {
            if (!CheckNumber(d))
                continue;
            IReadOnlyDictionary<string, string>? baseline =
                file.Base.TryGetValue(d.Target!.Id, out IReadOnlyDictionary<string, string>? b) ? b : null;
            Compare(d, columns, baseline, titleblockNames, options);
        }

        // Checked out by somebody else: nothing on the sheet moves - its number
        // included, so it must be settled BEFORE working out which numbers are
        // freed, or a new row could be handed a number that stays taken.
        MarkLocked(drafts);

        // --- 4. the number each sheet ends up with ------------------------------------
        // Unkeyed rows are matched against FINAL numbers, so a row that takes a
        // number another row has just freed creates a sheet rather than
        // updating the one that is moving away.
        Dictionary<string, string> finalById = new(StringComparer.OrdinalIgnoreCase);
        foreach (ModelSheet s in model)
            finalById[s.Id] = s.Number;
        foreach (Draft d in drafts.Where(d => d.Keyed && d.Action == ImportAction.Update))
            finalById[d.Target!.Id] = d.NewNumber();

        Dictionary<string, ModelSheet> byFinalNumber = new(StringComparer.OrdinalIgnoreCase);
        foreach (ModelSheet s in model)
            byFinalNumber[finalById[s.Id]] = s;

        // --- 5. rows without an id: matched by number, or created ---------------------
        Dictionary<string, Draft> claimedBy = new(StringComparer.OrdinalIgnoreCase);
        foreach (Draft d in drafts.Where(d => d.Keyed && !d.Settled))
            claimedBy[d.Target!.Id] = d;

        foreach (Draft d in drafts.Where(d => !d.Keyed && !d.Settled))
        {
            if (!CheckNumber(d))
                continue;

            if (byFinalNumber.TryGetValue(d.Number, out ModelSheet? s))
            {
                if (claimedBy.TryGetValue(s.Id, out Draft? other))
                {
                    d.Fail(other.Keyed
                        ? $"Sheet {d.Number} is already updated by row {other.ExcelRow}"
                        : $"Sheet {d.Number} is on two rows ({other.ExcelRow} and {d.ExcelRow})");
                    continue;
                }

                claimedBy[s.Id] = d;
                d.Target = s;
                Compare(d, columns, baseline: null, titleblockNames, options);
            }
            else
            {
                PlanCreate(d, columns, titleblockNames, options);
            }
        }

        // --- 6. no sheet number may end up used twice ---------------------------------
        ResolveNumberClashes(drafts, model);

        // --- 7. checked out by somebody else (the rows matched by number) -----------
        MarkLocked(drafts);

        return Finish(drafts, fileNotes, sameModel);
    }

    private static void MarkLocked(List<Draft> drafts)
    {
        foreach (Draft d in drafts.Where(d => d.Action == ImportAction.Update && d.Target!.Locked))
        {
            d.Action = ImportAction.Locked;
            d.Reason = d.Target!.LockedBy.Length > 0
                ? $"Checked out by {d.Target.LockedBy}"
                : "Checked out by another user";
        }
    }

    private static string? BaseValue(ExchangeFile file, string id, string header)
        => file.Base.TryGetValue(id, out IReadOnlyDictionary<string, string>? b) && b.TryGetValue(header, out string? v) ? v : null;

    private static bool CheckNumber(Draft d)
    {
        (bool ok, string why) = SheetPlanner.ValidSheetNumber(d.Number);
        if (!ok)
            d.Fail(why);
        return ok;
    }

    /// <summary>An update: every column judged against the model now, and the export copy when there is one.</summary>
    private static void Compare(
        Draft d,
        IReadOnlyList<ExchangeColumn> columns,
        IReadOnlyDictionary<string, string>? baseline,
        IReadOnlyList<string> titleblockNames,
        ImportOptions options)
    {
        ModelSheet s = d.Target!;
        d.Id = s.Id;

        foreach (ExchangeColumn c in columns)
        {
            string excel = SheetText.S(d.Row.Get(c.Header));
            string now = Current(s, c);
            string? was = baseline is not null && baseline.TryGetValue(c.Header, out string? bv) ? SheetText.S(bv) : null;

            Verdict verdict = Decide(c.Kind, excel, was, now);
            if (verdict == Verdict.None)
                continue;

            if (c.ReadOnly)
            {
                d.Notes.Add($"'{c.Header}' is read-only in Revit - the change is ignored");
                continue;
            }

            if (verdict == Verdict.Conflict && !options.ExcelWinsConflicts)
            {
                d.Conflicts.Add(new CellChange(c, now, excel, BothChanged: true));
                continue;
            }

            string? problem = Validate(c, excel, titleblockNames, forCreate: false, out int? tb);
            if (problem is not null)
            {
                d.Problems.Add(new CellProblem(c, excel, problem));
                continue;
            }

            if (c.Role == ColumnRole.Titleblock)
                d.TitleblockIndex = tb;

            d.Changes.Add(new CellChange(c, now, Canon(c.Kind, excel), verdict == Verdict.Conflict));
        }

        d.Action = d.Changes.Count > 0 ? ImportAction.Update : ImportAction.Unchanged;
        if (d.Name.Length == 0)
            d.Name = s.Name;
    }

    private static void PlanCreate(
        Draft d,
        IReadOnlyList<ExchangeColumn> columns,
        IReadOnlyList<string> titleblockNames,
        ImportOptions options)
    {
        d.Action = ImportAction.Create;
        d.Id = "";

        foreach (ExchangeColumn c in columns)
        {
            string excel = SheetText.S(d.Row.Get(c.Header));

            if (c.Role == ColumnRole.Titleblock)
                continue;   // below, with the default
            if (excel.Length == 0)
                continue;   // a new sheet keeps Revit's default

            if (c.ReadOnly)
            {
                d.Notes.Add($"'{c.Header}' is read-only in Revit - not written");
                continue;
            }

            string? problem = Validate(c, excel, titleblockNames, forCreate: true, out _);
            if (problem is not null)
            {
                d.Problems.Add(new CellProblem(c, excel, problem));
                continue;
            }

            d.Changes.Add(new CellChange(c, "", Canon(c.Kind, excel)));
        }

        // Named on the row, else the import's default. A name that matches
        // nothing is an ERROR, never a fallback: stamping a different titleblock
        // on a real drawing than the one asked for is worse than not creating it.
        ExchangeColumn? tbCol = columns.FirstOrDefault(c => c.Role == ColumnRole.Titleblock);
        string wanted = tbCol is null ? "" : SheetText.S(d.Row.Get(tbCol.Header));
        if (wanted.Length == 0)
            wanted = SheetText.S(options.DefaultTitleblock);

        if (wanted.Length > 0)
        {
            int? index = TitleblockResolver.Resolve(wanted, titleblockNames);
            if (index is null)
            {
                d.Fail($"No titleblock type matches '{wanted}'");
                return;
            }
            d.TitleblockIndex = index;
        }
    }

    private enum Verdict { None, Change, Conflict }

    private static Verdict Decide(ValueKind kind, string excel, string? was, string now)
    {
        if (was is not null && Same(kind, excel, was))
            return Verdict.None;                // untouched in Excel
        if (Same(kind, excel, now))
            return Verdict.None;                // already agree
        if (was is null)
            return excel.Length == 0 ? Verdict.None : Verdict.Change;   // no export copy: blank = leave
        if (Same(kind, now, was))
            return Verdict.Change;              // changed in Excel only
        return Verdict.Conflict;                // changed on both sides
    }

    private static string Current(ModelSheet s, ExchangeColumn c) => c.Role switch
    {
        ColumnRole.Number => s.Number,
        ColumnRole.Name => s.Name,
        ColumnRole.Titleblock => s.Titleblock,
        _ => s.Values.TryGetValue(c.Header, out string? v) ? SheetText.S(v) : "",
    };

    /// <summary>Why this value cannot be written, or null.</summary>
    private static string? Validate(
        ExchangeColumn c, string value, IReadOnlyList<string> titleblockNames, bool forCreate, out int? titleblock)
    {
        titleblock = null;
        switch (c.Role)
        {
            case ColumnRole.Number:
                return null;   // checked for the whole row

            case ColumnRole.Name:
                return value.Length == 0 ? "A sheet name cannot be blank" : null;

            case ColumnRole.Titleblock:
                if (value.Length == 0)
                    return "A titleblock cannot be removed here";
                titleblock = TitleblockResolver.Resolve(value, titleblockNames);
                return titleblock is null ? $"No titleblock type matches '{value}'" : null;
        }

        if (value.Length == 0)
        {
            return c.Kind == ValueKind.Text
                ? null
                : "Revit cannot clear a number or Yes/No value - type the value you want instead";
        }

        return c.Kind switch
        {
            ValueKind.Integer when !TryParseInteger(value, out _) => $"'{value}' is not a whole number",
            ValueKind.YesNo when !TryParseYesNo(value, out _) => $"'{value}' is not Yes or No",
            _ => null,
        };
    }

    /// <summary>
    /// Any number used twice, once everything is applied, fails the rows that
    /// would cause it. Repeated because failing a renumber puts its sheet back
    /// on its old number, which can clash in turn.
    /// </summary>
    private static void ResolveNumberClashes(List<Draft> drafts, IReadOnlyList<ModelSheet> model)
    {
        for (int pass = 0; pass < drafts.Count + 1; pass++)
        {
            Dictionary<string, Draft> rowFor = new(StringComparer.OrdinalIgnoreCase);
            foreach (Draft d in drafts.Where(d => d.Target is not null && d.Action == ImportAction.Update))
                rowFor[d.Target!.Id] = d;

            // (final number) -> who holds it: a model sheet (with the row moving it, if any) or a create row.
            Dictionary<string, List<(ModelSheet? Sheet, Draft? Row)>> holders = new(StringComparer.OrdinalIgnoreCase);
            void Hold(string number, ModelSheet? sheet, Draft? row)
            {
                if (!holders.TryGetValue(number, out List<(ModelSheet?, Draft?)>? list))
                    holders[number] = list = [];
                list.Add((sheet, row));
            }

            foreach (ModelSheet s in model)
            {
                rowFor.TryGetValue(s.Id, out Draft? row);
                Hold(row?.NewNumber() ?? s.Number, s, row);
            }
            foreach (Draft d in drafts.Where(d => d.Action == ImportAction.Create))
                Hold(d.Number, null, d);

            bool changed = false;
            foreach (KeyValuePair<string, List<(ModelSheet? Sheet, Draft? Row)>> kv in holders.Where(h => h.Value.Count > 1))
            {
                // Only a row that MOVES a sheet onto the number, or creates one,
                // is at fault; a sheet already holding it stays put.
                List<Draft> culprits = [.. kv.Value
                    .Where(h => h.Row is not null
                                && (h.Row.Action == ImportAction.Create
                                    || !Same(ValueKind.Text, h.Row.NewNumber(), h.Sheet!.Number)))
                    .Select(h => h.Row!)];

                string rows = string.Join(", ", culprits.Select(c => c.ExcelRow));
                foreach (Draft d in culprits)
                {
                    d.Fail(culprits.Count > 1
                        ? $"Sheet number {kv.Key} would be used twice (rows {rows})"
                        : $"Sheet number {kv.Key} is already used by another sheet in the model");
                    changed = true;
                }
            }

            if (!changed)
                return;
        }
    }

    private static ImportPlan Finish(List<Draft> drafts, List<string> fileNotes, bool sameModel)
    {
        List<ImportRow> rows = [];
        foreach (Draft d in drafts)
        {
            string number = d.Action is ImportAction.Update or ImportAction.Locked ? d.NewNumber() : d.Number;
            string name = d.Changes.FirstOrDefault(c => c.Column.Role == ColumnRole.Name)?.To ?? d.Name;
            rows.Add(new ImportRow(
                d.ExcelRow, d.Action,
                d.Action == ImportAction.Create ? "" : d.Target?.Id ?? "",
                number, name,
                [.. d.Changes], [.. d.Conflicts], [.. d.Problems], [.. d.Notes],
                d.Reason, d.TitleblockIndex));
        }

        ImportCounts counts = new(
            Rows: rows.Count,
            Create: rows.Count(r => r.Action == ImportAction.Create),
            Update: rows.Count(r => r.Action == ImportAction.Update),
            Unchanged: rows.Count(r => r.Action == ImportAction.Unchanged),
            Skip: rows.Count(r => r.Action == ImportAction.Skip),
            Locked: rows.Count(r => r.Action == ImportAction.Locked),
            Error: rows.Count(r => r.Action == ImportAction.Error),
            Values: rows.Where(r => r.Action is ImportAction.Create or ImportAction.Update).Sum(r => r.Changes.Count),
            Conflicts: rows.Sum(r => r.Conflicts.Count),
            Problems: rows.Sum(r => r.Problems.Count));

        return new ImportPlan(rows, fileNotes, sameModel, counts);
    }

    // --- values ------------------------------------------------------------------------

    public static bool Same(ValueKind kind, string a, string b)
        => string.Equals(Canon(kind, a), Canon(kind, b), StringComparison.Ordinal);

    /// <summary>
    /// The form two values are compared in: trimmed, one line-ending style,
    /// and Yes/No and whole numbers in one spelling ("yes", "TRUE" and "1" are
    /// all "Yes").
    /// </summary>
    public static string Canon(ValueKind kind, string? value)
    {
        string s = SheetText.S(value).Replace("\r\n", "\n").Replace('\r', '\n');
        switch (kind)
        {
            case ValueKind.YesNo:
                return TryParseYesNo(s, out bool yes) ? (yes ? "Yes" : "No") : s;
            case ValueKind.Integer:
                return TryParseInteger(s, out int i) ? i.ToString(CultureInfo.InvariantCulture) : s;
            default:
                return s;
        }
    }

    public static bool TryParseYesNo(string? value, out bool yes)
    {
        switch (SheetText.S(value).ToLowerInvariant())
        {
            case "yes": case "y": case "true": case "1": case "x":
                yes = true;
                return true;
            case "no": case "n": case "false": case "0":
                yes = false;
                return true;
            default:
                yes = false;
                return false;
        }
    }

    /// <summary>Whole numbers only; "12.0" (how Excel may hand one back) counts.</summary>
    public static bool TryParseInteger(string? value, out int number)
    {
        string s = SheetText.S(value);
        if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out number))
            return true;
        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double f)
            && f == Math.Floor(f) && f >= int.MinValue && f <= int.MaxValue)
        {
            number = (int)f;
            return true;
        }
        number = 0;
        return false;
    }
}
