using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using Ribbit.Geometry;
using Ribbit.Native.Revit;
using Ribbit.Native.Settings;
using Ribbit.Native.Ui;
using OperationCanceledException = Autodesk.Revit.Exceptions.OperationCanceledException;
// Autodesk.Revit.DB has a Units type too; the geometry one is meant here.
using Units = Ribbit.Geometry.Units;

namespace Ribbit.Native.Tools;

/// <summary>
/// Slope Armover. Ported from
/// _Ribbit.tab/PH_FP Tools.panel/SlopeArmover.pushbutton/script.py.
///
/// Puts a slope on one or several armovers — the horizontal takeoffs running
/// from a tee on the branch line out to an elbow, sprig and head — with ONE pick
/// per armover.
///
/// Slope Branch already re-pitches an armover correctly if you pick it and click
/// the tee end as fixed. This button removes the second click by working out
/// which end IS the tee end. An armover connects to the system at exactly one
/// end — the branch side — and terminates at its head a handful of elements past
/// the other, so the ends are told apart by walking what hangs off each with the
/// SAME rule <see cref="SlopeEngine.Closure"/> uses to classify groups:
///
///   tee end   the closure reaches a pipe longer than the foreign-run length AND
///             keeps growing past the small-group size — that is the branch line
///             and the rest of the system.
///   head end  the closure ends after a few elements (elbow, sprig, head)
///             without ever reaching a long run of pipe.
///
/// The detected element is then handed to <see cref="SlopeMulti.BuildJob"/> as
/// the fixed reference, so its group is PINNED rather than re-inferred and the
/// run anchors at the tee. Ambiguous armovers degrade per pick, never per batch.
/// </summary>
internal static class SlopeArmoverTool
{
    private const string DialogTitle = "Slope Armover";

    /// <summary>
    /// An end connector of the run sits exactly at the run's extreme station;
    /// anything meaningfully further in belongs to a mid-run coupling. ~15 mm.
    /// </summary>
    private const double EndSnapFt = 0.05;

    /// <summary>
    /// Below this a re-pitch would break and remake every joint on an armover to
    /// move nothing. 0.1 mm in internal feet.
    /// </summary>
    private static readonly double NoOpFt = 0.1 / Units.MmPerFt;

    // ------------------------------------------------------------- detection

