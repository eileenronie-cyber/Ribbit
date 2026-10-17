using Autodesk.Revit.DB;
using Ribbit.Geometry;
using Ribbit.Native.Revit;
using Ribbit.Native.Ui;

namespace Ribbit.Native.Tools;

/// <summary>Message is already phrased for the user; the caller shows it as-is.</summary>
internal sealed class ApplyError(string message) : Exception(message);

internal sealed class ApplyResult
{
    public int Branches { get; set; }
    public int Segments { get; set; }
    public int Groups { get; set; }
    public int JointsBroken { get; set; }
    public int JointsRemade { get; set; }

    /// <summary>(aId, bId, why) — joints that could not be put back.</summary>
    public List<(long AId, long BId, string Why)> OpenJoints { get; } = [];

    /// <summary>
    /// (elementId, why) — anchored-end fittings that were left exactly as they
    /// were because Revit refused to rebuild them.
    ///
    /// Same policy as <see cref="OpenJoints"/>: the run itself is correct, one
    /// cosmetic detail is not, and that is not worth throwing the whole re-pitch
    /// away for. Unlike an open joint the model is still functionally sound
    /// here — it is a thin gap at the fitting, not a break in the system.
    /// </summary>
    public List<(long ElemId, string Why)> FittingsNotRebuilt { get; } = [];
}

/// <summary>
/// Slope Branch / Slope Through — the applier. THIS CLASS WRITES TO THE MODEL.
/// Ported from lib/slopelib/applier.py.
///
/// Fixed order: BREAK every recorded joint, REWRITE each segment's curve (same
/// plan endpoints, new Z), MOVE each movable group, REMAKE the joints.
///
/// WHY DISCONNECT FIRST — Revit maintains connections during a move: shifting a
/// connected pipe drags the fittings on it, and through them whatever they are
/// attached to. On a sprinklered branch that would mean every attempt to
/// re-pitch the pipe also hauls the cross main around. So the run is cut free
/// first, moved as exact geometry, and joined back up afterwards.
///
/// FAILURE POLICY — NOT ALL-OR-NOTHING, DELIBERATELY. Geometry failures throw
/// <see cref="ApplyError"/> and the caller rolls the whole run back: a
/// half-re-pitched run is worse than none. RECONNECT failures do not — by then
/// every piece is already in the right place and the only thing wrong is an open
/// joint, which is visible, listed by element id, and fixable with
/// Move/Connect/Align.
///
/// A run can therefore COMMIT with open joints in it. Such a model looks
/// geometrically perfect in every view but is functionally broken at each open
/// joint: no flow, no system inheritance, and a clash pass will not flag a gap
/// that small because nothing is actually clashing. The mitigation is that every
/// open joint is reported by element id and linkified — the model is never
/// silently wrong, but it IS silently open unless someone reads that report.
/// </summary>
internal static class SlopeApplier
{
    /// <summary>
    /// Starts the transaction with the silent-warning preprocessor attached.
    /// The swallower's cleared list is only meaningful to read after
    /// Commit/RollBack.
    ///
    /// This is precisely the operation (disconnect / reshape / reconnect) that
    /// generates disconnect warnings, which are the earliest signal a joint did
    /// not reseat — so discarding them unrecorded is this tool's own known weak
    /// spot, and every one is kept.
    /// </summary>
    public static (Transaction Transaction, WarningSwallower Swallower) StartTransaction(
        Document doc, string name)
    {
        Transaction t = new(doc, name);
        t.Start();

        WarningSwallower swallower = new();
        FailureHandlingOptions opts = t.GetFailureHandlingOptions();
        opts.SetFailuresPreprocessor(swallower);
        opts.SetForcedModalHandling(false);
        t.SetFailureHandlingOptions(opts);

        return (t, swallower);
    }

    // ------------------------------------------------------------------ helpers

    private static ElementId Eid(long value)
    {
#if REVIT2023_OR_EARLIER
        return new ElementId((int)value);
#else
        return new ElementId(value);
#endif
    }

    private static List<ElementId> IdList(IEnumerable<long> ids) => [.. ids.Select(Eid)];

    private static List<Connector> OpenEndConnectors(Element? elem)
        => elem is null
            ? []
            : MepConnectors.GetEndConnectors(elem).FindAll(c => !c.IsConnected);

