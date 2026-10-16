using Autodesk.Revit.DB;
using Ribbit.Geometry.Sheets;

namespace Ribbit.Native.Revit;

/// <summary>One row's outcome, with the Revit element the plan actually produced.</summary>
internal sealed record SheetRowResult(RowPlan Plan, ViewSheet? Sheet);

/// <summary>
/// SheetMaker's Revit half. Ported from lib/sheetmakerlib/sheets.py.
///
/// The create/update/skip/spacer/error DECISION lives in
/// <see cref="SheetPlanner"/>, which is Revit-free and unit tested; this only
/// carries the decisions out.
///
/// DRY RUN IS GENUINELY READ-ONLY. The source Dynamo graph only skipped
/// *starting a transaction* when dry run was on, but still called
/// ViewSheet.Create / .SheetNumber= / FamilySymbol.Activate() for every row —
/// all of which need an open transaction, so every row failed and a "dry run"
/// produced a wall of errors instead of a preview. Here dry run never calls a
/// single document-mutating API.
/// </summary>
internal static class SheetMakerEngine
{
    public static List<FamilySymbol> CollectTitleblockSymbols(Document doc)
    {
        List<FamilySymbol> symbols =
        [
            .. new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_TitleBlocks)
                .WhereElementIsElementType()
                .Cast<FamilySymbol>()
        ];

        symbols.Sort((a, b) => string.Compare(
            TitleblockDisplayName(a), TitleblockDisplayName(b), StringComparison.OrdinalIgnoreCase));

