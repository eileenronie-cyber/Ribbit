using Ribbit.Geometry;
using Xunit;

namespace Ribbit.Tests;

/// <summary>
/// ElbowByClick shipped with no offline tests, so unlike the mepalignlib suite
/// these are new rather than ported. Each one pins an invariant its module
/// docstring explicitly claims — above all that the turn candidates are
/// EXACTLY 45/90 degrees off the run for ANY run orientation, which is the
/// whole reason it builds bend axes from cross products.
/// </summary>
public class ElbowMathTests
{
    private const double Tol = 1e-9;

    private static void AssertVecEqual(Vec3 expected, Vec3 actual, double tol = Tol)
    {
        Assert.Equal(expected.X, actual.X, tol);
        Assert.Equal(expected.Y, actual.Y, tol);
        Assert.Equal(expected.Z, actual.Z, tol);
    }

    // ------------------------------------------------------------- Vec3.Rotate
    // Stands in for Transform.CreateRotation(...).OfVector(...), so it has to
    // follow the right-hand rule the same way.

    [Fact]
    public void Rotate_XAboutZ_By90_GivesY()
    {
        Vec3 r = new Vec3(1, 0, 0).Rotate(Vec3.BasisZ, Math.PI / 2.0);
        AssertVecEqual(new Vec3(0, 1, 0), r);
    }

    [Fact]
    public void Rotate_AboutOwnAxis_IsIdentity()
    {
        Vec3 v = new Vec3(0, 0, 1);
        AssertVecEqual(v, v.Rotate(Vec3.BasisZ, 1.234));
    }

    [Fact]
    public void Rotate_PreservesLength()
    {
        Vec3 v = new Vec3(3, -4, 12);           // length 13
        Vec3 r = v.Rotate(new Vec3(1, 2, 3), 0.77);
        Assert.Equal(13.0, r.GetLength(), 1e-9);
    }

    // -------------------------------------------------------- TurnCandidates

    [Theory]
    // horizontal, axis-aligned
    [InlineData(1, 0, 0)]
    // horizontal, diagonal in plan
    [InlineData(1, 1, 0)]
    // sloped - the case the cross-product construction exists for
    [InlineData(1, 0, 0.25)]
    [InlineData(2, -1, 0.5)]
    public void TurnCandidates_AreExactly45Or90FromTheRun(double x, double y, double z)
    {
        Vec3 runDir = new Vec3(x, y, z).Normalize();

        List<TurnCandidate> candidates = ElbowMath.TurnCandidates(runDir);

        Assert.Equal(8, candidates.Count);
        foreach (TurnCandidate c in candidates)
        {
            double actualDeg = runDir.AngleTo(c.Direction) * 180.0 / Math.PI;
            Assert.Equal(c.AngleDeg, actualDeg, 1e-6);
            Assert.Equal(1.0, c.Direction.GetLength(), 1e-9);
        }
    }

    [Fact]
    public void TurnCandidates_HorizontalRun_HasFourPlanAndFourVertical()
    {
        List<TurnCandidate> candidates = ElbowMath.TurnCandidates(new Vec3(1, 0, 0));

        Assert.Equal(4, candidates.FindAll(c => c.Kind == TurnKind.Plan).Count);
        Assert.Equal(4, candidates.FindAll(c => c.Kind == TurnKind.Vertical).Count);
        Assert.Empty(candidates.FindAll(c => c.Kind == TurnKind.Fallback));
    }

    // A dead-vertical run has no heading to build bend axes from, so it falls
    // back to the eight horizontal world directions plus their 45-degree
    // bisectors with the run - 16 total, all Fallback.
    [Fact]
    public void TurnCandidates_DeadVerticalRun_FallsBackToSixteenWorldDirections()
    {
        List<TurnCandidate> candidates = ElbowMath.TurnCandidates(new Vec3(0, 0, 1));

        Assert.Equal(16, candidates.Count);
        Assert.All(candidates, c => Assert.Equal(TurnKind.Fallback, c.Kind));
        Assert.Equal(8, candidates.FindAll(c => c.AngleDeg == 90).Count);
        Assert.Equal(8, candidates.FindAll(c => c.AngleDeg == 45).Count);
    }

