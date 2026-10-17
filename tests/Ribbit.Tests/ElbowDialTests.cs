using Ribbit.Geometry;
using Xunit;

namespace Ribbit.Tests;

/// <summary>
/// The Elbow direction dial (3D views). Its promises: EVERY turn has a target
/// of its own - no menu, ever - the targets sit far enough apart to be clicked,
/// a click on a target makes exactly that turn, and a target stays where its
/// turn really points unless it has to move.
/// </summary>
public class ElbowDialTests
{
    private static readonly Vec3 O = new(100, 200, 10);
    private const double Dz = 0.5;

    /// <summary>Revit's default 3D view: looking down from the south-east.</summary>
    private static readonly Vec3 IsoRight = new Vec3(1, 1, 0).Normalize();
    private static readonly Vec3 IsoUp = new Vec3(-1, 1, 2).Normalize();

    private static readonly Vec3 East = new(1, 0, 0);

    private static DialTarget Target(ElbowDial d, Vec3 direction, int angle)
        => d.Targets.Single(t => t.Turn.AngleDeg == angle && t.Turn.Direction.DotProduct(direction.Normalize()) > 1 - 1e-9);

    private static double Angle(double u, double v) => Math.Atan2(v, u);

    private static double Reach(DialTarget t) => Math.Sqrt((t.U * t.U) + (t.V * t.V));

    private static double MinGap(ElbowDial d)
    {
        double min = double.MaxValue;
        for (int i = 0; i < d.Targets.Count; i++)
        {
            for (int j = i + 1; j < d.Targets.Count; j++)
            {
                DialTarget a = d.Targets[i], b = d.Targets[j];
                min = Math.Min(min, Math.Sqrt(((a.U - b.U) * (a.U - b.U)) + ((a.V - b.V) * (a.V - b.V))));
            }
        }

        return min / d.Radius;
    }

    // ---------------------------------------------------------------- the default 3D view

    [Fact]
    public void IsoView_EastRun_EveryTurnHasATarget_AndClickingItMakesThatTurn()
    {
        ElbowDial d = ElbowDial.Build(O, East, IsoRight, IsoUp, Dz);

        Assert.Equal(8, d.Targets.Count);
        foreach (DialTarget t in d.Targets)
        {
            (TurnCandidate? got, string? why) = d.Resolve(t.U, t.V);
            Assert.Null(why);
            Assert.Equal(t.Turn, got);
        }
    }

    // In this view "90 north" and "45 up" share a screen line, and so do "45
    // south-east" and "90 down" - the menu's old job. Their tips are at
    // different lengths, so both stay on their true line, just apart.
    [Fact]
    public void IsoView_TurnsSharingAScreenLine_AreSeparatedByLength_OnTheirTrueLine()
    {
        ElbowDial d = ElbowDial.Build(O, East, IsoRight, IsoUp, Dz);

        DialTarget north90 = Target(d, new Vec3(0, 1, 0), 90);
        DialTarget up45 = Target(d, new Vec3(1, 0, 1), 45);
        DialTarget southEast45 = Target(d, new Vec3(1, -1, 0), 45);
        DialTarget down90 = Target(d, new Vec3(0, 0, -1), 90);

        Assert.Equal(Angle(north90.U, north90.V), Angle(up45.U, up45.V), 9);
        Assert.Equal(Angle(southEast45.U, southEast45.V), Angle(down90.U, down90.V), 9);
        Assert.True(MinGap(d) >= ElbowDial.Spacing - 1e-9, $"min gap {MinGap(d):F3}");
        Assert.True(Reach(north90) > Reach(up45));
        Assert.True(Reach(down90) > Reach(southEast45));
    }

    // A turn nobody competes with stays exactly where it points.
    [Fact]
    public void IsoView_AnUncontestedTurn_IsNotMoved()
    {
        ElbowDial d = ElbowDial.Build(O, East, IsoRight, IsoUp, Dz);
        DialTarget south = Target(d, new Vec3(0, -1, 0), 90);

        Assert.False(south.Moved);
        Assert.Equal(new Vec3(0, -1, 0).DotProduct(IsoRight) * d.Radius, south.U, 12);
        Assert.Equal(new Vec3(0, -1, 0).DotProduct(IsoUp) * d.Radius, south.V, 12);
    }

