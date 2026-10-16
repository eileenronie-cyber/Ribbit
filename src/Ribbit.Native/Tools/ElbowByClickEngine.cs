using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using Ribbit.Geometry;
using Ribbit.Native.Revit;
using Ribbit.Native.Ui;

namespace Ribbit.Native.Tools;

/// <summary>
/// Elbow By Click. Ported from
/// _Ribbit.tab/Tools.panel/ElbowByClick.pushbutton/script.py.
///
/// Pick the pipe, duct, cable tray, or conduit that dead-ends where you want to
/// turn, then click near it in the active view, toward the side you want it to
/// bend. A new segment of the same type is created that way and an elbow
/// fitting joins it to the original dead end.
///
/// All the direction math lives in <see cref="ElbowMath"/> (Revit-free and
/// unit-tested); this file is only the parts that need a Document — element
/// creation, sizing, insulation, and the temporary pick plane.
/// </summary>
internal static class ElbowByClickEngine
{
    /// <summary>Feet — minimum length of the new segment before the elbow.</summary>
    private const double DefaultSegmentLength = 1.0;

    /// <summary>
    /// New segment is at least this many widths/diameters long — generous on
    /// purpose: some fitting families (cable tray elbows especially) need more
    /// clearance than a small multiple to physically fit, and that failure shows
    /// up as a native Revit dialog our code can't catch or suppress.
    /// </summary>
    private const double SegmentSizeMultiplier = 6.0;

    /// <summary>
    /// How many times to grow the length if the fitting won't fit — kept modest
    /// since it compounds on the already-generous multiplier above (worst case
    /// 6 * 1.5^3 ~= 20x, not 6 * 2^4 = 96x). Only helps whichever failures DO
    /// surface as a catchable exception rather than a native Revit dialog.
    /// </summary>
    private const int LengthRetryAttempts = 3;

    private const double LengthRetryMultiplier = 1.5;

    /// <summary>
    /// Click this close to the dead end (as a fraction of the view's visible
    /// extent) and no direction is taken from the click at all — the fallback
    /// menu opens instead. Measured off the view extent rather than as a fixed
    /// distance so the zone stays the same apparent size on screen at any zoom.
    /// </summary>
    private const double DeadZoneViewFraction = 0.03;

    /// <summary>Fallback dead-zone radius when the view's zoom extent can't be read.</summary>
    private const double DeadZoneSizeMultiplier = 1.5;

    private const string DialogTitle = "Elbow By Click";

    // ------------------------------------------------------------ size / type

    /// <summary>
    /// Which size parameters could actually apply to this element's type — a
    /// Pipe only ever has RBS_PIPE_DIAMETER_PARAM, never the cable tray or
    /// conduit ones. Narrowing avoids spending a Revit API call per element on
    /// parameters that could never exist on it.
    /// </summary>
    private static BuiltInParameter[] SizeParamsFor(Element elem) => elem switch
    {
        Pipe => [BuiltInParameter.RBS_PIPE_DIAMETER_PARAM],
        Duct =>
        [
            BuiltInParameter.RBS_CURVE_DIAMETER_PARAM,
            BuiltInParameter.RBS_CURVE_WIDTH_PARAM,
            BuiltInParameter.RBS_CURVE_HEIGHT_PARAM,
        ],
        Conduit => [BuiltInParameter.RBS_CONDUIT_DIAMETER_PARAM],
        CableTray =>
        [
            BuiltInParameter.RBS_CABLETRAY_WIDTH_PARAM,
            BuiltInParameter.RBS_CABLETRAY_HEIGHT_PARAM,
        ],
        _ =>
        [
            BuiltInParameter.RBS_PIPE_DIAMETER_PARAM,
            BuiltInParameter.RBS_CURVE_DIAMETER_PARAM,
            BuiltInParameter.RBS_CURVE_WIDTH_PARAM,
            BuiltInParameter.RBS_CURVE_HEIGHT_PARAM,
            BuiltInParameter.RBS_CONDUIT_DIAMETER_PARAM,
            BuiltInParameter.RBS_CABLETRAY_WIDTH_PARAM,
            BuiltInParameter.RBS_CABLETRAY_HEIGHT_PARAM,
        ],
    };

