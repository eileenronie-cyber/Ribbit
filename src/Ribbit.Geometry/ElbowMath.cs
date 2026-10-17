namespace Ribbit.Geometry;

public enum TurnKind
{
    /// <summary>Turned sideways relative to the run's own heading.</summary>
    Plan,

    /// <summary>Tilted up/down relative to the run's own heading.</summary>
    Vertical,

    /// <summary>Building-axis fallback, used when the run is dead vertical.</summary>
    Fallback,
}

public readonly record struct TurnCandidate(Vec3 Direction, int AngleDeg, TurnKind Kind);

/// <summary>
/// Pure geometry behind Elbow By Click. Ported from
/// _Ribbit.tab/Tools.panel/ElbowByClick.pushbutton/script.py.
///
/// Everything here is screen/vector math with no Revit types, which is what
/// makes the tool's hardest part — matching a 2D click against 3D turn
/// candidates — testable without a Revit session.
///
/// Two ways a click is read, and neither ever opens a menu:
///   - a 3D view shows <see cref="ElbowDial"/>, where every turn has its own
///     target and the click takes the nearest one;
///   - a flat view (plan, section, elevation) shows nothing and offers only the
///     turns that lie flat in it (<see cref="PickTurnDirection"/>), which is
///     every turn a click there can aim at without guessing.
/// </summary>
public static class ElbowMath
{
    public const double LenEps = 1.0e-6;                 // feet
    public const double AngleEps = 1.0e-9;               // radians

    /// <summary>Min on-screen projection for a candidate to count as "visible" from this view angle.</summary>
    public const double ScreenMinProj = 0.1;

    /// <summary>Min fraction of the click's screen vector that must align with the chosen axis.</summary>
    public const double AmbiguousRatio = 0.05;

    /// <summary>
    /// Scores within this fraction of each other count as a screen-space tie —
    /// kept tight so the 45-over-90 preference only overrides a CLEARLY
    /// better-scoring 90 when they're nearly identical, not just "close".
    /// </summary>
    public const double TieRatio = 0.01;

    /// <summary>
    /// In a FLAT view a turn is offered only when it lies (nearly) in the view:
    /// its component along the view direction is at most this. 0.55 keeps
    /// everything within about 33 degrees of the screen plane.
    ///
    /// Why: the turns that point INTO the screen are exactly the ones a click
    /// cannot aim at. In a plan view the 45 up and the 45 down off a level run
    /// land on the same screen line with the same length, so every click scored
    /// them identically; off a riser each flat 90 hid a 45-and-up twin. The old
    /// answer was a menu. Now a flat view simply offers the turns it shows full
    /// length, and the rest are made in a 3D view, where the dial gives each of
    /// them a target of its own.
    ///
    /// The up and down twins can never BOTH pass this (that would need the run
    /// sloped more than 15 degrees up and down at once), so no two offered
    /// turns share a screen line.
    /// </summary>
    public const double InViewPlaneMaxDepth = 0.55;

    /// <summary>How close to dead vertical counts as "no horizontal heading".</summary>
    public static readonly double VerticalTolRad = 0.5 * Math.PI / 180.0;
    public static readonly double VerticalHorizEps = Math.Sin(VerticalTolRad);

    /// <summary>
    /// A turn that comes out within this of true vertical is snapped to exactly
    /// plumb (world +/-Z). Turning UP or DOWN off a SLOPED run would otherwise
    /// carry the run's slope through the 90-degree bend, so the "riser" lands a
    /// couple degrees off plumb and Revit reports it as a sloped pipe.
    ///
    /// 5 degrees, not the 20 it used to be. 20 also caught runs sloped ON
    /// PURPOSE (a 15-degree run turned "up" came out plumb, through a 75-degree
    /// bend). The steepest everyday gravity fall, 1:12, is 4.8 degrees, so 5
    /// still snaps every drain and leaves a deliberate slope alone.
    /// </summary>
    public static readonly double VerticalSnapTolRad = 5.0 * Math.PI / 180.0;
    public static readonly double VerticalSnapHorizEps = Math.Sin(VerticalSnapTolRad);

    private static readonly int[] TurnAngles = [45, 90, -45, -90];

