using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using Ribbit.Native.Revit;

namespace Ribbit.Native.Tools;

/// <summary>
/// The model-writing steps Sprig Connect and Drop Connect share: split the
/// branch line, make a pipe, put a tee or an elbow on it, join the head - each
/// step proving it actually joined, and each failure phrased for the user.
///
/// These lived twice, as near-identical copies in SprigBuilder and DropBuilder
/// (about 200 lines each). A fix to one - like the crowded-tee message below -
/// had to be remembered in the other. One copy now.
///
/// CONNECTOR STALENESS — every connector is re-fetched by position immediately
/// before it is used; a Pipe.Create or a regen in between makes old ones stale.
/// </summary>
internal static class BranchBuild
{
    /// <summary>Tolerance for matching a fresh connector to a target point (ft, ~1/8").</summary>
    public const double ConnMatchTol = 0.01;

    /// <summary>How close to a pipe's centreline a point must be to count as "on" it.</summary>
    public const double OnLineTol = 0.02;

    /// <summary>
    /// Two tees closer than this on one branch line very likely cannot both be
    /// built: the first tee's body (and the pipe trimmed back to it) covers the
    /// second's point. Used only to EXPLAIN a refusal, never to refuse.
    /// </summary>
    public const double CrowdedTeeFt = 300.0 / 304.8;

    internal sealed record PipeContext(ElementId SystemTypeId, ElementId PipeTypeId, ElementId LevelId);

    /// <summary>
    /// System type, pipe type and level, captured from the INTACT branch pipe:
    /// BreakCurve hands back pieces whose MEPSystem is null (documented Revit
    /// behaviour), so this must be read once, before the first break.
    /// </summary>
    public static PipeContext ContextOf(Element original)
        => new(SprigEngine.GetSystemTypeId(original), original.GetTypeId(), SprigEngine.GetLevelId(original));

    public static WarningSwallower ApplyFailureHandling(Transaction transaction)
    {
        WarningSwallower swallower = new();
        FailureHandlingOptions opts = transaction.GetFailureHandlingOptions();
        opts.SetFailuresPreprocessor(swallower);
        transaction.SetFailureHandlingOptions(opts);
        return swallower;
    }

    public static Connector? ClosestOpenConnector(Element elem, XYZ point, double tol = ConnMatchTol)
    {
        Connector? best = MepConnectors.ClosestOpenConnector(elem, point);
        return best is not null && best.Origin.DistanceTo(point) <= tol ? best : null;
    }

    /// <summary>On the segment AND strictly between its ends, with room to break on both sides.</summary>
    public static bool PointInsideLine(Line line, XYZ point, double tol = OnLineTol)
    {
        XYZ p0 = line.GetEndPoint(0);
        XYZ vec = line.GetEndPoint(1).Subtract(p0);
        double length = vec.GetLength();
        if (length < tol)
            return false;

        XYZ direction = vec.Normalize();
        double t = point.Subtract(p0).DotProduct(direction);
        if (t < tol || t > length - tol)
            return false;

        return p0.Add(direction.Multiply(t)).DistanceTo(point) <= tol;
    }

    /// <summary>
    /// Which live piece of one original branch segment contains the point.
    /// Geometry, never id: every BreakCurve turns one pipe into two.
    /// </summary>
    public static Element? FindHost(Document doc, IEnumerable<ElementId> liveIds, XYZ point)
    {
        foreach (ElementId eid in liveIds)
        {
            Element? elem = doc.GetElement(eid);
            Line? line = elem is null ? null : ElementGeometry.GetLine(elem);
            if (line is not null && PointInsideLine(line, point))
                return elem;
        }
        return null;
    }

    /// <summary>Which live piece still has an OPEN end at the point (an end bend's root).</summary>
    public static Element? FindHostWithEnd(Document doc, IEnumerable<ElementId> liveIds, XYZ point)
    {
        foreach (ElementId eid in liveIds)
        {
            Element? elem = doc.GetElement(eid);
            if (elem is not null && ClosestOpenConnector(elem, point) is not null)
                return elem;
        }
        return null;
    }

