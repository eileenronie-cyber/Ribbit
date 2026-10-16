namespace Ribbit.Geometry.Grids;

/// <summary>
/// A crop (or scope box) rectangle in VIEW coordinates: X across, Y up.
/// </summary>
public readonly record struct ViewRect(double MinX, double MinY, double MaxX, double MaxY)
{
    public double MidX => 0.5 * (MinX + MaxX);
    public double MidY => 0.5 * (MinY + MaxY);

    /// <summary>The four corners, in the order the Dynamo script read them.</summary>
    public (double X, double Y)[] Corners() =>
    [
        (MinX, MinY), (MinX, MaxY), (MaxX, MinY), (MaxX, MaxY),
    ];
}

/// <summary>Offsets past the rectangle, per side, in feet. Negative pulls inside.</summary>
public readonly record struct SideOffsets(double Left, double Right, double Top, double Bottom);

/// <summary>
/// The pure half of Adjust Grids, ported from the Grid_v2.dyn Python node
/// "Adjust grids to the crop box". No Revit in here: every function takes
/// coordinates the Revit side has already transformed, so each can be tested
/// on its own.
/// </summary>
public static class GridMath
{
    /// <summary>Feet; the "is this corner on the line" tolerance.</summary>
    public const double Tol = 1e-7;

    /// <summary>Feet; Revit refuses a line shorter than this.</summary>
    public const double MinLength = 1e-3;

    /// <summary>
    /// Does the INFINITE line through a and b (view coordinates) cut the
    /// rectangle? True when the rectangle's corners are not all strictly on
    /// one side of it, or one lies on it.
    /// </summary>
    public static bool CrossesRectangle(
        double ax, double ay, double bx, double by, ViewRect rect)
    {
        double dx = bx - ax;
        double dy = by - ay;
        if (Math.Abs(dx) < Tol && Math.Abs(dy) < Tol)
            return false;

        bool above = false, below = false;
        foreach ((double cx, double cy) in rect.Corners())
        {
            double side = dx * (cy - ay) - dy * (cx - ax);
            if (side > Tol)
                above = true;
            else if (side < -Tol)
                below = true;
            else
                return true;

            if (above && below)
                return true;
        }

        return false;
    }

    /// <summary>
    /// A grid is horizontal in a view when its view-X run is at least its
    /// view-Y run. Ties (45 degrees) count as horizontal, as in the script.
    /// </summary>
    public static bool IsHorizontal(double ax, double ay, double bx, double by)
        => Math.Abs(bx - ax) >= Math.Abs(by - ay);

    /// <summary>
    /// Should the bubble go on End0? a and b are the MODEL curve's endpoint 0
    /// and 1 in view coordinates. Measured per grid, because a grid drawn
    /// right-to-left has its End0 on the right.
    /// </summary>
    public static bool BubbleOnEnd0(
        double ax, double ay, double bx, double by,
        bool horizontal, bool wantLeft, bool wantTop)
    {
        if (horizontal)
            return wantLeft ? ax <= bx : ax >= bx;

        return wantTop ? ay >= by : ay <= by;
    }

    /// <summary>
    /// Which offset applies to each end of a line running v1 -> v2 (view
    /// coordinates). (offset below the smallest projection, offset past the
    /// largest). The line keeps the DATUM's direction, so which physical side
    /// "low" is depends on where that direction points in the view.
    /// </summary>
    public static (double Low, double High) EndOffsets(
        double v1x, double v1y, double v2x, double v2y, bool horizontal, SideOffsets o)
    {
        if (horizontal)
        {
            bool forwards = v2x >= v1x;     // p1 -> p2 points view-right
            return forwards ? (o.Left, o.Right) : (o.Right, o.Left);
        }

        bool up = v2y >= v1y;               // p1 -> p2 points view-up
        return up ? (o.Bottom, o.Top) : (o.Top, o.Bottom);
    }

    /// <summary>
    /// The parameter range, along a unit direction from origin p1, that spans
    /// every projection plus the two offsets - or null when the offsets
    /// collapse it below <see cref="MinLength"/>.
    /// </summary>
    public static (double Low, double High)? StretchRange(
        IEnumerable<double> projections, double lowOffset, double highOffset)
    {
        double min = double.PositiveInfinity, max = double.NegativeInfinity;
        foreach (double p in projections)
        {
            if (p < min) min = p;
            if (p > max) max = p;
        }

        if (double.IsInfinity(min))
            return null;

        double low = min - lowOffset;
        double high = max + highOffset;
        return high - low < MinLength ? null : (low, high);
    }

