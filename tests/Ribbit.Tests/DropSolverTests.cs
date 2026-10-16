using Ribbit.Geometry;
using Xunit;

namespace Ribbit.Tests;

/// <summary>
/// Ported 1:1 from lib/droplib/tests/test_solver.py.
///
/// The central guarantee these pin: BOTH vertical pipes — the riser off the
/// branch and the drop to the head — come out dead plumb regardless of the
/// branch's own pitch. That is the deliberate opposite of the earlier "true-90
/// tee, leaning riser" geometry, so it is exactly the kind of decision a
/// regression test protects.
/// </summary>
public class DropSolverTests
{
    private const double Tol = 1e-9;

    private static readonly DropParams Params = new(
        DirectTol: 0.05,
        MinDrop: 0.01,
        MainMargin: 0.1,
        MaxArmover: 10.0,
        MinTeeSpacing: 0.05,
        LeanWarnDeg: 5.0,
        RiserHeight: 1.0,
        HangerWarn: 2.0,
        Clearance: 0.05,
        AllowDirect: true,

        // Cap OFF, so every pre-existing test keeps exercising the layout choice
        // it was written for. The cap has its own tests.
        MaxDirectLeanDeg: 0.0);

    /// <summary>
    /// The same pitched branch spriglib's tests use, for a consistent
    /// hand-worked reference: rises 2ft over a 10ft plan run, p1 end open.
    /// </summary>
    private static DropSegment PitchedBranch()
        => new(1, new Vec3(0, 0, 0), new Vec3(10, 0, 2), openP0: false, openP1: true);

    // ------------------------------------------------------------- DIRECT

    [Fact]
    public void Direct_HeadOnCentreline_GetsDirect()
    {
        DropHeadPlan plan = DropSolver.SolveOne(101, new Vec3(4, 0, -5), 0.05, [PitchedBranch()], Params);

        Assert.Equal(DropLayout.Direct, plan.Kind);
    }

    // Same cap as Sprig, for the same reason: a DIRECT drop tees at the
    // perpendicular foot and leans across the leftover plan offset, so judging
    // the layout on plan distance alone lets a badly angled drop through.
    // A RETURN BEND is plumb by construction.
    [Theory]
    [InlineData(0.0, DropLayout.Direct)]        // cap off: leans ~18 deg, built anyway
    [InlineData(15.0, DropLayout.ReturnBend)]   // cap on: routed to a return bend
    public void LeanCap_DecidesTheLayoutWhenThePlanOffsetWouldNot(
        double cap, DropLayout expected)
    {
        DropParams pars = Params with { DirectTol = 1.0, MaxDirectLeanDeg = cap };

        // 0.33ft off the centreline with a 1ft drop is about 18 degrees.
        DropHeadPlan plan = DropSolver.SolveOne(
            101, new Vec3(4, 0.33, -0.2), 0.05, [PitchedBranch()], pars);

        Assert.Equal(expected, plan.Kind);
    }

    [Fact]
    public void Direct_DropIsDeadPlumb_EvenOnAPitchedBranch()
    {
        DropHeadPlan plan = DropSolver.SolveOne(101, new Vec3(4, 0, -5), 0.05, [PitchedBranch()], Params);

        Assert.Equal(0.0, plan.LeanDeg, Tol);
    }

    [Fact]
    public void Direct_DropLength_IsThePitchedTeeZMinusTheHeadZ()
    {
        DropHeadPlan plan = DropSolver.SolveOne(101, new Vec3(4, 0, -5), 0.05, [PitchedBranch()], Params);

        Assert.Equal(0.8 - (-5.0), plan.DropLength, Tol);
    }

    // -------------------------------------------------------- RETURN BEND

    [Fact]
    public void ReturnBend_OffCentrelineHead_GetsAReturnBend()
    {
        DropHeadPlan plan = DropSolver.SolveOne(102, new Vec3(4, 3, -5), 0.05, [PitchedBranch()], Params);

        Assert.Equal(DropLayout.ReturnBend, plan.Kind);
    }

    [Fact]
    public void ReturnBend_RiserIsDeadPlumb()
    {
        DropHeadPlan plan = DropSolver.SolveOne(102, new Vec3(4, 3, -5), 0.05, [PitchedBranch()], Params);
        Vec3 riserVec = plan.RiserTop!.Value.Subtract(plan.TeePt!.Value);

        Assert.Equal(0.0, riserVec.X, Tol);
        Assert.Equal(0.0, riserVec.Y, Tol);
        Assert.True(riserVec.Z > 0.0, "riser must rise");
    }

    // BOTH verticals plumb, not just one.
    [Fact]
    public void ReturnBend_DropIsAlsoDeadPlumb()
    {
        DropHeadPlan plan = DropSolver.SolveOne(102, new Vec3(4, 3, -5), 0.05, [PitchedBranch()], Params);
        Vec3 dropVec = plan.OverPt!.Value.Subtract(plan.HeadPt);

        Assert.Equal(0.0, dropVec.X, Tol);
        Assert.Equal(0.0, dropVec.Y, Tol);
    }