    /// <summary>Pipe.Create takes the type's default size; 0 means "leave it".</summary>
    private static void SetDiameter(Element pipe, double diameterFt)
    {
        if (diameterFt <= 0.0)
            return;

        Parameter? p = pipe.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM);
        if (p is not null && !p.IsReadOnly)
            p.Set(diameterFt);
    }

    public static Pipe CreatePipe(Document doc, PipeContext ctx, XYZ p0, XYZ p1, double diameterFt, string what)
    {
        Pipe pipe;
        try
        {
            pipe = Pipe.Create(doc, ctx.SystemTypeId, ctx.PipeTypeId, ctx.LevelId, p0, p1);
        }
        catch (Exception ex)
        {
            throw new BuildError($"failed to create the {what} ({ex.Message})");
        }

        // One regen: it stands between the create/resize and the caller reading
        // this pipe's connectors.
        SetDiameter(pipe, diameterFt);
        doc.Regenerate();
        return pipe;
    }

    /// <summary>Splits the branch at the tee point; returns the new second piece.</summary>
    public static Element BreakBranch(Document doc, Element hostPipe, XYZ teePt)
    {
        ElementId newId;
        try
        {
            newId = PlumbingUtils.BreakCurve(doc, hostPipe.Id, teePt);
        }
        catch (Exception ex)
        {
            throw new BuildError($"failed to split the branch line at the tee point ({ex.Message})");
        }

        doc.Regenerate();
        return doc.GetElement(newId) ?? throw new BuildError("splitting the branch line produced no second segment");
    }

    /// <summary>The two open connectors facing each other across the break, read fresh.</summary>
    public static (Connector A, Connector B) BreakConnectors(Element pieceA, Element pieceB, XYZ teePt)
    {
        Connector? connA = ClosestOpenConnector(pieceA, teePt);
        Connector? connB = ClosestOpenConnector(pieceB, teePt);
        if (connA is null || connB is null)
            throw new BuildError("could not find the open connectors either side of the break in the branch line");
        return (connA, connB);
    }

    private static bool HasAnyConnection(Element elem)
        => MepConnectors.GetEndConnectors(elem).Any(c => c.IsConnected);

    private static bool IsConnectedAt(Element elem, XYZ point, double tol = ConnMatchTol)
        => MepConnectors.GetEndConnectors(elem).Any(c => c.Origin.DistanceTo(point) <= tol && c.IsConnected);

    /// <summary>
    /// Puts the tee on the branch. <paramref name="nearestOtherTeeFt"/> is how far
    /// the nearest OTHER tee planned on this branch line sits, if any.
    ///
    /// A refusal used to be blamed, always, on the pipe type's routing
    /// preferences ("a reducing tee is needed..."). The in-Revit suite showed the
    /// commonest real cause is two heads whose tees sit too close to fit - which
    /// the dry run had already warned about as "minimum spacing" - so that is
    /// what it says now when it applies.
    /// </summary>
    public static void PlaceTee(
        Document doc, Connector connA, Connector connB, Connector connBranch, string takeoff, double? nearestOtherTeeFt)
    {
        Element branchElem = connBranch.Owner;
        try
        {
            doc.Create.NewTeeFitting(connA, connB, connBranch);
        }
        catch (Exception ex)
        {
            if (nearestOtherTeeFt is double gap && gap < CrowdedTeeFt)
            {
                throw new BuildError(
                    $"failed to create the tee on the branch line ({ex.Message}). Its tee sits only "
                    + $"{gap * 304.8:F0} mm from another head's tee - too close for two tee fittings (the "
                    + "dry run flagged this as 'minimum spacing'). Move one of the two heads apart, or "
                    + "leave it out of the pick and pipe it by hand");
            }

            throw new BuildError(
                $"failed to create the tee on the branch line ({ex.Message}). Check that the branch "
                + "line's pipe type has a suitable tee in its routing preferences - a reducing tee is "
                + $"needed when the {takeoff} is smaller than the branch");
        }
        doc.Regenerate();

        // By CONNECTION STATE, not position: a fitting trims the adjoining pipe to
        // make room for its body, so the takeoff's connector can legitimately end
        // up a few mm from where it was. It had no connection before this call.
        if (!HasAnyConnection(branchElem))
            throw new BuildError($"the tee was placed but did NOT connect to the {takeoff} - they are touching, not joined");
    }

    public static void PlaceElbow(Document doc, Connector conn1, Connector conn2, string where)
    {
        Element elem2 = conn2.Owner;
        try
        {
            doc.Create.NewElbowFitting(conn1, conn2);
        }
        catch (Exception ex)
        {
            throw new BuildError(
                $"failed to create the elbow at the {where} ({ex.Message}). Check that the pipe type has "
                + "a matching elbow in its routing preferences");
        }
        doc.Regenerate();

        if (!HasAnyConnection(elem2))
            throw new BuildError($"the elbow at the {where} was placed but did NOT connect - the pipes are touching, not joined");
    }

    /// <summary>
    /// Joins the new pipe's end to the head's inlet (coincident by
    /// construction), then PROVES it took: a pipe that merely touches the head
    /// looks identical in 3D but carries no flow and joins no system.
    /// </summary>
    public static void ConnectHead(Document doc, Element pipe, Element headElem, XYZ headPt, string pipeWord, string pipeEnd)
    {
        Connector? pipeEndConn = ClosestOpenConnector(pipe, headPt);
        Connector? headConn = ClosestOpenConnector(headElem, headPt);

        if (pipeEndConn is null)
            throw new BuildError($"could not find the open connector at the {pipeEnd} of the new {pipeWord}");
        if (headConn is null)
            throw new BuildError("the head's inlet connector is no longer open at the expected point");

        try
        {
            pipeEndConn.ConnectTo(headConn);
        }
        catch (Exception ex)
        {
            throw new BuildError($"failed to connect the {pipeWord} to the head ({ex.Message})");
        }
        doc.Regenerate();

        if (!IsConnectedAt(headElem, headPt))
            throw new BuildError($"the {pipeWord} was placed but did NOT connect to the head - they are touching, not joined");
    }

    /// <summary>
    /// How far the nearest OTHER planned tee on the same original branch segment
    /// sits from this one; null when it is alone there.
    /// </summary>
    public static double? NearestOtherTee(XYZ teePt, IEnumerable<XYZ> otherTeePts)
    {
        double? best = null;
        foreach (XYZ p in otherTeePts)
        {
            double d = p.DistanceTo(teePt);
            if (d > 1.0e-9 && (best is null || d < best))
                best = d;
        }
        return best;
    }
}
