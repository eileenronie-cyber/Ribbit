namespace Ribbit.Geometry.Clash;

/// <summary>
/// The broad-phase spatial hash. Ported from the grid stage of
/// lib/clashlib/clash_engine.py.
///
/// Set A's padded boxes are bucketed into uniform cubic cells once; each Set B
/// box then only has to be tested against the A boxes sharing a cell, rather
/// than against all of them. That is what turns an O(A x B) sweep into something
/// that finishes on a real model.
///
/// Deliberately Revit-free: it stores INDICES into whatever list the caller
/// holds, so the same grid works for host elements, linked instances, or a test.
/// </summary>
public sealed class ClashGrid
{
    /// <summary>
    /// Cell size is clamped to this range in feet. Too small and a long duct
    /// spans thousands of cells; too large and every box lands in one bucket and
    /// the grid stops filtering anything.
    /// </summary>
    public const double MinCellSizeFt = 1.0;
    public const double MaxCellSizeFt = 50.0;

    /// <summary>Used when no box has a usable dimension at all.</summary>
    public const double FallbackCellSizeFt = 10.0;

    /// <summary>
    /// The most cells ONE box may be shredded into before it is held aside
    /// instead.
    ///
    /// The clamp above bounds the CELL size, not the BOX. A site boundary or a
    /// topography surface a thousand feet across, against the 1 ft minimum cell,
    /// is hundreds of millions of cells — enough to hang the run while building
    /// the grid, long before any clash is tested. Nothing in the real model comes
    /// close (worst case measured 2026-08-16: 736 cells), so this guards against
    /// the pathological element rather than being a tuning knob.
    ///
    /// A box over the cap goes in <see cref="_oversized"/> and is offered as a
    /// candidate for EVERY query. That is exact, not approximate: the broad phase
    /// is only ever allowed to over-offer, because the caller re-tests the real
    /// boxes afterwards.
    /// </summary>
    public const long MaxCellsPerBox = 4096;

    private readonly Dictionary<(int X, int Y, int Z), List<int>> _cells = [];

    /// <summary>Boxes too large to bucket. Always offered, never binned.</summary>
    private readonly List<int> _oversized = [];

    private readonly double _cellSize;
    private readonly double _invCellSize;
    private int _count;

    public double CellSizeFt => _cellSize;
    public int CellCount => _cells.Count;

    /// <summary>How many boxes were too large to bucket — 0 on a sane model.</summary>
    public int OversizedCount => _oversized.Count;

    private ClashGrid(double cellSize)
    {
        _cellSize = cellSize;
        _invCellSize = 1.0 / cellSize;
    }

    /// <summary>
    /// Cell size from the MEDIAN longest-edge of the boxes, clamped.
    ///
    /// Median rather than mean: one enormous element — a site boundary, a
    /// building-length duct — would drag a mean up until the grid degenerated
    /// into a single bucket. The median ignores it.
    /// </summary>
    public static double ChooseCellSize(IEnumerable<Aabb> boxes)
    {
        List<double> dims = [];
        foreach (Aabb b in boxes)
        {
            double d = b.MaxDimension;
            if (d > 0)
                dims.Add(d);
        }

        if (dims.Count == 0)
            return FallbackCellSizeFt;

        dims.Sort();
        double median = dims[dims.Count / 2];
        return Math.Max(MinCellSizeFt, Math.Min(median, MaxCellSizeFt));
    }

    public static ClashGrid Build(IReadOnlyList<Aabb> boxes)
    {
        ClashGrid grid = new(ChooseCellSize(boxes));
        grid._count = boxes.Count;

        for (int i = 0; i < boxes.Count; i++)
            grid.Insert(i, boxes[i]);

        return grid;
    }

    private void Insert(int index, Aabb box)
    {
        if (CellSpan(box) > MaxCellsPerBox)
        {
            _oversized.Add(index);
            return;
        }

        foreach ((int x, int y, int z) in CellsFor(box))
        {
            if (!_cells.TryGetValue((x, y, z), out List<int>? bucket))
                _cells[(x, y, z)] = bucket = [];
            bucket.Add(index);
        }
    }

    /// <summary>
    /// How many cells a box would occupy, counted in <c>long</c> before anything
    /// is allocated — the whole point is to answer this WITHOUT walking them.
    /// </summary>
    private long CellSpan(Aabb box)
    {
        long nx = CellIndex(box.Max.X) - CellIndex(box.Min.X) + 1L;
        long ny = CellIndex(box.Max.Y) - CellIndex(box.Min.Y) + 1L;
        long nz = CellIndex(box.Max.Z) - CellIndex(box.Min.Z) + 1L;

        // A non-finite or absurd coordinate makes the indices meaningless; treat
        // it as oversized so it is offered everywhere rather than binned
        // somewhere arbitrary. Bailing on any SINGLE axis past the cap also
        // keeps the multiply below from overflowing, which it otherwise would
        // long before the product could be compared.
        if (nx <= 0 || ny <= 0 || nz <= 0
            || nx > MaxCellsPerBox || ny > MaxCellsPerBox || nz > MaxCellsPerBox)
        {
            return long.MaxValue;
        }

        return nx * ny * nz;
    }

    private IEnumerable<(int X, int Y, int Z)> CellsFor(Aabb box)
    {
        // Inclusive on both ends: a box ending exactly on a cell boundary still
        // occupies the cell it touches.
        int x0 = CellIndex(box.Min.X), x1 = CellIndex(box.Max.X);
        int y0 = CellIndex(box.Min.Y), y1 = CellIndex(box.Max.Y);
        int z0 = CellIndex(box.Min.Z), z1 = CellIndex(box.Max.Z);

        for (int x = x0; x <= x1; x++)
        {
            for (int y = y0; y <= y1; y++)
            {
                for (int z = z0; z <= z1; z++)
                    yield return (x, y, z);
            }
        }
    }

    private int CellIndex(double v) => (int)Math.Floor(v * _invCellSize);

    /// <summary>
    /// Indices of every stored box that could overlap <paramref name="query"/>,
    /// deduplicated and in insertion order. A candidate here has only shared a
    /// CELL — the caller still has to test the boxes themselves.
    /// </summary>
    public List<int> Candidates(Aabb query)
    {
        HashSet<int> seen = [];
        List<int> result = [];

        // A query box past the cap would shred exactly as a stored one does, and
        // it is not stored anywhere to be held aside — so it falls back to
        // offering everything. Still exact, and still bounded: this is O(count)
        // once, against O(cells) that has no ceiling.
        if (CellSpan(query) > MaxCellsPerBox)
        {
            for (int i = 0; i < _count; i++)
                result.Add(i);

            return result;
        }

        foreach ((int x, int y, int z) in CellsFor(query))
        {
            if (!_cells.TryGetValue((x, y, z), out List<int>? bucket))
                continue;

            foreach (int i in bucket)
            {
                if (seen.Add(i))
                    result.Add(i);
            }
        }

        // The boxes that were never bucketed. Appended rather than merged in
        // cell order, because a candidate list only promises to be deduplicated,
        // not sorted.
        foreach (int i in _oversized)
        {
            if (seen.Add(i))
                result.Add(i);
        }

        return result;
    }

    /// <summary>
    /// Candidates that also genuinely overlap the query box — the full broad
    /// phase for one query.
    /// </summary>
    public List<int> Overlapping(Aabb query, IReadOnlyList<Aabb> boxes)
    {
        List<int> hits = [];
        foreach (int i in Candidates(query))
        {
            if (boxes[i].Overlaps(query))
                hits.Add(i);
        }
        return hits;
    }
}
