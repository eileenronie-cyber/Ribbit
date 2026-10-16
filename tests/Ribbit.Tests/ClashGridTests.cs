using Ribbit.Geometry;
using Ribbit.Geometry.Clash;
using Xunit;

namespace Ribbit.Tests;

/// <summary>
/// The clash detector's broad phase. This is the stage that decides which pairs
/// ever reach the expensive solid-vs-solid test, so a bug here shows up as
/// clashes silently not being reported — the worst failure mode the tool has.
/// </summary>
public class ClashGridTests
{
    private static Aabb Box(double x0, double y0, double z0, double x1, double y1, double z1)
        => new(new Vec3(x0, y0, z0), new Vec3(x1, y1, z1));

    private static Aabb Cube(double x, double y, double z, double size)
        => Box(x, y, z, x + size, y + size, z + size);

    // ------------------------------------------------------------- overlap

    [Fact]
    public void SeparateBoxes_DoNotOverlap()
        => Assert.False(Cube(0, 0, 0, 1).Overlaps(Cube(5, 0, 0, 1)));

    [Fact]
    public void IntersectingBoxes_Overlap()
        => Assert.True(Box(0, 0, 0, 2, 2, 2).Overlaps(Box(1, 1, 1, 3, 3, 3)));

    // Touching counts, matching the Python's inclusive comparisons - the pad is
    // what separates "near" from "touching", so a shared face is a candidate.
    [Fact]
    public void BoxesSharingAFace_Overlap()
        => Assert.True(Box(0, 0, 0, 1, 1, 1).Overlaps(Box(1, 0, 0, 2, 1, 1)));

    [Fact]
    public void SeparationOnAnySingleAxis_IsEnoughToMiss()
    {
        Aabb a = Box(0, 0, 0, 1, 1, 1);

        Assert.False(a.Overlaps(Box(2, 0, 0, 3, 1, 1)));   // x
        Assert.False(a.Overlaps(Box(0, 2, 0, 1, 3, 1)));   // y
        Assert.False(a.Overlaps(Box(0, 0, 2, 1, 1, 3)));   // z
    }

    // -------------------------------------------------------------- expand

    [Fact]
    public void ExpandGrowsEverySideEqually()
    {
        Aabb e = Cube(0, 0, 0, 1).Expand(0.5);

        Assert.Equal(-0.5, e.Min.X, 1e-9);
        Assert.Equal(1.5, e.Max.Z, 1e-9);
    }

    // The pad is the whole point of the broad phase: two boxes that miss by less
    // than the clearance must still become candidates.
    [Fact]
    public void APadTurnsANearMissIntoACandidate()
    {
        Aabb a = Cube(0, 0, 0, 1);
        Aabb b = Cube(1.4, 0, 0, 1);

        Assert.False(a.Overlaps(b));
        Assert.True(a.Expand(0.25).Overlaps(b.Expand(0.25)));
    }

    [Fact]
    public void AZeroPadReturnsTheSameBox()
    {
        Aabb a = Cube(1, 2, 3, 4);
        Assert.Equal(a, a.Expand(0.0));
    }

    // ----------------------------------------------------------- transform

    [Fact]
    public void TransformRefitsAxisAligned()
    {
        Aabb moved = Cube(0, 0, 0, 2).Transform(p => new Vec3(p.X + 10, p.Y, p.Z));

        Assert.Equal(10.0, moved.Min.X, 1e-9);
        Assert.Equal(12.0, moved.Max.X, 1e-9);
    }

    // ALL EIGHT corners must be transformed. Under rotation the min corner does
    // not map to the new minimum, so using only min/max would silently shrink
    // the box for any link placed at an angle - and a shrunken box misses
    // clashes.
    [Fact]
    public void RotationUsesAllEightCorners_NotJustMinAndMax()
    {
        // 45 degrees about Z. A unit cube's footprint widens to sqrt(2).
        double c = Math.Cos(Math.PI / 4), s = Math.Sin(Math.PI / 4);
        Aabb rotated = Cube(0, 0, 0, 1).Transform(
            p => new Vec3(p.X * c - p.Y * s, p.X * s + p.Y * c, p.Z));

        double width = rotated.Max.X - rotated.Min.X;
        Assert.Equal(Math.Sqrt(2.0), width, 1e-9);
    }

    // --------------------------------------------------------- cell sizing

