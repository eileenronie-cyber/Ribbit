using Autodesk.Revit.DB;
using Ribbit.Geometry;

namespace Ribbit.Native.Revit;

/// <summary>
/// The only place XYZ crosses into the Revit-free solver layer and back.
/// </summary>
internal static class GeometryInterop
{
    public static Vec3 ToVec3(this XYZ p) => new(p.X, p.Y, p.Z);

    public static XYZ ToXYZ(this Vec3 v) => new(v.X, v.Y, v.Z);
}
