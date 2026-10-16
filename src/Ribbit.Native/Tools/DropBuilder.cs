using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using Ribbit.Geometry;
using Ribbit.Native.Revit;
using Ribbit.Native.Ui;

namespace Ribbit.Native.Tools;

/// <summary>
/// Drop Connect — the builder. THIS CLASS WRITES TO THE MODEL. Ported from
/// lib/droplib/builder.py.
///
///   DIRECT       break branch -> create drop -> tee -> connect head
///   RETURN BEND  break branch -> riser -> tee -> armover -> elbow
///                             -> drop -> elbow -> connect head
///   END BEND     NO break, NO tee: elbow onto the branch's existing OPEN END
///                             -> riser -> armover -> drop -> connect head
///
/// The same three contracts as <see cref="SprigBuilder"/> apply — ALL OR
/// NOTHING, LIVE SEGMENT TRACKING (host re-resolved by geometry, never by id),
/// and BREAKCURVE NULLS MEPSystem (context captured once from the intact pipe).
/// The steps themselves are shared with Sprig Connect: <see cref="BranchBuild"/>.
///
/// The END BEND is the one case that never splits the branch, so it contributes
/// no new live pieces and needs a host with a free connector at the elbow point
/// rather than one containing the point.
/// </summary>
internal static class DropBuilder
{
    public static WarningSwallower ApplyFailureHandling(Transaction transaction)
        => BranchBuild.ApplyFailureHandling(transaction);

    private static List<ElementId> BuildHead(
        Document doc, DropHeadPlan plan, Element hostPipe, Element headElem,
        BranchBuild.PipeContext ctx, double diaFt, double? nearestOtherTeeFt)
    {
        if (plan.Kind == DropLayout.EndBend)
            return BuildEndBend(doc, plan, hostPipe, headElem, ctx, diaFt);

        XYZ teePt = plan.TeePt!.Value.ToXYZ();
        XYZ headPt = plan.HeadPt.ToXYZ();

        Element other = BranchBuild.BreakBranch(doc, hostPipe, teePt);
        Pipe drop;

        if (plan.Kind == DropLayout.ReturnBend)
        {
            XYZ riserTopPt = plan.RiserTop!.Value.ToXYZ();
            XYZ overPt = plan.OverPt!.Value.ToXYZ();

            Pipe riser = BranchBuild.CreatePipe(doc, ctx, teePt, riserTopPt, diaFt, "riser");

            // Every connector read fresh, after the regens Pipe.Create did.
            (Connector connA, Connector connB) = BranchBuild.BreakConnectors(hostPipe, other, teePt);
            Connector? riserBottom = BranchBuild.ClosestOpenConnector(riser, teePt);
            if (riserBottom is null)
                throw new BuildError("could not find the riser's connector at the tee point");
            BranchBuild.PlaceTee(doc, connA, connB, riserBottom, "riser/drop", nearestOtherTeeFt);

            Pipe armover = BranchBuild.CreatePipe(doc, ctx, riserTopPt, overPt, diaFt, "armover");

            Connector? riserTop = BranchBuild.ClosestOpenConnector(riser, riserTopPt);
            Connector? armStart = BranchBuild.ClosestOpenConnector(armover, riserTopPt);
            if (riserTop is null || armStart is null)
                throw new BuildError("could not find the connectors to elbow the riser into the armover");
            BranchBuild.PlaceElbow(doc, riserTop, armStart, "riser top");

            drop = BranchBuild.CreatePipe(doc, ctx, overPt, headPt, diaFt, "drop");

            Connector? armEnd = BranchBuild.ClosestOpenConnector(armover, overPt);
            Connector? dropTop = BranchBuild.ClosestOpenConnector(drop, overPt);
            if (armEnd is null || dropTop is null)
                throw new BuildError("could not find the connectors to elbow the armover into the drop");
            BranchBuild.PlaceElbow(doc, armEnd, dropTop, "top of the drop");
        }
        else
        {
            drop = BranchBuild.CreatePipe(doc, ctx, teePt, headPt, diaFt, "drop");

            (Connector connA, Connector connB) = BranchBuild.BreakConnectors(hostPipe, other, teePt);
            Connector? dropTop = BranchBuild.ClosestOpenConnector(drop, teePt);
            if (dropTop is null)
                throw new BuildError("could not find the drop's connector at the tee point");
            BranchBuild.PlaceTee(doc, connA, connB, dropTop, "riser/drop", nearestOtherTeeFt);
        }

        BranchBuild.ConnectHead(doc, drop, headElem, headPt, "drop", "bottom");

        return [other.Id];
    }

