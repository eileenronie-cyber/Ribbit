using Ribbit.Geometry;
using Xunit;

namespace Ribbit.Tests;

/// <summary>
/// Ported 1:1 from lib/spriglib/tests/test_solver.py.
///
/// That suite exists to pin one decision in particular: "SPRIGS ARE BUILT
/// PLUMB... the deliberate opposite of the earlier 'true-90 tee, leaning sprig'
/// choice". It had already been flipped once, so these are the tests that stop
/// a future change silently un-flipping it.
/// </summary>
public class SprigSolverTests
{
    private const double Tol = 1e-9;

    /// <summary>
    /// MaxDirectLeanDeg is 0 here — the cap OFF — so every pre-existing test
    /// keeps exercising the layout choice it was written for. The cap has its
    /// own tests below.
    /// </summary>
    private static readonly SprigParams Params = new(
        DirectTol: 0.05,
        MinSprig: 0.01,
        MainMargin: 0.1,
        MaxArmover: 10.0,
        MinTeeSpacing: 0.05,
        LeanWarnDeg: 5.0,
        MaxDirectLeanDeg: 0.0);

    /// <summary>A pitched main: rises 2ft over a 10ft plan run.</summary>
    private static MainSegment PitchedMain()
        => new(1, new Vec3(0, 0, 0), new Vec3(10, 0, 2));

    // ------------------------------------------------------- Project

    [Fact]
    public void Project_HandWorkedStationTeeAndOffset_OnAPitchedMain()
    {
        MainSegment seg = PitchedMain();

        MainSegment.Projection? proj = seg.Project(new Vec3(4, 3, 99));

        Assert.NotNull(proj);
        Assert.Equal(4.0, proj!.Value.TeePt.X, Tol);
        Assert.Equal(0.0, proj.Value.TeePt.Y, Tol);
        Assert.Equal(0.8, proj.Value.TeePt.Z, Tol);
        Assert.Equal(3.0, proj.Value.PlanDist, Tol);
        Assert.True(proj.Value.Inside);
    }

    [Fact]
    public void Project_HeadPastTheSegmentEnd_ClampsAndFlagsNotInside()
    {
        MainSegment seg = PitchedMain();

        MainSegment.Projection? proj = seg.Project(new Vec3(15, 0, 99));

        Assert.NotNull(proj);
        Assert.False(proj!.Value.Inside);
        Assert.Equal(10.0, proj.Value.TeePt.X, Tol);
    }

    [Fact]
    public void Project_VerticalSegment_ReturnsNull()
    {
        MainSegment riser = new(2, new Vec3(5, 5, 0), new Vec3(5, 5, 10));

        Assert.Null(riser.Project(new Vec3(6, 6, 5)));
    }

    // ------------------------------------------- armover takeoff angle
    //
    // This replaced a test of MainSegment.Foot(), a helper nothing in the
    // add-in ever called. It asserted that Foot's own arithmetic was
    // perpendicular — true, and protecting no shipped behaviour. The property
    // worth pinning is the one the BUILDER depends on: the armover leaves the
    // main square to its 3D axis, which is the takeoff direction
    // NewTeeFitting accepts. Nothing tested that until now.

    [Fact]
    public void ArmoverTakeoff_IsSquareToThe3DAxis_EvenOnAPitchedMain()
    {
        MainSegment seg = PitchedMain();

        HeadPlan plan = SprigSolver.SolveOne(1, new Vec3(4, 3, 10), [seg], Params);

        Assert.Equal(SprigLayout.Armover, plan.Kind);

        Vec3 arm = plan.ElbowPt!.Value.Subtract(plan.TeePt!.Value);
        Assert.Equal(0.0, arm.DotProduct(seg.Direction), 1e-9);

        // Horizontal too — that is what makes it square despite the pitch.
        Assert.Equal(0.0, arm.Z, Tol);
    }

    // ----------------------------------------------- EffectiveMargin

    [Fact]
    public void EffectiveMargin_LongPipe_UsesTheConfiguredMargin()
    {
        double configured = 30.0 / Units.MmPerFt;

        Assert.Equal(configured, SprigSolver.EffectiveMargin(1.0, configured)!.Value, Tol);
    }