    /// <summary>
    /// Copies over whichever size parameter(s) apply so the new segment's
    /// connector matches the original's — required for NewElbowFitting to join
    /// them cleanly.
    /// </summary>
    private static void MatchSize(Element source, Element target)
    {
        foreach (BuiltInParameter bip in SizeParamsFor(source))
        {
            Parameter? sp = source.get_Parameter(bip);
            Parameter? tp = target.get_Parameter(bip);
            if (sp is not null && tp is not null && sp.HasValue && !tp.IsReadOnly)
                tp.Set(sp.AsDouble());
        }
    }

    /// <summary>
    /// Largest applicable size parameter — used to scale the new segment's
    /// length so it's proportional to the element's own size. A flat length that
    /// looks fine on a 1" pipe is too short on a wide duct or cable tray.
    /// </summary>
    private static double CharacteristicSize(Element elem)
    {
        double size = 0.0;
        foreach (BuiltInParameter bip in SizeParamsFor(elem))
        {
            Parameter? p = elem.get_Parameter(bip);
            if (p is not null && p.HasValue)
                size = Math.Max(size, p.AsDouble());
        }
        return size;
    }

    private static ElementId GetLevelId(Element elem)
    {
        if (elem is MEPCurve curve && curve.ReferenceLevel is not null)
            return curve.ReferenceLevel.Id;

        Parameter? p = elem.get_Parameter(BuiltInParameter.RBS_START_LEVEL_PARAM);
        return p?.AsElementId() ?? ElementId.InvalidElementId;
    }

    /// <summary>
    /// Creates a new segment matching the element's type. Pipe/Duct carry a
    /// system (piping/mechanical) that has to be passed to Create; CableTray and
    /// Conduit are just raceways with no system of their own, so their Create
    /// signature doesn't take one.
    /// </summary>
    private static Element? MakeSegment(
        Document doc, Element elem, ElementId typeId, ElementId levelId,
        MEPSystem? mepSystem, XYZ p0, XYZ p1) => elem switch
        {
            Pipe => Pipe.Create(doc, mepSystem!.GetTypeId(), typeId, levelId, p0, p1),
            Duct => Duct.Create(doc, mepSystem!.GetTypeId(), typeId, levelId, p0, p1),
            CableTray => CableTray.Create(doc, typeId, p0, p1, levelId),
            Conduit => Conduit.Create(doc, typeId, p0, p1, levelId),
            _ => null,
        };

    /// <summary>
    /// How close to plumb a cable tray must be before Revit will let it be
    /// rolled about its own axis. About 2.5 degrees off vertical.
    /// </summary>
    private const double VerticalTrayDot = 0.999;

    /// <summary>
    /// For non-round profiles (rectangular/oval duct or cable tray), rotates the
    /// new segment around its own centerline so its cross-section orientation
    /// matches the original's, instead of sitting at the type's default (flat)
    /// roll. Round profiles are rotationally symmetric, so there's nothing to
    /// match. Returns the refreshed connector pair, since rotating invalidates
    /// the old connector objects.
    /// </summary>
    private static (Connector Open, Connector New) MatchRoll(
        Document doc, Element elem, Element newSegment, Connector openConn, Connector newConn)
    {
        if (openConn.Shape == ConnectorProfileType.Round
            || newConn.Shape == ConnectorProfileType.Round)
        {
            return (openConn, newConn);
        }

        XYZ axis = newConn.CoordinateSystem.BasisZ;

        // A cable tray that is not plumb MUST lie flat - Revit fixes its
        // cross-section and refuses any roll about its own axis with
        // "Horizontal cable tray cannot be rotated in this manner", an ERROR
        // rather than a warning, so SilentTransaction rightly does not swallow
        // it and the user gets a modal that kills the whole elbow.
        //
        // Skipping the roll is not a workaround, it is the correct answer:
        // flat is the only orientation such a tray is allowed to have, and
        // CableTray.Create already produces it. The roll still applies to a
        // VERTICAL tray, where it decides which way the riser faces in plan -
        // which is why turning horizontal-to-vertical always worked and only
        // vertical-to-horizontal failed.
        //
        // Ducts are deliberately NOT covered: Revit lets a horizontal
        // rectangular duct be rolled, and matching that roll is the whole
        // reason this method exists.
        if (newSegment is CableTray && Math.Abs(axis.Normalize().Z) < VerticalTrayDot)
            return (openConn, newConn);

        double angle = MoveConnectMath.SignedRollAngle(
            newConn.CoordinateSystem.BasisX.ToVec3(),
            openConn.CoordinateSystem.BasisX.ToVec3(),
            axis.ToVec3());

        if (Math.Abs(angle) > ElbowMath.AngleEps)
        {
            XYZ pivot = newConn.Origin;
            ElementTransformUtils.RotateElement(
                doc, newSegment.Id, Line.CreateBound(pivot, pivot.Add(axis)), angle);
            openConn = MepConnectors.RefreshConnector(doc, elem, openConn);
            newConn = MepConnectors.RefreshConnector(doc, newSegment, newConn);
        }

        return (openConn, newConn);
    }