    // ------------------------------------------------------------ RunDirection

    [Fact]
    public void RunDirection_PointsFromFarEndTowardOpenConnector()
    {
        // Line runs 0->10 along X; the open connector sits at the x=10 end, so
        // the run is heading +X.
        Vec3? dir = ElbowMath.RunDirection(
            new Vec3(0, 0, 0), new Vec3(10, 0, 0), new Vec3(10, 0, 0));

        Assert.NotNull(dir);
        AssertVecEqual(new Vec3(1, 0, 0), dir!.Value);
    }

    [Fact]
    public void RunDirection_OpenConnectorAtStart_ReversesHeading()
    {
        Vec3? dir = ElbowMath.RunDirection(
            new Vec3(0, 0, 0), new Vec3(10, 0, 0), new Vec3(0, 0, 0));

        Assert.NotNull(dir);
        AssertVecEqual(new Vec3(-1, 0, 0), dir!.Value);
    }

    [Fact]
    public void RunDirection_ZeroLengthRun_ReturnsNull()
    {
        Vec3? dir = ElbowMath.RunDirection(
            new Vec3(4, 4, 4), new Vec3(4, 4, 4), new Vec3(4, 4, 4));

        Assert.Null(dir);
    }

    // ------------------------------------------------------------- SnapToPlumb

    // A 90-degree turn up off a 1-in-12 sloped run lands a few degrees off
    // plumb; that is exactly the case the snap exists to fix.
    [Fact]
    public void SnapToPlumb_NearVerticalFromSlopedRun_BecomesExactlyPlumb()
    {
        Vec3 nearlyUp = new Vec3(0.05, 0.0, 1.0).Normalize();   // ~2.9 deg off plumb

        Vec3 snapped = ElbowMath.SnapToPlumb(nearlyUp);

        AssertVecEqual(new Vec3(0, 0, 1), snapped);
    }

    [Fact]
    public void SnapToPlumb_NearVerticalDownward_KeepsSign()
    {
        Vec3 nearlyDown = new Vec3(0.05, 0.0, -1.0).Normalize();

        AssertVecEqual(new Vec3(0, 0, -1), ElbowMath.SnapToPlumb(nearlyDown));
    }

    // A deliberate 45-degree bend is well outside the 20-degree window and must
    // survive untouched - snapping it would silently turn it into a riser.
    [Fact]
    public void SnapToPlumb_FortyFiveDegreeTurn_IsLeftAlone()
    {
        Vec3 fortyFive = new Vec3(1, 0, 1).Normalize();

        AssertVecEqual(fortyFive, ElbowMath.SnapToPlumb(fortyFive));
    }

    [Fact]
    public void SnapToPlumb_HorizontalTurn_IsLeftAlone()
    {
        Vec3 horizontal = new(0, 1, 0);

        AssertVecEqual(horizontal, ElbowMath.SnapToPlumb(horizontal));
    }

    // --------------------------------------------------------------- labelling

    [Theory]
    [InlineData(1, 0, 0, "East")]
    [InlineData(0, 1, 0, "North")]
    [InlineData(-1, 0, 0, "West")]
    [InlineData(0, -1, 0, "South")]
    [InlineData(1, 1, 0, "North-East")]
    [InlineData(-1, -1, 0, "South-West")]
    public void WorldDirLabel_NamesTheCardinal(double x, double y, double z, string expected)
        => Assert.Equal(expected, ElbowMath.WorldDirLabel(new Vec3(x, y, z).Normalize()));

