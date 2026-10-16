using System.Globalization;
using System.IO;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;
using Ribbit.Geometry.Views;

namespace Ribbit.Native.Revit;

/// <summary>What to copy besides the filter itself.</summary>
internal sealed record FilterCopyOptions(bool Lines, bool Patterns, bool Transparency, bool BringMissing, bool SyncRules);

/// <summary>The outcome of one copy run.</summary>
internal sealed class FilterCopyResult
{
    /// <summary>"template: filter" written (with "(without ...)" when a pattern was missing).</summary>
    public List<string> Written { get; } = [];

    /// <summary>"template: filter - why".</summary>
    public List<string> Skipped { get; } = [];

    /// <summary>Filters that exist here under the same name with different categories or rules.</summary>
    public List<string> Differences { get; } = [];

    public List<string> Log { get; } = [];

    public bool Committed { get; set; }
}

/// <summary>The source model, opened for the run if it was not open already.</summary>
internal sealed class FilterSource : IDisposable
{
    public Document Document { get; }
    public bool OpenedHere { get; }
    public string How { get; }

    public FilterSource(Document doc, bool openedHere, string how)
    {
        Document = doc;
        OpenedHere = openedHere;
        How = how;
    }

    /// <summary>Close it again if this run opened it - nothing outlives the run holding a source element.</summary>
    public void Dispose()
    {
        if (!OpenedHere)
            return;
        try
        {
            if (Document.IsValidObject)
                Document.Close(false);
        }
        catch (Exception)
        {
            // Left open: the user can close it from Revit's window list.
        }
    }
}

/// <summary>
/// Copy View Filters - the Revit half. One engine for both graphs it replaces:
/// "Filter v2.dyn" (between two templates of this model) and
/// "Filter_Revit_to_Revit_v2.dyn" (from another .rvt).
///
/// A filter's GRAPHICS and its DEFINITION are two different things. Everything
/// is resolved BY NAME in the target model - source ElementIds only mean the
/// same element while two files share an id space. Filters and patterns the
/// target has never seen are copied across; a filter that already exists under
/// the same name keeps its own categories and rules (Revit's copy offers no
/// "overwrite"), and every such mismatch is reported. Rewriting them is a
/// separate, model-wide choice, off unless asked for.
///
/// The template's V/G "Filters" row is TICKED so the filters reach its views
/// (the version before the graphs unticked it on every run).
/// </summary>
internal static class FilterCopyEngine
{
    /// <summary>Every view template in a document, sorted and labelled for the pickers.</summary>
    public static List<TemplateEntry> Templates(Document doc)
        => TemplateLabels.Build(new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
            .Where(v => v.IsTemplate)
            .Select(v => new TemplateEntry(v.Id.Value, v.ViewType.ToString(), v.Name)));

    private static string Norm(string path)
    {
        try { return Path.GetFullPath(path).TrimEnd('\\').ToLowerInvariant(); }
        catch (Exception) { return path.Trim().ToLowerInvariant(); }
    }

    /// <summary>
    /// The source model. Already open: used as it is. Workshared: opened
    /// DETACHED with every user workset closed - the central is never touched,
    /// no lock is taken, and no geometry loads (templates and filters live in
    /// the always-open worksets). A workshared file that fails to detach is
    /// reported, never retried as a plain open of the central.
    /// </summary>
    public static FilterSource OpenSource(Application app, string path)
    {
        string target = Norm(path);
        foreach (Document d in app.Documents)
        {
            if (!d.IsLinked && d.PathName.Length > 0 && Norm(d.PathName) == target)
                return new FilterSource(d, false, "already open");
        }

        if (!File.Exists(path))
            throw new FileNotFoundException("File not found: " + path);

        bool? shared;
        try { shared = BasicFileInfo.Extract(path).IsWorkshared; }
        catch (Exception) { shared = null; }

        ModelPath model = ModelPathUtils.ConvertUserVisiblePathToModelPath(path);
        if (shared != false)
        {
            try
            {
                OpenOptions o = new() { DetachFromCentralOption = DetachFromCentralOption.DetachAndPreserveWorksets };
                o.SetOpenWorksetsConfiguration(new WorksetConfiguration(WorksetConfigurationOption.CloseAllWorksets));
                return new FilterSource(app.OpenDocumentFile(model, o), true, "opened detached");
            }
            catch (Exception) when (shared is null)
            {
                // Header unreadable and it would not detach: not workshared, try a plain open.
            }
        }
        return new FilterSource(app.OpenDocumentFile(model, new OpenOptions()), true, "opened");
    }

