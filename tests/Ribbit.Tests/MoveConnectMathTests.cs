using Ribbit.Geometry;
using Xunit;

namespace Ribbit.Tests;

/// <summary>
/// Ported 1:1 from lib/mepalignlib/tests/test_geometry.py — see the note on
/// <see cref="AlignBranchMathTests"/> about these being hand-computed.
/// </summary>
public class MoveConnectMathTests
{
    private const double Tol = 1e-9;

    /// <summary>
    /// Stands in for a Revit Connector. Origin is all most tests need; Domain
    /// stands in for Revit's Domain enum for the compatibility tests, where the
    /// only thing that matters is that two values can differ.
    /// </summary>
    private sealed class FakeConn
    {
        public Vec3 Origin { get; }
        public string Domain { get; }

        public FakeConn(double x, double y, double z, string domain = "pipe")
        {
            Origin = new Vec3(x, y, z);
            Domain = domain;
        }
    }

    private static Vec3 OriginOf(FakeConn c) => c.Origin;

    private static bool SameDomain(FakeConn mover, FakeConn target)
        => mover.Domain == target.Domain;

    private static void AssertVecEqual(Vec3 expected, Vec3 actual, double tol = Tol)
    {
        Assert.Equal(expected.X, actual.X, tol);
        Assert.Equal(expected.Y, actual.Y, tol);
        Assert.Equal(expected.Z, actual.Z, tol);
    }

    // ---------------------------------------------------------- RotationAxisAngle

    [Fact]
    public void RotationAxisAngle_AlreadyAligned_NoRotation()
    {
        (Vec3? axis, double angle) = MoveConnectMath.RotationAxisAngle(
            new Vec3(1, 0, 0), new Vec3(1, 0, 0));

        Assert.Null(axis);
        Assert.Equal(0.0, angle, Tol);
    }

    [Fact]
    public void RotationAxisAngle_AntiParallel_AngleIsPiAndAxisPerpendicular()
    {
        (Vec3? axis, double angle) = MoveConnectMath.RotationAxisAngle(
            new Vec3(1, 0, 0), new Vec3(-1, 0, 0));

        Assert.Equal(Math.PI, angle, Tol);
        Assert.NotNull(axis);
        Assert.Equal(0.0, axis!.Value.DotProduct(new Vec3(1, 0, 0)), 1e-6);
    }

    [Fact]
    public void RotationAxisAngle_NinetyDegrees_AxisIsPlusZ()
    {
        (Vec3? axis, double angle) = MoveConnectMath.RotationAxisAngle(
            new Vec3(1, 0, 0), new Vec3(0, 1, 0));

        Assert.Equal(Math.PI / 2.0, angle, Tol);
        Assert.NotNull(axis);
        AssertVecEqual(new Vec3(0, 0, 1), axis!.Value);
    }

    // ---------------------------------------------------------- SignedRollAngle
    // Sign must flip with rotation direction.

    [Fact]
    public void SignedRollAngle_XToYAboutPlusZ_IsPlus90()
    {
        double ang = MoveConnectMath.SignedRollAngle(
            new Vec3(1, 0, 0), new Vec3(0, 1, 0), new Vec3(0, 0, 1));

        Assert.Equal(Math.PI / 2.0, ang, Tol);
    }

    [Fact]
    public void SignedRollAngle_YToXAboutPlusZ_IsMinus90()
    {
        double ang = MoveConnectMath.SignedRollAngle(
            new Vec3(0, 1, 0), new Vec3(1, 0, 0), new Vec3(0, 0, 1));

        Assert.Equal(-Math.PI / 2.0, ang, Tol);
    }

    // ---------------------------------------------------------- ArbitraryPerpendicular

    public static TheoryData<string, Vec3> PerpendicularCases => new()
    {
        { "+X", new Vec3(1, 0, 0) },
        { "+Y", new Vec3(0, 1, 0) },
        { "+Z", new Vec3(0, 0, 1) },
        { "diagonal", new Vec3(1, 1, 1).Normalize() },
    };

    [Theory]
    [MemberData(nameof(PerpendicularCases))]
    public void ArbitraryPerpendicular_IsPerpendicularAndUnitLength(string label, Vec3 v)
    {
        Vec3 perp = MoveConnectMath.ArbitraryPerpendicular(v);

        Assert.Equal(0.0, perp.DotProduct(v), 1e-9);
        Assert.Equal(1.0, perp.GetLength(), 1e-9);
        Assert.False(string.IsNullOrEmpty(label));
    }

    // ---------------------------------------------------------- PickNearestPair / ChoosePair

    [Fact]
    public void PickNearestPair_PicksGenuinelyClosestPair_NotTheFirst()
    {
        FakeConn[] moveConns = [new(0, 0, 0), new(100, 100, 100)];
        FakeConn[] targetConns = [new(1, 0, 0), new(200, 200, 200)];

        var pair = MoveConnectMath.PickNearestPair(moveConns, targetConns, OriginOf);

        Assert.NotNull(pair);
        Assert.Same(moveConns[0], pair!.Value.Mover);
        Assert.Same(targetConns[0], pair.Value.Target);
    }

    // With a click point: the target choice follows the click, and the mover's
    // own click point is ignored (the module's documented behaviour — a
    // body-click on a small fitting shouldn't decide the end).
    [Fact]
    public void ChoosePair_TargetFollowsClick_MoverFollowsChosenTarget()
    {
        FakeConn[] targetOpen = [new(0, 0, 0), new(0, 0, 5)];
        FakeConn[] moverOpen = [new(100, 0, 0), new(100, 0, 5)];
        Vec3 click = new(0, 0, 5.1);   // nearer the SECOND target connector

        var pair = MoveConnectMath.ChoosePair(moverOpen, targetOpen, click, OriginOf);

        Assert.NotNull(pair);
        Assert.Same(targetOpen[1], pair!.Value.Target);
        Assert.Same(moverOpen[1], pair.Value.Mover);
    }

