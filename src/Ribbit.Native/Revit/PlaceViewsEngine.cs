using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Ribbit.Geometry.Sheets;

namespace Ribbit.Native.Revit;

/// <summary>A view that can still go on a sheet (legends always can).</summary>
internal sealed record PlaceViewRow(long Id, string Type, string Name, bool IsLegend);

/// <summary>A sheet, with the names of the views already on it.</summary>
internal sealed record PlaceSheetRow(long Id, string Number, string Name, string OnIt)
{
    public string Label => $"{Number}  -  {Name}";
}

/// <summary>Where each viewport's centre goes: the centre of the sheet's largest title block, or a fixed point.</summary>
internal sealed record Placement(bool Auto, double XMm, double YMm);

/// <summary>What happened to one view.</summary>
internal sealed record PlaceRowResult(long ViewId, long SheetId, string View, string Sheet, bool Placed, string Why);

/// <summary>The whole run: per view, plus the change guard's verdict.</summary>
internal sealed class PlaceRunResult
{
    public List<PlaceRowResult> Rows { get; } = [];

    /// <summary>Non-empty: the guard found a change outside the run's scope and rolled EVERYTHING back.</summary>
    public List<string> GuardViolations { get; } = [];

    /// <summary>Revit's internal view/sheet records the run touched (allowed, listed for the record).</summary>
    public List<string> InternalRecords { get; } = [];

    /// <summary>Set when the run could not go ahead at all.</summary>
    public string? Error { get; set; }

    public int Placed => GuardViolations.Count == 0 && Error is null ? Rows.Count(r => r.Placed) : 0;
}

/// <summary>
/// Place Views on Sheets - the Revit half (was "Placing Views to Sheets v3.dyn",
/// nodes 1 and 3).
///
/// Only views Revit will accept in a viewport are listed: templates, sheets,
/// browsers, schedules and reports are dropped, and so is any view already on a
/// sheet - a view can sit on one sheet only. Legends are the exception.
///
/// THE CHANGE GUARD (the graph's, kept): the run may ADD only the viewports it
/// creates, may DELETE nothing, and may MODIFY only what placing a view
/// legitimately touches - the new viewports, the picked views and sheets, any
/// other non-template view (Revit rewrites "Sheet Number" / "Referencing Sheet"
/// on views that point at a placed one), section / elevation / callout markers,
/// anything owned by a picked view or sheet, and Revit's bare internal records
/// behind views and sheets. Checked twice: the document is listed again before
/// the commit, and Revit's own DocumentChanged report of the commit is read
/// inside a TransactionGroup. Either finding something else rolls the whole run
/// back, and both fail CLOSED.
/// </summary>
internal static class PlaceViewsEngine
{
    private static readonly HashSet<ViewType> NeverInViewport =
    [
        ViewType.DrawingSheet, ViewType.ProjectBrowser, ViewType.SystemBrowser, ViewType.Internal,
        ViewType.Undefined, ViewType.Schedule, ViewType.ColumnSchedule, ViewType.PanelSchedule,
        ViewType.Report, ViewType.CostReport, ViewType.LoadsReport, ViewType.PresureLossReport,
    ];

    private static readonly HashSet<long> ReferenceCategories =
    [
        .. new[]
        {
            BuiltInCategory.OST_Viewers, BuiltInCategory.OST_Elev, BuiltInCategory.OST_ElevationMarks,
            BuiltInCategory.OST_CalloutHeads, BuiltInCategory.OST_SectionHeads, BuiltInCategory.OST_ReferenceViewer,
            BuiltInCategory.OST_Callouts, BuiltInCategory.OST_Sections,
        }.Select(b => (long)b),
    ];

    /// <summary>Tests only: runs after the viewports are made and before the guard looks (to plant a stray change).</summary>
    internal static Action<Document>? AfterCreateHook { get; set; }

    public static string PrettyType(ViewType t) => Ribbit.Geometry.Views.TemplateLabels.PrettyType(t.ToString());

