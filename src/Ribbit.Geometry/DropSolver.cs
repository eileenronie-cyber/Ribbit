namespace Ribbit.Geometry;

/// <summary>
/// Layout type for one head. Direct vs ReturnBend is decided by offset d;
/// EndBend is decided by the head sitting in line with the branch but BEYOND an
/// open end of it (a head at the very end of the run, fed by an elbow onto the
/// branch end rather than a tee into its middle).
/// </summary>
public enum DropLayout
{
    Direct,
    ReturnBend,
    EndBend,
}

/// <summary>settings.as_feet() output, promoted from a string-keyed dict.</summary>
public readonly record struct DropParams(
    double DirectTol,
    double MinDrop,
    double MainMargin,
    double MaxArmover,
    double MinTeeSpacing,
    double LeanWarnDeg,
    double RiserHeight,
    double HangerWarn,
    double Clearance,
    bool AllowDirect,
    double MaxDirectLeanDeg);

/// <summary>
/// One straight piece of the branch run, precomputed for projection.
///
/// Distinct from <see cref="MainSegment"/> (Sprig's) because a drop needs two
/// things a sprig never does: the branch's real diameter, for the
/// pipe-through-pipe clearance checks, and whether each end has a FREE
/// connector, since only an open end can take an END BEND. The Python keeps two
/// separate classes for the same reason.
/// </summary>
public sealed class DropSegment
{
    public long ElemId { get; }
    public Vec3 P0 { get; }
    public Vec3 P1 { get; }

    /// <summary>
    /// Whether each end has a FREE connector. Only an open end can take an END
    /// BEND, which elbows straight onto the end of the run instead of teeing
    /// into the middle of it.
    /// </summary>
    public bool OpenP0 { get; }
    public bool OpenP1 { get; }

    public string Desc { get; }
    public bool IsPipe { get; }

    /// <summary>
    /// Outside diameter proxy for the clearance checks; 0 means "unknown",
    /// which skips those checks rather than guessing.
    /// </summary>
    public double Diameter { get; }

    public double Length { get; }
    public Vec3 Direction { get; }
    public double PlanLength { get; }
    public bool IsVertical { get; }
    public Vec3? PlanDirection { get; }
    public double SlopeDeg { get; }

    public DropSegment(
        long elemId, Vec3 p0, Vec3 p1, string? desc = null, bool isPipe = true,
        double diameter = 0.0, bool openP0 = false, bool openP1 = false)
    {
        ElemId = elemId;
        P0 = p0;
        P1 = p1;
        OpenP0 = openP0;
        OpenP1 = openP1;
        Desc = desc ?? "Pipe";
        IsPipe = isPipe;
        Diameter = diameter;

        Vec3 vec = p1.Subtract(p0);
        Length = vec.GetLength();
        Direction = Length > DropSolver.LenEps ? vec.Normalize() : Vec3.BasisX;

        Vec3 plan = new(p1.X - p0.X, p1.Y - p0.Y, 0.0);
        PlanLength = plan.GetLength();
        IsVertical = PlanLength < DropSolver.LenEps;
        PlanDirection = IsVertical ? null : plan.Normalize();

        SlopeDeg = Length > DropSolver.LenEps
            ? Math.Asin((p1.Z - p0.Z) / Length) * 180.0 / Math.PI
            : 0.0;
    }

    public readonly record struct Projection(
        double Station, Vec3 RefPt, double PlanDist, bool Inside);

    /// <summary>
    /// Plan-projects the point onto this segment. Null for a vertical segment.
    /// RefPt is the plan-projected point ON the axis, Z interpolated along the
    /// pitch.
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
        // carries the branch's pitch into the reference elevation.
        double frac = tClamped / PlanLength;
        Vec3 refPt = P0.Add(P1.Subtract(P0).Multiply(frac));