    // ------------------------------------------------------------------ reading a template

    private static Rgb? RgbOf(Color? c) => c is not null && c.IsValid ? new Rgb(c.Red, c.Green, c.Blue) : null;

    private static LinePatternRef? LinePattern(Document doc, ElementId id)
    {
        if (id == ElementId.InvalidElementId)
            return null;
        if (id == LinePatternElement.GetSolidPatternId())
            return new LinePatternRef(LinePatternRef.SolidName, null);
        return doc.GetElement(id) is Element e ? new LinePatternRef(e.Name, e.UniqueId) : null;
    }

    private static FillPatternRef? FillPattern(Document doc, ElementId id)
    {
        if (id == ElementId.InvalidElementId || doc.GetElement(id) is not FillPatternElement e)
            return null;
        string target;
        try { target = e.GetFillPattern().Target.ToString(); }
        catch (Exception) { target = "Drafting"; }
        return new FillPatternRef(e.Name, target, e.UniqueId);
    }

    /// <summary>
    /// Every filter on the template, in its order - including one that is only
    /// switched off, or carries no override at all: those are worth transferring
    /// too, and the version before the graphs dropped them silently.
    /// </summary>
    public static List<FilterOverrideRow> Extract(View template)
    {
        Document doc = template.Document;
        List<FilterOverrideRow> rows = [];
        foreach (ElementId fid in template.GetOrderedFilters())
        {
            if (doc.GetElement(fid) is not ParameterFilterElement f)
                continue;
            OverrideGraphicSettings o = template.GetFilterOverrides(fid);
            static int? pen(int w) => w > 0 ? w : null;
            rows.Add(new FilterOverrideRow(
                f.Name, f.UniqueId,
                template.GetFilterVisibility(fid),
                template.GetIsFilterEnabled(fid),
                o.Halftone,
                Math.Max(0, Math.Min(100, o.Transparency)),
                new LineOverride(RgbOf(o.ProjectionLineColor), LinePattern(doc, o.ProjectionLinePatternId), pen(o.ProjectionLineWeight)),
                new LineOverride(RgbOf(o.CutLineColor), LinePattern(doc, o.CutLinePatternId), pen(o.CutLineWeight)),
                new PatternOverride(FillPattern(doc, o.SurfaceForegroundPatternId), RgbOf(o.SurfaceForegroundPatternColor), o.IsSurfaceForegroundPatternVisible),
                new PatternOverride(FillPattern(doc, o.SurfaceBackgroundPatternId), RgbOf(o.SurfaceBackgroundPatternColor), o.IsSurfaceBackgroundPatternVisible),
                new PatternOverride(FillPattern(doc, o.CutForegroundPatternId), RgbOf(o.CutForegroundPatternColor), o.IsCutForegroundPatternVisible),
                new PatternOverride(FillPattern(doc, o.CutBackgroundPatternId), RgbOf(o.CutBackgroundPatternColor), o.IsCutBackgroundPatternVisible),
                o.DetailLevel == ViewDetailLevel.Undefined ? null : o.DetailLevel.ToString()));
        }
        return rows;
    }

    // ------------------------------------------------------------------ lookups by name

