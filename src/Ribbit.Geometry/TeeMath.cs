namespace Ribbit.Geometry;

/// <summary>
/// Pure geometry behind Tee Into Main. Ported from
/// _Ribbit.tab/PH_FP Tools.panel/TeeIntoMain.pushbutton/script.py.
/// </summary>
public static class TeeMath
{
    /// <summary>
    /// On sin^2(angle between axes) — below this the two runs are parallel and
    /// there is no unique point to tee into.
    /// </summary>
    public const double ParallelEps = 1.0e-6;

    public static double PointToAxisDistance(Vec3 point, Vec3 origin, Vec3 direction)
    {
        Vec3 v = point.Subtract(origin);
        return v.Subtract(direction.Multiply(v.DotProduct(direction))).GetLength();
    }

    /// <summary>
    /// Closest points on the two INFINITE lines p1 + s*d1 and p2 + t*d2
    /// (d1 and d2 must be unit vectors).
    ///
    /// The vector between them is the common perpendicular: adding it to line 1
    /// slides that line onto line 2 WITHOUT rotating it, which is how the
    /// branch's angle is preserved. For a vertical branch that vector is
    /// horizontal, so the branch's elevation is untouched.
    /// </summary>
    public static (Vec3 OnLine1, Vec3 OnLine2, bool IsParallel) AxisClosestPoints(
        Vec3 p1, Vec3 d1, Vec3 p2, Vec3 d2)
    {
        double b = d1.DotProduct(d2);
        double denom = 1.0 - b * b;          // = sin^2 of the angle between the two axes
        if (Math.Abs(denom) < ParallelEps)
            return (p1, p2, true);

        Vec3 w0 = p1.Subtract(p2);
        double d = d1.DotProduct(w0);
        double e = d2.DotProduct(w0);
        double s = (b * e - d) / denom;
        double t = (e - b * d) / denom;

        return (p1.Add(d1.Multiply(s)), p2.Add(d2.Multiply(t)), false);
    }
}