    [Fact]
    public void WorldDirLabel_NearVertical_ReadsStraightUpOrDown()
    {
        Assert.Equal("straight up", ElbowMath.WorldDirLabel(new Vec3(0, 0, 1)));
        Assert.Equal("straight down", ElbowMath.WorldDirLabel(new Vec3(0, 0, -1)));
    }

    [Fact]
    public void WorldDirLabel_SlopedCardinal_GetsUpDownSuffix()
    {
        Assert.Equal("East and up", ElbowMath.WorldDirLabel(new Vec3(1, 0, 0.5).Normalize()));
        Assert.Equal("East and down", ElbowMath.WorldDirLabel(new Vec3(1, 0, -0.5).Normalize()));
    }

    [Theory]
    [InlineData(1, 0, "right")]
    [InlineData(0, 1, "up")]
    [InlineData(-1, 0, "left")]
    [InlineData(0, -1, "down")]
    [InlineData(1, 1, "upper-right")]
    [InlineData(-1, -1, "lower-left")]
    public void ScreenOctantLabel_NamesTheOctant(double u, double v, string expected)
        => Assert.Equal(expected, ElbowMath.ScreenOctantLabel(u, v));

    // Negative angles must wrap the same way Python's % does; a naive C# %
    // would index out of the names array.
    [Fact]
    public void ScreenOctantLabel_NegativeAngles_WrapLikePython()
    {
        Assert.Equal("lower-right", ElbowMath.ScreenOctantLabel(1, -1));
        Assert.Equal("down", ElbowMath.ScreenOctantLabel(0, -1));
    }

    // ---------------------------------------------------------- PickTurnDirection
    // Screen basis: a plan view looking straight down - right = +X, up = +Y.

    private static readonly Vec3 Right = Vec3.BasisX;
    private static readonly Vec3 Up = Vec3.BasisY;

    [Fact]
    public void PickTurnDirection_ClickToTheSide_PicksThatNinetyDegreeTurn()
    {
        Vec3 runDir = new(1, 0, 0);                 // heading East
        // Click well to the north of the open end - that is the +Y plan turn.
        (TurnCandidate? chosen, string? reason) = ElbowMath.PickTurnDirection(
            runDir, (0.0, 10.0), Right, Up, deadZone: 0.5);

        Assert.Null(reason);
        Assert.NotNull(chosen);
        Assert.Equal(90, chosen!.Value.AngleDeg);
        AssertVecEqual(new Vec3(0, 1, 0), chosen.Value.Direction, 1e-9);
    }

    [Fact]
    public void PickTurnDirection_DiagonalClick_PrefersTheFortyFive()
    {
        Vec3 runDir = new(1, 0, 0);
        // Halfway between straight-ahead (+X) and perpendicular (+Y).
        (TurnCandidate? chosen, string? reason) = ElbowMath.PickTurnDirection(
            runDir, (7.0, 7.0), Right, Up, deadZone: 0.5);

        Assert.Null(reason);
        Assert.NotNull(chosen);
        Assert.Equal(45, chosen!.Value.AngleDeg);
    }

    // Clicking inside the dead zone means "no direction" - the caller's cue to
    // open the fallback menu.
    [Fact]
    public void PickTurnDirection_InsideDeadZone_DeclinesWithReason()
    {
        (TurnCandidate? chosen, string? reason) = ElbowMath.PickTurnDirection(
            new Vec3(1, 0, 0), (0.01, 0.01), Right, Up, deadZone: 1.0);

        Assert.Null(chosen);
        Assert.NotNull(reason);
        Assert.Contains("open end itself", reason);
    }

    // No candidate is more than 90 degrees off the run, so a backwards click
    // matches none of them.
    [Fact]
    public void PickTurnDirection_BackwardsClick_DeclinesWithReason()
    {
        (TurnCandidate? chosen, string? reason) = ElbowMath.PickTurnDirection(
            new Vec3(1, 0, 0), (-10.0, 0.0), Right, Up, deadZone: 0.5);

        Assert.Null(chosen);
        Assert.NotNull(reason);
        Assert.Contains("doesn't clearly indicate", reason);
    }