    private static Dictionary<string, ParameterFilterElement> HostFilters(Document doc)
    {
        Dictionary<string, ParameterFilterElement> d = new(StringComparer.Ordinal);
        foreach (ParameterFilterElement f in new FilteredElementCollector(doc).OfClass(typeof(ParameterFilterElement)).Cast<ParameterFilterElement>())
            d.TryAdd(f.Name, f);
        return d;
    }

    private static Dictionary<string, LinePatternElement> HostLinePatterns(Document doc)
    {
        Dictionary<string, LinePatternElement> d = new(StringComparer.Ordinal);
        foreach (LinePatternElement p in new FilteredElementCollector(doc).OfClass(typeof(LinePatternElement)).Cast<LinePatternElement>())
            d.TryAdd(p.Name, p);
        return d;
    }

    /// <summary>Keyed by (name, target): a drafting and a model pattern may share a name.</summary>
    private static Dictionary<(string, string), FillPatternElement> HostFillPatterns(Document doc)
    {
        Dictionary<(string, string), FillPatternElement> d = [];
        foreach (FillPatternElement p in new FilteredElementCollector(doc).OfClass(typeof(FillPatternElement)).Cast<FillPatternElement>())
        {
            string target;
            try { target = p.GetFillPattern().Target.ToString(); }
            catch (Exception) { continue; }
            d.TryAdd((p.Name, target), p);
        }
        return d;
    }

    private static ElementId? ResolveFill(FillPatternRef? spec, Dictionary<(string, string), FillPatternElement> patterns)
    {
        if (spec is null)
            return null;
        if (patterns.TryGetValue((spec.Name, spec.Target), out FillPatternElement? hit))
            return hit.Id;
        // the target may differ between models; fall back to the name alone
        return patterns.FirstOrDefault(kv => kv.Key.Item1 == spec.Name).Value?.Id;
    }

    // ------------------------------------------------------------------ a filter's own definition

    private static string ParameterKey(Document doc, ElementId id)
    {
        long v = id.Value;
        if (v < 0)
        {
            try { return LabelUtils.GetLabelFor((BuiltInParameter)(int)v); }
            catch (Exception) { return $"builtin {v}"; }
        }
        return doc.GetElement(id)?.Name ?? $"parameter {v}";
    }

    private static string RuleKey(Document doc, FilterRule rule)
    {
        if (rule is FilterInverseRule inv)
            return $"not({RuleKey(doc, inv.GetInnerRule())})";
        string parameter;
        try { parameter = ParameterKey(doc, rule.GetRuleParameter()); }
        catch (Exception) { parameter = "?"; }
        (string evaluator, string value) = rule switch
        {
            FilterStringRule s => (s.GetEvaluator().GetType().Name, s.RuleString),
            FilterDoubleRule d => (d.GetEvaluator().GetType().Name, d.RuleValue.ToString("R", CultureInfo.InvariantCulture)),
            FilterIntegerRule i => (i.GetEvaluator().GetType().Name, i.RuleValue.ToString(CultureInfo.InvariantCulture)),
            FilterElementIdRule e => (e.GetEvaluator().GetType().Name, doc.GetElement(e.RuleValue)?.Name ?? e.RuleValue.Value.ToString(CultureInfo.InvariantCulture)),
            _ => (rule.GetType().Name, ""),
        };
        return $"{parameter} {evaluator} {value}".Trim();
    }

    /// <summary>
    /// Every leaf rule as text, flat and sorted: Revit may re-bundle rules into
    /// a different tree than the one set, so comparing tree shapes would report
    /// differences that do not exist.
    /// </summary>
    private static void RuleKeys(Document doc, ElementFilter? filter, List<string> output)
    {
        switch (filter)
        {
            case null:
                return;
            case ElementLogicalFilter logical:
                foreach (ElementFilter inner in logical.GetFilters())
                    RuleKeys(doc, inner, output);
                return;
            case ElementParameterFilter p:
                foreach (FilterRule r in p.GetRules())
                    output.Add(RuleKey(doc, r));
                return;
            default:
                output.Add(filter.GetType().Name);
                return;
        }
    }

