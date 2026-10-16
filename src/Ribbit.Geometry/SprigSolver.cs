namespace Ribbit.Geometry;

/// <summary>Layout type for one head — decided by d, never by the user.</summary>
public enum SprigLayout
{
    Direct,
    Armover,
}

/// <summary>Per-head status, worst-wins.</summary>
public enum SprigStatus
{
    Ok,
    Warn,
    Skip,
}

/// <summary>
/// settings.as_feet() output, promoted from a string-keyed dict to a real type.
/// All values are in internal feet except <see cref="LeanWarnDeg"/>.
/// </summary>
public readonly record struct SprigParams(
    double DirectTol,
    double MinSprig,
    double MainMargin,
    double MaxArmover,
    double MinTeeSpacing,
    double LeanWarnDeg,
    double MaxDirectLeanDeg);

/// <summary>
/// One straight piece of the main run, precomputed for projection.
///
/// A segment that is vertical in plan (a riser) can't take a plan-projected
/// sprig at all, so it flags itself and Solve drops it — which conveniently
/// means the end riser can be included in the pick without breaking anything.
/// </summary>
public sealed class MainSegment
{
    public long ElemId { get; }
    public Vec3 P0 { get; }
    public Vec3 P1 { get; }

    /// <summary>
    /// What the element actually is, and whether the builder will be able to
    /// use Pipe-only APIs (BreakCurve, Pipe.Create) on it. The dry run solves
    /// the geometry either way.
    /// </summary>
    public string Desc { get; }
    public bool IsPipe { get; }

    public double Length { get; }
    public Vec3 Direction { get; }
    public double PlanLength { get; }
    public bool IsVertical { get; }
    public Vec3? PlanDirection { get; }

    /// <summary>Signed pitch in degrees off horizontal, measured along P0 -> P1.</summary>
    public double SlopeDeg { get; }

    public MainSegment(long elemId, Vec3 p0, Vec3 p1, string? desc = null, bool isPipe = true)
    {
        ElemId = elemId;
        P0 = p0;
        P1 = p1;
        Desc = desc ?? "Pipe";
        IsPipe = isPipe;

        Vec3 vec = p1.Subtract(p0);
        Length = vec.GetLength();
        Direction = Length > SprigSolver.LenEps ? vec.Normalize() : Vec3.BasisX;

        Vec3 plan = new(p1.X - p0.X, p1.Y - p0.Y, 0.0);
        PlanLength = plan.GetLength();
        IsVertical = PlanLength < SprigSolver.LenEps;
        PlanDirection = IsVertical ? null : plan.Normalize();

        SlopeDeg = Length > SprigSolver.LenEps
            ? Math.Asin((p1.Z - p0.Z) / Length) * 180.0 / Math.PI
            : 0.0;
    }

    public readonly record struct Projection(
        double Station, Vec3 TeePt, double PlanDist, bool Inside);

    /// <summary>
    /// Plan-projects the point onto this segment. Null for a vertical segment.
    ///
    /// Station is the distance along the segment's own 3D length from P0;
    /// TeePt is the tee location with Z interpolated along the pitch; PlanDist
    /// is the leftover plan distance d; Inside is false when the head lands off
    /// either end (Station is clamped to the segment in that case).
    /// </summary>
    public Projection? Project(Vec3 point)
    {
        if (IsVertical || PlanDirection is null)
            return null;

        Vec3 toPt = new(point.X - P0.X, point.Y - P0.Y, 0.0);
        double t = PlanDirection.Value.DotProduct(toPt);

        bool inside = t >= 0.0 && t <= PlanLength;
        double tClamped = Math.Max(0.0, Math.Min(t, PlanLength));

        // Interpolating the full 3D vector (not just the plan one) is what
        // carries the main's pitch into the tee elevation.
        double frac = tClamped / PlanLength;
        Vec3 teePt = P0.Add(P1.Subtract(P0).Multiply(frac));

        double dx = point.X - teePt.X;
        double dy = point.Y - teePt.Y;
        double planDist = Math.Sqrt(dx * dx + dy * dy);

        return new Projection(frac * Length, teePt, planDist, inside);
    }

    // Foot() lived here — a 3D perpendicular foot, documented as producing "the
    // one takeoff direction NewTeeFitting accepts". Nothing ever called it. The
    // armover already gets a perpendicular takeoff for free: its elbow sits at
    // the tee's own Z with the head's plan XY, so the arm is horizontal and
    // square to the branch axis at any pitch. Deleted rather than left as a
    // plausible-looking helper someone might reach for instead.
}

