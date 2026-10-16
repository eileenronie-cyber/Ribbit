using System.Globalization;

namespace Ribbit.Geometry.Clash;

/// <summary>
/// Parses a clearance/pad value into Revit internal feet. Ported from
/// <c>to_feet</c> in lib/clashlib/geometry.py.
///
/// Accepts a bare number (already feet) or a suffixed string — "25mm", "2.5cm",
/// "1m", "6\"", "6in", "2'", "2ft". Null on anything unparseable, which callers
/// treat as "no pad" rather than guessing.
/// </summary>
public static class UnitParse
{
    public static double? ToFeet(string? value)
    {
        if (value is null)
            return null;

        string s = value.Trim().ToLowerInvariant().Replace(" ", "");
        if (s.Length == 0)
            return null;

        // Order matters: "mm" and "cm" must be tested before the bare "m", and
        // "in" before "ft"/"'" so "6in" is not mistaken for a plain number.
        if (TrySuffix(s, "mm", 1.0 / 304.8, out double mm))
            return mm;
        if (TrySuffix(s, "cm", 1.0 / 30.48, out double cm))
            return cm;
        if (TrySuffix(s, "m", 1.0 / 0.3048, out double m))
            return m;
        if (TrySuffix(s, "in", 1.0 / 12.0, out double inches))
            return inches;
        if (TrySuffix(s, "\"", 1.0 / 12.0, out double quoted))
            return quoted;
        if (TrySuffix(s, "ft", 1.0, out double ft))
            return ft;
        if (TrySuffix(s, "'", 1.0, out double feet))
            return feet;

        return Parse(s);
    }

    /// <summary>Convenience for a value that is already numeric.</summary>
    public static double? ToFeet(double? value) => value;

    private static bool TrySuffix(string s, string suffix, double factor, out double result)
    {
        result = 0.0;
        if (!s.EndsWith(suffix, StringComparison.Ordinal))
            return false;

        double? parsed = Parse(s.Substring(0, s.Length - suffix.Length));
        if (parsed is null)
            return false;

        result = parsed.Value * factor;
        return true;
    }

    /// <summary>
    /// Invariant culture throughout: a workbook or a settings file written on a
    /// comma-decimal machine must not silently read "2,5" as 25.
    /// </summary>
    private static double? Parse(string s)
        => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)
            ? v
            : null;
}
