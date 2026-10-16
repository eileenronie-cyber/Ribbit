namespace Ribbit.Geometry;

public enum TurnKind
{
    /// <summary>Turned sideways relative to the run's own heading.</summary>
    Plan,

    /// <summary>Tilted up/down relative to the run's own heading.</summary>
    Vertical,

    /// <summary>World-axis fallback, used when the run is dead vertical.</summary>
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
    /// Two EQUALLY-SIZED turns whose on-screen directions are closer together
    /// than this cannot be told apart by clicking — not at this click, not at
    /// any click, because they occupy the same few pixels of the view.
    ///
    /// This is a property of the VIEW, not of the click, and that distinction
    /// is the whole point. Comparing the two candidates' SCORES instead looks
    /// equivalent and is not: two nearly-parallel candidates score similarly
    /// across a very wide range of click directions, so a score-margin test
    /// fires on ordinary side clicks the user aimed perfectly well. Asking
    /// whether the options are separable at all fires only when they genuinely
    /// are not.
    ///
    /// Different SIZES of turn are separated deliberately by the 45-over-90
    /// preference above, so only same-size pairs are checked.
    ///
    /// Measured on a real model: a plan 45 and a vertical 45 3.7 degrees apart
    /// on screen (unclickable — must ask), against a plan 45 and a vertical 45
    /// 24.4 degrees apart in the same view (perfectly aimable — must not).
    /// </summary>
    public static readonly double MinScreenSeparationRad = 10.0 * Math.PI / 180.0;

    /// <summary>How close to dead vertical counts as "no horizontal heading".</summary>
    public static readonly double VerticalTolRad = 0.5 * Math.PI / 180.0;
    public static readonly double VerticalHorizEps = Math.Sin(VerticalTolRad);

