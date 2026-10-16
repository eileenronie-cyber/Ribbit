namespace Ribbit.Geometry;

/// <summary>settings.as_feet() output for the slope tools.</summary>
public readonly record struct SlopeParams(
    double AngleTolDeg,
    double PlanOffsetTol,
    double Slope);

/// <summary>One straight picked piece of the run.</summary>
public sealed class SlopeSegment
{
    public long ElemId { get; }
    public Vec3 P0 { get; }
    public Vec3 P1 { get; }
    public string Desc { get; }
    public bool IsPipe { get; }

    public double Length { get; }
    public double PlanLength { get; }
    public bool IsVertical { get; }
    public Vec3? PlanDirection { get; }

    /// <summary>
    /// Rise over PLAN run, signed along P0 -> P1. Matches how the new slope is
    /// specified, so old and new are directly comparable.
    /// </summary>
    public double Rate { get; }

    public SlopeSegment(long elemId, Vec3 p0, Vec3 p1, string? desc = null, bool isPipe = true)
    {
        ElemId = elemId;
        P0 = p0;
        P1 = p1;
        Desc = desc ?? "Pipe";
        IsPipe = isPipe;

        Length = p1.Subtract(p0).GetLength();

        Vec3 plan = new(p1.X - p0.X, p1.Y - p0.Y, 0.0);
        PlanLength = plan.GetLength();
        IsVertical = PlanLength < SlopeSolver.LenEps;
        PlanDirection = IsVertical ? null : plan.Normalize();

        Rate = PlanLength > SlopeSolver.LenEps ? (p1.Z - p0.Z) / PlanLength : 0.0;
    }
}

/// <summary>
/// The picked segments assembled into one straight plan axis. Holds no Revit
/// state — just the axis, the origin and the station range — so every consumer
/// measures stations the same way.
/// </summary>
public sealed class SlopeRun
{
    public IReadOnlyList<SlopeSegment> Segments { get; }

    /// <summary>Unit, horizontal.</summary>
    public Vec3 Axis { get; }
    public Vec3 Origin { get; }

    public double SMin { get; }
    public double SMax { get; }
    public double PlanLength { get; }
    public double ZAtMin { get; }
    public double ZAtMax { get; }
    public double OldRate { get; }

    public SlopeRun(IReadOnlyList<SlopeSegment> segments, Vec3 axis, Vec3 origin)
    {
        Segments = segments;
        Axis = axis;
        Origin = origin;

        List<double> stations = [];
        foreach (SlopeSegment seg in segments)
        {
            stations.Add(Station(seg.P0));
            stations.Add(Station(seg.P1));
        }

        SMin = stations.Min();
        SMax = stations.Max();
        PlanLength = SMax - SMin;

        ZAtMin = ZAtExtreme(SMin);
        ZAtMax = ZAtExtreme(SMax);

        OldRate = PlanLength > SlopeSolver.LenEps ? (ZAtMax - ZAtMin) / PlanLength : 0.0;
    }

    /// <summary>
    /// Plan distance along the run axis from the origin. Z is deliberately
    /// ignored — slope is rise over PLAN run.
    /// </summary>
    public double Station(Vec3 point)
    {
        double dx = point.X - Origin.X;
        double dy = point.Y - Origin.Y;
        return dx * Axis.X + dy * Axis.Y;
    }

    /// <summary>
    /// Perpendicular plan distance from the run's axis line. This is what
    /// catches a jogged or offset run being picked as one.
    /// </summary>
    public double PlanOffset(Vec3 point)
    {
        double s = Station(point);
        double px = Origin.X + Axis.X * s;
        double py = Origin.Y + Axis.Y * s;
        return Math.Sqrt((point.X - px) * (point.X - px) + (point.Y - py) * (point.Y - py));
    }

    private double ZAtExtreme(double targetS)
    {
        double bestZ = 0.0;
        double? bestD = null;

        foreach (SlopeSegment seg in Segments)
        {
            foreach (Vec3 pt in new[] { seg.P0, seg.P1 })
            {
                double d = Math.Abs(Station(pt) - targetS);
                if (bestD is null || d < bestD)
                {
                    bestZ = pt.Z;
                    bestD = d;
                }
            }
        }
        return bestZ;
    }