    // -------------------------------------------------------------- insulation

    /// <summary>
    /// (typeId, thickness) for the element's existing insulation, or null if it
    /// has none. Only Pipe and Duct support insulation at all.
    /// </summary>
    private static (ElementId TypeId, double Thickness)? GetInsulation(Document doc, Element elem)
    {
        if (elem is not (Pipe or Duct))
            return null;

        ICollection<ElementId> ids = InsulationLiningBase.GetInsulationIds(doc, elem.Id);
        if (ids.Count == 0)
            return null;

        if (doc.GetElement(ids.First()) is not InsulationLiningBase insulation)
            return null;

        return (insulation.GetTypeId(), insulation.Thickness);
    }

    private static void ApplyInsulation(
        Document doc, bool isPipe, ElementId hostId, ElementId insulationTypeId, double thickness)
    {
        if (isPipe)
            PipeInsulation.Create(doc, hostId, insulationTypeId, thickness);
        else
            DuctInsulation.Create(doc, hostId, insulationTypeId, thickness);
    }

    // ------------------------------------------------------------------ picking

    /// <summary>
    /// Radius (in feet, measured on the view's screen plane) around the open
    /// connector inside which a click indicates NO direction at all, so the
    /// fallback menu opens instead.
    ///
    /// Sized from the view's currently visible extent so the zone stays the same
    /// apparent size on screen at any zoom or model scale — a fixed model-space
    /// radius would blanket half the screen zoomed in and be sub-pixel zoomed
    /// out. Only the right/up components of the zoom corners are used:
    /// GetZoomCorners() reports unreliable Z in orthographic projection.
    /// </summary>
    private static double ViewDeadZone(IPicker picker, View view, Element elem, XYZ right, XYZ up)
    {
        double? extent = picker.VisibleExtent(view, right, up);
        if (extent is > ElbowMath.LenEps)
            return extent.Value * DeadZoneViewFraction;

        return Math.Max(ElbowMath.LenEps, CharacteristicSize(elem) * DeadZoneSizeMultiplier);
    }

    /// <summary>
    /// Creates a temporary work plane facing the view through refPoint and makes
    /// it the view's active sketch plane, so PickPoint can resolve a point even
    /// when clicking empty space (e.g. the exact ViewCube orientations, where
    /// there's often nothing behind the cursor to snap onto). Returns the new
    /// SketchPlane's id to clean up, and whatever SketchPlane the view already
    /// had, to restore afterward rather than just clearing it.
    /// </summary>
    private static (ElementId? NewId, ElementId? PreviousId) SetPickPlane(
        Document doc, View view, XYZ refPoint)
    {
        ElementId? previousId = view.SketchPlane?.Id;

        using Transaction t = SilentTransaction.Start(doc, "Elbow By Click - Set Pick Plane");
        try
        {
            Plane plane = Plane.CreateByNormalAndOrigin(view.ViewDirection, refPoint);
            SketchPlane sketchPlane = SketchPlane.Create(doc, plane);
            view.SketchPlane = sketchPlane;
            t.Commit();
            return (sketchPlane.Id, previousId);
        }
        catch (Exception)
        {
            t.RollBack();
            return (null, previousId);
        }
    }

