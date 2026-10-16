using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Ribbit.Geometry;
using Ribbit.Native.Revit;

namespace Ribbit.Native.Tools;

/// <summary>
/// Move / Align / Connect engine. Ported from lib/mepalignlib/moveconnect.py.
///
/// Snaps a "free" element (pipe, duct, tray, conduit, fitting, or accessory)
/// onto a fixed target element by matching open connectors — position AND
/// orientation — then joins them. Works for any connector direction,
/// including sloped / angled connectors, since it uses each connector's full
/// 3D coordinate system rather than assuming axis-aligned geometry.
///
/// CONNECTOR CHOICE
///   On the FIXED target, the OPEN connector nearest to the CLICK POINT is
///   used — so the user clicks near the end they want joined. The LOOSE
///   element then joins with whichever of its own open connectors is nearest
///   that chosen end (its click point is deliberately ignored: on a small
///   fitting the connectors are only inches apart, so a body-click would pick
///   an end at random and the fitting would join back-to-front). With no click
///   point available, falls back to the closest open pair.
///
/// SILENT BY DESIGN
///   Nothing here ever opens a dialog. This is a fast pick-pick-pick tool and
///   a mis-click is a normal part of using it, so a modal alert per bad pair
///   costs more than the diagnosis is worth — you would dismiss it and pick
///   again either way. Revit's OWN warnings are suppressed too, via
///   <see cref="SilentTransaction"/>; genuine errors still surface, because
///   swallowing those could corrupt the model.
///
///   A pair that cannot be connected simply rolls back and leaves the model
///   untouched. In Multi mode the reason is carried into the next pick prompt,
///   which is feedback you read without having to dismiss anything.
///
/// NOTES
///   - Only elements with MEP connectors are pickable; insulation/lining picks
///     resolve to their host pipe/duct.
///   - If the mover already has another connection on a different connector,
///     rotating it will drag that connection's geometry too — Revit's
///     flex/curve-follow behavior on that side decides the result.
/// </summary>
internal static class MoveConnectEngine
{
    private static readonly Dictionary<Domain, string> DomainNames = new()
    {
        [Domain.DomainPiping] = "pipe",
        [Domain.DomainHvac] = "duct",
        [Domain.DomainElectrical] = "electrical",
        [Domain.DomainCableTrayConduit] = "tray/conduit",
    };

    // ------------------------------------------------------------- helpers

    private static List<Connector> OpenConnectors(Element elem)
        => MepConnectors.GetOpenConnectors(elem);

    private static Connector RefreshConnector(Document doc, Element elem, Connector oldConn)
        => MepConnectors.RefreshConnector(doc, elem, oldConn);

    private static string DomainName(Connector conn)
        => DomainNames.TryGetValue(conn.Domain, out string? name) ? name : conn.Domain.ToString();

    private static Vec3 OriginOf(Connector c) => c.Origin.ToVec3();

    /// <summary>
    /// A pipe connector and a duct connector are never joinable, however close
    /// they sit. Passed into the pair chooser so incompatible ends are filtered
    /// out of the candidates rather than rejected after one has been picked.
    /// </summary>
    private static bool SameDomain(Connector mover, Connector target)
        => mover.Domain == target.Domain;

    /// <summary>
    /// True only for a cross-section that HAS a rotational orientation worth
    /// matching. Round is symmetric so rolling it is pointless; Invalid means
    /// the connector has no profile at all (electrical).
    /// </summary>
    private static bool HasProfile(Connector conn)
        => conn.Shape is ConnectorProfileType.Rectangular or ConnectorProfileType.Oval;

    /// <summary>Distinct domain names on one side, for the failure message.</summary>
    private static string DomainList(List<Connector> conns)
    {
        List<string> names = [];
        foreach (Connector c in conns)
        {
            string name = DomainName(c);
            if (!names.Contains(name))
                names.Add(name);
        }

        return names.Count == 0 ? "none" : string.Join("/", names);
    }