    // No click point falls back to the globally nearest pair. move[1]-target[1]
    // (distance 1) is unambiguously nearer than every other combination (>=48)
    // — no tie, no floating-point ambiguity.
    [Fact]
    public void ChoosePair_NoClickPoint_FallsBackToNearestPairOverall()
    {
        FakeConn[] moverOpen = [new(0, 0, 0), new(50, 0, 0)];
        FakeConn[] targetOpen = [new(-49, 0, 0), new(49, 0, 0)];

        var pair = MoveConnectMath.ChoosePair(moverOpen, targetOpen, null, OriginOf);

        Assert.NotNull(pair);
        Assert.Same(moverOpen[1], pair!.Value.Mover);
        Assert.Same(targetOpen[1], pair.Value.Target);
    }

    // A click on the target cannot conjure an open end: with nothing open on
    // one side there is no pair, and no exception from an empty list.
    [Fact]
    public void ChoosePair_WithAClick_NoOpenMoverEnd_ReturnsNoPair()
    {
        FakeConn[] targetOpen = [new(0, 0, 0)];

        Assert.Null(MoveConnectMath.ChoosePair([], targetOpen, new Vec3(0, 0, 0), OriginOf));
    }

    [Fact]
    public void ChoosePair_WithAClick_NoOpenTargetEnd_ReturnsNoPair()
    {
        FakeConn[] moverOpen = [new(10, 0, 0)];

        Assert.Null(MoveConnectMath.ChoosePair(moverOpen, [], new Vec3(0, 0, 0), OriginOf));
    }

    // -------------------------------------------- domain-compatible pairing
    //
    // Real mechanical equipment carries connectors in several domains inches
    // apart. Choosing the nearest end blind and rejecting the mismatch
    // afterwards made the piping connection unreachable on those, because no
    // click position could get past the electrical connector sitting closer.

    [Fact]
    public void ChoosePair_SkipsANearerTargetEndTheMoverCannotJoin()
    {
        // The electrical end is nearest the click; the mover is pipe-only.
        FakeConn[] targetOpen = [new(0, 0, 5, "electrical"), new(0, 0, 0, "pipe")];
        FakeConn[] moverOpen = [new(100, 0, 0, "pipe")];
        Vec3 click = new(0, 0, 5.1);

        var pair = MoveConnectMath.ChoosePair(moverOpen, targetOpen, click, OriginOf, SameDomain);

        Assert.NotNull(pair);
        Assert.Same(targetOpen[1], pair!.Value.Target);   // the pipe end, not the nearer one
        Assert.Same(moverOpen[0], pair.Value.Mover);
    }

    [Fact]
    public void ChoosePair_MoverSideAlsoSkipsIncompatibleConnectors()
    {
        FakeConn[] targetOpen = [new(0, 0, 0, "pipe")];

        // The electrical connector is nearer the chosen target end.
        FakeConn[] moverOpen = [new(1, 0, 0, "electrical"), new(9, 0, 0, "pipe")];

        var pair = MoveConnectMath.ChoosePair(
            moverOpen, targetOpen, new Vec3(0, 0, 0), OriginOf, SameDomain);

        Assert.NotNull(pair);
        Assert.Same(moverOpen[1], pair!.Value.Mover);
    }

    [Fact]
    public void ChoosePair_NoCompatiblePairAtAll_IsNull()
    {
        FakeConn[] targetOpen = [new(0, 0, 0, "duct")];
        FakeConn[] moverOpen = [new(1, 0, 0, "pipe")];

        Assert.Null(MoveConnectMath.ChoosePair(
            moverOpen, targetOpen, new Vec3(0, 0, 0), OriginOf, SameDomain));

        Assert.Null(MoveConnectMath.PickNearestPair(moverOpen, targetOpen, OriginOf, SameDomain));
    }

    [Fact]
    public void PickNearestPair_WithoutAClick_StillHonoursCompatibility()
    {
        // Nearest overall is electrical-to-duct at distance 1, which is not
        // joinable. The only same-domain combination is the pipe pair, 50 away.
        FakeConn[] moverOpen = [new(0, 0, 0, "electrical"), new(50, 0, 0, "pipe")];
        FakeConn[] targetOpen = [new(1, 0, 0, "duct"), new(100, 0, 0, "pipe")];

        var pair = MoveConnectMath.PickNearestPair(moverOpen, targetOpen, OriginOf, SameDomain);

        Assert.NotNull(pair);
        Assert.Same(moverOpen[1], pair!.Value.Mover);
        Assert.Same(targetOpen[1], pair.Value.Target);
    }

    // The compatibility test is optional, so every existing caller keeps its
    // old behaviour exactly.
    [Fact]
    public void ChoosePair_WithoutACompatibilityTest_IsUnchanged()
    {
        FakeConn[] targetOpen = [new(0, 0, 5, "electrical"), new(0, 0, 0, "pipe")];
        FakeConn[] moverOpen = [new(100, 0, 0, "pipe")];

        var pair = MoveConnectMath.ChoosePair(
            moverOpen, targetOpen, new Vec3(0, 0, 5.1), OriginOf);

        Assert.NotNull(pair);
        Assert.Same(targetOpen[0], pair!.Value.Target);   // nearest, domain ignored
    }
}