/// <summary>
/// Everything the builder would need for one head, plus why it might not be
/// buildable. Nothing here is applied to the model.
/// </summary>
public sealed class HeadPlan(long headId, Vec3 headPt)
{
    public long HeadId { get; } = headId;
    public Vec3 HeadPt { get; } = headPt;

    public SprigLayout? Kind { get; internal set; }
    public MainSegment? Segment { get; internal set; }
    public int? SegmentIndex { get; internal set; }
    public double? Station { get; internal set; }
    public Vec3? TeePt { get; internal set; }

    /// <summary>Null for Direct.</summary>
    public Vec3? ElbowPt { get; internal set; }

    /// <summary>The plan distance d.</summary>
    public double Offset { get; internal set; }

    public double ArmoverLength { get; internal set; }
    public double SprigLength { get; internal set; }

    /// <summary>Direct sprig off plumb; 0 for Armover.</summary>
    public double LeanDeg { get; internal set; }

    public List<(SprigStatus Level, string Message)> Issues { get; } = [];
    public SprigStatus Status { get; private set; } = SprigStatus.Ok;

    /// <summary>Worst-wins: once a head is Skip it stays Skip.</summary>
    public void Add(SprigStatus level, string message)
    {
        Issues.Add((level, message));
        if (Status == SprigStatus.Skip)
            return;
        if (level == SprigStatus.Skip)
            Status = SprigStatus.Skip;
        else if (level == SprigStatus.Warn)
            Status = SprigStatus.Warn;
    }

    public string IssueText() => string.Join("; ", Issues.Select(i => i.Message));
}

/// <summary>
/// Sprig Connect — geometry solver. Ported from lib/spriglib/solver.py.
///
/// Pure geometry. Nothing in here touches the document, opens a Transaction, or
/// creates anything: it turns "these heads, that main run" into a full list of
/// intended tees/armovers/elbows/sprigs with numbers attached, so the whole
/// layout can be reported and eyeballed BEFORE a single element is built.
///
/// WHICH LAYOUTS ARE PLUMB. An ARMOVER sprig is plumb by construction: the
/// elbow sits at the head's own plan XY, so the vertical pipe rises dead
/// straight. A DIRECT sprig is NOT — its tee lands at the perpendicular foot on
/// the branch, and the pipe leans across whatever plan offset is left to reach
/// the head.
///
/// This doc used to claim the opposite ("sprigs are built plumb… the tee is off
/// square by the pitch"), describing an intent the code never implemented — a
/// tee cannot sit under a head that is off the branch line, because the tee has
/// to be ON the pipe. Measured on a LEVEL branch at the shipped defaults, a head
/// 300 mm off with a 300 mm rise came out at 45 degrees off plumb.
///
/// <see cref="SprigParams.MaxDirectLeanDeg"/> is what keeps that honest: past
/// that angle the head is routed to an armover instead, however small its plan
/// offset. Deciding on plan distance alone made a head 300 mm off get a 45
/// degree sprig while one 350 mm off got a clean armover — nearer geometry
/// producing a worse result.
/// </summary>
public static class SprigSolver
{
    public const double LenEps = 1.0e-9;

    /// <summary>
    /// An armover longer than this is long enough that "you picked the wrong
    /// branch line" is a plausible explanation. Below it, exceeding the user's
    /// configured limit just means the limit is tight.
    /// </summary>
    public static readonly double WrongBranchHintFt = 3000.0 / Units.MmPerFt;

    /// <summary>
    /// Smallest pipe stub the builder will leave either side of a tee. A tee
    /// needs SOMETHING to break into on both sides, but a fixed 30 mm margin
    /// rejected every head on any pipe shorter than 60 mm. This floor is what
    /// "genuinely too short for any fitting" means; above it the margin adapts
    /// to the pipe (see <see cref="EffectiveMargin"/>).
    /// </summary>
    public static readonly double MarginFloorFt = 6.0 / Units.MmPerFt;

    /// <summary>
    /// Two heads closer together than this are the SAME sprinkler modelled
    /// twice, not two real heads — no sprinkler layout puts two heads 50 mm
    /// apart. Deliberately generous: a genuine pair is metres apart.
    /// </summary>
    public static readonly double CoincidentHeadTolFt = 50.0 / Units.MmPerFt;

    /// <summary>
    /// How far off plumb a DIRECT sprig would come out: the head's plan offset
    /// against the height it has to climb.
    ///
    /// This is the quantity that actually matters, and it is NOT what
    /// <see cref="SprigParams.DirectTol"/> measures. The same 300 mm offset is
    /// 45 degrees on a 300 mm sprig and 17 degrees on a 1000 mm one.
    /// </summary>
    public static double DirectLeanDeg(double planDist, double rise)
        => Math.Abs(rise) < LenEps
            ? 90.0
            : Math.Atan2(Math.Abs(planDist), Math.Abs(rise)) * 180.0 / Math.PI;

