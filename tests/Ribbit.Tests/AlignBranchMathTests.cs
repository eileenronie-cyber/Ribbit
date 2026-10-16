using Ribbit.Geometry;
using Xunit;

namespace Ribbit.Tests;

/// <summary>
/// Ported 1:1 from lib/mepalignlib/tests/test_geometry.py. Every expected
/// value here was hand-computed in the original (worked out on paper, not by
/// running the code and eyeballing the result) — so these are the acceptance
/// criteria proving the C# port does the same geometry, not fresh guesses.
/// </summary>
public class AlignBranchMathTests
{
    private const double Tol = 1e-9;

    private static void AssertVecEqual(Vec3 expected, Vec3 actual, double tol = Tol)
    {
        Assert.Equal(expected.X, actual.X, tol);
        Assert.Equal(expected.Y, actual.Y, tol);
        Assert.Equal(expected.Z, actual.Z, tol);
    }

    // ------------------------------------------- degenerate (zero-length) runs
    // A zero-length direction has no line to be closest along. Each case must
    // still give finite parameters that keep the result on the lines it has.

    [Fact]
    public void LineParamsClosest_BothDirectionsZero_StaysPut()
    {
        (double s, double t) = AlignBranchMath.LineParamsClosest(
            new Vec3(1, 2, 3), new Vec3(0, 0, 0), new Vec3(7, 8, 9), new Vec3(0, 0, 0));

        Assert.Equal(0.0, s, Tol);
        Assert.Equal(0.0, t, Tol);
    }

    [Fact]
    public void LineParamsClosest_ZeroLengthBranch_ProjectsItsPointOntoTheReference()
    {
        // Branch is the point (5,3,0); the reference runs along X from the
        // origin with |dm| = 2, so the closest reference point (5,0,0) is t = 2.5.
        (double s, double t) = AlignBranchMath.LineParamsClosest(
            new Vec3(5, 3, 0), new Vec3(0, 0, 0), new Vec3(0, 0, 0), new Vec3(2, 0, 0));

        Assert.Equal(0.0, s, Tol);
        Assert.Equal(2.5, t, Tol);
    }

    [Fact]
    public void LineParamsClosest_ZeroLengthReference_FindsTheBranchPointNearestIt()
    {
        // Reference is the point (0,10,0); the branch runs along Y from (0,4,0)
        // with |db| = 2, so it reaches that point at s = 3.
        (double s, double t) = AlignBranchMath.LineParamsClosest(
            new Vec3(0, 4, 0), new Vec3(0, 2, 0), new Vec3(0, 10, 0), new Vec3(0, 0, 0));

        Assert.Equal(3.0, s, Tol);
        Assert.Equal(0.0, t, Tol);
    }

    // Branch along X through origin, reference along Y through (5,0,3).
    // Directions are perpendicular, so the only gap between the lines is the
    // 3-unit Z offset; closest points must be (5,0,0) on the branch and
    // (5,0,3) on the reference (s=5, t=0).
    [Fact]
    public void SkewPerpendicularLines_ClosestPointsAndPureZGap()
    {
        Vec3 b0 = new(0, 0, 0);
        Vec3 db = new(1, 0, 0);
        Vec3 m0 = new(5, 0, 3);
        Vec3 dm = new(0, 1, 0);

        (double s, double t) = AlignBranchMath.LineParamsClosest(b0, db, m0, dm);

        Assert.Equal(5.0, s, Tol);
        Assert.Equal(0.0, t, Tol);

        Vec3 ptBranch = b0.Add(db.Multiply(s));
        Vec3 ptRef = m0.Add(dm.Multiply(t));

        AssertVecEqual(new Vec3(5, 0, 0), ptBranch);
        AssertVecEqual(new Vec3(5, 0, 3), ptRef);
        Assert.Equal(3.0, ptRef.Subtract(ptBranch).GetLength(), Tol);
    }

    // Two lines that already intersect at the origin — both parameters must
    // land on the intersection (0,0,0), zero delta.
    [Fact]
    public void IntersectingLines_BothParamsZero()
    {
        (double s, double t) = AlignBranchMath.LineParamsClosest(
            new Vec3(0, 0, 0), new Vec3(1, 0, 0),
            new Vec3(0, 0, 0), new Vec3(0, 1, 0));

        Assert.Equal(0.0, s, Tol);
        Assert.Equal(0.0, t, Tol);
    }

    // Parallel lines (same direction, offset by (3,4,0)). a and c are both
    // nonzero but denom = a*c - b*b = 0 (b=1 since db==dm unit vectors dotted
    // = 1) -> falls back to s=0, t=e/c=-3. This removes only the Y offset
    // (perpendicular), leaving the branch's own position along X untouched —
    // matching the module's documented parallel-line behaviour.
    [Fact]
    public void ParallelLines_RemoveOnlyPerpendicularOffset()
    {
        (double s, double t) = AlignBranchMath.LineParamsClosest(
            new Vec3(0, 0, 0), new Vec3(1, 0, 0),
            new Vec3(3, 4, 0), new Vec3(1, 0, 0));

        Assert.Equal(0.0, s, Tol);
        Assert.Equal(-3.0, t, Tol);

        Vec3 delta = new Vec3(3, 4, 0).Add(new Vec3(1, 0, 0).Multiply(t))
            .Subtract(new Vec3(0, 0, 0).Add(new Vec3(1, 0, 0).Multiply(s)));

        AssertVecEqual(new Vec3(0, 4, 0), delta);
    }

