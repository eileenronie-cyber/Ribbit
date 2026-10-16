using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using Ribbit.Geometry;
using Ribbit.Native.Revit;
using Ribbit.Native.Ui;
using OperationCanceledException = Autodesk.Revit.Exceptions.OperationCanceledException;

namespace Ribbit.Native.Tools;

/// <summary>
/// Tee Into Main. Ported from
/// _Ribbit.tab/PH_FP Tools.panel/TeeIntoMain.pushbutton/script.py.
///
/// Connects a branch into a main with a tee fitting.
///
/// keepAngle=true  : preserve the branch's direction (a plumb riser stays
///                   plumb) and slide it sideways onto the main's axis.
/// keepAngle=false : fallback — pin the branch's far end and swing the
///                   connecting end onto the perpendicular foot, giving an
///                   exact 90 degrees at the cost of tilting the branch by the
///                   main's pitch.
/// </summary>
internal static class TeeIntoMainEngine
{
    private const double LenEps = 1.0e-6;           // feet - "same point" tolerance
    private const double ConnMatchTol = 0.01;       // feet - how close a connector must be to T
    private const double MinBranchLength = 0.05;    // feet - shorter than this can't host a connection
    private const double EndMargin = 0.01;          // feet - T must land this far inside the main's ends

    /// <summary>
    /// Max sideways slide allowed to put the branch's axis on the main's axis
    /// while keeping the branch's angle — a roughly-placed riser only needs
    /// inches, so anything bigger means the picks don't describe a real tee
    /// situation (e.g. branch nearly parallel to the main).
    /// </summary>
    private const double MaxShiftFt = 2.0;

    /// <summary>
    /// Slide worth confirming even when the branch's far end isn't attached to
    /// anything — half of MaxShiftFt, well past what a roughly-placed riser needs.
    /// </summary>
    private const double BigShiftWarnFt = 1.0;

    /// <summary>
    /// A joined far end is asked about once the slide passes this (1 mm): less
    /// than that is round-off, not a move anyone could see.
    /// </summary>
    private const double JoinedShiftAskFt = 1.0 / MmPerFt;

    /// <summary>
    /// Same idea for the fallback geometry (branch re-aimed at the
    /// perpendicular foot): a pitched main only ever needs the pitch angle.
    /// </summary>
    private const double MaxTiltDeg = 30.0;

    private const double MmPerFt = 304.8;
    private const string DialogTitle = "Tee Into Main";

    /// <summary>
    /// Pipe/duct and their fittings/accessories — anything else connected to
    /// the branch's anchor end (a sprinkler, fixture, piece of equipment) is a
    /// HEAD, whose plan position this tool would otherwise slide sideways
    /// without asking.
    /// </summary>
    private static readonly HashSet<long> InlineCategories =
    [
        .. new[]
        {
            BuiltInCategory.OST_PipeCurves, BuiltInCategory.OST_FlexPipeCurves,
            BuiltInCategory.OST_PipeFitting, BuiltInCategory.OST_PipeAccessory,
            BuiltInCategory.OST_DuctCurves, BuiltInCategory.OST_FlexDuctCurves,
            BuiltInCategory.OST_DuctFitting, BuiltInCategory.OST_DuctAccessory,
        }.Select(b => RevitCompat.IdKey(new ElementId(b)))
    ];