    /// <summary>
    /// A turn that comes out within this of true vertical is snapped to exactly
    /// plumb (world +/-Z). Turning UP or DOWN off a SLOPED run would otherwise
    /// carry the run's slope through the 90-degree bend, so the "riser" lands a
    /// couple degrees off plumb and Revit reports it as a sloped pipe. 20
    /// degrees clears any real gravity/drain slope with margin but is well
    /// short of a deliberate 45-degree bend, so only genuine risers get snapped.
    /// </summary>
    public static readonly double VerticalSnapTolRad = 20.0 * Math.PI / 180.0;
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
    /// world directions directly — +/-X, +/-Y, and the four 45-degree diagonals
    /// between them — plus their 45-degree bisectors with runDir.
    /// </summary>
    public static List<TurnCandidate> TurnCandidates(Vec3 runDir)
    {
        List<TurnCandidate> candidates = [];

        Vec3 horiz = new(runDir.X, runDir.Y, 0.0);
        if (horiz.GetLength() <= VerticalHorizEps)
        {
            foreach (Vec3 a in HorizontalAxes)
                candidates.Add(new TurnCandidate(a, 90, TurnKind.Fallback));
            foreach (Vec3 a in HorizontalAxes)
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
    /// Among the 45/90-degree turn candidates for runDir, return whichever
    /// one's ON-SCREEN direction best matches the click's on-screen position
    /// relative to the connector. Returns the candidate on a clear pick, or a
    /// reason if nothing qualifies.
    ///
    /// deadZone is the on-screen radius around the connector within which the
    /// click is taken to mean "no direction at all" rather than being resolved
    /// to whichever way its few inches of offset happened to point. Both that
    /// and a backwards click leave the direction unresolved, which is the
    /// caller's cue to offer the fallback menu.
    ///
    /// Ties (within TieRatio of the click length) go to the smaller (45-degree)
    /// angle - EXCEPT when the tied turns lie on the same line on screen (a
    /// 45-degree candidate can be screen-collinear with a 90-degree one, even
    /// though they're different in 3D). No click can separate those, so one of
    /// the two is reachable only from the menu whatever is chosen; the one
    /// lying flatter in the view wins, because that is the one the user can
    /// see and aim at. (The old reasoning, "the 90 is reachable from other click
    /// positions", is false for a collinear pair: off a riser in a plan view it
    /// left every horizontal 90 unclickable.)
    /// </summary>
    public static (TurnCandidate? Chosen, string? Reason) PickTurnDirection(
        Vec3 runDir,
        (double U, double V) clickUvDelta,
        Vec3 right,
        Vec3 up,
        double deadZone)
    {
        List<TurnCandidate> candidates = TurnCandidates(runDir);
        double clickLen = Math.Sqrt(clickUvDelta.U * clickUvDelta.U + clickUvDelta.V * clickUvDelta.V);

        // ScreenU/ScreenV are the candidate's UNIT direction on screen, kept so
        // two candidates can be compared against each other later and not just
        // against the click.
        List<(double Score, TurnCandidate Candidate, double ScreenU, double ScreenV, double ScreenMag)> visible = [];
        foreach (TurnCandidate c in candidates)
        {
            (double u, double v) = ScreenUv(c.Direction, right, up);
            double mag = Math.Sqrt(u * u + v * v);
            if (mag < ScreenMinProj)
                continue;   // points nearly into/out of the screen - not reliably clickable from here

            // Projection of the click's screen vector onto this candidate's own
            // screen direction (normalized) - a fair angular comparison across
            // candidates regardless of how foreshortened each looks on screen.
            double score = (u * clickUvDelta.U + v * clickUvDelta.V) / mag;
            visible.Add((score, c, u / mag, v / mag, mag));
        }

        // A turn on the SAME screen line as a flatter one can never be clicked:
        // every click scores the two identically. Leave it to the menu, and out
        // of every comparison below - the flatter one is the turn the user can
        // see full length and aim at. A riser in plan is the everyday case:
        // each horizontal 90 has a 45-and-up twin on its line (the rise is into
        // the screen). The old tie-break preferred the 45, so no click could
        // make the flat 90 at all.
        List<(double Score, TurnCandidate Candidate, double ScreenU, double ScreenV, double ScreenMag)> all = [.. visible];
        visible.RemoveAll(e => all.Exists(o => o.ScreenMag - e.ScreenMag > FlatnessEps
                                               && (o.ScreenU * e.ScreenU) + (o.ScreenV * e.ScreenV) > ShadowCos));

        if (visible.Count == 0)
        {
            return (null, "none of the valid turn directions are visible from this view angle "
                          + "- try orbiting the view");
        }

        // Rank once, best first, instead of folding a running best as we go.
        //
        // The previous version reassigned bestScore to the LOWER score whenever
        // the 45-over-90 preference fired, which lowered the bar for every
        // candidate after it — so the winner depended on the order candidates
        // happened to be generated in. Comparing everything against a fixed top
        // score removes that entirely.
        visible.Sort((a, b) => b.Score.CompareTo(a.Score));

        double topScore = visible[0].Score;
        double tieBand = TieRatio * clickLen;

        (double Score, TurnCandidate Candidate, double ScreenU, double ScreenV, double ScreenMag) winner = visible[0];
        foreach ((double Score, TurnCandidate Candidate, double ScreenU, double ScreenV, double ScreenMag) entry in visible)
        {
            if (entry.Score < topScore - tieBand)
                break;   // sorted, so everything after this is outside the band too

            if (entry.Candidate.AngleDeg < winner.Candidate.AngleDeg)
                winner = entry;
        }

        // Two ways a click declines to name a direction, both of which hand off
        // to the fallback menu: inside the dead zone (clicked essentially ON the
        // dead end), or not aligned with any candidate (clicked BACKWARDS along
        // the run - every candidate is at most 90 degrees off the run, so none
        // of them has any backward component to score against).
        if (clickLen < deadZone)
        {
            return (null, "clicked on the open end itself, which doesn't point at any turn "
                          + "direction - click to the side, or pick one from the menu");
        }
        if (winner.Score < AmbiguousRatio * clickLen)
        {
            return (null, "clicked point doesn't clearly indicate a turn direction - click "
                          + "further to the side, or pick one from the menu");
        }

        // Third way a click declines to name a direction, and the one that used
        // to be answered with a silent guess: another turn of the SAME size sits
        // on top of the winner ON SCREEN, so no click could separate them.
        //
        // Note this asks about the two CANDIDATES, not about the click. A click
        // aimed squarely between two well-separated options is a good click and
        // resolves to the nearer one; only options that share the same pixels
        // are unanswerable.
        foreach ((double Score, TurnCandidate Candidate, double ScreenU, double ScreenV, double ScreenMag) entry in visible)
        {
            if (SameTurn(entry.Candidate, winner.Candidate))
                continue;

            // A different SIZE of turn is separated deliberately, above.
            if (entry.Candidate.AngleDeg != winner.Candidate.AngleDeg)
                continue;

            double dot = (entry.ScreenU * winner.ScreenU) + (entry.ScreenV * winner.ScreenV);
            dot = Math.Max(-1.0, Math.Min(1.0, dot));   // acos hates 1+1e-16

            if (Math.Acos(dot) < MinScreenSeparationRad)
            {
                return (null, "two different turns of the same size point almost the same way "
                              + "from this view angle, so no click can choose between them - pick "
                              + "one from the menu, or orbit the view so they separate");
            }
        }

        return (winner.Candidate, null);
    }

    /// <summary>
    /// Two on-screen lengths closer than this count as equally flat in the
    /// view: neither hides the other.
    /// </summary>
    private const double FlatnessEps = 0.01;

    /// <summary>Two screen directions closer than half a degree are the same line: no click can split them.</summary>
    private static readonly double ShadowCos = Math.Cos(0.5 * Math.PI / 180.0);

    /// <summary>Same turn, not merely a similar-scoring one.</summary>
    private static bool SameTurn(TurnCandidate a, TurnCandidate b)
        => a.AngleDeg == b.AngleDeg && a.Direction.DotProduct(b.Direction) > 1.0 - 1.0e-9;

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

    /// <summary>Nearest 8-way screen direction name for a screen-space vector.</summary>
    public static string ScreenOctantLabel(double u, double v)
    {
        string[] names = ["right", "upper-right", "up", "upper-left",
                          "left", "lower-left", "down", "lower-right"];
        int idx = Mod8(RoundHalfAway(Math.Atan2(v, u) * 180.0 / Math.PI / 45.0));
        return names[idx];
    }

    /// <summary>
    /// Plain-language world heading for a direction vector — a cardinal (with
    /// 'and up'/'and down' if it also slopes), or 'straight up'/'straight down'
    /// for a near-vertical one. This is what the fallback menu shows so a turn
    /// can be chosen by where it actually goes in the model, not by where it
    /// lands on screen (which is the whole problem at a straight-on view).
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

    /// <summary>
    /// Menu label for one candidate turn: its world heading plus a hint tying it
    /// back to what's on screen — either which way it points on screen, or, for
    /// the edge-on ones the click can't express, whether it heads into or out of
    /// the screen.
    /// </summary>
    public static string DescribeTurn(
        TurnCandidate candidate, Vec3 right, Vec3 up, Vec3? viewDir)
    {
        (double u, double v) = ScreenUv(candidate.Direction, right, up);
        string hint;
        if (Math.Sqrt(u * u + v * v) < ScreenMinProj)
        {
            double depth = viewDir is null ? 0.0 : candidate.Direction.DotProduct(viewDir.Value);
            hint = depth > 0 ? "out of screen, toward you" : "into screen, away from you";
        }
        else
        {
            hint = ScreenOctantLabel(u, v) + " on screen";
        }

        return $"{candidate.AngleDeg}deg turn  ->  {WorldDirLabel(candidate.Direction)}   ({hint})";
    }

    public static bool IsEdgeOn(Vec3 direction, Vec3 right, Vec3 up)
    {
        (double u, double v) = ScreenUv(direction, right, up);
        return Math.Sqrt(u * u + v * v) < ScreenMinProj;
    }

    /// <summary>Python's % always returns non-negative; C#'s can go negative.</summary>
    private static int Mod8(int i) => ((i % 8) + 8) % 8;

    /// <summary>
    /// IronPython 2.7's round() rounds halves AWAY FROM ZERO, while C#'s
    /// Math.Round defaults to banker's rounding (to even). They disagree on
    /// exact octant boundaries — a heading of precisely 22.5 degrees would land
    /// on a different compass label. Match Python so the menu labels don't
    /// silently shift.
    /// </summary>
    private static int RoundHalfAway(double value)
        => (int)Math.Round(value, MidpointRounding.AwayFromZero);
}
