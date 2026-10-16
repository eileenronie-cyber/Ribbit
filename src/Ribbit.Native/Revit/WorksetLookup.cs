using Autodesk.Revit.DB;

namespace Ribbit.Native.Revit;

/// <summary>
/// Workset NAMES for elements across a federation - the host and every link.
///
/// Names, because a set leaves worksets out by name: a WorksetId is a number
/// private to its document, and the host's "MEP_ZZ_Typical" and a link's share
/// nothing but the name. Each element is read in its OWN document's workset
/// table - the same cross-document rule as everything else here, where looking a
/// link element's id up in the host would answer about something else entirely.
///
/// Cached per (document, workset): a set is thousands of elements on a handful
/// of worksets. A model that is not workshared has no worksets, so every element
/// in it answers "" and nothing can be left out of it.
/// </summary>
internal sealed class WorksetLookup
{
    private readonly Dictionary<(Document Doc, int Id), string> _names = [];

    /// <summary>The element's workset name, or "" when it has none or it cannot be read.</summary>
    public string NameOf(Element e)
    {
        try
        {
            Document doc = e.Document;
            if (!doc.IsWorkshared)
                return "";

            WorksetId id = e.WorksetId;
            if (id is null || id == WorksetId.InvalidWorksetId)
                return "";

            (Document, int) key = (doc, id.IntegerValue);
            if (_names.TryGetValue(key, out string? cached))
                return cached;

            string name = doc.GetWorksetTable().GetWorkset(id)?.Name ?? "";
            _names[key] = name;
            return name;
        }
        catch (Exception)
        {
            // Unreadable means "on no workset we know", which leaves it IN the
            // set - an exclusion must never drop something it cannot see.
            return "";
        }
    }

    /// <summary>A document's user worksets, and whether each is hidden by default.</summary>
    public static List<(string Name, bool HiddenByDefault)> UserWorksets(Document doc)
    {
        try
        {
            if (!doc.IsWorkshared)
                return [];

            return
            [
                .. new FilteredWorksetCollector(doc)
                    .OfKind(WorksetKind.UserWorkset)
                    .Select(w => (w.Name, !w.IsVisibleByDefault))
            ];
        }
        catch (Exception)
        {
            return [];
        }
    }
}
