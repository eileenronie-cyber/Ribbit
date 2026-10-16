using Autodesk.Revit.DB;

namespace Ribbit.Native.Revit;

/// <summary>
/// Category collection for the clash tools. Ported from lib/clashlib/categories.py.
/// </summary>
internal static class ClashCategories
{
    /// <summary>
    /// The exact 24-category list the source graph clashes against.
    ///
    /// DO NOT reorder, add, or remove entries. This is transcribed verbatim from
    /// the source's "CATEGORY COLLECTOR" node; changing it changes which
    /// elements are searched, silently, and the reports would no longer be
    /// comparable with previous runs.
    /// </summary>
    public static readonly (string Name, BuiltInCategory Bic)[] NameToBic =
    [
        ("Pipes", BuiltInCategory.OST_PipeCurves),
        ("Pipe Fittings", BuiltInCategory.OST_PipeFitting),
        ("Pipe Accessories", BuiltInCategory.OST_PipeAccessory),
        ("Sprinklers", BuiltInCategory.OST_Sprinklers),

        ("Ducts", BuiltInCategory.OST_DuctCurves),
        ("Duct Fittings", BuiltInCategory.OST_DuctFitting),
        ("Duct Accessories", BuiltInCategory.OST_DuctAccessory),
        ("Air Terminals", BuiltInCategory.OST_DuctTerminal),

        ("Cable Trays", BuiltInCategory.OST_CableTray),
        ("Cable Tray Fittings", BuiltInCategory.OST_CableTrayFitting),

        ("Conduits", BuiltInCategory.OST_Conduit),
        ("Conduit Fittings", BuiltInCategory.OST_ConduitFitting),

        ("Electrical Equipment", BuiltInCategory.OST_ElectricalEquipment),
        ("Mechanical Equipment", BuiltInCategory.OST_MechanicalEquipment),

        ("Electrical Fixtures", BuiltInCategory.OST_ElectricalFixtures),
        ("Lighting Fixtures", BuiltInCategory.OST_LightingFixtures),
        ("Lighting Devices", BuiltInCategory.OST_LightingDevices),

        ("Data Devices", BuiltInCategory.OST_DataDevices),
        ("Communication Devices", BuiltInCategory.OST_CommunicationDevices),
        ("Telephone Devices", BuiltInCategory.OST_TelephoneDevices),
        ("Nurse Call Devices", BuiltInCategory.OST_NurseCallDevices),

        ("Plumbing Fixtures", BuiltInCategory.OST_PlumbingFixtures),

        ("Security Devices", BuiltInCategory.OST_SecurityDevices),
        ("Fire Alarm Devices", BuiltInCategory.OST_FireAlarmDevices),
    ];

    public static readonly BuiltInCategory[] Bics = [.. NameToBic.Select(p => p.Bic)];

    /// <summary>
    /// One collector per category, exactly as the source's node did.
    /// </summary>
    public static Dictionary<BuiltInCategory, List<Element>> CollectByCategory(Document doc)
    {
        Dictionary<BuiltInCategory, List<Element>> buckets = [];

        foreach (BuiltInCategory bic in Bics)
        {
            try
            {
                buckets[bic] =
                [
                    .. new FilteredElementCollector(doc)
                        .OfCategory(bic)
                        .WhereElementIsNotElementType()
                ];
            }
            catch (Exception)
            {
                buckets[bic] = [];
            }
        }

        return buckets;
    }

    /// <summary>
    /// Same result as <see cref="CollectByCategory"/> but via ONE
    /// ElementMulticategoryFilter rather than 24 separate collectors, bucketed
    /// afterwards by each element's own category.
    ///
    /// Behaviourally identical — a Revit element belongs to exactly one
    /// category, so a single multi-category pass and 24 single-category passes
    /// can never disagree about which bucket an element lands in. It is just
    /// fewer collector round-trips, which matters once this runs per host
    /// document PLUS once per selected link.
    ///
    /// Falls back to the proven per-category path on any failure, so a
    /// multicategory-filter problem degrades to slower-but-verified rather than
    /// silently returning an incomplete result.
    /// </summary>
    public static Dictionary<BuiltInCategory, List<Element>> CollectByCategoryFast(Document doc)
        => CollectByCategoryFast(doc, Bics);

    /// <summary>
    /// The same single-pass collection over an ARBITRARY category list.
    ///
    /// Federated Clash needs this: a set may name Structural Framing or Walls,
    /// which are nowhere in the 24 MEP categories, and a category that is never
    /// collected can never match. The 24 stay the default for every other
    /// caller, so nothing they search changes.
    /// </summary>
    public static Dictionary<BuiltInCategory, List<Element>> CollectByCategoryFast(
        Document doc, IReadOnlyList<BuiltInCategory> bics)
    {
        if (bics.Count == 0)
            return [];

        List<Element> elements;
        try
        {
            ElementMulticategoryFilter filter = new([.. bics]);
            elements =
            [
                .. new FilteredElementCollector(doc)
                    .WherePasses(filter)
                    .WhereElementIsNotElementType()
            ];
        }
        catch (Exception)
        {
            return CollectByCategory(doc);
        }

        Dictionary<long, BuiltInCategory> byCatId = [];
        foreach (BuiltInCategory bic in bics)
        {
            try
            {
                byCatId[(long)bic] = bic;
            }
            catch (Exception)
            {
                // A category id that will not resolve simply never matches.
            }
        }

        Dictionary<BuiltInCategory, List<Element>> buckets = [];
        foreach (BuiltInCategory bic in bics)
            buckets[bic] = [];

        foreach (Element el in elements)
        {
            try
            {
                Category? cat = el.Category;
                if (cat is null)
                    continue;

                if (byCatId.TryGetValue(RevitCompat.IdKey(cat.Id), out BuiltInCategory bic))
                    buckets[bic].Add(el);
            }
            catch (Exception)
            {
                // Skip an element whose category will not read.
            }
        }

        return buckets;
    }

    private static bool IsImported(Category cat)
    {
        try
        {
            if (cat.Parent is not null)
                return true;

            string name = cat.Name.ToLowerInvariant();
            return name.Contains(".dwg") || name.Contains("import");
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool IsValidModelCategory(Category cat)
    {
        try
        {
            if (cat.CategoryType != CategoryType.Model)
                return false;
            if (cat.IsTagCategory)
                return false;
            if (IsImported(cat))
                return false;

            return !cat.Name.ToLowerInvariant().Contains("analytical");
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Every Model-type, non-tag, non-imported, non-analytical category in the
    /// document — what the category picker offers.
    /// </summary>
    public static List<Category> AllModelCategories(Document doc)
    {
        List<Category> outp = [];

        foreach (Category cat in doc.Settings.Categories)
        {
            try
            {
                if (IsValidModelCategory(cat))
                    outp.Add(cat);
            }
            catch (Exception)
            {
                // Skip an unreadable category.
            }
        }

        return outp;
    }
}