    /// <summary>
    /// Puts the view's original sketch plane back and deletes the temporary one.
    ///
    /// Only needed on the SUCCESS path now. Any other outcome rolls the whole
    /// transaction group back, which undoes the plane change along with
    /// everything else — see <see cref="Run"/>.
    /// </summary>
    private static void ClearPickPlane(
        Document doc, View view, ElementId? sketchPlaneId, ElementId? previousSketchPlaneId)
    {
        if (sketchPlaneId is null)
            return;

        using Transaction t = SilentTransaction.Start(doc, "Elbow By Click - Clear Pick Plane");
        try
        {
            view.SketchPlane = previousSketchPlaneId is not null
                ? doc.GetElement(previousSketchPlaneId) as SketchPlane
                : null;
        }
        catch (Exception)
        {
            // Restoring the original didn't work - clear it outright, so the
            // view isn't left pointing at the temp plane we delete below.
            try
            {
                view.SketchPlane = null;
            }
            catch (Exception)
            {
                // Nothing further we can do; the delete below still runs.
            }
        }

        if (doc.GetElement(sketchPlaneId) is not null)
            doc.Delete(sketchPlaneId);

        t.Commit();
    }

    /// <summary>
    /// Only straight MEP runs that still have an open end are pickable, so an
    /// element this tool could never extend cannot be selected in the first
    /// place — the failure stops existing rather than being reported afterwards.
    ///
    /// SPLIT CACHING, deliberately. "Is a straight run" is stable for the life of
    /// the command and is memoised, because Revit calls this for every element
    /// the cursor passes over. "Has an open connector" is NOT cached: adding an
    /// elbow CONSUMES the open end, so a cached answer would go stale the moment
    /// this tool does its job — the exact trap
    /// <see cref="CachedPickFilter"/> warns about, which is why this does not
    /// derive from it. The straight-run memo gates the connector walk, so only
    /// MEP runs ever pay for it.
    /// </summary>
    internal sealed class OpenEndRunFilter(Document doc) : ISelectionFilter
    {
        private readonly Dictionary<long, bool> _isStraightRun = [];

        public bool AllowElement(Element elem)
        {
            try
            {
                Element resolved = Picking.ResolveHost(doc, elem);

                Category? cat = resolved.Category;
                if (cat is null || cat.CategoryType != CategoryType.Model)
                    return false;

                long key = RevitCompat.IdKey(resolved.Id);
                if (!_isStraightRun.TryGetValue(key, out bool straight))
                {
                    straight = ElementGeometry.GetLine(resolved) is not null;
                    _isStraightRun[key] = straight;
                }

                if (!straight)
                    return false;

                return MepConnectors.GetOpenConnectors(resolved).Count > 0;
            }
            catch (Exception)
            {
                // A filter that throws mid-hover would abort the pick, so
                // anything unexpected means "not pickable".
                return false;
            }
        }

        public bool AllowReference(Reference reference, XYZ point) => true;
    }

    // --------------------------------------------------------------- validation

    private static (Line? Line, Connector? OpenConn, string? Reason) ValidateTarget(
        Element elem, XYZ pickPoint)
    {
        Line? line = ElementGeometry.GetLine(elem);
        if (line is null)
        {
            return (null, null,
                $"{Picking.DescribeElem(elem)} is not a straight run (curved/flex runs aren't supported)");
        }

        List<Connector> open = MepConnectors.GetOpenConnectors(elem);
        if (open.Count == 0)
        {
            return (null, null,
                $"{Picking.DescribeElem(elem)} has no open (dead-end) connector to extend");
        }

        return (line, ResolveOpenEnd(elem, line, open, pickPoint), null);
    }

    /// <summary>
    /// Which open end the elbow goes on.
    ///
    /// Normally the one nearest the click that selected the run. But a pipe with
    /// BOTH ends open is common, and then this single click is deciding two
    /// things at once — which run, and which end — while the user is only
    /// thinking about the first. Pick near the middle and the elbow lands on the
    /// far end with the entire set of turn directions mirrored, which is
    /// indistinguishable from the tool behaving at random.
    ///
    /// So when the click does not clearly favour one end, ask. The threshold is
    /// a fraction of the run's own length, because "clearly nearer" only means
    /// anything relative to how long the run is.
    /// </summary>
    private static Connector? ResolveOpenEnd(
        Element elem, Line line, List<Connector> open, XYZ pickPoint)
    {
        Connector? nearest = MepConnectors.ClosestOpenConnector(elem, pickPoint);
        if (open.Count < 2 || nearest is null)
            return nearest;

        List<Connector> byDistance =
            [.. open.OrderBy(c => c.Origin.DistanceTo(pickPoint))];

        double lead = byDistance[1].Origin.DistanceTo(pickPoint)
                      - byDistance[0].Origin.DistanceTo(pickPoint);

        if (lead >= OpenEndLeadFraction * line.Length)
            return byDistance[0];

        List<string> labels =
            [.. byDistance.Select((c, i) => $"End {i + 1}{(i == 0 ? " (nearer your click)" : "")}:  {EndLabel(c, byDistance[1 - i])}")];

        string? picked = RibbitUi.SelectFromList(
            labels,
            $"{Picking.DescribeElem(elem)} is open at BOTH ends - which one gets the elbow?",
            "Use this end");

        if (picked is null)
            return null;   // cancelled - the caller treats a null end as "give up quietly"

        return byDistance[labels.IndexOf(picked)];
    }

