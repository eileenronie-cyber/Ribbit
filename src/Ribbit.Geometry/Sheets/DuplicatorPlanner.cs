namespace Ribbit.Geometry.Sheets;

/// <summary>One worksheet row, as the duplicator reads it.</summary>
/// <param name="ExcelRow">The row number Excel shows (0 when not known).</param>
public sealed record DupRow(int ExcelRow, string Series, string FloorPlanViewName, string Phase, string TemplateName)
{
    public static DupRow From(XlsxRow r, int index)
        => new(r.RowNumber > 0 ? r.RowNumber : index + 2, r.Get("Series").Trim(), r.Get("Floor Plan View Name").Trim(),
               r.Get("Phase").Trim(), r.Get("Template Name").Trim());
}

/// <summary>A non-template floor plan already in the model.</summary>
/// <param name="Id">The view's ElementId value.</param>
/// <param name="PrimaryId">For a dependent view, its primary (parent) view's id; 0 otherwise.</param>
/// <param name="TemplateId">The view template it has now; 0 for none.</param>
/// <param name="Label">What a person sees for it: "Name  [Level]".</param>
/// <param name="LevelName">Its own level's name (a base view is suggested for the level with the same key).</param>
public sealed record DupView(long Id, string Name, long PrimaryId, long TemplateId, string Label = "", string LevelName = "")
{
    public bool IsDependent => PrimaryId != 0;
}

/// <summary>A view template in the model.</summary>
/// <param name="Kind">What views it is for, as Revit names them ("Floor Plan", "Ceiling Plan", ...).</param>
public sealed record DupTemplate(long Id, string Name, bool ForFloorPlans, string Kind);

/// <summary>What the model holds, taken once, so the whole plan is made from plain data.</summary>
/// <param name="ReservedNames">
/// Names Revit would refuse for a NEW floor plan beyond the floor plans' own
/// (the Revit side decides what belongs here), each with what holds it.
/// </param>
public sealed record DupModel(
    IReadOnlyList<DupView> FloorPlans,
    IReadOnlyList<DupTemplate> Templates,
    IReadOnlyDictionary<string, string>? ReservedNames = null);

/// <summary>What the user chose for one row, overriding what its name says.</summary>
public enum RowChoiceKind
{
    /// <summary>Decided from the name ("Sheet N of M" = a dependent of its Overall).</summary>
    Automatic,

    /// <summary>An independent view from the level's base view, whatever the name says.</summary>
    Independent,

    /// <summary>A dependent of <see cref="RowChoice.Parent"/> - an existing view or one this run makes.</summary>
    DependentOf,

    Skip,
}

/// <summary>A row's choice. <paramref name="Parent"/> is a view name, for DependentOf.</summary>
public sealed record RowChoice(RowChoiceKind Kind, string Parent = "")
{
    public static RowChoice Auto { get; } = new(RowChoiceKind.Automatic);
}

public enum DupAction
{
    /// <summary>A new independent view, duplicated with detailing from the level's base view.</summary>
    CreateParent,

    /// <summary>A new dependent ("Sheet N of M") view under its Overall.</summary>
    CreateDependent,

    /// <summary>An existing view the options say to update: template re-applied, parameters mirrored.</summary>
    Update,

    /// <summary>An existing view left alone (Skip mode).</summary>
    Exists,

    /// <summary>A row naming a view an earlier row already names.</summary>
    Repeat,

    /// <summary>A row the user chose to skip.</summary>
    Skipped,

    Error,
}

public enum TemplatePlan
{
    /// <summary>The row names no template.</summary>
    None,
    NotFound,

    AlreadySet,
    Apply,

    /// <summary>Skip mode: an existing view is not touched.</summary>
    LeftAlone,

    /// <summary>
    /// A dependent view: Revit shows its PARENT's template whatever is set on it
    /// (proven in Revit - a template set on a new sheet under an untemplated
    /// parent read back as none). So none is set; <see cref="DupStep.TemplateName"/>
    /// is the parent's.
    /// </summary>
    FromParent,
}

/// <summary>
/// One thing the run will do. Steps are listed in the order they are carried
/// out: every parent before the dependents made under it.
/// </summary>
public sealed class DupStep
{
    /// <summary>The worksheet row; null for an Overall made only because dependent rows need one.</summary>
    public int? ExcelRow { get; init; }

