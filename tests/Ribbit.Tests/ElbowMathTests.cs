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

    // A riser in a building drawn at 30 degrees: the eight flat turns follow the
    // building, the 45s still climb exactly 45 degrees, and a run that is not
    // dead vertical ignores the frame altogether.
    [Fact]
    public void TurnCandidates_DeadVerticalRun_FollowsTheBuildingFrame()
    {
        double frame = 30.0 * Math.PI / 180.0;
        List<TurnCandidate> candidates = ElbowMath.TurnCandidates(new Vec3(0, 0, 1), frame);

        AssertVecEqual(new Vec3(Math.Cos(frame), Math.Sin(frame), 0), candidates[0].Direction, 1e-12);
        AssertVecEqual(new Vec3(-Math.Sin(frame), Math.Cos(frame), 0), candidates[2].Direction, 1e-12);
        foreach (TurnCandidate c in candidates)
            Assert.Equal(c.AngleDeg, new Vec3(0, 0, 1).AngleTo(c.Direction) * 180.0 / Math.PI, 1e-9);

        List<TurnCandidate> level = ElbowMath.TurnCandidates(new Vec3(1, 0, 0), frame);
        List<TurnCandidate> plain = ElbowMath.TurnCandidates(new Vec3(1, 0, 0));
        for (int i = 0; i < level.Count; i++)
            AssertVecEqual(plain[i].Direction, level[i].Direction, 0);
    }

    [Fact]
    public void IsDeadVertical_OnlyWithinHalfADegreeOfPlumb()
    {
        Assert.True(ElbowMath.IsDeadVertical(new Vec3(0, 0, -1)));
        Assert.True(ElbowMath.IsDeadVertical(new Vec3(0.004, 0, 1).Normalize()));
        Assert.False(ElbowMath.IsDeadVertical(new Vec3(0.02, 0, 1).Normalize()));
    }

    [Fact]
    public void IsSideways_PlanTurnsAndARisersNinetiesOnly()
    {
        List<TurnCandidate> level = ElbowMath.TurnCandidates(new Vec3(1, 0, 0));
        List<TurnCandidate> riser = ElbowMath.TurnCandidates(new Vec3(0, 0, 1));

        Assert.All(level, c => Assert.Equal(c.Kind == TurnKind.Plan, ElbowMath.IsSideways(c)));
        Assert.All(riser, c => Assert.Equal(c.AngleDeg == 90, ElbowMath.IsSideways(c)));
    }

    // ----------------------------------------------------------- DominantFrame

    private static double Deg(double d) => d * Math.PI / 180.0;

    [Fact]
    public void DominantFrame_NoHeadings_IsWorldAxes()
        => Assert.Equal(0.0, ElbowMath.DominantFrame([]));

    // Grids in both directions, either way round, describe one frame - folded
    // into a quarter turn, and averaged rather than rounded to the bin.
    [Fact]
    public void DominantFrame_GridsBothWaysRound_AreOneFrame()
    {
        double f = ElbowMath.DominantFrame([Deg(30.1), Deg(120.1), Deg(-59.9), Deg(210.1)]);

        Assert.Equal(Deg(30.1), f, 12);
    }

    // The fullest bin wins: a couple of odd grids do not tilt the answer.
    [Fact]
    public void DominantFrame_TheMostCommonHeadingWins()
    {
        double f = ElbowMath.DominantFrame([Deg(0), Deg(90), Deg(180), Deg(17), Deg(17.1)]);

        Assert.Equal(0.0, f, 12);
    }

    // Headings a hair either side of the axis (89.9 is -0.1) average ACROSS it,
    // to -0.033 degrees, which is reported as its quarter-turn twin 89.967.
    [Fact]
    public void DominantFrame_EitherSideOfTheAxis_AveragesAcrossIt()
    {
        double f = ElbowMath.DominantFrame([Deg(89.9), Deg(89.9), Deg(0.1)]);

        Assert.Equal(Deg(90.0 - (0.1 / 3.0)), f, 12);
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

    // A run sloped 10 degrees ON PURPOSE, turned up: 10 degrees off plumb is
    // outside the 5-degree window, so it keeps its tilt (the old 20-degree
    // window forced it plumb through an 80-degree bend).
    [Fact]
    public void SnapToPlumb_DeliberateTenDegreeTilt_IsLeftAlone()
    {
        Vec3 tilted = new Vec3(Math.Sin(Deg(10)), 0.0, Math.Cos(Deg(10)));

        AssertVecEqual(tilted, ElbowMath.SnapToPlumb(tilted));
    }

    // A deliberate 45-degree bend is well outside the window and must survive
    // untouched - snapping it would silently turn it into a riser.
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

    // ---------------------------------------------------------- PickTurnDirection
    // The click in a FLAT view. Screen basis: a plan view looking straight
    // down - right = +X, up = +Y.

    private static readonly Vec3 Right = Vec3.BasisX;
    private static readonly Vec3 Up = Vec3.BasisY;

    [Fact]
    public void PickTurnDirection_ClickToTheSide_PicksThatNinetyDegreeTurn()
    {
        Vec3 runDir = new(1, 0, 0);                 // heading East
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
        (TurnCandidate? chosen, string? reason) = ElbowMath.PickTurnDirection(
            new Vec3(1, 0, 0), (7.0, 7.0), Right, Up, deadZone: 0.5);

        Assert.Null(reason);
        Assert.Equal(45, chosen!.Value.AngleDeg);
        AssertVecEqual(new Vec3(1, 1, 0).Normalize(), chosen.Value.Direction, 1e-9);
    }

    // A click between a 45 and a 90 means the gentler turn - also a hair
    // nearer the 90 (67.8), where the 90 ranks first and the 45 must still win.
    [Theory]
    [InlineData(67.5)]
    [InlineData(67.8)]
    public void PickTurnDirection_TieBetweenA45AndA90_GoesToThe45(double clickDeg)
    {
        (TurnCandidate? chosen, string? reason) = ElbowMath.PickTurnDirection(
            new Vec3(1, 0, 0), ScreenDirection(clickDeg, 10.0), Right, Up, deadZone: 0.5);

        Assert.Null(reason);
        Assert.Equal(45, chosen!.Value.AngleDeg);
    }

    [Fact]
    public void PickTurnDirection_InsideDeadZone_AsksAgainWithReason()
    {
        (TurnCandidate? chosen, string? reason) = ElbowMath.PickTurnDirection(
            new Vec3(1, 0, 0), (0.01, 0.01), Right, Up, deadZone: 1.0);

        Assert.Null(chosen);
        Assert.Contains("open end itself", reason);
    }

    // No candidate is more than 90 degrees off the run, so a backwards click
    // matches none of them.
    [Fact]
    public void PickTurnDirection_BackwardsClick_AsksAgainWithReason()
    {
        (TurnCandidate? chosen, string? reason) = ElbowMath.PickTurnDirection(
            new Vec3(1, 0, 0), (-10.0, 0.0), Right, Up, deadZone: 0.5);

        Assert.Null(chosen);
        Assert.Contains("behind the open end", reason);
    }

    // Straight ahead in plan is exactly between the two flat 45s. It used to be
    // a coin flip between a 45 UP and a 45 DOWN (they share that screen line);
    // those are no longer offered in a flat view at all, and the two flat 45s
    // left there are not guessed between either.
    [Fact]
    public void PickTurnDirection_StraightAheadInPlan_AsksAgainRatherThanFlipACoin()
    {
        (TurnCandidate? chosen, string? reason) = ElbowMath.PickTurnDirection(
            new Vec3(1, 0, 0), ScreenDirection(0.0, 10.0), Right, Up, deadZone: 0.5);

        Assert.Null(chosen);
        Assert.Contains("exactly between two turns", reason);
    }

    // The flat-view rule: whatever the click, a plan view never makes a turn
    // that heads into or out of the screen - those are made in a 3D view.
    [Fact]
    public void PickTurnDirection_PlanView_NeverMakesAnUpOrDownTurn()
    {
        for (int deg = 0; deg < 360; deg++)
        {
            (TurnCandidate? chosen, _) = ElbowMath.PickTurnDirection(
                new Vec3(1, 0, 0), ScreenDirection(deg + 0.5, 10.0), Right, Up, deadZone: 0.5);
            if (chosen is not null)
                Assert.Equal(TurnKind.Plan, chosen.Value.Kind);
        }
    }

    // A section looking north (right = east, up = world Z): the turns lying in
    // it are the up and down ones, and those are what a click makes there.
    [Fact]
    public void PickTurnDirection_SectionView_MakesTheUpAndDownTurns()
    {
        (TurnCandidate? rise, _) = ElbowMath.PickTurnDirection(
            new Vec3(1, 0, 0), (0.0, 10.0), Vec3.BasisX, Vec3.BasisZ, deadZone: 0.5);
        (TurnCandidate? downAt45, _) = ElbowMath.PickTurnDirection(
            new Vec3(1, 0, 0), (7.0, -7.0), Vec3.BasisX, Vec3.BasisZ, deadZone: 0.5);

        AssertVecEqual(new Vec3(0, 0, 1), rise!.Value.Direction);
        AssertVecEqual(new Vec3(1, 0, -1).Normalize(), downAt45!.Value.Direction);
        Assert.Equal(TurnKind.Vertical, downAt45.Value.Kind);
    }

    // THE REGRESSION GUARD from 2026-08-15: ordinary side clicks in plan are
    // decided, never asked about.
    [Theory]
    [InlineData(90.0)]
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

    // A riser seen in PLAN: each flat 90 has a 45-and-up twin on its screen
    // line. Only the flat ones lie in the view, so a click turns the riser
    // flat; its 45s are made in a 3D view.
    [Theory]
    [InlineData(0.0, 1.0)]      // click north
    [InlineData(1.0, 0.0)]      // click east
    [InlineData(-1.0, -1.0)]    // click south-west
    public void PickTurnDirection_RiserInPlan_ClickTurnsItHorizontally(double u, double v)
    {
        (TurnCandidate? chosen, string? reason) = ElbowMath.PickTurnDirection(
            new Vec3(0, 0, 1), (u * 6, v * 6), Right, Up, deadZone: 0.5);

        Assert.Null(reason);
        Assert.Equal(90, chosen!.Value.AngleDeg);
        AssertVecEqual(new Vec3(u, v, 0).Normalize(), chosen.Value.Direction, 1e-9);
    }

    // A building drawn at 30 degrees: off a riser, a click along the building's
    // own axis turns exactly along it, not along world X.
    [Fact]
    public void PickTurnDirection_RiserInARotatedBuilding_TurnsAlongTheBuilding()
    {
        double frame = 30.0 * Math.PI / 180.0;
        (TurnCandidate? chosen, string? reason) = ElbowMath.PickTurnDirection(
            new Vec3(0, 0, 1), ScreenDirection(30.0, 6.0), Right, Up, deadZone: 0.5, frameRad: frame);

        Assert.Null(reason);
        AssertVecEqual(new Vec3(Math.Cos(frame), Math.Sin(frame), 0), chosen!.Value.Direction, 1e-12);
    }

    // The same click through a mirrored screen basis visits equally-scoring
    // candidates in a different order; the chosen SIZE must not change.
    [Theory]
    [InlineData(10.0)]
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
        (TurnCandidate? b, string? reasonB) =
            ElbowMath.PickTurnDirection(runDir, (click.U, -click.V), Right, Up.Negate(), deadZone: 0.5);

        Assert.Equal(reasonA is null, reasonB is null);
        if (a is null || b is null)
            return;

        Assert.Equal(a.Value.AngleDeg, b.Value.AngleDeg);
        AssertVecEqual(a.Value.Direction, b.Value.Direction, 1e-9);
    }

    // A broken view basis (no screen axes at all) leaves nothing to click.
    // That has to come back as a reason the tool can show, not a pick.
    [Fact]
    public void PickTurnDirection_NoUsableScreenAxes_ExplainsInsteadOfPicking()
    {
        Vec3 none = new(0, 0, 0);

        (TurnCandidate? chosen, string? reason) =
            ElbowMath.PickTurnDirection(new Vec3(1, 0, 0), (10.0, 0.0), none, none, deadZone: 0.5);

        Assert.Null(chosen);
        Assert.Contains("shows no direction", reason);
    }

    private static (double U, double V) ScreenDirection(double degrees, double length)
    {
        double rad = degrees * Math.PI / 180.0;
        return (Math.Cos(rad) * length, Math.Sin(rad) * length);
    }
}