    /// <summary>
    /// Allows only elements that (after host resolution) carry MEP end
    /// connectors. Cached on "has end connectors at all", which never changes
    /// as pipes get connected — so the memo stays valid all run.
    /// </summary>
    internal sealed class MepConnectorFilter(Document doc) : CachedPickFilter(doc)
    {
        protected override bool Test(Element elem)
            => MepConnectors.GetEndConnectors(Picking.ResolveHost(Doc, elem)).Count > 0;
    }

    // ---------------------------------------------------------------- core

    /// <summary>Returns an error string, or null on success.</summary>
    public static string? MoveAlignConnect(Document doc, Element mover, Element target, XYZ? tClick)
    {
        // The sibling check AlignBranchToReference has always had, and this did
        // not. Without it the pair chooser hands back the SAME connector for
        // both sides, step 1 reads it as anti-parallel to itself and spins the
        // element 180 degrees, and the run only ends when ConnectTo(itself)
        // throws — a rollback with a Revit-worded message instead of a reason.
        //
        // The pick filter now bars this too, so reaching here means a caller
        // other than Run got it wrong. Cheap enough to keep both.
        if (mover.Id == target.Id)
        {
            return "Same element picked twice - note that a pick on insulation or lining "
                   + "counts as the pipe or duct it wraps.";
        }

        List<Connector> mOpen = OpenConnectors(mover);
        List<Connector> tOpen = OpenConnectors(target);
        if (mOpen.Count == 0)
            return "The LOOSE element has no open connectors.";
        if (tOpen.Count == 0)
            return "The FIXED element has no open connectors.";

        var pair = MoveConnectMath.ChoosePair(
            mOpen, tOpen, tClick?.ToVec3(), OriginOf, SameDomain);

        // Both sides were checked non-empty above, so the only way to get here
        // is that nothing on the loose element can join anything on the fixed
        // one. Naming both sets beats "could not choose a connector pair",
        // which left you guessing whether you had mis-clicked.
        if (pair is null)
        {
            return $"No connector pair in the same domain (loose has {DomainList(mOpen)}, "
                   + $"fixed has {DomainList(tOpen)}).";
        }

        Connector mc = pair.Value.Mover;
        Connector tc = pair.Value.Target;

        // --- Step 1: align primary connector axis (anti-parallel), incl. sloped ---
        Vec3 dirM = mc.CoordinateSystem.BasisZ.ToVec3();
        Vec3 dirT = tc.CoordinateSystem.BasisZ.ToVec3();
        (Vec3? axis1, double angle1) = MoveConnectMath.RotationAxisAngle(dirM, dirT.Negate());
        if (axis1 is not null && angle1 > MoveConnectMath.AngleEps)
        {
            XYZ pivot = mc.Origin;
            ElementTransformUtils.RotateElement(
                doc, mover.Id, Line.CreateBound(pivot, pivot.Add(axis1.Value.ToXYZ())), angle1);
            mc = RefreshConnector(doc, mover, mc);
        }

        // --- Step 2: roll alignment for non-round profiles (rect/oval duct) ---
        //
        // Tested POSITIVELY for a profile that actually has an orientation.
        // The old test was "neither is Round", which quietly included Invalid:
        // an electrical connector reports Shape = Invalid rather than throwing
        // (verified against a live model), so every electrical join was rolled
        // about its axis to match a BasisX that means nothing there.
        if (HasProfile(mc) && HasProfile(tc))
        {
            XYZ axisDir = mc.CoordinateSystem.BasisZ;
            Vec3 bxM = mc.CoordinateSystem.BasisX.ToVec3();
            Vec3 bxT = tc.CoordinateSystem.BasisX.ToVec3();
            double angle2 = MoveConnectMath.SignedRollAngle(bxM, bxT, axisDir.ToVec3());
            if (Math.Abs(angle2) > MoveConnectMath.AngleEps)
            {
                XYZ pivot = mc.Origin;
                ElementTransformUtils.RotateElement(
                    doc, mover.Id, Line.CreateBound(pivot, pivot.Add(axisDir)), angle2);
                mc = RefreshConnector(doc, mover, mc);
            }
        }

        // --- Step 3: translate connector origins together ---
        XYZ delta = tc.Origin.Subtract(mc.Origin);
        if (delta.GetLength() > MoveConnectMath.LenEps)
        {
            ElementTransformUtils.MoveElement(doc, mover.Id, delta);
            mc = RefreshConnector(doc, mover, mc);
        }

        // --- Step 4: connect ---
        mc.ConnectTo(tc);
        return null;
    }