    private static bool IsInlineElem(Element elem)
    {
        Category? cat = elem.Category;
        return cat is not null && InlineCategories.Contains(RevitCompat.IdKey(cat.Id));
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>
    /// Whatever the element is ALREADY connected to at this point — not
    /// necessarily open, unlike <see cref="ConnectorAt"/>, which only ever
    /// looks for a FREE connector. Null if nothing sits there or that connector
    /// is open.
    /// </summary>
    private static Element? NeighborAt(Element elem, XYZ point, double tol = ConnMatchTol)
    {
        Connector? best = null;
        double? bestDist = null;

        foreach (Connector c in MepConnectors.GetEndConnectors(elem))
        {
            double d = c.Origin.DistanceTo(point);
            if (bestDist is null || d < bestDist)
            {
                bestDist = d;
                best = c;
            }
        }

        if (best is null || bestDist > tol || !best.IsConnected)
            return null;

        ElementId ownerId = elem.Id;
        ConnectorSet refs;
        try
        {
            refs = best.AllRefs;
        }
        catch (Autodesk.Revit.Exceptions.ApplicationException)
        {
            return null;
        }

        foreach (Connector reference in refs)
        {
            if (reference.ConnectorType != ConnectorType.End)
                continue;
            if (reference.Owner is null || reference.Owner.Id == ownerId)
                continue;
            return reference.Owner;
        }
        return null;
    }

    /// <summary>
    /// The element's open connector sitting at this point (within
    /// ConnMatchTol), or null. Used to find the two split-point connectors
    /// after BreakCurve and the branch's connector after re-aiming — all of
    /// which should be exactly at T, so the tolerance is numeric-noise insurance.
    /// </summary>
    private static Connector? ConnectorAt(Element elem, XYZ point)
    {
        Connector? best = MepConnectors.ClosestOpenConnector(elem, point);
        if (best is null)
            return null;
        return best.Origin.DistanceTo(point) <= ConnMatchTol ? best : null;
    }

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

    /// <summary>
    /// Splits the main at the point; returns the new piece's id or null.
    /// BreakCurve leaves the two pieces DISCONNECTED at the split, each with an
    /// open connector there — exactly what NewTeeFitting needs to bridge.
    /// </summary>
    private static ElementId? BreakMain(Document doc, Element main, XYZ point)
        => main is Pipe
            ? PlumbingUtils.BreakCurve(doc, main.Id, point)
            : MechanicalUtils.BreakCurve(doc, main.Id, point);

    // --------------------------------------------------------------------- core

    internal sealed record ConnectSolution(
        Connector Conn,
        bool ConnIsEnd0,
        XYZ Anchor,
        XYZ BranchDir,
        XYZ NewAnchor,
        XYZ TeePoint);

    /// <summary>
    /// Pure decision-making, no document writes: works out which end of the
    /// branch connects, its fixed anchor end, the branch's own direction, and
    /// where the connecting end would land under keepAngle.
    ///
    /// Shared by <see cref="TeeIntoMain"/> (which applies it) and the
    /// pre-commit head-shift check in the picking loop (which only needs to
    /// know how far the anchor would move and what's already connected there) —
    /// one source of truth, so the preview and the real move can never disagree
    /// about where things land.
    /// </summary>
    private static (ConnectSolution? Solution, string? Error) SolveConnect(
        Element main, Element branch, bool keepAngle)
    {
        Line? mainLine = ElementGeometry.GetLine(main);
        if (mainLine is null)
            return (null, $"the MAIN {Picking.DescribeElem(main)} is not a straight run");

        Line? branchLine = ElementGeometry.GetLine(branch);
        if (branchLine is null)
            return (null, $"the BRANCH {Picking.DescribeElem(branch)} is not a straight run");

        XYZ m0 = mainLine.GetEndPoint(0);
        XYZ m1 = mainLine.GetEndPoint(1);
        XYZ mainVec = m1.Subtract(m0);
        double mainLen = mainVec.GetLength();
        if (mainLen < LenEps)
            return (null, $"the MAIN {Picking.DescribeElem(main)} has zero length");
        XYZ mainDir = mainVec.Normalize();

        // Which branch end connects: whichever OPEN end is nearer the main's
        // axis - the far end connecting through the pipe itself is never what
        // anyone means, so this doesn't need to be click-driven.
        List<Connector> bOpen = MepConnectors.GetOpenConnectors(branch);
        if (bOpen.Count == 0)
            return (null, $"the BRANCH {Picking.DescribeElem(branch)} has no open end to connect");

        Connector conn = bOpen[0];
        double bestDist = TeeMath.PointToAxisDistance(
            conn.Origin.ToVec3(), m0.ToVec3(), mainDir.ToVec3());
        for (int i = 1; i < bOpen.Count; i++)
        {
            double d = TeeMath.PointToAxisDistance(
                bOpen[i].Origin.ToVec3(), m0.ToVec3(), mainDir.ToVec3());
            if (d < bestDist)
            {
                bestDist = d;
                conn = bOpen[i];
            }
        }

        // The anchor is the branch's OTHER line endpoint.
        XYZ b0 = branchLine.GetEndPoint(0);
        XYZ b1 = branchLine.GetEndPoint(1);
        bool connIsEnd0 = conn.Origin.DistanceTo(b0) < conn.Origin.DistanceTo(b1);
        XYZ anchor = connIsEnd0 ? b1 : b0;

        XYZ branchVec = conn.Origin.Subtract(anchor);
        if (branchVec.GetLength() < LenEps)
            return (null, $"the BRANCH {Picking.DescribeElem(branch)} has zero length");
        XYZ branchDir = branchVec.Normalize();

        XYZ newAnchor;
        XYZ teePoint;

        if (keepAngle)
        {
            // Slide the branch along the common perpendicular until its axis
            // meets the main's axis. Pure translation, so the branch's angle is
            // untouched - a plumb riser stays plumb and keeps its exact elevation.
            (Vec3 ptBranch, Vec3 teeVec, bool parallel) = TeeMath.AxisClosestPoints(
                anchor.ToVec3(), branchDir.ToVec3(), m0.ToVec3(), mainDir.ToVec3());

            if (parallel)
            {
                return (null, "the BRANCH runs parallel to the MAIN - there is no point "
                              + "to tee into");
            }

            teePoint = teeVec.ToXYZ();
            newAnchor = anchor.Add(teePoint.Subtract(ptBranch.ToXYZ()));
        }
        else
        {
            // Fallback: foot of the perpendicular from the fixed anchor onto the
            // main's axis, making (anchor -> T) exactly perpendicular to the main.
            newAnchor = anchor;
            teePoint = m0.Add(mainDir.Multiply(anchor.Subtract(m0).DotProduct(mainDir)));
        }

        return (new ConnectSolution(conn, connIsEnd0, anchor, branchDir, newAnchor, teePoint), null);
    }

    /// <summary>
    /// Connects branch into main with a tee. Returns (error, teeWasRefused).
    /// Runs entirely inside the caller's transaction so any failure rolls the
    /// whole pair back.
    /// </summary>
    private static (string? Error, bool TeeRefused) TeeIntoMain(
        Document doc, Element main, Element branch, bool keepAngle)
    {
        (ConnectSolution? solution, string? err) = SolveConnect(main, branch, keepAngle);
        if (solution is null)
            return (err, false);

        Line mainLine = ElementGeometry.GetLine(main)!;
        XYZ m0 = mainLine.GetEndPoint(0);
        XYZ mainVec = mainLine.GetEndPoint(1).Subtract(m0);
        double mainLen = mainVec.GetLength();
        XYZ mainDir = mainVec.Normalize();

        if (keepAngle)
        {
            double shiftLen = solution.NewAnchor.Subtract(solution.Anchor).GetLength();
            if (shiftLen > MaxShiftFt)
            {
                return ($"keeping the branch at its current angle would slide it {shiftLen:F2} ft "
                        + $"sideways onto the main's centerline (limit {MaxShiftFt:F1} ft) "
                        + "- is this the pair you meant?", false);
            }

            if (solution.TeePoint.Subtract(solution.NewAnchor).DotProduct(solution.BranchDir) <= 0.0)
            {
                return ("the MAIN sits behind the branch's open end - the branch would have to "
                        + "reverse direction to reach it", false);
            }
        }

        double tParam = solution.TeePoint.Subtract(m0).DotProduct(mainDir);
        if (tParam < EndMargin || tParam > mainLen - EndMargin)
        {
            double outside = Math.Max(-tParam, tParam - mainLen);
            return ($"the branch doesn't land within the MAIN's length - it would tee "
                    + $"{outside:F2} ft outside the run", false);
        }

        XYZ newBranchVec = solution.TeePoint.Subtract(solution.NewAnchor);
        double newBranchLen = newBranchVec.GetLength();
        if (newBranchLen < MinBranchLength)
        {
            return ("the branch's far end sits practically on the MAIN's axis - no room for a "
                    + "branch between them", false);
        }

        if (!keepAngle)
        {
            double tilt = solution.BranchDir.AngleTo(newBranchVec.Normalize()) * 180.0 / Math.PI;
            if (tilt > MaxTiltDeg)
            {
                return ($"connecting would swing the branch {tilt:F1} degrees (limit {MaxTiltDeg:F0}) "
                        + "- these two don't look like a tee pair (is the branch parallel to the "
                        + "main, or the wrong end open?)", false);
            }
        }

        // Move/trim the branch. Endpoint order is preserved so the segment's own
        // direction doesn't flip.
        Line newLine = solution.ConnIsEnd0
            ? Line.CreateBound(solution.TeePoint, solution.NewAnchor)
            : Line.CreateBound(solution.NewAnchor, solution.TeePoint);

        // SolveConnect already proved the branch has a Line location, so this
        // cast cannot realistically fail - but Location is the base type in C#,
        // where Python's duck typing let the assignment stand unguarded.
        if (branch.Location is not LocationCurve branchLocation)
            return ($"the BRANCH {Picking.DescribeElem(branch)} has no movable location curve", false);

        branchLocation.Curve = newLine;
        doc.Regenerate();

        Connector? branchConn = ConnectorAt(branch, solution.TeePoint);
        if (branchConn is null)
        {
            return ("the branch's open connector didn't follow the trim to the connection point "
                    + "(is that end connected to something rigid?)", false);
        }

        (ElementId TypeId, double Thickness)? insulation = GetInsulation(doc, main);

        ElementId? newPieceId = BreakMain(doc, main, solution.TeePoint);
        if (newPieceId is null || newPieceId == ElementId.InvalidElementId)
            return ("Revit could not split the MAIN at the connection point", false);

        Element newPiece = doc.GetElement(newPieceId);
        doc.Regenerate();

        Connector? mainConn = ConnectorAt(main, solution.TeePoint);
        Connector? pieceConn = ConnectorAt(newPiece, solution.TeePoint);
        if (mainConn is null || pieceConn is null)
            return ("could not find the split-point connectors on the MAIN after splitting it", false);

        FamilyInstance tee;
        try
        {
            tee = doc.Create.NewTeeFitting(mainConn, pieceConn, branchConn);
        }
        catch (Exception ex)
        {
            return ExplainRefusal(ex.Message, main, solution, tParam, mainLen, mainDir, keepAngle);
        }

        doc.Regenerate();

        // BreakCurve doesn't insulate the new piece, and the tee is brand new -
        // continue the main's insulation across both. Bonus, not a requirement.
        if (insulation is not null)
        {
            bool isPipe = main is Pipe;
            foreach (ElementId elemId in new[] { newPieceId, tee.Id })
            {
                try
                {
                    if (InsulationLiningBase.GetInsulationIds(doc, elemId).Count == 0)
                    {
                        ApplyInsulation(doc, isPipe, elemId,
                            insulation.Value.TypeId, insulation.Value.Thickness);
                    }
                }
                catch (Exception)
                {
                    // Insulation is a bonus - never fail the tee over it.
                }
            }
        }

        return (null, false);
    }

    /// <summary>
    /// Room a tee needs from the end of the main: twice the main's size, and
    /// never under 150 mm. A tee closer than that has nowhere to put its body.
    /// </summary>
    private static double RoomFt(Element main)
    {
        double size = 0.0;
        foreach (BuiltInParameter bip in new[]
                 {
                     BuiltInParameter.RBS_PIPE_DIAMETER_PARAM, BuiltInParameter.RBS_CURVE_DIAMETER_PARAM,
                     BuiltInParameter.RBS_CURVE_WIDTH_PARAM,
                 })
        {
            Parameter? p = main.get_Parameter(bip);
            if (p is not null && p.HasValue)
                size = Math.Max(size, p.AsDouble());
        }
        return Math.Max(150.0 / MmPerFt, 2.0 * size);
    }

    /// <summary>
    /// Why NewTeeFitting refused, in the words that fit THIS pair, and whether
    /// the tilt-to-90 retry could help.
    ///
    /// It used to give one message for every refusal: "it meets a sloped main ...
    /// check 'Allow Slope Adjustments'", then offer the tilt retry. The in-Revit
    /// suite showed it on a LEVEL main whose tee simply had no room near the main's
    /// end: the advice was about a slope that was not there, and the retry - the
    /// branch was already square to the main - failed the same way and repeated
    /// the whole message. Tilting only helps when keeping the angle is what made
    /// the tee meet the main off 90 degrees.
    /// </summary>
    private static (string Error, bool TeeRefused) ExplainRefusal(
        string revitSays, Element main, ConnectSolution solution, double tParam, double mainLen,
        XYZ mainDir, bool keepAngle)
    {
        double toEnd = Math.Min(tParam, mainLen - tParam);
        if (toEnd < RoomFt(main))
        {
            return ($"Revit refused to insert the tee ({revitSays}).\n\nThe tee would sit only "
                    + $"{toEnd * MmPerFt:F0} mm from the end of the MAIN - there is no room for the "
                    + "fitting's body there. Move the branch along the main, or extend the main past it.",
                false);
        }

        XYZ branchDir = solution.TeePoint.Subtract(solution.NewAnchor).Normalize();
        bool offSquare = Math.Abs(branchDir.DotProduct(mainDir)) > 1.0e-6;
        if (keepAngle && offSquare)
        {
            return ($"Revit refused to insert the tee fitting ({revitSays}).\n\n"
                    + "With the branch kept at its own angle it meets the sloped main at slightly "
                    + "off 90 degrees, so the tee family has to absorb the pitch: open it and "
                    + "CHECK 'Allow Slope Adjustments' on its two run connectors.\n\n"
                    + "Also check on the MAIN's type (Edit Type -> Routing Preferences) that a "
                    + "Tee row exists, its family is loaded, and its Min/Max size range covers "
                    + "both the main and branch sizes.", true);
        }

        return ($"Revit refused to insert the tee fitting ({revitSays}).\n\n"
                + "Check on the MAIN's type (Edit Type -> Routing Preferences) that a Tee row "
                + "exists, its family is loaded, and its Min/Max size range covers both the main "
                + "and branch sizes.", false);
    }

    // ------------------------------------------------------------------ picking

    /// <summary>
    /// Only straight Pipe or Duct elements (after host resolution). Can be
    /// narrowed to one type (so the branch pick matches the main's kind) and can
    /// exclude one element id (the already-picked main).
    /// </summary>
    private sealed class RunPickFilter(Document doc, Type? requireType, ElementId? excludeId)
        : ISelectionFilter
    {
        public bool AllowElement(Element elem)
        {
            try
            {
                elem = Picking.ResolveHost(doc, elem);

                // Compared AFTER host resolution: excludeId is the already-picked
                // MAIN's own id, and hovering that main's INSULATION would
                // otherwise resolve back to the same main without ever matching
                // the raw id - letting the branch pick tee the main into itself.
                if (excludeId is not null && elem.Id == excludeId)
                    return false;

                if (requireType is not null)
                {
                    if (!requireType.IsInstanceOfType(elem))
                        return false;
                }
                else if (elem is not (Pipe or Duct))
                {
                    return false;
                }

                return ElementGeometry.GetLine(elem) is not null;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public bool AllowReference(Reference reference, XYZ point) => true;
    }

    /// <summary>Pick one element; returns it host-resolved, or null on Esc.</summary>
    private static Element? Pick(
        Document doc, IPicker picker, string prompt, Type? requireType = null, ElementId? excludeId = null)
    {
        Element? picked = picker.PickElement(prompt, new RunPickFilter(doc, requireType, excludeId)).Element;
        return picked is null ? null : Picking.ResolveHost(doc, picked);
    }

    /// <summary>The pick filter, for the test suite.</summary>
    internal static ISelectionFilter PickFilter(Document doc, Type? requireType = null, ElementId? excludeId = null)
        => new RunPickFilter(doc, requireType, excludeId);

    // --------------------------------------------------------------------- entry

    /// <summary>
    /// One transaction per attempt: commits on success, rolls the whole pair
    /// back on any failure.
    /// </summary>
    private static (string? Error, bool TeeRefused) Attempt(
        Document doc, Element main, Element branch, bool keepAngle)
    {
        using Transaction t = new(doc, "Tee Into Main");
        t.Start();

        string? err;
        bool teeRefused;
        try
        {
            (err, teeRefused) = TeeIntoMain(doc, main, branch, keepAngle);
        }
        catch (Exception ex)
        {
            t.RollBack();
            return ($"Unexpected error:\n{ex.Message}", false);
        }

        if (err is null)
        {
            // A pair Revit rolls back at commit is not connected, whatever it looked
            // like a moment ago - never count it.
            return t.Commit() == TransactionStatus.Committed
                ? (null, false)
                : ("Revit rolled the tee back when it was committed - see the message Revit showed.", false);
        }

        t.RollBack();
        return (err, teeRefused);
    }

    public static void Run(UIDocument uidoc) => Run(uidoc.Document, new RevitPicker(uidoc));

    /// <summary>The pick loop, on any picker. Returns how many pairs were connected.</summary>
    internal static int Run(Document doc, IPicker picker)
    {
        int connected = 0;

        while (true)
        {
            Element? main = Pick(
                doc, picker, $"Pick the MAIN run to tee into ({connected} connected so far, Esc to finish)");
            if (main is null)
                break;

            Element? branch = Pick(
                doc, picker, "Pick the BRANCH to connect (Esc to re-pick main)",
                requireType: main.GetType(), excludeId: main.Id);
            if (branch is null)
                continue;   // Esc on 2nd pick: back to main pick

            if (!ConfirmAnchorShift(doc, main, branch))
                continue;   // declined - back to picking the main again

            (string? err, bool teeRefused) = Attempt(doc, main, branch, keepAngle: true);
            if (err is null)
            {
                connected++;
                continue;
            }

            // The branch's angle was preserved but the tee family couldn't
            // absorb the main's pitch. Offer the old geometry rather than
            // leaving the pair undone - it succeeds, at the cost of tilting the
            // branch by the main's slope.
            if (teeRefused)
            {
                bool retry = RibbitUi.Confirm(
                    err + "\n\nRetry by TILTING the branch to exactly 90 degrees to the main? "
                    + "It will connect, but the branch takes on the main's slope instead of "
                    + "staying plumb.", DialogTitle);

                if (!retry)
                    continue;   // declined - the message has already been shown

                (err, _) = Attempt(doc, main, branch, keepAngle: false);
                if (err is null)
                {
                    connected++;
                    continue;
                }
            }

            RibbitUi.Alert("Cannot connect: " + err, DialogTitle);
        }

        return connected;
    }

    /// <summary>
    /// Keeping the branch's angle means its ANCHOR (the far, non-connecting
    /// end) is what slides sideways to reach the main. If something is already
    /// attached there and it isn't a pipe or fitting (a sprinkler, fixture,
    /// piece of equipment), this would otherwise drag it the same distance with
    /// no check and no prompt — the one silent head-move in this extension. Ask
    /// first. keepAngle=false never moves the anchor at all, so this only needs
    /// checking once, before the first attempt.
    /// </summary>
    private static bool ConfirmAnchorShift(Document doc, Element main, Element branch)
    {
        (ConnectSolution? solution, string? solveErr) = SolveConnect(main, branch, keepAngle: true);
        if (solution is null || solveErr is not null)
            return true;   // let the real attempt report the problem

        double shiftLen = solution.NewAnchor.Subtract(solution.Anchor).GetLength();
        Element? neighbor = NeighborAt(branch, solution.Anchor);
        bool isHead = neighbor is not null && !IsInlineElem(neighbor);

        // Joined to a pipe or fitting is NOT harmless either. Measured in the
        // in-Revit suite: a riser tee'd into a branch line below was slid 91 mm,
        // and Revit dragged that whole branch line - 12 ft of pipe and its tee -
        // 91 mm sideways with it, silently. Only a free far end, or a slide too
        // small to see, goes ahead without asking.
        bool joined = neighbor is not null && shiftLen > JoinedShiftAskFt;

        if (!isHead && !joined && shiftLen <= BigShiftWarnFt)
            return true;

        string what = isHead ? Picking.DescribeElem(neighbor!) : "the branch's far end";
        string headNote = isHead
            ? "\n\nThat end is not a pipe or fitting - its PLAN POSITION will move with the branch."
            : joined
                ? $"\n\nThat end is joined to {Picking.DescribeElem(neighbor!)} - Revit drags it, and "
                  + "everything rigidly joined beyond it, the same distance."
                : "";

        return RibbitUi.Confirm(
            $"Connecting will slide {what} sideways by {shiftLen * MmPerFt:F0} mm to meet the "
            + $"main.{headNote}\n\nProceed?", DialogTitle);
    }
}
