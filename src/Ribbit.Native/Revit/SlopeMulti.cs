using Autodesk.Revit.DB;
using Ribbit.Geometry;

namespace Ribbit.Native.Revit;

/// <summary>
/// One branch: what was picked, what was solved, what will move.
/// Ported from lib/slopelib/multi.py's Job.
/// </summary>
internal sealed class SlopeJob(int index, Element seed, Element reference)
{
    public int Index { get; } = index;
    public Element Seed { get; } = seed;
    public Element Reference { get; } = reference;

    public long SeedId { get; } = RevitCompat.IdKey(seed.Id);
    public long ReferenceId { get; } = RevitCompat.IdKey(reference.Id);

    public SlopeRun? Run { get; set; }
    public RePitch? Pitch { get; set; }
    public List<SlopeGroup> Groups { get; set; } = [];
    public List<Joint> Joints { get; set; } = [];

    /// <summary>
    /// Kept as TWO lists rather than the Python's single mixed one — a pick
    /// dropped before it became a segment and a segment dropped by the run
    /// builder are different things with different reasons.
    /// </summary>
    public List<SprigEngine.Rejected> RejectedPicks { get; set; } = [];

    public List<(SlopeSegment Segment, string Reason)> RejectedSegments { get; set; } = [];

    public HashSet<long> BranchIds { get; set; } = [];
    public List<Element> Elements { get; set; } = [];

    /// <summary>
    /// Plan for rebuilding the fixed reference's OWN fitting fresh after the
    /// re-pitch, so it adopts the new angle instead of staying at the old one.
    /// Null when the reference is not a plain 2-connector fitting — a TEE keeps
    /// its pin-and-never-touch treatment, because rebuilding one would have to
    /// preserve the two branch-line connections it also carries.
    ///
    /// Same plan and same rebuild Slope Through uses at ITS fixed end.
    /// </summary>
    public Tools.FixedRebuild? FixedRebuild { get; set; }

    /// <summary>
    /// The same, for a TEE — which is the usual case for an armover, since it
    /// takes off the branch line through one.
    /// </summary>
    public Tools.FixedTeeRebuild? FixedTeeRebuild { get; set; }

    /// <summary>Why this branch could not be solved; null when it could.</summary>
    public string? Error { get; set; }

    public bool Ok => Error is null && Pitch is not null;

    /// <summary>
    /// True when no reference was ever established — the job exists only to
    /// carry its reason into the report. <see cref="ReferenceId"/> is meaningless
    /// on these, which is why the report must not print it.
    /// </summary>
    public bool ReferenceUnknown { get; private init; }

    /// <summary>
    /// A pick whose fixed reference could not be worked out at all.
    ///
    /// The seed stands in as the reference so the object is constructible, and
    /// <see cref="ReferenceUnknown"/> says so out loud — otherwise a reader
    /// (or a future conflict test) could take SeedId == ReferenceId at face
    /// value. Never Ok, so it is filtered out before anything uses those ids.
    /// </summary>
    public static SlopeJob Failed(int index, Element seed, string? error)
        => new(index, seed, seed) { Error = error ?? "could not be solved", ReferenceUnknown = true };

    /// <summary>
    /// Every element this job READS — its plan depends on where all of these
    /// are.
    /// </summary>
    public HashSet<long> TouchedIds()
    {
        HashSet<long> ids = [.. BranchIds];
        foreach (SlopeGroup group in Groups)
            ids.UnionWith(group.MemberIds);
        return ids;
    }

    /// <summary>
    /// Only the elements this job WRITES. Two jobs may safely read the same
    /// cross main — each breaks and remakes its own joints on it — but nothing
    /// one job moves may be so much as read by the other, or the other's plan
    /// is invalidated mid-run.
    /// </summary>
    public HashSet<long> MovingIds()
    {
        HashSet<long> ids = [.. BranchIds];
        foreach (SlopeGroup group in SlopeGroupUtil.Movable(Groups))
            ids.UnionWith(group.MemberIds);
        return ids;
    }
}

