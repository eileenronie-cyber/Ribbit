using Autodesk.Revit.DB;

namespace Ribbit.Native.Revit;

internal static class ElementGeometry
{
    /// <summary>
    /// The element's LocationCurve as a Line, or null if it hasn't got one (or
    /// is curved/arc, which none of these tools handle).
    /// </summary>
    public static Line? GetLine(Element elem)
        => (elem.Location as LocationCurve)?.Curve as Line;

    /// <summary>
    /// A point near the element, used to anchor a temporary pick plane. The
    /// exact position doesn't affect the on-screen math (depth is discarded
    /// anyway) — it just keeps the plane in the right neighbourhood.
    /// </summary>
    public static XYZ? GetReferencePoint(Element elem)
    {
        Line? line = GetLine(elem);
        if (line is not null)
            return line.Evaluate(0.5, true);

        return (elem.Location as LocationPoint)?.Point;
    }
}
