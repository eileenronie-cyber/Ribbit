namespace Ribbit.Geometry;

/// <summary>
/// Revit-free stand-in for Autodesk.Revit.DB.XYZ.
///
/// The solver layer is deliberately built on this rather than on XYZ so it
/// can be exercised without a Revit process, exactly the way the pyRevit
/// tests stubbed XYZ out with MockXYZ. Member names deliberately track the
/// XYZ ones so the adapter at the Revit boundary stays a straight mapping
/// and the port stays reviewable against the Python source.
/// </summary>
public readonly struct Vec3
{
    public double X { get; }
    public double Y { get; }
    public double Z { get; }

    public Vec3(double x, double y, double z)
    {
        X = x;
        Y = y;
        Z = z;
    }

    public static readonly Vec3 Zero = new(0.0, 0.0, 0.0);
    public static readonly Vec3 BasisX = new(1.0, 0.0, 0.0);
    public static readonly Vec3 BasisY = new(0.0, 1.0, 0.0);
    public static readonly Vec3 BasisZ = new(0.0, 0.0, 1.0);

    public double DotProduct(Vec3 o) => X * o.X + Y * o.Y + Z * o.Z;

    public Vec3 CrossProduct(Vec3 o) => new(
        Y * o.Z - Z * o.Y,
        Z * o.X - X * o.Z,
        X * o.Y - Y * o.X);

    public double GetLength() => Math.Sqrt(DotProduct(this));

    public Vec3 Normalize()
    {
        double length = GetLength();
        return new Vec3(X / length, Y / length, Z / length);
    }

    public Vec3 Add(Vec3 o) => new(X + o.X, Y + o.Y, Z + o.Z);

    public Vec3 Subtract(Vec3 o) => new(X - o.X, Y - o.Y, Z - o.Z);

    public Vec3 Multiply(double s) => new(X * s, Y * s, Z * s);

    public Vec3 Negate() => new(-X, -Y, -Z);

    public double DistanceTo(Vec3 o) => Subtract(o).GetLength();

    public double AngleTo(Vec3 o)
    {
        Vec3 a = Normalize();
        Vec3 b = o.Normalize();
        // Clamp before acos: dotting two normalized vectors can land a hair
        // outside [-1, 1] on rounding, which would hand acos a NaN.
        double d = Math.Max(-1.0, Math.Min(1.0, a.DotProduct(b)));
        return Math.Acos(d);
    }

    /// <summary>
    /// Rotate this vector about <paramref name="axis"/> by
    /// <paramref name="angleRad"/>, right-hand rule — the Revit-free stand-in
    /// for Transform.CreateRotation(axis, angle).OfVector(v).
    /// Rodrigues' rotation formula; the axis is normalized here so callers
    /// need not pre-normalize.
    /// </summary>
    public Vec3 Rotate(Vec3 axis, double angleRad)
    {
        Vec3 k = axis.Normalize();
        double cos = Math.Cos(angleRad);
        double sin = Math.Sin(angleRad);

        return Multiply(cos)
            .Add(k.CrossProduct(this).Multiply(sin))
            .Add(k.Multiply(k.DotProduct(this) * (1.0 - cos)));
    }

    public override string ToString() => $"Vec3({X:F6}, {Y:F6}, {Z:F6})";
}