    /// <summary>
    /// Every (connectorOnA, connectorOnB) pair directly joining the two — the
    /// same walk the Disconnect button does.
    /// </summary>
    private static List<(Connector A, Connector B)> SharedConnections(Element elemA, Element elemB)
    {
        List<(Connector, Connector)> pairs = [];
        ElementId bId = elemB.Id;

        foreach (Connector ca in MepConnectors.GetEndConnectors(elemA))
        {
            ConnectorSet refs;
            try
            {
                refs = ca.AllRefs;
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException)
            {
                continue;
            }

            foreach (Connector cb in refs)
            {
                Element? owner;
                try
                {
                    if (cb.ConnectorType != ConnectorType.End)
                        continue;
                    owner = cb.Owner;
                }
                catch (Autodesk.Revit.Exceptions.ApplicationException)
                {
                    continue;
                }

                if (owner is not null && owner.Id == bId)
                    pairs.Add((ca, cb));
            }
        }
        return pairs;
    }

    // -------------------------------------------------------------------- steps

    /// <summary>
    /// Cut every recorded joint, returning how many connections were ACTUALLY
    /// broken per pair.
    ///
    /// That count is what drives the remake, not the number of joints recorded.
    /// The two can differ: this disconnects every shared connection between a
    /// pair, and a pair joined at two connectors with only one recorded would
    /// otherwise have the second broken and never restored — silently, because
    /// the remake loop would not even look for it.
    /// </summary>
    public static Dictionary<(long A, long B), int> BreakJoints(
        Document doc, IReadOnlyList<Joint> joints, ApplyResult result)
    {
        HashSet<(long, long)> pairs =
        [
            .. joints.Select(j => (Math.Min(j.AId, j.BId), Math.Max(j.AId, j.BId)))
        ];

        Dictionary<(long, long), int> broken = [];

        foreach ((long aId, long bId) in pairs)
        {
            Element? elemA = doc.GetElement(Eid(aId));
            Element? elemB = doc.GetElement(Eid(bId));
            if (elemA is null || elemB is null)
                continue;

            foreach ((Connector ca, Connector cb) in SharedConnections(elemA, elemB))
            {
                try
                {
                    ca.DisconnectFrom(cb);
                }
                catch (Exception ex)
                {
                    throw new ApplyError(
                        $"could not disconnect element {aId} from element {bId} ({ex.Message}). "
                        + "Nothing can be re-pitched safely while it is still joined, so the run "
                        + "is being rolled back.");
                }

                broken[(aId, bId)] = broken.TryGetValue((aId, bId), out int n) ? n + 1 : 1;
                result.JointsBroken++;
            }
        }

        doc.Regenerate();
        return broken;
    }

    public static void RepitchSegments(Document doc, RePitch pitch, ApplyResult result)
    {
        foreach (SlopeSegment seg in pitch.Run.Segments)
        {
            Element? elem = doc.GetElement(Eid(seg.ElemId));
            if (elem is null)
                throw new ApplyError($"segment {seg.ElemId} is no longer in the model");

            Vec3 newP0 = pitch.NewPoint(seg.P0);
            Vec3 newP1 = pitch.NewPoint(seg.P1);

            if (elem.Location is not LocationCurve loc)
            {
                throw new ApplyError(
                    $"segment {seg.ElemId} has no LocationCurve to rewrite - re-pitching works by "
                    + "moving the two endpoints of the pipe's own curve, and an element placed by "
                    + "point has none");
            }

            try
            {
                loc.Curve = Line.CreateBound(newP0.ToXYZ(), newP1.ToXYZ());
            }
            catch (Exception ex)
            {
                throw new ApplyError($"could not re-pitch segment {seg.ElemId} ({ex.Message})");
            }

            result.Segments++;
        }

        doc.Regenerate();
    }