    /// <summary>
    /// The segment whose station range contains this station, or the nearest one
    /// when the station falls in a tee gap.
    /// </summary>
    public SlopeSegment? SegmentAt(double station)
    {
        SlopeSegment? best = null;
        double? bestD = null;

        foreach (SlopeSegment seg in Segments)
        {
            double s0 = Station(seg.P0);
            double s1 = Station(seg.P1);
            double lo = Math.Min(s0, s1);
            double hi = Math.Max(s0, s1);

            if (station >= lo && station <= hi)
                return seg;

            double d = Math.Min(Math.Abs(station - lo), Math.Abs(station - hi));
            if (bestD is null || d < bestD)
            {
                best = seg;
                bestD = d;
            }
        }
        return best;
    }

    /// <summary>
    /// Current Z of the run at a station, interpolated along the segment that
    /// holds it (clamped, so a station in a tee gap reads the nearest end).
    /// </summary>
    public double ZAt(double station)
    {
        SlopeSegment? seg = SegmentAt(station);
        if (seg is null)
            return 0.0;

        double s0 = Station(seg.P0);
        double s1 = Station(seg.P1);
        if (Math.Abs(s1 - s0) < SlopeSolver.LenEps)
            return seg.P0.Z;

        double t = Math.Max(0.0, Math.Min(1.0, (station - s0) / (s1 - s0)));
        return seg.P0.Z + t * (seg.P1.Z - seg.P0.Z);
    }
}

/// <summary>
/// The complete answer: where every station ends up, and the rigid transform
/// each in-line fitting needs.
///
/// ANCHOR ANYWHERE, NOT JUST AT AN END — <see cref="NewZ"/> measures
/// |station - anchor|, so anchoring mid-run makes a V: the run rises (or falls)
/// away from the fixed point in BOTH directions. That is exactly what a branch
/// fed by a mid-run tee wants; an end anchor is just the degenerate V whose
/// short arm has zero length.
///
/// ROTATION IS PER-STATION, NOT PER-RUN — a fitting's rotation is new pitch
/// minus old pitch AT ITS OWN STATION. Both terms vary along the run: the old
/// rate because the segments need not start at one uniform pitch, the new rate
/// because a V's two arms slope opposite ways. One run-wide theta mis-rotates
/// every fitting on the short arm of a V, so <see cref="ThetaAt"/> is the only
/// correct thing to ask for; <see cref="Theta"/> is a representative value for
/// the report.
/// </summary>
public sealed class RePitch
{
    private const double EndTol = 0.01;   // ft, ~3 mm

    public SlopeRun Run { get; }

    /// <summary>Signed; positive RISES away from the anchor.</summary>
    public double Slope { get; }

    public double AnchorStation { get; }

    /// <summary>
    /// The anchor's CURRENT elevation, always read from the model — used for the
    /// "slope now" report so it describes the run as it stands.
    /// </summary>
    public double AnchorZModel { get; }

    /// <summary>
    /// The anchor's TARGET elevation. Normally the same as the model value: the
    /// fixed end keeps its elevation. Slope Through overrides it for every leg
    /// after the first, feeding in the elevation the UPSTREAM leg leaves at the
    /// shared corner, so a developed run rising away from one tie-in stays
    /// continuous across each elbow instead of every leg re-anchoring to its own
    /// current corner Z.
    /// </summary>
    public double AnchorZ { get; }

    public bool AnchorAtMin { get; }
    public bool AnchorAtMax { get; }
    public bool IsEndAnchor => AnchorAtMin || AnchorAtMax;

    public double FarStation { get; }
    public double NearStation { get; }
    public double FarZOld { get; }
    public double FarZNew { get; }
    public double NearZOld { get; }
    public double NearZNew { get; }

    public double OldRate { get; }
    public double OldRateAway { get; }
    public double NewRateAway { get; }

    public Vec3 AxisDir { get; }
    public double Theta { get; }