    [Fact]
    public void ReturnBend_ArmoverLength_MatchesTheHeadsPlanOffset()
    {
        DropHeadPlan plan = DropSolver.SolveOne(102, new Vec3(4, 3, -5), 0.05, [PitchedBranch()], Params);

        Assert.Equal(3.0, plan.ArmoverLength, Tol);
    }

    // The tee sits directly under the riser top in plan even though the branch
    // is pitched - that is what makes the riser plumb.
    [Fact]
    public void ReturnBend_TeeSharesPlanPositionWithTheRiserTop()
    {
        DropHeadPlan plan = DropSolver.SolveOne(102, new Vec3(4, 3, -5), 0.05, [PitchedBranch()], Params);

        Assert.Equal(plan.RiserTop!.Value.X, plan.TeePt!.Value.X, Tol);
        Assert.Equal(plan.RiserTop.Value.Y, plan.TeePt.Value.Y, Tol);
    }

    // ------------------------------------------------------------ END BEND

    [Fact]
    public void EndBend_InLineHeadPastTheOpenEnd_GetsAnEndBend()
    {
        DropHeadPlan plan = DropSolver.SolveOne(103, new Vec3(12, 0, 10), 0.05, [PitchedBranch()], Params);

        Assert.Equal(DropLayout.EndBend, plan.Kind);
        Assert.True(plan.EndBend);
    }

    [Fact]
    public void EndBend_RiserOffTheOpenEnd_IsDeadPlumb()
    {
        DropHeadPlan plan = DropSolver.SolveOne(103, new Vec3(12, 0, 10), 0.05, [PitchedBranch()], Params);
        Vec3 riserVec = plan.RiserTop!.Value.Subtract(plan.TeePt!.Value);

        Assert.Equal(0.0, riserVec.X, Tol);
        Assert.Equal(0.0, riserVec.Y, Tol);
        Assert.Equal(Params.RiserHeight, riserVec.Z, Tol);
    }

    [Fact]
    public void EndBend_DropDownToTheHead_IsAlsoDeadPlumb()
    {
        DropHeadPlan plan = DropSolver.SolveOne(103, new Vec3(12, 0, 10), 0.05, [PitchedBranch()], Params);
        Vec3 dropVec = plan.OverPt!.Value.Subtract(plan.HeadPt);

        Assert.Equal(0.0, dropVec.X, Tol);
        Assert.Equal(0.0, dropVec.Y, Tol);
    }

    // An open end takes ONE end bend - the elbow uses it up. Two heads in line
    // past the same end both used to be planned as end bends; the dry run
    // promised both, and the builder then failed on the second and rolled back
    // the WHOLE run, valid heads elsewhere included (seen in the in-Revit suite).
    // The head nearest the end keeps the end bend; the other is skipped, saying why.
    [Fact]
    public void EndBend_TwoHeadsPastTheSameOpenEnd_OnlyTheNearerGetsIt()
    {
        List<DropHeadPlan> plans = DropSolver.Solve(
            [(201, new Vec3(14, 0, -5), 0.05), (202, new Vec3(12, 0, -5), 0.05)],
            [PitchedBranch()], Params);

        DropHeadPlan nearer = plans.Single(p => p.HeadId == 202);
        DropHeadPlan farther = plans.Single(p => p.HeadId == 201);
        Assert.Equal(DropLayout.EndBend, nearer.Kind);
        Assert.NotEqual(SprigStatus.Skip, nearer.Status);
        Assert.Equal(SprigStatus.Skip, farther.Status);
        Assert.Contains("202", farther.IssueText());
    }

    // ... but two different open ends are two end bends.
    [Fact]
    public void EndBend_HeadsPastDifferentOpenEnds_BothGetOne()
    {
        DropSegment bothOpen = new(1, new Vec3(0, 0, 0), new Vec3(10, 0, 0), openP0: true, openP1: true);
        List<DropHeadPlan> plans = DropSolver.Solve(
            [(301, new Vec3(12, 0, -5), 0.05), (302, new Vec3(-2, 0, -5), 0.05)], [bothOpen], Params);

        Assert.All(plans, p => Assert.Equal(DropLayout.EndBend, p.Kind));
        Assert.All(plans, p => Assert.NotEqual(SprigStatus.Skip, p.Status));
    }

    // A PENDENT head hangs BELOW the branch. The ported fixtures above put the
    // end-bend head at z=+10, i.e. above the armover - fine for checking which
    // layout is chosen, but not a drop anyone would build. These use z=-5.
    private static DropHeadPlan EndBendBelow()
        => DropSolver.SolveOne(113, new Vec3(12, 0, -5), 0.05, [PitchedBranch()], Params);

    // A plumb end-bend drop must read 0 deg, the same as Direct and ReturnBend.
    // The Python measured this one drop DOWNWARD against +Z, so identical plumb
    // geometry reported 180 deg here and 0 for the other two layouts - which
    // showed up on a live dry run and read as a fault.
    [Fact]
    public void EndBend_APlumbDrop_ReadsZeroLean_NotOneEighty()
    {
        DropHeadPlan plan = EndBendBelow();

        Assert.Equal(DropLayout.EndBend, plan.Kind);
        Assert.Equal(0.0, plan.LeanDeg, 1e-9);
    }

