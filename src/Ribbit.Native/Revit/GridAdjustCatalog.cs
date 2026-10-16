using System.ComponentModel;
using Autodesk.Revit.DB;
using Ribbit.Geometry.Sheets;

namespace Ribbit.Native.Revit;

/// <summary>A row the Adjust Grids lists can tick. Plain data - never an Element.</summary>
internal abstract class CheckRow : INotifyPropertyChanged
{
    private bool _isChecked;

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (_isChecked == value)
                return;
            _isChecked = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));
            CheckChanged?.Invoke();
        }
    }

    /// <summary>Raised on every tick change, however it was made.</summary>
    public Action? CheckChanged { get; set; }
}

internal sealed class GridViewRow : CheckRow
{
    public string Uid { get; init; } = "";
    public long Id { get; init; }
    public string Name { get; init; } = "";
    public string Type { get; init; } = "";

    /// <summary>Sheet number(s) the view is placed on, "" for none.</summary>
    public string Sheet { get; init; } = "";

    public bool CropOn { get; init; }
    public long? ScopeId { get; init; }
    public string ScopeName { get; init; } = "-";

    public string SheetText => Sheet.Length > 0 ? Sheet : "-";
    public string CropText => CropOn ? "on" : "OFF";
}

internal sealed class GridRow : CheckRow
{
    public string Uid { get; init; } = "";
    public long Id { get; init; }
    public string Name { get; init; } = "";
    public long? ScopeId { get; init; }
    public string ScopeName { get; init; } = "-";
}

internal sealed record ScopeBoxRow(long Id, string Name, string Uid);

/// <summary>
/// What the Adjust Grids window lists, read ONCE when it opens - the lists
/// are re-filtered on every keystroke and View.Name is an API call each time.
/// </summary>
internal static class GridAdjustCatalog
{
    /// <summary>Every view type except the ones that cannot carry a grid.</summary>
    private static readonly HashSet<ViewType> CannotHoldAGrid = BuildExcluded();

    private static HashSet<ViewType> BuildExcluded()
    {
        HashSet<ViewType> set = [];
        foreach (string name in new[]
                 {
                     "Undefined", "Schedule", "DrawingSheet", "ProjectBrowser", "Report", "Legend",
                     "SystemBrowser", "CostReport", "LoadsReport", "PresureLossReport",
                     "ColumnSchedule", "PanelSchedule", "Walkthrough", "Rendering", "Internal",
                 })
        {
            // by name, so a member this Revit version lacks is a miss, not an error
            if (Enum.TryParse(name, out ViewType t))
                set.Add(t);
        }

        return set;
    }

    public static string TypeLabel(ViewType t) => t switch
    {
        ViewType.FloorPlan => "Floor Plan",
        ViewType.CeilingPlan => "Ceiling Plan",
        ViewType.EngineeringPlan => "Structural Plan",
        ViewType.AreaPlan => "Area Plan",
        ViewType.DraftingView => "Drafting View",
        ViewType.ThreeD => "3D View",
        _ => t.ToString(),
    };

    public static List<ScopeBoxRow> ScopeBoxes(Document doc)
    {
        List<ScopeBoxRow> rows = [];
        try
        {
            foreach (Element sb in new FilteredElementCollector(doc)
                         .OfCategory(BuiltInCategory.OST_VolumeOfInterest)
                         .WhereElementIsNotElementType())
            {
                try { rows.Add(new ScopeBoxRow(RevitCompat.IdKey(sb.Id), sb.Name, sb.UniqueId)); }
                catch (Exception) { }
            }
        }
        catch (Exception) { }

        rows.Sort((a, b) => NaturalSort.Comparer.Compare(a.Name, b.Name));
        return rows;
    }