    /// <summary>
    /// How much nearer one open end must be, as a fraction of the run's length,
    /// before the click is taken to have chosen it. A fifth of the run is enough
    /// to separate a deliberate click near one end from a vague one in the
    /// middle, without nagging on every pick.
    /// </summary>
    private const double OpenEndLeadFraction = 0.2;

    /// <summary>
    /// Which end, in words a person can match to the screen: the compass side
    /// it is on relative to the other end ("West end"), or "Top" / "Bottom" for
    /// a riser. It used to list world coordinates in mm (x 1219200 mm ...),
    /// which say nothing about which end is which.
    /// </summary>
    private static string EndLabel(Connector conn, Connector other)
    {
        XYZ d = conn.Origin - other.Origin;
        double horiz = Math.Sqrt((d.X * d.X) + (d.Y * d.Y));
        if (horiz < 0.1 * d.GetLength())
            return d.Z > 0 ? "Top end" : "Bottom end";

        string compass = ElbowMath.WorldDirLabel(new Vec3(d.X, d.Y, 0));
        string height = d.Z > 0.1 * horiz ? " (the higher one)" : d.Z < -0.1 * horiz ? " (the lower one)" : "";
        return $"{compass} end{height}";
    }
    // --------------------------------------------------------------------- core

    /// <summary>
    /// Works out which way to bend. Tries the click first; if the click can't
    /// clearly indicate a direction it falls back to a menu of every valid turn,
    /// which is the only way to reach edge-on turns from a straight-on
    /// orthographic or plan view.
    /// </summary>
    private static (XYZ? Direction, int AngleDeg, string? Reason) ResolveTurn(
        IPicker picker, Element elem, Line line, Connector openConn, XYZ clickPoint, View view)
    {
        XYZ? right = view.RightDirection;
        XYZ? up = view.UpDirection;
        if (right is null || up is null)
            return (null, 0, "the active view doesn't expose a screen orientation to match against");

        Vec3? runDir = ElbowMath.RunDirection(
            line.GetEndPoint(0).ToVec3(), line.GetEndPoint(1).ToVec3(), openConn.Origin.ToVec3());
        if (runDir is null)
            return (null, 0, $"{Picking.DescribeElem(elem)} has zero length");

        double deadZone = ViewDeadZone(picker, view, elem, right, up);
        (double U, double V) clickUvDelta = ElbowMath.ScreenUv(
            clickPoint.Subtract(openConn.Origin).ToVec3(), right.ToVec3(), up.ToVec3());

        (TurnCandidate? chosen, string? reason) = ElbowMath.PickTurnDirection(
            runDir.Value, clickUvDelta, right.ToVec3(), up.ToVec3(), deadZone);

        if (chosen is null)
        {
            // The click couldn't pick a direction on its own - offer the full
            // menu (edge-on turns listed first).
            TurnCandidate? fromMenu = ChooseTurnViaDialog(
                ElbowMath.TurnCandidates(runDir.Value),
                right.ToVec3(), up.ToVec3(), view.ViewDirection?.ToVec3());

            // Cancelled: no reason, so the caller ends quietly. Telling the
            // user why the CLICK did not decide, after they chose to cancel the
            // menu it opened, is a second dialog for nothing.
            if (fromMenu is null)
                return (null, 0, null);

            chosen = fromMenu;
        }

        // A near-vertical turn (however it was picked) is forced to true plumb,
        // so a riser off a sloped run comes out vertical instead of inheriting
        // the run's slope. No effect on plan turns or angled offsets.
        return (ElbowMath.SnapToPlumb(chosen.Value.Direction).ToXYZ(), chosen.Value.AngleDeg, null);
    }

