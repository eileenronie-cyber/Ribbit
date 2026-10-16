using Autodesk.Revit.DB;
using Ribbit.Geometry.Sheets;

namespace Ribbit.Native.Revit;

public enum StepOutcome
{
    /// <summary>The step did what the plan said.</summary>
    Done,

    /// <summary>A step the plan does not act on (left alone, a repeat, an error in the plan).</summary>
    NotRun,

    /// <summary>Revit refused it; the note says why.</summary>
    Failed,
}

/// <summary>What one planned step came to.</summary>
/// <param name="ViewId">The view made or updated; 0 when none.</param>
/// <param name="Name">The name it ended up with (differs from the plan only if Revit refused the planned one).</param>
public sealed record DupStepResult(DupStep Step, StepOutcome Outcome, long ViewId, string Name, IReadOnlyList<string> Notes);

public sealed record DupRunSummary(
    int ParentsCreated, int DependentsCreated, int Updated, int Failed, int TemplatesApplied, int ParamValuesSet);

/// <summary>
/// Part Sheets Duplicator - the Revit half. Everything it decides is decided by
/// <see cref="DuplicatorPlanner"/> first, from <see cref="Snapshot"/>: this
/// file only reads the model into plain data and carries a plan out.
///
/// Ported from lib/partsheetslib/duplicator.py; the naming and level rules live
/// in <see cref="ViewNaming"/>.
/// </summary>
internal static class DuplicatorEngine
{
    /// <summary>Identity parameters mirrored from an Overall view onto its dependents.</summary>
    internal static readonly string[] MirrorParams = ["B&W Discipline", "B&W Series"];

    // ------------------------------------------------------------- collection

    /// <summary>
    /// "ViewName  [LevelName]" - the dropdown label, and the key a level's
    /// chosen base view is remembered by. View ElementIds are not stable
    /// across model sessions; names are.
    /// </summary>
    public static string ViewDisplayName(ViewPlan v)
    {
        string level = LevelName(v);
        string name = v.Name?.Trim() ?? "";
        return level.Length > 0 ? $"{name}  [{level}]" : name;
    }

    private static string LevelName(ViewPlan v)
    {
        string level = "";
        try
        {
            level = v.GenLevel?.Name?.Trim() ?? "";
        }
        catch (Autodesk.Revit.Exceptions.ApplicationException)
        {
            // A view with no usable level still deserves a label.
        }
        return level;
    }

    /// <summary>The model as the planner sees it: floor plans and view templates.</summary>
    public static DupModel Snapshot(Document doc)
    {
        List<DupView> plans = [];
        List<DupTemplate> templates = [];

        foreach (View v in new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>())
        {
            if (v.IsTemplate)
            {
                templates.Add(new DupTemplate(RevitCompat.IdKey(v.Id), v.Name?.Trim() ?? "",
                    v.ViewType == ViewType.FloorPlan, GridAdjustCatalog.TypeLabel(v.ViewType)));
            }
            else if (v.ViewType == ViewType.FloorPlan && v is ViewPlan vp)
            {
                plans.Add(new DupView(
                    RevitCompat.IdKey(v.Id),
                    v.Name?.Trim() ?? "",
                    RevitCompat.IdKey(v.GetPrimaryViewId()) is long p && p > 0 ? p : 0,
                    RevitCompat.IdKey(v.ViewTemplateId) is long t && t > 0 ? t : 0,
                    ViewDisplayName(vp),
                    LevelName(vp)));
            }
        }

        return new DupModel(plans, templates);
    }

    // ------------------------------------------------------------------ apply

