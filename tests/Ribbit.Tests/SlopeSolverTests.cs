using Ribbit.Geometry;
using Xunit;

namespace Ribbit.Tests;

/// <summary>
/// Ported 1:1 from lib/slopelib/tests/test_solver.py.
///
/// Two of these pin bugs that were caught in live use rather than in review:
/// the 45-degree-leg trap that motivated STEEP_DEG=30, and the end-anchor sign
/// guard where every fitting read +0.1147 deg except the end elbow at -0.3437.
/// </summary>
public class SlopeSolverTests
{
    private const double Tol = 1e-9;

    private static readonly SlopeParams Params = new(
        AngleTolDeg: 5.0, PlanOffsetTol: 0.02, Slope: 0.002);

    // ------------------------------------------------------------- IsSteep

    // A run pitched at 2 mm/m with a 45-degree leg rising off it: the leg's own
    // angle off horizontal is 45 minus atan(0.002), i.e. a hair under 45 - the
    // old STEEP_DEG=45 would have let it through as "not steep" if it also
    // happened to share the run's plan direction.
    [Fact]
    public void IsSteep_AGenuine45DegreeLeg_IsRejectedAsSteep()
    {
        SlopeSegment leg = new(1, new Vec3(0, 0, 0), new Vec3(1, 0, 1));

        Assert.True(SlopeSolver.IsSteep(leg));
    }

    [Fact]
    public void IsSteep_ANormalBranchPitch_IsNotSteep()
    {
        SlopeSegment flat = new(2, new Vec3(0, 0, 0), new Vec3(10, 0, 0.02));

        Assert.False(SlopeSolver.IsSteep(flat));
    }

    [Fact]
    public void IsSteep_AVerticalRiser_IsSteep()
    {
        SlopeSegment riser = new(3, new Vec3(0, 0, 0), new Vec3(0, 0, 5));

        Assert.True(SlopeSolver.IsSteep(riser));
    }

    // --------------------------------------------------------- RotateAbout

    [Fact]
    public void RotateAbout_Plus90AboutZ_SendsPlusXToPlusY()
    {
        Vec3 r = SlopeSolver.RotateAbout(
            new Vec3(1, 0, 0), new Vec3(0, 0, 0), new Vec3(0, 0, 1), Math.PI / 2.0);

        Assert.Equal(0.0, r.X, 1e-9);
        Assert.Equal(1.0, r.Y, 1e-9);
        Assert.Equal(0.0, r.Z, 1e-9);
    }

    [Fact]
    public void RotateAbout_OffOriginPivot_StillRotatesCorrectly()
    {
        Vec3 r = SlopeSolver.RotateAbout(
            new Vec3(2, 0, 5), new Vec3(1, 0, 5), new Vec3(0, 0, 1), Math.PI / 2.0);

        Assert.Equal(1.0, r.X, 1e-9);
        Assert.Equal(1.0, r.Y, 1e-9);
        Assert.Equal(5.0, r.Z, 1e-9);
    }

    // --------------------------------------------------------- SignedTheta

    // Rotating the OLD direction by the returned theta about axisDir must land
    // on the NEW direction - the same numerical self-check the function performs,
    // re-verified independently here.
    [Fact]
    public void SignedTheta_RotatingOldDirectionByTheta_LandsOnNew()
    {
        Vec3 axis = new(1, 0, 0);
        Vec3 axisDir = SlopeSolver.RotationAxis(axis);

        double theta = SlopeSolver.SignedTheta(axis, 0.0, 0.01, axisDir);
        Vec3 dOld = SlopeSolver.DirectionAtRate(axis, 0.0);
        Vec3 dNew = SlopeSolver.DirectionAtRate(axis, 0.01);

        Vec3 landed = SlopeSolver.RotateAbout(dOld, Vec3.Zero, axisDir, theta);

        Assert.Equal(0.0, landed.DistanceTo(dNew), 1e-6);
    }

    [Fact]
    public void SignedTheta_ReversingThePitchChange_FlipsTheSign()
    {
        Vec3 axis = new(1, 0, 0);
        Vec3 axisDir = SlopeSolver.RotationAxis(axis);

        double up = SlopeSolver.SignedTheta(axis, 0.0, 0.01, axisDir);
        double down = SlopeSolver.SignedTheta(axis, 0.01, 0.0, axisDir);

        Assert.True(up > 0 != down > 0, $"expected opposite signs, got {up} and {down}");
    }

    // -------------------------------------------------------------- RePitch