    [Fact]
    public void IsoView_UpAndDownTurnsCarryADotAndACross_SidewaysOnesNothing()
    {
        ElbowDial d = ElbowDial.Build(O, East, IsoRight, IsoUp, Dz);

        Assert.Equal(1, Target(d, new Vec3(0, 0, 1), 90).Mark);
        Assert.Equal(1, Target(d, new Vec3(1, 0, 1), 45).Mark);
        Assert.Equal(-1, Target(d, new Vec3(0, 0, -1), 90).Mark);
        Assert.Equal(-1, Target(d, new Vec3(1, 0, -1), 45).Mark);
        Assert.All(d.Targets.Where(t => t.Turn.Kind == TurnKind.Plan), t => Assert.Equal(0, t.Mark));
    }

    // ---------------------------------------------------------------- views that hide turns

    // A 3D view straight down: the 90 up and the 90 down point at the viewer
    // and away (no screen direction at all), and the 45 up and down sit on the
    // very same spot. Every one of them still gets a target.
    [Fact]
    public void TopDown3D_EdgeOnTurnsAreParkedBehind_AndTwinsPushedApart()
    {
        ElbowDial d = ElbowDial.Build(O, East, Vec3.BasisX, Vec3.BasisY, Dz);

        DialTarget rise = Target(d, new Vec3(0, 0, 1), 90);
        DialTarget drop = Target(d, new Vec3(0, 0, -1), 90);
        Assert.True(rise.Moved && drop.Moved);
        Assert.True(rise.U < 0 && drop.U < 0, "parked behind the open end, beside the run");
        Assert.True(rise.V > 0 && drop.V < 0, "up on one side, down on the other");
        Assert.Equal((1, -1), (rise.Mark, drop.Mark));

        DialTarget up45 = Target(d, new Vec3(1, 0, 1), 45);
        DialTarget down45 = Target(d, new Vec3(1, 0, -1), 45);
        Assert.True(up45.Moved && down45.Moved);
        Assert.NotEqual(up45.V, down45.V);
        Assert.True(MinGap(d) >= ElbowDial.Spacing - 1e-9, $"min gap {MinGap(d):F3}");
        foreach (DialTarget t in d.Targets)
            Assert.Equal(t.Turn, d.Resolve(t.U, t.V).Turn);
    }

    // A 3D view looking north, level: the 90 north heads away from you and the
    // 90 south toward you. Sideways turns, but edge-on - so they carry the
    // marks too (cross = away, dot = toward).
    [Fact]
    public void Front3D_SidewaysTurnsAtTheViewerAreMarkedAndParked()
    {
        ElbowDial d = ElbowDial.Build(O, East, Vec3.BasisX, Vec3.BasisZ, Dz);

        DialTarget north = Target(d, new Vec3(0, 1, 0), 90);
        DialTarget south = Target(d, new Vec3(0, -1, 0), 90);
        Assert.Equal((-1, 1), (north.Mark, south.Mark));
        Assert.True(north.Moved && south.Moved);
        Assert.True(MinGap(d) >= ElbowDial.Spacing - 1e-9, $"min gap {MinGap(d):F3}");
    }

    // Looking straight along the run: the run itself has no screen direction,
    // and the dial still lays every turn out.
    [Fact]
    public void ViewAlongTheRun_StillGivesEveryTurnARoom()
    {
        ElbowDial d = ElbowDial.Build(O, East, Vec3.BasisY, Vec3.BasisZ, Dz);

        Assert.Equal(8, d.Targets.Count);
        Assert.True(MinGap(d) >= ElbowDial.Spacing - 1e-9, $"min gap {MinGap(d):F3}");
        foreach (DialTarget t in d.Targets)
            Assert.Equal(t.Turn, d.Resolve(t.U, t.V).Turn);
    }

    // ---------------------------------------------------------------- every view