    public static List<GridViewRow> Views(Document doc, IReadOnlyDictionary<long, string> scopeNames)
    {
        Dictionary<long, List<string>> onSheet = [];
        Dictionary<long, string> numbers = [];
        foreach (Viewport vp in new FilteredElementCollector(doc).OfClass(typeof(Viewport)).Cast<Viewport>())
        {
            try
            {
                long key = RevitCompat.IdKey(vp.SheetId);
                if (!numbers.TryGetValue(key, out string? number))
                    numbers[key] = number = (doc.GetElement(vp.SheetId) as ViewSheet)?.SheetNumber ?? "";
                long viewKey = RevitCompat.IdKey(vp.ViewId);
                if (!onSheet.TryGetValue(viewKey, out List<string>? list))
                    onSheet[viewKey] = list = [];
                list.Add(number);
            }
            catch (Exception) { }
        }

        List<GridViewRow> rows = [];
        foreach (View v in new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>())
        {
            try
            {
                if (v.IsTemplate || CannotHoldAGrid.Contains(v.ViewType))
                    continue;

                long id = RevitCompat.IdKey(v.Id);
                string sheet = "";
                try
                {
                    if (onSheet.TryGetValue(id, out List<string>? s))
                        sheet = string.Join(",  ", s.OrderBy(x => x, NaturalSort.Comparer));
                }
                catch (Exception) { }

                bool crop = false;
                try { crop = v.CropBoxActive; } catch (Exception) { }

                long? scope = null;
                try { scope = ScopeOf(v, BuiltInParameter.VIEWER_VOLUME_OF_INTEREST_CROP); } catch (Exception) { }

                rows.Add(new GridViewRow
                {
                    Uid = v.UniqueId,
                    Id = id,
                    Name = v.Name,
                    Type = TypeLabel(v.ViewType),
                    Sheet = sheet,
                    CropOn = crop,
                    ScopeId = scope,
                    ScopeName = NameOf(scope, scopeNames),
                });
            }
            catch (Exception)
            {
                // a view that will not describe itself is not offered
            }
        }

        rows.Sort((a, b) => NaturalSort.Comparer.Compare(a.Name, b.Name));
        return rows;
    }

    public static List<GridRow> Grids(Document doc, IReadOnlyDictionary<long, string> scopeNames)
    {
        List<GridRow> rows = [];
        foreach (Grid g in new FilteredElementCollector(doc).OfClass(typeof(Grid)).Cast<Grid>())
        {
            try
            {
                long? scope = ScopeOf(g, BuiltInParameter.DATUM_VOLUME_OF_INTEREST);
                rows.Add(new GridRow
                {
                    Uid = g.UniqueId,
                    Id = RevitCompat.IdKey(g.Id),
                    Name = g.Name,
                    ScopeId = scope,
                    ScopeName = NameOf(scope, scopeNames),
                });
            }
            catch (Exception) { }
        }

        rows.Sort((a, b) => NaturalSort.Comparer.Compare(a.Name, b.Name));
        return rows;
    }

    /// <summary>The UniqueIds of the grids Revit draws in the view.</summary>
    public static HashSet<string> GridsDrawnIn(Document doc, View view)
        => [.. new FilteredElementCollector(doc, view.Id).OfClass(typeof(Grid)).Select(g => g.UniqueId)];

    private static string NameOf(long? scope, IReadOnlyDictionary<long, string> names)
        => scope is null ? "-" : names.TryGetValue(scope.Value, out string? n) ? n : scope.Value.ToString();

    /// <summary>
    /// The element's scope box id, or null. Three routes, each guarded on its
    /// own, so one failing does not take the others down.
    /// </summary>
    private static long? ScopeOf(Element e, BuiltInParameter builtin)
    {
        foreach (Func<Parameter?> read in new Func<Parameter?>[]
                 {
                     () => e.get_Parameter(builtin),
                     () => e.LookupParameter("Scope Box"),
                     () => e.Parameters.Cast<Parameter>().FirstOrDefault(p => p.Definition?.Name == "Scope Box"),
                 })
        {
            try
            {
                Parameter? p = read();
                if (p is null || p.StorageType != StorageType.ElementId)
                    continue;
                long v = RevitCompat.IdKey(p.AsElementId());
                if (v > 0)
                    return v;
            }
            catch (Exception) { }
        }

        return null;
    }
}