    /// <summary>
    /// Every DISTINCT element beyond the run at each end, keyed by id so a
    /// repeated AllRefs entry for one element is not counted twice.
    ///
    /// Normally exactly one per end. They are COLLECTED rather than letting the
    /// last one found silently overwrite the rest, so an unusual multi-owner
    /// connector is surfaced instead of guessed at.
    /// </summary>
    private static (List<Element> AtMin, List<Element> AtMax) ExternalOwnersAtEnds(
        SlopeRun run, IReadOnlyDictionary<long, Element> elemById, ISet<long> branchIds)
    {
        Dictionary<long, Element> atMin = [];
        Dictionary<long, Element> atMax = [];

        foreach (SlopeSegment seg in run.Segments)
        {
            if (!elemById.TryGetValue(seg.ElemId, out Element? elem))
                continue;

            foreach (Connector conn in MepConnectors.GetAllConnectors(elem))
            {
                double s = run.Station(conn.Origin.ToVec3());

                Dictionary<long, Element> bucket;
                if (Math.Abs(s - run.SMin) <= EndSnapFt)
                    bucket = atMin;
                else if (Math.Abs(s - run.SMax) <= EndSnapFt)
                    bucket = atMax;
                else
                    continue;

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
                        if (owner is null)
                            continue;

                        long oid = RevitCompat.IdKey(owner.Id);
                        if (branchIds.Contains(oid))
                            continue;

                        bucket[oid] = owner;
                    }
                    catch (Exception)
                    {
                        // Skip an unreadable reference.
                    }
                }
            }
        }

        return ([.. atMin.Values], [.. atMax.Values]);
    }

    /// <summary>
    /// Does the closure beyond this end reach the rest of the system?
    ///
    /// Decided with the SAME walk and the SAME stop rule
    /// <see cref="SlopeEngine.Closure"/> uses, so the anchor choice can never
    /// disagree with how the groups get classified afterwards.
    ///
    /// The Python guarded against engine._closure()'s tuple growing another
    /// value — it had already gone 3 to 4 and silently broken this. That guard
    /// is not ported because it cannot happen here: ClosureResult is a named
    /// record and the compiler enforces the shape.
    ///
    /// DIVERGENCE FROM THE PYTHON, deliberate. It answered this with hit_cap or
    /// stopped_early alone, both of which are SIZE tests. A branch whose closure
    /// finishes inside the small-assembly threshold then reads as a dead end
    /// even though it plainly reaches the branch line — measured live at exactly
    /// 15 elements against a threshold of 15, which made every armover fall back
    /// to a manual pick and defeated the whole point of the button. Reaching a
    /// long pipe is the signal that actually distinguishes a tee end from a head
    /// end, so <see cref="SlopeEngine.ClosureResult.ReachesSystem"/> is used.
    /// </summary>
    private static (bool LeadsIn, string Why, int Size) LeadsIntoSystem(
        Element? owner, ISet<long> branchIds, SlopeGroupParams gp)
    {
        if (owner is null)
            return (false, "open end - nothing connected", 0);

        SlopeEngine.ClosureResult result = SlopeEngine.Closure(
            owner, branchIds, gp.MaxGroupElements, gp.ForeignRun, gp.SmallGroupElements);

        int size = result.Order.Count;

        // Kept SHORT. These land in a table cell repeated once per armover, and
        // read again after "Tee end:" in each detail heading - a full sentence
        // of justification in either place is noise by the second row.
        if (result.HitCap)
            return (true, $"over {gp.MaxGroupElements} connected elements", size);

        if (result.StoppedEarly)
            return (true, $"pipe over {Units.Mm(gp.ForeignRun)}, still growing at {size}", size);

        if (result.LongSeen)
            return (true, $"reaches a pipe over {Units.Mm(gp.ForeignRun)} ({size} elements)", size);

        if (result.ReachesSystem(gp.SmallGroupElements))
            return (true, $"{size} elements, past the small-assembly size", size);

        // The old wording claimed "no long pipe" on every negative, including
        // closures that HAD seen one and simply never grew past the threshold.
        // It now only says that when it is true.
        return (false, $"{size} element(s), none longer than {Units.Mm(gp.ForeignRun)} - "
                       + "this assembly just ends", size);
    }

    /// <summary>
    /// One end's verdict, given every distinct element found beyond it.
    ///
    ///   0 owners : open end.
    ///   1 owner  : the normal case — walked with <see cref="LeadsIntoSystem"/>.
    ///   2+       : an unusual multi-owner connector. Rather than pick one of
    ///              several candidates to walk from arbitrarily, this end is
    ///              conservatively treated as leading into the system with no
    ///              single owner identified — the same "ambiguity degrades
    ///              toward pinned, never toward guessing what is safe to move"
    ///              convention the rest of slopelib uses.
    /// </summary>
    private static (bool LeadsIn, string Why, Element? Owner, int Size) ResolveEnd(
        IReadOnlyList<Element> owners, ISet<long> branchIds, SlopeGroupParams gp)
    {
        if (owners.Count == 0)
            return (false, "open end - nothing connected", null, 0);

        if (owners.Count > 1)
        {
            return (true, $"{owners.Count} distinct elements share this end's connector - "
                          + "treated as leading into the system rather than guessing which one "
                          + "to follow", null, 0);
        }

        (bool leads, string why, int size) = LeadsIntoSystem(owners[0], branchIds, gp);
        return (leads, why, owners[0], size);
    }

    /// <summary>
    /// How many times bigger one end's closure must be than the other's before
    /// size alone names the tee end, and the minimum absolute gap that must also
    /// hold.
    ///
    /// An armover terminates at its head a handful of elements in — elbow, sprig,
    /// head — while the tee end reaches the branch line and everything on it.
    /// That gap is normally 4x or more, so requiring BOTH a ratio and a real
    /// element count keeps two similarly-sized assemblies from being separated
    /// by noise.
    /// </summary>
    private const int LopsidedRatio = 2;
    private const int LopsidedGap = 4;

    /// <summary>
    /// Fewest connected elements an end may reach and still be called the
    /// branch, when the OTHER end is open and there is nothing to compare
    /// against.
    ///
    /// This is what keeps the inverted case honest: an armover whose tee is not
    /// connected yet but whose head carries an elbow, sprig and head. That side
    /// is a handful of elements, so it falls short here and the user is asked
    /// rather than having a head assembly pinned as a branch.
    /// </summary>
    private const int MinBranchClosure = 4;

    /// <summary>
    /// The element just beyond the tee end, established by walking both ends.
    /// Exactly one of reference/error comes back set.
    ///
    /// An armover whose ends BOTH dead-end (not yet connected to its branch)
    /// falls back to asking, once, for that armover only — the same element
    /// Slope Branch Multi's second click supplies.
    /// </summary>
    /// <remarks>
    /// The <see cref="SlopeMulti.RunScan"/> comes back with the reference so the
    /// caller can hand it to BuildJob. Following the run, building its segments
    /// and solving the straight line are the same three steps for detection and
    /// for solving — doing them once per armover instead of twice halves the
    /// geometry reads this tool makes.
    /// </remarks>
    private static (Element? Reference, string? Why, string? Error, SlopeMulti.RunScan? Scan)
        DetectReference(
            IPicker picker, Document doc, int index, Element seed,
            SlopeParams parameters, SlopeGroupParams gp)
    {
        (SlopeMulti.RunScan? scan, string? reason) = SlopeMulti.ScanRun(doc, seed, parameters);
        if (scan is null)
            return (null, null, reason, null);

        (List<Element> atMin, List<Element> atMax) =
            ExternalOwnersAtEnds(scan.Run, scan.ElemById, scan.BranchIds);

        (bool minSys, string minWhy, Element? ownerMin, int minSize) =
            ResolveEnd(atMin, scan.BranchIds, gp);
        (bool maxSys, string maxWhy, Element? ownerMax, int maxSize) =
            ResolveEnd(atMax, scan.BranchIds, gp);

        // RELATIVE FALLBACK, in two ORDERED options. Every absolute test above
        // needs a threshold - a pipe length, or an element count - and a model
        // can sit under all of them while still plainly having a tee end: a
        // branch carrying closely-spaced tees has no segment over the
        // foreign-run length, and a compact system finishes inside the
        // small-assembly size. Comparing the ends against each other needs no
        // threshold at all.
        //
        // Both options run only when neither end qualified outright, so neither
        // can override a positive answer.

        // ---- OPTION 1: BOTH ENDS CONNECTED - the complete armover ----------
        // Tried FIRST, and it is the one that should normally decide. Both
        // closures are real measurements, so they can be compared directly: an
        // armover terminates at its head a handful of elements in, while the tee
        // end reaches the branch and everything on it.
        if (!minSys && !maxSys && ownerMin is not null && ownerMax is not null)
        {
            int bigger = Math.Max(minSize, maxSize);
            int smaller = Math.Min(minSize, maxSize);

            if (bigger >= smaller * LopsidedRatio && bigger - smaller >= LopsidedGap)
            {
                bool minIsBigger = minSize > maxSize;
                string note = $"larger closure: {bigger} elements against {smaller} at the other end";

                if (minIsBigger)
                    (minSys, minWhy) = (true, note);
                else
                    (maxSys, maxWhy) = (true, note);
            }
        }

        // ---- OPTION 2: ONE END OPEN - a stub not fitted out yet ------------
        // Only reached when option 1 could not apply or could not decide. An
        // armover hangs off the branch at ONE end, so if a far end has nothing
        // connected at all, the connected end is the branch side by
        // construction - there is nothing else it could be.
        //
        // Still floored at MinBranchClosure so the inverted case (open tee,
        // fitted head) is asked about rather than guessed.
        if (!minSys && !maxSys && (ownerMin is null) != (ownerMax is null))
        {
            bool minIsConnected = ownerMin is not null;
            int size = minIsConnected ? minSize : maxSize;

            if (size >= MinBranchClosure)
            {
                string note = $"other end open; this end reaches {size} connected element(s)";

                if (minIsConnected)
                    (minSys, minWhy) = (true, note);
                else
                    (maxSys, maxWhy) = (true, note);
            }
        }

        if (minSys && maxSys)
        {
            return (null, null,
                $"both ends lead into the rest of the system (one: {minWhy}; other: {maxWhy}) - "
                + "this pipe bridges two runs and is not an armover. Use Slope Through on it "
                + "instead.", scan);
        }

        if (minSys || maxSys)
        {
            Element? owner = minSys ? ownerMin : ownerMax;
            string why = minSys ? minWhy : maxWhy;

            if (owner is null)
            {
                // The system-side end is the ambiguous multi-owner case - there
                // is no single element to hand back as the fixed reference.
                return (null, null,
                    "the end that leads into the system has more than one element sharing its "
                    + $"connector, so the fixed reference can't be identified automatically "
                    + $"({why})", scan);
            }

            return (owner, why, null, scan);
        }

        // Neither end reaches the system - ask, once, for this armover only.
        Element? picked = PickSingle(picker,
            $"Armover {index}: neither end reaches the rest of the system, so the fixed end "
            + "can't be auto-detected. Pick the element that must NOT MOVE - normally the tee "
            + "at the branch line (Esc skips this armover)");

        if (picked is null)
        {
            return (null, null,
                $"neither end reaches the rest of the system (one: {minWhy}; other: {maxWhy}) "
                + "and no fixed element was picked", scan);
        }

        return (Picking.ResolveHost(doc, picked), "picked by hand", null, scan);
    }

    // -------------------------------------------------------------- conflicts

    /// <summary>
    /// Everything this job WRITES: run pipes and moving-group members get new
    /// geometry, and every joint partner is disconnected and reconnected.
    ///
    /// WHY THIS TEST IS ITS OWN, NOT SlopeMulti's moved-vs-touched. Armovers off
    /// one branch line each WALK the shared branch to prove their tee end
    /// reaches the system, and that walk routinely passes through the
    /// NEIGHBOURING armovers before it has seen enough to stop. So armover A's
    /// foreign closure legitimately CONTAINS armover B's moving elements, and
    /// the stock moved-vs-touched test would refuse every pair of neighbours.
    ///
    /// Those foreign members are pure READS that contribute nothing to A's
    /// numbers — a FOREIGN group never moves, and only its attachment points,
    /// which sit on A's own run, enter the solve. What must be exclusive is what
    /// a job WRITES, so writes-vs-writes is the exact invariant. It still
    /// refuses a double-picked armover or a Y-fed pair sharing a tee.
    /// </summary>
    private static HashSet<long> WrittenIds(SlopeJob job)
    {
        HashSet<long> ids = [.. job.BranchIds];

        foreach (SlopeGroup group in SlopeGroupUtil.Movable(job.Groups))
            ids.UnionWith(group.MemberIds);

        foreach (Joint joint in job.Joints)
        {
            ids.Add(joint.AId);
            ids.Add(joint.BId);
        }

        return ids;
    }

    private static List<(SlopeJob A, SlopeJob B, List<long> Shared)> FindConflicts(
        IReadOnlyList<SlopeJob> jobs)
    {
        List<(SlopeJob, SlopeJob, List<long>)> outp = [];
        List<SlopeJob> good = [.. jobs.Where(j => j.Ok)];

        // Built ONCE per job, not once per pair. The comparison is O(n^2) by
        // nature, but recomputing each job's write set inside the inner loop
        // made the WORK O(n^2) too - walking every group and joint of both jobs
        // on every comparison.
        List<HashSet<long>> written = [.. good.Select(WrittenIds)];

        for (int i = 0; i < good.Count; i++)
        {
            for (int k = i + 1; k < good.Count; k++)
            {
                // Intersect the smaller set into a copy - the cost is the size
                // of the smaller side, not of whichever happens to be first.
                bool iSmaller = written[i].Count <= written[k].Count;
                HashSet<long> shared = new(iSmaller ? written[i] : written[k]);
                shared.IntersectWith(iSmaller ? written[k] : written[i]);

                if (shared.Count > 0)
                    outp.Add((good[i], good[k], [.. shared.OrderBy(x => x)]));
            }
        }

        return outp;
    }

    /// <summary>
    /// <see cref="SlopeMulti.PinReference"/>'s note says the reference was
    /// picked; here it was detected. Same pin, honest wording.
    ///
    /// The group is found by its <see cref="SlopeGroup.PinnedAsReference"/> flag,
    /// NOT by pattern-matching the note's prose — text coupling would silently
    /// stop firing the day that wording changes, and the report would go on
    /// claiming a detected reference was picked by hand.
    /// </summary>
    private static void RewordPinNote(SlopeJob job)
    {
        foreach (SlopeGroup group in job.Groups)
        {
            if (!group.PinnedAsReference || group.Notes.Count == 0)
                continue;

            group.Notes[group.Notes.Count - 1] =
                $"holds element {job.ReferenceId}, the auto-detected tee end into the branch "
                + "line - pinned as the fixed end";
        }
    }

    // ------------------------------------------------------------------ flow

    /// <summary>
    /// The largest vertical move this job would make, in feet.
    ///
    /// One pass answers both questions the flow asks — "is this already at the
    /// slope?" and "what is the biggest move?" — which were previously three
    /// separate walks of every segment (twice to partition the jobs, once more
    /// to size the confirmation).
    /// </summary>
    private static double LargestMove(SlopeJob job)
    {
        RePitch pitch = job.Pitch!;
        double biggest = 0.0;

        foreach (SlopeSegment seg in pitch.Run.Segments)
        {
            biggest = Math.Max(biggest, Math.Abs(pitch.Dz(seg.P0)));
            biggest = Math.Max(biggest, Math.Abs(pitch.Dz(seg.P1)));
        }

        return biggest;
    }

    private static Element? PickSingle(IPicker picker, string prompt)
        => picker.PickElement(prompt, new AnyElement()).Element;

    private sealed class AnyElement : ISelectionFilter
    {
        public bool AllowElement(Element elem) => true;

        public bool AllowReference(Reference reference, XYZ position) => true;
    }

    private static bool Confirm(
        IReadOnlyList<SlopeJob> runnable,
        IReadOnlyList<SlopeJob> skipped,
        IReadOnlyList<SlopeJob> unchanged,
        double biggest)
    {
        int segments = runnable.Sum(j => j.Run!.Segments.Count);
        int joints = runnable.Sum(j => j.Joints.Count);

        List<string> lines =
        [
            $"Slope {runnable.Count} armover(s) to "
            + $"{SlopeSolver.FmtSlope(runnable[0].Pitch!.NewRateAway)}?",
            "",
            "  Every tee end is held fixed - the branch line is undisturbed",
            $"  {segments} pipe segment(s) in total",
            $"  {joints} joint(s) broken and remade",
            $"  Largest move on any pipe: {SlopeSolver.Mm1(biggest)}",
        ];

        if (unchanged.Count > 0)
        {
            lines.Add("");
            lines.Add($"{unchanged.Count} armover(s) are already at this slope and will be "
                      + "left alone.");
        }

        if (skipped.Count > 0)
        {
            lines.Add("");
            lines.Add($"{skipped.Count} armover(s) could not be solved and will be skipped - "
                      + "see the report.");
        }

        lines.Add("");
        lines.Add("All of it is one transaction: a geometry failure on any armover rolls back "
                  + "every armover, and one Ctrl+Z undoes the whole run.");

        return RibbitUi.Confirm(string.Join("\n", lines), DialogTitle);
    }

    /// <summary>Shift+Click — settings only, shared with Slope Branch.</summary>
    public static void RunSettingsOnly()
    {
        SlopeSettings saved = SlopeSettings.Load();
        SlopeSettingsWindow window = new(saved);
        if (window.ShowDialog() != true || window.Result is null)
            return;

        window.Result.Save();
        RibbitUi.Notify("Slope Armover settings saved (shared with Slope Branch).", DialogTitle);
    }

    public static void Run(UIDocument uidoc)
    {
        RevitLinks.EnsureInitialized();
        Run(uidoc.Document, new RevitPicker(uidoc));
    }

    /// <summary>The whole command, on any picker (the test suite's way in).</summary>
    internal static void Run(Document doc, IPicker picker)
    {

        List<Element> seeds = [];
        while (true)
        {
            string ordinal = seeds.Count > 0 ? "another" : "an";
            Element? seed = PickSingle(picker,
                $"Armover {seeds.Count + 1}: pick ONE pipe on {ordinal} ARMOVER to slope - tee, "
                + "elbow and sprig are found automatically (Esc when you have picked them all)");

            if (seed is null)
                break;

            seeds.Add(Picking.ResolveHost(doc, seed));
        }

        if (seeds.Count == 0)
            return;

        SlopeSettings saved = SlopeSettings.Load();
        SlopeSettingsWindow settingsWindow = new(saved);
        if (settingsWindow.ShowDialog() != true || settingsWindow.Result is null)
            return;

        SlopeSettings chosen = settingsWindow.Result;
        chosen.Save();

        SlopeParams parameters = chosen.AsFeet();
        SlopeGroupParams groupParams = chosen.GroupParams();

        List<SlopeJob> jobs = [];
        Dictionary<long, ElementId> idMap = [];
        Dictionary<int, string> detectedWhy = [];

        // Detection walks the connected system out from BOTH ends of every
        // armover, which on a large branch can each reach the group cap. With
        // several armovers picked and no feedback, the gap between the settings
        // dialog and the report reads as a hang.
        using (ProgressDialog pb = new("Detecting tee ends - {0} of {1}", cancellable: false))
        {
            for (int i = 0; i < seeds.Count; i++)
            {
                pb.Update(i, seeds.Count);

                int number = i + 1;
                Element seed = seeds[i];
                idMap[RevitCompat.IdKey(seed.Id)] = seed.Id;

                (Element? reference, string? why, string? error, SlopeMulti.RunScan? scan) =
                    DetectReference(picker, doc, number, seed, parameters, groupParams);

                if (reference is null)
                {
                    // A pick whose tee end could not be established still gets a
                    // row in the report - it must not vanish silently.
                    jobs.Add(SlopeJob.Failed(number, seed, error));
                    continue;
                }

                idMap[RevitCompat.IdKey(reference.Id)] = reference.Id;

                // The scan is reused, not recomputed - see DetectReference.
                SlopeJob job = SlopeMulti.BuildJob(
                    doc, number, seed, reference, parameters, groupParams, scan);

                if (job.Ok && why != "picked by hand")
                    RewordPinNote(job);

                if (why is not null)
                    detectedWhy[number] = why;

                jobs.Add(job);
            }

            pb.Update(seeds.Count, seeds.Count);
        }

        List<(SlopeJob A, SlopeJob B, List<long> Shared)> conflicts = FindConflicts(jobs);

        RibbitUi.ShowReport($"{DialogTitle} - Dry Run",
            SlopeArmoverReport.Build(jobs, conflicts, detectedWhy, chosen, idMap));

        if (conflicts.Count > 0)
        {
            RibbitUi.Alert(
                $"{conflicts.Count} pair(s) of picked armovers would write the same element(s).\n\n"
                + "Usually the same armover picked twice, or two takeoffs sharing a tee. See the "
                + "overlap table in the report; nothing is applied while any pair conflicts.",
                DialogTitle);
            return;
        }

        // ONE pass, one Dz walk per job. Partitioning with two separate
        // AlreadyDone predicates walked every segment of every job twice, and
        // the confirmation then walked them a third time to size the move.
        List<SlopeJob> runnable = [];
        List<SlopeJob> skipped = [];
        List<SlopeJob> unchanged = [];
        double biggestMove = 0.0;

        foreach (SlopeJob job in jobs)
        {
            if (!job.Ok)
            {
                skipped.Add(job);
                continue;
            }

            double move = LargestMove(job);
            if (move > NoOpFt)
            {
                runnable.Add(job);
                biggestMove = Math.Max(biggestMove, move);
            }
            else
            {
                unchanged.Add(job);
            }
        }

        if (runnable.Count == 0)
        {
            RibbitUi.Alert(
                "Nothing to change.\n\n"
                + $"{unchanged.Count} armover(s) already at this slope, {skipped.Count} could "
                + "not be solved - each one's reason is in the report.",
                DialogTitle);
            return;
        }

        if (!Confirm(runnable, skipped, unchanged, biggestMove))
        {
            RibbitUi.Notify("Cancelled - nothing was modified.", DialogTitle);
            return;
        }

        (Transaction transaction, WarningSwallower swallower) =
            SlopeApplier.StartTransaction(doc, DialogTitle);

        ApplyResult outcome;
        using (transaction)
        {
            // Nothing but ApplyMany and Commit goes inside the try: reporting the
            // result must not be able to land in a handler whose first act is to
            // roll back a transaction that has already been committed.
            try
            {
                outcome = SlopeApplier.ApplyMany(doc, runnable, chosen.ReconnectTolFt);

                // Never report a run Revit rolled back at commit as done.
                if (transaction.Commit() != TransactionStatus.Committed)
                {
                    ReportCleared(swallower);
                    RibbitUi.Notify("Revit rolled the change back when it was committed - nothing was "
                                    + "modified. See the message Revit showed.", DialogTitle);
                    return;
                }
            }
            catch (ApplyError ex)
            {
                transaction.RollBack();
                ReportCleared(swallower);
                RibbitUi.Alert(
                    "Rolled back - nothing was modified, including armovers that had already "
                    + $"finished. {ex.Message}", DialogTitle);
                return;
            }
            catch (Exception ex)
            {
                transaction.RollBack();
                ReportCleared(swallower);
                RibbitUi.Alert(
                    $"Rolled back - nothing was modified. Unexpected error: {ex.Message}",
                    DialogTitle);
                return;
            }
        }

        ReportCleared(swallower);

        RibbitUi.Notify(
            $"Sloped {outcome.Branches} armover(s): {outcome.Segments} segment(s), "
            + $"{outcome.Groups} group(s), {outcome.JointsRemade} of {outcome.JointsBroken} "
            + "joint(s) remade."
            + (skipped.Count > 0 ? $" {skipped.Count} skipped." : "")
            + (unchanged.Count > 0 ? $" {unchanged.Count} already at this slope." : "")
            // Said here rather than only in a report: the run IS sloped, so this
            // dialog is the only place the leftover gap gets mentioned at all.
            + (outcome.FittingsNotRebuilt.Count > 0
                ? $" {outcome.FittingsNotRebuilt.Count} anchored-end fitting(s) could not be "
                  + "rebuilt and were left as they were - a thin gap may show there."
                : ""),
            DialogTitle);

        if (outcome.OpenJoints.Count > 0)
        {
            RibbitUi.ShowReport($"{DialogTitle} - open joints",
                SlopeReport.BuildOpenJoints(outcome.OpenJoints, idMap));
        }
    }

    private static void ReportCleared(WarningSwallower swallower)
    {
        if (swallower.Cleared.Count > 0)
        {
            RibbitUi.ShowReport($"{DialogTitle} - warnings cleared",
                SlopeReport.BuildClearedWarnings(swallower.Cleared));
        }
    }
}