    /// <summary>
    /// Fallback when a click can't indicate a direction. Edge-on turns — the
    /// ones a click physically can't express in an orthographic view — are
    /// listed first, since that's why the menu appeared.
    /// </summary>
    private static TurnCandidate? ChooseTurnViaDialog(
        List<TurnCandidate> candidates, Vec3 right, Vec3 up, Vec3? viewDir)
    {
        List<TurnCandidate> ordered = [.. candidates];
        ordered.Sort((a, b) =>
        {
            int aEdge = ElbowMath.IsEdgeOn(a.Direction, right, up) ? 0 : 1;
            int bEdge = ElbowMath.IsEdgeOn(b.Direction, right, up) ? 0 : 1;
            return aEdge != bEdge ? aEdge.CompareTo(bEdge) : a.AngleDeg.CompareTo(b.AngleDeg);
        });

        List<string> labels = [];
        List<TurnCandidate> byLabel = [];
        HashSet<string> seen = [];

        foreach (TurnCandidate c in ordered)
        {
            string label = ElbowMath.DescribeTurn(c, right, up, viewDir);
            string bas = label;
            int n = 2;
            while (!seen.Add(label))
                label = $"{bas} #{n++}";

            labels.Add(label);
            byLabel.Add(c);
        }

        string? chosen = RibbitUi.SelectFromList(
            labels, $"{DialogTitle} - pick a turn direction", "Add elbow");
        if (chosen is null)
            return null;

        int idx = labels.IndexOf(chosen);
        return idx >= 0 ? byLabel[idx] : null;
    }

    /// <summary>
    /// Extends the element by a new segment in newDir and joins it with an elbow
    /// fitting. Returns null on success, or a failure message. The turn direction
    /// is already resolved, so the direction-picking (which may pop a menu)
    /// stays outside every modeling transaction.
    ///
    /// EACH ATTEMPT IS ITS OWN TRANSACTION, with an <see cref="ErrorCollector"/>.
    /// A fitting that does not fit fails one of two ways: NewElbowFitting
    /// throws, or Revit accepts it and posts an ERROR when the transaction
    /// commits ("insufficient space to create the required fittings"). The old
    /// single silent transaction retried only the first; the second reached the
    /// user as Revit's own modal error dialog - seen in the in-Revit suite, where
    /// that dialog hung the unattended run. Now both roll the attempt back, the
    /// segment grows, and only if every attempt fails does the user get one
    /// message, carrying Revit's own reason.
    /// </summary>
    private static string? BuildElbow(
        Document doc, Element elem, Connector openConn, XYZ newDir, int angleDeg)
    {
        MEPSystem? mepSystem = (elem as MEPCurve)?.MEPSystem;
        if (elem is Pipe or Duct && mepSystem is null)
            return $"{Picking.DescribeElem(elem)} has no assigned system";

        ElementId typeId = elem.GetTypeId();
        ElementId levelId = GetLevelId(elem);
        double length = Math.Max(
            DefaultSegmentLength, CharacteristicSize(elem) * SegmentSizeMultiplier);
        (ElementId TypeId, double Thickness)? insulation = GetInsulation(doc, elem);
        int openId = openConn.Id;

        string? lastError = null;
        double lastLength = length;
        for (int attempt = 0; attempt < LengthRetryAttempts; attempt++)
        {
            // After a rolled-back attempt, read the open end afresh by its id.
            Connector open = MepConnectors.GetEndConnectors(elem).Find(c => c.Id == openId) ?? openConn;
            lastLength = length;

            using Transaction t = new(doc, "Elbow By Click");
            ErrorCollector errors = ErrorCollector.Start(t);
            (string? failed, bool retry) attemptResult;
            try
            {
                attemptResult = BuildOnce(doc, elem, open, newDir, length, typeId, levelId, mepSystem, insulation);
            }
            catch (Exception)
            {
                t.RollBack();
                throw;
            }

            if (attemptResult.failed is not null)
            {
                t.RollBack();
                if (!attemptResult.retry)
                    return attemptResult.failed;
                lastError = attemptResult.failed;
            }
            else if (t.Commit() == TransactionStatus.Committed)
            {
                return null;
            }
            else
            {
                lastError = errors.Errors.FirstOrDefault() ?? "Revit rolled it back";
            }

            length *= LengthRetryMultiplier;
        }

        return $"failed to create a {angleDeg}-degree elbow fitting even after giving it up to "
               + $"{lastLength:F2} ft of straight length - check that {Picking.DescribeElem(elem)}'s "
               + $"type's routing preferences have a matching elbow fitting assigned for that "
               + $"angle (Revit said: {lastError})";
    }

