using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Ribbit.Geometry;
using Ribbit.Native.Revit;

namespace Ribbit.Native.Tools;

/// <summary>
/// Align Branch To Reference engine (centerline-to-centerline, no rotation).
/// Ported from lib/mepalignlib/alignbranch.py.
///
/// Moves the selected branch pipe as a RIGID WHOLE (both endpoints translate
/// together) so it lines up with the reference pipe's CENTERLINE. The
/// reference pipe never moves.
///
/// Always aligns in FULL 3D: the branch translates in any direction so the
/// centerlines meet (closest point between the two infinite lines).
///
///   - The alignment target is the reference pipe's centerline itself — never
///     one of its connectors, never specifically its endpoint.
///   - The branch's own direction/orientation and length are always preserved
///     exactly — this only ever translates, it never rotates, trims, extends,
///     or creates fittings.
///   - If the branch and reference run parallel there's no single closest
///     point, so the branch's position is projected straight onto the
///     reference's line: that removes the perpendicular offset while leaving
///     the branch's position along its own length untouched.
///
/// This uses full 3D geometry directly (never a view's sketch plane), so
/// results are identical in 3D, plan, and section views, for horizontal,
/// vertical, and sloped branches alike.
///
/// SILENT BY DESIGN
///   Nothing here ever opens a dialog, matching Move Connect. A mis-click is a
///   normal part of a fast pick-pick-pick tool, and a modal alert per bad pair
///   costs more than the diagnosis is worth. Revit's own warnings are
///   suppressed too, via <see cref="SilentTransaction"/>; genuine errors still
///   surface, because swallowing those could corrupt the model.
///
///   A pair it cannot align rolls back and leaves the model untouched. In Multi
///   mode the reason rides along in the next pick prompt — which is how
///   "already aligned" becomes visible at all, rather than being
///   indistinguishable from a dead click.
///
/// NOTES
///   - Only straight runs are pickable (a Revit Pipe/Duct/Conduit's
///     LocationCurve must be a Line); insulation/lining picks resolve to their
///     host pipe/duct.
///   - This does not join/connect the pipes or create any fitting — it only
///     repositions the branch; connecting is a separate step.
///   - Moving a branch that's already connected on the far end drags that
///     connected geometry too, same as native Revit move behavior.
/// </summary>
internal static class AlignBranchEngine
{
    /// <summary>
    /// The run's centreline, or null unless it is a straight MEP RUN - pipe,
    /// duct, cable tray or conduit.
    ///
    /// "Has a straight location line" alone is not enough: walls, beams and
    /// model lines have one too. The in-Revit suite proved a wall passed the
    /// pick filter and was then moved onto the pipe's centreline.
    /// </summary>
    private static Line? GetLine(Element elem) => elem is MEPCurve ? ElementGeometry.GetLine(elem) : null;

    /// <summary>
    /// Allows only straight MEP runs (after host resolution). Cached: a run's
    /// location curve stays a Line, so the memo never goes stale.
    /// </summary>
    internal sealed class StraightRunFilter(Document doc) : CachedPickFilter(doc)
    {
        protected override bool Test(Element elem)
            => GetLine(Picking.ResolveHost(Doc, elem)) is not null;
    }

    /// <summary>
    /// Null when the branch was moved, otherwise a short reason it was not.
    ///
    /// A REASON, not a bool. Four different outcomes used to collapse into
    /// "false" — same element, branch not straight, reference not straight, and
    /// already aligned — which made a successful no-op indistinguishable from a
    /// dead click. The caller cannot report what it was never told.
    /// </summary>
    public static string? AlignBranchToReference(Document doc, Element branch, Element reference)
    {
        if (branch.Id == reference.Id)
            return "same element picked twice (a pick on insulation counts as the pipe it wraps)";

        Line? branchCurve = GetLine(branch);
        if (branchCurve is null)
            return "branch is not a straight pipe, duct, tray or conduit run";

        Line? refCurve = GetLine(reference);
        if (refCurve is null)
            return "reference is not a straight pipe, duct, tray or conduit run";

        Vec3 delta = AlignBranchMath.ComputeDelta(
            branchCurve.GetEndPoint(0).ToVec3(),
            branchCurve.GetEndPoint(1).ToVec3(),
            refCurve.GetEndPoint(0).ToVec3(),
            refCurve.GetEndPoint(1).ToVec3());

        // Not a failure - the click registered and there was nothing to do.
        // Worth saying, because silence otherwise reads as "nothing happened".
        if (delta.GetLength() < AlignBranchMath.LenEps)
            return "already aligned";

        ElementTransformUtils.MoveElement(doc, branch.Id, delta.ToXYZ());
        return null;
    }

