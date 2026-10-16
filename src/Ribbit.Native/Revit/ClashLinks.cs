using Autodesk.Revit.DB;

namespace Ribbit.Native.Revit;

/// <summary>
/// Revit Link handling for the clash tools. Ported from lib/clashlib/links.py.
/// </summary>
internal static class ClashLinks
{
    /// <summary>
    /// Link instances, deduplicated. The category filter is a fallback for
    /// environments where the class filter comes back empty.
    /// </summary>
    public static List<RevitLinkInstance> GetLinkInstances(Document doc)
    {
        List<RevitLinkInstance> links = [];

        try
        {
            links =
            [
                .. new FilteredElementCollector(doc)
                    .OfClass(typeof(RevitLinkInstance))
                    .WhereElementIsNotElementType()
                    .Cast<RevitLinkInstance>()
            ];
        }
        catch (Exception)
        {
            links = [];
        }

        if (links.Count == 0)
        {
            try
            {
                links =
                [
                    .. new FilteredElementCollector(doc)
                        .OfCategory(BuiltInCategory.OST_RvtLinks)
                        .WhereElementIsNotElementType()
                        .OfType<RevitLinkInstance>()
                ];
            }
            catch (Exception)
            {
                links = [];
            }
        }

        HashSet<long> seen = [];
        List<RevitLinkInstance> outp = [];
        foreach (RevitLinkInstance link in links)
        {
            if (seen.Add(RevitCompat.IdKey(link.Id)))
                outp.Add(link);
        }

        return outp;
    }

    public static string DisplayName(RevitLinkInstance? link)
    {
        if (link is null)
            return "Link";

        try
        {
            string name = link.Name;
            if (!string.IsNullOrEmpty(name))
                return name;
        }
        catch (Exception)
        {
            // Fall through to the id.
        }

        try
        {
            return $"Link {RevitCompat.IdKey(link.Id)}";
        }
        catch (Exception)
        {
            return "Link";
        }
    }

    /// <summary>
    /// Human-readable load status, for diagnosing an empty Set B.
    ///
    /// A link can be present in the model and visible in Manage Links while
    /// currently UNLOADED, in which case GetLinkDocument() returns null and no
    /// element can ever be collected from it — silently, unless this is
    /// surfaced.
    /// </summary>
    public static string LoadStatus(RevitLinkInstance? link)
    {
        if (link is null)
            return "no link selected";

        Document? linkDoc;
        try
        {
            linkDoc = link.GetLinkDocument();
        }
        catch (Exception)
        {
            linkDoc = null;
        }

        if (linkDoc is not null)
            return "loaded";

        try
        {
            if (link.Document.GetElement(link.GetTypeId()) is RevitLinkType linkType)
                return $"unloaded (status: {linkType.GetLinkedFileStatus()})";
        }
        catch (Exception)
        {
            // Fall through to the generic message.
        }

        return "unloaded or not found (GetLinkDocument() returned nothing)";
    }

    /// <summary>
    /// Elements of a category inside the linked document.
    ///
    /// The category id is passed straight to OfCategoryId. The source Python
    /// node re-wrapped it as ElementId(c.Id) — because Dynamo's Category wrapper
    /// exposes a plain int — which on a raw Revit Category is an invalid
    /// constructor call. It was swallowed by the surrounding try/except and
    /// returned an empty list every single time, which is why Set B always came
    /// back empty even with a loaded link and a real category selected.
    /// </summary>
    public static List<Element> CollectCategoryInLink(
        RevitLinkInstance? link, Category? category)
    {
        if (link is null || category is null)
            return [];

        Document? linkDoc;
        try
        {
            linkDoc = link.GetLinkDocument();
        }
        catch (Exception)
        {
            linkDoc = null;
        }

        if (linkDoc is null)
            return [];

        try
        {
            return
            [
                .. new FilteredElementCollector(linkDoc)
                    .OfCategoryId(category.Id)
                    .WhereElementIsNotElementType()
            ];
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>
    /// Linked document -> every placed instance of it, with that instance's own
    /// total transform. One linked file can be placed several times, each at a
    /// different position, and each needs its own transform.
    ///
    /// This map — and every id-dedup keyed on (Document, id) — relies on
    /// GetLinkDocument() and an element's .Document returning the SAME Document
    /// reference for a given link. Revit guarantees that, and Document does not
    /// override equality, so it is reference identity rather than value
    /// equality.
    ///
    /// If reported clashes ever look geometrically wrong for LINK-side elements
    /// specifically, suspect this first: a lookup miss here makes a link element
    /// silently get treated as host coordinates instead of raising — which is
    /// the dangerous failure direction, wrong-but-plausible rather than a crash.
    /// </summary>
    public static Dictionary<Document, List<(RevitLinkInstance Instance, Transform Tr)>>
        BuildTransformMap(Document doc)
    {
        Dictionary<Document, List<(RevitLinkInstance, Transform)>> map = [];

        List<RevitLinkInstance> instances;
        try
        {
            instances =
            [
                .. new FilteredElementCollector(doc)
                    .OfClass(typeof(RevitLinkInstance))
                    .Cast<RevitLinkInstance>()
            ];
        }
        catch (Exception)
        {
            return map;
        }

        foreach (RevitLinkInstance li in instances)
        {
            try
            {
                Document? ldoc = li.GetLinkDocument();
                if (ldoc is null)
                    continue;

                Transform? tr = li.GetTotalTransform();
                if (tr is null)
                    continue;

                if (!map.TryGetValue(ldoc, out List<(RevitLinkInstance, Transform)>? list))
                    map[ldoc] = list = [];

                list.Add((li, tr));
            }
            catch (Exception)
            {
                // A link that will not report a transform is skipped; its
                // elements then never enter the search, which the caller can see
                // from an empty Set B plus LoadStatus.
            }
        }

        return map;
    }
}