    // ------------------------------------------- same-size near-ties (the 2026-08-15 defect)
    //
    // A view angle that puts a PLAN 45 and a VERTICAL 45 within a few degrees of
    // each other on screen. Both are 45s, so the 45-over-90 preference has
    // nothing to say, and no click can separate them - the tool used to pick one
    // silently, decided by the order candidates were generated in.
    //
    // Measured on a real model: North-West 45 and West-and-up 45 landed 3.7
    // degrees apart, contesting a 49-degree arc of click directions.

    /// <summary>
    /// A screen basis tilted so the vertical turns project alongside the plan
    /// ones instead of edge-on, which is what a real orbited 3D view does.
    /// </summary>
    private static readonly Vec3 TiltedRight = new Vec3(0.947, 0.321, 0.0).Normalize();

    private static readonly Vec3 TiltedUp = new Vec3(-0.152, 0.448, 0.881).Normalize();

    [Fact]
    public void PickTurnDirection_TwoSameSizeTurnsNearlyIdenticalOnScreen_AsksInsteadOfGuessing()
    {
        Vec3 runDir = new(-1, 0, 0);   // heading West, as measured on the real pipe

        // Aimed into the arc the plan 45 and the vertical 45 both claim - they
        // are 3.7 degrees apart on screen in this view.
        (double U, double V) click = ScreenDirection(134.0, 10.0);

        (TurnCandidate? chosen, string? reason) = ElbowMath.PickTurnDirection(
            runDir, click, TiltedRight, TiltedUp, deadZone: 0.5);

        Assert.Null(chosen);
        Assert.NotNull(reason);
        Assert.Contains("almost the same way", reason);
    }

    // In a PLAN view the two vertical 45s project onto exactly the same screen
    // direction - along the run - with equal magnitude. Their scores are
    // identical at every click angle, so clicking straight ahead used to be a
    // coin flip between a 45 up and a 45 down.
    [Fact]
    public void PickTurnDirection_PlanViewClickAlongTheRun_AsksRatherThanFlipUpVersusDown()
    {
        (TurnCandidate? chosen, string? reason) = ElbowMath.PickTurnDirection(
            new Vec3(1, 0, 0), ScreenDirection(0.0, 10.0), Right, Up, deadZone: 0.5);

        Assert.Null(chosen);
        Assert.NotNull(reason);
        Assert.Contains("almost the same way", reason);
    }

    // THE REGRESSION GUARD. An earlier version of this check compared the two
    // candidates' SCORES, which are close across a wide arc whenever the
    // candidates are nearly parallel - so it interrupted ordinary side clicks
    // that were aimed perfectly well. Separation is a property of the view; the
    // click is not part of it.
    [Theory]
    [InlineData(90.0)]     // square to the run
    [InlineData(120.0)]
    [InlineData(150.0)]
    [InlineData(210.0)]
    [InlineData(240.0)]
    [InlineData(270.0)]
    public void PickTurnDirection_OrdinarySideClickInPlanView_DecidesWithoutAsking(double clickDeg)
    {
        (TurnCandidate? chosen, string? reason) = ElbowMath.PickTurnDirection(
            new Vec3(1, 0, 0), ScreenDirection(clickDeg, 10.0), Right, Up, deadZone: 0.5);

        Assert.Null(reason);
        Assert.NotNull(chosen);
    }

    [Fact]
    public void PickTurnDirection_ClearWinner_StillDecidesWithoutAsking()
    {
        // The guard must not turn into "always ask": a click squarely at one
        // candidate, in the same awkward view, still resolves.
        Vec3 runDir = new(-1, 0, 0);
        (double U, double V) click = ScreenDirection(270.0, 10.0);   // straight down on screen

        (TurnCandidate? chosen, string? reason) = ElbowMath.PickTurnDirection(
            runDir, click, TiltedRight, TiltedUp, deadZone: 0.5);

        Assert.Null(reason);
        Assert.NotNull(chosen);
        Assert.Equal(90, chosen!.Value.AngleDeg);
        AssertVecEqual(new Vec3(0, 0, -1), chosen.Value.Direction, 1e-6);
    }