    public static bool MoveGroup(Document doc, SlopeGroup group, RePitch pitch, ApplyResult result)
    {
        if (group.Kind == GroupKind.Inline)
        {
            // Each in-line fitting sits at its own station, so each rotates
            // about its own pivot BY ITS OWN ANGLE - the old pitch can differ
            // per segment and a V-anchored run's two arms slope opposite ways -
            // and drops by its own amount.
            foreach (InlinePart part in group.Parts)
            {
                Element? fitting = doc.GetElement(Eid(part.ElemId));
                if (fitting is null)
                    throw new ApplyError($"fitting {part.ElemId} is no longer in the model");

                XYZ pivot = part.Pivot.ToXYZ();

                double theta = pitch.ThetaAt(pitch.Run.Station(part.Pivot));
                if (Math.Abs(theta) > 1.0e-9)
                {
                    Line axis = Line.CreateUnbound(pivot, pitch.AxisDir.ToXYZ());
                    try
                    {
                        ElementTransformUtils.RotateElement(doc, fitting.Id, axis, theta);
                    }
                    catch (Exception ex)
                    {
                        throw new ApplyError(
                            $"could not rotate in-line fitting {part.ElemId} to the new pitch "
                            + $"({ex.Message})");
                    }
                }

                // Corner elbows only: a SECOND rotation, about the axis the
                // first one just landed the parent-facing connector on, to bring
                // the child-facing connector as close as it can get to its own
                // leg's independently required direction. Never set for an
                // ordinary in-line fitting.
                if (part.RollAxis is not null && Math.Abs(part.RollAngle) > 1.0e-9)
                {
                    Line rollLine = Line.CreateUnbound(
                        (part.RollPivot ?? part.Pivot).ToXYZ(), part.RollAxis.Value.Normalize().ToXYZ());
                    try
                    {
                        ElementTransformUtils.RotateElement(doc, fitting.Id, rollLine, part.RollAngle);
                    }
                    catch (Exception ex)
                    {
                        throw new ApplyError(
                            $"could not roll corner elbow {part.ElemId} to match its child leg "
                            + $"({ex.Message})");
                    }
                }

                if (Math.Abs(part.Dz) > 1.0e-9)
                {
                    try
                    {
                        ElementTransformUtils.MoveElement(doc, fitting.Id, new XYZ(0.0, 0.0, part.Dz));
                    }
                    catch (Exception ex)
                    {
                        throw new ApplyError(
                            $"could not move in-line fitting {part.ElemId} ({ex.Message})");
                    }
                }

                foreach ((List<long> ids, Vec3 delta) in part.FollowSets)
                {
                    if (ids.Count == 0 || delta.GetLength() <= 1.0e-9)
                        continue;

                    try
                    {
                        ElementTransformUtils.MoveElements(doc, IdList(ids), delta.ToXYZ());
                    }
                    catch (Exception ex)
                    {
                        throw new ApplyError(
                            $"could not move the {ids.Count} element(s) hanging off fitting "
                            + $"{part.ElemId} ({ex.Message})");
                    }
                }
            }
        }
        else if (group.Kind == GroupKind.Rider)
        {
            if (Math.Abs(group.Dz) > 1.0e-9)
            {
                try
                {
                    // Moved as one selection rather than element by element: a
                    // multi-element move is rigid, where moving the members one
                    // at a time would let Revit drag each one's neighbours and
                    // double up the displacement.
                    ElementTransformUtils.MoveElements(
                        doc, IdList(group.MemberIds), new XYZ(0.0, 0.0, group.Dz));
                }
                catch (Exception ex)
                {
                    throw new ApplyError(
                        $"could not move the {group.Size} element(s) hanging off the run at "
                        + $"element {group.RootId} ({ex.Message})");
                }
            }
        }
        else
        {
            return false;
        }

        result.Groups++;
        return true;
    }