    public RePitch(SlopeRun run, double anchorStation, double slope, double? anchorZ = null)
    {
        Run = run;
        Slope = slope;
        AnchorStation = anchorStation;

        AnchorZModel = run.ZAt(anchorStation);
        AnchorZ = anchorZ ?? AnchorZModel;

        AnchorAtMin = Math.Abs(anchorStation - run.SMin) <= EndTol;
        AnchorAtMax = Math.Abs(anchorStation - run.SMax) <= EndTol;

        if (Math.Abs(run.SMax - anchorStation) >= Math.Abs(run.SMin - anchorStation))
        {
            FarStation = run.SMax;
            NearStation = run.SMin;
        }
        else
        {
            FarStation = run.SMin;
            NearStation = run.SMax;
        }

        FarZOld = run.ZAt(FarStation);
        FarZNew = NewZ(FarStation);
        NearZOld = run.ZAt(NearStation);
        NearZNew = NewZ(NearStation);

        OldRate = run.OldRate;

        // The slopes stated the way the user typed theirs: rise per plan metre
        // measured AWAY FROM THE FIXED POINT.
        double span = Math.Abs(FarStation - anchorStation);
        OldRateAway = span > SlopeSolver.LenEps ? (FarZOld - AnchorZModel) / span : 0.0;
        NewRateAway = slope;

        AxisDir = SlopeSolver.RotationAxis(run.Axis);
        Theta = ThetaAt(FarStation);
    }

    /// <summary>
    /// The run's CURRENT pitch at a station, along +axis — read from the segment
    /// holding that station, because the segments need not all start at the same
    /// pitch.
    /// </summary>
    public double OldRateAt(double station)
    {
        SlopeSegment? seg = Run.SegmentAt(station);
        if (seg?.PlanDirection is null)
            return OldRate;

        return seg.PlanDirection.Value.DotProduct(Run.Axis) >= 0.0 ? seg.Rate : -seg.Rate;
    }

    /// <summary>
    /// The TARGET pitch at a station, along +axis: +slope on the far side of the
    /// anchor, -slope on the near side (the two arms of the V).
    ///
    /// An END anchor has only one arm, so the whole run — including a fitting
    /// whose pivot sits a few mm OUTSIDE the anchored end, as the end elbow's
    /// does — gets that arm's rate. Without this guard the end fitting compared
    /// as "below the anchor station" and was rotated as if it sat on an arm that
    /// does not exist, wrong way by the full old-to-minus-new angle.
    /// </summary>
    public double NewRateAt(double station)
    {
        if (AnchorAtMin)
            return Slope;
        if (AnchorAtMax)
            return -Slope;
        return station >= AnchorStation ? Slope : -Slope;
    }

    /// <summary>Rotation for an in-line fitting AT this station.</summary>
    public double ThetaAt(double station)
        => SlopeSolver.SignedTheta(Run.Axis, OldRateAt(station), NewRateAt(station), AxisDir);

    /// <summary>
    /// One segment's own current slope, stated away from the fixed point.
    /// <see cref="SlopeSegment.Rate"/> is measured P0 -> P1, and the segments of
    /// a run are not all drawn the same way round, so reporting it raw makes two
    /// identically pitched pipes look like they disagree.
    /// </summary>
    public double SegmentRateAway(SlopeSegment seg)
    {
        if (seg.PlanDirection is null)
            return 0.0;

        double along = seg.PlanDirection.Value.DotProduct(Run.Axis) > 0.0 ? seg.Rate : -seg.Rate;
        double mid = (Run.Station(seg.P0) + Run.Station(seg.P1)) / 2.0;
        return mid >= AnchorStation ? along : -along;
    }

    public double NewZ(double station) => AnchorZ + Slope * Math.Abs(station - AnchorStation);

    /// <summary>Same plan position, new elevation.</summary>
    public Vec3 NewPoint(Vec3 point) => new(point.X, point.Y, NewZ(Run.Station(point)));

    /// <summary>
    /// How far this point has to move vertically. Read against the point's OWN
    /// current Z, so an already-inconsistent run still lands exactly on the new
    /// line.
    /// </summary>
    public double Dz(Vec3 point) => NewZ(Run.Station(point)) - point.Z;