    /// <summary>
    /// Carry out <paramref name="plan"/>. The caller owns the transaction.
    /// A step Revit refuses is recorded and the rest go on; a step whose
    /// parent failed fails too, saying so.
    /// </summary>
    /// <param name="progress">(done, total) after each step; return false to stop (throws OperationCanceledException).</param>
    public static (List<DupStepResult> Results, DupRunSummary Summary) Apply(
        Document doc, DupPlan plan, Func<int, int, bool>? progress = null)
    {
        List<DupStepResult> results = [];
        Dictionary<int, View> made = [];
        int parents = 0, dependents = 0, updated = 0, failed = 0, templates = 0, values = 0;

        int total = plan.Steps.Count(s => s.Writes);
        int done = 0;

        for (int i = 0; i < plan.Steps.Count; i++)
        {
            DupStep step = plan.Steps[i];
            if (!step.Writes)
            {
                results.Add(new DupStepResult(step, StepOutcome.NotRun,
                    step.ExistingId, step.Name, []));
                continue;
            }

            List<string> notes = [];
            View? view = null;
            string name = step.Name;
            try
            {
                switch (step.Action)
                {
                    case DupAction.CreateParent:
                    {
                        View source = doc.GetElement(RevitCompat.IdFrom(step.BaseViewId)) as View
                                      ?? throw new InvalidOperationException("its base view is no longer in the model");
                        view = Duplicate(doc, source, ViewDuplicateOption.WithDetailing, ref name, notes);
                        parents++;
                        break;
                    }
                    case DupAction.CreateDependent:
                    {
                        View parent = Parent(doc, step, made)
                                      ?? throw new InvalidOperationException($"its Overall \"{step.ParentName}\" was not made");
                        view = Duplicate(doc, parent, ViewDuplicateOption.AsDependent, ref name, notes);
                        dependents++;
                        break;
                    }
                    case DupAction.Update:
                        view = doc.GetElement(RevitCompat.IdFrom(step.ExistingId)) as View
                               ?? throw new InvalidOperationException("the view is no longer in the model");
                        updated++;
                        break;
                }

                if (view is null)
                    throw new InvalidOperationException("Revit made no view");
                made[i] = view;

                if (step.Template == TemplatePlan.Apply && ApplyTemplate(view, step, notes))
                    templates++;

                if (step.MirrorFromParent && Parent(doc, step, made) is View from)
                    values += CopyIdentityParams(from, view, notes);

                results.Add(new DupStepResult(step, StepOutcome.Done, RevitCompat.IdKey(view.Id), name, notes));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                notes.Insert(0, "Revit refused: " + ex.Message);
                results.Add(new DupStepResult(step, StepOutcome.Failed, 0, name, notes));
            }

            done++;
            if (progress is not null && !progress(done, total))
                throw new OperationCanceledException();
        }

        return (results, new DupRunSummary(parents, dependents, updated, failed, templates, values));
    }

    private static View? Parent(Document doc, DupStep step, Dictionary<int, View> made)
    {
        if (step.ParentStep >= 0)
            return made.TryGetValue(step.ParentStep, out View? v) ? v : null;
        return step.ParentId != 0 ? doc.GetElement(RevitCompat.IdFrom(step.ParentId)) as View : null;
    }

    /// <summary>
    /// Duplicate and name. If Revit refuses the planned name - something the
    /// snapshot does not show holds it - the first free "name (n)" is used and
    /// the step says so, rather than the view being left as "Copy of ...".
    /// </summary>
    private static View Duplicate(Document doc, View source, ViewDuplicateOption option, ref string name, List<string> notes)
    {
        if (!source.CanViewBeDuplicated(option))
            throw new InvalidOperationException($"\"{source.Name}\" cannot be duplicated {(option == ViewDuplicateOption.AsDependent ? "as a dependent" : "with detailing")}");

        View view = doc.GetElement(source.Duplicate(option)) as View
                    ?? throw new InvalidOperationException("Revit made no view");

        string planned = name;
        for (int n = 0; ; n++)
        {
            string candidate = n == 0 ? planned : $"{planned} ({n})";
            try
            {
                view.Name = candidate;
                name = candidate;
                break;
            }
            catch (Autodesk.Revit.Exceptions.ArgumentException) when (n < 50)
            {
            }
        }
        if (name != planned)
            notes.Add($"Revit would not take the name \"{planned}\", so it is \"{name}\".");
        return view;
    }

    private static bool ApplyTemplate(View view, DupStep step, List<string> notes)
    {
        ElementId id = RevitCompat.IdFrom(step.TemplateId);
        if (!view.IsValidViewTemplate(id))
        {
            notes.Add($"Revit says \"{step.TemplateName}\" is not a template this view can use - not applied.");
            return false;
        }
        view.ViewTemplateId = id;
        return true;
    }

    /// <summary>Copies each identity parameter both views have; returns how many were set.</summary>
    private static int CopyIdentityParams(View src, View dst, List<string> notes)
    {
        int set = 0;
        foreach (string pname in MirrorParams)
        {
            Parameter? sp = src.LookupParameter(pname);
            Parameter? dp = dst.LookupParameter(pname);
            if (sp is null || dp is null || dp.IsReadOnly)
                continue;

            bool ok = sp.StorageType switch
            {
                StorageType.String => dp.Set(sp.AsString() ?? ""),
                StorageType.Double => dp.Set(sp.AsDouble()),
                StorageType.Integer => dp.Set(sp.AsInteger()),
                StorageType.ElementId => dp.Set(sp.AsElementId()),
                _ => false,
            };
            if (ok)
                set++;
            else
                notes.Add($"\"{pname}\" could not be copied from the Overall.");
        }
        return set;
    }
}