/// <summary>
/// Many branches in one run. Ported from lib/slopelib/multi.py.
///
/// READ-ONLY, like <see cref="SlopeEngine"/>: builds one <see cref="SlopeJob"/>
/// per branch, all solved against the model as it stands, and hands the lot to
/// the applier in a single transaction.
///
/// NOT PORTED: multi.py's own find_overlaps(). It backs the Slope Branch Multi
/// button, which this package does not have, and Slope Armover — the one caller
/// that exists — deliberately uses a DIFFERENT conflict test. See
/// SlopeArmoverTool for why moved-vs-touched is wrong there.
/// </summary>
internal static class SlopeMulti
{
    /// <summary>
    /// A run of more pipes than this is not a branch line, it is a mistake —
    /// stop walking rather than swallow a whole floor.
    /// </summary>
    private const int ExtendCap = 200;

    /// <summary>
    /// A single point standing for the reference element. Only its position
    /// ALONG the run matters — it decides which end is anchored — so the
    /// midpoint of a long cross main is as good as any.
    /// </summary>
    public static Vec3? ReferencePoint(Element elem)
    {
        (Vec3? p0, Vec3? p1, _) = SlopeEngine.GetAxisPoints(elem);
        if (p0 is not null && p1 is not null)
            return p0.Value.Add(p1.Value).Multiply(0.5);

        try
        {
            if (elem.Location is LocationPoint lp && lp.Point is not null)
                return lp.Point.ToVec3();
        }
        catch (Exception)
        {
            // Fall through to the connector average.
        }

        List<Connector> conns = MepConnectors.GetAllConnectors(elem);
        if (conns.Count == 0)
            return null;

        Vec3 total = new(0.0, 0.0, 0.0);
        foreach (Connector conn in conns)
            total = total.Add(conn.Origin.ToVec3());

        return total.Multiply(1.0 / conns.Count);
    }

    /// <summary>Every element joined to this one at an End connector.</summary>
    public static List<Element> ConnectedAtEnds(Element elem)
    {
        List<Element> outp = [];

        foreach (Connector conn in MepConnectors.GetAllConnectors(elem))
        {
            ConnectorSet refs;
            try
            {
                refs = conn.AllRefs;
            }
            catch (Exception)
            {
                continue;
            }

            foreach (Connector other in refs)
            {
                try
                {
                    if (other.ConnectorType != ConnectorType.End)
                        continue;

                    Element? owner = other.Owner;
                    if (owner is not null)
                        outp.Add(owner);
                }
                catch (Exception)
                {
                    // Skip an unreadable reference.
                }
            }
        }

        return outp;
    }

