using Ribbit.Geometry;
using Xunit;

namespace Ribbit.Tests;

/// <summary>
/// TeeIntoMain shipped without offline tests, so these are new. The
/// load-bearing property is the one its docstring leans on: the vector between
/// the two closest points is the common perpendicular, so sliding the branch
/// along it preserves the branch's angle — which is what keeps a plumb riser
/// plumb and at its exact elevation.
/// </summary>
public class TeeMathTests
{
    private const double Tol = 1e-9;

    private static void AssertVecEqual(Vec3 expected, Vec3 actual, double tol = Tol)
    {
        Assert.Equal(expected.X, actual.X, tol);
        Assert.Equal(expected.Y, actual.Y, tol);
        Assert.Equal(expected.Z, actual.Z, tol);
    }

    [Fact]
    public void PointToAxisDistance_MeasuresPerpendicularOffset()
    {
        double d = TeeMath.PointToAxisDistance(
            new Vec3(0, 3, 0), new Vec3(0, 0, 0), new Vec3(1, 0, 0));

        Assert.Equal(3.0, d, Tol);
    }

    [Fact]
    public void PointToAxisDistance_PointOnAxis_IsZero()
    {
        double d = TeeMath.PointToAxisDistance(
            new Vec3(7, 0, 0), new Vec3(0, 0, 0), new Vec3(1, 0, 0));

        Assert.Equal(0.0, d, Tol);
    }

    // Hand-worked: a plumb riser at (0,0) and a main running along X at y=3,
    // z=0. Closest points are (0,0,0) on the riser and (0,3,0) on the main, so
    // the common perpendicular is (0,3,0) - purely horizontal.
    [Fact]
    public void AxisClosestPoints_PlumbRiserIntoHorizontalMain()
    {
        (Vec3 onBranch, Vec3 onMain, bool parallel) = TeeMath.AxisClosestPoints(
            new Vec3(0, 0, 0), new Vec3(0, 0, 1),
            new Vec3(5, 3, 0), new Vec3(1, 0, 0));

        Assert.False(parallel);
        AssertVecEqual(new Vec3(0, 0, 0), onBranch);
        AssertVecEqual(new Vec3(0, 3, 0), onMain);
    }

    // The property the whole keep_angle mode rests on: the slide that puts the
    // branch's axis on the main's axis has NO vertical component for a plumb
    // riser, so the riser keeps its exact elevation.
    [Fact]
    public void AxisClosestPoints_ShiftForPlumbRiser_HasNoVerticalComponent()
    {
        (Vec3 onBranch, Vec3 onMain, _) = TeeMath.AxisClosestPoints(
            new Vec3(2, -4, 11), new Vec3(0, 0, 1),      // plumb riser
            new Vec3(5, 3, 6), new Vec3(1, 0, 0));       // main along X

        Vec3 shift = onMain.Subtract(onBranch);

        Assert.Equal(0.0, shift.Z, Tol);
        Assert.True(shift.GetLength() > Tol, "expected a real sideways slide");
    }

    // A sloped main still yields a horizontal-only slide for a plumb branch,
    // because the common perpendicular between a vertical line and any line is
    // perpendicular to the vertical one.
    [Fact]
    public void AxisClosestPoints_SlopedMain_StillSlidesPlumbBranchHorizontally()
    {
        Vec3 slopedDir = new Vec3(1, 0, 0.02).Normalize();   // ~1:50 fall

        (Vec3 onBranch, Vec3 onMain, bool parallel) = TeeMath.AxisClosestPoints(
            new Vec3(0, 0, 0), new Vec3(0, 0, 1),
            new Vec3(4, 2, 9), slopedDir);

        Assert.False(parallel);
        Assert.Equal(0.0, onMain.Subtract(onBranch).Z, Tol);
    }

    [Fact]
    public void AxisClosestPoints_ParallelRuns_AreFlagged()
    {
        (_, _, bool parallel) = TeeMath.AxisClosestPoints(
            new Vec3(0, 0, 0), new Vec3(1, 0, 0),
            new Vec3(0, 5, 0), new Vec3(1, 0, 0));

        Assert.True(parallel);
    }

    [Fact]
    public void AxisClosestPoints_AntiParallelRuns_AreAlsoFlagged()
    {
        (_, _, bool parallel) = TeeMath.AxisClosestPoints(
            new Vec3(0, 0, 0), new Vec3(1, 0, 0),
            new Vec3(0, 5, 0), new Vec3(-1, 0, 0));

        Assert.True(parallel);
    }

    // Two intersecting axes have a zero-length common perpendicular - the
    // branch is already on the main's axis and needs no slide at all.
    [Fact]
    public void AxisClosestPoints_IntersectingAxes_GiveZeroShift()
    {
        (Vec3 onBranch, Vec3 onMain, bool parallel) = TeeMath.AxisClosestPoints(
            new Vec3(0, 0, 0), new Vec3(0, 0, 1),
            new Vec3(0, 0, 4), new Vec3(1, 0, 0));

        Assert.False(parallel);
        Assert.Equal(0.0, onMain.Subtract(onBranch).GetLength(), Tol);
    }
}