    /// <summary>
    /// Put back every joint that was broken, matching by position. Connectors
    /// are re-found here, never carried across the moves.
    ///
    /// WHICH PAIR OF CONNECTORS — not simply the closest open pair. A short pipe
    /// between two fittings has an open connector at BOTH ends, and so does a
    /// tee; the nearest pair is not reliably the pair that was actually joined,
    /// and joining the wrong two would look entirely plausible afterwards. So
    /// every candidate pair must first close to within tolerance, and among
    /// those the winner is the one landing nearest to where the joint used to
    /// be. Nothing moves more than a few millimetres here, so the old position
    /// is a strong hint — it just cannot be used as an exact target, because a
    /// rotated fitting does not put its outlet back where the old one was.
    /// </summary>
    public static void RemakeJoints(
        Document doc, IReadOnlyList<Joint> joints, double tol,
        ApplyResult result, IReadOnlyDictionary<(long A, long B), int> broken)
    {
        Dictionary<(long A, long B), List<Vec3>> byPair = [];
        foreach (Joint j in joints)
        {
            (long, long) key = (Math.Min(j.AId, j.BId), Math.Max(j.AId, j.BId));
            Vec3 midpoint = j.AOrigin.Add(j.BOrigin).Multiply(0.5);
            if (!byPair.TryGetValue(key, out List<Vec3>? hints))
                byPair[key] = hints = [];
            hints.Add(midpoint);
        }

        foreach (((long aId, long bId), List<Vec3> hints) in byPair)
        {
            // Driven by what was really broken, not by what was recorded. Extra
            // breaks get an unhinted slot rather than being quietly abandoned.
            int count = broken.TryGetValue((aId, bId), out int n) ? n : hints.Count;

            List<Vec3?> slots = [.. hints.Take(count).Select(h => (Vec3?)h)];
            while (slots.Count < count)
                slots.Add(null);

            if (doc.GetElement(Eid(aId)) is null || doc.GetElement(Eid(bId)) is null)
            {
                result.OpenJoints.Add((aId, bId, "one of the elements is gone"));
                continue;
            }

            foreach (Vec3? hint in slots)
            {
                Element? elemA = doc.GetElement(Eid(aId));
                Element? elemB = doc.GetElement(Eid(bId));

                (double Drift, double Gap)? bestScore = null;
                Connector? bestA = null;
                Connector? bestB = null;
                double? closestGap = null;

                foreach (Connector ca in OpenEndConnectors(elemA))
                {
                    foreach (Connector cb in OpenEndConnectors(elemB))
                    {
                        double gap = ca.Origin.DistanceTo(cb.Origin);
                        if (closestGap is null || gap < closestGap)
                            closestGap = gap;
                        if (gap > tol)
                            continue;

                        double drift = hint is null
                            ? 0.0
                            : ca.Origin.Add(cb.Origin).Multiply(0.5).ToVec3().DistanceTo(hint.Value);

                        (double, double) score = (drift, gap);
                        if (bestScore is null
                            || score.Item1 < bestScore.Value.Drift
                            || (score.Item1 == bestScore.Value.Drift && score.Item2 < bestScore.Value.Gap))
                        {
                            bestScore = score;
                            bestA = ca;
                            bestB = cb;
                        }
                    }
                }

                if (closestGap is null)
                {
                    result.OpenJoints.Add((aId, bId, "no open connectors left to join"));
                    break;
                }

                if (bestA is null || bestB is null)
                {
                    result.OpenJoints.Add((aId, bId,
                        $"closest open connectors ended up {SlopeSolver.Mm1(closestGap)} apart, "
                        + $"past the {SlopeSolver.Mm1(tol)} reconnect tolerance - most likely one "
                        + "side of this joint is a group the dry run said would not move"));
                    break;
                }

                try
                {
                    bestA.ConnectTo(bestB);
                    result.JointsRemade++;
                    doc.Regenerate();
                }
                catch (Exception ex)
                {
                    result.OpenJoints.Add((aId, bId, $"reconnect refused ({ex.Message})"));
                    break;
                }
            }
        }
    }

    // --------------------------------------------------------------------- core

    /// <summary>
    /// Performs a single-run re-pitch. Throws <see cref="ApplyError"/> on any
    /// geometry failure — the caller must roll the transaction back. Reconnect
    /// failures are collected on the result instead.
    /// </summary>
    public static ApplyResult Apply(
        Document doc, RePitch pitch, IReadOnlyList<SlopeGroup> groups,
        IReadOnlyList<Joint> joints, double reconnectTol)
    {
        ApplyResult result = new();
        List<SlopeGroup> movable = SlopeGroupUtil.Movable(groups);

        int steps = 2 + movable.Count;
        using (ProgressDialog progress = new("Slope Branch - step {0} of {1}", cancellable: true))
        {
            Dictionary<(long A, long B), int> broken = BreakJoints(doc, joints, result);
            progress.Update(1, steps);

            if (progress.Cancelled)
                throw new ApplyError("cancelled before anything moved - rolling back");

            RepitchSegments(doc, pitch, result);
            progress.Update(2, steps);

            for (int index = 0; index < movable.Count; index++)
            {
                if (progress.Cancelled)
                {
                    throw new ApplyError(
                        $"cancelled after {index} of {movable.Count} group(s) - the whole run is "
                        + "being rolled back");
                }

                MoveGroup(doc, movable[index], pitch, result);
                progress.Update(3 + index, steps);
            }

            doc.Regenerate();
            RemakeJoints(doc, joints, reconnectTol, result, broken);
            doc.Regenerate();
        }

        return result;
    }

