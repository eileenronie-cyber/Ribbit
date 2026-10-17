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
/// fitting joins it to the original dead end. In a 3D view a dial shows every
/// turn as a target to click; a flat view offers the turns lying flat in it.
/// There is no menu.
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
    /// extent) and no direction is taken from the click at all — the tool asks
    /// for another click instead. Measured off the view extent rather than as a fixed
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
    /// tool asks for another click instead.
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
    /// there's often nothing behind the cursor to snap onto). False when it could
    /// not be set.
    ///
    /// It is NEVER tidied up by deleting: the caller makes it inside a
    /// transaction group that it rolls back after the click (see
    /// <see cref="Run"/>). Deleting does not work in a workshared model - there
    /// Revit adds a companion plane for every plane created and a copy for every
    /// plane a view is set to, so a delete left planes behind and the view on a
    /// copy of its old plane (proven on a workshared project, 2026-10-17). A
    /// rollback restores all of it exactly.
    /// </summary>
    private static bool SetPickPlane(Document doc, View view, XYZ refPoint)
    {
        using Transaction t = SilentTransaction.Start(doc, "Elbow By Click - Set Pick Plane");
        try
        {
            Plane plane = Plane.CreateByNormalAndOrigin(view.ViewDirection, refPoint);
            view.SketchPlane = SketchPlane.Create(doc, plane);
            return t.Commit() == TransactionStatus.Committed;
        }
        catch (Exception)
        {
            t.RollBack();
            return false;
        }
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

    /// <summary>The prompt while the dial is up (3D views).</summary>
    private const string DialPrompt =
        "Click the arrow you want: teal turns sideways, orange goes up or down "
        + "(a dot is up, a cross is down); solid is a 90, dashed a 45. Esc to cancel.";

    /// <summary>The prompt in a flat view, which offers only the turns lying in it.</summary>
    private const string FlatPrompt =
        "Click toward the way you want to turn. This view offers the turns lying flat in it; "
        + "to go up or down from a plan, use a 3D or section view. Esc to cancel.";

    /// <summary>
    /// Waits for the direction click until it names a turn, or Esc (null).
    ///
    /// NO MENU. A 3D view's dial gives every turn a target of its own, and a
    /// flat view offers only the turns lying flat in it, so no click is ever
    /// undecidable. A click that names nothing - on the open end, behind it, or
    /// nowhere near an arrow - asks again, saying why in the prompt. The old
    /// fallback menu was a modal list of world headings that broke the
    /// pick-and-click rhythm exactly where the view was hardest to read.
    /// </summary>
    private static TurnCandidate? AskForTurn(IPicker picker, ElbowDial? dial, Vec3 origin, Vec3 runDir,
        Vec3 right, Vec3 up, double deadZone, double frameRad, string note)
    {
        string prompt = (dial is null ? FlatPrompt : DialPrompt) + note;
        while (true)
        {
            XYZ? click = picker.PickPoint(prompt);
            if (click is null)
                return null;

            (double U, double V) d = ElbowMath.ScreenUv(click.ToVec3().Subtract(origin), right, up);
            (TurnCandidate? turn, string? why) = dial is not null
                ? dial.Resolve(d.U, d.V)
                : ElbowMath.PickTurnDirection(runDir, d, right, up, deadZone, frameRad);
            if (turn is not null)
                return turn;

            // Short, reason first: the status bar cuts a long prompt off at the
            // end, and the reason is the part that is new.
            prompt = $"Not taken: {why}. Click again, or Esc to cancel.";
        }
    }

    /// <summary>
    /// The building's rotation on plan, for turning off a RISER (radians; 0 =
    /// world X/Y). A riser has no heading of its own, so its eight flat turns
    /// were always world north/east/south/west and the diagonals - every one of
    /// them a miss on a building drawn at an angle.
    ///
    /// First choice: whatever feeds the riser at its other end (the fitting it
    /// turned up through, or the run beyond), because that is the line a riser
    /// most often turns back to. Otherwise the grids, by
    /// <see cref="ElbowMath.DominantFrame"/>. Neither, or anything unreadable:
    /// 0, as before.
    /// </summary>
    private static double BuildingFrame(Document doc, Element riser, Connector openConn)
    {
        try
        {
            foreach (Connector end in MepConnectors.GetEndConnectors(riser))
            {
                if (end.Id == openConn.Id || !end.IsConnected)
                    continue;

                foreach (Connector other in end.AllRefs)
                {
                    if (other.Owner is null || other.Owner.Id == riser.Id)
                        continue;
                    if (FlatHeading(other.Owner) is double h)
                        return ElbowMath.DominantFrame([h]);
                }
            }

            List<double> grids = [];
            foreach (Grid g in new FilteredElementCollector(doc).OfClass(typeof(Grid)).Cast<Grid>())
            {
                if (g.Curve is Line l && Math.Abs(l.Direction.Z) < 0.1)
                    grids.Add(Math.Atan2(l.Direction.Y, l.Direction.X));
            }

            return ElbowMath.DominantFrame(grids);
        }
        catch (Autodesk.Revit.Exceptions.ApplicationException)
        {
            return 0.0;   // world axes, as the tool always used
        }
    }

    /// <summary>
    /// The plan heading of what feeds a riser, or null: a straight run's own
    /// line when it is not steep, else (a fitting) the first of its ends that
    /// points sideways.
    /// </summary>
    private static double? FlatHeading(Element e)
    {
        if (ElementGeometry.GetLine(e) is Line l)
            return Math.Abs(l.Direction.Z) < 0.7 ? Math.Atan2(l.Direction.Y, l.Direction.X) : null;

        foreach (Connector c in MepConnectors.GetEndConnectors(e))
        {
            XYZ z = c.CoordinateSystem.BasisZ;
            if (Math.Abs(z.Z) < 0.7)
                return Math.Atan2(z.Y, z.X);
        }

        return null;
    }

    /// <summary>
    /// Extends the element by a new segment in newDir and joins it with an elbow
    /// fitting. Returns null on success, or a failure message. The turn direction
    /// is already resolved, so the direction click (a UI pick)
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

        XYZ? right = activeView.RightDirection;
        XYZ? up = activeView.UpDirection;
        if (right is null || up is null)
        {
            RibbitUi.Notify("Cannot add elbow: the active view has no screen directions to click in.", DialogTitle);
            return;
        }

        Vec3? runDir = ElbowMath.RunDirection(
            line.GetEndPoint(0).ToVec3(), line.GetEndPoint(1).ToVec3(), openConn.Origin.ToVec3());
        if (runDir is null)
        {
            RibbitUi.Notify($"Cannot add elbow: {Picking.DescribeElem(element)} has zero length.", DialogTitle);
            return;
        }

        Vec3 origin = openConn.Origin.ToVec3();
        double deadZone = ViewDeadZone(picker, activeView, element, right, up);
        double frame = ElbowMath.IsDeadVertical(runDir.Value) ? BuildingFrame(doc, element, openConn) : 0.0;

        // The dial is for 3D views, where turns hide behind each other. A flat
        // view's turns are the ones lying in it, plain to aim at without one.
        ElbowDial? dial = activeView is View3D
            ? ElbowDial.Build(origin, runDir.Value, right.ToVec3(), up.ToVec3(), deadZone, frame)
            : null;

        // ONE undo entry for the whole command, and the rollback safeguard:
        // `keep` is set only on the one path that fully succeeded, and the
        // finally rolls back on every other - error, cancel, or an exception from
        // anywhere in between. A failed or abandoned run leaves NO trace at all.
        using TransactionGroup group = new(doc, "Elbow By Click");
        group.Start();
        bool keep = false;

        try
        {
            // Everything the CLICK needs - the temporary pick plane, the dial's
            // lines - lives in its own group that is rolled back as soon as the
            // click is in, before anything is built. A rollback restores the model
            // exactly; deleting did not (see SetPickPlane), and it is faster too.
            TurnCandidate? turn;
            using (TransactionGroup pick = new(doc, "Elbow By Click - Pick"))
            {
                pick.Start();
                try
                {
                    bool planeSet = SetPickPlane(doc, activeView, openConn.Origin);

                    // The click lands on the plane through the open end facing the view,
                    // the plane the dial is drawn in - so even in PERSPECTIVE a click on
                    // a target is that target. Only without that plane can it drift.
                    string note = activeView is View3D { IsPerspective: true } && !planeSet
                        ? " (Perspective view without a work plane: a click may land off the dial.)"
                        : "";

                    // The dial is up only while the click is awaited: disposing it in the
                    // using takes it off screen whether the user clicks, presses Esc, or
                    // something throws.
                    using (picker.ShowDial(activeView, dial))
                    {
                        turn = AskForTurn(picker, dial, origin, runDir.Value, right.ToVec3(), up.ToVec3(), deadZone, frame, note);
                    }
                }
                finally
                {
                    pick.RollBack();
                }
            }

            if (turn is null)
                return;   // Esc

            // A near-vertical turn is forced to true plumb, so a riser off a
            // sloped run comes out vertical instead of inheriting the run's
            // slope. No effect on plan turns or angled offsets.
            XYZ newDir = ElbowMath.SnapToPlumb(turn.Value.Direction).ToXYZ();

            string? error = BuildElbow(doc, element, openConn, newDir, turn.Value.AngleDeg);
            if (error is not null)
            {
                RibbitUi.Notify($"Cannot add elbow: {error}", DialogTitle);
                return;
            }

            keep = true;
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