    /// <summary>(category ids, rule text) - comparable across documents (built-in category ids match everywhere).</summary>
    public static (List<long> Categories, List<string> Rules) Definition(Document doc, ParameterFilterElement f)
    {
        List<string> rules = [];
        RuleKeys(doc, f.GetElementFilter(), rules);
        rules.Sort(StringComparer.Ordinal);
        return ([.. f.GetCategories().Select(c => c.Value).OrderBy(c => c)], rules);
    }

    private static string CategoryNames(Document doc, ParameterFilterElement f)
        => string.Join(", ", f.GetCategories().Select(c => Category.GetCategory(doc, c)?.Name ?? c.Value.ToString()).OrderBy(n => n));

    /// <summary>What each source filter is in the target model, for the window: missing, the same, or defined differently.</summary>
    public static Dictionary<string, string> TargetStatus(Document target, Document source, IEnumerable<FilterOverrideRow> rows)
    {
        Dictionary<string, ParameterFilterElement> host = HostFilters(target);
        Dictionary<string, string> status = new(StringComparer.Ordinal);
        foreach (FilterOverrideRow r in rows)
        {
            if (!host.TryGetValue(r.Name, out ParameterFilterElement? h))
            {
                status[r.Name] = "not in this model";
                continue;
            }
            if (source.GetElement(r.SourceUniqueId) is not ParameterFilterElement s || (s.Document.Equals(target) && s.Id == h.Id))
            {
                status[r.Name] = "in this model";
                continue;
            }
            (List<long> hc, List<string> hr) = Definition(target, h);
            (List<long> sc, List<string> sr) = Definition(source, s);
            status[r.Name] = hc.SequenceEqual(sc) && hr.SequenceEqual(sr) ? "in this model, same rules" : "in this model, DIFFERENT rules";
        }
        return status;
    }

    // ------------------------------------------------------------------ writing

    private sealed class UseDestination : IDuplicateTypeNamesHandler
    {
        public DuplicateTypeAction OnDuplicateTypeNamesFound(DuplicateTypeNamesHandlerArgs args) => DuplicateTypeAction.UseDestinationTypes;
    }

    private static CopyPasteOptions Quiet()
    {
        CopyPasteOptions o = new();
        o.SetDuplicateTypeNamesHandler(new UseDestination());
        return o;
    }

