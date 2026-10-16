using Ribbit.Geometry;
using Ribbit.Geometry.Clash;
using Xunit;

namespace Ribbit.Tests;

/// <summary>
/// <see cref="Aabb.DistanceTo"/> — the gap measurement behind clearance clashes.
///
/// Worth testing properly: it is the only part of clearance detection that is
/// pure maths, and getting it wrong under-reports (a gap read as larger than it
/// is hides a real clearance violation) or floods the report (a gap read as
/// smaller than it is invents one).
/// </summary>
public class AabbDistanceTests
{
    private static Aabb Box(
        double minX, double minY, double minZ, double maxX, double maxY, double maxZ)
        => new(new Vec3(minX, minY, minZ), new Vec3(maxX, maxY, maxZ));

    private static Aabb Unit(double x, double y, double z)
        => Box(x, y, z, x + 1, y + 1, z + 1);

    [Fact]
    public void OverlappingBoxesAreZeroApart()
    {
        Aabb a = Box(0, 0, 0, 10, 10, 10);
        Aabb b = Box(5, 5, 5, 15, 15, 15);

        Assert.Equal(0.0, a.DistanceTo(b));
        Assert.Equal(0.0, b.DistanceTo(a));
    }

    [Fact]
    public void TouchingFacesAreZeroApart()
    {
        // Overlaps() treats a shared face as touching, so the distance has to
        // agree with it - a gap of 0 and "overlapping" must never disagree.
        Aabb a = Box(0, 0, 0, 1, 1, 1);
        Aabb b = Box(1, 0, 0, 2, 1, 1);

        Assert.True(a.Overlaps(b));
        Assert.Equal(0.0, a.DistanceTo(b));
    }

    [Fact]
    public void ContainedBoxIsZeroApart()
    {
        Aabb outer = Box(0, 0, 0, 10, 10, 10);
        Aabb inner = Box(4, 4, 4, 6, 6, 6);

        Assert.Equal(0.0, outer.DistanceTo(inner));
        Assert.Equal(0.0, inner.DistanceTo(outer));
    }

    [Theory]
    [InlineData(3.0)]
    [InlineData(0.25)]
    [InlineData(100.0)]
    public void SeparatedOnOneAxisReturnsThatGap(double gap)
    {
        Aabb a = Box(0, 0, 0, 1, 1, 1);
        Aabb b = Box(1 + gap, 0, 0, 2 + gap, 1, 1);

        Assert.Equal(gap, a.DistanceTo(b), 9);
        Assert.Equal(gap, b.DistanceTo(a), 9);
    }

    [Fact]
    public void SeparatedOnTwoAxesReturnsTheDiagonal()
    {
        // 3-4-5. The largest single-axis gap would say 4, which would call a box
        // 5 away "within 4" and invent a clearance violation.
        Aabb a = Box(0, 0, 0, 1, 1, 1);
        Aabb b = Box(4, 5, 0, 5, 6, 1);

        Assert.Equal(5.0, a.DistanceTo(b), 9);
    }

    [Fact]
    public void SeparatedOnThreeAxesReturnsTheThreeDimensionalDiagonal()
    {
        Aabb a = Box(0, 0, 0, 1, 1, 1);
        Aabb b = Box(3, 3, 3, 4, 4, 4);

        // Gap of 2 on each axis.
        Assert.Equal(Math.Sqrt(12.0), a.DistanceTo(b), 9);
    }

    [Fact]
    public void DistanceIsSymmetric()
    {
        Aabb a = Unit(0, 0, 0);
        Aabb b = Unit(7.5, -3.25, 2);

        Assert.Equal(a.DistanceTo(b), b.DistanceTo(a), 12);
    }

    [Fact]
    public void DistanceToSelfIsZero()
    {
        Aabb a = Box(-5, -5, -5, 5, 5, 5);

        Assert.Equal(0.0, a.DistanceTo(a));
    }

    [Fact]
    public void ExpandingByTheGapMakesBoxesTouch()
    {
        // The property clearance detection relies on: a pad equal to the gap is
        // exactly enough to bring two boxes into contact, so "within the pad" and
        // "gap <= pad" describe the same set of pairs.
        Aabb a = Box(0, 0, 0, 1, 1, 1);
        Aabb b = Box(4, 0, 0, 5, 1, 1);

        double gap = a.DistanceTo(b);
        Assert.Equal(3.0, gap, 9);

        Assert.False(a.Overlaps(b));
        Assert.True(a.Expand(gap).Overlaps(b));
        Assert.False(a.Expand(gap - 0.001).Overlaps(b));
    }

    [Fact]
    public void NeverNegative()
    {
        Aabb a = Box(0, 0, 0, 10, 2, 2);
        Aabb b = Box(-30, -30, -30, -20, -20, -20);

        Assert.True(a.DistanceTo(b) > 0.0);
        Assert.True(b.DistanceTo(a) > 0.0);
    }
}