    // The promise over many orbits and runs, risers included: each target is
    // its own turn, they keep their distance, and none sits in the click-again
    // circle or past the outer reach.
    [Theory]
    [InlineData(1, 0, 0)]
    [InlineData(0, 0, 1)]
    [InlineData(0, 0, -1)]
    [InlineData(1, 0, 0.05)]
    [InlineData(0.3, 0.8, 0.2)]
    [InlineData(1, 1, 0)]
    [InlineData(0, 1, -1)]
    public void EveryOrbit_EveryTurnIsClickable(double x, double y, double z)
    {
        Vec3 run = new Vec3(x, y, z).Normalize();
        for (int orbit = 0; orbit < 360; orbit += 15)
        {
            for (int tilt = -80; tilt <= 80; tilt += 20)
            {
                (Vec3 right, Vec3 up) = View(orbit, tilt);
                ElbowDial d = ElbowDial.Build(O, run, right, up, Dz);

                Assert.Equal(ElbowMath.TurnCandidates(run).Count, d.Targets.Count);
                Assert.True(MinGap(d) >= 0.9 * ElbowDial.Spacing, $"orbit {orbit} tilt {tilt}: min gap {MinGap(d):F3}");
                foreach (DialTarget t in d.Targets)
                {
                    Assert.InRange(Reach(t) / d.Radius, ElbowDial.MinReach - 1e-9, ElbowDial.MaxReach + 1e-9);
                    Assert.Equal(t.Turn, d.Resolve(t.U, t.V).Turn);
                }
            }
        }
    }

    // A riser in a building drawn at 30 degrees: its flat 90s are the
    // building's axes, on the dial too.
    [Fact]
    public void RiserInARotatedBuilding_TargetsFollowTheBuilding()
    {
        double frame = 30.0 * Math.PI / 180.0;
        ElbowDial d = ElbowDial.Build(O, new Vec3(0, 0, 1), IsoRight, IsoUp, Dz, frame);

        Assert.Equal(16, d.Targets.Count);
        Assert.Contains(d.Targets, t => t.Turn.AngleDeg == 90
            && t.Turn.Direction.DotProduct(new Vec3(Math.Cos(frame), Math.Sin(frame), 0)) > 1 - 1e-12);
    }

    // ---------------------------------------------------------------- the click

    [Fact]
    public void Click_OnTheOpenEnd_AsksAgain()
    {
        ElbowDial d = ElbowDial.Build(O, East, IsoRight, IsoUp, Dz);

        (TurnCandidate? got, string? why) = d.Resolve(0.1 * Dz, 0.1 * Dz);

        Assert.Null(got);
        Assert.Contains("open end itself", why);
    }

    // A long throw past a tip means that tip: a click five radii out along
    // "90 north" is the 90, not the "45 up" that shares its line.
    [Fact]
    public void Click_FarOutAlongALine_TakesTheOuterTarget()
    {
        ElbowDial d = ElbowDial.Build(O, East, IsoRight, IsoUp, Dz);
        DialTarget north90 = Target(d, new Vec3(0, 1, 0), 90);

        (TurnCandidate? got, _) = d.Resolve(north90.U * 5, north90.V * 5);

        Assert.Equal(north90.Turn, got);
    }

    // Behind the open end, along the run, nothing is near: ask, never guess.
    [Fact]
    public void Click_NowhereNearAnyArrow_AsksAgain()
    {
        ElbowDial d = ElbowDial.Build(O, East, IsoRight, IsoUp, Dz);
        (double u, double v) = ElbowMath.ScreenUv(East.Negate(), IsoRight, IsoUp);
        double m = Math.Sqrt((u * u) + (v * v));

        (TurnCandidate? got, string? why) = d.Resolve(u / m * d.Radius, v / m * d.Radius);

        Assert.Null(got);
        Assert.Contains("not near any arrow", why);
    }

    // ---------------------------------------------------------------- the drawing

