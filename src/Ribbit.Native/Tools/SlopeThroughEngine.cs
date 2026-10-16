using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Ribbit.Geometry;
using Ribbit.Native.Revit;
using Ribbit.Native.Settings;
using Ribbit.Native.Ui;
// Autodesk.Revit.DB also has a Units type; ours wins inside this file.
using Units = Ribbit.Geometry.Units;

namespace Ribbit.Native.Tools;

/// <summary>
/// Slope Through. Ported from
/// _Ribbit.tab/PH_FP Tools.panel/SlopeThrough.pushbutton/script.py.
///
/// Puts ONE developed slope on a branch line, straight or turning corners —
/// every horizontal leg from the fixed end out to the last elbow, in a single
/// pass. A branch with no corners comes out as one leg, and a one-leg solve IS
/// Slope Branch: same BuildRun, same RePitch, same groups, same applier.
///
/// DRY RUN FIRST, ALWAYS. The report prints before anything moves and the
/// confirmation is where it commits — cancel there and the model is untouched,
/// so this doubles as a read-only developed-slope check.
///
/// EVERY exit reports: an abort says why, so the button can never read as "did
/// nothing".
/// </summary>
internal static class SlopeThroughEngine
{
    private const string DialogTitle = "Slope Through";

    /// <summary>
    /// Below this, a re-pitch would break and remake every joint to move
    /// nothing. 0.1 mm in internal feet.
    /// </summary>
    private static readonly double NoOpFt = 0.1 / Units.MmPerFt;

    /// <summary>Shift+Click — the settings-only screen.</summary>
    public static void RunSettingsOnly()
    {
        SlopeSettings saved = SlopeSettings.Load();
        SlopeSettingsWindow window = new(saved);
        if (window.ShowDialog() == true && window.Result is not null)
            window.Result.Save();
    }

    public static void Run(UIDocument uidoc)
    {
        RevitLinks.EnsureInitialized();
        Run(uidoc.Document, new RevitPicker(uidoc));
    }

    /// <summary>The whole command, on any picker (the test suite's way in).</summary>
    internal static void Run(Document doc, IPicker picker)
    {
        (List<Element>? seeds, Element? fixedElem, XYZ? fixedPoint) = SlopeThrough.Pick(doc, picker);
        if (seeds is null || seeds.Count == 0 || fixedElem is null || fixedPoint is null)
            return;

        SlopeSettings saved = SlopeSettings.Load();
        SlopeSettingsWindow settingsWindow = new(saved);
        if (settingsWindow.ShowDialog() != true || settingsWindow.Result is null)
            return;

        SlopeSettings chosen = settingsWindow.Result;
        chosen.Save();

        SlopeParams parameters = chosen.AsFeet();
        SlopeGroupParams groupParams = chosen.GroupParams();

        SlopePlan plan = SlopeThrough.Discover(seeds, fixedElem, parameters);
        plan = SlopeThrough.Solve(doc, plan, fixedElem, fixedPoint.ToVec3(), parameters, groupParams);
        SlopeThrough.BuildIdMap(plan);

        if (plan.Error is not null)
        {
            Abort(plan, chosen, plan.Error, "See the report. Nothing was modified.");
            return;
        }

        RibbitUi.ShowReport($"{DialogTitle} - Dry Run",
            SlopeReport.Build(plan, chosen, plan.IdMap));

        List<Leg> good = [.. plan.Legs.Where(l => l.Ok)];
        if (good.Count == 0)
        {
            RibbitUi.Alert(
                "No leg of this branch could be solved.\n\nEach leg's reason is in the report "
                + "window. Nothing was modified.", DialogTitle);
            return;
        }

        if (AlreadyDone(plan))
        {
            RibbitUi.Alert(
                "The branch is already at this slope.\n\nNothing would move, so nothing was "
                + "changed.", DialogTitle);
            return;
        }

        if (!Confirm(plan, good))
        {
            RibbitUi.Notify("Cancelled - nothing was modified.", DialogTitle);
            return;
        }

        (Transaction t, WarningSwallower swallower) =
            SlopeApplier.StartTransaction(doc, "Slope Through");

        ApplyResult outcome;
        // The try sits INSIDE the using, as in Slope Armover. It used to wrap the
        // using: an ApplyError (Cancel on the progress window raises one) left the
        // using - which disposed, and so rolled back, the transaction - and only
        // then reached the catch, whose t.RollBack() on the disposed transaction
        // threw InvalidObjectException out of the command. Revit showed "external
        // command failed" instead of "Rolled back"; proven by the in-Revit suite.
        //
        // Nothing but Apply() and Commit() goes inside the try: reporting the
        // result must not land in a handler that would roll back a committed
        // transaction.
        using (t)
        {
            try
            {
                outcome = SlopeThrough.Apply(doc, plan, chosen.ReconnectTolFt);
                if (t.Commit() != TransactionStatus.Committed)
                {
                    ShowClearedWarnings(swallower);
                    RibbitUi.Notify("Revit rolled the change back when it was committed - nothing was "
                                    + "modified. See the message Revit showed.", DialogTitle);
                    return;
                }
            }
            catch (ApplyError ex)
            {
                t.RollBack();
                ShowClearedWarnings(swallower);
                RibbitUi.Notify($"Rolled back - nothing was modified. {ex.Message}", DialogTitle);
                return;
            }
            catch (Exception ex)
            {
                if (t.GetStatus() == TransactionStatus.Started)
                    t.RollBack();
                ShowClearedWarnings(swallower);
                RibbitUi.Notify($"Rolled back - nothing was modified. Unexpected error: {ex.Message}",
                    DialogTitle);
                return;
            }
        }

        ShowClearedWarnings(swallower);
        RibbitUi.Notify(OutcomeMessage(outcome, plan), DialogTitle);

        if (outcome.OpenJoints.Count > 0)
        {
            RibbitUi.ShowReport($"{DialogTitle} - joints left OPEN",
                SlopeReport.BuildOpenJoints(outcome.OpenJoints, plan.IdMap));
        }
    }

