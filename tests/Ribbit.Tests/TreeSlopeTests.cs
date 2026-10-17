using Ribbit.Geometry;
using Xunit;

namespace Ribbit.Tests;

/// <summary>
/// Slope Through on a TREE: a main with branches teed off it. What
/// SlopeThrough.SolveLeg does for a branch that meets its parent at a tee:
///   anchor at the TEE CENTRE's station on the branch (unclamped - it sits a
///   half-tee short of the branch's first pipe), anchorZ = parent.NewZ there.
/// And the roll that tilts the tee's outlet onto the branch's slope
/// (<see cref="SlopeSolver.JunctionRoll"/>).
/// </summary>
public class TreeSlopeTests
{
    private const double Tol = 1e-9;
    private const double Slope = 0.01;   // 10 mm/m
    private const double HalfTee = 0.25;

    private static SlopeRun Run(Vec3 a, Vec3 b, Vec3 axis)
        => new([new SlopeSegment(1, a, b)], axis, new Vec3(a.X, a.Y, 0.0));

    private static double Deg(double rad) => rad * 180.0 / Math.PI;

    // Main 20 ft east from the fixed end; a tee at x = 8; the branch runs north
    // from the tee's outlet (y = 0.25) to y = 6. Every point of the branch must
    // sit at slope x (8 + y): along the main to the tee, then out along the branch.
    [Fact]
    public void A_branch_teed_off_the_main_rises_by_the_developed_distance_through_the_tee()
    {
        SlopeRun main = Run(new Vec3(0, 0, 0), new Vec3(20, 0, 0), Vec3.BasisX);
        RePitch mainPitch = new(main, main.SMin, Slope);

        Vec3 tee = new(8, 0, 0);
        SlopeRun branch = Run(new Vec3(8, HalfTee, 0), new Vec3(8, 6, 0), Vec3.BasisY);
        RePitch branchPitch = new(branch, branch.Station(tee), Slope,
            anchorZ: mainPitch.NewZ(main.Station(tee)));

        Assert.Equal(Slope * (8 + HalfTee), branchPitch.NewZ(branch.SMin), Tol);
        Assert.Equal(Slope * (8 + 6), branchPitch.NewZ(branch.SMax), Tol);
        // The anchor is off the run, so it is not an end anchor - but every station
        // is on the far side of it, so the whole branch takes +slope.
        Assert.False(branchPitch.IsEndAnchor);
        Assert.Equal(Slope, branchPitch.NewRateAt(branch.SMin), Tol);
        Assert.Equal(Slope, branchPitch.NewRateAt(branch.SMax), Tol);
    }

    // The same branch on the OTHER side of the main, still drawn along +Y: every
    // station is now below the anchor, so the rate along +axis is -slope - which is
    // still a RISE away from the main.
    [Fact]
    public void A_branch_on_the_minus_axis_side_still_rises_away_from_the_main()
    {
        SlopeRun main = Run(new Vec3(0, 0, 0), new Vec3(20, 0, 0), Vec3.BasisX);
        RePitch mainPitch = new(main, main.SMin, Slope);

        Vec3 tee = new(8, 0, 0);
        SlopeRun branch = Run(new Vec3(8, -6, 0), new Vec3(8, -HalfTee, 0), Vec3.BasisY);
        RePitch branchPitch = new(branch, branch.Station(tee), Slope,
            anchorZ: mainPitch.NewZ(main.Station(tee)));

        Assert.Equal(Slope * (8 + 6), branchPitch.NewZ(branch.SMin), Tol);
        Assert.Equal(Slope * (8 + HalfTee), branchPitch.NewZ(branch.SMax), Tol);
        Assert.Equal(-Slope, branchPitch.NewRateAt(branch.SMin), Tol);
    }

    // "Falls away from the fixed end" is the same solve with the sign flipped.
    [Fact]
    public void Falling_away_drops_the_branch_by_the_same_developed_distance()
    {
        SlopeRun main = Run(new Vec3(0, 0, 0), new Vec3(20, 0, 0), Vec3.BasisX);
        RePitch mainPitch = new(main, main.SMin, -Slope);

        Vec3 tee = new(8, 0, 0);
        SlopeRun branch = Run(new Vec3(8, HalfTee, 0), new Vec3(8, 6, 0), Vec3.BasisY);
        RePitch branchPitch = new(branch, branch.Station(tee), -Slope,
            anchorZ: mainPitch.NewZ(main.Station(tee)));

        Assert.Equal(-Slope * 14, branchPitch.NewZ(branch.SMax), Tol);
    }

    // A cross with a branch both sides, collinear, is ONE leg through the cross:
    // anchored mid-run, it rises away from the cross BOTH ways (a V).
    [Fact]
    public void A_branch_straight_through_a_cross_rises_away_from_it_both_ways()
    {
        SlopeRun main = Run(new Vec3(0, 0, 0), new Vec3(20, 0, 0), Vec3.BasisX);
        RePitch mainPitch = new(main, main.SMin, Slope);

        Vec3 cross = new(12, 0, 0);
        SlopeRun branch = new(
            [new SlopeSegment(1, new Vec3(12, -5, 0), new Vec3(12, -HalfTee, 0)),
             new SlopeSegment(2, new Vec3(12, HalfTee, 0), new Vec3(12, 4, 0))],
            Vec3.BasisY, new Vec3(12, -5, 0));
        RePitch branchPitch = new(branch, branch.Station(cross), Slope,
            anchorZ: mainPitch.NewZ(main.Station(cross)));

        Assert.Equal(Slope * (12 + 5), branchPitch.NewZ(branch.SMin), Tol);
        Assert.Equal(Slope * (12 + 4), branchPitch.NewZ(branch.SMax), Tol);
    }