    /// <summary>
    /// Copy the chosen filters onto every target template. ONE undo step: the
    /// three passes (bring across, optionally rewrite definitions, write the
    /// overrides) run in a TransactionGroup that is assimilated at the end.
    /// </summary>
    public static FilterCopyResult Apply(Document doc, IReadOnlyList<View> targets, Document source, long sourceTemplateId,
        IReadOnlyList<FilterOverrideRow> chosen, FilterCopyOptions opt)
    {
        FilterCopyResult res = new();
        List<View> realTargets = [.. targets.Where(t =>
        {
            bool same = source.Equals(doc) && t.Id.Value == sourceTemplateId;
            if (same)
                res.Log.Add($"\"{t.Name}\" is the source template itself - left out.");
            return !same && t.IsTemplate;
        })];
        if (realTargets.Count == 0 || chosen.Count == 0)
        {
            res.Log.Add(chosen.Count == 0 ? "No filters were ticked." : "No target template to write to.");
            return res;
        }

        using TransactionGroup group = new(doc, "Copy View Filters");
        group.Start();
        Dictionary<string, ParameterFilterElement> filters = HostFilters(doc);
        Dictionary<string, LinePatternElement> lines = HostLinePatterns(doc);
        Dictionary<(string, string), FillPatternElement> fills = HostFillPatterns(doc);

        // ---- pass 1: bring across what this model is missing ----
        if (opt.BringMissing && !source.Equals(doc))
        {
            HashSet<ElementId> need = [];
            void Need(string? uid)
            {
                if (uid is not null && source.GetElement(uid) is Element e)
                    need.Add(e.Id);
            }
            foreach (FilterOverrideRow r in chosen)
            {
                if (!filters.ContainsKey(r.Name))
                    Need(r.SourceUniqueId);
                if (opt.Lines)
                    foreach (LinePatternRef p in r.LinePatterns().Where(p => !lines.ContainsKey(p.Name)))
                        Need(p.UniqueId);
                if (opt.Patterns)
                    foreach (FillPatternRef p in r.FillPatterns().Where(p => !fills.ContainsKey((p.Name, p.Target))))
                        Need(p.UniqueId);
            }
            if (need.Count > 0)
            {
                using Transaction t = new(doc, "Copy View Filters - bring across");
                ErrorCollector errors = ErrorCollector.Start(t);
                int copied = 0;
                try
                {
                    copied = ElementTransformUtils.CopyElements(source, need, doc, Transform.Identity, Quiet()).Count;
                }
                catch (Exception ex)
                {
                    res.Log.Add("Nothing could be copied across: " + ex.Message);
                }
                // Only claim a copy that happened: a throw above leaves the transaction open with nothing in it.
                if (copied == 0)
                    t.RollBack();
                else if (t.Commit() == TransactionStatus.Committed)
                    res.Log.Add($"Copied {need.Count} filter(s)/pattern(s) into this model.");
                else if (errors.Errors.Count > 0)
                    res.Log.Add("Revit refused to copy them in: " + string.Join("; ", errors.Errors));
                filters = HostFilters(doc);
                lines = HostLinePatterns(doc);
                fills = HostFillPatterns(doc);
            }
        }

        // ---- pass 2: compare each filter's own definition, and optionally rewrite it ----
        List<(ParameterFilterElement Host, ParameterFilterElement Src, string Name)> differ = [];
        foreach (FilterOverrideRow r in chosen)
        {
            if (!filters.TryGetValue(r.Name, out ParameterFilterElement? h) || source.GetElement(r.SourceUniqueId) is not ParameterFilterElement s)
                continue;
            if (s.Document.Equals(doc) && s.Id == h.Id)
                continue;
            (List<long> hc, List<string> hr) = Definition(doc, h);
            (List<long> sc, List<string> sr) = Definition(source, s);
            List<string> parts = [];
            if (!hc.SequenceEqual(sc))
                parts.Add($"categories (source: {(CategoryNames(source, s) is { Length: > 0 } names ? names : "none")})");
            if (!hr.SequenceEqual(sr))
                parts.Add($"rules (source: {(sr.Count > 0 ? string.Join("; ", sr) : "no rules")})");
            if (parts.Count > 0)
            {
                res.Differences.Add($"{r.Name}  -  {string.Join(" and ", parts)}");
                differ.Add((h, s, r.Name));
            }
        }
        if (differ.Count > 0 && opt.SyncRules)
        {
            using Transaction t = new(doc, "Copy View Filters - rewrite filter rules");
            ErrorCollector errors = ErrorCollector.Start(t);
            foreach ((ParameterFilterElement h, ParameterFilterElement s, string name) in differ)
                res.Log.Add(SyncDefinition(doc, h, s, name));
            if (t.Commit() != TransactionStatus.Committed)
                res.Log.Add("Revit rolled the rule rewrite back: " + string.Join("; ", errors.Errors.DefaultIfEmpty("no reason given")));
        }
        else if (differ.Count > 0)
        {
            res.Log.Add($"{differ.Count} filter(s) here are defined differently from the source. Tick \"Overwrite this model's filter rules\" to bring their definitions across too.");
        }

        // ---- pass 3: write the overrides onto every target ----
        using (Transaction t = new(doc, "Copy View Filters"))
        {
            ErrorCollector errors = ErrorCollector.Start(t);
            ElementId solid = LinePatternElement.GetSolidPatternId();
            foreach (View target in realTargets)
            {
                TickFiltersRow(target, res);
                HashSet<long> onView = [.. target.GetFilters().Select(f => f.Value)];
                foreach (FilterOverrideRow r in chosen)
                {
                    string who = realTargets.Count > 1 ? $"{target.Name}: {r.Name}" : r.Name;
                    if (!filters.TryGetValue(r.Name, out ParameterFilterElement? h))
                    {
                        res.Skipped.Add($"{who}  -  this model has no filter by that name{(opt.BringMissing ? "" : " (bringing missing filters across is off)")}");
                        continue;
                    }
                    try
                    {
                        if (!onView.Contains(h.Id.Value))
                        {
                            target.AddFilter(h.Id);
                            onView.Add(h.Id.Value);
                        }
                        target.SetFilterVisibility(h.Id, r.Visible);
                        target.SetIsFilterEnabled(h.Id, r.Enabled);

                        SortedSet<string> missing = new(StringComparer.Ordinal);
                        OverrideGraphicSettings o = new(target.GetFilterOverrides(h.Id));
                        if (opt.Lines)
                        {
                            ApplyLine(r.ProjectionLine, o.SetProjectionLineColor, o.SetProjectionLinePatternId, o.SetProjectionLineWeight, lines, solid, missing);
                            ApplyLine(r.CutLine, o.SetCutLineColor, o.SetCutLinePatternId, o.SetCutLineWeight, lines, solid, missing);
                        }
                        if (opt.Patterns)
                        {
                            ApplyFill(r.SurfaceForeground, o.SetSurfaceForegroundPatternId, o.SetSurfaceForegroundPatternColor, o.SetSurfaceForegroundPatternVisible, fills, missing);
                            ApplyFill(r.SurfaceBackground, o.SetSurfaceBackgroundPatternId, o.SetSurfaceBackgroundPatternColor, o.SetSurfaceBackgroundPatternVisible, fills, missing);
                            ApplyFill(r.CutForeground, o.SetCutForegroundPatternId, o.SetCutForegroundPatternColor, o.SetCutForegroundPatternVisible, fills, missing);
                            ApplyFill(r.CutBackground, o.SetCutBackgroundPatternId, o.SetCutBackgroundPatternColor, o.SetCutBackgroundPatternVisible, fills, missing);
                        }
                        if (opt.Transparency)
                        {
                            o.SetSurfaceTransparency(r.Transparency);
                            o.SetHalftone(r.Halftone);
                            if (r.DetailLevel is not null && Enum.TryParse(r.DetailLevel, out ViewDetailLevel level))
                                o.SetDetailLevel(level);
                        }
                        target.SetFilterOverrides(h.Id, o);
                        res.Written.Add(missing.Count > 0 ? $"{who}  (without {string.Join(", ", missing)})" : who);
                        if (missing.Count > 0)
                            res.Log.Add($"{who}: {string.Join(", ", missing)} not found in this model");
                    }
                    catch (Exception ex)
                    {
                        res.Skipped.Add($"{who}  -  {ex.Message}");
                    }
                }
            }

            if (res.Written.Count == 0)
            {
                t.RollBack();
            }
            else if (t.Commit() != TransactionStatus.Committed)
            {
                res.Log.Add("Revit rolled the overrides back: " + string.Join("; ", errors.Errors.DefaultIfEmpty("no reason given")));
                res.Written.Clear();
            }
        }

        if (res.Written.Count == 0 && res.Log.All(l => !l.Contains("definition rewritten")))
        {
            group.RollBack();
            res.Log.Add("Nothing was written - the model is unchanged.");
            return res;
        }

        group.Assimilate();
        res.Committed = true;
        res.Log.Add($"{res.Written.Count} written, {res.Skipped.Count} skipped, on {realTargets.Count} template(s).");
        return res;
    }

