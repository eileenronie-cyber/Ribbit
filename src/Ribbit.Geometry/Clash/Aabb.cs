namespace Ribbit.Geometry.Clash;

/// <summary>
/// An axis-aligned bounding box in host feet. Replaces the Python's bare
/// six-tuple, which every consumer had to unpack positionally.
/// </summary>
public readonly record struct Aabb(Vec3 Min, Vec3 Max)
{
    public static Aabb FromPoints(IReadOnlyList<Vec3> points)
    {
        double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;

        foreach (Vec3 p in points)
        {
            minX = Math.Min(minX, p.X);
            minY = Math.Min(minY, p.Y);
            minZ = Math.Min(minZ, p.Z);
            maxX = Math.Max(maxX, p.X);
            maxY = Math.Max(maxY, p.Y);
            maxZ = Math.Max(maxZ, p.Z);
        }

        return new Aabb(new Vec3(minX, minY, minZ), new Vec3(maxX, maxY, maxZ));
    }

    /// <summary>The longest edge — what the grid's auto cell size is derived from.</summary>
    public double MaxDimension => Math.Max(
        Max.X - Min.X, Math.Max(Max.Y - Min.Y, Max.Z - Min.Z));

    /// <summary>Grown equally on every side. A pad of 0 returns this box unchanged.</summary>
    public Aabb Expand(double pad)
    {
        if (pad == 0.0)
            return this;

        return new Aabb(
            new Vec3(Min.X - pad, Min.Y - pad, Min.Z - pad),
            new Vec3(Max.X + pad, Max.Y + pad, Max.Z + pad));
    }

    /// <summary>
    /// Touching counts as overlapping — the comparisons are inclusive, matching
    /// the Python. Two boxes sharing a face are a clash candidate, which is what
    /// you want when the pad is what separates "near" from "touching".
    /// </summary>
    public bool Overlaps(Aabb other)
        => Min.X <= other.Max.X && Max.X >= other.Min.X
           && Min.Y <= other.Max.Y && Max.Y >= other.Min.Y
           && Min.Z <= other.Max.Z && Max.Z >= other.Min.Z;

    /// <summary>
    /// The shortest distance between this box and another, or 0 when they touch
    /// or overlap.
    ///
    /// Per-axis separations combined as a 3D diagonal, which is the true gap
    /// between two axis-aligned boxes — not the largest single-axis gap. Two
    /// boxes offset diagonally are further apart than any one axis says.
    ///
    /// THIS IS A BOX GAP, NOT A SURFACE GAP. A bounding box is bigger than the
    /// geometry inside it, so this UNDER-estimates the true distance between two
    /// elements: it can call things nearer than they are, never further. For a
    /// clearance check that is the safe direction to be wrong in.
    /// </summary>
    public double DistanceTo(Aabb other)
    {
        double dx = AxisGap(Min.X, Max.X, other.Min.X, other.Max.X);
        double dy = AxisGap(Min.Y, Max.Y, other.Min.Y, other.Max.Y);
        double dz = AxisGap(Min.Z, Max.Z, other.Min.Z, other.Max.Z);

        if (dx == 0.0 && dy == 0.0 && dz == 0.0)
            return 0.0;

        return Math.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
    }

    /// <summary>Separation of two intervals on one axis; 0 when they overlap.</summary>
    private static double AxisGap(double minA, double maxA, double minB, double maxB)
    {
        if (minA > maxB)
            return minA - maxB;

        if (minB > maxA)
            return minB - maxA;

        return 0.0;
    }

    /// <summary>
    /// This box mapped through a transform and re-fitted axis-aligned.
    ///
    /// All EIGHT corners are transformed, not just min and max: under rotation
    /// the min corner does not necessarily map to the new minimum, and using two
    /// corners would silently shrink the box for any linked model placed at an
    /// angle.
    /// </summary>
    public Aabb Transform(Func<Vec3, Vec3> transform)
    {
        Vec3[] corners =
        [
            new(Min.X, Min.Y, Min.Z), new(Min.X, Min.Y, Max.Z),
            new(Min.X, Max.Y, Min.Z), new(Min.X, Max.Y, Max.Z),
            new(Max.X, Min.Y, Min.Z), new(Max.X, Min.Y, Max.Z),
            new(Max.X, Max.Y, Min.Z), new(Max.X, Max.Y, Max.Z),
        ];

        Vec3[] mapped = new Vec3[corners.Length];
        for (int i = 0; i < corners.Length; i++)
            mapped[i] = transform(corners[i]);

        return FromPoints(mapped);
    }
}