    private static readonly Vec3[] HorizontalAxes =
    [
        Vec3.BasisX, Vec3.BasisX.Negate(),
        Vec3.BasisY, Vec3.BasisY.Negate(),
        new Vec3(1, 1, 0).Normalize(), new Vec3(1, -1, 0).Normalize(),
        new Vec3(-1, 1, 0).Normalize(), new Vec3(-1, -1, 0).Normalize(),
    ];

    /// <summary>
    /// Projects a world point/vector onto the view's screen plane —
    /// (rightward amount, upward amount). Dropping the view-direction component
    /// this way is what lets us ignore a pick's depth entirely.
    /// </summary>
    public static (double U, double V) ScreenUv(Vec3 pointOrVec, Vec3 right, Vec3 up)
        => (pointOrVec.DotProduct(right), pointOrVec.DotProduct(up));

    /// <summary>
    /// Unit vector the run is already heading (from its far end toward the open
    /// dead-end connector), or null if the run has zero length.
    /// </summary>
    public static Vec3? RunDirection(Vec3 lineStart, Vec3 lineEnd, Vec3 openConnOrigin)
    {
        Vec3 otherEnd = openConnOrigin.DistanceTo(lineStart) < openConnOrigin.DistanceTo(lineEnd)
            ? lineEnd
            : lineStart;

        Vec3 runVec = openConnOrigin.Subtract(otherEnd);
        if (runVec.GetLength() < LenEps)
            return null;
        return runVec.Normalize();
    }

    /// <summary>True when the run has no horizontal heading (a riser or a drop).</summary>
    public static bool IsDeadVertical(Vec3 runDir)
        => Math.Sqrt((runDir.X * runDir.X) + (runDir.Y * runDir.Y)) <= VerticalHorizEps;

    /// <summary>
    /// The 45/90-degree turns reachable from runDir by rotating it around two
    /// bend axes that are ALWAYS exactly perpendicular to it (by cross-product
    /// construction, so the resulting angle really is 45/90 degrees no matter
    /// how runDir is oriented — not just for horizontal or vertical runs, but
    /// sloped ones too):
    ///   - bendAxis = Z x runDir — horizontal, tilts runDir up/down relative to
    ///     its own heading ("vertical bends").
    ///   - sideAxis = runDir x bendAxis — turns runDir sideways relative to its
    ///     own heading ("plan turns" for a horizontal run; for a sloped run this
    ///     is no longer exactly vertical, but it's still the perpendicular that
    ///     best generalizes "turn sideways").
    ///
    /// A dead-vertical run has no heading for those axes to be built from (the
    /// cross products degenerate to zero), so it falls back to eight horizontal
    /// directions directly — the building's two axes both ways and the four
    /// diagonals between them — plus their 45-degree bisectors with runDir.
    /// <paramref name="frameRad"/> is the building's rotation on plan
    /// (<see cref="DominantFrame"/>). 0 is world X/Y, which used to be the only
    /// choice, so on a building drawn at 30 degrees every turn off a riser missed.
    /// </summary>
    public static List<TurnCandidate> TurnCandidates(Vec3 runDir, double frameRad = 0.0)
    {
        List<TurnCandidate> candidates = [];

        if (IsDeadVertical(runDir))
        {
            List<Vec3> axes = [.. HorizontalAxes.Select(a => frameRad == 0.0 ? a : a.Rotate(Vec3.BasisZ, frameRad))];
            foreach (Vec3 a in axes)
                candidates.Add(new TurnCandidate(a, 90, TurnKind.Fallback));
            foreach (Vec3 a in axes)
                candidates.Add(new TurnCandidate(a.Add(runDir).Normalize(), 45, TurnKind.Fallback));
            return candidates;
        }

        Vec3 bendAxis = Vec3.BasisZ.CrossProduct(runDir).Normalize();
        Vec3 sideAxis = runDir.CrossProduct(bendAxis).Normalize();

        foreach (int angle in TurnAngles)
        {
            double rad = angle * Math.PI / 180.0;
            candidates.Add(new TurnCandidate(
                runDir.Rotate(sideAxis, rad).Normalize(), Math.Abs(angle), TurnKind.Plan));
            candidates.Add(new TurnCandidate(
                runDir.Rotate(bendAxis, rad).Normalize(), Math.Abs(angle), TurnKind.Vertical));
        }
        return candidates;
    }