    /// <summary>
    /// Where a point ends up under the in-line-fitting transform: rotate by the
    /// PIVOT's own theta about the horizontal axis through pivot, then drop the
    /// pivot onto the new line. The applier performs exactly this pair of
    /// operations on the fitting itself; this lets the solver predict where the
    /// fitting's outlet will land so whatever hangs off it can be translated to
    /// match.
    /// </summary>
    public Vec3 TransformInline(Vec3 point, Vec3 pivot)
    {
        double theta = ThetaAt(Run.Station(pivot));
        Vec3 rotated = SlopeSolver.RotateAbout(point, pivot, AxisDir, theta);
        return new Vec3(rotated.X, rotated.Y, rotated.Z + Dz(pivot));
    }
}

/// <summary>
/// Slope Branch / Slope Through — geometry solver. Ported from
/// lib/slopelib/solver.py.
///
/// Pure geometry: turns "these pipe segments, that fixed end, this slope" into
/// an exact new elevation for every point of interest, so the whole re-pitch can
/// be reported and eyeballed BEFORE anything moves.
///
/// IN-LINE FITTINGS ROTATE — a coupling or tee sitting in a sloped run is itself
/// sloped, so changing the run's pitch by dtheta means every in-line fitting has
/// to rotate by dtheta about the horizontal axis perpendicular to the run, or
/// its connectors stop lining up with the pipe either side. The sign is verified
/// numerically rather than reasoned about — see <see cref="SignedTheta"/>.
///
/// SPRIGS STAY VERTICAL — what hangs off an in-line tee is translated, never
/// rotated, so sprigs stay plumb and heads stay over their plan position.
/// </summary>
public static class SlopeSolver
{
    public const double LenEps = 1.0e-9;

    /// <summary>
    /// Steeper than this off horizontal is never branch line. The cutoff used to
    /// be 45 deg exactly, which is a trap: a 45-deg elbow rising off a pitched
    /// run puts its leg at 45 minus the pitch — fractionally UNDER the cutoff —
    /// and a leg that also shares the run's plan direction was then accepted as a
    /// run segment and flattened onto the slope line. Branch pitch is mm-per-m,
    /// so 30 deg still catches a 45-deg leg at any realistic pitch.
    /// </summary>
    public const double SteepDeg = 30.0;

    public static readonly double SteepRate = Math.Tan(SteepDeg * Math.PI / 180.0);

    /// <summary>
    /// Steeper than <see cref="SteepDeg"/> off horizontal — a sprig, riser, drop
    /// or the leg of a 45-deg offset, never a branch line.
    /// </summary>
    public static bool IsSteep(SlopeSegment seg)
        => seg.PlanLength <= LenEps
           || Math.Abs(seg.P1.Z - seg.P0.Z) > seg.PlanLength * SteepRate;

    /// <summary>
    /// Same to one decimal. Used for elevations and vertical deltas, where a
    /// whole run's worth of pitch can be under a millimetre and rounding it to 0
    /// would hide the entire point of the tool.
    /// </summary>
    public static string Mm1(double? valueFt)
    {
        if (valueFt is null)
            return "-";

        double mm = valueFt.Value * Units.MmPerFt;
        // See Units.Mm - avoids a "-0.0 mm" that reads as a negative move.
        return Math.Abs(mm) < 0.05 ? "0.0 mm" : $"{mm:F1} mm";
    }

    /// <summary>
    /// A rise-over-run ratio -> "+2.00 mm/m (1:500, +0.115 deg)". The rate is
    /// signed; the sign is carried on the mm/m figure and the ratio is printed
    /// unsigned because "1:-500" reads as a typo.
    /// </summary>
    public static string FmtSlope(double? rate)
    {
        if (rate is null)
            return "-";
        if (Math.Abs(rate.Value) < 1.0e-9)
            return "0.00 mm/m (level)";

        double mmPerM = rate.Value * 1000.0;
        double deg = Math.Atan(rate.Value) * 180.0 / Math.PI;
        return $"{mmPerM:+0.00;-0.00} mm/m (1:{1.0 / Math.Abs(rate.Value):F0}, {deg:+0.000;-0.000} deg)";
    }

    // ---------------------------------------------------------------- rotation

