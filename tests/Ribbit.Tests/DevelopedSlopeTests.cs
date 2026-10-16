using Ribbit.Geometry;
using Xunit;

namespace Ribbit.Tests;

/// <summary>
/// Slope Through's central claim, tested directly: chaining each leg's anchor
/// elevation to where the upstream leg leaves the shared corner produces ONE
/// developed slope across every turn — rise over the total plan run as you walk
/// the pipe, which is what a ratio on a drawing means for a branch that turns.
///
/// The topology that finds the legs needs Revit, but this — the arithmetic the
/// whole tool exists to get right — does not. It reproduces exactly what
/// SlopeThrough.SolveLeg does for a downstream leg:
///   anchor at the corner station, anchorZ = parent.NewZ(parent station of corner).
/// </summary>
public class DevelopedSlopeTests
{
    private const double Tol = 1e-9;
    private const double Slope = 0.002;   // 2 mm/m, i.e. 1:500

    /// <summary>A flat run along <paramref name="axis"/> from p0, of the given plan length.</summary>
    private static SlopeRun Leg(Vec3 p0, Vec3 axis, double planLength, double z)
    {
        Vec3 start = new(p0.X, p0.Y, z);
        Vec3 end = new(p0.X + axis.X * planLength, p0.Y + axis.Y * planLength, z);
        SlopeSegment seg = new(1, start, end);
        return new SlopeRun([seg], axis, new Vec3(p0.X, p0.Y, 0.0));
    }

    // Leg 1 runs 10 ft east from the fixed end; leg 2 turns north and runs 5 ft.
    // Developed length is 15, so the far end must sit 0.002 x 15 above the fixed
    // end - NOT 0.002 x 5, which is what each leg re-anchoring to its own corner
    // elevation would give.
    [Fact]
    public void TwoLegs_RiseByTheSlopeTimesTheDEVELOPEDLength()
    {
        SlopeRun leg1 = Leg(new Vec3(0, 0, 0), Vec3.BasisX, 10.0, z: 0.0);
        RePitch pitch1 = new(leg1, leg1.SMin, Slope);

        Vec3 corner = new(10, 0, 0);
        double cornerZ = pitch1.NewZ(leg1.Station(corner));

        Assert.Equal(0.002 * 10.0, cornerZ, Tol);

        SlopeRun leg2 = Leg(new Vec3(10, 0, 0), Vec3.BasisY, 5.0, z: 0.0);
        RePitch pitch2 = new(leg2, leg2.Station(corner), Slope, anchorZ: cornerZ);

        double farZ = pitch2.NewZ(leg2.SMax);

        Assert.Equal(0.002 * 15.0, farZ, Tol);
    }

    // The corner itself must be single-valued: whatever elevation leg 1 leaves it
    // at is exactly where leg 2 starts. A mismatch here is an open joint.
    [Fact]
    public void TheSharedCorner_HasOneElevationFromBothSides()
    {
        SlopeRun leg1 = Leg(new Vec3(0, 0, 0), Vec3.BasisX, 10.0, z: 0.0);
        RePitch pitch1 = new(leg1, leg1.SMin, Slope);

        Vec3 corner = new(10, 0, 0);
        double cornerZ = pitch1.NewZ(leg1.Station(corner));

        SlopeRun leg2 = Leg(new Vec3(10, 0, 0), Vec3.BasisY, 5.0, z: 0.0);
        RePitch pitch2 = new(leg2, leg2.Station(corner), Slope, anchorZ: cornerZ);

        Assert.Equal(cornerZ, pitch2.NewZ(leg2.Station(corner)), Tol);
    }

    // Three legs, so the chaining is exercised past the first hand-off.
    [Fact]
    public void ThreeLegs_StayOnOneDevelopedSlope()
    {
        double[] lengths = [8.0, 6.0, 4.0];
        Vec3[] axes = [Vec3.BasisX, Vec3.BasisY, Vec3.BasisX];
        Vec3 at = new(0, 0, 0);

        double anchorZ = 0.0;
        double developed = 0.0;

        for (int i = 0; i < lengths.Length; i++)
        {
            SlopeRun run = Leg(at, axes[i], lengths[i], z: 0.0);
            Vec3 corner = new(at.X, at.Y, 0.0);

            RePitch pitch = new(run, run.Station(corner), Slope, anchorZ: anchorZ);

            developed += lengths[i];
            Vec3 farEnd = new(
                at.X + axes[i].X * lengths[i], at.Y + axes[i].Y * lengths[i], 0.0);

            anchorZ = pitch.NewZ(run.Station(farEnd));
            at = farEnd;

            Assert.Equal(Slope * developed, anchorZ, Tol);
        }

        Assert.Equal(Slope * 18.0, anchorZ, Tol);
    }