    // Circles are kept whole (Revit draws each as a couple of arcs): a plane circle
    // of the dial's radius in its own plane, the click-again circle dashed.
    [Fact]
    public void Drawing_TwoPlanesAsTrueCircles_AndADashedClickAgainCircle()
    {
        ElbowDial d = ElbowDial.Build(O, East, IsoRight, IsoUp, Dz);

        Assert.Equal(ElbowDial.RingOverDeadZone * Dz, d.Radius, 12);
        DialCircle level = Assert.Single(d.Circles, c => c.Ink == DialInk.SidewaysPlane);
        DialCircle upright = Assert.Single(d.Circles, c => c.Ink == DialInk.UpDownPlane);
        DialCircle again = Assert.Single(d.Circles, c => c.Ink == DialInk.ClickAgain);

        Assert.All(new[] { level, upright }, c => { Assert.Equal(d.Radius, c.Radius, 12); Assert.Equal(O, c.Centre); });
        Assert.All(ElbowDial.Segments(level, ElbowDial.CircleSegments), s => { Assert.Equal(d.Radius, s.A.DistanceTo(O), 9); Assert.Equal(O.Z, s.A.Z, 9); });   // level: flat
        Assert.All(ElbowDial.Segments(upright, ElbowDial.CircleSegments), s => Assert.Equal(O.Y, s.A.Y, 9));                                                 // upright: through the run
        Assert.True(again.Dashed);
        Assert.Equal(Dz, again.Radius, 12);
        Assert.False(level.Dashed);
    }

    // A circle as segments (for a picture): every one, or every other one when dashed.
    [Fact]
    public void Segments_AllOfASolidCircle_HalfOfADashedOne()
    {
        DialCircle solid = new(O, Vec3.BasisX, Vec3.BasisY, 2.0, DialInk.Sideways90, false);
        DialCircle dashed = solid with { Dashed = true };

        Assert.Equal(24, ElbowDial.Segments(solid, 24).Count);
        Assert.Equal(12, ElbowDial.Segments(dashed, 24).Count);
        Assert.All(ElbowDial.Segments(solid, 24), s => { Assert.Equal(2.0, s.A.DistanceTo(O), 12); Assert.Equal(DialInk.Sideways90, s.Ink); });
        Assert.Equal(O.Add(new Vec3(0, 2, 0)).X, solid.PointAt(Math.PI / 2).X, 12);
    }

    [Fact]
    public void Drawing_ARiserHasOnlyItsLevelPlane()
    {
        ElbowDial d = ElbowDial.Build(O, new Vec3(0, 0, 1), IsoRight, IsoUp, Dz);

        DialCircle level = Assert.Single(d.Circles, c => c.Ink == DialInk.SidewaysPlane);
        Assert.DoesNotContain(d.Circles, c => c.Ink == DialInk.UpDownPlane);
        Assert.All(ElbowDial.Segments(level, ElbowDial.CircleSegments), s => Assert.Equal(O.Z, s.A.Z, 9));
    }

    // Ink by plane and size: a 90 is one solid shaft, a 45 four dashes; every
    // target has its circle; an up/down turn adds its dot (a circle) or cross (2 strokes).
    [Fact]
    public void Drawing_InkSaysPlaneAndSize()
    {
        ElbowDial d = ElbowDial.Build(O, East, IsoRight, IsoUp, Dz);

        Assert.Equal(DialInk.Sideways90, ElbowDial.InkOf(Target(d, new Vec3(0, 1, 0), 90).Turn));
        Assert.Equal(DialInk.Sideways45, ElbowDial.InkOf(Target(d, new Vec3(1, 1, 0), 45).Turn));
        Assert.Equal(DialInk.UpDown90, ElbowDial.InkOf(Target(d, new Vec3(0, 0, 1), 90).Turn));
        Assert.Equal(DialInk.UpDown45, ElbowDial.InkOf(Target(d, new Vec3(1, 0, 1), 45).Turn));

        // The 90 north and the 90 down each pass through the 45 on their line,
        // so each shaft is drawn in two pieces: one stroke more.
        Assert.Equal((2 * 1) + 1, d.Strokes.Count(s => s.Ink == DialInk.Sideways90));
        Assert.Equal(2 * 4, d.Strokes.Count(s => s.Ink == DialInk.Sideways45));
        Assert.Equal((2 * 1) + 1 + 2, d.Strokes.Count(s => s.Ink == DialInk.UpDown90));
        Assert.Equal((2 * 4) + 2, d.Strokes.Count(s => s.Ink == DialInk.UpDown45));
        Assert.Equal(2, d.Circles.Count(c => c.Ink == DialInk.Sideways90));
        Assert.Equal(2, d.Circles.Count(c => c.Ink == DialInk.Sideways45));
        Assert.Equal(2 + 1, d.Circles.Count(c => c.Ink == DialInk.UpDown90));   // + the dot of the 90 up
        Assert.Equal(2 + 1, d.Circles.Count(c => c.Ink == DialInk.UpDown45));
    }