    /// <summary>
    /// Horizontal direction to rotate an in-line fitting about: the run axis
    /// crossed with Z. Which of the two perpendiculars this is decides only the
    /// SIGN of theta, and <see cref="SignedTheta"/> resolves that numerically, so
    /// this just has to be consistent.
    /// </summary>
    public static Vec3 RotationAxis(Vec3 axis)
    {
        Vec3 cross = axis.CrossProduct(Vec3.BasisZ);
        if (cross.GetLength() < LenEps)
        {
            // A vertical run has no meaningful in-plane rotation; the engine
            // rejects vertical segments long before this, so this is only a
            // guard against a degenerate axis.
            return new Vec3(0.0, 1.0, 0.0);
        }
        return cross.Normalize();
    }

    /// <summary>Rodrigues rotation of a point about the line (pivot, axisUnit).</summary>
    public static Vec3 RotateAbout(Vec3 point, Vec3 pivot, Vec3 axisUnit, double angle)
        => pivot.Add(point.Subtract(pivot).Rotate(axisUnit, angle));

    /// <summary>
    /// Unit 3D direction of a run with this plan axis pitched at this rate (rise
    /// over plan run).
    /// </summary>
    public static Vec3 DirectionAtRate(Vec3 axis, double rate)
        => new Vec3(axis.X, axis.Y, rate).Normalize();

    /// <summary>
    /// Rotation that takes the run's OLD 3D direction to its NEW one, about
    /// axisDir.
    ///
    /// The magnitude is just the difference of the two pitch angles. The sign
    /// depends on which way <see cref="RotationAxis"/> happened to point, and
    /// getting it backwards would tip every fitting the wrong way for a result
    /// that still looks plausible in the report. So rather than reason it out,
    /// both candidates are applied to the old direction and the one that actually
    /// lands on the new direction wins.
    /// </summary>
    public static double SignedTheta(Vec3 axis, double oldRate, double newRate, Vec3 axisDir)
    {
        double magnitude = Math.Abs(Math.Atan(newRate) - Math.Atan(oldRate));
        if (magnitude < 1.0e-12)
            return 0.0;

        Vec3 dOld = DirectionAtRate(axis, oldRate);
        Vec3 dNew = DirectionAtRate(axis, newRate);

        double best = 0.0;
        double? bestErr = null;
        foreach (double candidate in new[] { magnitude, -magnitude })
        {
            Vec3 got = RotateAbout(dOld, Vec3.Zero, axisDir, candidate);
            double err = got.DistanceTo(dNew);
            if (bestErr is null || err < bestErr)
            {
                best = candidate;
                bestErr = err;
            }
        }
        return best;
    }

    /// <summary>
    /// Signed angle to rotate vecFrom about rollAxis so it lands as close as
    /// possible to vecTarget.
    ///
    /// Only vecFrom's component PERPENDICULAR to rollAxis can move under such a
    /// rotation — the parallel component is invariant. So this is EXACT when the
    /// two vectors happen to have the same angle to rollAxis, and best-effort
    /// otherwise; the caller is expected to measure what is actually left over
    /// rather than trusting this to be a perfect match.
    ///
    /// Used to align a corner elbow's CHILD-facing connector as closely as
    /// possible to its own leg's required direction, after a first rotation has
    /// already made the PARENT-facing connector exact — a single rigid elbow
    /// generally cannot satisfy two independently sloped legs at once.
    /// </summary>
    public static double CornerRoll(Vec3 vecFrom, Vec3 vecTarget, Vec3 rollAxis)
    {
        Vec3 axis = rollAxis.Normalize();
        Vec3 fromPerp = vecFrom.Subtract(axis.Multiply(axis.DotProduct(vecFrom)));
        Vec3 targetPerp = vecTarget.Subtract(axis.Multiply(axis.DotProduct(vecTarget)));

        if (fromPerp.GetLength() < LenEps || targetPerp.GetLength() < LenEps)
            return 0.0;

        fromPerp = fromPerp.Normalize();
        targetPerp = targetPerp.Normalize();

        double cosA = Math.Max(-1.0, Math.Min(1.0, fromPerp.DotProduct(targetPerp)));
        double magnitude = Math.Acos(cosA);
        if (magnitude < 1.0e-12)
            return 0.0;

        double best = 0.0;
        double? bestErr = null;
        foreach (double candidate in new[] { magnitude, -magnitude })
        {
            Vec3 got = RotateAbout(fromPerp, Vec3.Zero, axis, candidate);
            double err = got.DistanceTo(targetPerp);
            if (bestErr is null || err < bestErr)
            {
                best = candidate;
                bestErr = err;
            }
        }
        return best;
    }