    /// <summary>For an automatic Overall: the first row that needed it (for listing it beside that row).</summary>
    public int NeededByRow { get; init; }

    public DupAction Action { get; init; }
    public string Series { get; init; } = "";

    /// <summary>The view's name - what the worksheet says, with the phase appended.</summary>
    public string Name { get; init; } = "";

    /// <summary>Set when <see cref="Name"/> was taken by something else and a free one is used instead.</summary>
    public string? RenamedFrom { get; init; }

    public string LevelKey { get; init; } = "";

    /// <summary>The existing view this step updates or leaves alone.</summary>
    public long ExistingId { get; init; }

    /// <summary>CreateParent: the base view it is duplicated from.</summary>
    public long BaseViewId { get; init; }
    public string BaseViewLabel { get; init; } = "";

    /// <summary>CreateDependent: the parent's name, and either the step that creates it or the existing view.</summary>
    public string ParentName { get; init; } = "";
    public int ParentStep { get; init; } = -1;
    public long ParentId { get; init; }

    public string TemplateName { get; internal set; } = "";
    public long TemplateId { get; internal set; }
    public TemplatePlan Template { get; internal set; }

    /// <summary>
    /// Set ("Ceiling Plan", say) when the template is for another kind of view.
    /// Revit accepts one on a floor plan, and the worksheet asked for it, so it IS
    /// applied - but it is worth a second look, so the window flags it.
    /// </summary>
    public string TemplateKind { get; internal set; } = "";

    /// <summary>Copy the parent's identity parameters onto this view.</summary>
    public bool MirrorFromParent { get; init; }

    /// <summary>
    /// The parent is where this sheet's existing sister sheets already hang,
    /// because no view has the "... Overall" name.
    /// </summary>
    public bool ParentFromSisters { get; init; }

    /// <summary>The user chose what this row does (it is not what its name says).</summary>
    public bool ByYou { get; internal set; }

    /// <summary>A dependent whose row asked for a template other than the one its parent gives it.</summary>
    public bool TemplateNotShown { get; internal set; }

    /// <summary>Why a step is an error or a repeat, and anything else worth knowing.</summary>
    public List<string> Notes { get; } = [];

    public bool IsAutomatic => ExcelRow is null;
    public bool Creates => Action is DupAction.CreateParent or DupAction.CreateDependent;
    public bool Writes => Creates || (Action == DupAction.Update && (Template == TemplatePlan.Apply || MirrorFromParent));
}

/// <summary>One level the worksheet references, and what it needs.</summary>
/// <param name="NeedsBaseView">
/// False when every view on the level already exists (or only dependents of an
/// existing Overall are to be made) - then no base view has to be chosen.
/// </param>
public sealed record DupLevel(string Key, string Example, int Rows, bool NeedsBaseView, long BaseViewId);

public sealed record DupCounts(
    int Rows, int CreateParents, int CreateDependents, int Automatic, int Update, int Exists, int Repeat, int Error,
    int TemplatesToApply, int Skipped = 0)
{
    public int Creates => CreateParents + CreateDependents;
    public int Actionable => Creates + Update;
}

public sealed class DupPlan
{
    /// <summary>In the order they run.</summary>
    public List<DupStep> Steps { get; } = [];
    public List<DupLevel> Levels { get; } = [];
    public DupCounts Counts { get; internal set; } = new(0, 0, 0, 0, 0, 0, 0, 0, 0);

    /// <summary>Every view a dependent could be put under: existing independent floor plans and the independent views this run makes.</summary>
    public List<string> ParentChoices { get; } = [];

    /// <summary>Levels that need a base view and have none: their rows are errors until one is chosen.</summary>
    public List<string> MissingBaseViews { get; } = [];

    /// <summary>Template names no template in the model has.</summary>
    public List<string> MissingTemplates { get; } = [];

    /// <summary>Templates asked for that are for another kind of view (applied, but flagged): name, kind.</summary>
    public List<(string Name, string Kind)> OtherKindTemplates { get; } = [];

    /// <summary>The steps in worksheet order, each automatic Overall just before the first row that needed it.</summary>
    public IEnumerable<DupStep> InSheetOrder()
        => Steps.Select((s, i) => (s, i))
                .OrderBy(t => t.s.ExcelRow ?? t.s.NeededByRow)
                .ThenBy(t => t.s.IsAutomatic ? 0 : 1)
                .ThenBy(t => t.i)
                .Select(t => t.s);
}