    /// <summary>
    /// Room-to-break margin actually applied on a pipe of this length.
    ///
    /// On a long pipe this is just the configured margin. On a SHORT pipe the
    /// configured margin would leave no usable window at all (two 30 mm margins
    /// overlap below 60 mm), so it shrinks to keep a central band, never below
    /// <see cref="MarginFloorFt"/>. Null when the pipe is too short to take any
    /// fitting even at the floor.
    /// </summary>
    public static double? EffectiveMargin(double length, double configured)
    {
        if (length <= 2.0 * MarginFloorFt)
            return null;
        return Math.Min(configured, length * 0.5 - MarginFloorFt);
    }

    /// <summary>
    /// Returns the plans sorted into main-run order (segment, then station), so
    /// the report reads along the pipe the way the run does.
    /// </summary>
    public static List<HeadPlan> Solve(
        IReadOnlyList<(long HeadId, Vec3 HeadPt)> heads,
        IReadOnlyList<MainSegment> segments,
        SprigParams parameters)
    {
        List<MainSegment> usable =
            [.. segments.Where(s => !s.IsVertical && s.Length > LenEps)];

        List<HeadPlan> plans =
            [.. heads.Select(h => SolveOne(h.HeadId, h.HeadPt, usable, parameters))];

        CheckDuplicateHeads(plans);
        CheckTeeSpacing(plans, parameters);

        plans.Sort((a, b) =>
        {
            int ai = a.SegmentIndex ?? int.MaxValue;
            int bi = b.SegmentIndex ?? int.MaxValue;
            if (ai != bi)
                return ai.CompareTo(bi);
            return (a.Station ?? 0.0).CompareTo(b.Station ?? 0.0);
        });

        return plans;
    }

    /// <summary>
    /// SKIP heads that sit on top of each other — duplicate sprinklers in the
    /// model, not a layout. ALL of the pair is skipped, not just the extras,
    /// because nothing here can tell which instance is the real one.
    /// </summary>
    public static void CheckDuplicateHeads(IReadOnlyList<HeadPlan> plans)
    {
        for (int i = 0; i < plans.Count; i++)
        {
            for (int j = i + 1; j < plans.Count; j++)
            {
                HeadPlan a = plans[i];
                HeadPlan b = plans[j];

                double gap = a.HeadPt.Subtract(b.HeadPt).GetLength();
                if (gap > CoincidentHeadTolFt)
                    continue;

                a.Add(SprigStatus.Skip,
                    $"Head {b.HeadId} sits {Units.Mm(gap)} away - they are the same sprinkler "
                    + "modelled twice. Delete the duplicate, then re-run");
                b.Add(SprigStatus.Skip,
                    $"Head {a.HeadId} sits {Units.Mm(gap)} away - they are the same sprinkler "
                    + "modelled twice. Delete the duplicate, then re-run");
            }
        }
    }

