using Autodesk.Revit.DB;

namespace Ribbit.Native.Revit;

/// <summary>
/// The single seam for Revit-version API breaks.
///
/// pyRevit resolved these at runtime (`hasattr` probes, e.g. the
/// ElementId.IntegerValue/.Value check at mepalignlib/common.py:10). C# cannot
/// do that across the .NET Framework 4.8 (Revit 2024) / .NET 8 (Revit 2025+)
/// boundary, so each probe becomes a compile-time branch. Keep every one of
/// them in this file — scattering #if through the engines is what makes a
/// multi-version port unmaintainable.
/// </summary>
internal static class RevitCompat
{
    /// <summary>
    /// Cheap hashable key for an ElementId. ElementId.IntegerValue is
    /// deprecated from Revit 2024 in favour of .Value (which widened to long).
    /// </summary>
    public static long IdKey(ElementId id)
    {
#if REVIT2023_OR_EARLIER
        return id.IntegerValue;
#else
        return id.Value;
#endif
    }

    /// <summary>
    /// The reverse of <see cref="IdKey"/> — an ElementId from a stored or
    /// transmitted number. The constructor took an int before Revit 2024 and
    /// takes a long from 2024 on.
    /// </summary>
    public static ElementId IdFrom(long value)
    {
#if REVIT2023_OR_EARLIER
        return new ElementId((int)value);
#else
        return new ElementId(value);
#endif
    }
}
