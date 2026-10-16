using Ribbit.Geometry;
using Xunit;

namespace Ribbit.Tests;

/// <summary>
/// The Elbow direction dial. Its one promise: every arrow is a direction a
/// click really resolves to the turn the arrow stands for, in any view, and
/// no direction a click decides is left without an arrow.
/// </summary>
public class ElbowDialTests
{
    private static readonly Vec3 Right = Vec3.BasisX, Up = Vec3.BasisY;   // a plan view
    private static readonly Vec3 O = new(100, 200, 10);
    private const double Dz = 0.5;

    private static List<DialSpoke> Decided(ElbowDial d) => d.Spokes.FindAll(s => s.Turn is not null);

    [Fact]
    public void EastRunInPlan_ShowsTheFourFlatTurns_AndAsksStraightAhead()
    {
        ElbowDial d = ElbowDial.Build(O, new Vec3(1, 0, 0), Right, Up, Dz);

        Assert.Equal(4, Decided(d).Count);
        Assert.Equal(2, d.Spokes.FindAll(s => s.Ink == DialInk.Ninety).Count);      // north, south
        Assert.Equal(2, d.Spokes.FindAll(s => s.Ink == DialInk.FortyFive).Count);   // NE, SE
        // Straight ahead, the 45 up and the 45 down share the screen line: a click there asks.
        DialSpoke ahead = Assert.Single(d.Spokes, s => s.Ink == DialInk.Ask);
        Assert.Equal(1.0, ahead.U, 9);
        Assert.True(d.HasEdgeOn);   // straight up / down: menu only
    }

    [Fact]
    public void RiserInPlan_ShowsEightFlatNinetiesAndNoFortyFives()
    {
        ElbowDial d = ElbowDial.Build(O, new Vec3(0, 0, 1), Right, Up, Dz);

        Assert.Equal(8, d.Spokes.Count);
        Assert.All(d.Spokes, s => Assert.Equal(DialInk.Ninety, s.Ink));
        Assert.All(d.Spokes, s => Assert.Equal(0.0, s.Turn!.Value.Direction.Z, 9));
    }

    // The promise, over many views and runs: click along any arrow and you get its turn.
    [Theory]
    [InlineData(1, 0, 0, 0)]
    [InlineData(0, 0, 1, 0)]
    [InlineData(1, 0, 0.05, 30)]
    [InlineData(0.3, 0.8, 0.2, 55)]
    [InlineData(1, 1, 0, 120)]
    [InlineData(0, 1, -1, 210)]
    public void EveryArrow_IsWhatAClickAlongItGives(double x, double y, double z, double orbitDeg)
    {
        (Vec3 right, Vec3 up) = Orbit(orbitDeg);
        Vec3 run = new Vec3(x, y, z).Normalize();
        ElbowDial d = ElbowDial.Build(O, run, right, up, Dz);

        foreach (DialSpoke s in d.Spokes)
        {
            (TurnCandidate? got, _) = ElbowMath.PickTurnDirection(run, (s.U * d.Radius, s.V * d.Radius), right, up, Dz);
            if (s.Turn is null)
            {
                Assert.Null(got);
            }
            else
            {
                Assert.NotNull(got);
                Assert.Equal(s.Turn.Value.AngleDeg, got!.Value.AngleDeg);
                Assert.True(s.Turn.Value.Direction.DotProduct(got.Value.Direction) > 1 - 1e-9);
            }
        }
    }

    // ... and no decided turn is missing an arrow: sweep the whole dial.
    [Theory]
    [InlineData(1, 0, 0, 0)]
    [InlineData(0, 0, 1, 0)]
    [InlineData(0.3, 0.8, 0.2, 55)]
    public void EveryTurnAClickCanMake_HasAnArrow(double x, double y, double z, double orbitDeg)
    {
        (Vec3 right, Vec3 up) = Orbit(orbitDeg);
        Vec3 run = new Vec3(x, y, z).Normalize();
        ElbowDial d = ElbowDial.Build(O, run, right, up, Dz);

        for (int deg = 0; deg < 360; deg++)
        {
            double a = deg * Math.PI / 180;
            (TurnCandidate? got, _) = ElbowMath.PickTurnDirection(run, (Math.Cos(a) * d.Radius, Math.Sin(a) * d.Radius), right, up, Dz);
            if (got is null)
                continue;
            Assert.Contains(d.Spokes, s => s.Turn is { } t && t.AngleDeg == got.Value.AngleDeg
                                           && t.Direction.DotProduct(got.Value.Direction) > 1 - 1e-9);
        }
    }

    [Fact]
    public void RingAndMenuZone_AreCentredOnTheOpenEnd_InTheScreenPlane()
    {
        ElbowDial d = ElbowDial.Build(O, new Vec3(1, 0, 0), Right, Up, Dz);

        Assert.Equal(ElbowDial.RingOverDeadZone * Dz, d.Radius, 12);
        List<DialStroke> ring = d.Strokes.FindAll(s => s.Ink == DialInk.Ring);
        List<DialStroke> zone = d.Strokes.FindAll(s => s.Ink == DialInk.MenuZone);
        Assert.Equal(ElbowDial.CircleSegments, ring.Count);
        Assert.Equal(ElbowDial.CircleSegments, zone.Count);
        Assert.All(ring, s => Assert.Equal(d.Radius, s.A.DistanceTo(O), 9));
        Assert.All(zone, s => Assert.Equal(Dz, s.A.DistanceTo(O), 9));
        Assert.All(d.Strokes, s => { Assert.Equal(O.Z, s.A.Z, 9); Assert.Equal(O.Z, s.B.Z, 9); });   // flat to a plan screen
    }

    [Fact]
    public void FortyFiveArrows_AreShorterThanNinetyArrows()
    {
        ElbowDial d = ElbowDial.Build(O, new Vec3(1, 0, 0), Right, Up, Dz);
        double Reach(DialInk ink) => d.Strokes.FindAll(s => s.Ink == ink).Max(s => Math.Max(s.A.DistanceTo(O), s.B.DistanceTo(O)));

        Assert.Equal(d.Radius, Reach(DialInk.Ninety), 9);
        Assert.Equal(d.Radius * ElbowDial.FortyFiveLength, Reach(DialInk.FortyFive), 9);
    }

    /// <summary>A view orbited about world Z and tilted 35 degrees down: screen right / up.</summary>
    private static (Vec3 Right, Vec3 Up) Orbit(double deg)
    {
        if (deg == 0)
            return (Right, Up);
        double a = deg * Math.PI / 180, tilt = 35 * Math.PI / 180;
        Vec3 right = new(Math.Cos(a), Math.Sin(a), 0);
        Vec3 toViewer = new Vec3(-Math.Sin(a) * Math.Cos(tilt), Math.Cos(a) * Math.Cos(tilt), Math.Sin(tilt)).Negate();
        Vec3 up = toViewer.CrossProduct(right).Normalize();
        if (up.Z < 0)
            up = up.Negate();
        return (right, up);
    }
}