    // ---------------------------------------------------------------- assembly

    /// <summary>
    /// Assembles the picked segments into one straight run, dropping any that do
    /// not belong to it. Rejected entries carry a reason so the report can say
    /// which pick was dropped and why.
    /// </summary>
    public static (SlopeRun? Run, List<(SlopeSegment Segment, string Reason)> Rejected) BuildRun(
        IReadOnlyList<SlopeSegment> segments, SlopeParams parameters)
    {
        List<(SlopeSegment, string)> rejected = [];

        // The same pipe picked twice is a slip, not two segments - and it would
        // otherwise read as a segment overlapping itself below.
        HashSet<long> seenIds = [];
        List<SlopeSegment> deduped = [];
        foreach (SlopeSegment seg in segments)
        {
            if (seenIds.Add(seg.ElemId))
                deduped.Add(seg);
        }

        // A sprig canted a hair off plumb is NOT vertical by the exact LenEps
        // test, and its sliver of plan direction can happen to line up with the
        // run - live testing caught two of them collected as "run segments",
        // which the re-pitch would have crushed to zero length.
        List<SlopeSegment> flat = [];
        foreach (SlopeSegment seg in deduped)
        {
            if (IsSteep(seg))
            {
                rejected.Add((seg,
                    $"rises {Units.Mm(Math.Abs(seg.P1.Z - seg.P0.Z))} over "
                    + $"{Units.Mm(seg.PlanLength)} of plan - steeper than {SteepDeg:F0} deg, so "
                    + "this is a sprig, riser or offset leg, not a piece of the branch line. It "
                    + "still moves with its fitting's group; it just isn't re-pitched as pipe."));
            }
            else
            {
                flat.Add(seg);
            }
        }

        List<SlopeSegment> usable =
            [.. flat.Where(s => !s.IsVertical && s.PlanLength > LenEps)];

        if (usable.Count == 0)
        {
            foreach (SlopeSegment s in flat)
                rejected.Add((s, "no plan length - a vertical riser can't carry a slope"));
            return (null, rejected);
        }

        // The longest segment defines the axis: it is the one whose direction is
        // least sensitive to endpoint noise.
        SlopeSegment spine = usable.OrderByDescending(s => s.PlanLength).First();
        Vec3 axis = spine.PlanDirection!.Value;
        Vec3 origin = new(spine.P0.X, spine.P0.Y, 0.0);

        double cosTol = Math.Cos(parameters.AngleTolDeg * Math.PI / 180.0);

        List<SlopeSegment> kept = [];
        foreach (SlopeSegment seg in flat)
        {
            if (seg.IsVertical || seg.PlanLength <= LenEps)
            {
                rejected.Add((seg,
                    "vertical - a riser can't carry a slope, and re-pitching the run it hangs off "
                    + "moves it anyway"));
                continue;
            }

            // Abs because the segments of one run are not all drawn in the same
            // direction; anti-parallel is still in line.
            double alignment = Math.Abs(seg.PlanDirection!.Value.DotProduct(axis));
            if (alignment < cosTol)
            {
                double off = Math.Acos(Math.Max(-1.0, Math.Min(1.0, alignment))) * 180.0 / Math.PI;
                rejected.Add((seg,
                    $"runs {off:F2} deg off the rest of the pick - this tool re-pitches ONE "
                    + "straight run, so pick the segments of a single branch line"));
                continue;
            }

            kept.Add(seg);
        }

        if (kept.Count == 0)
            return (null, rejected);

        SlopeRun run = new(kept, axis, origin);

        // Second pass: in line with the axis is not the same as ON it. Two
        // parallel branch lines picked together would sail through the angle
        // check and then be re-pitched onto a single shared line.
        double tol = parameters.PlanOffsetTol;
        List<SlopeSegment> onAxis = [];
        List<(SlopeSegment, string)> offAxis = [];
        foreach (SlopeSegment seg in kept)
        {
            double offset = Math.Max(run.PlanOffset(seg.P0), run.PlanOffset(seg.P1));
            if (offset > tol)
            {
                offAxis.Add((seg,
                    $"sits {Units.Mm(offset)} off the run's centreline - it is parallel to the run "
                    + "but not on it, so it is a different branch line"));
            }
            else
            {
                onAxis.Add(seg);
            }
        }

        rejected.AddRange(offAxis);
        if (onAxis.Count == 0)
            return (null, rejected);
        if (offAxis.Count > 0)
            run = new SlopeRun(onAxis, axis, origin);

        // Third pass: on the axis in plan is still not the same as being the same
        // pipe. PlanOffset discards Z entirely, so a branch and a main stacked
        // directly above one another share a plan line and sail through both
        // checks above - and would then be flattened onto a single sloped line
        // together. Overlapping stations is the giveaway: the segments of one
        // real run are separated by fittings, so their station ranges never
        // overlap.
        List<(SlopeSegment A, SlopeSegment B, double Amount, double Gap)> overlaps =
            StationOverlaps(run, parameters.PlanOffsetTol);

        if (overlaps.Count > 0)
        {
            foreach ((SlopeSegment a, SlopeSegment b, double amount, double gap) in overlaps)
            {
                rejected.Add((a, OverlapMessage(b, amount, gap)));
                rejected.Add((b, OverlapMessage(a, amount, gap)));
            }
            return (null, rejected);
        }

        // Fourth pass: stacked runs that do NOT overlap.
        //
        // The overlap test catches a second branch line lying over the same
        // stretch. It says nothing about one lying BEYOND it — two runs at
        // different elevations, one picked after the other along the axis, whose
        // station ranges never meet. Those clear all three checks above and are
        // then re-pitched onto a single line, flattening the step between them.
        // Live-caught on real geometry: two pipes 659 mm apart in Z with a
        // 362 mm gap along the run, accepted with zero rejections, and a 2 mm/m
        // re-pitch would have dragged the second one 807 mm.
        List<(SlopeSegment A, SlopeSegment B, double Rise, double Gap)> steps =
            StationSteps(run, parameters.PlanOffsetTol);

        if (steps.Count > 0)
        {
            foreach ((SlopeSegment a, SlopeSegment b, double rise, double gap) in steps)
            {
                rejected.Add((a, StepMessage(b, rise, gap)));
                rejected.Add((b, StepMessage(a, rise, gap)));
            }
            return (null, rejected);
        }

        return (run, rejected);
    }