    // ~15mm - short enough to need shrinking, not rejection.
    [Fact]
    public void EffectiveMargin_ShortPipe_ShrinksRatherThanRejectingEveryHead()
    {
        double configured = 30.0 / Units.MmPerFt;

        double? shrunk = SprigSolver.EffectiveMargin(0.05, configured);

        Assert.NotNull(shrunk);
        Assert.True(shrunk < configured, $"expected shrunk margin, got {shrunk}");
    }

    // ~6mm - genuinely too short for any fitting.
    [Fact]
    public void EffectiveMargin_GenuinelyTooShortPipe_ReturnsNull()
        => Assert.Null(SprigSolver.EffectiveMargin(0.02, 30.0 / Units.MmPerFt));

    // ------------------------------------------------------ SolveOne
    // The actual design guarantee.

    // DIRECT, head on the branch centreline, PITCHED main: the sprig must be
    // exactly vertical even though the main is sloped. This is the module's
    // central, previously-reversed decision.
    [Fact]
    public void SolveOne_DirectSprigOnAPitchedMain_IsDeadPlumb()
    {
        MainSegment seg = PitchedMain();

        HeadPlan plan = SprigSolver.SolveOne(101, new Vec3(4, 0, 10), [seg], Params);

        Assert.Equal(SprigLayout.Direct, plan.Kind);
        Assert.Equal(0.0, plan.LeanDeg, Tol);
    }

    [Fact]
    public void SolveOne_DirectSprigLength_IsHeadZMinusThePitchedTeeZ()
    {
        MainSegment seg = PitchedMain();

        HeadPlan plan = SprigSolver.SolveOne(101, new Vec3(4, 0, 10), [seg], Params);

        Assert.Equal(10.0 - 0.8, plan.SprigLength, Tol);
    }

    [Fact]
    public void SolveOne_OffCentrelineHead_GetsArmover()
    {
        MainSegment seg = PitchedMain();

        HeadPlan plan = SprigSolver.SolveOne(102, new Vec3(4, 3, 10), [seg], Params);

        Assert.Equal(SprigLayout.Armover, plan.Kind);
    }

    // The armover vector (tee -> elbow) must be perpendicular to the main's FULL
    // 3D axis, not just its plan direction - "perpendicular to the branch for
    // free" per the docstring, at any pitch.
    [Fact]
    public void SolveOne_ArmoverTakeoff_IsPerpendicularToThe3DAxisNotJustThePlan()
    {
        MainSegment seg = PitchedMain();

        HeadPlan plan = SprigSolver.SolveOne(102, new Vec3(4, 3, 10), [seg], Params);
        Vec3 armoverVec = plan.ElbowPt!.Value.Subtract(plan.TeePt!.Value);

        Assert.Equal(0.0, armoverVec.DotProduct(seg.Direction), 1e-9);
    }

    [Fact]
    public void SolveOne_ArmoverSprigFromElbowUpToHead_IsAlsoPlumb()
    {
        MainSegment seg = PitchedMain();

        HeadPlan plan = SprigSolver.SolveOne(102, new Vec3(4, 3, 10), [seg], Params);

        Assert.Equal(plan.HeadPt.X, plan.ElbowPt!.Value.X, Tol);
        Assert.Equal(plan.HeadPt.Y, plan.ElbowPt.Value.Y, Tol);
    }

    // A head at or below the main must be skipped, not built with a
    // negative/zero-length sprig.
    [Fact]
    public void SolveOne_HeadAtOrBelowTheMain_IsSkipped()
    {
        MainSegment seg = PitchedMain();

        HeadPlan plan = SprigSolver.SolveOne(103, new Vec3(4, 0, 0.5), [seg], Params);

        Assert.Equal(SprigStatus.Skip, plan.Status);
    }

