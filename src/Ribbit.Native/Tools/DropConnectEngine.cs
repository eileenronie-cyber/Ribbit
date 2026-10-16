using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Ribbit.Geometry;
using Ribbit.Native.Revit;
using Ribbit.Native.Settings;
using Ribbit.Native.Ui;

namespace Ribbit.Native.Tools;

/// <summary>
/// Drop Connect. Ported from
/// _Ribbit.tab/PH_FP Tools.panel/DropConnect.pushbutton/script.py.
///
/// Builds pendent-sprinkler drops off a branch line, fed from the TOP of the
/// pipe through a return bend — the NFPA 13 detail that keeps sediment out of
/// the drops on raw-water systems.
///
/// DRY RUN FIRST, ALWAYS. The report is shown BEFORE anything is created, and
/// nothing is built until you say yes to the confirmation.
/// </summary>
internal static class DropConnectEngine
{
    private const string DialogTitle = "Drop Connect";

    /// <summary>Shift+Click — the settings-only screen.</summary>
    public static void RunSettingsOnly()
    {
        DropSettings saved = DropSettings.Load();
        DropSettingsWindow window = new(saved);
        if (window.ShowDialog() == true && window.Result is not null)
            window.Result.Save();
    }

    public static void Run(UIDocument uidoc) => Run(uidoc.Document, new RevitPicker(uidoc));

    /// <summary>The whole command, on any picker (the test suite's way in).</summary>
    internal static void Run(Document doc, IPicker picker)
    {

        RevitLinks.EnsureInitialized();

        List<Element>? branchPicks = picker.PickElements(
            "Pick the BRANCH LINE the drops hang from - NOT the cross main. Pick every pipe "
            + "segment of that one run (it is usually split into several by the tees) - fittings "
            + "are ignored (click Finish when done; Esc cancels)");
        if (branchPicks is null || branchPicks.Count == 0)
            return;

        List<Element>? headPicks = picker.PickElements(
            "Pick the pendent sprinkler HEADS (click Finish when done; Esc cancels)");
        if (headPicks is null || headPicks.Count == 0)
            return;

        DropSettings saved = DropSettings.Load();
        DropSettingsWindow settingsWindow = new(saved);
        if (settingsWindow.ShowDialog() != true || settingsWindow.Result is null)
            return;

        DropSettings chosen = settingsWindow.Result;
        chosen.Save();

        DropEngine.MainRead mains = DropEngine.BuildMainSegments(doc, branchPicks);
        DropEngine.HeadRead heads = DropEngine.BuildHeads(doc, headPicks);

        if (mains.Segments.Count == 0)
        {
            RibbitUi.Notify("No usable branch-line segment in the first pick - see the report for "
                            + "why each pick was dropped.", DialogTitle);
        }
        if (heads.Heads.Count == 0)
        {
            RibbitUi.Notify("No usable heads in the second pick - see the report for why each "
                            + "pick was dropped.", DialogTitle);
        }

        // The report links and the builder both need the real ElementId; the
        // solver only ever carries plain ids.
        Dictionary<long, ElementId> idMap = [];
        foreach (Element raw in branchPicks.Concat(headPicks))
        {
            Element elem = Picking.ResolveHost(doc, raw);
            idMap[RevitCompat.IdKey(elem.Id)] = elem.Id;
        }

        List<DropHeadPlan> plans = DropSolver.Solve(heads.Heads, mains.Segments, chosen.AsFeet());

        RibbitUi.ShowReport($"{DialogTitle} - Dry Run",
            DropReport.Build(plans, mains.Segments, mains.RejectedPicks, heads.RejectedPicks,
                heads.Info, chosen, idMap));

        (int _, int warn, int skip, int direct, int returnBend, int endBend) =
            DropSolver.Summarize(plans);
        List<DropHeadPlan> buildable = [.. plans.Where(p => p.Status != SprigStatus.Skip)];

        if (buildable.Count == 0)
        {
            RibbitUi.Notify($"Nothing to build: {skip} head(s) skipped. See the report for why. "
                            + "Nothing was modified.", DialogTitle);
            return;
        }

        if (!Confirm(buildable.Count, warn, skip, direct, returnBend, endBend))
        {
            RibbitUi.Notify("Cancelled - nothing was modified.", DialogTitle);
            return;
        }

        using Transaction t = new(doc, "Drop Connect");
        t.Start();

        // Warnings get cleared so a long batch runs unattended; errors still stop
        // the build and land in the rollback below.
        WarningSwallower swallower = DropBuilder.ApplyFailureHandling(t);

        int built;
        // Nothing but Build() and Commit() goes inside the try: reporting the
        // result must not land in a handler that would roll back a committed
        // transaction.
        try
        {
            built = DropBuilder.Build(doc, plans, idMap, heads.Elements, heads.Info);

            // Never report a build Revit rolled back at commit as done.
            if (t.Commit() != TransactionStatus.Committed)
            {
                ShowClearedWarnings(swallower);
                RibbitUi.Notify("Revit rolled the build back when it was committed - nothing was "
                                + "modified. See the message Revit showed.", DialogTitle);
                return;
            }
        }
        catch (BuildError ex)
        {
            t.RollBack();
            ShowClearedWarnings(swallower);
            RibbitUi.Notify($"Rolled back - nothing was modified. {ex.Message}", DialogTitle);
            return;
        }
        catch (Exception ex)
        {
            t.RollBack();
            ShowClearedWarnings(swallower);
            RibbitUi.Notify($"Rolled back - nothing was modified. Unexpected error: {ex.Message}",
                DialogTitle);
            return;
        }

        ShowClearedWarnings(swallower);
        RibbitUi.Notify(
            $"Built {built} sprinkler drop(s): {direct} direct, {returnBend} return bend, "
            + $"{endBend} end bend.", DialogTitle);
    }

    private static void ShowClearedWarnings(WarningSwallower swallower)
    {
        if (swallower.Cleared.Count == 0)
            return;

        RibbitUi.ShowReport($"{DialogTitle} - warnings cleared during the build",
            DropReport.BuildClearedWarnings(swallower.Cleared));
    }

    private static bool Confirm(
        int count, int warn, int skip, int direct, int returnBend, int endBend)
    {
        List<string> lines =
        [
            $"Build {count} sprinkler drop(s)?",
            "",
            $"  {direct} DIRECT (straight drop)",
            $"  {returnBend} RETURN BEND (riser + armover + drop)",
            $"  {endBend} END BEND (off the branch end)",
        ];

        if (warn > 0)
        {
            lines.Add("");
            lines.Add($"{warn} of them have warnings - these WILL be built. See the report.");
        }
        if (skip > 0)
        {
            lines.Add("");
            lines.Add($"{skip} head(s) are skipped and will not be built.");
        }

        lines.Add("");
        lines.Add("Any failure rolls the whole run back.");

        return RibbitUi.Confirm(string.Join("\n", lines), DialogTitle);
    }
}
