using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using Ribbit.Geometry;
using Ribbit.Native.Revit;
using Ribbit.Native.Ui;

namespace Ribbit.Native.Tools;

/// <summary>Message is already phrased for the user; the caller shows it as-is.</summary>
internal sealed class BuildError(string message) : Exception(message);

/// <summary>
/// Clears Revit's own WARNING dialogs during the build, keeping every one it
/// cleared so the caller can log them into the report instead of them vanishing.
///
/// Splicing tees into a live run routinely posts warnings Revit wants clicked —
/// and a modal dialog on head 7 of 40 stops an otherwise unattended run dead.
/// ERRORS are deliberately left alone: they still stop the transaction and reach
/// the caller's rollback. The point is to stop the run being interrupted, never
/// to hide a real failure.
/// </summary>
internal sealed class WarningSwallower : IFailuresPreprocessor
{
    public List<string> Cleared { get; } = [];

    public FailureProcessingResult PreprocessFailures(FailuresAccessor accessor)
    {
        foreach (FailureMessageAccessor failure in accessor.GetFailureMessages())
        {
            if (failure.GetSeverity() != FailureSeverity.Warning)
                continue;

            try
            {
                Cleared.Add(failure.GetDescriptionText());
            }
            catch (Exception)
            {
                Cleared.Add("(warning - description unavailable)");
            }

            accessor.DeleteWarning(failure);
        }

        return FailureProcessingResult.Continue;
    }
}

/// <summary>
/// Sprig Connect — the builder. THIS CLASS WRITES TO THE MODEL. Ported from
/// lib/spriglib/builder.py.
///
///   DIRECT   break branch -> create sprig -> tee -> connect head
///   ARMOVER  break branch -> create armover -> tee
///                         -> create sprig -> elbow -> connect head
///
/// ALL OR NOTHING — throws <see cref="BuildError"/> on the first failure and
/// expects the caller to roll the whole transaction back. Half a sprinkler
/// layout is worse than none, and the dry run has already told the user which
/// heads were going to fail.
///
/// LIVE SEGMENT TRACKING — we splice N times into the SAME pipe. Every
/// BreakCurve turns one pipe into two, so the element that holds the next tee
/// point is not the element we started with, and caching the original id would
/// splice the second head into a pipe that no longer reaches it. After each
/// break both pieces are tracked, and the host for the next head is re-resolved
/// BY GEOMETRY, never by id.
///
/// BREAKCURVE NULLS MEPSystem — documented Revit behaviour: the segments
/// BreakCurve produces keep their connections but come back with a null
/// MEPSystem, which cannot be set programmatically. So system type, pipe type
/// and level are captured ONCE from the intact branch pipe before the first
/// break and reused for every head on that segment.
///
/// CONNECTOR STALENESS — connector objects do not survive a document change.
/// Every connector is re-fetched by position immediately before it is used.
/// </summary>
internal static class SprigBuilder
{
    public static WarningSwallower ApplyFailureHandling(Transaction transaction)
        => BranchBuild.ApplyFailureHandling(transaction);

    /// <summary>
    /// Builds one head's drop. Returns the new pipe ids so the caller keeps
    /// tracking the live pieces of the branch line.
    /// </summary>
    private static List<ElementId> BuildHead(
        Document doc, HeadPlan plan, Element hostPipe, Element headElem,
        BranchBuild.PipeContext ctx, double diaFt, double? nearestOtherTeeFt)
    {
        XYZ teePt = plan.TeePt!.Value.ToXYZ();
        XYZ headPt = plan.HeadPt.ToXYZ();

        Element other = BranchBuild.BreakBranch(doc, hostPipe, teePt);
        Pipe sprig;

        if (plan.Kind == SprigLayout.Armover)
        {
            XYZ elbowPt = plan.ElbowPt!.Value.ToXYZ();
            Pipe armover = BranchBuild.CreatePipe(doc, ctx, teePt, elbowPt, diaFt, "armover");

            // Every connector read fresh, after the regens Pipe.Create did.
            (Connector connA, Connector connB) = BranchBuild.BreakConnectors(hostPipe, other, teePt);
            Connector? armStart = BranchBuild.ClosestOpenConnector(armover, teePt);
            if (armStart is null)
                throw new BuildError("could not find the armover's connector at the tee point");
            BranchBuild.PlaceTee(doc, connA, connB, armStart, "sprig/armover", nearestOtherTeeFt);

            sprig = BranchBuild.CreatePipe(doc, ctx, elbowPt, headPt, diaFt, "sprig");

            Connector? armEnd = BranchBuild.ClosestOpenConnector(armover, elbowPt);
            Connector? sprigBottom = BranchBuild.ClosestOpenConnector(sprig, elbowPt);
            if (armEnd is null || sprigBottom is null)
                throw new BuildError("could not find the connectors to elbow the armover into the sprig");
            BranchBuild.PlaceElbow(doc, armEnd, sprigBottom, "armover");
        }
        else
        {
            sprig = BranchBuild.CreatePipe(doc, ctx, teePt, headPt, diaFt, "sprig");

            (Connector connA, Connector connB) = BranchBuild.BreakConnectors(hostPipe, other, teePt);
            Connector? sprigBottom = BranchBuild.ClosestOpenConnector(sprig, teePt);
            if (sprigBottom is null)
                throw new BuildError("could not find the sprig's connector at the tee point");
            BranchBuild.PlaceTee(doc, connA, connB, sprigBottom, "sprig/armover", nearestOtherTeeFt);
        }

        BranchBuild.ConnectHead(doc, sprig, headElem, headPt, "sprig", "top");

        return [other.Id];
    }