    // ------------------------------------------------------------------ the roll

    // The main re-pitched to +1%. A square tee's branch outlet points along +Y and
    // the pitch rotation (about the axis perpendicular to the main, i.e. Y) leaves
    // it where it was. Rolling about the main's new direction must then put it on
    // the branch's own +1%, all but exactly.
    [Fact]
    public void A_square_tee_rolls_its_outlet_onto_the_branch_slope_almost_exactly()
    {
        Vec3 dParentNew = SlopeSolver.DirectionAtRate(Vec3.BasisX, Slope);
        Vec3 outlet = Vec3.BasisY;
        Vec3 target = SlopeSolver.DirectionAtRate(Vec3.BasisY, Slope);

        double roll = SlopeSolver.JunctionRoll([(outlet, target)], dParentNew);
        double residual = SlopeSolver.RotateAbout(outlet, Vec3.Zero, dParentNew, roll).AngleTo(target);

        Assert.True(Deg(Math.Abs(roll)) > 0.5, $"it rolled {Deg(roll):F4} deg - it must tilt the outlet");
        Assert.True(Deg(residual) < 0.01, $"{Deg(residual):F5} deg left");
        // The roll never disturbs the main's own connectors: they lie along its axis.
        Assert.True(SlopeSolver.RotateAbout(dParentNew, Vec3.Zero, dParentNew, roll).DistanceTo(dParentNew) < 1e-12);
    }

    // A cross with branches both sides, each rising AWAY from the main: one roll
    // can only tip one outlet up by tipping the other down. The fair answer is no
    // roll, leaving each branch off by about the slope's own angle - which is
    // what the report then has to say.
    [Fact]
    public void A_cross_with_a_branch_each_side_is_split_fairly_and_left_off_by_the_slope_angle()
    {
        Vec3 dParentNew = SlopeSolver.DirectionAtRate(Vec3.BasisX, Slope);
        Vec3 north = Vec3.BasisY, south = Vec3.BasisY.Multiply(-1.0);
        Vec3 northTarget = SlopeSolver.DirectionAtRate(Vec3.BasisY, Slope);
        Vec3 southTarget = SlopeSolver.DirectionAtRate(Vec3.BasisY.Multiply(-1.0), Slope);

        double roll = SlopeSolver.JunctionRoll([(north, northTarget), (south, southTarget)], dParentNew);

        double n = Deg(SlopeSolver.RotateAbout(north, Vec3.Zero, dParentNew, roll).AngleTo(northTarget));
        double s = Deg(SlopeSolver.RotateAbout(south, Vec3.Zero, dParentNew, roll).AngleTo(southTarget));
        double slopeDeg = Deg(Math.Atan(Slope));

        Assert.True(Math.Abs(roll) < 1e-9, $"rolled {Deg(roll):F4} deg");
        Assert.Equal(slopeDeg, n, 3);
        Assert.Equal(slopeDeg, s, 3);
    }

    // Better than either branch's own roll for the worse of the two.
    [Fact]
    public void For_a_cross_neither_branchs_own_roll_beats_the_split()
    {
        Vec3 dParentNew = SlopeSolver.DirectionAtRate(Vec3.BasisX, Slope);
        (Vec3, Vec3)[] outlets =
        [
            (Vec3.BasisY, SlopeSolver.DirectionAtRate(Vec3.BasisY, Slope)),
            (Vec3.BasisY.Multiply(-1.0), SlopeSolver.DirectionAtRate(Vec3.BasisY.Multiply(-1.0), Slope)),
        ];

        double Worst(double r) => outlets.Max(o => SlopeSolver.RotateAbout(o.Item1, Vec3.Zero, dParentNew, r).AngleTo(o.Item2));

        double chosen = SlopeSolver.JunctionRoll(outlets, dParentNew);
        foreach ((Vec3 after, Vec3 target) in outlets)
            Assert.True(Worst(chosen) <= Worst(SlopeSolver.CornerRoll(after, target, dParentNew)) + 1e-12);
    }

    [Fact]
    public void One_outlet_is_exactly_the_corner_roll()
    {
        Vec3 axis = SlopeSolver.DirectionAtRate(new Vec3(1, 1, 0).Normalize(), 0.004);
        Vec3 after = new Vec3(-1, 1, 0.01).Normalize();
        Vec3 target = SlopeSolver.DirectionAtRate(new Vec3(-1, 1, 0).Normalize(), 0.02);

        Assert.Equal(SlopeSolver.CornerRoll(after, target, axis),
            SlopeSolver.JunctionRoll([(after, target)], axis), 12);
    }

    [Fact]
    public void No_outlets_means_no_roll()
        => Assert.Equal(0.0, SlopeSolver.JunctionRoll([], Vec3.BasisX));
}