    // Median, not mean: one enormous element must not drag the cell size up
    // until the whole grid degenerates into a single bucket.
    [Fact]
    public void CellSize_UsesTheMedian_SoOneGiantBoxDoesNotDominate()
    {
        List<Aabb> boxes =
        [
            Cube(0, 0, 0, 2), Cube(0, 0, 0, 2), Cube(0, 0, 0, 2),
            Cube(0, 0, 0, 400),   // a site-boundary-sized outlier
        ];

        Assert.Equal(2.0, ClashGrid.ChooseCellSize(boxes), 1e-9);
    }

    [Fact]
    public void CellSize_IsClampedAtBothEnds()
    {
        Assert.Equal(ClashGrid.MinCellSizeFt,
            ClashGrid.ChooseCellSize([Cube(0, 0, 0, 0.01)]), 1e-9);

        Assert.Equal(ClashGrid.MaxCellSizeFt,
            ClashGrid.ChooseCellSize([Cube(0, 0, 0, 5000)]), 1e-9);
    }

    // Zero-size boxes contribute no dimension, so there is nothing to take a
    // median of - the fallback keeps the grid usable rather than dividing by 0.
    [Fact]
    public void CellSize_FallsBackWhenNoBoxHasAnyDimension()
    {
        Assert.Equal(ClashGrid.FallbackCellSizeFt,
            ClashGrid.ChooseCellSize([Cube(0, 0, 0, 0)]), 1e-9);

        Assert.Equal(ClashGrid.FallbackCellSizeFt, ClashGrid.ChooseCellSize([]), 1e-9);
    }

    // -------------------------------------------------------------- grid

    [Fact]
    public void AQueryFindsTheBoxItOverlaps()
    {
        List<Aabb> boxes = [Cube(0, 0, 0, 2), Cube(100, 100, 100, 2)];
        ClashGrid grid = ClashGrid.Build(boxes);

        Assert.Equal([0], grid.Overlapping(Cube(1, 1, 1, 1), boxes));
    }

    [Fact]
    public void AQueryFarAwayFindsNothing()
    {
        List<Aabb> boxes = [Cube(0, 0, 0, 2)];
        ClashGrid grid = ClashGrid.Build(boxes);

        Assert.Empty(grid.Overlapping(Cube(500, 500, 500, 2), boxes));
    }

    // A box spanning many cells must be found from any of them, exactly once.
    [Fact]
    public void ABoxSpanningManyCells_IsFoundOnceFromAnywhereAlongIt()
    {
        List<Aabb> boxes = [Box(0, 0, 0, 100, 1, 1)];   // a long duct
        ClashGrid grid = ClashGrid.Build(boxes);

        Assert.Equal([0], grid.Overlapping(Cube(0.5, 0.2, 0.2, 0.2), boxes));
        Assert.Equal([0], grid.Overlapping(Cube(60, 0.2, 0.2, 0.2), boxes));
        Assert.Equal([0], grid.Overlapping(Cube(99, 0.2, 0.2, 0.2), boxes));
    }

    // The grid is a PRE-filter: sharing a cell is not the same as overlapping,
    // and the caller still has to test the boxes.
    [Fact]
    public void SharingACell_IsNotByItselfAnOverlap()
    {
        // Two small boxes in opposite corners of one large cell.
        List<Aabb> boxes = [Cube(0, 0, 0, 0.1), Cube(0, 0, 0, 20)];
        ClashGrid grid = ClashGrid.Build(boxes);
        Aabb query = Cube(19.5, 19.5, 19.5, 0.1);

        Assert.Contains(1, grid.Candidates(query));
        Assert.DoesNotContain(0, grid.Overlapping(query, boxes));
    }

