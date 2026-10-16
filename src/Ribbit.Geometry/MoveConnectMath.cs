namespace Ribbit.Geometry;

/// <summary>
/// Pure geometry behind Move / Align / Connect. Ported from
/// lib/mepalignlib/moveconnect.py.
///
/// The connector-pair helpers are generic over the connector type with an
/// origin selector, so this assembly never has to know about
/// Autodesk.Revit.DB.Connector — the Revit layer passes real connectors,
/// the tests pass fakes, and both get the same code path.
/// </summary>
public static class MoveConnectMath
{
    public const double AngleEps = 1.0e-9;
    public const double LenEps = 1.0e-9;

    public static Vec3 ArbitraryPerpendicular(Vec3 v)
    {
        Vec3 reference = Math.Abs(v.DotProduct(Vec3.BasisX)) < 0.9 ? Vec3.BasisX : Vec3.BasisY;
        return v.CrossProduct(reference).Normalize();
    }

    /// <summary>
    /// Axis/angle to rotate <paramref name="vFrom"/> onto <paramref name="vTo"/>.
    /// Handles parallel/anti-parallel. A null axis means "already aligned".
    /// </summary>
    public static (Vec3? Axis, double Angle) RotationAxisAngle(Vec3 vFrom, Vec3 vTo)
    {
        Vec3 cross = vFrom.CrossProduct(vTo);
        if (cross.GetLength() < LenEps)
        {
            if (vFrom.DotProduct(vTo) > 0)
                return (null, 0.0);                                  // already aligned
            return (ArbitraryPerpendicular(vFrom), Math.PI);         // exactly opposite
        }
        return (cross.Normalize(), vFrom.AngleTo(vTo));
    }

    /// <summary>Signed angle (radians) to rotate bxFrom onto bxTo about axis.</summary>
    public static double SignedRollAngle(Vec3 bxFrom, Vec3 bxTo, Vec3 axis)
    {
        Vec3 cross = bxFrom.CrossProduct(bxTo);
        return Math.Atan2(cross.DotProduct(axis), bxFrom.DotProduct(bxTo));
    }

    /// <summary>
    /// Closest open-connector pair by origin distance. Null when either side is
    /// empty, or when <paramref name="canPair"/> rejects every combination.
    /// </summary>
    /// <param name="canPair">
    /// Optional (mover, target) compatibility test — the Revit layer uses it to
    /// require matching domains. Kept as a predicate so this assembly still
    /// knows nothing about Autodesk.Revit.DB.Domain.
    /// </param>
    public static (T Mover, T Target)? PickNearestPair<T>(
        IReadOnlyList<T> moveConns,
        IReadOnlyList<T> targetConns,
        Func<T, Vec3> origin,
        Func<T, T, bool>? canPair = null)
    {
        (T, T)? bestPair = null;
        double? bestDist = null;

        foreach (T mc in moveConns)
        {
            foreach (T tc in targetConns)
            {
                if (canPair is not null && !canPair(mc, tc))
                    continue;

                double d = origin(mc).DistanceTo(origin(tc));
                if (bestDist is null || d < bestDist)
                {
                    bestDist = d;
                    bestPair = (mc, tc);
                }
            }
        }
        return bestPair;
    }

    /// <summary>
    /// On the FIXED target, the open connector nearest the CLICK POINT wins —
    /// the user clicks near the end they want joined. The LOOSE element then
    /// joins with whichever of its own open connectors is nearest that chosen
    /// end; its own click point is deliberately ignored, because on a small
    /// fitting the connectors are only inches apart and a body-click would
    /// pick an end at random. With no click point, falls back to the closest
    /// open pair.
    ///
    /// ONLY VIABLE TARGET ENDS ARE CONSIDERED. Mechanical equipment routinely
    /// carries connectors in several domains at once — a real model here had
    /// pumps exposing an electrical connector and a piping connector inches
    /// apart. Picking the nearest end blind and rejecting the mismatch
    /// afterwards made the piping connection UNREACHABLE on those: no click
    /// position could get past the electrical connector that happened to sit
    /// closer. Compatibility has to filter the candidates, not veto the answer.
    /// </summary>
    /// <param name="canPair">
    /// Optional (mover, target) compatibility test — see
    /// <see cref="PickNearestPair"/>.
    /// </param>
    public static (T Mover, T Target)? ChoosePair<T>(
        IReadOnlyList<T> moverOpen,
        IReadOnlyList<T> targetOpen,
        Vec3? targetClick,
        Func<T, Vec3> origin,
        Func<T, T, bool>? canPair = null)
    {
        if (targetClick is null)
            return PickNearestPair(moverOpen, targetOpen, origin, canPair);

        if (moverOpen.Count == 0 || targetOpen.Count == 0)
            return null;

        // Nearest target end that SOME mover connector could actually join.
        int targetIndex = -1;
        double bestTarget = 0.0;
        for (int i = 0; i < targetOpen.Count; i++)
        {
            if (canPair is not null && !AnyCanPair(moverOpen, targetOpen[i], canPair))
                continue;

            double d = origin(targetOpen[i]).DistanceTo(targetClick.Value);
            if (targetIndex < 0 || d < bestTarget)
            {
                bestTarget = d;
                targetIndex = i;
            }
        }

        if (targetIndex < 0)
            return null;

        T tc = targetOpen[targetIndex];

        int moverIndex = -1;
        double bestMover = 0.0;
        for (int i = 0; i < moverOpen.Count; i++)
        {
            if (canPair is not null && !canPair(moverOpen[i], tc))
                continue;

            double d = origin(moverOpen[i]).DistanceTo(origin(tc));
            if (moverIndex < 0 || d < bestMover)
            {
                bestMover = d;
                moverIndex = i;
            }
        }

        return moverIndex < 0 ? null : (moverOpen[moverIndex], tc);
    }

    private static bool AnyCanPair<T>(
        IReadOnlyList<T> moverOpen, T target, Func<T, T, bool> canPair)
    {
        foreach (T mc in moverOpen)
        {
            if (canPair(mc, target))
                return true;
        }
        return false;
    }
}