    /// <summary>One pair in its own transaction: null when aligned, otherwise why not.</summary>
    internal static string? AlignPair(Document doc, Element branchR, Element referenceR)
    {
        // Revit's own warnings are auto-resolved here, so a move Revit
        // merely grumbles about does not interrupt the run.
        using Transaction t = SilentTransaction.Start(doc, "Align Branch To Reference");
        try
        {
            string? reason = AlignBranchToReference(doc, branchR, referenceR);
            if (reason is null)
            {
                // A move Revit rolls back at commit did not happen, whatever it
                // looked like before: never count it as aligned.
                return t.Commit() == TransactionStatus.Committed ? null : "Revit rolled the move back";
            }

            t.RollBack();
            return reason;
        }
        catch (Exception ex)
        {
            // Message only, never ToString(): a stack trace is not
            // something to put in front of someone mid-pick.
            t.RollBack();
            return ex.Message;
        }
    }

    /// <summary>
    /// multiple=false: until one branch is aligned, then exit on its own (no
    ///                 Esc needed). A pair that could not be aligned asks
    ///                 again, saying why - it used to end the command without
    ///                 a word, which read exactly like a dead click.
    /// multiple=true : keep going until Esc.
    /// </summary>
    public static void Run(UIDocument uidoc, bool multiple)
        => Run(uidoc.Document, new RevitPicker(uidoc), multiple);

    /// <summary>The pick loop, on any picker. Returns how many branches were aligned.</summary>
    internal static int Run(Document doc, IPicker picker, bool multiple)
    {
        StraightRunFilter selFilter = new(doc);

        int aligned = 0;
        string? lastNote = null;

        while (true)
        {
            // The only feedback channel that costs the user nothing: they are
            // already reading this prompt to make the next pick.
            string prompt = multiple
                ? $"Pick the FIXED reference pipe ({aligned} aligned"
                  + (lastNote is null ? "" : $"; last: {lastNote}")
                  + ", Esc to finish)"
                : lastNote is null
                    ? "Pick the FIXED reference pipe"
                    : $"Pick the FIXED reference pipe (last pair: {lastNote}; Esc to quit)";

            selFilter.Exclude(null);
            (Element? reference, _) = picker.PickElement(prompt, selFilter);
            if (reference is null)
                break;   // Esc exits immediately

            // Resolve BEFORE excluding — see MoveConnectEngine.Run. Barring the
            // raw pick leaves the reference's own insulation pickable as the
            // branch, and both resolve back to one element. Caught here rather
            // than by the guard in AlignBranchToReference, which then only has
            // to cover direct callers.
            Element referenceR = Picking.ResolveHost(doc, reference);
            selFilter.Exclude(referenceR.Id);

            (Element? branch, _) = picker.PickElement(
                "Pick the BRANCH pipe to align (Esc to exit)", selFilter);
            if (branch is null)
                break;   // Esc exits immediately - no second press needed

            Element branchR = Picking.ResolveHost(doc, branch);

            string? reason = AlignPair(doc, branchR, referenceR);
            if (reason is null)
            {
                aligned++;
                lastNote = null;
            }
            else
            {
                lastNote = reason;
            }

            if (!multiple && reason is null)
                break;
        }

        return aligned;
    }
}