    /// <summary>
    /// Builds every non-SKIP plan, in branch-line order. Returns the number of
    /// heads built; throws <see cref="BuildError"/> on the first failure —
    /// including a user cancel — and the caller must roll the transaction back.
    /// </summary>
    public static int Build(
        Document doc,
        IReadOnlyList<HeadPlan> plans,
        IReadOnlyDictionary<long, ElementId> idMap,
        IReadOnlyDictionary<long, Element> headElems,
        IReadOnlyDictionary<long, SprigEngine.HeadInfo> headInfo)
    {
        Dictionary<long, List<ElementId>> live = [];
        Dictionary<long, BranchBuild.PipeContext> contexts = [];
        int built = 0;

        List<HeadPlan> todo =
            [.. plans.Where(p => p.Status != SprigStatus.Skip && p.Segment is not null)];
        int total = todo.Count;

        using ProgressDialog progress = new("Sprig Connect - building {0} of {1}", cancellable: true);

        for (int index = 0; index < todo.Count; index++)
        {
            if (progress.Cancelled)
            {
                throw new BuildError(
                    $"cancelled after {built} of {total} head(s) - the whole run is being rolled back");
            }

            HeadPlan plan = todo[index];
            long segId = plan.Segment!.ElemId;

            if (!live.ContainsKey(segId))
            {
                if (!idMap.TryGetValue(segId, out ElementId? startId))
                    throw new BuildError($"head {plan.HeadId}: lost track of the branch-line element");

                Element? original = doc.GetElement(startId);
                if (original is null)
                {
                    throw new BuildError(
                        $"head {plan.HeadId}: the branch-line element is no longer in the model");
                }

                live[segId] = [startId];
                // Captured from the INTACT pipe, before any break - see
                // BREAKCURVE NULLS MEPSystem in the class docs.
                contexts[segId] = BranchBuild.ContextOf(original);
            }

            XYZ teePt = plan.TeePt!.Value.ToXYZ();
            Element? host = BranchBuild.FindHost(doc, live[segId], teePt);
            if (host is null)
            {
                throw new BuildError(
                    $"head {plan.HeadId}: no live piece of the branch line still contains its tee "
                    + "point - an earlier tee probably landed too close to this one");
            }

            if (!headElems.TryGetValue(plan.HeadId, out Element? headElem))
                throw new BuildError($"head {plan.HeadId}: element not found");

            double diaFt = headInfo.TryGetValue(plan.HeadId, out SprigEngine.HeadInfo? info)
                ? info.DiameterFt
                : 0.0;

            double? crowd = BranchBuild.NearestOtherTee(teePt,
                todo.Where(p => p != plan && p.Segment!.ElemId == segId).Select(p => p.TeePt!.Value.ToXYZ()));

            List<ElementId> newIds;
            try
            {
                newIds = BuildHead(doc, plan, host, headElem, contexts[segId], diaFt, crowd);
            }
            catch (BuildError ex)
            {
                throw new BuildError($"head {plan.HeadId}: {ex.Message}");
            }

            live[segId].AddRange(newIds);
            built++;
            progress.Update(index + 1, total);
        }

        return built;
    }
}