    private static void Abort(SlopePlan plan, SlopeSettings chosen, string headline, string detail)
    {
        RibbitUi.ShowReport($"{DialogTitle} - stopped, nothing modified",
            SlopeReport.Build(plan, chosen, plan.IdMap));
        RibbitUi.Alert($"{headline}\n\n{detail}", DialogTitle);
    }

    private static void ShowClearedWarnings(WarningSwallower swallower)
    {
        if (swallower.Cleared.Count == 0)
            return;

        RibbitUi.ShowReport($"{DialogTitle} - warnings cleared during the run",
            SlopeReport.BuildClearedWarnings(swallower.Cleared));
    }

    /// <summary>
    /// True when every point of every solved leg is already within a tenth of a
    /// millimetre of where the new slope would put it — a re-pitch would break
    /// and remake every joint to move nothing.
    /// </summary>
    private static bool AlreadyDone(SlopePlan plan)
    {
        foreach (Leg leg in plan.Legs)
        {
            if (!leg.Ok)
                continue;

            foreach (SlopeSegment seg in leg.Pitch!.Run.Segments)
            {
                foreach (Vec3 point in new[] { seg.P0, seg.P1 })
                {
                    if (Math.Abs(leg.Pitch.Dz(point)) > NoOpFt)
                        return false;
                }
            }
        }
        return true;
    }

    private static bool Confirm(SlopePlan plan, IReadOnlyList<Leg> good)
    {
        int segments = good.Sum(l => l.Run!.Segments.Count);

        double biggest = 0.0;
        foreach (Leg leg in good)
        {
            foreach (SlopeSegment seg in leg.Run!.Segments)
            {
                foreach (Vec3 point in new[] { seg.P0, seg.P1 })
                    biggest = Math.Max(biggest, Math.Abs(leg.Pitch!.Dz(point)));
            }
        }

        RePitch first = good[0].Pitch!;

        List<string> lines =
        [
            $"Slope this branch to {SlopeSolver.FmtSlope(first.NewRateAway)} across "
            + $"{good.Count} leg(s)?",
            "",
            $"  Developed length: {Units.Mm(SlopeThrough.DevelopedLength(plan))}",
            $"  {segments} pipe segment(s) over {good.Count} leg(s)",
            $"  {plan.Joints.Count} joint(s) broken and remade",
            $"  Largest move on any pipe: {SlopeSolver.Mm1(biggest)}",
            "",
            "  The fixed end holds its elevation; the branch rises away from it across every elbow.",
            "",
            "One transaction - a geometry failure rolls the whole branch back, and one Ctrl+Z "
            + "undoes it.",
        ];

        return RibbitUi.Confirm(string.Join("\n", lines), DialogTitle);
    }

    private static string OutcomeMessage(ApplyResult outcome, SlopePlan plan)
    {
        string msg = $"Sloped the branch: {outcome.Segments} segment(s), {outcome.Groups} "
                     + $"group(s), {outcome.JointsRemade} of {outcome.JointsBroken} joint(s) remade.";

        if (outcome.OpenJoints.Count > 0)
            msg += $" {outcome.OpenJoints.Count} joint(s) were left OPEN - see the report.";

        // The run IS sloped when this happens, so this dialog is the only place
        // the leftover gap gets mentioned at all.
        if (outcome.FittingsNotRebuilt.Count > 0)
        {
            msg += $" The anchored end's fitting could not be rebuilt and was left as it was - "
                   + "a thin gap may show where the pipe meets it.";
        }

        // Said here as well as in the report, for the same reason open joints
        // are: this dialog is the only thing guaranteed to be read, and a corner
        // this far out is a thing to go and look at, not a number in a table.
        int corners = plan.Legs.Count(
            l => l.Ok && l.CornerResidualDeg > SlopeSettings.CornerResidualWarnDeg);

        if (corners > 0)
        {
            msg += $" {corners} corner elbow(s) are more than "
                   + $"{SlopeSettings.CornerResidualWarnDeg:F1} deg out - see the report.";
        }

        return msg;
    }
}