    /// <summary>
    /// END BEND: elbow onto the branch's existing open end, riser up, armover out
    /// past the end, drop down to the head. Nothing is split, so no new live
    /// branch pieces come back.
    /// </summary>
    private static List<ElementId> BuildEndBend(
        Document doc, DropHeadPlan plan, Element hostPipe, Element headElem,
        BranchBuild.PipeContext ctx, double diaFt)
    {
        XYZ endPt = plan.TeePt!.Value.ToXYZ();
        XYZ headPt = plan.HeadPt.ToXYZ();
        XYZ riserTopPt = plan.RiserTop!.Value.ToXYZ();
        XYZ overPt = plan.OverPt!.Value.ToXYZ();

        if (BranchBuild.ClosestOpenConnector(hostPipe, endPt) is null)
            throw new BuildError("the branch's open end connector is no longer free at the expected point");

        Pipe riser = BranchBuild.CreatePipe(doc, ctx, endPt, riserTopPt, diaFt, "riser");

        // Re-read the branch end fresh after Pipe.Create's regens.
        Connector? branchEnd = BranchBuild.ClosestOpenConnector(hostPipe, endPt);
        Connector? riserBottom = BranchBuild.ClosestOpenConnector(riser, endPt);
        if (branchEnd is null || riserBottom is null)
            throw new BuildError("could not find the connectors to elbow the branch end into the riser");
        BranchBuild.PlaceElbow(doc, branchEnd, riserBottom, "branch end");

        Pipe armover = BranchBuild.CreatePipe(doc, ctx, riserTopPt, overPt, diaFt, "armover");

        Connector? riserTop = BranchBuild.ClosestOpenConnector(riser, riserTopPt);
        Connector? armStart = BranchBuild.ClosestOpenConnector(armover, riserTopPt);
        if (riserTop is null || armStart is null)
            throw new BuildError("could not find the connectors to elbow the riser into the armover");
        BranchBuild.PlaceElbow(doc, riserTop, armStart, "riser top");

        Pipe drop = BranchBuild.CreatePipe(doc, ctx, overPt, headPt, diaFt, "drop");

        Connector? armEnd = BranchBuild.ClosestOpenConnector(armover, overPt);
        Connector? dropTop = BranchBuild.ClosestOpenConnector(drop, overPt);
        if (armEnd is null || dropTop is null)
            throw new BuildError("could not find the connectors to elbow the armover into the drop");
        BranchBuild.PlaceElbow(doc, armEnd, dropTop, "top of the drop");

        BranchBuild.ConnectHead(doc, drop, headElem, headPt, "drop", "bottom");

        return [];
    }

    /// <summary>
    /// Builds every non-SKIP plan, in branch-line order. Returns the number of
    /// heads built; throws <see cref="BuildError"/> on the first failure —
    /// including a user cancel — and the caller must roll the transaction back.
    /// </summary>
    public static int Build(
        Document doc,
        IReadOnlyList<DropHeadPlan> plans,
        IReadOnlyDictionary<long, ElementId> idMap,
        IReadOnlyDictionary<long, Element> headElems,
        IReadOnlyDictionary<long, DropEngine.HeadInfo> headInfo)
    {
        Dictionary<long, List<ElementId>> live = [];
        Dictionary<long, BranchBuild.PipeContext> contexts = [];
        int built = 0;

        List<DropHeadPlan> todo =
            [.. plans.Where(p => p.Status != SprigStatus.Skip && p.Segment is not null)];
        int total = todo.Count;

        using ProgressDialog progress = new("Drop Connect - building {0} of {1}", cancellable: true);

        for (int index = 0; index < todo.Count; index++)
        {
            if (progress.Cancelled)
            {
                throw new BuildError(
                    $"cancelled after {built} of {total} head(s) - the whole run is being rolled back");
            }

            DropHeadPlan plan = todo[index];
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
                contexts[segId] = BranchBuild.ContextOf(original);
            }

            XYZ rootPt = plan.TeePt!.Value.ToXYZ();
            Element? host;
            if (plan.EndBend)
            {
                host = BranchBuild.FindHostWithEnd(doc, live[segId], rootPt);
                if (host is null)
                {
                    throw new BuildError(
                        $"head {plan.HeadId}: no live piece of the branch line still has an open "
                        + "end at the elbow point");
                }
            }
            else
            {
                host = BranchBuild.FindHost(doc, live[segId], rootPt);
                if (host is null)
                {
                    throw new BuildError(
                        $"head {plan.HeadId}: no live piece of the branch line still contains its "
                        + "tee point - an earlier tee probably landed too close to this one");
                }
            }

            if (!headElems.TryGetValue(plan.HeadId, out Element? headElem))
                throw new BuildError($"head {plan.HeadId}: element not found");

            double diaFt = headInfo.TryGetValue(plan.HeadId, out DropEngine.HeadInfo? info)
                ? info.DiameterFt
                : 0.0;

            double? crowd = plan.EndBend
                ? null
                : BranchBuild.NearestOtherTee(rootPt,
                    todo.Where(p => p != plan && !p.EndBend && p.Segment!.ElemId == segId)
                        .Select(p => p.TeePt!.Value.ToXYZ()));

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