    private static RePitch FlatRunAnchoredAtNearEnd()
    {
        SlopeSegment seg = new(10, new Vec3(0, 0, 0), new Vec3(20, 0, 0));
        SlopeRun run = new([seg], new Vec3(1, 0, 0), new Vec3(0, 0, 0));
        return new RePitch(run, 0.0, 0.002);
    }

    [Fact]
    public void RePitch_AnchorKeepsItsOwnElevation()
        => Assert.Equal(0.0, FlatRunAnchoredAtNearEnd().NewZ(0.0), Tol);

    [Fact]
    public void RePitch_FarEndRisesBySlopeTimesDevelopedLength()
        => Assert.Equal(0.002 * 20.0, FlatRunAnchoredAtNearEnd().NewZ(20.0), Tol);

    // The live-caught end-elbow sign bug: with an END anchor there is only one
    // arm, so a pivot a hair OUTSIDE the anchored end must still read that arm's
    // rate rather than flipping sign.
    [Fact]
    public void RePitch_EndAnchor_NewRateIsPlusSlopeEverywhereIncludingOutsideTheEnd()
    {
        RePitch pitch = FlatRunAnchoredAtNearEnd();

        Assert.Equal(0.002, pitch.NewRateAt(0.0), Tol);
        Assert.Equal(0.002, pitch.NewRateAt(20.0), Tol);
        Assert.Equal(0.002, pitch.NewRateAt(-0.05), Tol);
    }

    private static RePitch VAnchoredRun()
    {
        SlopeSegment seg = new(11, new Vec3(-10, 0, 0), new Vec3(10, 0, 0));
        SlopeRun run = new([seg], new Vec3(1, 0, 0), new Vec3(0, 0, 0));
        return new RePitch(run, 0.0, 0.002);
    }

    // Anchoring mid-run makes a V: the run rises away from the fixed point in
    // BOTH directions, per the docstring's |station - anchor|.
    [Fact]
    public void RePitch_VAnchor_RisesOnTheFarSide()
        => Assert.Equal(0.002 * 10.0, VAnchoredRun().NewZ(10.0), Tol);

    [Fact]
    public void RePitch_VAnchor_AlsoRisesOnTheNearSide()
        => Assert.Equal(0.002 * 10.0, VAnchoredRun().NewZ(-10.0), Tol);

    [Fact]
    public void RePitch_VAnchor_NewRateFlipsSignEitherSideOfTheAnchor()
    {
        RePitch pitch = VAnchoredRun();

        Assert.Equal(0.002, pitch.NewRateAt(5.0), Tol);
        Assert.Equal(-0.002, pitch.NewRateAt(-5.0), Tol);
    }

    // ------------------------------------------------- Station / PlanOffset

    private static SlopeRun StationRun()
    {
        SlopeSegment seg = new(20, new Vec3(0, 0, 0), new Vec3(10, 0, 0));
        return new SlopeRun([seg], new Vec3(1, 0, 0), new Vec3(0, 0, 0));
    }

    [Fact]
    public void Station_APointOnAxis_ReadsItsXCoordinate()
        => Assert.Equal(7.0, StationRun().Station(new Vec3(7, 0, 0)), Tol);

    // Z must be ignored - slope is rise over PLAN run.
    [Fact]
    public void PlanOffset_APointThreeUnitsOffTheAxis_ReadsThree()
        => Assert.Equal(3.0, StationRun().PlanOffset(new Vec3(7, 3, 99)), Tol);

    // ------------------------------------------------------------- BuildRun

    [Fact]
    public void BuildRun_SteepLeg_IsRejectedNotFlattenedOntoTheRun()
    {
        SlopeSegment flat = new(30, new Vec3(0, 0, 0), new Vec3(10, 0, 0));
        SlopeSegment steep = new(31, new Vec3(10, 0, 0), new Vec3(10, 0, 10));

        (SlopeRun? run, List<(SlopeSegment Segment, string Reason)> rejected) =
            SlopeSolver.BuildRun([flat, steep], Params);

        Assert.NotNull(run);
        Assert.Single(run!.Segments);
        Assert.Equal(30, run.Segments[0].ElemId);
        Assert.Contains(rejected, r => r.Segment.ElemId == 31);
    }

    [Fact]
    public void BuildRun_A90DegreeOffshoot_IsRejectedAsADifferentBranch()
    {
        SlopeSegment flat = new(40, new Vec3(0, 0, 0), new Vec3(10, 0, 0));
        SlopeSegment offshoot = new(41, new Vec3(10, 0, 0), new Vec3(10, 10, 0));

        (SlopeRun? run, List<(SlopeSegment Segment, string Reason)> rejected) =
            SlopeSolver.BuildRun([flat, offshoot], Params);

        Assert.NotNull(run);
        Assert.Single(run!.Segments);
        Assert.Contains(rejected, r => r.Segment.ElemId == 41);
    }

