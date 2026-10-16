using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using Ribbit.Geometry;
using Ribbit.Native.Settings;
using OperationCanceledException = Autodesk.Revit.Exceptions.OperationCanceledException;
// Autodesk.Revit.DB also has a Units type; ours wins inside this file.
using Units = Ribbit.Geometry.Units;

namespace Ribbit.Native.Revit;

/// <summary>
/// Sprig Connect — Revit-side picking and reading. Ported from
/// lib/spriglib/engine.py (its reporting half lives in SprigReport).
///
/// READ-ONLY. This never opens a Transaction and never changes the model.
///
/// TERMINOLOGY: "main" throughout means "the pipe the heads tee off" — which in
/// sprinkler terms is the BRANCH LINE, not the cross main. All user-facing text
/// says "branch line"; the internal names kept "main" because that is what the
/// geometry is relative to.
///
/// WHAT COUNTS AS A VALID PICK — deliberately capability-based, not
/// class/category-based:
///   main : anything with a straight axis
///   head : anything with exactly one OPEN connector
/// What the element actually IS gets reported rather than used to reject, so a
/// fabrication-part main or a sprinkler in an unexpected category still solves,
/// flagged. The one thing still worth rejecting outright is a linked-model
/// instance, since nothing can be read or written through it.
/// </summary>
internal static class SprigEngine
{
    internal sealed record HeadInfo(
        string Desc,
        string Category,
        bool Upright,
        bool IsSprinkler,
        double DiameterFt,
        string DiameterSource);

    internal sealed record Rejected(ElementId Id, string Why);

    internal sealed record MainRead(
        List<MainSegment> Segments,
        List<Rejected> RejectedPicks);

    internal sealed record HeadRead(
        List<(long HeadId, Vec3 HeadPt)> Heads,
        List<Rejected> RejectedPicks,
        Dictionary<long, HeadInfo> Info,
        Dictionary<long, Element> Elements);

    // ------------------------------------------------------------------ basics

    public static string TypeName(Element elem) => elem.GetType().Name;

    public static string CategoryName(Element elem) => elem.Category?.Name ?? "no category";

    /// <summary>
    /// Class name + category — so a rejection reports what the thing IS, not
    /// merely what it isn't. Every reject message goes through here.
    /// </summary>
    public static string DescribeElem(Element elem) => $"{TypeName(elem)} / {CategoryName(elem)}";

    /// <summary>
    /// (p0, p1, source) for the element's straight axis, or (null, null, reason).
    ///
    /// LocationCurve first. Fabrication parts — which is what a grooved
    /// sprinkler main usually is — are placed by POINT and carry no
    /// LocationCurve at all, so fall back to a pair of End connectors, which
    /// define exactly the same axis.
    /// </summary>
    public static (Vec3? P0, Vec3? P1, string Source) GetAxisPoints(Element elem)
    {
        Line? line = ElementGeometry.GetLine(elem);
        if (line is not null)
            return (line.GetEndPoint(0).ToVec3(), line.GetEndPoint(1).ToVec3(), "LocationCurve");

        List<Connector> conns = MepConnectors.GetEndConnectors(elem);
        if (conns.Count == 2)
            return (conns[0].Origin.ToVec3(), conns[1].Origin.ToVec3(), "2 end connectors");

        return (null, null,
            $"no LocationCurve and {conns.Count} end connector(s) - need a straight curve or "
            + "exactly 2 connectors to get an axis");
    }

    /// <summary>Reference level, for Pipe.Create.</summary>
    public static ElementId GetLevelId(Element pipe)
    {
        if (pipe is MEPCurve curve && curve.ReferenceLevel is not null)
            return curve.ReferenceLevel.Id;

        Parameter? p = pipe.get_Parameter(BuiltInParameter.RBS_START_LEVEL_PARAM);
        return p?.AsElementId() ?? ElementId.InvalidElementId;
    }