        return symbols;
    }

    /// <summary>
    /// "Family : Type", read through BuiltInParameter rather than .Family.Name
    /// so it degrades to "No Family"/"No Type" instead of throwing on an
    /// edge-case titleblock type.
    /// </summary>
    public static string TitleblockDisplayName(Element symbol)
    {
        try
        {
            Parameter? familyParam = symbol.get_Parameter(BuiltInParameter.SYMBOL_FAMILY_NAME_PARAM);
            Parameter? typeParam = symbol.get_Parameter(BuiltInParameter.SYMBOL_NAME_PARAM);

            string familyName = familyParam?.AsString() ?? "No Family";
            string typeName = typeParam?.AsString() ?? "No Type";
            return $"{familyName} : {typeName}";
        }
        catch (Autodesk.Revit.Exceptions.ApplicationException)
        {
            return "(titleblock)";
        }
    }

    public static Dictionary<string, ViewSheet> ExistingSheetsByNumber(Document doc)
    {
        Dictionary<string, ViewSheet> existing = [];
        foreach (ViewSheet sheet in new FilteredElementCollector(doc)
                     .OfClass(typeof(ViewSheet))
                     .Cast<ViewSheet>())
        {
            existing[SheetText.S(sheet.SheetNumber)] = sheet;
        }
        return existing;
    }

    /// <summary>Only ever called when actually committing — never during a dry run.</summary>
    private static ElementId? ActivateAndGetId(Document doc, FamilySymbol? symbol)
    {
        if (symbol is null)
            return null;

        try
        {
            if (!symbol.IsActive)
            {
                symbol.Activate();
                doc.Regenerate();
            }
        }
        catch (Autodesk.Revit.Exceptions.ApplicationException)
        {
            // An un-activatable symbol still has a usable id; Create will report
            // the real problem if there is one.
        }

        return symbol.Id;
    }

    /// <summary>
    /// Sheets already checked out by somebody else, mapped to that person.
    ///
    /// Asked BEFORE any editing starts: Revit refuses the change anyway, but it
    /// refuses it mid-transaction, which would roll back a whole good batch
    /// because one sheet is open on a colleague's machine.
    /// </summary>
    public static Dictionary<string, string> LockedSheetOwners(Document doc)
    {
        Dictionary<string, string> locked = new(StringComparer.OrdinalIgnoreCase);
        if (!doc.IsWorkshared)
            return locked;

        foreach (ViewSheet sheet in new FilteredElementCollector(doc)
                     .OfClass(typeof(ViewSheet))
                     .Cast<ViewSheet>())
        {
            try
            {
                if (WorksharingUtils.GetCheckoutStatus(doc, sheet.Id)
                    != CheckoutStatus.OwnedByOtherUser)
                {
                    continue;
                }

                string owner = WorksharingUtils.GetWorksharingTooltipInfo(doc, sheet.Id).Owner;
                locked[SheetText.S(sheet.SheetNumber)] =
                    owner.Length > 0 ? owner : "another user";
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException)
            {
                // A sheet whose status cannot be read is left to the write to
                // judge - guessing "locked" here would skip work that is fine.
            }
        }

        return locked;
    }

    /// <summary>The titleblock instance placed on a sheet, if there is one.</summary>
    private static FamilyInstance? TitleblockOn(Document doc, ViewSheet sheet)
        => SheetExchangeEngine.TitleblockOn(doc, sheet);   // owner filter: no drawing the sheet

    /// <summary>
    /// Writes to the sheet, or failing that to its titleblock instance.
    ///
    /// A titleblock label may be driven by either, and SheetMaker used to write
    /// only to the sheet — so a mapping naming a titleblock's own shared
    /// parameter did nothing at all, silently, on every row.
    /// </summary>
    private static void SetParamIfChanged(
        ViewSheet sheet, Lazy<FamilyInstance?> titleblock, string name, string value)
    {
        if (TryWrite(sheet, name, value))
            return;

        FamilyInstance? tb = titleblock.Value;
        if (tb is not null)
            TryWrite(tb, name, value);
    }

    /// <summary>
    /// Writes only when the value actually differs, so an unchanged run does not
    /// dirty every sheet in the model. Returns false when this element has no
    /// such writable parameter, so the caller can try elsewhere.
    /// </summary>
    private static bool TryWrite(Element elem, string name, string value)
    {
        Parameter? p = elem.LookupParameter(name);
        if (p is null || p.IsReadOnly || p.StorageType != StorageType.String)
            return false;

        try
        {
            if (SheetText.S(p.AsString()) == SheetText.S(value))
                return true;
            return p.Set(value);
        }
        catch (Exception)
        {
            // A parameter that refuses the value is not worth failing the row
            // over - the rest of the sheet is still correct, and the source
            // behaved the same way. Reported as handled so the titleblock is not
            // then written with a value the sheet already rejected.
            return true;
        }
    }

    /// <summary>
    /// Plan every row and, unless <paramref name="dryRun"/>, carry it out.
    /// The caller owns the transaction (and must not open one for a dry run).
    /// </summary>
    /// <param name="progress">
    /// Called with (row, total) per row. Throttling is the dialog's job, so pass
    /// every row.
    /// </param>
    public static (List<SheetRowResult> Results, SheetMakerReport Report) Run(
        Document doc,
        IReadOnlyList<XlsxRow> rows,
        IReadOnlyDictionary<string, string> mapping,
        IReadOnlyList<FamilySymbol> titleblocks,
        ExistingMode existingMode,
        bool dryRun,
        bool blankAsSpacer,
        Action<int, int>? progress = null,
        string titleblockColumn = "",
        string defaultTitleblock = "",
        IReadOnlyDictionary<string, string>? titleblockBySheet = null)
    {
        (string sheetNoCol, string sheetNameCol) = SheetPlanner.KeyColumns(mapping);
        List<(string Param, string Column)> mappedPairs = SheetPlanner.BuildMappedPairs(mapping);

        Dictionary<string, ViewSheet> existingByNo = ExistingSheetsByNumber(doc);
        HashSet<string> existingNumbers = [.. existingByNo.Keys];

        Dictionary<string, string> lockedOwners = LockedSheetOwners(doc);
        List<string> titleblockNames = [.. titleblocks.Select(TitleblockDisplayName)];

        RowContext ctx = new(
            sheetNoCol, sheetNameCol, mappedPairs, existingNumbers, existingMode, blankAsSpacer,
            titleblockColumn, titleblockNames, new HashSet<string>(lockedOwners.Keys),
            defaultTitleblock, titleblockBySheet);

        List<SheetRowResult> results = [];

        for (int i = 0; i < rows.Count; i++)
        {
            progress?.Invoke(i, rows.Count);

            RowPlan plan;
            try
            {
                plan = SheetPlanner.PlanRow(i, rows[i], ctx);

                if (plan.Action == SheetAction.Locked)
                {
                    string who = lockedOwners.TryGetValue(plan.SheetNumber, out string? o)
                        ? o
                        : "another user";

                    // Error doubles as the report's "why" column, and being
                    // checked out by a named person IS the reason this row did
                    // nothing - leaving it blank reads as an unexplained skip.
                    plan = plan with { Owner = who, Error = $"Checked out by {who}" };
                }
            }
            catch (Exception ex)
            {
                results.Add(new SheetRowResult(
                    new RowPlan(i, SheetAction.Error, "", "", [], Error: ex.Message), null));
                continue;
            }

            try
            {
                switch (plan.Action)
                {
                    case SheetAction.Create:
                    {
                        // The planner already applied the whole precedence -
                        // per-sheet assignment, then data column, then default -
                        // so this is simply the answer.
                        int? tbIndex = plan.TitleblockIndex;
                        FamilySymbol? symbol =
                            tbIndex is not null && tbIndex.Value < titleblocks.Count
                                ? titleblocks[tbIndex.Value]
                                : null;

                        plan = plan with
                        {
                            Titleblock = symbol is null
                                ? "(placeholder - no titleblock selected)"
                                : TitleblockDisplayName(symbol),
                        };

                        if (dryRun)
                        {
                            // Claim the number even though nothing is created, or
                            // the dry run does not predict the real run: a number
                            // repeated in the workbook would report two creates
                            // here and produce one create plus one update for
                            // real. The preview band plans it the same way.
                            existingNumbers.Add(plan.SheetNumber);

                            results.Add(new SheetRowResult(plan, null));
                            break;
                        }

                        ElementId? tbId = ActivateAndGetId(doc, symbol);
                        ViewSheet sheet = tbId is not null
                            ? ViewSheet.Create(doc, tbId)
                            : ViewSheet.CreatePlaceholder(doc);

                        ApplyNumberAndName(sheet, plan);

                        // Lazy: most mappings are satisfied by the sheet alone,
                        // and this is a filtered collector per sheet.
                        Lazy<FamilyInstance?> tb = new(() => TitleblockOn(doc, sheet));

                        foreach ((string param, string value) in plan.MappedValues)
                        {
                            if (value.Length > 0)
                                SetParamIfChanged(sheet, tb, param, value);
                        }

                        // Track it so a duplicate number later in the same run
                        // resolves as an update rather than a second create.
                        existingByNo[plan.SheetNumber] = sheet;
                        existingNumbers.Add(plan.SheetNumber);

                        results.Add(new SheetRowResult(plan, sheet));
                        break;
                    }

                    case SheetAction.Update:
                    {
                        existingByNo.TryGetValue(plan.SheetNumber, out ViewSheet? sheet);

                        if (!dryRun && sheet is not null)
                        {
                            if (plan.SheetName.Length > 0)
                                TrySetName(sheet, plan.SheetName);

                            ViewSheet target = sheet;
                            Lazy<FamilyInstance?> tb = new(() => TitleblockOn(doc, target));

                            foreach ((string param, string value) in plan.MappedValues)
                            {
                                if (value.Length > 0)
                                    SetParamIfChanged(target, tb, param, value);
                            }
                        }

                        results.Add(new SheetRowResult(plan, sheet));
                        break;
                    }

                    // Locked joins Skip: nothing is written either way, but the
                    // sheet is still linked so the report can open it.
                    case SheetAction.Skip:
                    case SheetAction.Locked:
                        existingByNo.TryGetValue(plan.SheetNumber, out ViewSheet? existing);
                        results.Add(new SheetRowResult(plan, existing));
                        break;

                    default:
                        results.Add(new SheetRowResult(plan, null));
                        break;
                }
            }
            catch (Exception ex)
            {
                results.Add(new SheetRowResult(
                    plan with { Action = SheetAction.Error, Error = ex.Message }, null));
            }
        }

        SheetMakerReport report = SheetPlanner.Summarize(
            [.. results.Select(r => r.Plan)], dryRun);

        return (results, report);
    }

    private static void ApplyNumberAndName(ViewSheet sheet, RowPlan plan)
    {
        try
        {
            sheet.SheetNumber = plan.SheetNumber;
        }
        catch (Exception)
        {
            // A duplicate or refused number leaves Revit's own generated one;
            // the report row still names what was intended.
        }

        if (plan.SheetName.Length > 0)
            TrySetName(sheet, plan.SheetName);
    }

    private static void TrySetName(ViewSheet sheet, string name)
    {
        try
        {
            sheet.Name = name;
        }
        catch (Exception)
        {
            // Same rationale as the number above.
        }
    }
}
