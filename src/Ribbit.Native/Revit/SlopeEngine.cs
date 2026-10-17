using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using Ribbit.Geometry;
// Autodesk.Revit.DB also has a Units type; ours wins inside this file.
using Units = Ribbit.Geometry.Units;

namespace Ribbit.Native.Revit;

/// <summary>
/// The grouping half of settings.as_feet(), promoted from a dict.
///
/// <see cref="FollowDeadEnds"/> is Slope Through's alone: a closure that would
/// otherwise be "the rest of the system" because it holds long pipe and is
/// bigger than a small assembly is instead walked to completion and, if it is
/// complete, kept movable and flagged <see cref="SlopeGroup.DeadEnd"/> - a whole
/// branch the user did not select, which rides with its tee as one piece. The
/// caller MUST then prove it really is a dead end (it joins the sloped pipe at
/// one place only, and is no bigger than what it hangs off) or pin it; this
/// layer cannot see the other legs, so it cannot decide that.
/// </summary>
internal readonly record struct SlopeGroupParams(
    double ForeignRun,
    int SmallGroupElements,
    int MaxGroupElements,
    double GroupTiltTol,
    double AngleTolDeg,
    double PlanOffsetTol,
    bool FollowDeadEnds = false);

/// <summary>
/// Slope Branch / Slope Through — Revit-side reading and grouping. Ported from
/// the grouping half of lib/slopelib/engine.py.
///
/// READ-ONLY. This never opens a Transaction and never changes the model. Its
/// whole job is to decide, for every closure hanging off the picked run, whether
/// it is an in-line fitting that must rotate with the pitch, a rider that just
/// translates, or the rest of the system that must never move at all.
/// </summary>
internal static class SlopeEngine
{
    /// <summary>
    /// How far off the run axis a connector may point and still count as
    /// pointing ALONG the run. Generous: the only thing it has to separate is
    /// "in line with the pipe" from "teeing off it", and those are 90 apart.
    /// </summary>
    public const double InlineConeDeg = 20.0;

    /// <summary>
    /// Fittings and accessories both sit IN a run and both have to rotate with
    /// it, so they are treated the same throughout.
    /// </summary>
    public static bool IsFittingLike(Element elem)
    {
        if (elem.Category is null)
            return false;

        long catId = RevitCompat.IdKey(elem.Category.Id);
        return catId == (long)BuiltInCategory.OST_PipeFitting
               || catId == (long)BuiltInCategory.OST_PipeAccessory;
    }

    /// <summary>
    /// (p0, p1, source) for the element's straight axis. LocationCurve first,
    /// falling back to a pair of end connectors — which is how a fabrication
    /// part, placed by point, still yields the same axis.
    /// </summary>
    public static (Vec3? P0, Vec3? P1, string Source) GetAxisPoints(Element elem)
    {
        Line? line = ElementGeometry.GetLine(elem);
        if (line is not null)
            return (line.GetEndPoint(0).ToVec3(), line.GetEndPoint(1).ToVec3(), "LocationCurve");

        List<Connector> conns = MepConnectors.GetEndConnectors(elem);
        if (conns.Count == 2)
            return (conns[0].Origin.ToVec3(), conns[1].Origin.ToVec3(), "2 end connectors");

        return (null, null,
            $"no LocationCurve and {conns.Count} end connector(s) - need a straight curve or "
            + "exactly 2 connectors to get an axis");
    }

    /// <summary>
    /// The connector's outward direction, or null. A connector's BasisZ points
    /// OUT of the element it belongs to.
    /// </summary>
    public static Vec3? ConnectorDirection(Connector conn)
    {
        try
        {
            return conn.CoordinateSystem.BasisZ.ToVec3();
        }
        catch (Autodesk.Revit.Exceptions.ApplicationException)
        {
            return null;
        }
    }