    /// <summary>
    /// The segment a -> b (view coordinates) cut to the rectangle, as the
    /// parameters (low, high) along it, 0 = a and 1 = b - or null when it
    /// misses the rectangle. An end already inside keeps its own parameter.
    /// V23's clip_to_crop: a 3D grid end past the crop box is DRAWN at the
    /// crop edge, and that is where it must be converted to 2D.
    /// </summary>
    public static (double Low, double High)? ClipToRect(
        double ax, double ay, double bx, double by, ViewRect rect)
    {
        double dx = bx - ax, dy = by - ay;
        double low = 0.0, high = 1.0;
        foreach ((double d, double start, double lo, double hi) in new[]
                 {
                     (dx, ax, rect.MinX, rect.MaxX),
                     (dy, ay, rect.MinY, rect.MaxY),
                 })
        {
            if (Math.Abs(d) < Tol)
            {
                if (start < lo - Tol || start > hi + Tol)
                    return null;
                continue;
            }

            double t0 = (lo - start) / d, t1 = (hi - start) / d;
            if (t0 > t1)
                (t0, t1) = (t1, t0);
            low = Math.Max(low, t0);
            high = Math.Min(high, t1);
        }

        return high - low < Tol ? null : (low, high);
    }

    /// <summary>
    /// How far each position must move to sit <paramref name="gap"/> from its
    /// neighbours: {index: shift}, only for the ones that move. <paramref
    /// name="fixedIndices"/> never move - bubbles that already carry a leader -
    /// and the others are spaced AROUND them, never from them as if they had
    /// moved.
    /// </summary>
    /// <param name="tolerance">
    /// How far under <paramref name="gap"/> still counts as spaced. Pass the
    /// precision the positions were READ BACK at: a bubble this code put exactly
    /// <paramref name="gap"/> away comes back from Revit a fraction of a
    /// millimetre off, and with a tighter tolerance the NEXT run reads that as a
    /// clash and elbows its neighbour for nothing (seen in the gridtest harness,
    /// intermittently, depending on Revit's rounding).
    /// </param>
    public static Dictionary<int, double> SpacingShifts(
        IReadOnlyList<double> positions, double gap, IEnumerable<int>? fixedIndices = null,
        double tolerance = Tol)
    {
        HashSet<int> fixedSet = fixedIndices is null ? [] : [.. fixedIndices];
        List<double> walls = [.. fixedSet.Select(i => positions[i])];

        List<double> Clashes(double p) => [.. walls.Where(w => Math.Abs(p - w) < gap - tolerance)];

        Dictionary<int, double> shifts = [];
        double? placed = null;

        // OrderBy is stable, matching Python's sorted() on ties.
        foreach (int i in Enumerable.Range(0, positions.Count).OrderBy(n => positions[n]))
        {
            if (fixedSet.Contains(i))
                continue;

            double? low = placed + gap;
            // within `tolerance` of the slot counts as in it: no hair's-breadth shift
            double target = low is null || positions[i] >= low.Value - tolerance ? positions[i] : low.Value;
            List<double> hit = Clashes(target);
            if (hit.Count > 0)
            {
                // the slot just before the fixed bubble, if it is free...
                double back = hit.Min() - gap;
                if ((low is null || back >= low.Value - tolerance) && Clashes(back).Count == 0)
                {
                    target = back;
                }
                else
                {
                    // ...otherwise hop past it
                    while (hit.Count > 0)
                    {
                        target = hit.Max() + gap;
                        hit = Clashes(target);
                    }
                }
            }

            placed = target;
            if (Math.Abs(target - positions[i]) > tolerance)
                shifts[i] = target - positions[i];
        }

        return shifts;
    }

    /// <summary>
    /// A textbox answer in millimetres. Empty is 0 ("no offset on that side"
    /// is a real answer); a comma is read as a decimal point; anything else
    /// that is not a number is null, so it can be reported rather than
    /// quietly becoming 0.
    ///
    /// A comma that reads as a THOUSANDS separator is refused, not guessed at:
    /// "3,000" meant as 3000 would otherwise become 3 mm and pass every check.
    /// So a comma with a point also present ("3,000.5"), or a comma followed
    /// by exactly three digits ("3,000", "12,500"), is null - reported as not
    /// a number. "12,5" and "1,25" are still decimals.
    /// </summary>
    public static double? ParseMm(string? text)
    {
        string t = (text ?? "").Trim();
        if (t.Length == 0)
            return 0.0;

        int comma = t.IndexOf(',');
        if (comma >= 0)
        {
            if (t.IndexOf('.') >= 0 || t.IndexOf(',', comma + 1) >= 0)
                return null;
            string after = t.Substring(comma + 1);
            if (after.Length == 3 && after.All(char.IsDigit))
                return null;
            t = t.Replace(',', '.');
        }

        return double.TryParse(t, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out double v)
            && !double.IsNaN(v) && !double.IsInfinity(v)
            ? v
            : null;
    }

    /// <summary>3000.0 back into a box as "3000" - nobody typed the .0.</summary>
    public static string PrettyMm(double value)
        => value == Math.Floor(value) && Math.Abs(value) < 1e15
            ? ((long)value).ToString(System.Globalization.CultureInfo.InvariantCulture)
            : value.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
}