        double dx = point.X - refPt.X;
        double dy = point.Y - refPt.Y;
        return new Projection(frac * Length, refPt, Math.Sqrt(dx * dx + dy * dy), inside);
    }

    /// <summary>3D perpendicular foot of the point on this segment's axis.</summary>
    public (double Station, Vec3 FootPt, bool Inside) Foot(Vec3 point)
    {
        double t = point.Subtract(P0).DotProduct(Direction);
        bool inside = t >= 0.0 && t <= Length;
        double tClamped = Math.Max(0.0, Math.Min(t, Length));
        return (tClamped, P0.Add(Direction.Multiply(tClamped)), inside);
    }

    /// <summary>
    /// Perpendicular plan distance from the point to this segment's INFINITE
    /// axis line — unclamped, so it stays small for a head that is in line with
    /// the run but past its end. Project's PlanDist is measured to the clamped
    /// endpoint instead, which is why the END BEND test needs this one.
    /// </summary>
    public double? PlanLateral(Vec3 point)
    {
        if (IsVertical || PlanDirection is null)
            return null;

        Vec3 toPt = new(point.X - P0.X, point.Y - P0.Y, 0.0);
        double t = PlanDirection.Value.DotProduct(toPt);
        Vec3 foot = PlanDirection.Value.Multiply(t);
        return new Vec3(toPt.X - foot.X, toPt.Y - foot.Y, 0.0).GetLength();
    }

    /// <summary>
    /// The OPEN end this point projects beyond, or null.
    ///
    /// Only returns an end when the point's plan projection lands past that end
    /// AND that end has a free connector — the two conditions an END BEND needs.
    /// OutDir is the branch's plan direction pointing OUT of the run, i.e. the
    /// way the armover will run.
    /// </summary>
    public (Vec3 EndPt, Vec3 OutDir)? OpenEndToward(Vec3 point)
    {
        if (PlanDirection is null)
            return null;

        double t = point.Subtract(P0).DotProduct(Direction);
        if (t < 0.0 && OpenP0)
            return (P0, PlanDirection.Value.Negate());
        if (t > Length && OpenP1)
            return (P1, PlanDirection.Value);
        return null;
    }
}

/// <summary>
/// Everything the builder would need for one head, plus why it might not be
/// buildable. Nothing here is applied to the model.
/// </summary>
public sealed class DropHeadPlan(long headId, Vec3 headPt, double dia = 0.0)
{
    public long HeadId { get; } = headId;
    public Vec3 HeadPt { get; } = headPt;

    /// <summary>
    /// Size of every new pipe for this head — riser, armover and drop alike.
    /// Read from the head's own inlet connector rather than set, so it also
    /// feeds the pipe-through-pipe clearance checks.
    /// </summary>
    public double Dia { get; } = dia;

    public DropLayout? Kind { get; internal set; }
    public DropSegment? Segment { get; internal set; }
    public int? SegmentIndex { get; internal set; }

    /// <summary>Distance along that segment to the TEE. Null for an end bend.</summary>
    public double? Station { get; internal set; }

    /// <summary>T — on the branch axis (tee/elbow root).</summary>
    public Vec3? TeePt { get; internal set; }

    /// <summary>E — null for Direct.</summary>
    public Vec3? RiserTop { get; internal set; }

    /// <summary>F — top of the drop; null for Direct.</summary>
    public Vec3? OverPt { get; internal set; }

    /// <summary>The plan distance d.</summary>
    public double Offset { get; internal set; }

    public double RiserLength { get; internal set; }
    public double ArmoverLength { get; internal set; }
    public double DropLength { get; internal set; }

    /// <summary>Riser (ReturnBend) or drop (Direct) off plumb.</summary>
    public double LeanDeg { get; internal set; }

    /// <summary>
    /// END BEND only: the elbow roots on one of the branch's open ends.
    /// <see cref="TeePt"/> holds that end point — no tee is cut, the builder
    /// elbows onto the existing open connector there.
    /// </summary>
    public bool EndBend { get; internal set; }

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
/// Drop Connect — geometry solver. Ported from lib/droplib/solver.py.
///
/// Pure geometry: turns "these pendent heads, that branch line" into a full list
/// of intended tees/risers/armovers/drops with numbers attached, so the whole
/// layout can be reported and eyeballed BEFORE a single element is built.
///
/// BOTH VERTICAL PIPES ARE BUILT PLUMB — the riser off the branch AND the drop
/// to the sprinkler must both read dead vertical. Every tee therefore sits at
/// the head's PLAN PROJECTION on the branch, and the vertical pipes run straight
/// up/down. THE TRADE-OFF: on a PITCHED branch that makes the tee off square by
/// exactly the pitch. At drainage pitch (~0.12 deg) that is a fraction of a
/// degree and NewTeeFitting takes it. This is the deliberate opposite of the
/// earlier "true-90 tee, leaning riser" geometry, which was rejected because the
/// vertical pipe came out sloped.
///
/// CLEARANCES are checked, never silently fixed: the return-bend drop runs down
/// PAST the branch at plan offset d, and the armover runs over the TOP of it at
/// the riser height. Moving the head or raising the riser is the modeller's call.
/// </summary>
public static class DropSolver
{
    public const double LenEps = 1.0e-9;