    /// <summary>
    /// One attempt, inside the caller's transaction: (null, _) when built,
    /// otherwise why not and whether a longer segment could help.
    /// </summary>
    private static (string? Failed, bool Retry) BuildOnce(
        Document doc, Element elem, Connector openConn, XYZ newDir, double length,
        ElementId typeId, ElementId levelId, MEPSystem? mepSystem,
        (ElementId TypeId, double Thickness)? insulation)
    {
        XYZ endPoint = openConn.Origin.Add(newDir.Multiply(length));
        Element? newSegment = MakeSegment(
            doc, elem, typeId, levelId, mepSystem, openConn.Origin, endPoint);

        if (newSegment is null)
        {
            return ($"{Picking.DescribeElem(elem)} is not a supported type "
                    + "(only Pipe, Duct, CableTray, and Conduit are supported)", false);
        }

        MatchSize(elem, newSegment);
        openConn = MepConnectors.RefreshConnector(doc, elem, openConn);

        Connector? newConn = MepConnectors.ClosestOpenConnector(newSegment, openConn.Origin);
        if (newConn is null)
            return ("could not find the connector on the new segment", false);

        (openConn, newConn) = MatchRoll(doc, elem, newSegment, openConn, newConn);

        FamilyInstance elbow;
        try
        {
            elbow = doc.Create.NewElbowFitting(openConn, newConn);
        }
        catch (Exception ex)
        {
            return (ex.Message, true);
        }

        doc.Regenerate();

        if (insulation is not null)
        {
            bool isPipe = elem is Pipe;
            try
            {
                ApplyInsulation(doc, isPipe, newSegment.Id,
                    insulation.Value.TypeId, insulation.Value.Thickness);
                ApplyInsulation(doc, isPipe, elbow.Id,
                    insulation.Value.TypeId, insulation.Value.Thickness);
            }
            catch (Exception)
            {
                // Insulation is a bonus - don't fail an otherwise-successful
                // elbow over it.
            }
        }

        return (null, false);
    }

    /// <summary>
    /// The direction dial for this pick, or null when the view has no screen
    /// orientation or the run no length (the click then works as before, just
    /// without the picture). Same dead zone the click is judged with.
    /// </summary>
    private static ElbowDial? BuildDial(IPicker picker, Element elem, Line line, Connector openConn, View view)
    {
        XYZ? right = view.RightDirection;
        XYZ? up = view.UpDirection;
        if (right is null || up is null)
            return null;

        Vec3? runDir = ElbowMath.RunDirection(
            line.GetEndPoint(0).ToVec3(), line.GetEndPoint(1).ToVec3(), openConn.Origin.ToVec3());
        if (runDir is null)
            return null;

        double deadZone = ViewDeadZone(picker, view, elem, right, up);
        return ElbowDial.Build(openConn.Origin.ToVec3(), runDir.Value, right.ToVec3(), up.ToVec3(), deadZone);
    }
    // --------------------------------------------------------------------- entry

    public static void Run(UIDocument uidoc)
        => Run(uidoc.Document, uidoc.Document.ActiveView, new RevitPicker(uidoc));