    /// <summary>
    /// Piping system type for Pipe.Create. MEPSystem first, falling back to the
    /// type parameter — a pipe that isn't in a connected system has no
    /// MEPSystem, and an unguarded .MEPSystem.GetTypeId() throws on it. The
    /// fallback also matters after BreakCurve, which returns pieces with a null
    /// MEPSystem.
    /// </summary>
    public static ElementId GetSystemTypeId(Element pipe)
    {
        if (pipe is MEPCurve curve && curve.MEPSystem is not null)
        {
            try
            {
                return curve.MEPSystem.GetTypeId();
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException)
            {
                // Fall through to the type parameter.
            }
        }

        Parameter? p = pipe.get_Parameter(BuiltInParameter.RBS_PIPING_SYSTEM_TYPE_PARAM);
        return p?.AsElementId() ?? ElementId.InvalidElementId;
    }

    /// <summary>Reported, never used to reject.</summary>
    public static bool IsSprinkler(Element elem)
        => elem.Category is not null
           && RevitCompat.IdKey(elem.Category.Id) == (long)BuiltInCategory.OST_Sprinklers;

    private static bool CategoryIs(Element elem, BuiltInCategory bic)
        => elem.Category is not null && RevitCompat.IdKey(elem.Category.Id) == (long)bic;

    /// <summary>
    /// A fitting has two end connectors, so the connector-axis fallback would
    /// happily accept a coupling as a 2-inch "main segment". Catch it first and
    /// say what to pick instead.
    /// </summary>
    private static string? FittingRejectMessage(Element elem)
    {
        if (CategoryIs(elem, BuiltInCategory.OST_PipeFitting))
        {
            return $"{DescribeElem(elem)} - this is a pipe FITTING, not a length of pipe. Pick "
                   + "the straight pipe segments between the fittings instead; the tool inserts "
                   + "its own tees.";
        }
        if (CategoryIs(elem, BuiltInCategory.OST_PipeAccessory))
        {
            return $"{DescribeElem(elem)} - this is a pipe ACCESSORY (valve, etc.), not a length "
                   + "of pipe. Pick the straight pipe segments instead.";
        }
        return null;
    }

    /// <summary>
    /// PickObjects on something inside a linked model hands back the LINK
    /// INSTANCE, not the element inside it — the single most likely reason an
    /// entire pick comes back unusable.
    /// </summary>
    private static string? LinkRejectMessage(Element elem)
        => elem is RevitLinkInstance
            ? $"{DescribeElem(elem)} - this is a LINKED MODEL instance, not an element inside it. "
              + "Picking through a link returns the link itself; Sprig Connect reads and writes "
              + "host-model elements only."
            : null;

    // ----------------------------------------------------------------- picking