    /// <summary>
    /// Every pipe on the same straight run as the seed, seed included.
    ///
    /// Walks the connections outward, stepping THROUGH fittings without ever
    /// collecting them — the fittings are found again later as groups — and
    /// keeping any pipe parallel to the seed, on its axis line, and not steep.
    /// A tee's sprig fails by being steep, a cross main fails by being
    /// perpendicular, so the walk stops itself at the ends of the branch without
    /// needing to know what any fitting is.
    ///
    /// TWO RULES LIVE TESTING FORCED, both worth not re-deriving:
    ///
    ///   - STEEPNESS, not exact verticality. A sprig canted a hair off plumb has
    ///     a sliver of plan direction that can line up with the run, and it sits
    ///     on the axis by construction — two were collected as "run segments"
    ///     whose re-pitch would have crushed them to zero length. The cutoff also
    ///     sits below 45 deg so the leg of a 45-deg elbow on a pitched run
    ///     (leaning at 45 minus the pitch) can never walk in as run.
    ///   - Fittings go through the FRONTIER, never into the collection. Stepping
    ///     exactly one fitting deep meant a tee-plus-coupling gap blocked the
    ///     walk, while a coupling reached as the far side of another fitting
    ///     slipped INTO the collection — two end connectors on the run axis
    ///     satisfy the same test a short pipe does.
    ///
    /// The reason is set only when the SEED itself cannot start a run.
    /// </summary>
    public static (List<Element> Elements, string? Reason) ExtendRun(
        Element seed, SlopeParams parameters)
    {
        (Vec3? sp0, Vec3? sp1, _) = SlopeEngine.GetAxisPoints(seed);
        if (sp0 is null || sp1 is null)
            return ([], $"{SprigEngine.DescribeElem(seed)} has no straight axis to follow");

        Vec3 p0 = sp0.Value;
        Vec3 p1 = sp1.Value;

        Vec3 plan = new(p1.X - p0.X, p1.Y - p0.Y, 0.0);
        double planLen = plan.GetLength();

        if (planLen < SlopeSolver.LenEps
            || Math.Abs(p1.Z - p0.Z) > planLen * SlopeSolver.SteepRate)
        {
            return ([], $"{SprigEngine.DescribeElem(seed)} is vertical or steeper than "
                        + $"{SlopeSolver.SteepDeg:F0} deg - pick a horizontal pipe on the branch, "
                        + "not a riser or a sprig");
        }

        Vec3 axis = plan.Normalize();
        Vec3 origin = new(p0.X, p0.Y, 0.0);
        double offsetTol = parameters.PlanOffsetTol;
        double cosTol = Math.Cos(parameters.AngleTolDeg * Math.PI / 180.0);

        bool OnAxis(Vec3 point)
        {
            double station = ((point.X - origin.X) * axis.X) + ((point.Y - origin.Y) * axis.Y);
            double px = origin.X + (axis.X * station);
            double py = origin.Y + (axis.Y * station);
            return Math.Sqrt(((point.X - px) * (point.X - px))
                             + ((point.Y - py) * (point.Y - py))) <= offsetTol;
        }

        bool Belongs(Element elem)
        {
            (Vec3? q0, Vec3? q1, _) = SlopeEngine.GetAxisPoints(elem);
            if (q0 is null || q1 is null)
                return false;

            Vec3 qPlan = new(q1.Value.X - q0.Value.X, q1.Value.Y - q0.Value.Y, 0.0);
            double qLen = qPlan.GetLength();
            if (qLen < SlopeSolver.LenEps)
                return false;
            if (Math.Abs(q1.Value.Z - q0.Value.Z) > qLen * SlopeSolver.SteepRate)
                return false;
            if (Math.Abs(qPlan.Normalize().DotProduct(axis)) < cosTol)
                return false;

            return OnAxis(q0.Value) && OnAxis(q1.Value);
        }

        long seedId = RevitCompat.IdKey(seed.Id);
        Dictionary<long, Element> collected = new() { [seedId] = seed };
        HashSet<long> visited = [seedId];
        Stack<Element> frontier = new();
        frontier.Push(seed);

        while (frontier.Count > 0 && collected.Count < ExtendCap)
        {
            Element elem = frontier.Pop();

            foreach (Element neighbour in ConnectedAtEnds(elem))
            {
                long nid;
                try
                {
                    nid = RevitCompat.IdKey(neighbour.Id);
                }
                catch (Exception)
                {
                    continue;
                }

                if (!visited.Add(nid))
                    continue;

                if (SlopeEngine.IsFittingLike(neighbour))
                {
                    // Pass-through node: walk beyond it, never collect it.
                    frontier.Push(neighbour);
                }
                else if (Belongs(neighbour))
                {
                    collected[nid] = neighbour;
                    frontier.Push(neighbour);
                }
            }
        }

        return ([.. collected.Values], null);
    }

    /// <summary>
    /// The READ-ONLY half of solving a branch: follow the run out from the seed
    /// and build it. Everything here depends only on the seed, never on which
    /// end is anchored.
    ///
    /// Separated so a caller that has to inspect the run BEFORE it knows the
    /// reference — Slope Armover, which detects the tee end by walking both ends
    /// — can hand the result straight to <see cref="BuildJob"/> instead of
    /// making it follow the same run, rebuild the same segments and re-solve the
    /// same straight line a second time. That was pure duplicated work, once per
    /// armover picked.
    /// </summary>
    public static (RunScan? Scan, string? Reason) ScanRun(
        Document doc, Element seed, SlopeParams parameters)
    {
        (List<Element> elements, string? reason) = ExtendRun(seed, parameters);
        if (reason is not null)
            return (null, reason);

        (List<SlopeSegment> segments, Dictionary<long, Element> elemById,
         List<SprigEngine.Rejected> rejected) = SlopeEngine.BuildSegments(doc, elements);

        if (segments.Count == 0)
            return (null, "no usable pipe segment on this branch");

        (SlopeRun? run, List<(SlopeSegment Segment, string Reason)> runRejects) =
            SlopeSolver.BuildRun(segments, parameters);

        if (run is null)
        {
            return (null, "the pipes on this branch do not form one straight run - "
                          + "see the dropped picks below");
        }

        return (new RunScan(run, elemById, [.. run.Segments.Select(s => s.ElemId)],
                            rejected, runRejects), null);
    }

    /// <summary>One branch's run, already followed and built.</summary>
    internal sealed record RunScan(
        SlopeRun Run,
        Dictionary<long, Element> ElemById,
        HashSet<long> BranchIds,
        List<SprigEngine.Rejected> RejectedPicks,
        List<(SlopeSegment Segment, string Reason)> RejectedSegments);