    // A riser seen in PLAN: every horizontal 90 has a 45-and-up twin pointing
    // the same way on screen (the 45's rise is into the screen), so no click can
    // tell them apart. The in-plane 90 is the turn the user can SEE full length,
    // and the one they almost always mean off the top of a riser; the 45 stays
    // reachable from the menu. Before the fix the 45 always won, silently: in a
    // plan view the horizontal 90 off a riser could not be clicked at all.
    [Theory]
    [InlineData(0.0, 1.0, 0.0)]      // click north
    [InlineData(1.0, 0.0, 0.0)]      // click east
    [InlineData(-1.0, -1.0, 0.0)]    // click south-west
    public void PickTurnDirection_RiserInPlan_ClickTurnsItHorizontally(double u, double v, double _)
    {
        Vec3 runDir = new(0, 0, 1);                 // a riser, open at the top
        (TurnCandidate? chosen, string? reason) = ElbowMath.PickTurnDirection(
            runDir, (u * 6, v * 6), Right, Up, deadZone: 0.5);

        Assert.Null(reason);
        Assert.NotNull(chosen);
        Assert.Equal(90, chosen!.Value.AngleDeg);
        AssertVecEqual(new Vec3(u, v, 0).Normalize(), chosen.Value.Direction, 1e-9);
    }

    // The 45-over-90 preference is still right where the two are NOT the same
    // line on screen: a diagonal click off a horizontal run in plan is a 45.
    [Fact]
    public void PickTurnDirection_RiserInPlan_TheFortyFiveIsStillInTheMenu()
    {
        List<TurnCandidate> all = ElbowMath.TurnCandidates(new Vec3(0, 0, 1));
        Assert.Contains(all, c => c.AngleDeg == 45 && c.Direction.Z > 0.7 && c.Direction.Y > 0.7);
    }

    // The old winner-selection loop reassigned its running best to the LOWER
    // score whenever the 45-preference fired, lowering the bar for every
    // candidate after it - so the answer depended on generation order. Feeding
    // the same click through a reversed screen basis exercises a different
    // ordering of equally-scoring candidates; the chosen SIZE must not change.
    [Theory]
    [InlineData(0.0)]
    [InlineData(45.0)]
    [InlineData(90.0)]
    [InlineData(200.0)]
    [InlineData(315.0)]
    public void PickTurnDirection_IsNotDecidedByCandidateOrder(double clickDeg)
    {
        Vec3 runDir = new(1, 0, 0);
        (double U, double V) click = ScreenDirection(clickDeg, 10.0);

        (TurnCandidate? a, string? reasonA) =
            ElbowMath.PickTurnDirection(runDir, click, Right, Up, deadZone: 0.5);

        // Same geometry, opposite handedness of the screen basis: the candidate
        // list is unchanged but the scores tie differently as they are visited.
        (double U, double V) mirrored = (click.U, -click.V);
        (TurnCandidate? b, string? reasonB) =
            ElbowMath.PickTurnDirection(runDir, mirrored, Right, Up.Negate(), deadZone: 0.5);

        Assert.Equal(reasonA is null, reasonB is null);
        if (a is null || b is null)
            return;

        Assert.Equal(a.Value.AngleDeg, b.Value.AngleDeg);
        AssertVecEqual(a.Value.Direction, b.Value.Direction, 1e-9);
    }

    private static (double U, double V) ScreenDirection(double degrees, double length)
    {
        double rad = degrees * Math.PI / 180.0;
        return (Math.Cos(rad) * length, Math.Sin(rad) * length);
    }
}
