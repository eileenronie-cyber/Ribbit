using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Ribbit.Geometry.Sheets;
using Ribbit.Native.Revit;
using Ribbit.Native.Settings;
using Ribbit.Native.Ui;

namespace Ribbit.Native.Tools;

/// <summary>
/// Part Sheets Duplicator. Ported from
/// _Ribbit.tab/Sheets.panel/PartSheetsDuplicator.pushbutton/script.py.
///
/// Reads a worksheet (Series, Floor Plan View Name, Phase, Template Name) and
/// creates or updates the "Overall" (parent) and dependent ("Sheet N of M")
/// floor plan views it lists, applying each row's view template and mirroring
/// the parent's identity parameters onto every dependent view.
///
/// The window shows the whole plan before anything is written; Run carries out
/// exactly that plan in ONE transaction - one undo step, and all or nothing if
/// it is cancelled.
/// </summary>
internal static class PartSheetsDuplicatorTool
{
    /// <summary>Tests only: owner for the window, so a test can drive it.</summary>
    internal static System.Windows.Window? TestOwner { get; set; }

    /// <summary>Tests only: false keeps the progress window from opening.</summary>
    internal static bool ShowProgress { get; set; } = true;

    /// <summary>The last run's step results (tests read them).</summary>
    internal static List<DupStepResult>? LastResults { get; private set; }

    public static void Run(UIDocument uidoc) => Run(uidoc.Document);

    internal static void Run(Document doc)
    {
        RevitLinks.EnsureInitialized();
        LastResults = null;

        DupModel model = DuplicatorEngine.Snapshot(doc);
        if (model.FloorPlans.Count == 0)
        {
            RibbitUi.Alert("This model has no floor plan views, so there is nothing to duplicate from.",
                DuplicatorWindow.DialogTitle);
            return;
        }

        DuplicatorWindow window = new(model, DuplicatorSettings.Load());
        if (TestOwner is not null)
            window.Owner = TestOwner;
        if (window.ShowDialog() != true || window.Plan is null)
            return;

        window.SettingsToSave().Save();
        Execute(doc, window.Plan);
    }

    /// <summary>Carry out a plan in one transaction and report it.</summary>
    internal static void Execute(Document doc, DupPlan plan)
    {
        int writes = plan.Steps.Count(s => s.Writes);
        ProgressDialog? progress = ShowProgress && writes > 5
            ? new ProgressDialog("Part Sheets Duplicator - {0} of {1}", cancellable: true)
            : null;

        List<DupStepResult> results;
        DupRunSummary summary;
        try
        {
            using Transaction t = new(doc, "Part Sheets Duplicator");
            ErrorCollector errors = ErrorCollector.Start(t);
            try
            {
                (results, summary) = DuplicatorEngine.Apply(doc, plan, progress is null ? null : (done, total) =>
                {
                    progress.Update(done, total);
                    return !progress.Cancelled;
                });
            }
            catch (OperationCanceledException)
            {
                t.RollBack();
                RibbitUi.Alert("Cancelled - nothing was created or changed.", DuplicatorWindow.DialogTitle);
                return;
            }
            catch (Exception ex)
            {
                t.RollBack();
                RibbitUi.Alert($"Rolled back - nothing was created or changed.\n\nUnexpected error: {ex.Message}",
                    DuplicatorWindow.DialogTitle);
                return;
            }

            if (t.Commit() != TransactionStatus.Committed)
            {
                RibbitUi.Alert("Revit rolled the run back - nothing was created or changed.\n\n"
                               + string.Join("\n", errors.Errors.DefaultIfEmpty("No reason was given.")),
                    DuplicatorWindow.DialogTitle);
                return;
            }
        }
        finally
        {
            progress?.Dispose();
        }

        LastResults = results;
        int made = summary.ParentsCreated + summary.DependentsCreated;
        string heading = summary.Failed > 0
            ? $"{DuplicatorWindow.DialogTitle} - {made} made, {summary.Failed} failed"
            : $"{DuplicatorWindow.DialogTitle} - {made} view(s) made, {summary.Updated} updated";
        RibbitUi.ShowReport(heading, BuildReport(results, summary));
    }

    private static (string Label, string Class) Label(DupStepResult r) => r.Outcome switch
    {
        StepOutcome.Failed => ("failed", "skip"),
        StepOutcome.NotRun => r.Step.Action switch
        {
            DupAction.Exists => ("left alone", "warn"),
            DupAction.Repeat => ("repeat - ignored", "warn"),
            DupAction.Update => ("up to date", "warn"),
            _ => ("error - not made", "skip"),
        },
        _ => r.Step.Action switch
        {
            DupAction.CreateParent => (r.Step.IsAutomatic ? "created (automatic Overall)" : "created", "ok"),
            DupAction.CreateDependent => ("created dependent", "ok"),
            _ => ("updated", "ok"),
        },
    };

    private static string BuildReport(IReadOnlyList<DupStepResult> results, DupRunSummary s)
    {
        StringBuilder sb = new();
        sb.Append("<ul>");
        sb.Append($"<li><strong>{s.ParentsCreated}</strong> Overall/plain view(s) and "
                  + $"<strong>{s.DependentsCreated}</strong> dependent view(s) created</li>");
        sb.Append($"<li><strong>{s.Updated}</strong> updated, <strong>{s.TemplatesApplied}</strong> template(s) applied, "
                  + $"<strong>{s.ParamValuesSet}</strong> parameter value(s) copied from an Overall</li>");
        if (s.Failed > 0)
            sb.Append($"<li><strong>{s.Failed}</strong> refused by Revit - see below; everything else was done</li>");
        sb.Append("</ul>");

        // Problems first: a failure at row 60 must not need scrolling to find.
        IEnumerable<DupStepResult> ordered = results
            .OrderBy(r => r.Outcome == StepOutcome.Failed || (r.Outcome == StepOutcome.NotRun && r.Step.Action == DupAction.Error) ? 0 : 1)
            .ThenBy(r => r.Step.ExcelRow ?? r.Step.NeededByRow);

        List<IReadOnlyList<string>> rows = [];
        foreach (DupStepResult r in ordered)
        {
            (string label, string css) = Label(r);
            rows.Add(new[]
            {
                r.Step.ExcelRow?.ToString() ?? "(auto)",
                ReportHtml.Escape(r.Name),
                $"<span class=\"{css}\">{label}</span>",
                ReportHtml.Escape(string.Join("; ", r.Step.Notes.Concat(r.Notes))),
            });
        }
        ReportHtml.Table(sb, "Row by row", ["Row", "View", "Result", "Note"], rows);
        ReportHtml.Note(sb, "Rows marked <em>(auto)</em> are Overall views made because sheet rows needed one "
                            + "and no row named it. Ctrl+Z in Revit undoes the whole run.");
        return sb.ToString();
    }
}