    // A FALLING branch is a valid, documented input - the slope is signed, and
    // the dry run is what catches a wrong sign. It must chain the same way.
    [Fact]
    public void ANegativeSlope_FallsAwayFromTheFixedEndAcrossTheCorner()
    {
        const double falling = -0.002;

        SlopeRun leg1 = Leg(new Vec3(0, 0, 0), Vec3.BasisX, 10.0, z: 0.0);
        RePitch pitch1 = new(leg1, leg1.SMin, falling);

        Vec3 corner = new(10, 0, 0);
        double cornerZ = pitch1.NewZ(leg1.Station(corner));

        SlopeRun leg2 = Leg(new Vec3(10, 0, 0), Vec3.BasisY, 5.0, z: 0.0);
        RePitch pitch2 = new(leg2, leg2.Station(corner), falling, anchorZ: cornerZ);

        Assert.Equal(falling * 15.0, pitch2.NewZ(leg2.SMax), Tol);
    }

    // THE CORNER ELBOW IS NOT FREE. Its centre sits PAST the end of the parent's
    // pipe, so the span the pitch integrates across is longer than the pipe. A
    // live run made this visible: 3462 mm of pipe reported, but 16.0 mm of rise
    // at 4 mm/m, which is 4000 mm of developed run - the missing 538 mm was five
    // elbows at ~105 mm each. Any developed length that sums only pipe plan
    // lengths will not reconcile with the elevations printed beside it.
    [Fact]
    public void TheCornerElbowSpan_CountsTowardTheDevelopedRun()
    {
        SlopeRun leg1 = Leg(new Vec3(0, 0, 0), Vec3.BasisX, 10.0, z: 0.0);

        // Anchored at the far end, as a branch tying into a main at its end is.
        RePitch pitch1 = new(leg1, leg1.SMax, Slope);

        // The elbow centre sits 2 beyond the pipe's near end - i.e. OUTSIDE the
        // run's own station range, which is exactly what the live run showed
        // (corner stations of -102 mm on a run starting at 0).
        Vec3 corner = new(-2, 0, 0);
        double cornerStation = leg1.Station(corner);

        Assert.True(cornerStation < leg1.SMin, "the corner must sit outside the run");

        double cornerZ = pitch1.NewZ(cornerStation);
        double pipeEndZ = pitch1.NewZ(leg1.SMin);

        // The pipe's own end rises by slope x 10; the elbow centre by slope x 12.
        Assert.Equal(Slope * 10.0, pipeEndZ, Tol);
        Assert.Equal(Slope * 12.0, cornerZ, Tol);

        // So a second leg chained from the corner starts 2 further along the
        // developed run than the pipe length alone would suggest.
        SlopeRun leg2 = Leg(new Vec3(-2, 0, 0), Vec3.BasisY, 5.0, z: 0.0);
        RePitch pitch2 = new(leg2, leg2.Station(corner), Slope, anchorZ: cornerZ);

        Assert.Equal(Slope * 17.0, pitch2.NewZ(leg2.SMax), Tol);

        // 10 + 5 = 15 of pipe, but 17 of developed run. Reporting 15 here is the
        // bug this test exists to stop coming back.
        Assert.NotEqual(Slope * 15.0, pitch2.NewZ(leg2.SMax), precision: 9);
    }

    // Without the anchorZ override every leg would re-level to its own current
    // corner height, and the branch would come out as a staircase of identical
    // short slopes instead of one developed run. This pins the difference the
    // override actually makes.
    [Fact]
    public void WithoutTheAnchorOverride_TheSecondLegWouldRestartFromZero()
    {
        SlopeRun leg2 = Leg(new Vec3(10, 0, 0), Vec3.BasisY, 5.0, z: 0.0);

        RePitch chained = new(leg2, leg2.Station(new Vec3(10, 0, 0)), Slope, anchorZ: 0.002 * 10.0);
        RePitch unchained = new(leg2, leg2.Station(new Vec3(10, 0, 0)), Slope);

        Assert.Equal(0.002 * 15.0, chained.NewZ(leg2.SMax), Tol);
        Assert.Equal(0.002 * 5.0, unchained.NewZ(leg2.SMax), Tol);
    }
}