    // Neither instance can be identified as "the real one", so both go.
    [Fact]
    public void CheckDuplicateHeads_BothNearCoincidentHeads_AreSkipped()
    {
        MainSegment seg = PitchedMain();
        List<HeadPlan> plans =
        [
            SprigSolver.SolveOne(201, new Vec3(4, 0, 10), [seg], Params),
            SprigSolver.SolveOne(202, new Vec3(4, 0.01, 10), [seg], Params),
        ];

        SprigSolver.CheckDuplicateHeads(plans);

        Assert.Equal(SprigStatus.Skip, plans[0].Status);
        Assert.Equal(SprigStatus.Skip, plans[1].Status);
    }

    // ------------------------------------------------- DIRECT lean cap
    //
    // A DIRECT sprig is not plumb: its tee is the perpendicular foot, so the
    // pipe leans across whatever plan offset is left. Judging the layout on plan
    // distance alone meant a head 300mm off a 300mm rise was built at 45 degrees
    // while one 350mm off got a clean armover — nearer geometry, worse result.

    /// <summary>A dead LEVEL main, so nothing here can be blamed on pitch.</summary>
    private static MainSegment LevelMain() => new(1, new Vec3(0, 0, 0), new Vec3(10, 0, 0));

    /// <summary>Plan offset deliberately not the limiting factor — the lean is.</summary>
    private static SprigParams WithLeanCap(double maxLeanDeg)
        => Params with { DirectTol = 1.0, MaxDirectLeanDeg = maxLeanDeg };

    [Theory]
    [InlineData(0.3, 0.3, 45.0)]
    [InlineData(0.3, 1.0, 16.70)]
    [InlineData(0.0, 1.0, 0.0)]
    public void DirectLeanDeg_IsTheAngleTheSprigWouldActuallyCome(
        double planDist, double rise, double expected)
    {
        Assert.Equal(expected, SprigSolver.DirectLeanDeg(planDist, rise), 2);
    }

    // Nothing to climb: the "sprig" would be horizontal.
    [Fact]
    public void DirectLeanDeg_NoRise_IsFullyOver()
    {
        Assert.Equal(90.0, SprigSolver.DirectLeanDeg(0.5, 0.0), 6);
    }

    [Theory]
    [InlineData(0.0, SprigLayout.Direct)]      // cap off: leans ~18 deg, built anyway
    [InlineData(15.0, SprigLayout.Armover)]    // cap on: sent to an armover instead
    public void LeanCap_DecidesTheLayoutWhenThePlanOffsetWouldNot(
        double cap, SprigLayout expected)
    {
        HeadPlan plan = SprigSolver.SolveOne(
            1, new Vec3(5, 0.33, 1), [LevelMain()], WithLeanCap(cap));

        Assert.Equal(expected, plan.Kind);
    }

    // An armover is plumb by construction, so clearing the cap is not merely a
    // relabelling — the geometry really does straighten up.
    [Fact]
    public void LeanCap_TheArmoverItRoutesTo_IsActuallyPlumb()
    {
        HeadPlan plan = SprigSolver.SolveOne(
            1, new Vec3(5, 0.33, 1), [LevelMain()], WithLeanCap(15.0));

        Assert.Equal(SprigLayout.Armover, plan.Kind);
        Assert.Equal(0.0, plan.LeanDeg, Tol);
        Assert.Equal(0.33, plan.ArmoverLength, 1e-6);
    }

    [Fact]
    public void LeanCap_LeavesAnAlreadyStraightSprigAlone()
    {
        HeadPlan plan = SprigSolver.SolveOne(
            1, new Vec3(5, 0.0, 1), [LevelMain()], WithLeanCap(15.0));

        Assert.Equal(SprigLayout.Direct, plan.Kind);
        Assert.Equal(0.0, plan.LeanDeg, Tol);
    }

    // The message used to blame the main's pitch. This main is dead level.
    [Fact]
    public void LeanWarning_NamesTheHeadOffset_NotThePitch()
    {
        HeadPlan plan = SprigSolver.SolveOne(
            1, new Vec3(5, 0.2, 1), [LevelMain()], WithLeanCap(0.0));

        string text = plan.IssueText();

        Assert.Contains("off the branch centreline", text);
        Assert.DoesNotContain("pitched", text);
    }
}