/// <summary>
/// Part Sheets Duplicator's decisions, made from plain data so they can be
/// shown before anything is written - the window lists this plan, and the
/// Revit side carries it out step by step. There is no separate "dry run": the
/// plan IS the preview, so the preview cannot disagree with the run.
///
/// A row whose name has "Sheet N of M" (and not "Overall") is a DEPENDENT view:
/// it is made under its Overall - the same name with "Sheet N of M" replaced by
/// "Overall". If that Overall is neither in the model nor on another row, it is
/// made automatically. Any other row is an independent view duplicated from its
/// level's base view.
///
/// Parents are planned before dependents, so a worksheet that lists a sheet
/// above its Overall works the same as one that lists it below.
///
/// Names differ from project to project, so the user can override any row
/// (<see cref="RowChoice"/>): make it an independent view, a dependent of a
/// view they pick, or skip it.
/// </summary>
public static class DuplicatorPlanner
{
    public static DupPlan Plan(
        IReadOnlyList<DupRow> rows,
        DupModel model,
        IReadOnlyDictionary<string, long> baseViews,
        ExistingMode mode,
        IReadOnlyDictionary<int, RowChoice>? choices = null)
    {
        choices ??= new Dictionary<int, RowChoice>();

        // Which levels need a base view at all: plan as if every level had one,
        // and see which levels' steps duplicate from it.
        HashSet<string> needing = [];
        Dictionary<string, long> everyLevel = [];
        foreach (DupRow r in rows)
        {
            string? k = ViewNaming.NormalizeLevelKey(r.FloorPlanViewName);
            if (k is not null)
                everyLevel[k] = -1;
        }
        foreach (DupStep s in Build(rows, model, everyLevel, mode, choices).Steps)
        {
            if (s.Action == DupAction.CreateParent)
                needing.Add(s.LevelKey);
        }

        DupPlan plan = Build(rows, model, baseViews, mode, choices);

        foreach (ViewNaming.LevelKeyEntry e in ViewNaming.LevelKeySummary(rows.Select(ToXlsx)))
        {
            baseViews.TryGetValue(e.Key, out long id);
            plan.Levels.Add(new DupLevel(e.Key, e.Example, e.Count, needing.Contains(e.Key), id));
            if (needing.Contains(e.Key) && id == 0)
                plan.MissingBaseViews.Add(e.Key);
        }

        return plan;
    }

    private static XlsxRow ToXlsx(DupRow r)
        => new(new Dictionary<string, string> { [SheetText.Normalize("Floor Plan View Name")] = r.FloorPlanViewName });

    private static string Low(string s) => s.Trim().ToLowerInvariant();