    /// <summary>
    /// Does this turn go sideways (stay in the run's own "level" plane) rather
    /// than up or down? The dial colours it by this. A riser's 90s are level
    /// turns; its 45s climb or drop.
    /// </summary>
    public static bool IsSideways(TurnCandidate c)
        => c.Kind == TurnKind.Plan || (c.Kind == TurnKind.Fallback && c.AngleDeg == 90);

    /// <summary>
    /// The building's rotation on plan, from the headings of its grid lines or
    /// of a run that feeds a riser, in radians in [0, pi/2); 0 when none is
    /// given.
    ///
    /// Each heading is folded into a quarter turn (a grid at 120 degrees and one
    /// at 30 describe the same frame), grouped into half-degree bins, and the
    /// fullest bin wins, so a few odd grids do not tilt the answer. The result
    /// is the AVERAGE of that bin, not its centre, so a frame of exactly 30.1
    /// degrees comes back as 30.1 and not 30.0.
    /// </summary>
    public static double DominantFrame(IEnumerable<double> headingsRad)
    {
        const double quarter = Math.PI / 2.0;
        const int bins = 180;
        const double binWidth = quarter / bins;

        List<(int Bin, double Folded)> folded = [];
        foreach (double h in headingsRad)
        {
            double f = h % quarter;
            if (f < 0)
                f += quarter;
            folded.Add((((int)Math.Round(f / binWidth)) % bins, f));
        }

        if (folded.Count == 0)
            return 0.0;

        int best = folded.GroupBy(x => x.Bin)
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key)
            .First().Key;

        // Average as offsets from the bin's centre, so a member just under a
        // quarter turn counts as just under 0 in bin 0, not as 89.9 degrees.
        double centre = best * binWidth;
        double mean = folded.Where(x => x.Bin == best)
            .Select(x => x.Folded - centre > quarter / 2 ? x.Folded - centre - quarter : x.Folded - centre)
            .Average();