    /// <summary>See <see cref="SprigSolver.WrongBranchHintFt"/> — same rationale.</summary>
    public static readonly double WrongBranchHintFt = 3000.0 / Units.MmPerFt;

    public static readonly double MarginFloorFt = 6.0 / Units.MmPerFt;

    public static readonly double CoincidentHeadTolFt = 50.0 / Units.MmPerFt;

    /// <summary>See <see cref="SprigSolver.EffectiveMargin"/> — same rule.</summary>
    public static double? EffectiveMargin(double length, double configured)
    {
        if (length <= 2.0 * MarginFloorFt)
            return null;
        return Math.Min(configured, length * 0.5 - MarginFloorFt);
    }

    /// <summary>
    /// Heads are (id, connector origin, connector diameter). Returns the plans
    /// sorted into branch-run order (segment, then station).
    /// </summary>
    public static List<DropHeadPlan> Solve(
        IReadOnlyList<(long HeadId, Vec3 HeadPt, double Dia)> heads,
        IReadOnlyList<DropSegment> segments,
        DropParams parameters)
    {
        List<DropSegment> usable =
            [.. segments.Where(s => !s.IsVertical && s.Length > LenEps)];

        List<DropHeadPlan> plans =
            [.. heads.Select(h => SolveOne(h.HeadId, h.HeadPt, h.Dia, usable, parameters))];

        CheckDuplicateHeads(plans);
        CheckSharedEndBends(plans);
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
    /// SKIP heads that sit on top of each other. ALL of the pair is skipped, not
    /// just the extras, because nothing here can tell which instance is real.
    /// </summary>
    public static void CheckDuplicateHeads(IReadOnlyList<DropHeadPlan> plans)
    {
        for (int i = 0; i < plans.Count; i++)
        {
            for (int j = i + 1; j < plans.Count; j++)
            {
                DropHeadPlan a = plans[i];
                DropHeadPlan b = plans[j];

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

    /// <summary>
    /// An open end takes ONE end bend: the elbow put on it uses the end up. When
    /// several heads past the same end were all planned as end bends, the dry run
    /// promised every one, and the builder then failed on the second and rolled
    /// back the WHOLE run - valid heads elsewhere on the branch included (seen in
    /// the in-Revit suite). The head nearest the end keeps it; the others are
    /// skipped, naming the head that has it.
    /// </summary>
    public static void CheckSharedEndBends(IReadOnlyList<DropHeadPlan> plans)
    {
        List<DropHeadPlan> endBends =
            [.. plans.Where(p => p.EndBend && p.TeePt is not null && p.Status != SprigStatus.Skip)];

        while (endBends.Count > 0)
        {
            Vec3 end = endBends[0].TeePt!.Value;
            List<DropHeadPlan> sameEnd =
                [.. endBends.Where(p => p.TeePt!.Value.DistanceTo(end) < SameEndTolFt)
                            .OrderBy(p => p.ArmoverLength)];

            DropHeadPlan keeper = sameEnd[0];
            foreach (DropHeadPlan other in sameEnd.Skip(1))
            {
                other.Add(SprigStatus.Skip,
                    $"Head {keeper.HeadId} already takes this open end's end bend (it sits nearer "
                    + "the end) - an open end can take only one. Pipe this head off the run another "
                    + "way, or extend the branch past it");
            }

            endBends.RemoveAll(p => sameEnd.Contains(p));
        }
    }

    /// <summary>Two end-bend roots closer than this are the same open end.</summary>
    private static readonly double SameEndTolFt = 1.0 / Units.MmPerFt;

    public static DropHeadPlan SolveOne(
        long headId, Vec3 headPt, double dia,
        IReadOnlyList<DropSegment> segments, DropParams parameters)
    {
        DropHeadPlan plan = new(headId, headPt, dia);

        if (segments.Count == 0)
        {
            plan.Add(SprigStatus.Skip,
                "No usable branch segment - every picked element is vertical or zero-length");
            return plan;
        }

        int bestIdx = -1;
        DropSegment? bestSeg = null;
        DropSegment.Projection bestProj = default;
        (int InsideRank, double PlanDist) bestRank = default;

        for (int idx = 0; idx < segments.Count; idx++)
        {
            DropSegment.Projection? proj = segments[idx].Project(headPt);
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
            plan.Add(SprigStatus.Skip, "Could not project the head onto any branch segment");
            return plan;
        }

        plan.Segment = bestSeg;
        plan.SegmentIndex = bestIdx;
        plan.Offset = bestProj.PlanDist;

        if (!bestProj.Inside)
        {
            // A head off the end IS the END BEND case - as long as it is in LINE
            // with the branch and the end it runs past has an open connector to
            // elbow onto. "In line" is the perpendicular offset to the axis, NOT
            // Project's offset (which is the distance to the clamped endpoint
            // and so is large for a head beyond the end).
            double? lateral = bestSeg.PlanLateral(headPt);
            (Vec3 EndPt, Vec3 OutDir)? end = bestSeg.OpenEndToward(headPt);
            bool inLine = lateral is not null && lateral <= parameters.DirectTol;

            if (inLine && end is not null)
            {
                SolveEndBend(plan, bestSeg, headPt, end.Value, parameters);
                Validate(plan, bestSeg, parameters);
                return plan;
            }

            if (inLine)
            {
                plan.Add(SprigStatus.Skip,
                    "Head sits past the end of the branch run, but that end has no open connector "
                    + "to elbow onto - an end bend needs a free pipe end");
                return plan;
            }

            plan.Add(SprigStatus.Skip,
                "Head is off the end of the branch run - the nearest point on the branch is a "
                + "segment endpoint");
            return plan;
        }

        // Same two-part test as SprigSolver: near enough in plan to consider a
        // straight drop, AND the straight drop would not come out absurdly off
        // plumb. DirectTol limits the input; MaxDirectLeanDeg limits the result,
        // which is the part anyone actually sees. 0 disables the cap.
        double directDrop = bestProj.RefPt.Z - headPt.Z;
        bool leanAcceptable =
            parameters.MaxDirectLeanDeg <= 0.0
            || SprigSolver.DirectLeanDeg(bestProj.PlanDist, directDrop) <= parameters.MaxDirectLeanDeg;

        if (bestProj.PlanDist <= parameters.DirectTol && leanAcceptable)
        {
            if (parameters.AllowDirect)
            {
                SolveDirect(plan, bestSeg, headPt, parameters);
            }
            else
            {
                plan.Add(SprigStatus.Skip,
                    "Head sits directly under the branch (a straight drop), but DIRECT drops are "
                    + "turned off - this detail isn't used here. Only return bends and end bends "
                    + "are built");
                plan.Kind = DropLayout.Direct;
                return plan;
            }
        }
        else
        {
            SolveReturnBend(plan, bestSeg, headPt, bestProj.RefPt, parameters);
        }

        Validate(plan, bestSeg, parameters);
        return plan;
    }

    /// <summary>
    /// Elbow off the OPEN END of the branch, over, and plumb down to the head —
    /// the pendent-at-the-end-of-branch detail. Three elbows, no tee. No
    /// BreakCurve and no tee means the true-90 tee constraint never applies here.
    /// </summary>
    private static void SolveEndBend(
        DropHeadPlan plan, DropSegment seg, Vec3 headPt,
        (Vec3 EndPt, Vec3 OutDir) end, DropParams parameters)
    {
        plan.Kind = DropLayout.EndBend;
        plan.EndBend = true;

        plan.TeePt = end.EndPt;   // elbow root, not a real tee

        // For an end bend "offset d" is the lateral misalignment from the branch
        // axis - near zero when the head is truly in line.
        plan.Offset = seg.PlanLateral(headPt) ?? 0.0;

        Vec3 riserTop = new(end.EndPt.X, end.EndPt.Y, end.EndPt.Z + parameters.RiserHeight);
        plan.RiserTop = riserTop;
        plan.RiserLength = parameters.RiserHeight;

        // Armover runs along the branch's own plan direction, out past the end,
        // to sit directly over the head. Its length is derived, never asked for.
        Vec3 overPt = new(headPt.X, headPt.Y, riserTop.Z);
        plan.OverPt = overPt;
        plan.ArmoverLength =
            new Vec3(overPt.X - riserTop.X, overPt.Y - riserTop.Y, 0.0).GetLength();

        // SIGNED, matching ReturnBend. On a valid pendent drop over_pt is ABOVE
        // the head, so this is positive; a head at or above the armover comes out
        // <= 0 and is caught below.
        //
        // The Python used drop_vec.GetLength() here - a magnitude, which is never
        // negative - so its "head is at or above the armover" check could only
        // ever fire on an exactly coincident point. A head sitting ABOVE the
        // armover passed as a valid end bend with a positive drop, and the
        // builder would then run a "drop" pipe UPWARDS from the armover to it.
        // ReturnBend computes the same quantity signed and does not have the hole.
        plan.DropLength = overPt.Z - headPt.Z;

        // Measured UPWARD, matching Direct and ReturnBend. The Python took the
        // angle of head->over (which points DOWN) against +Z, so a perfectly
        // plumb end-bend drop reported 180 deg while the other two layouts
        // reported 0 for identical geometry. Nothing acted on it - the lean
        // warning only applies to Direct - but the number was on the report and
        // read as a fault.
        Vec3 dropVec = overPt.Subtract(headPt);
        if (dropVec.GetLength() > LenEps)
            plan.LeanDeg = dropVec.Normalize().AngleTo(Vec3.BasisZ) * 180.0 / Math.PI;

        // Geometry first (head at or above the armover), then the user's
        // min-drop setting.
        if (plan.DropLength <= 0.0)
        {
            plan.Add(SprigStatus.Skip,
                $"Head is at or above the armover - the drop would be {Units.Mm(plan.DropLength)}");
        }
        else if (plan.DropLength < parameters.MinDrop)
        {
            plan.Add(SprigStatus.Skip,
                $"Drop is {Units.Mm(plan.DropLength)}, under the {Units.Mm(parameters.MinDrop)} "
                + "'minimum drop length' SETTING - the geometry is fine, the setting is rejecting it");
        }
    }

    /// <summary>
    /// Tee straight down to the head.
    ///
    /// NOT PLUMB, despite what this said before. The tee lands at the
    /// perpendicular foot on the branch, so the drop leans across whatever plan
    /// offset is left to reach the head — which is exactly what the warning at
    /// the bottom of this method has always described. A tee cannot sit over a
    /// head that is off the branch line, because the tee has to be ON the pipe.
    ///
    /// RETURN BEND is the layout that IS plumb: its elbow sits at the head's own
    /// plan XY, so both vertical pipes run dead straight.
    /// <see cref="DropParams.MaxDirectLeanDeg"/> routes a head here rather than
    /// letting a direct drop lean past what anyone would accept.
    /// </summary>
    private static void SolveDirect(
        DropHeadPlan plan, DropSegment seg, Vec3 headPt, DropParams parameters)
    {
        plan.Kind = DropLayout.Direct;

        DropSegment.Projection? proj = seg.Project(headPt);
        if (proj is null)
        {
            plan.Add(SprigStatus.Skip, "Could not project the head onto the branch segment");
            return;
        }

        Vec3 teePt = proj.Value.RefPt;
        plan.Station = seg.P0.DistanceTo(teePt);
        plan.TeePt = teePt;

        if (!proj.Value.Inside)
        {
            plan.Add(SprigStatus.Skip,
                "Head is off the end of the branch run - its plan projection lands past a "
                + "segment endpoint");
            return;
        }

        Vec3 dropVec = teePt.Subtract(headPt);
        plan.DropLength = dropVec.GetLength();

        if (headPt.Z >= teePt.Z - LenEps)
        {
            plan.Add(SprigStatus.Skip,
                "Head is at or above the branch line - a pendent drop has to come DOWN to the head");
            return;
        }

        if (plan.DropLength > LenEps)
            plan.LeanDeg = dropVec.Normalize().AngleTo(Vec3.BasisZ) * 180.0 / Math.PI;

        if (plan.LeanDeg > parameters.LeanWarnDeg)
        {
            plan.Add(SprigStatus.Warn,
                $"Drop leans {plan.LeanDeg:F2} deg off plumb - the head sits off the branch "
                + "centreline, so its drop must reach sideways to meet it");
        }
    }

    /// <summary>
    /// Up off the top of the branch, over, and plumb down to the head. BOTH
    /// vertical pipes — the riser and the drop — are built plumb.
    /// </summary>
    private static void SolveReturnBend(
        DropHeadPlan plan, DropSegment seg, Vec3 headPt, Vec3 refPt, DropParams parameters)
    {
        plan.Kind = DropLayout.ReturnBend;

        // PLUMB riser: tee directly under the riser top (the head's plan
        // projection, already on the branch axis), so the riser runs dead
        // vertical even on a pitched branch.
        Vec3 riserTop = new(refPt.X, refPt.Y, refPt.Z + parameters.RiserHeight);
        Vec3 teePt = refPt;

        plan.Station = seg.P0.DistanceTo(teePt);
        plan.TeePt = teePt;
        plan.RiserTop = riserTop;
        plan.OverPt = new Vec3(headPt.X, headPt.Y, riserTop.Z);

        plan.RiserLength = riserTop.Subtract(teePt).GetLength();
        plan.ArmoverLength = plan.Offset;
        plan.DropLength = riserTop.Z - headPt.Z;

        Vec3 riserVec = riserTop.Subtract(teePt);
        if (plan.RiserLength > LenEps)
            plan.LeanDeg = riserVec.Normalize().AngleTo(Vec3.BasisZ) * 180.0 / Math.PI;

        if (headPt.Z >= refPt.Z)
        {
            plan.Add(SprigStatus.Warn,
                "Head sits ABOVE the branch line - the return bend still solves, but is this "
                + "really a pendent head?");
        }

        // Two genuinely different failures, kept apart on purpose: one is
        // geometry (the head is up at or above the armover), the other is a
        // threshold the user set.
        if (plan.DropLength <= 0.0)
        {
            plan.Add(SprigStatus.Skip,
                $"Head is at or above the riser top - the drop would be {Units.Mm(plan.DropLength)}");
        }
        else if (plan.DropLength < parameters.MinDrop)
        {
            plan.Add(SprigStatus.Skip,
                $"Drop is {Units.Mm(plan.DropLength)}, under the {Units.Mm(parameters.MinDrop)} "
                + "'minimum drop length' SETTING - the geometry is fine, the setting is rejecting it");
        }

        // Pipe-through-pipe checks, against the branch's real diameter when it
        // is known. The drop passes the branch at plan offset d; the armover
        // crosses over the top of it at the riser height.
        if (seg.Diameter > 0.0 && plan.Dia > 0.0)
        {
            double halfPipe = plan.Dia / 2.0;
            double need = seg.Diameter / 2.0 + halfPipe + parameters.Clearance;
            if (plan.Offset < need)
            {
                plan.Add(SprigStatus.Warn,
                    $"Drop passes only {Units.Mm(plan.Offset)} from the branch centreline - needs "
                    + $"{Units.Mm(need)} to clear the pipe ({Units.Mm(seg.Diameter)} branch + "
                    + $"{Units.Mm(plan.Dia)} drop + {Units.Mm(parameters.Clearance)} gap)");
            }

            double needH = seg.Diameter / 2.0 + halfPipe + parameters.Clearance;
            if (parameters.RiserHeight < needH)
            {
                plan.Add(SprigStatus.Warn,
                    $"Riser height {Units.Mm(parameters.RiserHeight)} is too short for the armover "
                    + $"to clear the top of the branch - needs {Units.Mm(needH)}");
            }
        }
    }

    private static void Validate(DropHeadPlan plan, DropSegment seg, DropParams parameters)
    {
        if (plan.Status == SprigStatus.Skip)
            return;

        if (plan.Kind == DropLayout.Direct && plan.DropLength < parameters.MinDrop)
        {
            plan.Add(SprigStatus.Skip,
                $"Drop is {Units.Mm(plan.DropLength)}, under the {Units.Mm(parameters.MinDrop)} "
                + "'minimum drop length' SETTING - the geometry is fine, the setting is rejecting it");
        }

        // The tee-margin check is about breaking a pipe mid-run, so it only
        // applies to the tee'd types. An END BEND elbows onto an existing open
        // end - nothing is broken, and Station is null for it.
        if (!plan.EndBend && plan.Station is not null)
        {
            double? margin = EffectiveMargin(seg.Length, parameters.MainMargin);
            if (margin is null)
            {
                plan.Add(SprigStatus.Skip,
                    $"Branch pipe is only {Units.Mm(seg.Length)} long - too short to insert a "
                    + "fitting at all");
            }
            else if (plan.Station < margin)
            {
                plan.Add(SprigStatus.Skip,
                    $"Tee lands {Units.Mm(plan.Station)} from the start of the pipe - no room to "
                    + $"break it (needs {Units.Mm(margin)} of stub)");
            }
            else if (plan.Station > seg.Length - margin)
            {
                plan.Add(SprigStatus.Skip,
                    $"Tee lands {Units.Mm(seg.Length - plan.Station.Value)} from the end of the "
                    + $"pipe - no room to break it (needs {Units.Mm(margin)} of stub)");
            }
        }

        // 0 means "no limit". Both the return bend and the end bend have an
        // armover worth sanity-checking.
        bool hasArmover = plan.Kind is DropLayout.ReturnBend or DropLayout.EndBend;
        double maxArmover = parameters.MaxArmover;
        if (hasArmover && maxArmover > 0.0 && plan.ArmoverLength > maxArmover)
        {
            if (plan.ArmoverLength > WrongBranchHintFt)
            {
                plan.Add(SprigStatus.Warn,
                    $"Armover is {Units.Mm(plan.ArmoverLength)} - longer than the "
                    + $"{Units.Mm(maxArmover)} limit, and long enough that this head may belong to "
                    + "a different branch line");
            }
            else
            {
                plan.Add(SprigStatus.Warn,
                    $"Armover is {Units.Mm(plan.ArmoverLength)} - longer than the "
                    + $"{Units.Mm(maxArmover)} limit SETTING; the geometry itself looks reasonable");
            }
        }

        // NFPA 13 hangs an armover past 24 in (610 mm) - 12 in over 100 psi.
        // Advisory only: the tool doesn't know the system pressure.
        double hanger = parameters.HangerWarn;
        if (hasArmover && hanger > 0.0 && plan.ArmoverLength > hanger)
        {
            plan.Add(SprigStatus.Warn,
                $"Armover is {Units.Mm(plan.ArmoverLength)} - past the {Units.Mm(hanger)} "
                + "unsupported-armover limit, so it will need its own hanger (NFPA 13; the limit "
                + "drops to 305 mm above 100 psi)");
        }
    }

    /// <summary>
    /// Two tees too close together on the same segment leave no room for both
    /// fittings. An END BEND has no tee and no station, so it is not part of the
    /// tee-spacing question at all.
    /// </summary>
    private static void CheckTeeSpacing(IReadOnlyList<DropHeadPlan> plans, DropParams parameters)
    {
        double minSpacing = parameters.MinTeeSpacing;

        Dictionary<int, List<DropHeadPlan>> bySegment = [];
        foreach (DropHeadPlan plan in plans)
        {
            if (plan.SegmentIndex is null || plan.Status == SprigStatus.Skip
                || plan.Station is null)
            {
                continue;
            }

            if (!bySegment.TryGetValue(plan.SegmentIndex.Value, out List<DropHeadPlan>? group))
                bySegment[plan.SegmentIndex.Value] = group = [];
            group.Add(plan);
        }

        foreach (List<DropHeadPlan> group in bySegment.Values)
        {
            group.Sort((a, b) => (a.Station ?? 0.0).CompareTo(b.Station ?? 0.0));
            for (int i = 0; i < group.Count - 1; i++)
            {
                DropHeadPlan prev = group[i];
                DropHeadPlan curr = group[i + 1];
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

    public static (int Ok, int Warn, int Skip, int Direct, int ReturnBend, int EndBend) Summarize(
        IReadOnlyList<DropHeadPlan> plans)
    {
        int ok = plans.Count(p => p.Status == SprigStatus.Ok);
        int warn = plans.Count(p => p.Status == SprigStatus.Warn);
        int skip = plans.Count(p => p.Status == SprigStatus.Skip);
        int direct = plans.Count(p => p.Kind == DropLayout.Direct && p.Status != SprigStatus.Skip);
        int returnBend = plans.Count(p => p.Kind == DropLayout.ReturnBend && p.Status != SprigStatus.Skip);
        int endBend = plans.Count(p => p.Kind == DropLayout.EndBend && p.Status != SprigStatus.Skip);
        return (ok, warn, skip, direct, returnBend, endBend);
    }
}