    /// <summary>(views that can be placed, sheets, how many views are already on a sheet).</summary>
    public static (List<PlaceViewRow> Views, List<PlaceSheetRow> Sheets, int Placed) Collect(Document doc)
    {
        HashSet<long> placed = [];
        Dictionary<long, List<string>> onSheet = [];
        foreach (Viewport vp in new FilteredElementCollector(doc).OfClass(typeof(Viewport)).Cast<Viewport>())
        {
            placed.Add(vp.ViewId.Value);
            if (!onSheet.TryGetValue(vp.SheetId.Value, out List<string>? names))
                onSheet[vp.SheetId.Value] = names = [];
            names.Add(doc.GetElement(vp.ViewId)?.Name ?? "");
        }

        List<PlaceViewRow> views = [];
        foreach (View v in new FilteredElementCollector(doc).OfClass(typeof(View)).WhereElementIsNotElementType().Cast<View>())
        {
            if (v.IsTemplate || NeverInViewport.Contains(v.ViewType) || v is ViewSheet)
                continue;
            bool legend = v.ViewType == ViewType.Legend;
            if (!legend && placed.Contains(v.Id.Value))
                continue;
            views.Add(new PlaceViewRow(v.Id.Value, PrettyType(v.ViewType), v.Name, legend));
        }
        views.Sort((a, b) =>
        {
            int c = string.Compare(a.Type, b.Type, StringComparison.OrdinalIgnoreCase);
            return c != 0 ? c : NaturalSort.Comparer.Compare(a.Name, b.Name);
        });

        List<PlaceSheetRow> sheets = [];
        foreach (ViewSheet s in new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>())
        {
            if (s.IsPlaceholder)
                continue;
            List<string> names = onSheet.TryGetValue(s.Id.Value, out List<string>? n) ? n : [];
            names.Sort(NaturalSort.Comparer);
            sheets.Add(new PlaceSheetRow(s.Id.Value, s.SheetNumber, s.Name, string.Join(";  ", names)));
        }
        sheets.Sort((a, b) => NaturalSort.Comparer.Compare(a.Number, b.Number));
        return (views, sheets, placed.Count);
    }

    /// <summary>Centre of the LARGEST title block on the sheet (a revision block beside it is ignored), in sheet feet.</summary>
    public static XYZ? TitleBlockCentre(Document doc, ViewSheet sheet)
    {
        (double Area, XYZ Centre)? best = null;
        foreach (Element tb in new FilteredElementCollector(doc, sheet.Id).OfCategory(BuiltInCategory.OST_TitleBlocks).WhereElementIsNotElementType())
        {
            BoundingBoxXYZ? bb = tb.get_BoundingBox(sheet);
            if (bb is null)
                continue;
            double area = (bb.Max.X - bb.Min.X) * (bb.Max.Y - bb.Min.Y);
            if (best is null || area > best.Value.Area)
                best = (area, new XYZ((bb.Min.X + bb.Max.X) / 2.0, (bb.Min.Y + bb.Max.Y) / 2.0, 0.0));
        }
        return best?.Centre;
    }

    private static XYZ PointFor(Document doc, ViewSheet sheet, Placement p, out string note)
    {
        note = "";
        if (p.Auto)
        {
            if (TitleBlockCentre(doc, sheet) is XYZ c)
                return c;
            note = "no title block on the sheet - placed at the X / Y point instead";
        }
        return new XYZ(p.XMm / 304.8, p.YMm / 304.8, 0.0);
    }

    /// <summary>Every element id in the document, types included.</summary>
    private static HashSet<long> Snapshot(Document doc)
    {
        HashSet<long> ids = [];
        foreach (ElementId id in new FilteredElementCollector(doc).WhereElementIsNotElementType().ToElementIds())
            ids.Add(id.Value);
        foreach (ElementId id in new FilteredElementCollector(doc).WhereElementIsElementType().ToElementIds())
            ids.Add(id.Value);
        return ids;
    }

    internal static string DescribeElement(Document doc, long id)
    {
        Element? e = doc.GetElement(new ElementId(id));
        if (e is null)
            return $"id {id}  (no longer in the model to describe)";
        string name;
        try { name = e.Name ?? ""; } catch (Exception) { name = ""; }
        return $"{e.Category?.Name ?? "-"}  |  {e.GetType().Name}  |  {(name.Length > 0 ? name : "<unnamed>")}  |  id {id}";
    }