        double frame = centre + mean;
        return frame < 0 ? frame + quarter : frame;
    }

    /// <summary>
    /// The click in a FLAT view (plan, section, elevation): among the turns that
    /// lie flat in the view (<see cref="InViewPlaneMaxDepth"/>), whichever one's
    /// on-screen direction best matches the click's position relative to the
    /// open end. Returns the candidate, or a reason to put in the next prompt
    /// when the click names no direction; the tool then asks for another click.
    ///
    /// deadZone is the on-screen radius around the open end inside which a
    /// click means "no direction at all" rather than whichever way its few
    /// inches of offset happened to point.
    ///
    /// Ties (within TieRatio of the click length) go to the smaller (45-degree)
    /// angle: a click exactly between a 45 and a 90 means the gentler turn.
    /// </summary>
    public static (TurnCandidate? Chosen, string? Reason) PickTurnDirection(
        Vec3 runDir,
        (double U, double V) clickUvDelta,
        Vec3 right,
        Vec3 up,
        double deadZone,
        double frameRad = 0.0)
    {
        Vec3 viewDir = right.CrossProduct(up);
        double clickLen = Math.Sqrt(clickUvDelta.U * clickUvDelta.U + clickUvDelta.V * clickUvDelta.V);

        List<(double Score, TurnCandidate Candidate)> visible = [];
        foreach (TurnCandidate c in TurnCandidates(runDir, frameRad))
        {
            if (Math.Abs(c.Direction.DotProduct(viewDir)) > InViewPlaneMaxDepth)
                continue;   // heads into or out of the screen: made in a 3D view

            (double u, double v) = ScreenUv(c.Direction, right, up);
            double mag = Math.Sqrt(u * u + v * v);
            if (mag < ScreenMinProj)
                continue;   // only a broken view basis gets here

            // Projection of the click's screen vector onto this candidate's own
            // screen direction (normalized) - a fair angular comparison across
            // candidates regardless of how foreshortened each looks on screen.
            visible.Add(((u * clickUvDelta.U + v * clickUvDelta.V) / mag, c));
        }

        if (visible.Count == 0)
            return (null, "this view shows no direction to turn in - use a 3D view");

        if (clickLen < deadZone)
            return (null, "that click was on the open end itself");

        // Rank once, best first, and compare everything against a FIXED top
        // score. (An older fold lowered its running best whenever the 45
        // preference fired, so the winner depended on candidate order.)
        visible.Sort((a, b) => b.Score.CompareTo(a.Score));
        double topScore = visible[0].Score;
        double tieBand = TieRatio * clickLen;

        (double Score, TurnCandidate Candidate) winner = visible[0];
        foreach ((double Score, TurnCandidate Candidate) entry in visible)
        {
            if (entry.Score < topScore - tieBand)
                break;   // sorted, so everything after this is outside the band too

            if (entry.Candidate.AngleDeg < winner.Candidate.AngleDeg)
                winner = entry;
        }

        // Clicked BACKWARDS along the run: every turn is at most 90 degrees off
        // the run, so none has any backward component to score against.
        if (winner.Score < AmbiguousRatio * clickLen)
            return (null, "that click was behind the open end - click to the side you want to turn");

        // Clicked exactly BETWEEN two turns of the same size - straight ahead in
        // a plan view sits halfway between the two 45s. The order the sort left
        // them in would decide, which is a coin flip: ask again instead.
        foreach ((double Score, TurnCandidate Candidate) entry in visible)
        {
            if (entry.Score < topScore - tieBand)
                break;

            if (entry.Candidate.AngleDeg == winner.Candidate.AngleDeg
                && entry.Candidate.Direction.DotProduct(winner.Candidate.Direction) < 1.0 - 1.0e-9)
            {
                return (null, "that click was exactly between two turns - click nearer the one you want");
            }
        }

        return (winner.Candidate, null);
    }

    /// <summary>
    /// If a turn lands within VerticalSnapTolRad of true vertical, return it as
    /// exactly plumb (world +/-Z); otherwise return it unchanged.
    ///
    /// Turning up or down off a SLOPED run rotates the run's own heading by 90
    /// degrees, so the result keeps the run's slope and the "riser" comes out
    /// tilted a couple degrees off plumb. Snapping it to world Z makes the riser
    /// truly vertical — the joining elbow just ends up a hair off 90 degrees
    /// instead, which is the correct trade for a plumb riser.
    /// </summary>
    public static Vec3 SnapToPlumb(Vec3 direction)
    {
        double horiz = Math.Sqrt(direction.X * direction.X + direction.Y * direction.Y);
        if (horiz < VerticalSnapHorizEps && Math.Abs(direction.Z) > LenEps)
            return direction.Z > 0 ? new Vec3(0, 0, 1) : new Vec3(0, 0, -1);
        return direction;
    }

    /// <summary>
    /// Plain-language world heading for a direction vector — a cardinal (with
    /// 'and up'/'and down' if it also slopes), or 'straight up'/'straight down'
    /// for a near-vertical one. The which-end question names the ends with it.
    /// </summary>
    public static string WorldDirLabel(Vec3 direction)
    {
        double horiz = Math.Sqrt(direction.X * direction.X + direction.Y * direction.Y);
        if (horiz < 0.1)
            return direction.Z > 0 ? "straight up" : "straight down";

        string[] names = ["East", "North-East", "North", "North-West",
                          "West", "South-West", "South", "South-East"];
        int idx = Mod8(RoundHalfAway(Math.Atan2(direction.Y, direction.X) * 180.0 / Math.PI / 45.0));
        string bas = names[idx];

        if (direction.Z > 0.25 * horiz)
            return bas + " and up";
        if (direction.Z < -0.25 * horiz)
            return bas + " and down";
        return bas;
    }

    /// <summary>Python's % always returns non-negative; C#'s can go negative.</summary>
    private static int Mod8(int i) => ((i % 8) + 8) % 8;

    /// <summary>
    /// IronPython 2.7's round() rounds halves AWAY FROM ZERO, while C#'s
    /// Math.Round defaults to banker's rounding (to even). They disagree on
    /// exact octant boundaries — a heading of precisely 22.5 degrees would land
    /// on a different compass label. Match Python so the labels don't silently
    /// shift.
    /// </summary>
    private static int RoundHalfAway(double value)
        => (int)Math.Round(value, MidpointRounding.AwayFromZero);
}
