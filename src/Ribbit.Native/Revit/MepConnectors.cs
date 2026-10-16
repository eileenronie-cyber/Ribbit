using Autodesk.Revit.DB;

namespace Ribbit.Native.Revit;

/// <summary>
/// Connector lookup shared by the MEP tools.
///
/// The Python source repeated this getattr-based probe in every tool that
/// needed connectors (moveconnect.py, Disconnect/script.py, and others).
/// C# can resolve it once with a type switch, so it lives here rather than
/// being copied per tool.
///
/// Note the End-only vs all-connectors split: Move/Connect/Align only ever
/// wants End connectors, while Disconnect has to see every ref (a curve can
/// be joined through a non-End connector). Keep them distinct.
/// </summary>
internal static class MepConnectors
{
    public static ConnectorManager? GetConnectorManager(Element elem) => elem switch
    {
        MEPCurve curve => curve.ConnectorManager,
        FamilyInstance fi => fi.MEPModel?.ConnectorManager,
        _ => null,
    };

    /// <summary>Every connector on the element, whatever its type.</summary>
    public static List<Connector> GetAllConnectors(Element elem)
    {
        ConnectorManager? cm = GetConnectorManager(elem);
        if (cm is null)
            return [];

        List<Connector> result = [];
        foreach (Connector c in cm.Connectors)
            result.Add(c);
        return result;
    }

    /// <summary>Only ConnectorType.End — the ones a join can be made on.</summary>
    public static List<Connector> GetEndConnectors(Element elem)
        => GetAllConnectors(elem).FindAll(c => c.ConnectorType == ConnectorType.End);

    public static List<Connector> GetOpenConnectors(Element elem)
        => GetEndConnectors(elem).FindAll(c => !c.IsConnected);

    /// <summary>
    /// Nearest open connector by real 3D distance. Used to resolve which end of
    /// a run the user meant from where they clicked to select it.
    /// </summary>
    public static Connector? ClosestOpenConnector(Element elem, XYZ point)
    {
        Connector? best = null;
        double? bestDist = null;

        foreach (Connector c in GetOpenConnectors(elem))
        {
            double d = c.Origin.DistanceTo(point);
            if (bestDist is null || d < bestDist)
            {
                bestDist = d;
                best = c;
            }
        }
        return best;
    }

    /// <summary>
    /// Regenerates so the connector reports the element's NEW position, and
    /// re-fetches it by id.
    ///
    /// It used to say "Connector objects do not survive a regeneration". THAT IS
    /// NOT TRUE, measured 2026-08-16 against a live model: a Connector held
    /// across `MoveElement` + `Regenerate` still reads fine and reports the
    /// updated origin — both on the element that moved and on one that did not.
    /// The claim mattered, because it made the target connector in
    /// <c>MoveConnectEngine</c> look like a live bug for being re-used without a
    /// refresh. It is not; it is fine.
    ///
    /// So the RE-FETCH here is belt-and-braces and could go. The REGENERATE is
    /// the part that earns its keep: it is what makes the new geometry readable
    /// before the next step measures against it. Whether the origin would update
    /// without it has not been tested.
    /// </summary>
    public static Connector RefreshConnector(Document doc, Element elem, Connector oldConn)
    {
        doc.Regenerate();
        foreach (Connector c in GetEndConnectors(elem))
        {
            if (c.Id == oldConn.Id)
                return c;
        }
        return oldConn;
    }
}
