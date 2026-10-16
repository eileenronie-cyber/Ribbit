namespace Ribbit.Geometry;

/// <summary>
/// Pure geometry behind Align Branch To Reference. Ported from
/// lib/mepalignlib/alignbranch.py — translation only, never rotation.
/// </summary>
public static class AlignBranchMath
{
    /// <summary>Feet — below this, treat points/lengths as coincident.</summary>
    public const double LenEps = 1.0e-6;

    /// <summary>
    /// sin² of the angle below which two runs count as parallel. ~2 degrees.
    ///
    /// TWO DEGREES IS CHOSEN, not arbitrary: modelled-parallel runs are parallel
    /// to within rounding, while the shallowest deliberate branch angle anyone
    /// draws is 11.25 degrees. Anything in between separates "meant to be
    /// parallel" from "meant to be angled" with room on both sides.
    ///
    /// This replaced an ABSOLUTE test, <c>Math.Abs(denom) &lt; 1e-9</c>, on a
    /// quantity with units of length⁴ — so it meant different angles for a
    /// 6-inch fitting and a 100-foot main, and in practice never fired at all.
    /// Measured before the change: two 100ft runs 5ft apart and 0.5 degrees off
    /// parallel produced |delta| = 0, i.e. "already aligned", while sitting 5ft
    /// apart. Near-parallel lines DO intersect — thousands of feet away — so the
    /// closest-point answer is arithmetically right and useless.
    /// </summary>
    public const double ParallelSinSquared = 0.0012;

    /// <summary>
    /// Parameters (s, t) of the closest points on two INFINITE lines
    /// b0 + s*db (branch) and m0 + t*dm (reference). When there is no unique
    /// closest pair — parallel lines, or a degenerate direction — the branch
    /// stays put along its own length and its start is projected onto the
    /// reference instead, which removes only the perpendicular offset.
    /// </summary>
    public static (double S, double T) LineParamsClosest(Vec3 b0, Vec3 db, Vec3 m0, Vec3 dm)
    {
        double a = db.DotProduct(db);
        double c = dm.DotProduct(dm);
        Vec3 w0 = b0.Subtract(m0);
        double d = db.DotProduct(w0);
        double e = dm.DotProduct(w0);

        if (a < 1e-12 && c < 1e-12)
            return (0.0, 0.0);
        if (a < 1e-12)
            return (0.0, e / c);
        if (c < 1e-12)
            return (-d / a, 0.0);

        double b = db.DotProduct(dm);
        double denom = a * c - b * b;

        // denom is |db|²|dm|²sin²θ, so denom / (a*c) IS sin²θ — a pure number,
        // independent of how long either run is. Comparing denom against a
        // fixed number instead compares an area-squared against a constant,
        // which is why the old test drifted with model scale.
        if (denom <= ParallelSinSquared * a * c)
            return (0.0, e / c);

        double s = (b * e - c * d) / denom;
        double t = (a * e - b * d) / denom;
        return (s, t);
    }

    /// <summary>
    /// Full-3D rigid translation vector for the branch. Never rotates.
    /// Takes raw endpoints rather than a Line so it stays Revit-free.
    /// </summary>
    public static Vec3 ComputeDelta(Vec3 branchStart, Vec3 branchEnd, Vec3 refStart, Vec3 refEnd)
    {
        Vec3 db = branchEnd.Subtract(branchStart);
        Vec3 dm = refEnd.Subtract(refStart);

        (double s, double t) = LineParamsClosest(branchStart, db, refStart, dm);
        return refStart.Add(dm.Multiply(t)).Subtract(branchStart.Add(db.Multiply(s)));
    }
}