    private static void ApplyLine(LineOverride spec, Func<Color, OverrideGraphicSettings> setColor, Func<ElementId, OverrideGraphicSettings> setPattern, Func<int, OverrideGraphicSettings> setWeight,
        Dictionary<string, LinePatternElement> lines, ElementId solid, ISet<string> missing)
    {
        setColor(spec.Color is { } c ? new Color(c.R, c.G, c.B) : Color.InvalidColorValue);
        ElementId? pid = spec.Pattern switch
        {
            null => null,
            { IsSolid: true } => solid,
            { } p => lines.TryGetValue(p.Name, out LinePatternElement? hit) ? hit.Id : null,
        };
        if (spec.Pattern is not null && pid is null)
            missing.Add($"line pattern \"{spec.Pattern.Name}\"");
        setPattern(pid ?? ElementId.InvalidElementId);
        setWeight(spec.Weight ?? OverrideGraphicSettings.InvalidPenNumber);
    }

    private static void ApplyFill(PatternOverride spec, Func<ElementId, OverrideGraphicSettings> setPattern, Func<Color, OverrideGraphicSettings> setColor, Func<bool, OverrideGraphicSettings> setVisible,
        Dictionary<(string, string), FillPatternElement> fills, ISet<string> missing)
    {
        ElementId? pid = ResolveFill(spec.Pattern, fills);
        if (spec.Pattern is not null && pid is null)
            missing.Add($"fill pattern \"{spec.Pattern.Name}\"");
        setPattern(pid ?? ElementId.InvalidElementId);
        setColor(spec.Color is { } c ? new Color(c.R, c.G, c.B) : Color.InvalidColorValue);
        setVisible(spec.Visible);
    }

