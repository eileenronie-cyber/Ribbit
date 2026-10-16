using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Ribbit.Native.Revit;

namespace Ribbit.Native.Tools;

/// <summary>
/// Disconnect. Ported from _Ribbit.tab/Tools.panel/Disconnect.pushbutton.
///
/// Click on two connected MEP elements (pipes, ducts, cable trays, conduits,
/// fittings, accessories, equipment, etc.) and this command disconnects them —
/// the reverse of Move/Connect/Align.
///
/// The disconnect runs inside a SILENT transaction: any warning dialogs Revit
/// would normally pop up mid-operation are auto-resolved instead of
/// interrupting the user.
///
/// USAGE
///   1st click : the FIRST MEP element
///   2nd click : the SECOND MEP element (must already be connected to the first)
///
/// NOTES
///   - Only the connector(s) actually joining the two picked elements are
///     disconnected — any other connections either element has elsewhere are
///     left untouched.
///   - Only elements with MEP connectors can be picked, and not the first
///     element (or its insulation) a second time.
///   - If the two picked elements aren't directly connected to each other,
///     nothing happens: the next prompt says why and you pick again. No
///     dialog - the same as Connect and Align.
/// </summary>
internal static class DisconnectEngine
{
    /// <summary>
    /// Every (connectorOnA, connectorOnB) pair where the two connectors are
    /// directly joined to each other. Usually just one pair, but a multi-tap
    /// fitting could share more than one.
    /// </summary>
    public static List<(Connector A, Connector B)> FindSharedConnections(Element elemA, Element elemB)
    {
        List<(Connector, Connector)> pairs = [];
        ElementId bId = elemB.Id;

        foreach (Connector ca in MepConnectors.GetAllConnectors(elemA))
        {
            ConnectorSet refs;
            try
            {
                refs = ca.AllRefs;
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException)
            {
                // A connector that refuses to report its refs simply has none
                // we can act on; skip it rather than failing the whole pick.
                continue;
            }

            foreach (Connector cb in refs)
            {
                Element? owner;
                try
                {
                    owner = cb.Owner;
                }
                catch (Autodesk.Revit.Exceptions.ApplicationException)
                {
                    continue;
                }

                if (owner is not null && owner.Id == bId)
                    pairs.Add((ca, cb));
            }
        }

        return pairs;
    }

    /// <summary>
    /// Only elements that have MEP connectors (after insulation and lining
    /// resolve to what they wrap) can be picked. Anything else can never be
    /// disconnected - a wall used to be pickable, and cost a dialog saying so.
    /// Cached: whether an element HAS connectors never changes while picking.
    /// </summary>
    internal sealed class ConnectorOwnerFilter(Document doc) : CachedPickFilter(doc)
    {
        protected override bool Test(Element elem)
            => MepConnectors.GetAllConnectors(Picking.ResolveHost(Doc, elem)).Count > 0;
    }

    /// <summary>
    /// Breaks every joint between the two, in one transaction. Null when done,
    /// otherwise why not.
    /// </summary>
    internal static string? DisconnectPair(Document doc, Element elemA, Element elemB)
    {
        if (elemA.Id == elemB.Id)
            return "pick two different elements";

        List<(Connector A, Connector B)> pairs = FindSharedConnections(elemA, elemB);
        if (pairs.Count == 0)
            return "those two elements aren't directly connected";

        using Transaction t = SilentTransaction.Start(doc, "Disconnect");
        try
        {
            foreach ((Connector ca, Connector cb) in pairs)
                ca.DisconnectFrom(cb);

            return t.Commit() == TransactionStatus.Committed ? null : "Revit rolled the disconnect back";
        }
        catch (Exception ex)
        {
            t.RollBack();
            return $"disconnect failed: {ex.Message}";
        }
    }

    public static void Run(UIDocument uidoc) => Run(uidoc.Document, new RevitPicker(uidoc));

    /// <summary>
    /// Until one pair is disconnected, or Esc. Like Connect and Align it opens
    /// no dialog: a pair that cannot be disconnected says why in the next
    /// prompt, and the next two clicks try again.
    /// </summary>
    internal static void Run(Document doc, IPicker picker)
    {
        ConnectorOwnerFilter filter = new(doc);
        string? lastNote = null;

        while (true)
        {
            filter.Exclude(null);
            string prompt = lastNote is null
                ? "Click the FIRST MEP element"
                : $"Click the FIRST MEP element (last pair: {lastNote}; Esc to quit)";

            Element? elemA = picker.PickElement(prompt, filter).Element;
            if (elemA is null)
                return;

            // Resolve BEFORE excluding, as Connect does: the first element's
            // own insulation must not be pickable as the second.
            elemA = Picking.ResolveHost(doc, elemA);
            filter.Exclude(elemA.Id);

            Element? elemB = picker.PickElement("Click the SECOND MEP element (connected to the first)", filter).Element;
            if (elemB is null)
                return;

            lastNote = DisconnectPair(doc, elemA, Picking.ResolveHost(doc, elemB));
            if (lastNote is null)
                return;
        }
    }
}
