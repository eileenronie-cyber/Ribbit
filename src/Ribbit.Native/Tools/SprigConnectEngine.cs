using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Ribbit.Geometry;
using Ribbit.Native.Revit;
using Ribbit.Native.Settings;
using Ribbit.Native.Ui;

namespace Ribbit.Native.Tools;

/// <summary>
/// Sprig Connect. Ported from
/// _Ribbit.tab/PH_FP Tools.panel/SprigConnect.pushbutton/script.py.
///
/// Builds the upright-sprinkler drops off a branch line: for every head it works
/// out the tee point on the branch, whether that head wants a straight vertical
/// sprig or an armover-plus-elbow, and how long every piece is — reports the
/// lot, then builds it once you confirm.
///
/// DRY RUN FIRST, ALWAYS. The report is shown BEFORE anything is created, and
/// nothing is built until you say yes to the confirmation. Cancelling at that
/// prompt leaves the model untouched, so the button doubles as a read-only
/// checker: run it, read the report, cancel.
/// </summary>
internal static class SprigConnectEngine
{
    private const string DialogTitle = "Sprig Connect";

    /// <summary>Shift+Click — the settings-only screen (config.py in pyRevit).</summary>
    public static void RunSettingsOnly()
    {
        SprigSettings saved = SprigSettings.Load();
        SprigSettingsWindow window = new(saved);
        if (window.ShowDialog() == true && window.Result is not null)
            window.Result.Save();
    }

    public static void Run(UIDocument uidoc) => Run(uidoc.Document, new RevitPicker(uidoc));

    /// <summary>The whole command, on any picker (the test suite's way in).</summary>
    internal static void Run(Document doc, IPicker picker)
    {

        // The report's element links need an API context to have created the
        // ExternalEvent before the modeless window can raise it.
        RevitLinks.EnsureInitialized();

        List<Element>? branchPicks = picker.PickElements(
            "Pick the BRANCH LINE the sprinklers sit over - NOT the cross main. Pick every pipe "
            + "segment of that one run (it is usually split into several by the tees) - fittings "
            + "are ignored (click Finish when done; Esc cancels)");
        if (branchPicks is null || branchPicks.Count == 0)
            return;

        List<Element>? headPicks = picker.PickElements(
            "Pick the upright sprinkler HEADS (click Finish when done; Esc cancels)");
        if (headPicks is null || headPicks.Count == 0)
            return;

        SprigSettings saved = SprigSettings.Load();
        SprigSettingsWindow settingsWindow = new(saved);
        if (settingsWindow.ShowDialog() != true || settingsWindow.Result is null)
            return;

        SprigSettings chosen = settingsWindow.Result;
        chosen.Save();

        SprigEngine.MainRead mains = SprigEngine.BuildMainSegments(doc, branchPicks);
        SprigEngine.HeadRead heads = SprigEngine.BuildHeads(doc, headPicks);

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

        List<HeadPlan> plans = SprigSolver.Solve(heads.Heads, mains.Segments, chosen.AsFeet());

        RibbitUi.ShowReport($"{DialogTitle} - Dry Run",
            SprigReport.Build(plans, mains.Segments, mains.RejectedPicks, heads.RejectedPicks,
                heads.Info, chosen, idMap));

        (int _, int warn, int skip, int direct, int armover) = SprigSolver.Summarize(plans);
        List<HeadPlan> buildable = [.. plans.Where(p => p.Status != SprigStatus.Skip)];

        if (buildable.Count == 0)
        {
            RibbitUi.Notify($"Nothing to build: {skip} head(s) skipped. See the report for why. "
                            + "Nothing was modified.", DialogTitle);
            return;
        }

        if (!Confirm(buildable.Count, warn, skip, direct, armover))
        {
            RibbitUi.Notify("Cancelled - nothing was modified.", DialogTitle);
            return;
        }

        using Transaction t = new(doc, "Sprig Connect");
        t.Start();

        // Warnings get cleared so a long batch runs unattended; errors still stop
        // the build and land in the rollback below.
        WarningSwallower swallower = SprigBuilder.ApplyFailureHandling(t);

        int built;
        // Nothing but Build() and Commit() goes inside the try: reporting the
        // result must not land in a handler that would roll back a committed
        // transaction.
        try
        {
            built = SprigBuilder.Build(doc, plans, idMap, heads.Elements, heads.Info);

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
            $"Built {built} sprinkler drop(s): {direct} direct, {armover} armover.", DialogTitle);
    }

    private static void ShowClearedWarnings(WarningSwallower swallower)
    {
        if (swallower.Cleared.Count == 0)
            return;

        RibbitUi.ShowReport($"{DialogTitle} - warnings cleared during the build",
            SprigReport.BuildClearedWarnings(swallower.Cleared));
    }

    private static bool Confirm(int count, int warn, int skip, int direct, int armover)
    {
        List<string> lines =
        [
            $"Build {count} sprinkler drop(s)?",
            "",
            $"  {direct} DIRECT (vertical sprig)",
            $"  {armover} ARMOVER (armover + elbow + sprig)",
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
