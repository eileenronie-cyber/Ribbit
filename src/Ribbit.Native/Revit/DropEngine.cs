using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using Ribbit.Geometry;
using Ribbit.Native.Settings;
// Autodesk.Revit.DB also has a Units type; ours wins inside this file.
using Units = Ribbit.Geometry.Units;

namespace Ribbit.Native.Revit;

/// <summary>
/// Drop Connect — Revit-side reading. Ported from the read half of
/// lib/droplib/engine.py.
///
/// READ-ONLY. Shares picking and element-describing with
/// <see cref="SprigEngine"/>, which is the same code in the Python too; what
/// differs here is that a drop segment also needs the branch's real diameter
/// (for the pipe-through-pipe clearance checks) and which of its ends are OPEN
/// (only an open end can take an END BEND).
/// </summary>
internal static class DropEngine
{
    internal sealed record HeadInfo(
        string Desc,
        string Category,
        bool Pendent,
        bool IsSprinkler,
        double DiameterFt,
        string DiameterSource);

    internal sealed record MainRead(
        List<DropSegment> Segments,
        List<SprigEngine.Rejected> RejectedPicks);

    internal sealed record HeadRead(
        List<(long HeadId, Vec3 HeadPt, double Dia)> Heads,
        List<SprigEngine.Rejected> RejectedPicks,
        Dictionary<long, HeadInfo> Info,
        Dictionary<long, Element> Elements);

    /// <summary>
    /// Nominal diameter in internal feet, or 0 when it can't be read. Feeds the
    /// solver's pipe-through-pipe clearance checks, which skip themselves on 0
    /// rather than guess.
    /// </summary>
    public static double GetDiameter(Element elem)
    {
        Parameter? p = elem.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM);
        if (p is not null)
        {
            double val = p.AsDouble();
            if (val > 0.0)
                return val;
        }

        if (elem is Pipe pipe)
        {
            try
            {
                if (pipe.Diameter > 0.0)
                    return pipe.Diameter;
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException)
            {
                // Unreadable - fall through to 0, which disables the checks.
            }
        }

