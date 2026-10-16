using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;

namespace Ribbit.Native.Revit;

/// <summary>
/// Insulation lookup and cable-tray fitting expansion. Ported from
/// lib/clashlib/insulation.py and lib/clashlib/fittings.py.
/// </summary>
internal static class ClashElementUtils
{
    /// <summary>
    /// The element's insulation, or null. Only the FIRST insulation id is used,
    /// matching the sibling Clash Detect tool.
    ///
    /// Everything is resolved against the ELEMENT'S OWN document, never a
    /// captured host document: a pipe or duct inside a Revit Link has its own
    /// Document, and resolving its insulation id against the host would either
    /// miss or — worse — hit an unrelated element that happens to share the id.
    ///
    /// ANY MEP ELEMENT, NOT JUST PIPES AND DUCTS. This used to switch on
    /// <c>Pipe</c>/<c>Duct</c>, so a fitting's insulation was never returned -
    /// and the insulation categories are not collected either, so this is the
    /// ONLY way insulation reaches a set. Measured on testproject: 2,756 of
    /// 6,086 insulation elements wrap pipe or duct FITTINGS, and none of them
    /// could ever be clash-checked. <see cref="InsulationLiningBase.GetInsulationIds"/>
    /// answers for fittings, accessories and flex runs alike.
    /// </summary>
    public static Element? GetInsulation(Element? elem)
    {
        if (elem is null)
            return null;

        try
        {
            Document elemDoc = elem.Document;
            ICollection<ElementId>? ids = InsulationLiningBase.GetInsulationIds(elemDoc, elem.Id);

            if (ids is not null && ids.Count > 0)
                return elemDoc.GetElement(ids.First());
        }
        catch (Exception)
        {
            // An element whose insulation will not resolve simply has none for
            // reporting purposes.
        }

        return null;
    }

    /// <summary>(family name, type name), or nulls.</summary>
    public static (string? Family, string? Type) FamilyAndTypeName(Element? elem)
    {
        if (elem is null)
            return (null, null);

        ElementId typeId;
        try
        {
            typeId = elem.GetTypeId();
        }
        catch (Exception)
        {
            return (null, null);
        }

        if (typeId is null || typeId == ElementId.InvalidElementId)
            return (null, null);

        Element? typeElem;
        try
        {
            typeElem = elem.Document.GetElement(typeId);
        }
        catch (Exception)
        {
            return (null, null);
        }

        if (typeElem is null)
            return (null, null);

        string? family = null;
        string? type = null;

        try
        {
            family = (typeElem as FamilySymbol)?.FamilyName;
        }
        catch (Exception)
        {
            // Leave null.
        }

        try
        {
            type = typeElem.Name;
        }
        catch (Exception)
        {
            // Leave null.
        }

        return (family, type);
    }

    /// <summary>
    /// The "null bypass" gate: true when the element's family name resolves to
    /// something non-empty. Near-always true for a real placed fitting; kept for
    /// parity on the rare unresolvable-family edge case.
    /// </summary>
    public static bool HasResolvableFamily(Element? elem)
        => !string.IsNullOrEmpty(FamilyAndTypeName(elem).Family);

    /// <summary>
    /// Those of <paramref name="candidates"/> directly connected to any of
    /// <paramref name="trays"/>.
    ///
    /// Cable tray fittings are collected separately from the trays themselves,
    /// so a clash search over trays alone would miss the elbows and tees that
    /// physically continue the run.
    /// </summary>
    public static List<Element> ConnectedCableTrayFittings(
        IEnumerable<Element> trays, IEnumerable<Element> candidates)
    {
        HashSet<long> connectedIds = [];

        foreach (Element tray in trays)
        {
            try
            {
                foreach (Connector conn in MepConnectors.GetAllConnectors(tray))
                {
                    foreach (Connector r in conn.AllRefs)
                    {
                        try
                        {
                            Element? owner = r.Owner;
                            if (owner is not null)
                                connectedIds.Add(RevitCompat.IdKey(owner.Id));
                        }
                        catch (Exception)
                        {
                            // Skip an unreadable reference.
                        }
                    }
                }
            }
            catch (Exception)
            {
                // Skip a tray whose connectors will not enumerate.
            }
        }

        List<Element> connected = [];
        foreach (Element fitting in candidates)
        {
            try
            {
                if (connectedIds.Contains(RevitCompat.IdKey(fitting.Id)))
                    connected.Add(fitting);
            }
            catch (Exception)
            {
                // Skip an unreadable candidate.
            }
        }

        return connected;
    }
}