    private static DupPlan Build(
        IReadOnlyList<DupRow> rows, DupModel model, IReadOnlyDictionary<string, long> baseViews, ExistingMode mode,
        IReadOnlyDictionary<int, RowChoice> choices)
    {
        DupPlan plan = new();
        bool update = mode == ExistingMode.Update;

        Dictionary<string, DupView> existing = [];
        foreach (DupView v in model.FloorPlans)
        {
            string k = Low(v.Name);
            if (!existing.ContainsKey(k))
                existing[k] = v;
        }
        Dictionary<long, DupView> byId = [];
        foreach (DupView v in model.FloorPlans)
            byId[v.Id] = v;

        // Existing "Sheet N of M" dependents, by their name without that part:
        // a new sheet's sisters.
        Dictionary<string, List<DupView>> sheetFamilies = [];
        foreach (DupView v in model.FloorPlans)
        {
            if (!v.IsDependent || !ViewNaming.HasSheetToken(v.Name))
                continue;
            string k = ViewNaming.SheetFamilyKey(v.Name);
            if (!sheetFamilies.TryGetValue(k, out List<DupView>? list))
                sheetFamilies[k] = list = [];
            list.Add(v);
        }

        Dictionary<string, DupTemplate> templates = [];
        foreach (DupTemplate t in model.Templates)
        {
            string k = Low(t.Name);
            // Two templates can share a name across kinds; prefer the floor-plan one.
            if (!templates.TryGetValue(k, out DupTemplate? had) || (!had.ForFloorPlans && t.ForFloorPlans))
                templates[k] = t;
        }

        // Every name this run takes, so two new views never collide and a
        // reserved name is never used.
        HashSet<string> taken = [.. existing.Keys];
        if (model.ReservedNames is not null)
            foreach (string n in model.ReservedNames.Keys)
                taken.Add(Low(n));

        // Target name -> the step that owns it (the first row naming it).
        Dictionary<string, int> stepByName = [];

        // The names rows ask for as independent views: an Overall named by its
        // own row is made (or updated) by THAT row, not automatically.
        HashSet<string> parentRowNames = [];

        List<(DupRow Row, string Target, string Level)> parents = [];

        // Parent: null = decided from the name; otherwise the view the user chose.
        List<(DupRow Row, string Target, string Level, string? Parent)> dependents = [];

        foreach (DupRow row in rows)
        {
            RowChoice choice = choices.TryGetValue(row.ExcelRow, out RowChoice? c) ? c : RowChoice.Auto;
            string fpv = row.FloorPlanViewName;
            if (fpv.Length == 0)
            {
                Error(plan, row, "", "", "The \"Floor Plan View Name\" cell is empty.");
                continue;
            }

            string target = ViewNaming.EnsurePhaseAppended(fpv, row.Phase);
            string? level = ViewNaming.NormalizeLevelKey(fpv);
            if (choice.Kind == RowChoiceKind.Skip)
            {
                DupStep skipped = new() { ExcelRow = row.ExcelRow, Action = DupAction.Skipped, Series = row.Series, Name = target, LevelKey = level ?? "" };
                skipped.Notes.Add("Skipped - you chose to leave this row out.");
                plan.Steps.Add(skipped);
                continue;
            }

            // A dependent of a view the user picked needs no level: nothing is
            // copied from a base view.
            if (level is null && choice.Kind != RowChoiceKind.DependentOf)
            {
                Error(plan, row, fpv, "",
                    "No level found in the name - it needs one such as \"Level 01\" or \"Roof Level\" "
                    + "(or choose \"Dependent of\" for it).");
                continue;
            }

            bool dependent = choice.Kind switch
            {
                RowChoiceKind.Independent => false,
                RowChoiceKind.DependentOf => true,
                _ => ViewNaming.HasSheetToken(fpv) && !ViewNaming.HasOverallToken(fpv),
            };
            if (dependent)
            {
                dependents.Add((row, target, level ?? "", choice.Kind == RowChoiceKind.DependentOf ? choice.Parent : null));
            }
            else
            {
                parents.Add((row, target, level!));
                parentRowNames.Add(Low(target));
            }
        }

        // ------------------------------------------------ independent views
        foreach ((DupRow row, string target, string level) in parents)
        {
            string key = Low(target);
            if (stepByName.TryGetValue(key, out int first))
            {
                Repeat(plan, row, target, level, plan.Steps[first]);
                continue;
            }

            if (existing.TryGetValue(key, out DupView? view))
            {
                stepByName[key] = Existing(plan, row, target, level, view, null, templates, update);
                continue;
            }

            // Registered even when it is an error, so its sheets point at it
            // instead of each trying to make the same Overall again.
            NewParent(plan, row.ExcelRow, 0, row, target, level, baseViews, templates, taken, model);
            stepByName[key] = plan.Steps.Count - 1;
        }

        // ----------------------------------------------------- dependents
        Dictionary<string, int> autoParents = [];
        foreach ((DupRow row, string target, string level, string? chosen) in dependents)
        {
            string key = Low(target);
            if (stepByName.TryGetValue(key, out int first))
            {
                Repeat(plan, row, target, level, plan.Steps[first]);
                continue;
            }

            string parentName = chosen ?? ViewNaming.ParentNameFromFpv(row.FloorPlanViewName, row.Phase);
            string parentKey = Low(parentName);
            existing.TryGetValue(key, out DupView? view);

            // ---- where its Overall comes from
            int parentStep = -1;
            long parentId = 0;
            string? parentProblem = null;
            string? fromSisters = null;

            if (chosen is not null)
            {
                // The user's pick: that view and nothing else - no sisters, no
                // automatic Overall.
                if (stepByName.TryGetValue(parentKey, out int cs) && plan.Steps[cs].Action == DupAction.CreateParent)
                    parentStep = cs;
                else if (existing.TryGetValue(parentKey, out DupView? cv) && !cv.IsDependent)
                    parentId = cv.Id;
                else if (cv is not null || (stepByName.TryGetValue(parentKey, out int cd) && plan.Steps[cd].Action == DupAction.CreateDependent))
                    parentProblem = $"The view you chose, \"{chosen}\", is a dependent view, and a dependent view cannot have dependents of its own.";
                else if (stepByName.TryGetValue(parentKey, out int cf))
                    parentProblem = $"The view you chose, \"{chosen}\", cannot be made - see row {plan.Steps[cf].ExcelRow}.";
                else
                    parentProblem = $"The view you chose, \"{chosen}\", is not in the model and no row makes it.";
            }
            else if (stepByName.TryGetValue(parentKey, out int ps) && plan.Steps[ps].Creates)
            {
                parentStep = ps;
            }
            else if (existing.TryGetValue(parentKey, out DupView? pv))
            {
                if (pv.IsDependent)
                {
                    parentProblem = $"Its Overall \"{pv.Name}\" is itself a dependent view, and a dependent view "
                                    + "cannot have dependents of its own.";
                }
                else
                {
                    parentId = pv.Id;

                    // An existing Overall with no row of its own is updated
                    // once, with its first sheet's template, in Update mode.
                    if (update && !parentRowNames.Contains(parentKey) && !autoParents.ContainsKey(parentKey))
                        autoParents[parentKey] = Existing(plan, null, pv.Name, level, pv, row, templates, true);
                }
            }
            else if (stepByName.TryGetValue(parentKey, out int failed))
            {
                parentProblem = $"Its Overall \"{parentName}\" cannot be made - see row {plan.Steps[failed].ExcelRow}.";
            }
            else if (SisterParent(target, sheetFamilies, byId) is (DupView sister, int sisters))
            {
                // No "... Overall" anywhere, but this sheet's sisters ("Sheet 1
                // of 3" beside a new "Sheet 3 of 4") already hang under a parent
                // of their own: join them there rather than start a second
                // parent for one sheet - which is what a real run did.
                parentId = sister.Id;
                fromSisters = $"No \"{parentName}\" exists, so it goes under \"{sister.Name}\" beside its "
                              + $"{sisters} sister sheet{(sisters == 1 ? "" : "s")}.";
                parentName = sister.Name;
            }
            else if (autoParents.TryGetValue(parentKey, out int ap))
            {
                if (plan.Steps[ap].Creates)
                    parentStep = ap;
                else
                    parentProblem = $"Its Overall \"{parentName}\" cannot be made: {plan.Steps[ap].Notes.FirstOrDefault()}";
            }
            else if (view is null)
            {
                // No Overall anywhere, and this sheet is to be made: make one
                // with this row's template. (A sheet that already exists never
                // causes an Overall to be made.)
                DupStep? made = NewParent(plan, null, row.ExcelRow, row, parentName, level, baseViews, templates, taken, model);
                autoParents[parentKey] = plan.Steps.Count - 1;
                if (made is not null)
                    parentStep = plan.Steps.Count - 1;
                else
                    parentProblem = $"Its Overall \"{parentName}\" cannot be made: {plan.Steps[plan.Steps.Count - 1].Notes.FirstOrDefault()}";
            }

            // ---- a sheet already in the model
            if (view is not null)
            {
                int s = Existing(plan, row, target, level, view, null, templates, update,
                    parentProblem is null ? parentName : "", parentStep, parentId);
                stepByName[key] = s;
                if (!update)
                    continue;

                if (view.PrimaryId != 0 && parentId != 0 && view.PrimaryId != parentId)
                {
                    plan.Steps[s].Notes.Add(
                        $"It is a dependent of \"{(byId.TryGetValue(view.PrimaryId, out DupView? p) ? p.Name : "another view")}\", "
                        + $"not of \"{parentName}\" - Revit cannot move a dependent, so it stays there.");
                }
                else if (view.PrimaryId == 0)
                {
                    plan.Steps[s].Notes.Add("It is an independent view, not a dependent - it stays that way.");
                }
                if (parentProblem is not null)
                    plan.Steps[s].Notes.Add(parentProblem + " No parameters are copied.");
                else if (parentStep < 0 && parentId == 0)
                    plan.Steps[s].Notes.Add($"Its Overall \"{parentName}\" is not in the model, so no parameters are copied.");
                continue;
            }

            if (parentProblem is not null)
            {
                stepByName[key] = plan.Steps.Count;
                Error(plan, row, target, level, parentProblem);
                continue;
            }

            string unique = Unique(target, taken, out string? renamed);
            DupStep dep = new()
            {
                ExcelRow = row.ExcelRow,
                Action = DupAction.CreateDependent,
                Series = row.Series,
                Name = unique,
                RenamedFrom = renamed,
                LevelKey = level,
                ParentName = parentName,
                ParentStep = parentStep,
                ParentId = parentId,
                TemplateName = row.TemplateName,       // the row's, until FollowParentTemplates
                Template = TemplatePlan.FromParent,
                MirrorFromParent = true,
                ParentFromSisters = fromSisters is not null,
            };
            if (fromSisters is not null)
                dep.Notes.Add(fromSisters);
            AddRenameNote(dep, model);
            stepByName[key] = plan.Steps.Count;
            plan.Steps.Add(dep);
        }

        FollowParentTemplates(plan, byId, model.Templates);

        foreach (DupStep s in plan.Steps)
        {
            if (choices.TryGetValue(s.ExcelRow ?? -1, out RowChoice? c) && c.Kind != RowChoiceKind.Automatic)
                s.ByYou = true;
            if (s.Template == TemplatePlan.NotFound && !plan.MissingTemplates.Contains(s.TemplateName, StringComparer.OrdinalIgnoreCase))
                plan.MissingTemplates.Add(s.TemplateName);
            if (s.Template is TemplatePlan.Apply or TemplatePlan.AlreadySet
                && templates.TryGetValue(Low(s.TemplateName), out DupTemplate? t) && !t.ForFloorPlans)
            {
                s.TemplateKind = t.Kind;
                s.Notes.Add($"\"{t.Name}\" is a {t.Kind} template, not a Floor Plan one - Revit accepts it, so it is applied.");
                if (!plan.OtherKindTemplates.Any(w => w.Name == t.Name))
                    plan.OtherKindTemplates.Add((t.Name, t.Kind));
            }
        }

        plan.Counts = new DupCounts(
            rows.Count,
            plan.Steps.Count(s => s.Action == DupAction.CreateParent),
            plan.Steps.Count(s => s.Action == DupAction.CreateDependent),
            plan.Steps.Count(s => s.IsAutomatic),
            plan.Steps.Count(s => s.Action == DupAction.Update),
            plan.Steps.Count(s => s.Action == DupAction.Exists),
            plan.Steps.Count(s => s.Action == DupAction.Repeat),
            plan.Steps.Count(s => s.Action == DupAction.Error),
            plan.Steps.Count(s => s.Template == TemplatePlan.Apply),
            plan.Steps.Count(s => s.Action == DupAction.Skipped));

        // What a dependent can go under: existing independent floor plans, and
        // the independent views this run makes.
        plan.ParentChoices.AddRange(
            model.FloorPlans.Where(v => !v.IsDependent).Select(v => v.Name)
                .Concat(plan.Steps.Where(s => s.Action == DupAction.CreateParent).Select(s => s.Name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n, NaturalSort.Comparer));
        return plan;
    }

    /// <summary>
    /// A dependent view shows its parent's template, so each dependent's
    /// template is its parent's as it will be after this run: the parent step's
    /// template, else (a new copy) the base view's, else the existing view's.
    /// Says so when the row asked for a different one.
    /// </summary>
    private static void FollowParentTemplates(DupPlan plan, Dictionary<long, DupView> byId, IReadOnlyList<DupTemplate> templates)
    {
        Dictionary<long, string> names = [];
        foreach (DupTemplate t in templates)
            names[t.Id] = t.Name;
        string NameOf(long id) => id != 0 && names.TryGetValue(id, out string? n) ? n : "";

        long TemplateAfterRun(DupStep p)
        {
            if (p.Template is TemplatePlan.Apply or TemplatePlan.AlreadySet)
                return p.TemplateId;
            if (p.Action == DupAction.CreateParent)
                return byId.TryGetValue(p.BaseViewId, out DupView? b) ? b.TemplateId : 0;
            return byId.TryGetValue(p.ExistingId, out DupView? e) ? e.TemplateId : 0;
        }

        long ParentTemplate(DupStep s)
        {
            if (s.ParentStep >= 0)
                return TemplateAfterRun(plan.Steps[s.ParentStep]);
            // An existing dependent's REAL parent, else the planned one.
            long parent = byId.TryGetValue(s.ExistingId, out DupView? self) && self.IsDependent ? self.PrimaryId : s.ParentId;
            DupStep? updated = plan.Steps.FirstOrDefault(p => p.Action == DupAction.Update && p.ExistingId == parent && p.ExistingId != 0);
            if (updated is not null)
                return TemplateAfterRun(updated);
            return byId.TryGetValue(parent, out DupView? v) ? v.TemplateId : 0;
        }

        foreach (DupStep s in plan.Steps.Where(s => s.Template == TemplatePlan.FromParent))
        {
            string asked = s.TemplateName;
            long id = ParentTemplate(s);
            s.TemplateId = id;
            s.TemplateName = NameOf(id);
            if (asked.Length > 0 && !string.Equals(asked, s.TemplateName, StringComparison.OrdinalIgnoreCase))
            {
                s.TemplateNotShown = true;
                s.Notes.Add($"A dependent view shows its parent's template ({(s.TemplateName.Length > 0 ? $"\"{s.TemplateName}\"" : "none")}), "
                            + $"so the row's \"{asked}\" does not show on it.");
            }
        }
    }

    // ------------------------------------------------------------------ steps

    private static void Error(DupPlan plan, DupRow row, string name, string level, string why)
    {
        DupStep s = new() { ExcelRow = row.ExcelRow, Action = DupAction.Error, Series = row.Series, Name = name, LevelKey = level, TemplateName = row.TemplateName };
        s.Notes.Add(why);
        plan.Steps.Add(s);
    }

    private static void Repeat(DupPlan plan, DupRow row, string name, string level, DupStep first)
    {
        DupStep s = new() { ExcelRow = row.ExcelRow, Action = DupAction.Repeat, Series = row.Series, Name = name, LevelKey = level, TemplateName = row.TemplateName };
        s.Notes.Add(first.ExcelRow is int r
            ? $"Row {r} already names this view - this row is ignored."
            : "An earlier row already names this view - this row is ignored.");
        plan.Steps.Add(s);
    }

    /// <summary>A view already in the model: updated or left alone. Returns its step index.</summary>
    private static int Existing(
        DupPlan plan, DupRow? row, string name, string level, DupView view, DupRow? neededBy,
        Dictionary<string, DupTemplate> templates, bool update,
        string parentName = "", int parentStep = -1, long parentId = 0)
    {
        string templateName = (row ?? neededBy)!.TemplateName;
        bool dependent = parentStep >= 0 || parentId != 0;

        DupStep s;
        if (update)
        {
            // A dependent view's template is its parent's (FollowParentTemplates).
            (TemplatePlan tp, long tid, string note) = view.IsDependent
                ? (TemplatePlan.FromParent, 0L, "")
                : ResolveTemplate(templateName, templates, view.TemplateId);
            s = new DupStep
            {
                ExcelRow = row?.ExcelRow,
                NeededByRow = neededBy?.ExcelRow ?? 0,
                Action = DupAction.Update,
                Series = (row ?? neededBy)!.Series,
                Name = view.Name,
                LevelKey = level,
                ExistingId = view.Id,
                ParentName = parentName,
                ParentStep = parentStep,
                ParentId = parentId,
                TemplateName = templateName,
                TemplateId = tid,
                Template = tp,
                MirrorFromParent = dependent,
            };
            AddNote(s, note);
        }
        else
        {
            s = new DupStep
            {
                ExcelRow = row?.ExcelRow,
                NeededByRow = neededBy?.ExcelRow ?? 0,
                Action = DupAction.Exists,
                Series = (row ?? neededBy)!.Series,
                Name = view.Name,
                LevelKey = level,
                ExistingId = view.Id,
                ParentName = parentName,
                ParentStep = parentStep,
                ParentId = parentId,
                TemplateName = templateName,
                Template = TemplatePlan.LeftAlone,
            };
        }

        if (row is null)
            s.Notes.Add("Not on its own row: it is the Overall of the sheets below.");

        plan.Steps.Add(s);
        return plan.Steps.Count - 1;
    }

    /// <summary>A new independent view from the level's base view, or an error step if there is none.</summary>
    private static DupStep? NewParent(
        DupPlan plan, int? excelRow, int neededBy, DupRow row, string name, string level,
        IReadOnlyDictionary<string, long> baseViews, Dictionary<string, DupTemplate> templates, HashSet<string> taken,
        DupModel model)
    {
        if (!baseViews.TryGetValue(level, out long baseId) || baseId == 0)
        {
            DupStep err = new()
            {
                ExcelRow = excelRow,
                NeededByRow = neededBy,
                Action = DupAction.Error,
                Series = row.Series,
                Name = name,
                LevelKey = level,
                TemplateName = row.TemplateName,
            };
            err.Notes.Add($"No base view is chosen for {ViewNaming.LevelTitle(level)}.");
            plan.Steps.Add(err);
            return null;
        }

        (TemplatePlan tp, long tid, string note) = ResolveTemplate(row.TemplateName, templates, 0);
        string unique = Unique(name, taken, out string? renamed);
        DupStep s = new()
        {
            ExcelRow = excelRow,
            NeededByRow = neededBy,
            Action = DupAction.CreateParent,
            Series = row.Series,
            Name = unique,
            RenamedFrom = renamed,
            LevelKey = level,
            BaseViewId = baseId,
            TemplateName = row.TemplateName,
            TemplateId = tid,
            Template = tp,
        };
        if (excelRow is null)
            s.Notes.Add("No row names this Overall, so it is made for the sheets that need it.");
        AddNote(s, note);
        AddRenameNote(s, model);
        plan.Steps.Add(s);
        return s;
    }

    /// <summary>
    /// The independent view most of a new sheet's existing sisters are
    /// dependents of (ties: the first by name), and how many sisters are there.
    /// </summary>
    private static (DupView Parent, int Sisters)? SisterParent(
        string target, Dictionary<string, List<DupView>> families, Dictionary<long, DupView> byId)
    {
        if (!families.TryGetValue(ViewNaming.SheetFamilyKey(target), out List<DupView>? sisters))
            return null;

        var best = sisters
            .Where(s => !string.Equals(s.Name.Trim(), target.Trim(), StringComparison.OrdinalIgnoreCase))
            .Where(s => byId.TryGetValue(s.PrimaryId, out DupView? p) && !p.IsDependent)
            .GroupBy(s => s.PrimaryId)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => byId[g.Key].Name, StringComparer.Ordinal)
            .FirstOrDefault();
        return best is null ? null : (byId[best.Key], best.Count());
    }

    private static void AddNote(DupStep s, string note)
    {
        if (note.Length > 0)
            s.Notes.Add(note);
    }

    private static void AddRenameNote(DupStep s, DupModel model)
    {
        if (s.RenamedFrom is null)
            return;
        string holder = model.ReservedNames?.FirstOrDefault(
            kv => string.Equals(kv.Key.Trim(), s.RenamedFrom, StringComparison.OrdinalIgnoreCase)).Value ?? "another view";
        s.Notes.Add($"\"{s.RenamedFrom}\" is already used by {holder}, so it is named \"{s.Name}\".");
    }

    private static string Unique(string name, HashSet<string> taken, out string? renamed)
    {
        renamed = null;
        string result = name;
        if (taken.Contains(Low(name)))
        {
            for (int i = 1; ; i++)
            {
                string candidate = $"{name} ({i})";
                if (!taken.Contains(Low(candidate)))
                {
                    result = candidate;
                    renamed = name;
                    break;
                }
            }
        }
        taken.Add(Low(result));
        return result;
    }

    private static (TemplatePlan Plan, long Id, string Note) ResolveTemplate(
        string name, Dictionary<string, DupTemplate> templates, long current)
    {
        if (name.Trim().Length == 0)
            return (TemplatePlan.None, 0, "");
        if (!templates.TryGetValue(Low(name), out DupTemplate? t))
            return (TemplatePlan.NotFound, 0, $"Template \"{name}\" is not in this model - no template is applied.");
        if (t.Id == current)
            return (TemplatePlan.AlreadySet, t.Id, "");
        return (TemplatePlan.Apply, t.Id, "");
    }
}
