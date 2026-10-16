namespace Ribbit.Geometry.Rooms;

/// <summary>A point in plan (feet).</summary>
public readonly record struct Pt2(double X, double Y);

/// <summary>
/// The arithmetic behind Room Tag Checker (ported from "Room Tag Checker v3.dyn"),
/// kept apart from Revit so every rule can be tested on its own.
/// </summary>
public static class RoomTagMath
{
    /// <summary>
    /// Don't stack a new tag within this planar distance (ft) of a tag head already
    /// in the view - an overlapping or redundant room in the link would otherwise
    /// be double-tagged under a different id.
    /// </summary>
    public const double DuplicateProximityFt = 1.0;

    /// <summary>
    /// Ray casting in plan. The crop is a closed loop, so this is exact for any
    /// shape it can take, concave ones included; a split or edited crop is not
    /// a rectangle, and its bounding box would accept rooms in the parts cut away.
    /// </summary>
    public static bool InPolygon(double x, double y, IReadOnlyList<Pt2> points)
    {
        bool inside = false;
        int count = points.Count;
        for (int i = 0, j = count - 1; i < count; j = i++)
        {
            Pt2 a = points[i], b = points[j];
            if ((a.Y > y) != (b.Y > y) && b.Y != a.Y && x < a.X + (y - a.Y) * (b.X - a.X) / (b.Y - a.Y))
                inside = !inside;
        }
        return inside;
    }

    /// <summary>Inside any of the crop's loops.</summary>
    public static bool InAnyLoop(double x, double y, IEnumerable<IReadOnlyList<Pt2>> loops)
        => loops.Any(l => l.Count >= 3 && InPolygon(x, y, l));

    /// <summary>Within <paramref name="tolerance"/> (planar) of any of the points.</summary>
    public static bool NearAny(Pt2 p, IEnumerable<Pt2> others, double tolerance)
    {
        foreach (Pt2 o in others)
        {
            double dx = p.X - o.X, dy = p.Y - o.Y;
            if (Math.Sqrt(dx * dx + dy * dy) < tolerance)
                return true;
        }
        return false;
    }

    public static string Normalize(string? name) => (name ?? "").Trim().ToLowerInvariant();

    /// <summary>
    /// Host level -> source index, for the levels whose names agree (trimmed, case
    /// ignored). Nothing else is guessed: a host view on "ZZ_00_Level 00 (Ground)"
    /// was found against a link whose rooms all sat on "DC_00_Level 0.50 (Ground)".
    /// The first source with a name wins, as the graph's dictionary did.
    /// </summary>
    public static Dictionary<long, int> AutoPair(
        IEnumerable<(long LevelId, string Name)> hostLevels, IReadOnlyList<string> sourceLevelNames)
    {
        Dictionary<string, int> byName = new(StringComparer.Ordinal);
        for (int i = 0; i < sourceLevelNames.Count; i++)
        {
            string key = Normalize(sourceLevelNames[i]);
            if (!byName.ContainsKey(key))
                byName[key] = i;
        }
        Dictionary<long, int> pairs = [];
        foreach ((long id, string name) in hostLevels)
            if (byName.TryGetValue(Normalize(name), out int s))
                pairs[id] = s;
        return pairs;
    }

    /// <summary>
    /// Of one room's tags, which to keep (the oldest - lowest id) and which are
    /// the extras to delete.
    /// </summary>
    public static (long Keep, List<long> Extras) SplitDuplicates(IEnumerable<long> tagIds)
    {
        List<long> ordered = [.. tagIds.OrderBy(i => i)];
        if (ordered.Count == 0)
            throw new ArgumentException("no tags", nameof(tagIds));
        return (ordered[0], ordered.GetRange(1, ordered.Count - 1));
    }

    /// <summary>"name (2)" for the second of two identical labels, and so on.</summary>
    public static string UniqueLabel(string label, ISet<string> used)
    {
        string candidate = label;
        int suffix = 2;
        while (used.Contains(candidate))
            candidate = $"{label} ({suffix++})";
        used.Add(candidate);
        return candidate;
    }
}