    /// <summary>
    /// One job, solved as far as it will go. A job that fails carries its reason
    /// in <see cref="SlopeJob.Error"/> rather than throwing — one bad branch out
    /// of six should report itself, not abort the other five.
    /// </summary>
    public static SlopeJob BuildJob(
        Document doc, int index, Element seed, Element reference,
        SlopeParams parameters, SlopeGroupParams groupParams, RunScan? scan = null)
    {
        SlopeJob job = new(index, seed, reference);

        if (job.SeedId == job.ReferenceId)
        {
            job.Error = "the same element was picked as both the branch and the fixed reference";
            return job;
        }

        if (scan is null)
        {
            (scan, string? scanReason) = ScanRun(doc, seed, parameters);
            if (scan is null)
            {
                job.Error = scanReason;
                return job;
            }
        }

        SlopeRun run = scan.Run;
        Dictionary<long, Element> elemById = scan.ElemById;
        job.RejectedPicks = scan.RejectedPicks;
        job.RejectedSegments = scan.RejectedSegments;

        Vec3? anchorPoint = ReferencePoint(reference);
        if (anchorPoint is null)
        {
            job.Error = "the fixed reference has no position that can be read, so there is no "
                        + "way to tell which end of the run it is at";
            return job;
        }

        job.Run = run;

        // snapToEnd: false — the reference IS the feed. A cross main teeing in
        // mid-run anchors the run right there and the slope rises away from it in
        // both directions (a V), which live testing showed is exactly how a
        // mid-fed branch is modelled in the first place.
        job.Pitch = SlopeSolver.Solve(run, anchorPoint.Value, parameters, snapToEnd: false);
        job.BranchIds = scan.BranchIds;

        if (job.BranchIds.Contains(job.ReferenceId))
        {
            job.Error = "the fixed reference is one of the pipes on this branch, so it cannot "
                        + "both move and stay put - pick the cross main, or the tee into it, "
                        + "instead";
            job.Pitch = null;
            return job;
        }

        job.Elements =
        [
            .. run.Segments.Where(s => elemById.ContainsKey(s.ElemId))
                           .Select(s => elemById[s.ElemId])
        ];

        (job.Groups, job.Joints) = SlopeEngine.CollectGroups(
            job.Elements, job.BranchIds, run, job.Pitch, groupParams);

        PinReference(job);

        // The anchored end's own fitting is replaced fresh after the re-pitch so
        // it comes back at the new angle. Without this the pipe's tilted end face
        // leaves a visible sliver against the fitting's socket - the gap Revit's
        // own Trim never produces, because Trim makes Revit re-create the fitting
        // too.
        //
        // Exactly one of these applies: an elbow has two connectors, a tee three.
        job.FixedRebuild = Tools.SlopeThrough.PlanFixedRebuild(
            job.BranchIds, reference, job.ReferenceId);

        job.FixedTeeRebuild = job.FixedRebuild is null
            ? Tools.SlopeThrough.PlanFixedTeeRebuild(job.BranchIds, reference, job.ReferenceId)
            : null;

        return job;
    }

    /// <summary>
    /// Forces the group holding the reference element to never move.
    ///
    /// This is the whole point of naming a reference. The single-branch button
    /// has to INFER which side is the rest of the system from pipe lengths and
    /// element counts; here it has been stated outright, and an explicit answer
    /// beats a heuristic every time. It also means a branch whose armover or
    /// drop is long enough to trip the FOREIGN length test still gets pinned in
    /// the one place that actually matters.
    /// </summary>
    public static void PinReference(SlopeJob job)
    {
        foreach (SlopeGroup group in job.Groups)
        {
            if (!group.MemberIds.Contains(job.ReferenceId))
                continue;

            // Strip BEFORE clearing, or the leg's joint list keeps stale entries
            // pointing inside a group that will never move.
            job.Joints = SlopeGroupUtil.StripGroupJoints(job.Joints, group);

            group.Kind = GroupKind.Foreign;
            group.Rotate = false;
            group.Parts = [];
            group.InternalJoints = [];
            group.PinnedAsReference = true;
            group.AddNote($"holds element {job.ReferenceId}, which you picked as the FIXED "
                          + "reference for this branch - pinned, not guessed");
        }
    }
}