    public static HeadPlan SolveOne(
        long headId, Vec3 headPt, IReadOnlyList<MainSegment> segments, SprigParams parameters)
    {
        HeadPlan plan = new(headId, headPt);

        if (segments.Count == 0)
        {
            plan.Add(SprigStatus.Skip,
                "No usable main segment - every picked main is vertical or zero-length");
            return plan;
        }

        // Prefer a segment the head actually sits along; fall back to the
        // nearest one so an off-run head is still reported with real numbers
        // instead of silently disappearing.
        int bestIdx = -1;
        MainSegment? bestSeg = null;
        MainSegment.Projection bestProj = default;
        (int InsideRank, double PlanDist) bestRank = default;

        for (int idx = 0; idx < segments.Count; idx++)
        {
            MainSegment.Projection? proj = segments[idx].Project(headPt);
            if (proj is null)
                continue;

            (int, double) rank = (proj.Value.Inside ? 0 : 1, proj.Value.PlanDist);
            if (bestSeg is null
                || rank.Item1 < bestRank.InsideRank
                || (rank.Item1 == bestRank.InsideRank && rank.Item2 < bestRank.PlanDist))
            {
                bestRank = rank;
                bestIdx = idx;
                bestSeg = segments[idx];
                bestProj = proj.Value;
            }
        }

        if (bestSeg is null)
        {
            plan.Add(SprigStatus.Skip, "Could not project the head onto any main segment");
            return plan;
        }

        plan.Segment = bestSeg;
        plan.SegmentIndex = bestIdx;
        plan.Station = bestProj.Station;
        plan.TeePt = bestProj.TeePt;
        plan.Offset = bestProj.PlanDist;

        // TWO tests, not one. The plan offset says the head is near enough to
        // the branch to consider going straight up; the lean says whether going
        // straight up would produce a pipe anyone would accept. A head can pass
        // the first and fail the second badly - 300 mm off a 300 mm rise is 45
        // degrees - and before the second test existed it was built anyway.
        // 0 disables the cap, the same "0 means no limit" convention as
        // MaxArmover.
        double directRise = headPt.Z - bestProj.TeePt.Z;
        bool leanAcceptable =
            parameters.MaxDirectLeanDeg <= 0.0
            || DirectLeanDeg(bestProj.PlanDist, directRise) <= parameters.MaxDirectLeanDeg;

        if (bestProj.PlanDist <= parameters.DirectTol && leanAcceptable)
        {
            plan.Kind = SprigLayout.Direct;
            // The tee is the perpendicular foot on the branch, so the sprig
            // leans across whatever plan offset is left. LeanDeg below measures
            // exactly that; the cap above bounds it.
            Vec3 sprigVec = headPt.Subtract(bestProj.TeePt);
            plan.SprigLength = sprigVec.GetLength();
            if (plan.SprigLength > LenEps)
                plan.LeanDeg = sprigVec.Normalize().AngleTo(Vec3.BasisZ) * 180.0 / Math.PI;
        }
        else
        {
            plan.Kind = SprigLayout.Armover;
            // Horizontal by construction: same Z as the tee, head's plan XY.
            // Already exactly perpendicular to the main's 3D axis, at any pitch.
            Vec3 elbowPt = new(headPt.X, headPt.Y, bestProj.TeePt.Z);
            plan.ElbowPt = elbowPt;
            plan.ArmoverLength = bestProj.PlanDist;
            plan.SprigLength = headPt.Z - elbowPt.Z;
        }

        Validate(plan, bestSeg, bestProj.Station, bestProj.Inside, parameters);
        return plan;
    }

    private static void Validate(
        HeadPlan plan, MainSegment seg, double station, bool inside, SprigParams parameters)
    {
        if (!inside)
        {
            // Everything downstream is measured from a tee point that got
            // clamped to the segment end, so the armover/deviation numbers would
            // be meaningless. Report the one real reason and stop.
            plan.Add(SprigStatus.Skip,
                "Head is off the end of the main run - the nearest point on the main is a "
                + "segment endpoint");
            return;
        }

        // Two genuinely different failures, kept apart on purpose: one is
        // geometry (the head is under the main), the other is a threshold the
        // user set. Checked on head-vs-tee elevation rather than on SprigLength,
        // which for a DIRECT sprig is a 3D vector length and so is never
        // negative however far under the main the head sits.
        if (plan.HeadPt.Z <= plan.TeePt!.Value.Z + LenEps)
        {
            plan.Add(SprigStatus.Skip,
                "Head is at or below the main - an upright sprig has to come UP to the head");
            return;
        }

        if (plan.SprigLength < parameters.MinSprig)
        {
            plan.Add(SprigStatus.Skip,
                $"Sprig is {Units.Mm(plan.SprigLength)}, under the {Units.Mm(parameters.MinSprig)} "
                + "'minimum sprig length' SETTING - the geometry is fine, the setting is "
                + "rejecting it");
        }

        // Same idea as EffectiveMargin on the tee: an armover this short has no
        // room to take the elbow at its end. Without this, a stub armover
        // reaches the builder and NewElbowFitting fails there instead, rolling
        // back the whole batch with a message about routing preferences rather
        // than about the actual cause.
        if (plan.Kind == SprigLayout.Armover && plan.ArmoverLength < MarginFloorFt)
        {
            plan.Add(SprigStatus.Skip,
                $"Armover would be {Units.Mm(plan.ArmoverLength)} - too short to take the elbow "
                + $"at its end (needs at least {Units.Mm(MarginFloorFt)}). If this keeps "
                + "happening, 'Direct-sprig threshold' may be set too tight, sending near-in-line "
                + "heads down the armover path with barely any offset to work with");
        }

        double? margin = EffectiveMargin(seg.Length, parameters.MainMargin);
        if (margin is null)
        {
            plan.Add(SprigStatus.Skip,
                $"Branch pipe is only {Units.Mm(seg.Length)} long - too short to insert a "
                + "fitting at all");
        }
        else if (station < margin)
        {
            plan.Add(SprigStatus.Skip,
                $"Tee lands {Units.Mm(station)} from the start of the pipe - no room to break it "
                + $"(needs {Units.Mm(margin)} of stub)");
        }
        else if (station > seg.Length - margin)
        {
            plan.Add(SprigStatus.Skip,
                $"Tee lands {Units.Mm(seg.Length - station)} from the end of the pipe - no room "
                + $"to break it (needs {Units.Mm(margin)} of stub)");
        }

        // 0 means "no limit" - the usual convention, and far kinder than
        // flagging every single armover as too long.
        double maxArmover = parameters.MaxArmover;
        if (plan.Kind == SprigLayout.Armover && maxArmover > 0.0
            && plan.ArmoverLength > maxArmover)
        {
            // Only suggest the wrong-branch explanation when the armover is long
            // in ABSOLUTE terms. An 800 mm armover that trips a 300 mm limit is a
            // tight setting, not a mis-picked branch line.
            if (plan.ArmoverLength > WrongBranchHintFt)
            {
                plan.Add(SprigStatus.Warn,
                    $"Armover is {Units.Mm(plan.ArmoverLength)} - longer than the "
                    + $"{Units.Mm(maxArmover)} limit, and long enough that this head may belong "
                    + "to a different branch line");
            }
            else
            {
                plan.Add(SprigStatus.Warn,
                    $"Armover is {Units.Mm(plan.ArmoverLength)} - longer than the "
                    + $"{Units.Mm(maxArmover)} limit SETTING; the geometry itself looks reasonable");
            }
        }

        if (plan.LeanDeg > parameters.LeanWarnDeg)
        {
            plan.Add(SprigStatus.Warn,
                // The old wording blamed the main's pitch. It is not the cause:
                // this fires on a dead level branch, and pitch only enters
                // through the rise. The head being off the branch centreline is
                // the whole of it - which is what the sibling message in
                // DropSolver has always said.
                $"Sprig leans {plan.LeanDeg:F2} deg off plumb - the head sits off the branch "
                + "centreline, so its sprig must reach sideways to meet it");
        }
    }