    /// <summary>Place the pairs (view id, sheet id). Everything is looked up again by id at the moment of writing.</summary>
    public static PlaceRunResult Place(Document doc, IReadOnlyList<(long View, long Sheet)> pairs, Placement placement)
    {
        PlaceRunResult result = new();
        HashSet<long> placedNow = [.. new FilteredElementCollector(doc).OfClass(typeof(Viewport)).Cast<Viewport>().Select(v => v.ViewId.Value)];

        // ---- re-resolve, and skip what can no longer be placed ----
        List<(View View, ViewSheet Sheet)> todo = [];
        HashSet<long> doneViews = [];
        foreach ((long viewId, long sheetId) in pairs)
        {
            View? view = doc.GetElement(new ElementId(viewId)) as View;
            ViewSheet? sheet = doc.GetElement(new ElementId(sheetId)) as ViewSheet;
            string vName = view?.Name ?? $"view {viewId}", sName = sheet is null ? $"sheet {sheetId}" : $"{sheet.SheetNumber} - {sheet.Name}";
            string? why = view is null ? "the view is no longer in the model"
                : sheet is null ? "the sheet is no longer in the model"
                : view.ViewType != ViewType.Legend && (placedNow.Contains(viewId) || !doneViews.Add(viewId)) ? "the view is already on a sheet (placed since the list was made?)"
                : !Viewport.CanAddViewToSheet(doc, sheet.Id, view.Id) ? "Revit will not put this view on this sheet"
                : null;
            if (why is not null)
                result.Rows.Add(new PlaceRowResult(viewId, sheetId, vName, sName, false, why));
            else
                todo.Add((view!, sheet!));
        }
        if (todo.Count == 0)
            return result;

        HashSet<long> pickedViews = [.. todo.Select(t => t.View.Id.Value)];
        HashSet<long> pickedSheets = [.. todo.Select(t => t.Sheet.Id.Value)];
        HashSet<long> created = [];

        using TransactionGroup group = new(doc, "Place Views on Sheets");
        group.Start();
        HashSet<long> before = Snapshot(doc);

        List<PlaceRowResult> rows = [];
        using (Transaction t = new(doc, "Place Views on Sheets"))
        {
            ErrorCollector errors = ErrorCollector.Start(t);
            foreach ((View view, ViewSheet sheet) in todo)
            {
                string sLabel = $"{sheet.SheetNumber} - {sheet.Name}";
                XYZ point = PointFor(doc, sheet, placement, out string note);
                Viewport? vp = null;
                string why = note;
                try
                {
                    vp = Viewport.Create(doc, sheet.Id, view.Id, point);
                    if (vp is null)
                        why = "Revit declined to create the viewport and gave no reason";
                }
                catch (Exception ex)
                {
                    why = "Revit refused: " + ex.Message;
                }
                if (vp is not null)
                    created.Add(vp.Id.Value);
                rows.Add(new PlaceRowResult(view.Id.Value, sheet.Id.Value, view.Name, sLabel, vp is not null, why));
            }

            AfterCreateHook?.Invoke(doc);

            if (created.Count == 0)
            {
                t.RollBack();
                group.RollBack();
                result.Rows.AddRange(rows);
                return result;
            }

            // ---- check 1: the document listed again, before the commit ----
            doc.Regenerate();
            HashSet<long> after = Snapshot(doc);
            foreach (long id in before.Where(id => !after.Contains(id)).OrderBy(i => i))
                result.GuardViolations.Add($"deleted   id {id}");
            foreach (long id in after.Where(id => !before.Contains(id) && !created.Contains(id)).OrderBy(i => i))
                result.GuardViolations.Add("added     " + DescribeElement(doc, id));
            if (result.GuardViolations.Count > 0)
            {
                t.RollBack();
                group.RollBack();
                result.GuardViolations.Insert(0, "Before the commit:");
                Describe(doc, result);
                result.Rows.AddRange(rows.Select(r => r with { Placed = false, Why = r.Placed ? "rolled back by the change guard" : r.Why }));
                return result;
            }

            // ---- check 2: Revit's own account of the commit ----
            HashSet<long> added = [], modified = [], deleted = [];
            bool seen = false;
            void OnChanged(object? sender, DocumentChangedEventArgs e)
            {
                if (!e.GetDocument().Equals(doc))
                    return;
                seen = true;
                foreach (ElementId id in e.GetAddedElementIds()) added.Add(id.Value);
                foreach (ElementId id in e.GetModifiedElementIds()) modified.Add(id.Value);
                foreach (ElementId id in e.GetDeletedElementIds()) deleted.Add(id.Value);
            }

            doc.Application.DocumentChanged += OnChanged;
            TransactionStatus status;
            try
            {
                status = t.Commit();
            }
            finally
            {
                doc.Application.DocumentChanged -= OnChanged;
            }

            if (status != TransactionStatus.Committed)
            {
                group.RollBack();
                result.Error = "Revit rolled the placements back: " + string.Join("; ", errors.Errors.DefaultIfEmpty("no reason was given"));
                result.Rows.AddRange(rows.Select(r => r with { Placed = false, Why = r.Placed ? "rolled back by Revit" : r.Why }));
                return result;
            }

            if (!seen)
                result.GuardViolations.Add("Revit sent no change report for the commit, so nothing could be checked - rolled back (fail closed).");

            HashSet<string>? names = null;
            HashSet<string> Names() => names ??= AllNames(doc);
            HashSet<long> owners = [.. pickedViews, .. pickedSheets];

            foreach (long id in deleted.OrderBy(i => i))
                result.GuardViolations.Add($"deleted   id {id}");
            foreach (long id in added.Where(i => !created.Contains(i)).OrderBy(i => i))
                result.GuardViolations.Add("added     " + DescribeElement(doc, id));
            foreach (long id in modified.OrderBy(i => i))
            {
                if (created.Contains(id) || owners.Contains(id))
                    continue;
                Element? e = doc.GetElement(new ElementId(id));
                if (e is null)
                {
                    result.GuardViolations.Add($"modified  id {id} (gone)");
                    continue;
                }
                if (e.Category is null && e.GetType() == typeof(Element))
                {
                    string n;
                    try { n = e.Name ?? ""; } catch (Exception) { n = ""; }
                    if (n.Length == 0 || Names().Contains(n))
                    {
                        result.InternalRecords.Add(DescribeElement(doc, id));
                        continue;
                    }
                }
                if (e is View v && !v.IsTemplate)
                    continue;
                if (e.Category is not null && ReferenceCategories.Contains(e.Category.Id.Value))
                    continue;
                if (owners.Contains(e.OwnerViewId.Value))
                    continue;
                result.GuardViolations.Add("modified  " + DescribeElement(doc, id));
            }

            if (result.GuardViolations.Count > 0)
            {
                group.RollBack();
                result.GuardViolations.Insert(0, "Revit's report of the commit:");
                result.Rows.AddRange(rows.Select(r => r with { Placed = false, Why = r.Placed ? "rolled back by the change guard" : r.Why }));
                return result;
            }
        }

        group.Assimilate();
        result.Rows.AddRange(rows);
        return result;
    }

    /// <summary>Every view name, and "number - name" for every sheet: what Revit names its internal records after.</summary>
    private static HashSet<string> AllNames(Document doc)
    {
        HashSet<string> names = new(StringComparer.Ordinal);
        foreach (View v in new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>())
        {
            if (v.IsTemplate)
                continue;
            names.Add(v.Name);
            if (v is ViewSheet s)
                names.Add($"{s.SheetNumber} - {s.Name}");
        }
        return names;
    }

    /// <summary>After a rollback a deleted element is back, so it can be named.</summary>
    private static void Describe(Document doc, PlaceRunResult r)
    {
        for (int i = 0; i < r.GuardViolations.Count; i++)
        {
            string line = r.GuardViolations[i];
            if (line.StartsWith("deleted   id ", StringComparison.Ordinal) && long.TryParse(line.Substring(13), out long id))
                r.GuardViolations[i] = "deleted   " + DescribeElement(doc, id);
        }
    }
}