    private static double PlanLength(Element elem)
    {
        (Vec3? p0, Vec3? p1, _) = GetAxisPoints(elem);
        if (p0 is null || p1 is null)
            return 0.0;

        double dx = p1.Value.X - p0.Value.X;
        double dy = p1.Value.Y - p0.Value.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    // ---------------------------------------------------------------- segments

    public static (List<SlopeSegment> Segments, Dictionary<long, Element> ElemById,
                   List<SprigEngine.Rejected> Rejected)
        BuildSegments(Document doc, IEnumerable<Element> elements)
    {
        List<SlopeSegment> segments = [];
        Dictionary<long, Element> elemById = [];
        List<SprigEngine.Rejected> rejected = [];

        foreach (Element raw in elements)
        {
            Element elem = Picking.ResolveHost(doc, raw);
            string what = SprigEngine.DescribeElem(elem);

            if (elem is RevitLinkInstance)
            {
                rejected.Add(new SprigEngine.Rejected(elem.Id,
                    $"{what} - this is a LINKED MODEL instance, not an element inside it. "
                    + "Picking through a link returns the link itself, and nothing in a link can "
                    + "be moved from here."));
                continue;
            }

            if (IsFittingLike(elem))
            {
                rejected.Add(new SprigEngine.Rejected(elem.Id,
                    $"{what} - this is a pipe fitting/accessory, not a length of pipe. Pick the "
                    + "straight pipe segments; every fitting in the run is found and re-pitched "
                    + "automatically."));
                continue;
            }

            (Vec3? p0, Vec3? p1, string source) = GetAxisPoints(elem);
            if (p0 is null || p1 is null)
            {
                rejected.Add(new SprigEngine.Rejected(elem.Id, $"{what} - {source}"));
                continue;
            }

            SlopeSegment seg = new(
                RevitCompat.IdKey(elem.Id), p0.Value, p1.Value,
                desc: $"{what} (axis from {source})",
                isPipe: elem is Pipe);

            if (seg.Length < 1.0e-6)
            {
                rejected.Add(new SprigEngine.Rejected(elem.Id, $"{what} - zero length"));
                continue;
            }

            segments.Add(seg);
            elemById[seg.ElemId] = elem;
        }

        return (segments, elemById, rejected);
    }

    // ----------------------------------------------------------------- closure

    internal sealed record ClosureResult(
        List<long> Order,
        Dictionary<long, Element> Seen,
        bool HitCap,
        bool StoppedEarly,
        bool LongSeen)
    {
        /// <summary>
        /// True when this closure is the rest of the SYSTEM rather than a
        /// terminating assembly.
        ///
        /// WHY THIS IS NOT <see cref="StoppedEarly"/>. StoppedEarly is a
        /// STOPPING optimisation — "provably too big to move, no point walking
        /// further". Its converse does NOT mean "not the system": the walk also
        /// fails to stop early when the system is small enough to finish inside
        /// the small-group threshold. Reading StoppedEarly as the answer
        /// misclassifies a compact system as a dead end — which is exactly what
        /// a 15-element branch closure did against a small-assembly size of 15,
        /// live, making tee-end detection fall back to a manual pick on every
        /// armover.
        ///
        /// Reaching a pipe longer than the foreign-run length is one real
        /// signal. SIZE is the other, and it must stand on its own: the source's
        /// own description of a tee end is "reaches a long pipe AND keeps
        /// growing past the small-group size", but requiring BOTH means a branch
        /// chopped into short segments never qualifies. Measured live on a
        /// branch carrying seven closely-spaced tees — every segment between
        /// them under the 1500 mm foreign-run length — where a 21-element
        /// closure still read as a dead end.
        /// </summary>
        public bool ReachesSystem(int small)
            => HitCap || StoppedEarly || LongSeen || Order.Count > small;
    }

    /// <summary>
    /// Every element reachable from the start without passing through the
    /// blocked set.
    ///
    /// Called twice with different blocks: with the picked segments, to find a
    /// group; and again with the segments PLUS every in-line fitting, to find
    /// what hangs off one particular fitting.
    ///
    /// WHEN THE WALK MAY STOP EARLY — only once the group is already too big to
    /// ever be moved. A closure of <paramref name="small"/> or fewer elements
    /// must be walked to completion even if it contains a pipe longer than
    /// <paramref name="foreignRun"/>: that is precisely an armover assembly,
    /// which terminates at its head a handful of elements in and RIDES WITH THE
    /// BRANCH. Only when the walk has seen a long pipe AND grown past
    /// <paramref name="small"/> is the group provably the rest of the system.
    ///
    /// An earlier version stopped at the first long pipe unconditionally. That
    /// both condemned every long armover to be left behind, and left the closure
    /// truncated mid-system so a second connection to the same system could seed
    /// a duplicate group.
    /// </summary>
    public static ClosureResult Closure(
        Element startElem, ISet<long> blockedIds, int cap,
        double? foreignRun = null, int? small = null)
    {
        Dictionary<long, Element> seen = [];
        List<long> order = [];
        Stack<Element> stack = new();
        stack.Push(startElem);

        bool hitCap = false;
        bool longSeen = false;
        bool stoppedEarly = false;

        while (stack.Count > 0)
        {
            Element elem = stack.Pop();
            long eid = RevitCompat.IdKey(elem.Id);
            if (seen.ContainsKey(eid) || blockedIds.Contains(eid))
                continue;

            seen[eid] = elem;
            order.Add(eid);

            if (foreignRun is not null && PlanLength(elem) > foreignRun)
                longSeen = true;

            if (longSeen && small is not null && order.Count > small)
            {
                stoppedEarly = true;
                break;
            }

            if (order.Count > cap)
            {
                hitCap = true;
                break;
            }

            foreach (Connector conn in MepConnectors.GetEndConnectors(elem))
            {
                ConnectorSet refs;
                try
                {
                    refs = conn.AllRefs;
                }
                catch (Autodesk.Revit.Exceptions.ApplicationException)
                {
                    continue;
                }

                foreach (Connector other in refs)
                {
                    Element? owner;
                    try
                    {
                        // AllRefs also hands back Logical/System connectors,
                        // which link everything on one piping system to
                        // everything else - following those would swallow the
                        // whole model in one closure.
                        if (other.ConnectorType != ConnectorType.End)
                            continue;
                        owner = other.Owner;
                    }
                    catch (Autodesk.Revit.Exceptions.ApplicationException)
                    {
                        continue;
                    }

                    if (owner is null)
                        continue;

                    long oid = RevitCompat.IdKey(owner.Id);
                    if (seen.ContainsKey(oid) || blockedIds.Contains(oid))
                        continue;

                    stack.Push(owner);
                }
            }
        }

        return new ClosureResult(order, seen, hitCap, stoppedEarly, longSeen);
    }

    // ------------------------------------------------------------------ groups

    /// <summary>
    /// Joints is every connector pair that crosses out of — or runs between —
    /// the picked segments. The applier breaks exactly these and no others, then
    /// puts them back.
    /// </summary>
    public static (List<SlopeGroup> Groups, List<Joint> Joints) CollectGroups(
        IReadOnlyList<Element> branchElems,
        ISet<long> branchIds,
        SlopeRun run,
        RePitch pitch,
        SlopeGroupParams parameters)
    {
        int cap = parameters.MaxGroupElements;
        double foreignRun = parameters.ForeignRun;
        int small = parameters.SmallGroupElements;

        List<SlopeGroup> groups = [];
        Dictionary<long, int> groupOf = [];
        Dictionary<long, Element> elemCache = [];
        List<Joint> joints = [];
        HashSet<(long, long, double, double, double)> seenJoints = [];

        foreach (Element belem in branchElems)
        {
            long bId = RevitCompat.IdKey(belem.Id);

            foreach (Connector conn in MepConnectors.GetEndConnectors(belem))
            {
                ConnectorSet refs;
                try
                {
                    refs = conn.AllRefs;
                }
                catch (Autodesk.Revit.Exceptions.ApplicationException)
                {
                    continue;
                }

                foreach (Connector other in refs)
                {
                    Element? owner;
                    try
                    {
                        if (other.ConnectorType != ConnectorType.End)
                            continue;
                        owner = other.Owner;
                    }
                    catch (Autodesk.Revit.Exceptions.ApplicationException)
                    {
                        continue;
                    }

                    if (owner is null)
                        continue;

                    long oId = RevitCompat.IdKey(owner.Id);
                    if (oId == bId)
                        continue;

                    (long, long, double, double, double) key = (
                        Math.Min(bId, oId), Math.Max(bId, oId),
                        Math.Round(conn.Origin.X, 6),
                        Math.Round(conn.Origin.Y, 6),
                        Math.Round(conn.Origin.Z, 6));

                    if (seenJoints.Add(key))
                    {
                        joints.Add(new Joint(
                            bId, conn.Origin.ToVec3(), oId, other.Origin.ToVec3()));
                    }

                    if (branchIds.Contains(oId))
                    {
                        // Segment-to-segment inside the run: it is a joint to
                        // break and remake, but there is no group to move.
                        continue;
                    }

                    if (!groupOf.TryGetValue(oId, out int gi))
                    {
                        // Following dead ends needs the WHOLE closure: stopping at the
                        // first long pipe past a small assembly is exactly what would
                        // make an unselected branch look like the rest of the system.
                        ClosureResult closure = Closure(
                            owner, branchIds, cap, foreignRun,
                            parameters.FollowDeadEnds ? null : small);
                        foreach (KeyValuePair<long, Element> kv in closure.Seen)
                            elemCache[kv.Key] = kv.Value;

                        SlopeGroup group = new(oId, closure.Order);

                        if (closure.StoppedEarly)
                        {
                            // The element count is a WHERE-IT-GAVE-UP, not a
                            // size. Without this note, a real armover assembly
                            // one element over the small limit is
                            // indistinguishable from the cross main.
                            group.AddNote(
                                $"the walk stopped at {closure.Order.Count} elements after seeing "
                                + "a long pipe, so the true size is unknown - if this is really an "
                                + "armover assembly, raise 'Small assembly size' and re-run");
                        }

                        if (closure.HitCap)
                        {
                            group.Kind = GroupKind.Foreign;
                            group.Truncated = true;
                            group.AddNote(
                                $"more than {cap} connected elements and no long pipe found - the "
                                + "walk was CUT SHORT, so this group may be incomplete");
                        }

                        gi = groups.Count;
                        groups.Add(group);
                        foreach (long mid in closure.Order)
                            groupOf[mid] = gi;
                    }

                    groups[gi].Attachments.Add(
                        (bId, conn.Origin.ToVec3(), other.Origin.ToVec3()));
                }
            }
        }

        foreach (SlopeGroup group in groups)
        {
            Classify(group, elemCache, branchIds, run, pitch, parameters);
            // Folded into the one joint list so break/remake and the open-joint
            // report treat them exactly like a branch joint.
            joints.AddRange(group.InternalJoints);
        }

        return (groups, joints);
    }

    /// <summary>Decide this group's kind and, if it moves, exactly how.</summary>
    private static void Classify(
        SlopeGroup group, IReadOnlyDictionary<long, Element> elemCache,
        ISet<long> branchIds, SlopeRun run, RePitch pitch,
        SlopeGroupParams parameters)
    {
        double foreignRun = parameters.ForeignRun;
        double tiltTol = parameters.GroupTiltTol;
        int small = parameters.SmallGroupElements;

        // --- does the closure run off into the rest of the system?
        if (group.Kind != GroupKind.Foreign)
        {
            foreach (long mid in group.MemberIds)
            {
                if (!elemCache.TryGetValue(mid, out Element? elem))
                    continue;

                (Vec3? p0, Vec3? p1, _) = GetAxisPoints(elem);
                if (p0 is null || p1 is null)
                    continue;

                double dx = p1.Value.X - p0.Value.X;
                double dy = p1.Value.Y - p0.Value.Y;
                double planLen = Math.Sqrt(dx * dx + dy * dy);
                if (planLen <= foreignRun)
                    continue;

                Vec3 planDir = new Vec3(dx, dy, 0.0).Normalize();
                bool inLine = Math.Abs(planDir.DotProduct(run.Axis))
                              > Math.Cos(parameters.AngleTolDeg * Math.PI / 180.0);
                bool onAxis = run.PlanOffset(p0.Value) <= parameters.PlanOffsetTol
                              && run.PlanOffset(p1.Value) <= parameters.PlanOffsetTol;

                if (inLine && onAxis)
                {
                    group.Kind = GroupKind.Missed;
                    group.AddNote(
                        $"element {mid} is {Units.Mm(planLen)} of pipe in line with, and on, this "
                        + "run - it looks like more of the SAME branch line that was not picked. "
                        + "Re-run and include it.");
                }
                else if (group.Size > small && parameters.FollowDeadEnds && !group.Truncated)
                {
                    // Provisional: the caller still has to prove it is a dead end.
                    group.DeadEnd = true;
                    group.AddNote(
                        $"a whole branch you did not select ({group.Size} element(s), including "
                        + $"{Units.Mm(planLen)} of pipe at element {mid}) - moved as ONE piece with "
                        + "the fitting it hangs off, keeping its own slope");
                }
                else if (group.Size > small)
                {
                    group.Kind = GroupKind.Foreign;
                    group.AddNote(
                        $"contains {Units.Mm(planLen)} of pipe (element {mid}) - this leads off "
                        + "into the rest of the system and is left alone");
                }
                else
                {
                    // A long pipe in a SMALL, complete closure is an armover,
                    // not the rest of the system: the assembly terminated at its
                    // head a few elements in. It rides with the branch.
                    group.AddNote(
                        $"contains {Units.Mm(planLen)} of pipe (element {mid}), but the whole "
                        + $"assembly is only {group.Size} element(s) and ends at a head - treated "
                        + "as an armover and moved with the branch");
                }
                break;
            }
        }

        // --- where does it attach, and does one translation suit all of them?
        List<double> dzs = [.. group.Attachments.Select(a => pitch.Dz(a.GroupOrigin))];
        List<Vec3> points = [.. group.Attachments.Select(a => a.GroupOrigin)];

        if (points.Count == 0)
        {
            group.Kind = GroupKind.Foreign;
            group.AddNote("no usable attachment point");
            return;
        }

        group.Dz = dzs.Average();
        double spread = dzs.Max() - dzs.Min();

        // --- in line with the run, or hanging off it?
        //
        // Tested in FULL 3D against the run's current pitched direction, not
        // against the plan axis. The first cut compared plan directions only,
        // which reads a 45 degree lateral as in-line - it does point along the
        // run in plan. That put a connector 45 degrees up the leg into the pivot
        // average, lifting the pivot off the pipe centreline, leaving the fitting
        // with no outlet to follow, and opening the joint by exactly the amount
        // the pivot was wrong by.
        //
        // In 3D the three cases separate cleanly: an in-line connector is ~0 off
        // the run axis, a lateral is 45, and a vertical sprig or a perpendicular
        // armover is 90. The run's CURRENT direction is read at this group's own
        // station - the old pitch can differ per segment, and the fittings are
        // aligned to what is there now, not to the end-to-end average.
        Vec3 runDir = SlopeSolver.DirectionAtRate(
            run.Axis, pitch.OldRateAt(run.Station(points[0])));
        double cone = Math.Cos(InlineConeDeg * Math.PI / 180.0);

        if (group.Kind is GroupKind.Foreign or GroupKind.Missed)
        {
            group.Pivot = points[0];
            return;
        }

        List<InlinePart> parts = BuildInlineParts(
            group, elemCache, branchIds, runDir, cone, pitch, parameters.MaxGroupElements);

        if (parts.Count > 0)
        {
            group.Kind = GroupKind.Inline;
            group.Rotate = true;
            group.Parts = parts;
            group.Pivot = parts[0].Pivot;
            group.Dz = parts.Average(p => p.Dz);
            group.InternalJoints = CollectInternalJoints(group, elemCache);

            HashSet<long> claimed = [.. parts.Select(p => p.ElemId)];
            foreach (InlinePart part in parts)
            {
                foreach ((List<long> ids, _) in part.FollowSets)
                    claimed.UnionWith(ids);
            }

            List<long> stranded = [.. group.MemberIds.Where(m => !claimed.Contains(m))];
            if (stranded.Count > 0)
            {
                group.AddNote(
                    $"{stranded.Count} element(s) in this group are not reachable from any in-line "
                    + $"fitting and will be left where they are: {string.Join(", ", stranded)}");
            }
        }
        else
        {
            group.Kind = GroupKind.Rider;
            group.Pivot = points[0];
            if (spread > tiltTol)
            {
                group.Kind = GroupKind.NonRigid;
                group.AddNote(
                    $"touches the run at points wanting to move by amounts {SlopeSolver.Mm1(spread)} "
                    + "apart - no single translation is right, so it is left alone");
            }
        }

        group.MaxMove = EstimateMaxMove(group);
    }

    private static (double, double, double) OriginKey(Vec3 point)
        => (Math.Round(point.X, 6), Math.Round(point.Y, 6), Math.Round(point.Z, 6));

    /// <summary>Origins of this element's connectors that point along the run.</summary>
    private static List<Vec3> AlignedOrigins(Element elem, Vec3 runDir, double cone)
    {
        List<Vec3> outp = [];
        foreach (Connector conn in MepConnectors.GetEndConnectors(elem))
        {
            Vec3? direction = ConnectorDirection(conn);
            if (direction is null || direction.Value.GetLength() < 1.0e-9)
                continue;

            if (Math.Abs(direction.Value.Normalize().DotProduct(runDir)) > cone)
                outp.Add(conn.Origin.ToVec3());
        }
        return outp;
    }

    /// <summary>
    /// Every fitting in this group that sits IN the run, each with the
    /// sub-closure that hangs off it.
    ///
    /// The first cut treated a group as one root fitting plus followers, and
    /// carried every other member on the root's outlet. That is wrong the moment
    /// two fittings bridge the gap between two picked segments: the second one
    /// sits at its own station, wants its own drop onto the new line, and instead
    /// inherited the first one's — which is exactly one open joint's worth of
    /// error, at the far end of the gap.
    /// </summary>
    private static List<InlinePart> BuildInlineParts(
        SlopeGroup group, IReadOnlyDictionary<long, Element> elemCache,
        ISet<long> branchIds, Vec3 runDir, double cone, RePitch pitch, int cap)
    {
        Dictionary<long, List<Vec3>> inlineMap = [];
        foreach (long mid in group.MemberIds)
        {
            if (!elemCache.TryGetValue(mid, out Element? elem) || !IsFittingLike(elem))
                continue;

            List<Vec3> origins = AlignedOrigins(elem, runDir, cone);
            if (origins.Count > 0)
                inlineMap[mid] = origins;
        }

        if (inlineMap.Count == 0)
            return [];

        // Followers may not be walked through the run, nor through another
        // in-line fitting - each of those is its own part.
        HashSet<long> blocked = [.. branchIds, .. inlineMap.Keys];
        HashSet<long> claimed = [];
        List<InlinePart> parts = [];

        foreach (long mid in inlineMap.Keys.OrderBy(k => k))
        {
            List<Vec3> origins = inlineMap[mid];
            Element elem = elemCache[mid];

            Vec3 pivot = new(
                origins.Average(p => p.X),
                origins.Average(p => p.Y),
                origins.Average(p => p.Z));

            InlinePart part = new(mid, pivot, pitch.Dz(pivot));

            HashSet<(double, double, double)> inlineKeys = [.. origins.Select(OriginKey)];

            foreach (Connector conn in MepConnectors.GetEndConnectors(elem))
            {
                if (inlineKeys.Contains(OriginKey(conn.Origin.ToVec3())))
                    continue;

                ConnectorSet refs;
                try
                {
                    refs = conn.AllRefs;
                }
                catch (Autodesk.Revit.Exceptions.ApplicationException)
                {
                    continue;
                }

                foreach (Connector other in refs)
                {
                    Element? owner;
                    try
                    {
                        if (other.ConnectorType != ConnectorType.End)
                            continue;
                        owner = other.Owner;
                    }
                    catch (Autodesk.Revit.Exceptions.ApplicationException)
                    {
                        continue;
                    }

                    if (owner is null)
                        continue;

                    long oid = RevitCompat.IdKey(owner.Id);
                    if (blocked.Contains(oid) || claimed.Contains(oid))
                        continue;

                    // No foreignRun here: this walk is looking for the sprig
                    // hanging off one fitting, and stopping it early would
                    // truncate exactly what needs moving.
                    ClosureResult sub = Closure(owner, blocked, cap);
                    List<long> ids = [.. sub.Order.Where(i => !claimed.Contains(i))];
                    if (ids.Count == 0)
                        continue;

                    claimed.UnionWith(ids);

                    // The sprig is TRANSLATED, never rotated, so it stays plumb -
                    // by exactly the displacement of the outlet it hangs from as
                    // that outlet tips with its fitting.
                    Vec3 after = pitch.TransformInline(conn.Origin.ToVec3(), pivot);
                    part.FollowSets.Add((ids, after.Subtract(conn.Origin.ToVec3())));
                }
            }

            parts.Add(part);
        }

        return parts;
    }

    /// <summary>
    /// Joints INSIDE an in-line group that have to be broken like any other.
    ///
    /// Every joint on an in-line fitting joins two things that are about to
    /// receive DIFFERENT transforms — another fitting rotating about its own
    /// pivot at its own station, or a sprig that translates without rotating at
    /// all. Revit maintains a connection through a move, so leaving these intact
    /// means rotating the first fitting drags the second, then rotating the
    /// second about its own pivot drags the first back, and the pair ends up
    /// somewhere neither of them was asked to go.
    ///
    /// The visible symptom was a group reporting a clean 7.2 mm move and then
    /// opening both its branch joints by exactly 7.2 mm — the fittings had not
    /// moved at all. The invisible one is worse: a sprig dragged by its tee and
    /// then translated again lands at roughly twice the intended offset, with its
    /// joint still closed, so nothing reports it.
    /// </summary>
    private static List<Joint> CollectInternalJoints(
        SlopeGroup group, IReadOnlyDictionary<long, Element> elemCache)
    {
        HashSet<long> members = [.. group.MemberIds];
        List<Joint> outp = [];
        HashSet<(long, long, double, double, double)> seen = [];

        foreach (InlinePart part in group.Parts)
        {
            if (!elemCache.TryGetValue(part.ElemId, out Element? elem))
                continue;

            foreach (Connector conn in MepConnectors.GetEndConnectors(elem))
            {
                ConnectorSet refs;
                try
                {
                    refs = conn.AllRefs;
                }
                catch (Autodesk.Revit.Exceptions.ApplicationException)
                {
                    continue;
                }

                foreach (Connector other in refs)
                {
                    Element? owner;
                    try
                    {
                        if (other.ConnectorType != ConnectorType.End)
                            continue;
                        owner = other.Owner;
                    }
                    catch (Autodesk.Revit.Exceptions.ApplicationException)
                    {
                        continue;
                    }

                    if (owner is null)
                        continue;

                    long oid = RevitCompat.IdKey(owner.Id);
                    if (oid == part.ElemId || !members.Contains(oid))
                        continue;

                    (double x, double y, double z) = OriginKey(conn.Origin.ToVec3());
                    (long, long, double, double, double) key = (
                        Math.Min(part.ElemId, oid), Math.Max(part.ElemId, oid), x, y, z);

                    if (!seen.Add(key))
                        continue;

                    outp.Add(new Joint(
                        part.ElemId, conn.Origin.ToVec3(), oid, other.Origin.ToVec3()));
                }
            }
        }

        return outp;
    }

    /// <summary>
    /// The largest vertical displacement anything in this group sees. Used only
    /// to flag a suspicious run in the report.
    /// </summary>
    private static double EstimateMaxMove(SlopeGroup group)
    {
        List<double> moves = [Math.Abs(group.Dz)];
        foreach (InlinePart part in group.Parts)
        {
            moves.Add(Math.Abs(part.Dz));
            foreach ((_, Vec3 delta) in part.FollowSets)
                moves.Add(Math.Abs(delta.Z));
        }
        return moves.Max();
    }

    public static Dictionary<GroupKind, int> Summarize(IEnumerable<SlopeGroup> groups)
    {
        Dictionary<GroupKind, int> counts = [];
        foreach (SlopeGroup g in groups)
            counts[g.Kind] = counts.TryGetValue(g.Kind, out int n) ? n + 1 : 1;
        return counts;
    }
}
