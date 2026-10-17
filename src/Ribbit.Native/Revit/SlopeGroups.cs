using Ribbit.Geometry;

namespace Ribbit.Native.Revit;

/// <summary>What to do with one closure hanging off the run.</summary>
public enum GroupKind
{
    /// <summary>Sits IN the run: rotates with the pitch, then drops.</summary>
    Inline,

    /// <summary>Hangs off the run: translated vertically, never rotated.</summary>
    Rider,

    /// <summary>The rest of the system — never moved.</summary>
    Foreign,

    Missed,
    NonRigid,
}

/// <summary>
/// One joint between two elements, recorded before anything moves so it can be
/// broken and remade. Carries plain ids and positions only — never live
/// Connector objects, which do not survive a document change.
/// </summary>
public readonly record struct Joint(long AId, Vec3 AOrigin, long BId, Vec3 BOrigin);

/// <summary>
/// One fitting that sits IN the run, with everything that hangs off it.
///
/// A group can hold several of these. The gap between two picked segments is
/// often bridged by more than one fitting — a tee plus a nipple, a pair of
/// grooved couplings — and each sits at its own station, so each needs its own
/// pivot and its own drop onto the new line. Carrying the second one along on
/// the first one's outlet is only right when they are rigidly one piece, which
/// they are not.
/// </summary>
public sealed class InlinePart(long elemId, Vec3 pivot, double dz)
{
    public long ElemId { get; } = elemId;
    public Vec3 Pivot { get; } = pivot;
    public double Dz { get; } = dz;

    /// <summary>
    /// The sprigs/drops hanging off this particular fitting, each translated by
    /// the displacement of the outlet they hang from.
    /// </summary>
    public List<(List<long> Ids, Vec3 Delta)> FollowSets { get; } = [];

    /// <summary>
    /// Optional SECOND rotation, about a DIFFERENT axis than the group's own
    /// pitch axis — used only by Slope Through's corner elbows, which need a
    /// roll on top of the ordinary pitch rotation to bring the CHILD leg's
    /// connector as close as possible to where it independently needs to be.
    /// Null/0 for every ordinary in-line fitting.
    /// </summary>
    public Vec3? RollAxis { get; set; }

    public double RollAngle { get; set; }

    /// <summary>
    /// A point the roll axis passes through, as it stands AFTER the pitch
    /// rotation and before the drop - or null for <see cref="Pivot"/>.
    ///
    /// A corner elbow's pivot is its chord midpoint, which is OFF the parent
    /// pipe's centreline, so rolling about it lifted the parent-side connector by
    /// half the elbow size x the roll. On a big bend that opened the parent's own
    /// joint (Snowdon's 200 mm bend, 2026-10-17). Rolling about a line through the
    /// parent-side connector leaves that connector exactly where the pitch put it.
    /// </summary>
    public Vec3? RollPivot { get; set; }
}

/// <summary>
/// One connected closure hanging off the run, plus the decision about what to do
/// with it. Carries plain ids and positions only.
/// </summary>
public sealed class SlopeGroup(long rootId, IEnumerable<long> memberIds)
{
    public long RootId { get; } = rootId;
    public List<long> MemberIds { get; } = [.. memberIds];

    /// <summary>(branchId, branchConnOrigin, groupConnOrigin)</summary>
    public List<(long BranchId, Vec3 BranchOrigin, Vec3 GroupOrigin)> Attachments { get; } = [];

    public GroupKind Kind { get; set; } = GroupKind.Rider;
    public List<string> Notes { get; } = [];

    /// <summary>
    /// True when this group was pinned because it holds the user's explicitly
    /// picked fixed reference. A flag, not something callers infer by
    /// pattern-matching the note text.
    /// </summary>
    public bool PinnedAsReference { get; set; }

    /// <summary>
    /// A whole unselected branch kept movable only because Slope Through asked
    /// for dead ends to be followed. Provisional until the caller has proved it
    /// touches the sloped pipe at one place and is no bigger than what it hangs
    /// off; one that fails is pinned and the run refused.
    /// </summary>
    public bool DeadEnd { get; set; }

    /// <summary>In-line rotation pivot / attach point.</summary>
    public Vec3? Pivot { get; set; }

    /// <summary>Vertical move of the pivot.</summary>
    public double Dz { get; set; }

    public bool Rotate { get; set; }
    public List<InlinePart> Parts { get; set; } = [];

    /// <summary>Joints WITHIN the group that must break.</summary>
    public List<Joint> InternalJoints { get; set; } = [];

    /// <summary>Closure hit the cap — may be incomplete.</summary>
    public bool Truncated { get; set; }

    public double MaxMove { get; set; }

    public int Size => MemberIds.Count;

    /// <summary>(min, max) vertical move across the group's in-line parts.</summary>
    public (double Min, double Max) DzRange()
    {
        if (Parts.Count == 0)
            return (Dz, Dz);

        double min = Parts.Min(p => p.Dz);
        double max = Parts.Max(p => p.Dz);
        return (min, max);
    }

    public void AddNote(string text)
    {
        if (!Notes.Contains(text))
            Notes.Add(text);
    }
}

public static class SlopeGroupUtil
{
    /// <summary>The kinds that actually move.</summary>
    public static bool Moves(GroupKind kind) => kind is GroupKind.Inline or GroupKind.Rider;

    public static List<SlopeGroup> Movable(IEnumerable<SlopeGroup> groups)
        => [.. groups.Where(g => Moves(g.Kind))];

    /// <summary>
    /// Remove every entry that came from this group's OWN internal joints,
    /// matched by the (aId, bId) pair rather than by identity.
    ///
    /// Call this BEFORE clearing the group's internal joints, from whichever
    /// pin path forces a group to Foreign after the fact: collect_groups may
    /// already have classified the closure as a movable in-line assembly and
    /// computed internal joints across its own members before the override runs.
    /// Those stale joints have to come out of the leg's joint list too, or the
    /// break/remake machinery would disconnect and reconnect a web of
    /// connections inside a group that never moves.
    /// </summary>
    public static List<Joint> StripGroupJoints(IEnumerable<Joint> joints, SlopeGroup group)
    {
        HashSet<(long, long)> internalPairs =
        [
            .. group.InternalJoints.Select(j =>
                (Math.Min(j.AId, j.BId), Math.Max(j.AId, j.BId)))
        ];

        return [.. joints.Where(j =>
            !internalPairs.Contains((Math.Min(j.AId, j.BId), Math.Max(j.AId, j.BId))))];
    }
}
