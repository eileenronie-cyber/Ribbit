using Autodesk.Revit.DB;
using Ribbit.Geometry.Sheets;

namespace Ribbit.Native.Revit;

/// <summary>Where a writable parameter lives, and so what has to be edited.</summary>
internal enum ParameterHome
{
    /// <summary>On the ViewSheet itself.</summary>
    Sheet,

    /// <summary>On the titleblock family instance placed on the sheet.</summary>
    Titleblock,

    /// <summary>Carried by both; the sheet is written and the titleblock follows.</summary>
    Both,
}

/// <summary>One offerable parameter, with enough context to choose it sensibly.</summary>
/// <param name="Kind">
/// How its value travels as text, or null for a kind the sheet exchange cannot
/// carry (an element id, chiefly).
/// </param>
/// <param name="IsKey">Sheet Number or Sheet Name, which the exchange always carries itself.</param>
internal sealed record SheetParameterInfo(
    string Name, ParameterHome Home, bool ReadOnly, ValueKind? Kind = ValueKind.Text, bool IsKey = false)
{
    public string HomeLabel => Home switch
    {
        ParameterHome.Sheet => "sheet",
        ParameterHome.Titleblock => "titleblock",
        _ => "sheet + titleblock",
    };
}

/// <summary>
/// Everything SheetMaker could write to, discovered from the model instead of
/// typed into a spreadsheet by hand.
///
/// The distinction that matters: a titleblock LABEL may be driven either by a
/// parameter on the sheet or by one on the titleblock INSTANCE. SheetMaker only
/// ever wrote to the sheet, so the second kind silently did nothing — and an
/// office titleblock full of shared parameters is exactly where that bites.
///
/// TYPE parameters are deliberately excluded. A titleblock type is shared by
/// every sheet using it, so writing per-row data there would have each row
/// overwrite the last and change sheets nobody asked about.
/// </summary>
internal static class SheetParameterCatalog
{
    /// <summary>One parameter as found on an element or a binding.</summary>
    private readonly record struct Entry(string Name, bool ReadOnly, ValueKind? Kind, bool IsKey);

    public static List<SheetParameterInfo> Collect(
        Document doc, IReadOnlyList<FamilySymbol> titleblockTypes)
    {
        Dictionary<string, (ParameterHome Home, bool ReadOnly, ValueKind? Kind, bool IsKey)> found =
            new(StringComparer.OrdinalIgnoreCase);

        void Add(Entry e, ParameterHome home)
        {
            if (e.Name.Length == 0)
                return;

            if (found.TryGetValue(e.Name, out (ParameterHome Home, bool ReadOnly, ValueKind? Kind, bool IsKey) seen))
            {
                // Writable in either home is writable enough to offer. The kind
                // the sheet reports wins: it is read first, and it is the one
                // written first.
                found[e.Name] = (
                    seen.Home == home ? home : ParameterHome.Both,
                    seen.ReadOnly && e.ReadOnly,
                    seen.Kind ?? e.Kind,
                    seen.IsKey || e.IsKey);
                return;
            }

            found[e.Name] = (home, e.ReadOnly, e.Kind, e.IsKey);
        }

        foreach (Entry e in SheetEntries(doc))
            Add(e, ParameterHome.Sheet);

        foreach (Entry e in TitleblockEntries(doc, titleblockTypes))
            Add(e, ParameterHome.Titleblock);

        List<SheetParameterInfo> all =
        [
            .. found.Select(kv => new SheetParameterInfo(
                kv.Key, kv.Value.Home, kv.Value.ReadOnly, kv.Value.Kind, kv.Value.IsKey))
        ];

        all.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return all;
    }

    /// <summary>
    /// How a parameter's value travels as text. Decided by STORAGE first - that
    /// is what Set accepts - and by data type only to tell a Yes/No from an
    /// ordinary integer. Null: an element id, which a sheet list cannot carry.
    /// </summary>
    public static ValueKind? KindOf(Parameter p)
    {
        switch (p.StorageType)
        {
            case StorageType.String:
                return ValueKind.Text;
            case StorageType.Integer:
                return IsYesNo(p.Definition) ? ValueKind.YesNo : ValueKind.Integer;
            case StorageType.Double:
                return ValueKind.Number;
            default:
                return null;
        }
    }