    /// <summary>One pair in its own transaction: null when connected, otherwise why not.</summary>
    internal static string? ConnectPair(Document doc, Element moverR, Element targetR, XYZ? tClick)
    {
        // Revit's own warnings are auto-resolved here, so a connect that
        // Revit merely grumbles about does not stop the run either.
        using Transaction t = SilentTransaction.Start(doc, "Move Connect Align");
        try
        {
            string? err = MoveAlignConnect(doc, moverR, targetR, tClick);
            if (err is null)
            {
                // A join Revit rolls back at commit did not happen, whatever it
                // looked like before: never count it as connected.
                return t.Commit() == TransactionStatus.Committed ? null : "Revit rolled the connection back";
            }

            t.RollBack();
            return err;
        }
        catch (Exception ex)
        {
            // Message only, never ToString(): a stack trace is not
            // something to put in front of someone mid-pick.
            t.RollBack();
            return ex.Message;
        }
    }

    // --------------------------------------------------------------- entry

    /// <summary>
    /// multiple=false: until one pair is connected, then exit on its own (no
    ///                 Esc needed). A pair that could not be connected asks
    ///                 again, saying why - it used to end the command without
    ///                 a word, which read exactly like a dead click.
    /// multiple=true : keep going until Esc.
    /// </summary>
    public static void Run(UIDocument uidoc, bool multiple)
        => Run(uidoc.Document, new RevitPicker(uidoc), multiple);

    /// <summary>The pick loop, on any picker. Returns how many pairs were connected.</summary>
    internal static int Run(Document doc, IPicker picker, bool multiple)
    {
        MepConnectorFilter selFilter = new(doc);

        int connected = 0;
        string? lastNote = null;

        while (true)
        {
            // The only feedback channel that costs the user nothing: they are
            // already reading this prompt to make the next pick.
            string prompt = multiple
                ? $"Pick the FIXED target element ({connected} connected"
                  + (lastNote is null ? "" : $"; last: {lastNote}")
                  + ", Esc to finish)"
                : lastNote is null
                    ? "Pick the FIXED target element"
                    : $"Pick the FIXED target element (last pair: {lastNote}; Esc to quit)";

            selFilter.Exclude(null);
            (Element? target, XYZ? tClick) = picker.PickElement(prompt, selFilter);
            if (target is null)
                break;   // Esc exits immediately

            // Resolve BEFORE excluding: barring the raw pick would still leave
            // the target's own insulation pickable as the mover, and both would
            // resolve back to the same element.
            Element targetR = Picking.ResolveHost(doc, target);
            selFilter.Exclude(targetR.Id);

            (Element? mover, _) = picker.PickElement(
                "Pick the LOOSE element to move (Esc to exit)", selFilter);
            if (mover is null)
                break;   // Esc exits immediately - no second press needed

            Element moverR = Picking.ResolveHost(doc, mover);

            string? err = ConnectPair(doc, moverR, targetR, tClick);
            if (err is null)
            {
                connected++;
                lastNote = null;
            }
            else
            {
                lastNote = err;
            }

            if (!multiple && err is null)
                break;
        }

        return connected;
    }
}