    private static string StepMessage(SlopeSegment other, double rise, double gap)
        => $"sits {Units.Mm(rise)} above or below element {other.ElemId}, with only "
           + $"{Units.Mm(gap)} of run between them. Getting from one to the other means climbing "
           + $"steeper than {SteepDeg:F0} deg, which no branch line does - so these are two runs "
           + "at different elevations, not one. Re-pitching them together would flatten the step "
           + "between them. Nothing has been changed; re-pick just the one run.";

    /// <summary>
    /// Consecutive segments that are in line and do not overlap, but can only be
    /// reached from each other by climbing.
    ///
    /// The test is the IMPLIED RATE between the two facing ends, measured against
    /// the same <see cref="SteepRate"/> that rejects a sprig or riser from being
    /// treated as run pipe. No new threshold: if the hop from one segment to the
    /// next is steeper than a branch line ever is, it is not part of one.
    ///
    /// <paramref name="riseTol"/> keeps ordinary joints out of it — consecutive
    /// pipes meet across a tee gap with a fraction of a millimetre between them,
    /// and on a run that is about to be re-pitched anyway that difference means
    /// nothing.
    /// </summary>
    private static List<(SlopeSegment A, SlopeSegment B, double Rise, double Gap)>
        StationSteps(SlopeRun run, double riseTol)
    {
        List<(double Lo, double Hi, SlopeSegment Seg)> spans = [];
        foreach (SlopeSegment seg in run.Segments)
        {
            double s0 = run.Station(seg.P0);
            double s1 = run.Station(seg.P1);
            spans.Add((Math.Min(s0, s1), Math.Max(s0, s1), seg));
        }
        spans.Sort((a, b) => a.Lo.CompareTo(b.Lo));

        List<(SlopeSegment, SlopeSegment, double, double)> steps = [];
        for (int i = 0; i < spans.Count - 1; i++)
        {
            (double aLo, double aHi, SlopeSegment segA) = spans[i];
            (double bLo, double bHi, SlopeSegment segB) = spans[i + 1];

            // Clamped at zero: a hair of overlap too small for the overlap pass
            // to report still leaves no horizontal room to climb through.
            double gap = Math.Max(0.0, bLo - aHi);
            double rise = Math.Abs(ZOn(segB, run, bLo) - ZOn(segA, run, aHi));

            if (rise <= riseTol)
                continue;

            if (gap <= LenEps || rise / gap > SteepRate)
                steps.Add((segA, segB, rise, gap));
        }
        return steps;
    }