        return 0.0;
    }

    /// <summary>
    /// Whether each axis end has a FREE connector. An END BEND can only elbow
    /// onto an end that is actually open, so the solver needs to know which ends
    /// are free. Matched by position: an open connector within tol of p0/p1
    /// marks that end open.
    /// </summary>
    public static (bool OpenP0, bool OpenP1) OpenEnds(
        Element elem, XYZ p0, XYZ p1, double tol = 0.02)
    {
        bool openP0 = false;
        bool openP1 = false;

        foreach (Connector c in MepConnectors.GetOpenConnectors(elem))
        {
            XYZ o = c.Origin;
            if (o.DistanceTo(p0) <= tol)
                openP0 = true;
            else if (o.DistanceTo(p1) <= tol)
                openP1 = true;
        }

        return (openP0, openP1);
    }

    public static MainRead BuildMainSegments(Document doc, IEnumerable<Element> elements)
    {
        List<DropSegment> segments = [];
        List<SprigEngine.Rejected> rejected = [];

        foreach (Element raw in elements)
        {
            Element elem = Picking.ResolveHost(doc, raw);
            string what = SprigEngine.DescribeElem(elem);

            string? reject = RejectReason(elem);
            if (reject is not null)
            {
                rejected.Add(new SprigEngine.Rejected(elem.Id, reject));
                continue;
            }

            (Vec3? p0, Vec3? p1, string source) = SprigEngine.GetAxisPoints(elem);
            if (p0 is null || p1 is null)
            {
                rejected.Add(new SprigEngine.Rejected(elem.Id, $"{what} - {source}"));
                continue;
            }

            (bool openP0, bool openP1) = OpenEnds(elem, p0.Value.ToXYZ(), p1.Value.ToXYZ());

            DropSegment seg = new(
                RevitCompat.IdKey(elem.Id), p0.Value, p1.Value,
                desc: $"{what} (axis from {source})",
                isPipe: elem is Pipe,
                diameter: GetDiameter(elem),
                openP0: openP0,
                openP1: openP1);

            if (seg.Length < DropSolver.LenEps)
            {
                rejected.Add(new SprigEngine.Rejected(elem.Id, $"{what} - zero length"));
                continue;
            }
            if (seg.IsVertical)
            {
                rejected.Add(new SprigEngine.Rejected(elem.Id,
                    $"{what} - vertical; a riser can't take a plan-projected drop"));
                continue;
            }

            segments.Add(seg);
        }

        return new MainRead(segments, rejected);
    }

    /// <summary>
    /// Heads are (id, connector origin, connector diameter) — the connector
    /// origin, not the family origin, because that is exactly where the bottom
    /// of the drop has to land.
    /// </summary>
    public static HeadRead BuildHeads(Document doc, IEnumerable<Element> elements)
    {
        List<(long, Vec3, double)> heads = [];
        List<SprigEngine.Rejected> rejected = [];
        Dictionary<long, HeadInfo> info = [];
        Dictionary<long, Element> elems = [];

        foreach (Element raw in elements)
        {
            Element elem = Picking.ResolveHost(doc, raw);
            string what = SprigEngine.DescribeElem(elem);

            string? reject = LinkRejectOnly(elem);
            if (reject is not null)
            {
                rejected.Add(new SprigEngine.Rejected(elem.Id, reject));
                continue;
            }

            List<Connector> allConns = MepConnectors.GetEndConnectors(elem);
            List<Connector> conns = allConns.FindAll(c => !c.IsConnected);

            if (allConns.Count == 0)
            {
                rejected.Add(new SprigEngine.Rejected(elem.Id,
                    $"{what} - no connectors at all, so it can't be piped up to"));
                continue;
            }
            if (conns.Count == 0)
            {
                rejected.Add(new SprigEngine.Rejected(elem.Id,
                    $"{what} - all {allConns.Count} connector(s) are already connected; this head "
                    + "looks piped up already"));
                continue;
            }
            if (conns.Count > 1)
            {
                rejected.Add(new SprigEngine.Rejected(elem.Id,
                    $"{what} - {conns.Count} open connectors; can't tell which one feeds it"));
                continue;
            }

            Connector conn = conns[0];
            long headId = RevitCompat.IdKey(elem.Id);
            (double diaFt, string diaSource) = ConnectorDiameter(conn);

            heads.Add((headId, conn.Origin.ToVec3(), diaFt));
            elems[headId] = elem;

            // A connector's BasisZ points OUT of the element, so a PENDENT
            // head's inlet faces UP - the drop comes down onto it. Recorded,
            // not enforced - families vary. (This is the opposite sense from
            // SprigConnect's upright check.)
            bool facingUp;
            try
            {
                facingUp = conn.CoordinateSystem.BasisZ.Z > 0.5;
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException)
            {
                facingUp = true;
            }

            info[headId] = new HeadInfo(
                what, SprigEngine.CategoryName(elem), facingUp,
                SprigEngine.IsSprinkler(elem), diaFt, diaSource);
        }

        return new HeadRead(heads, rejected, info, elems);
    }

    /// <summary>
    /// (diameter, source) for the pipe that must run down to this head. The
    /// riser, armover and drop all have to match what the head takes, which is a
    /// fact of the model, not a preference.
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

        return (DropSettings.FallbackDiameterMm / Units.MmPerFt,
                $"fallback ({DropSettings.FallbackDiameterMm:F0} mm)");
    }

    private static string? LinkRejectOnly(Element elem)
        => elem is RevitLinkInstance
            ? $"{SprigEngine.DescribeElem(elem)} - this is a LINKED MODEL instance, not an element "
              + "inside it. Picking through a link returns the link itself; Drop Connect reads and "
              + "writes host-model elements only."
            : null;

    private static string? RejectReason(Element elem)
    {
        string? link = LinkRejectOnly(elem);
        if (link is not null)
            return link;

        if (elem.Category is null)
            return null;

        long catId = RevitCompat.IdKey(elem.Category.Id);
        if (catId == (long)BuiltInCategory.OST_PipeFitting)
        {
            return $"{SprigEngine.DescribeElem(elem)} - this is a pipe FITTING, not a length of "
                   + "pipe. Pick the straight pipe segments between the fittings instead; the tool "
                   + "inserts its own tees.";
        }
        if (catId == (long)BuiltInCategory.OST_PipeAccessory)
        {
            return $"{SprigEngine.DescribeElem(elem)} - this is a pipe ACCESSORY (valve, etc.), "
                   + "not a length of pipe. Pick the straight pipe segments instead.";
        }
        return null;
    }
}