    // The broad phase must never LOSE a clash. Brute force is the oracle.
    [Fact]
    public void TheGridAgreesWithBruteForce_OverAScatteredSet()
    {
        Random rng = new(20260808);
        List<Aabb> boxes = [];
        for (int i = 0; i < 300; i++)
        {
            double x = rng.NextDouble() * 200, y = rng.NextDouble() * 200, z = rng.NextDouble() * 40;
            boxes.Add(Cube(x, y, z, 0.5 + rng.NextDouble() * 6));
        }

        ClashGrid grid = ClashGrid.Build(boxes);

        for (int q = 0; q < 60; q++)
        {
            Aabb query = Cube(
                rng.NextDouble() * 200, rng.NextDouble() * 200, rng.NextDouble() * 40,
                0.5 + rng.NextDouble() * 6);

            List<int> expected = [];
            for (int i = 0; i < boxes.Count; i++)
            {
                if (boxes[i].Overlaps(query))
                    expected.Add(i);
            }

            List<int> actual = grid.Overlapping(query, boxes);
            actual.Sort();

            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public void NegativeCoordinates_BucketCorrectly()
    {
        List<Aabb> boxes = [Cube(-50, -50, -50, 2)];
        ClashGrid grid = ClashGrid.Build(boxes);

        Assert.Equal([0], grid.Overlapping(Cube(-49.5, -49.5, -49.5, 1), boxes));
    }

    // ------------------------------------------------- oversized boxes
    //
    // A site boundary or topography surface against the 1 ft minimum cell would
    // shred into hundreds of millions of cells and hang the run while BUILDING
    // the grid. Such a box is held aside instead - which must not cost a single
    // clash, since the broad phase is only ever allowed to over-offer.

    /// <summary>A box far past the cap is never bucketed, so no cell explodes.</summary>
    [Fact]
    public void AnEnormousBox_IsHeldAsideRatherThanShredded()
    {
        // Small neighbours fix the median cell size at ~1 ft, then one box a
        // thousand feet across - the pathological case, in miniature.
        List<Aabb> boxes = [Cube(0, 0, 0, 1), Cube(3, 0, 0, 1), Cube(-500, -500, -500, 1000)];

        ClashGrid grid = ClashGrid.Build(boxes);

        Assert.Equal(1, grid.OversizedCount);

        // The two small ones are still bucketed normally.
        Assert.True(grid.CellCount > 0);
        Assert.True(grid.CellCount < ClashGrid.MaxCellsPerBox);
    }

    /// <summary>Being held aside must not lose the clash it was part of.</summary>
    [Fact]
    public void AnOversizedBox_IsStillFoundByAQueryInsideIt()
    {
        List<Aabb> boxes = [Cube(0, 0, 0, 1), Cube(3, 0, 0, 1), Cube(-500, -500, -500, 1000)];
        ClashGrid grid = ClashGrid.Build(boxes);

        // A query nowhere near the small boxes, but well inside the big one.
        List<int> hits = grid.Overlapping(Cube(200, 200, 200, 2), boxes);

        Assert.Equal([2], hits);
    }

    /// <summary>An oversized QUERY has nowhere to be held aside, so it offers all.</summary>
    [Fact]
    public void AnOversizedQuery_StillFindsEveryOverlap()
    {
        List<Aabb> boxes = [Cube(0, 0, 0, 1), Cube(3, 0, 0, 1), Cube(900, 900, 900, 1)];
        ClashGrid grid = ClashGrid.Build(boxes);

        List<int> hits = grid.Overlapping(Cube(-500, -500, -500, 1000), boxes);
        hits.Sort();

        // The third box sits outside the query, so it must NOT come back - the
        // fallback offers everything, it does not report everything.
        Assert.Equal([0, 1], hits);
    }

    /// <summary>The oracle again, with an oversized box mixed into the scatter.</summary>
    [Fact]
    public void TheGridAgreesWithBruteForce_EvenWithAnOversizedBox()
    {
        Random rng = new(20260816);
        List<Aabb> boxes = [];
        for (int i = 0; i < 300; i++)
        {
            double x = rng.NextDouble() * 200, y = rng.NextDouble() * 200, z = rng.NextDouble() * 40;
            boxes.Add(Cube(x, y, z, 0.5 + rng.NextDouble() * 6));
        }

        // The site slab: spans everything, one box.
        boxes.Add(Box(-100, -100, -5, 400, 400, -4));

        ClashGrid grid = ClashGrid.Build(boxes);
        Assert.Equal(1, grid.OversizedCount);

        for (int q = 0; q < 60; q++)
        {
            Aabb query = Cube(
                rng.NextDouble() * 200, rng.NextDouble() * 200, rng.NextDouble() * 40,
                0.5 + rng.NextDouble() * 6);

            List<int> expected = [];
            for (int i = 0; i < boxes.Count; i++)
            {
                if (boxes[i].Overlaps(query))
                    expected.Add(i);
            }

            List<int> actual = grid.Overlapping(query, boxes);
            actual.Sort();

            Assert.Equal(expected, actual);
        }
    }

    /// <summary>An ordinary model must not trip the cap at all.</summary>
    [Fact]
    public void OrdinaryBoxes_AreNeverHeldAside()
    {
        Random rng = new(20260816);
        List<Aabb> boxes = [];
        for (int i = 0; i < 300; i++)
        {
            boxes.Add(Cube(
                rng.NextDouble() * 200, rng.NextDouble() * 200, rng.NextDouble() * 40,
                0.5 + rng.NextDouble() * 6));
        }

        Assert.Equal(0, ClashGrid.Build(boxes).OversizedCount);
    }
}