    private static string OverlapMessage(SlopeSegment other, double amount, double gap)
        => $"overlaps element {other.ElemId} along {Units.Mm(amount)} of the run, with "
           + $"{Mm1(gap)} of vertical separation between them. Two pipes cannot occupy the same "
           + "stretch of one run - this is almost always a second branch line stacked above or "
           + "below the one you meant, picked by accident. Nothing has been changed; re-pick just "
           + "the one run.";

    /// <summary>
    /// Z of the point on the segment at the given station, extrapolating if the
    /// station is off the end.
    /// </summary>
    private static double ZOn(SlopeSegment seg, SlopeRun run, double station)
    {
        double s0 = run.Station(seg.P0);
        double s1 = run.Station(seg.P1);
        if (Math.Abs(s1 - s0) < LenEps)
            return seg.P0.Z;

        double t = (station - s0) / (s1 - s0);
        return seg.P0.Z + t * (seg.P1.Z - seg.P0.Z);
    }

    /// <summary>
    /// Segments whose station ranges genuinely overlap. Segments that merely
    /// touch end to end produce an overlap of 0 and are not reported.
    /// </summary>
    private static List<(SlopeSegment A, SlopeSegment B, double Amount, double Gap)>
        StationOverlaps(SlopeRun run, double tol)
    {
        List<(double Lo, double Hi, SlopeSegment Seg)> spans = [];
        foreach (SlopeSegment seg in run.Segments)
        {
            double s0 = run.Station(seg.P0);
            double s1 = run.Station(seg.P1);
            spans.Add((Math.Min(s0, s1), Math.Max(s0, s1), seg));
        }
        spans.Sort((a, b) => a.Lo.CompareTo(b.Lo));

        List<(SlopeSegment, SlopeSegment, double, double)> outp = [];
        for (int i = 0; i < spans.Count - 1; i++)
        {
            (double aLo, double aHi, SlopeSegment segA) = spans[i];
            (double bLo, double bHi, SlopeSegment segB) = spans[i + 1];

            double overlap = Math.Min(aHi, bHi) - Math.Max(aLo, bLo);
            if (overlap <= tol)
                continue;

            double middle = (Math.Max(aLo, bLo) + Math.Min(aHi, bHi)) / 2.0;
            double gap = ZOn(segB, run, middle) - ZOn(segA, run, middle);
            outp.Add((segA, segB, overlap, gap));
        }
        return outp;
    }

    /// <summary>
    /// RePitch for the run, anchored where the click says.
    ///
    /// snapToEnd true — anchored at whichever END the click is nearer: the
    /// single-branch button's contract, where any click on the correct half works.
    /// snapToEnd false — anchored at the click's own station, clamped to the run.
    /// Used by Multi, whose click is the fixed REFERENCE element: a cross main
    /// teeing in mid-run anchors the run right at the feed, and the slope rises
    /// away from it BOTH ways (a V).
    /// </summary>
    public static RePitch Solve(
        SlopeRun run, Vec3 clickPoint, SlopeParams parameters, bool snapToEnd = true)
    {
        double sClick = run.Station(clickPoint);
        double anchor = snapToEnd
            ? (Math.Abs(sClick - run.SMin) <= Math.Abs(sClick - run.SMax) ? run.SMin : run.SMax)
            : Math.Min(Math.Max(sClick, run.SMin), run.SMax);

        return new RePitch(run, anchor, parameters.Slope);
    }
}