    // No shaft runs through another target's circle, in any view: a circle on
    // someone else's line would read as a bead on their arrow, not a target.
    [Theory]
    [InlineData(1, 0, 0)]
    [InlineData(0, 0, 1)]
    [InlineData(0.3, 0.8, 0.2)]
    public void Drawing_NoShaftRunsThroughAnotherTarget(double x, double y, double z)
    {
        Vec3 run = new Vec3(x, y, z).Normalize();
        for (int orbit = 0; orbit < 360; orbit += 30)
        {
            for (int tilt = -60; tilt <= 80; tilt += 35)
            {
                (Vec3 right, Vec3 up) = View(orbit, tilt);
                ElbowDial d = ElbowDial.Build(O, run, right, up, Dz);
                double ring = ElbowDial.TargetSize * d.Radius;
                foreach (DialStroke s in d.Strokes)
                {
                    (double au, double av) = ElbowMath.ScreenUv(s.A.Subtract(O), right, up);
                    (double bu, double bv) = ElbowMath.ScreenUv(s.B.Subtract(O), right, up);
                    foreach (DialTarget t in d.Targets)
                    {
                        // A target's own cross sits inside its own circle by design.
                        if (Math.Sqrt(((au - t.U) * (au - t.U)) + ((av - t.V) * (av - t.V))) <= ring + 1e-9
                            && Math.Sqrt(((bu - t.U) * (bu - t.U)) + ((bv - t.V) * (bv - t.V))) <= ring + 1e-9)
                            continue;
                        Assert.True(SegmentGap(au, av, bu, bv, t.U, t.V) >= ring - 1e-9,
                            $"orbit {orbit} tilt {tilt}: a {s.Ink} stroke runs through the {t.Turn.AngleDeg} target");
                    }
                }
            }
        }
    }

    // Seen edge-on a plane is just a line across the dial: it is left out.
    [Fact]
    public void Drawing_APlaneSeenEdgeOn_IsLeftOut()
    {
        ElbowDial top = ElbowDial.Build(O, East, Vec3.BasisX, Vec3.BasisY, Dz);
        ElbowDial front = ElbowDial.Build(O, East, Vec3.BasisX, Vec3.BasisZ, Dz);

        Assert.DoesNotContain(top.Circles, c => c.Ink == DialInk.UpDownPlane);
        Assert.Contains(top.Circles, c => c.Ink == DialInk.SidewaysPlane);
        Assert.DoesNotContain(front.Circles, c => c.Ink == DialInk.SidewaysPlane);
        Assert.Contains(front.Circles, c => c.Ink == DialInk.UpDownPlane);
    }

    private static double SegmentGap(double au, double av, double bu, double bv, double pu, double pv)
    {
        double du = bu - au, dv = bv - av;
        double len2 = (du * du) + (dv * dv);
        double t = len2 == 0 ? 0 : Math.Max(0, Math.Min(1, (((pu - au) * du) + ((pv - av) * dv)) / len2));
        double cu = au + (t * du) - pu, cv = av + (t * dv) - pv;
        return Math.Sqrt((cu * cu) + (cv * cv));
    }

    /// <summary>A view orbited about world Z and tilted (degrees, + looks down): screen right / up.</summary>
    private static (Vec3 Right, Vec3 Up) View(double orbitDeg, double tiltDeg)
    {
        double a = orbitDeg * Math.PI / 180, t = tiltDeg * Math.PI / 180;
        Vec3 right = new(Math.Cos(a), Math.Sin(a), 0);
        Vec3 forward = new(-Math.Sin(a) * Math.Cos(t), Math.Cos(a) * Math.Cos(t), -Math.Sin(t));
        Vec3 up = right.CrossProduct(forward).Normalize();
        return (right, up);
    }
}