    /// <summary>The same answer from a definition alone - a binding has no Parameter to ask.</summary>
    public static ValueKind? KindOf(Definition d)
    {
        try
        {
            ForgeTypeId type = d.GetDataType();
            if (type == SpecTypeId.Boolean.YesNo)
                return ValueKind.YesNo;
            if (type == SpecTypeId.Int.Integer)
                return ValueKind.Integer;
            if (type == SpecTypeId.String.Text || type == SpecTypeId.String.MultilineText
                || type == SpecTypeId.String.Url)
                return ValueKind.Text;
            if (UnitUtils.IsMeasurableSpec(type) || type == SpecTypeId.Number)
                return ValueKind.Number;
        }
        catch (Autodesk.Revit.Exceptions.ApplicationException)
        {
            // a definition that cannot say is not offered
        }
        return null;
    }

    private static bool IsYesNo(Definition? d)
    {
        try
        {
            return d is not null && d.GetDataType() == SpecTypeId.Boolean.YesNo;
        }
        catch (Autodesk.Revit.Exceptions.ApplicationException)
        {
            return false;
        }
    }

    private static bool IsKey(Definition? d)
        => d is InternalDefinition i
           && i.BuiltInParameter is BuiltInParameter.SHEET_NUMBER or BuiltInParameter.SHEET_NAME;

    private static Entry EntryOf(Parameter p)
        => new(p.Definition.Name, p.IsReadOnly, KindOf(p), IsKey(p.Definition));

    /// <summary>
    /// An existing sheet answers most faithfully. A model still being set up may
    /// have none — which is SheetMaker's own case — so the category bindings
    /// answer instead. A binding carries no Parameter, so read-only is unknown
    /// there and assumed false; the write itself is the final judge either way.
    /// </summary>
    private static IEnumerable<Entry> SheetEntries(Document doc)
    {
        ViewSheet? sample = new FilteredElementCollector(doc)
            .OfClass(typeof(ViewSheet))
            .Cast<ViewSheet>()
            .FirstOrDefault();

        if (sample is not null)
        {
            foreach (Parameter p in sample.Parameters)
            {
                if (p.Definition is not null)
                    yield return EntryOf(p);
            }
            yield break;
        }

        ElementId? sheets = Category.GetCategory(doc, BuiltInCategory.OST_Sheets)?.Id;
        if (sheets is null)
            yield break;

        DefinitionBindingMapIterator it = doc.ParameterBindings.ForwardIterator();
        while (it.MoveNext())
        {
            if (it.Current is not ElementBinding binding)
                continue;

            foreach (Category c in binding.Categories)
            {
                if (c.Id == sheets)
                {
                    yield return new Entry(it.Key.Name, false, KindOf(it.Key), false);
                    break;
                }
            }
        }
    }

    /// <summary>
    /// Instance parameters of a PLACED titleblock — that is the element the
    /// engine will write to, so it is the only honest source. With none placed
    /// there is nothing to report; the sheet half still carries the run.
    /// </summary>
    private static IEnumerable<Entry> TitleblockEntries(
        Document doc, IReadOnlyList<FamilySymbol> titleblockTypes)
    {
        HashSet<ElementId> wanted = [.. titleblockTypes.Select(t => t.Id)];

        List<FamilyInstance> placed =
        [
            .. new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_TitleBlocks)
                .WhereElementIsNotElementType()
                .Cast<FamilyInstance>()
        ];

        // Prefer an instance of a type the user might actually choose; fall back
        // to any placed titleblock, since office families share their labels.
        FamilyInstance? sample =
            placed.FirstOrDefault(i => wanted.Contains(i.GetTypeId()))
            ?? placed.FirstOrDefault();

        if (sample is null)
            yield break;

        foreach (Parameter p in sample.Parameters)
        {
            if (p.Definition is not null)
                yield return EntryOf(p);
        }
    }
    /// <summary>Writable names only, for the preview's "does this exist?" check.</summary>
    public static HashSet<string> WritableNames(IEnumerable<SheetParameterInfo> all)
        => new(all.Where(p => !p.ReadOnly).Select(p => p.Name), StringComparer.OrdinalIgnoreCase);
}