    // All three layouts must agree on what "plumb" reads as, or the Lean column
    // is not comparable down the report.
    [Fact]
    public void AllThreeLayouts_ReportPlumbTheSameWay()
    {
        DropHeadPlan direct = DropSolver.SolveOne(101, new Vec3(4, 0, -5), 0.05, [PitchedBranch()], Params);
        DropHeadPlan returnBend = DropSolver.SolveOne(102, new Vec3(4, 3, -5), 0.05, [PitchedBranch()], Params);
        DropHeadPlan endBend = EndBendBelow();

        Assert.Equal(DropLayout.Direct, direct.Kind);
        Assert.Equal(DropLayout.ReturnBend, returnBend.Kind);
        Assert.Equal(DropLayout.EndBend, endBend.Kind);

        Assert.Equal(0.0, direct.LeanDeg, 1e-9);
        Assert.Equal(0.0, returnBend.LeanDeg, 1e-9);
        Assert.Equal(0.0, endBend.LeanDeg, 1e-9);
    }

    // The drop length must be SIGNED, or "head is at or above the armover" can
    // never fire: a magnitude is never negative. The Python compared
    // drop_vec.GetLength() to 0, so a head ABOVE the armover passed as a valid
    // end bend and the builder would have run a "drop" pipe UPWARDS to it.
    [Fact]
    public void EndBend_HeadAboveTheArmover_IsSkipped()
    {
        // z=+10 is well above the branch (which tops out at z=2) plus the 1.0
        // riser, so the armover sits far below this head.
        DropHeadPlan plan = DropSolver.SolveOne(114, new Vec3(12, 0, 10), 0.05, [PitchedBranch()], Params);

        Assert.Equal(DropLayout.EndBend, plan.Kind);
        Assert.Equal(SprigStatus.Skip, plan.Status);
        Assert.Contains("at or above the armover", plan.IssueText());
    }

    [Fact]
    public void EndBend_AValidDrop_HasAPositiveLength()
        => Assert.True(EndBendBelow().DropLength > 0.0);

    [Fact]
    public void EndBend_ElbowRoot_IsTheBranchesActualOpenEndPoint()
    {
        DropHeadPlan plan = DropSolver.SolveOne(103, new Vec3(12, 0, 10), 0.05, [PitchedBranch()], Params);

        Assert.Equal(10.0, plan.TeePt!.Value.X, Tol);
        Assert.Equal(0.0, plan.TeePt.Value.Y, Tol);
        Assert.Equal(2.0, plan.TeePt.Value.Z, Tol);
    }

    // The SAME in-line-past-the-end head on a segment whose far end is NOT open
    // must be refused, not silently built anyway.
    [Fact]
    public void EndBend_RefusedWhenTheFarEndHasNoOpenConnector()
    {
        DropSegment closed = new(2, new Vec3(0, 0, 0), new Vec3(10, 0, 2),
            openP0: false, openP1: false);

        DropHeadPlan plan = DropSolver.SolveOne(104, new Vec3(12, 0, 10), 0.05, [closed], Params);

        Assert.Equal(SprigStatus.Skip, plan.Status);
    }

    // A head genuinely off to the side, past the end, is the plain "off the end"
    // rejection - not mistaken for an end bend.
    [Fact]
    public void EndBend_HeadPastTheEndButNotInLine_IsAPlainOffTheEndSkip()
    {
        DropHeadPlan plan = DropSolver.SolveOne(105, new Vec3(12, 5, 10), 0.05, [PitchedBranch()], Params);

        Assert.NotEqual(DropLayout.EndBend, plan.Kind);
        Assert.Equal(SprigStatus.Skip, plan.Status);
    }

    // -------------------------------- PlanLateral / OpenEndToward
    // The two helpers END BEND depends on.

    [Fact]
    public void PlanLateral_PointOnTheInfiniteAxis_ReadsZeroEvenFarPastTheEnd()
        => Assert.Equal(0.0, PitchedBranch().PlanLateral(new Vec3(50, 0, 999))!.Value, Tol);

    [Fact]
    public void PlanLateral_IgnoresZ_PerpendicularPlanDistanceOnly()
        => Assert.Equal(3.0, PitchedBranch().PlanLateral(new Vec3(5, 3, 999))!.Value, Tol);

    [Fact]
    public void OpenEndToward_BeforeTheStart_ReturnsNullWhenP0IsNotOpen()
        => Assert.Null(PitchedBranch().OpenEndToward(new Vec3(-5, 0, 0)));

    [Fact]
    public void OpenEndToward_PastTheOpenP1End_ReturnsP1WithTheForwardDirection()
    {
        (Vec3 EndPt, Vec3 OutDir)? end = PitchedBranch().OpenEndToward(new Vec3(20, 0, 0));

        Assert.NotNull(end);
        Assert.Equal(10.0, end!.Value.EndPt.X, Tol);
        Assert.Equal(1.0, end.Value.OutDir.X, Tol);
    }
}