    /// <summary>
    /// Two tees too close together on the same segment leave no room for both
    /// fittings. Both sides of an offending pair are flagged, naming each other
    /// — which one to move is the modeller's call, not ours.
    /// </summary>
    private static void CheckTeeSpacing(IReadOnlyList<HeadPlan> plans, SprigParams parameters)
    {
        double minSpacing = parameters.MinTeeSpacing;

        Dictionary<int, List<HeadPlan>> bySegment = [];
        foreach (HeadPlan plan in plans)
        {
            if (plan.SegmentIndex is null || plan.Status == SprigStatus.Skip)
                continue;
            if (!bySegment.TryGetValue(plan.SegmentIndex.Value, out List<HeadPlan>? group))
                bySegment[plan.SegmentIndex.Value] = group = [];
            group.Add(plan);
        }

        foreach (List<HeadPlan> group in bySegment.Values)
        {
            group.Sort((a, b) => (a.Station ?? 0.0).CompareTo(b.Station ?? 0.0));
            for (int i = 0; i < group.Count - 1; i++)
            {
                HeadPlan prev = group[i];
                HeadPlan curr = group[i + 1];
                double gap = (curr.Station ?? 0.0) - (prev.Station ?? 0.0);
                if (gap >= minSpacing)
                    continue;

                prev.Add(SprigStatus.Warn,
                    $"Tee is only {Units.Mm(gap)} from head {curr.HeadId}'s tee "
                    + $"(minimum spacing is {Units.Mm(minSpacing)})");
                curr.Add(SprigStatus.Warn,
                    $"Tee is only {Units.Mm(gap)} from head {prev.HeadId}'s tee "
                    + $"(minimum spacing is {Units.Mm(minSpacing)})");
            }
        }
    }

    /// <summary>
    /// Counts for the report footer. 'Ok' is hypothetical — nothing is built.
    /// </summary>
    public static (int Ok, int Warn, int Skip, int Direct, int Armover) Summarize(
        IReadOnlyList<HeadPlan> plans)
    {
        int ok = plans.Count(p => p.Status == SprigStatus.Ok);
        int warn = plans.Count(p => p.Status == SprigStatus.Warn);
        int skip = plans.Count(p => p.Status == SprigStatus.Skip);
        int direct = plans.Count(p => p.Kind == SprigLayout.Direct && p.Status != SprigStatus.Skip);
        int armover = plans.Count(p => p.Kind == SprigLayout.Armover && p.Status != SprigStatus.Skip);
        return (ok, warn, skip, direct, armover);
    }
}
