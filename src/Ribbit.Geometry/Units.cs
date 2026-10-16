namespace Ribbit.Geometry;

/// <summary>
/// The solvers compute in Revit internal decimal feet throughout; mm is a
/// display concern only, so it lives here in one place rather than being
/// threaded through the geometry.
/// </summary>
public static class Units
{
    public const double MmPerFt = 304.8;

    /// <summary>Internal feet -> "1234 mm" for messages and reports.</summary>
    public static string Mm(double? valueFt)
    {
        if (valueFt is null)
            return "-";

        double mm = valueFt.Value * MmPerFt;
        // A station a hair below zero rounds to "-0 mm", which reads as a
        // negative measurement rather than the origin it actually is.
        return Math.Abs(mm) < 0.5 ? "0 mm" : $"{mm:F0} mm";
    }
}