    /// <summary>Returns the picked elements, or null if the user pressed Esc.</summary>
    public static List<Element>? PickMultiple(UIDocument uidoc, string prompt)
    {
        try
        {
            IList<Reference> refs = uidoc.Selection.PickObjects(ObjectType.Element, prompt);
            return [.. refs.Select(r => uidoc.Document.GetElement(r.ElementId))];
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    // ----------------------------------------------------------------- reading

    /// <summary>
    /// Accepts anything with a straight axis. Whether it is really a Pipe is
    /// recorded on the segment for the report, because only the builder needs
    /// Pipe-only APIs — the geometry here does not.
    /// </summary>
    public static MainRead BuildMainSegments(Document doc, IEnumerable<Element> elements)
    {
        List<MainSegment> segments = [];
        List<Rejected> rejected = [];

        foreach (Element raw in elements)
        {
            Element elem = Picking.ResolveHost(doc, raw);
            string what = DescribeElem(elem);

            string? linkMsg = LinkRejectMessage(elem);
            if (linkMsg is not null)
            {
                rejected.Add(new Rejected(elem.Id, linkMsg));
                continue;
            }

            string? fittingMsg = FittingRejectMessage(elem);
            if (fittingMsg is not null)
            {
                rejected.Add(new Rejected(elem.Id, fittingMsg));
                continue;
            }

            (Vec3? p0, Vec3? p1, string source) = GetAxisPoints(elem);
            if (p0 is null || p1 is null)
            {
                rejected.Add(new Rejected(elem.Id, $"{what} - {source}"));
                continue;
            }

            MainSegment seg = new(
                RevitCompat.IdKey(elem.Id), p0.Value, p1.Value,
                desc: $"{what} (axis from {source})",
                isPipe: elem is Pipe);

            if (seg.Length < SprigSolver.LenEps)
            {
                rejected.Add(new Rejected(elem.Id, $"{what} - zero length"));
                continue;
            }
            if (seg.IsVertical)
            {
                rejected.Add(new Rejected(elem.Id,
                    $"{what} - vertical; a riser can't take a plan-projected sprig"));
                continue;
            }

            segments.Add(seg);
        }

        return new MainRead(segments, rejected);
    }

    /// <summary>
    /// Heads are (id, connector origin) — the connector origin, not the family
    /// origin, because that is exactly where the top of the sprig has to land.
    /// </summary>
    public static HeadRead BuildHeads(Document doc, IEnumerable<Element> elements)
    {
        List<(long, Vec3)> heads = [];
        List<Rejected> rejected = [];
        Dictionary<long, HeadInfo> info = [];
        Dictionary<long, Element> elems = [];

        foreach (Element raw in elements)
        {
            Element elem = Picking.ResolveHost(doc, raw);
            string what = DescribeElem(elem);

            string? linkMsg = LinkRejectMessage(elem);
            if (linkMsg is not null)
            {
                rejected.Add(new Rejected(elem.Id, linkMsg));
                continue;
            }

            List<Connector> allConns = MepConnectors.GetEndConnectors(elem);
            List<Connector> conns = allConns.FindAll(c => !c.IsConnected);

            if (allConns.Count == 0)
            {
                rejected.Add(new Rejected(elem.Id,
                    $"{what} - no connectors at all, so it can't be piped up to"));
                continue;
            }
            if (conns.Count == 0)
            {
                rejected.Add(new Rejected(elem.Id,
                    $"{what} - all {allConns.Count} connector(s) are already connected; this "
                    + "head looks piped up already"));
                continue;
            }
            if (conns.Count > 1)
            {
                rejected.Add(new Rejected(elem.Id,
                    $"{what} - {conns.Count} open connectors; can't tell which one feeds it"));
                continue;
            }

            Connector conn = conns[0];
            long headId = RevitCompat.IdKey(elem.Id);
            heads.Add((headId, conn.Origin.ToVec3()));
            elems[headId] = elem;

            // A connector's BasisZ points OUT of the element, so an upright
            // head's inlet faces down. Recorded, not enforced - families vary.
            bool facingDown;
            try
            {
                facingDown = conn.CoordinateSystem.BasisZ.Z < -0.5;
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException)
            {
                facingDown = true;
            }

            (double diaFt, string diaSource) = ConnectorDiameter(conn);

            info[headId] = new HeadInfo(
                what, CategoryName(elem), facingDown, IsSprinkler(elem), diaFt, diaSource);
        }

        return new HeadRead(heads, rejected, info, elems);
    }

    /// <summary>
    /// (diameter, source) for the pipe that must run up to this head.
    ///
    /// Read from the head's own inlet connector rather than asked for: the sprig
    /// has to match what the head takes, which is a fact of the model, not a
    /// preference. Round connectors expose Radius; a non-round or unreadable one
    /// falls back to the constant in settings.
    /// </summary>
    public static (double DiameterFt, string Source) ConnectorDiameter(Connector conn)
    {
        try
        {
            double radius = conn.Radius;
            if (radius > 0.0)
                return (radius * 2.0, "head connector");
        }
        catch (Exception)
        {
            // Deliberately broad, matching the Python. Connector.Radius is only
            // meaningful on a ROUND connector; on a rectangular or oval one it
            // throws, and which exception type it throws is not something to bet
            // an unhandled crash on. Falling back is always safe here.
        }

        return (SprigSettings.FallbackDiameterMm / Units.MmPerFt,
                $"fallback ({SprigSettings.FallbackDiameterMm:F0} mm)");
    }
}