    /// <summary>Tick V/G Overrides &gt; Filters on the template, so what is written here reaches its views.</summary>
    private static void TickFiltersRow(View template, FilterCopyResult res)
    {
        long filtersParam = (long)BuiltInParameter.VIS_GRAPHICS_FILTERS;
        List<ElementId> current = [.. template.GetNonControlledTemplateParameterIds()];
        List<ElementId> keep = [.. current.Where(id => id.Value != filtersParam)];
        if (keep.Count == current.Count)
            return;
        template.SetNonControlledTemplateParameterIds(keep);
        res.Log.Add($"\"{template.Name}\": ticked V/G Overrides > Filters so the template passes them on to its views.");
    }

    /// <summary>
    /// Rewrite this model's filter to the source's categories and rules. The
    /// source filter is copied in briefly so Revit remaps its rule parameters
    /// (the only reliable way to move rules between documents), read, and deleted.
    /// </summary>
    private static string SyncDefinition(Document doc, ParameterFilterElement host, ParameterFilterElement src, string name)
    {
        ICollection<ElementId> copied;
        try
        {
            copied = ElementTransformUtils.CopyElements(src.Document, [src.Id], doc, Transform.Identity, Quiet());
        }
        catch (Exception ex)
        {
            return $"{name}: definition left alone - the source rules could not be brought across ({ex.Message})";
        }
        ParameterFilterElement? temp = copied.Select(doc.GetElement).OfType<ParameterFilterElement>().FirstOrDefault();
        if (temp is null)
            return $"{name}: definition left alone - the source rules could not be brought across";
        if (temp.Id == host.Id)
            return $"{name}: definition left alone - Revit reused the existing filter instead of copying";

        string outcome;
        try
        {
            List<ElementId> cats = [.. temp.GetCategories()];
            ElementFilter? rules = temp.GetElementFilter();
            if (cats.Count > 0)
                host.SetCategories(cats);
            if (rules is null)
                host.ClearRules();
            else
                host.SetElementFilter(rules);
            outcome = $"{name}: definition rewritten to match the source.";
        }
        catch (Exception ex)
        {
            outcome = $"{name}: definition left alone - {ex.Message}";
        }
        try { doc.Delete(temp.Id); }
        catch (Exception ex) { outcome += $" (a temporary copy was left behind: {ex.Message})"; }
        return outcome;
    }
}