    // Two parallel but offset branch lines picked together must be split - the
    // off-axis one rejected, not flattened onto a shared line.
    [Fact]
    public void BuildRun_AParallelBranchLineOffAxis_IsRejectedNotMerged()
    {
        SlopeSegment a = new(50, new Vec3(0, 0, 0), new Vec3(10, 0, 0));
        SlopeSegment b = new(51, new Vec3(0, 5, 0), new Vec3(10, 5, 0));

        (SlopeRun? run, List<(SlopeSegment Segment, string Reason)> rejected) =
            SlopeSolver.BuildRun([a, b], Params);

        Assert.NotNull(run);
        Assert.Single(run!.Segments);
        Assert.Contains(rejected, r => r.Segment.ElemId == 51);
    }

    // ----------------------------------------- stacked runs, separated in station
    //
    // The overlap pass catches a second branch line lying OVER the same stretch.
    // One lying BEYOND it clears every check and is then flattened onto a single
    // line. Live-caught: two real pipes 659mm apart in Z with a 362mm gap along
    // the run were accepted with zero rejections, and a 2mm/m re-pitch would have
    // dragged the second 807mm.

    [Fact]
    public void BuildRun_StackedRunBeyondTheFirst_IsRejectedNotFlattened()
    {
        // Same plan line, 2ft apart in Z, 1ft of run between them.
        SlopeSegment a = new(60, new Vec3(0, 0, 0), new Vec3(10, 0, 0));
        SlopeSegment b = new(61, new Vec3(11, 0, 2), new Vec3(20, 0, 2));

        (SlopeRun? run, List<(SlopeSegment Segment, string Reason)> rejected) =
            SlopeSolver.BuildRun([a, b], Params);

        Assert.Null(run);
        Assert.Contains(rejected, r => r.Reason.Contains("two runs at different elevations"));
    }

    // No horizontal room at all to climb through: the segments meet at one
    // station with a step between them.
    [Fact]
    public void BuildRun_StepWithNoGapAtAll_IsRejected()
    {
        SlopeSegment a = new(62, new Vec3(0, 0, 0), new Vec3(10, 0, 0));
        SlopeSegment b = new(63, new Vec3(10, 0, 2), new Vec3(20, 0, 2));

        (SlopeRun? run, _) = SlopeSolver.BuildRun([a, b], Params);

        Assert.Null(run);
    }

    // ---- and the cases that must STILL build, because this rejects picks that
    // ---- used to succeed.

    // An ordinary run: consecutive pipes either side of a tee gap, meeting at
    // essentially the same elevation.
    [Fact]
    public void BuildRun_OrdinaryRunAcrossATeeGap_StillBuilds()
    {
        SlopeSegment a = new(64, new Vec3(0, 0, 0), new Vec3(10, 0, 0));
        SlopeSegment b = new(65, new Vec3(10.25, 0, 0), new Vec3(20, 0, 0));

        (SlopeRun? run, _) = SlopeSolver.BuildRun([a, b], Params);

        Assert.NotNull(run);
        Assert.Equal(2, run!.Segments.Count);
    }

    // An already-pitched run: consecutive pipes DO differ in Z, by the amount
    // their own slope puts there. That must not read as a step.
    [Fact]
    public void BuildRun_AlreadyPitchedRun_StillBuilds()
    {
        // 20 mm/m — ten times the tool's own default — over a 10ft segment.
        SlopeSegment a = new(66, new Vec3(0, 0, 0), new Vec3(10, 0, 0.2));
        SlopeSegment b = new(67, new Vec3(10.25, 0, 0.205), new Vec3(20, 0, 0.4));

        (SlopeRun? run, _) = SlopeSolver.BuildRun([a, b], Params);

        Assert.NotNull(run);
        Assert.Equal(2, run!.Segments.Count);
    }

    // A long stretch missing from the middle of the pick, with a modest rise
    // across it: shallow, so it is a gap in one run, not a step between two.
    [Fact]
    public void BuildRun_LongGapWithAShallowRise_StillBuilds()
    {
        SlopeSegment a = new(68, new Vec3(0, 0, 0), new Vec3(10, 0, 0));
        SlopeSegment b = new(69, new Vec3(60, 0, 1), new Vec3(70, 0, 1));

        (SlopeRun? run, _) = SlopeSolver.BuildRun([a, b], Params);

        Assert.NotNull(run);
        Assert.Equal(2, run!.Segments.Count);
    }
}