    /// <summary>The whole command, on any picker and view (the test suite's way in).</summary>
    internal static void Run(Document doc, View activeView, IPicker picker)
    {
        // Filtered, so an element this tool could never extend is not selectable.
        (Element? element, XYZ? elementPickPoint) = picker.PickElement(
            "Pick the pipe/duct/cable tray/conduit with the open end to extend "
            + "- click nearer the end you want to extend",
            new OpenEndRunFilter(doc));

        if (element is null)
            return;

        element = Picking.ResolveHost(doc, element);
        elementPickPoint ??= ElementGeometry.GetReferencePoint(element);
        if (elementPickPoint is null)
        {
            RibbitUi.Notify("Cannot add elbow: could not locate the picked element.", DialogTitle);
            return;
        }

        // Kept as a safety net even though the filter now screens for both of
        // these: the filter answers during hover, and a model can change under a
        // long-lived pick.
        (Line? line, Connector? openConn, string? targetError) =
            ValidateTarget(element, elementPickPoint);

        if (openConn is null || line is null)
        {
            // No reason means the which-end question was cancelled: quietly.
            // (It used to show "Cannot add elbow: " with nothing after it.)
            if (targetError is not null)
                RibbitUi.Notify($"Cannot add elbow: {targetError}", DialogTitle);
            return;
        }

        // ONE undo entry for the whole command.
        //
        // Setting and clearing the temporary pick plane are real document
        // changes, so without this an elbow cost THREE undo steps and the first
        // Ctrl+Z undid "Clear Pick Plane" rather than the elbow the user was
        // looking at.
        //
        // The group is also the rollback safeguard: `keep` is set only on the
        // one path that fully succeeded, and the finally rolls back on every
        // other - error, cancel, or an exception from anywhere in between. A
        // failed or abandoned run therefore leaves NO trace at all, not even the
        // temporary sketch plane.
        using TransactionGroup group = new(doc, "Elbow By Click");
        group.Start();
        bool keep = false;

        try
        {
            (ElementId? sketchPlaneId, ElementId? previousSketchPlaneId) =
                SetPickPlane(doc, activeView, openConn.Origin);

            // The dial is up only while the click is awaited: disposing it in the
            // using takes it off screen whether the user clicks, presses Esc, or
            // something throws.
            XYZ? clickPoint;
            using (picker.ShowDial(activeView, BuildDial(picker, element, line, openConn, activeView)))
            {
                clickPoint = picker.PickPoint(
                    "Click toward an arrow on the dial to bend that way - or inside its small circle "
                    + "(or backwards along the run) for a menu of every turn, incl. ones into/out of "
                    + "the screen like a drop from a plan view");
            }

            if (clickPoint is null)
                return;

            string warning = activeView is View3D { IsPerspective: true }
                ? "Note: active view is in perspective mode - on-screen direction matching is "
                  + "approximate there. "
                : "";

            // Resolve the turn direction BEFORE opening the build transaction -
            // it may pop the fallback menu (a UI action), which must not run
            // inside a modeling transaction. An open transaction GROUP is fine.
            (XYZ? newDir, int angleDeg, string? reason) =
                ResolveTurn(picker, element, line, openConn, clickPoint, activeView);

            if (newDir is null)
            {
                if (reason is not null)   // null: the turn menu was cancelled
                    RibbitUi.Notify($"{warning}Cannot add elbow: {reason}", DialogTitle);
                return;
            }

            string? error = BuildElbow(doc, element, openConn, newDir, angleDeg);
            if (error is not null)
            {
                RibbitUi.Notify($"{warning}Cannot add elbow: {error}", DialogTitle);
                return;
            }

            // THE ELBOW IS COMMITTED. From here the run is a keeper whatever
            // happens next, so `keep` is set BEFORE the cleanup rather than
            // after it.
            //
            // It used to be set after, and ClearPickPlane can throw — its Delete
            // and Commit are outside its own try. A cleanup failure therefore
            // reached the catch below with `keep` still false, and the finally
            // rolled the group back, DISCARDING AN ELBOW THAT HAD JUST BEEN
            // BUILT SUCCESSFULLY. Tidying up is never worth the work it was
            // tidying up after.
            keep = true;

            try
            {
                ClearPickPlane(doc, activeView, sketchPlaneId, previousSketchPlaneId);
            }
            catch (Exception ex)
            {
                // Worth saying, because this tool is the only SketchPlane
                // creator in the add-in, so a stray work plane can only have
                // come from here. Not worth losing the elbow over.
                RibbitUi.Notify(
                    "The elbow was created. Tidying up the temporary work plane failed "
                    + $"({ex.Message}) - the view may still be set to it.",
                    DialogTitle);
            }
        }
        catch (Exception ex)
        {
            RibbitUi.Notify($"Unexpected error: {ex.Message}", DialogTitle);
        }
        finally
        {
            if (keep)
                group.Assimilate();
            else
                group.RollBack();
        }
    }
}