    /// <summary>
    /// Applies several independent jobs inside ONE caller-owned transaction.
    ///
    /// ALL-OR-NOTHING ACROSS JOBS. A geometry failure anywhere throws
    /// <see cref="ApplyError"/> and the caller rolls back every job, including
    /// ones that had already finished — a batch that half-applied would leave
    /// some branches at the new slope and some at the old with no record of
    /// which, and re-running would compound it.
    ///
    /// Each job is applied WHOLE before the next starts rather than interleaving
    /// the break/move/remake phases across jobs. The callers guarantee the jobs
    /// write disjoint element sets, so ordering between them cannot matter — and
    /// one at a time keeps a failure attributable to the branch that caused it.
    ///
    /// Reconnect failures do NOT abort: they accumulate on the combined result
    /// exactly as they do for a single run.
    /// </summary>
    public static ApplyResult ApplyMany(
        Document doc, IReadOnlyList<SlopeJob> jobs, double reconnectTol)
    {
        ApplyResult total = new();

        using ProgressDialog progress =
            new("Slope Armover - branch {0} of {1}", cancellable: true);

        for (int i = 0; i < jobs.Count; i++)
        {
            if (progress.Cancelled)
            {
                throw new ApplyError(
                    $"cancelled after {i} of {jobs.Count} branch(es) - the whole batch is "
                    + "being rolled back");
            }

            progress.Update(i, jobs.Count);

            ApplyResult one = ApplyOne(doc, jobs[i], reconnectTol);

            total.Branches += 1;
            total.Segments += one.Segments;
            total.Groups += one.Groups;
            total.JointsBroken += one.JointsBroken;
            total.JointsRemade += one.JointsRemade;
            total.OpenJoints.AddRange(one.OpenJoints);
            total.FittingsNotRebuilt.AddRange(one.FittingsNotRebuilt);
        }

        progress.Update(jobs.Count, jobs.Count);
        return total;
    }

    /// <summary>
    /// One job's break / re-pitch / move / remake, without a progress dialog of
    /// its own — <see cref="ApplyMany"/> owns the bar, so a batch shows branch
    /// progress rather than a per-branch bar flickering in and out.
    /// </summary>
    private static ApplyResult ApplyOne(Document doc, SlopeJob job, double reconnectTol)
    {
        ApplyResult result = new();

        Dictionary<(long A, long B), int> broken = BreakJoints(doc, job.Joints, result);

        RepitchSegments(doc, job.Pitch!, result);

        foreach (SlopeGroup group in SlopeGroupUtil.Movable(job.Groups))
            MoveGroup(doc, group, job.Pitch!, result);

        doc.Regenerate();
        RemakeJoints(doc, job.Joints, reconnectTol, result, broken);
        doc.Regenerate();

        // AFTER the ordinary remake, which has already rejoined the run to the
        // OLD fitting - this deletes that and builds a fresh one, rather than
        // trying to intercept the generic joint machinery. Exactly the order
        // Slope Through uses.
        //
        // Per job rather than after the whole batch: jobs write disjoint element
        // sets, so ordering between them cannot matter, and doing it here keeps a
        // failure attributable to the branch that caused it.
        if (job.FixedRebuild is not null)
        {
            // Survivable, like the tee below: a refusal rolls back only the fitting.
            string? why = SlopeThrough.RebuildFixedElbow(job.FixedRebuild, doc);
            if (why is not null)
                result.FittingsNotRebuilt.Add((job.ReferenceId, why));
        }
        else if (job.FixedTeeRebuild is not null)
        {
            // Survivable: a refusal rolls back only the fitting and reports.
            string? why = SlopeThrough.RebuildFixedTee(job.FixedTeeRebuild, doc);
            if (why is not null)
                result.FittingsNotRebuilt.Add((job.ReferenceId, why));
        }

        return result;
    }
}