    // Both directions zero-length -> defined fallback (0,0).
    [Fact]
    public void BothDirectionsDegenerate_ReturnsZeroZero()
    {
        (double s, double t) = AlignBranchMath.LineParamsClosest(
            new Vec3(1, 2, 3), new Vec3(0, 0, 0),
            new Vec3(4, 5, 6), new Vec3(0, 0, 0));

        Assert.Equal(0.0, s, Tol);
        Assert.Equal(0.0, t, Tol);
    }

    [Fact]
    public void ComputeDelta_MatchesHandWorkedSkewCase()
    {
        Vec3 delta = AlignBranchMath.ComputeDelta(
            new Vec3(0, 0, 0), new Vec3(10, 0, 0),
            new Vec3(5, 0, 3), new Vec3(5, 10, 3));

        AssertVecEqual(new Vec3(0, 0, 3), delta);
    }

    // ------------------------------------------------- near-parallel runs
    //
    // Lines that are ALMOST parallel still intersect — thousands of feet away —
    // so the closest-point answer used to be "already aligned" for two runs
    // sitting feet apart. Measured before the fix: 100ft runs, 5ft apart, 0.5
    // degrees off gave |delta| = 0.

    /// <summary>A reference run rotated <paramref name="degrees"/> off the branch.</summary>
    private static Vec3 RefEnd(Vec3 start, double length, double degrees)
    {
        double rad = degrees * Math.PI / 180.0;
        return new Vec3(
            start.X + (length * Math.Cos(rad)),
            start.Y + (length * Math.Sin(rad)),
            start.Z);
    }

    /// <summary>Perpendicular distance from a point to an infinite line.</summary>
    private static double DistanceToLine(Vec3 point, Vec3 lineStart, Vec3 lineDir)
    {
        Vec3 w = point.Subtract(lineStart);
        double t = w.DotProduct(lineDir) / lineDir.DotProduct(lineDir);
        return w.Subtract(lineDir.Multiply(t)).GetLength();
    }

    // Asserted as a PROPERTY, not as an exact vector: once the runs are not
    // exactly parallel the perpendicular foot shifts slightly ALONG the
    // reference, so the delta is not purely lateral. What has to be true is
    // that the branch ends up on the reference's centreline, having moved about
    // as far as it was offset — not that it moved in one particular direction.
    [Theory]
    [InlineData(0.0)]
    [InlineData(0.1)]
    [InlineData(0.5)]
    [InlineData(1.5)]
    public void NearParallel_PutsTheBranchOnTheReferenceLine(double degrees)
    {
        Vec3 branchStart = new(0, 0, 0);
        Vec3 refStart = new(0, 5, 0);
        Vec3 refEnd = RefEnd(refStart, 100, degrees);

        Vec3 delta = AlignBranchMath.ComputeDelta(
            branchStart, new Vec3(100, 0, 0), refStart, refEnd);

        Vec3 moved = branchStart.Add(delta);

        Assert.Equal(0.0, DistanceToLine(moved, refStart, refEnd.Subtract(refStart)), 1e-9);
        Assert.Equal(5.0, delta.GetLength(), 0.05);
    }

    // A deliberate shallow branch is NOT parallel and must keep the
    // closest-point behaviour: coplanar runs at a real angle genuinely meet, so
    // there is nothing to move.
    [Theory]
    [InlineData(11.25)]
    [InlineData(45.0)]
    [InlineData(90.0)]
    public void DeliberateAngles_KeepClosestPointBehaviour(double degrees)
    {
        Vec3 refStart = new(0, 5, 0);

        Vec3 delta = AlignBranchMath.ComputeDelta(
            new Vec3(0, 0, 0), new Vec3(100, 0, 0),
            refStart, RefEnd(refStart, 100, degrees));

        Assert.True(
            delta.GetLength() < 1e-6,
            $"{degrees} deg should still resolve to the crossing point, got {delta.GetLength()}");
    }

    // THE SCALE TRAP. The old test compared denom (units of length^4) against a
    // constant, so the same angle classified differently for a 6-inch fitting
    // and a 100-foot main. The same angle must give the same answer at any size.
    [Theory]
    [InlineData(0.5)]
    [InlineData(500.0)]
    public void ParallelDetection_DoesNotDependOnRunLength(double length)
    {
        Vec3 refStart = new(0, length / 20.0, 0);

        Vec3 delta = AlignBranchMath.ComputeDelta(
            new Vec3(0, 0, 0), new Vec3(length, 0, 0),
            refStart, RefEnd(refStart, length, 0.5));

        // Offset is length/20 in Y whatever the scale.
        AssertVecEqual(new Vec3(0, length / 20.0, 0), delta, length * 0.01);
    }

    // Skew near-parallel: the vertical AND the lateral offset must both go. The
    // old behaviour moved only the vertical, which looked plausible in a
    // section and left the branch laterally off in plan.
    [Fact]
    public void NearParallelSkew_RemovesLateralOffsetToo()
    {
        Vec3 refStart = new(0, 5, -3);
        Vec3 refEnd = RefEnd(refStart, 100, 0.5);

        Vec3 delta = AlignBranchMath.ComputeDelta(
            new Vec3(0, 0, 0), new Vec3(100, 0, 0), refStart, refEnd);

        AssertVecEqual(new Vec3(0, 5, -3), delta, 0.05);
    }
}
