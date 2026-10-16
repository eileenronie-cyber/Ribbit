using Autodesk.Revit.DB;
using Ribbit.Geometry.Grids;

namespace Ribbit.Native.Revit;

/// <summary>One view's entry in the "each view uses its own scope box" plan.</summary>
internal sealed record GridScopePlanEntry(
    string ScopeName, IReadOnlyList<string> GridUids, GridPreset? Settings);

/// <summary>Everything the Adjust Grids window hands the engine.</summary>
internal sealed class GridAdjustRequest
{
    public List<View> Views { get; init; } = [];

    /// <summary>The grids the run may touch. Empty means every grid in the model.</summary>
    public List<Grid> PickedGrids { get; init; } = [];

    /// <summary>The settings on screen when the window closed.</summary>
    public GridSteps Base { get; init; } = new();

    /// <summary>
    /// View id -> its own scope box's grids and settings, or null when "each
    /// view uses its own scope box" is off. A view missing from the plan uses
    /// <see cref="PickedGrids"/> and <see cref="Base"/>. Ignored when copying.
    /// </summary>
    public Dictionary<long, GridScopePlanEntry>? Plan { get; init; }

    /// <summary>
    /// When set, STEPS 1-4 are not run: every view copies this one's set-up,
    /// for EVERY grid both views draw - ticks and scope boxes do not narrow it.
    /// </summary>
    public View? CopySource { get; init; }

    /// <summary>Hide, per view, the grids Revit draws there that are not in its set.</summary>
    public bool HideOthers { get; init; }

    /// <summary>Unhide, per view, the grids of its set hidden there with Hide in View.</summary>
    public bool ShowSet { get; init; }

    /// <summary>
    /// Convert every grid of the set each view shows to 2D - crop box or not,
    /// hidden or not - where it is drawn now. Its own transaction, run last.
    /// </summary>
    public bool All2d { get; init; }

    /// <summary>STEP 2 stretches to the scope box (the view's own, else <see cref="ChosenScope"/>).</summary>
    public bool ToScope { get; init; }

    public Element? ChosenScope { get; init; }
}

internal sealed class GridAdjustResult
{
    public List<string> Adjusted { get; } = [];
    public List<string> Skipped { get; } = [];
    public List<string> Log { get; } = [];
    internal List<string> Reasons { get; } = [];

    /// <summary>
    /// Elbows a copy left in a target view because the source has none there.
    /// Revit's API cannot remove an elbow (measured: SetLeader(null) is
    /// refused, hiding and re-showing the bubble brings the same elbow back,
    /// and switching the end to 3D and back keeps it), so each is NAMED for
    /// the report - a thirty-second fix by hand, instead of a hunt.
    /// </summary>
    public List<ExtraElbow> ExtraElbows { get; } = [];
}

internal sealed record ExtraElbow(ElementId GridId, string GridName, ElementId ViewId, string ViewName, string End);

/// <summary>
/// Adjust Grids, ported from the V23 "Adjust grids to the crop box" Python
/// node of Grid_v23.dyn.
///
/// Nothing here writes a grid's model (3D) extent: every call that changes
/// anything takes a view, and the only extent type ever written is
/// ViewSpecific. That is not taken on trust - every grid's 3D line and
/// vertical extent is recorded before the run and compared after it, and the
/// whole run sits in one TransactionGroup: if anything moved in 3D, the group
/// is rolled back and the model is exactly as it was. Otherwise it is merged
/// into ONE step in Undo.
///
/// Every 2D write goes through <see cref="Write2d"/>: both ends to 2D and the
/// line, in a sub-transaction, or the grid left exactly as it was.
///
/// A grid is touched in a view only if a view-scoped collector says the view
/// draws it. Bubble sides are measured per grid off the MODEL curve in VIEW
/// coordinates, and rebuilt lines keep the datum's direction, so End0 cannot
/// change underneath.
/// </summary>
internal sealed class GridAdjustEngine
{
    private static readonly DatumEnds[] Ends = [DatumEnds.End0, DatumEnds.End1];
    private const double Mm = 304.8;
    private const double LeaderTol = 1e-3;

    private readonly Document _doc;
    private readonly GridAdjustRequest _req;
    private readonly GridAdjustResult _r = new();

    private readonly HashSet<long> _leftAlone = [];
    private readonly Dictionary<long, (Grid Grid, Line Line)?> _lineCache = [];
    private readonly Dictionary<long, HashSet<long>?> _keepFor = [];
    private readonly Dictionary<long, Crop> _cropCache = [];
    private readonly List<Job> _jobs = [];
    private readonly List<string> _shapedNotes = [];
    private Dictionary<long, Grid>? _allGrids;
    private int _revealedEarly;

    private sealed record Job(
        View View, GridSteps Settings, List<(Grid Grid, Line Line)> Lines, string Source);

    private GridAdjustEngine(Document doc, GridAdjustRequest req)
    {
        _doc = doc;
        _req = req;
    }

    public static GridAdjustResult Run(Document doc, GridAdjustRequest req)
        => new GridAdjustEngine(doc, req).Execute();

    private static long Id(Element e) => RevitCompat.IdKey(e.Id);

    /// <summary>Revit's message, first line only - the rest buries it.</summary>
    private static string FirstLine(Exception ex)
        => (ex.Message ?? "").Trim().Split('\n').FirstOrDefault()?.Trim() ?? "";

    private void Skip(string what, string reason)
    {
        _r.Skipped.Add($"{what}  -  {reason}");
        _r.Reasons.Add(reason);
    }

    /// <summary>Skip a whole view, and keep it out of the later passes too.</summary>
    private void Leave(View view, string reason)
    {
        Skip(view.Name, reason);
        _leftAlone.Add(Id(view));
    }

    private bool Copying => _req.CopySource is not null;

    private Dictionary<long, Grid> AllGrids()
        => _allGrids ??= new FilteredElementCollector(_doc).OfClass(typeof(Grid)).Cast<Grid>()
            .ToDictionary(Id);

    // ---------------------------------------------------------------- setup

    private GridAdjustResult Execute()
    {
        List<(Grid, Line)> baseLines = StraightGrids(
            _req.PickedGrids.Count > 0
                ? _req.PickedGrids
                : new FilteredElementCollector(_doc).OfClass(typeof(Grid)).Cast<Grid>());

        HashSet<long>? baseKeep = _req.PickedGrids.Count > 0
            ? [.. _req.PickedGrids.Select(Id)]
            : null;

        foreach (View view in _req.Views)
        {
            // a copy takes every grid the reference shows, so the per-view
            // scope box plan decides nothing for it - not even which views run
            GridScopePlanEntry? entry = null;
            if (!Copying)
                _req.Plan?.TryGetValue(Id(view), out entry);
            if (entry is null)
            {
                _jobs.Add(new Job(view, _req.Base, baseLines,
                    _req.PickedGrids.Count > 0 ? "a chosen set" : "every grid"));
                _keepFor[Id(view)] = baseKeep;
                continue;
            }

            string scope = string.IsNullOrEmpty(entry.ScopeName) ? "its scope box" : entry.ScopeName;
            List<Grid> setGrids = [.. entry.GridUids.Select(u => _doc.GetElement(u)).OfType<Grid>()];
            List<(Grid, Line)> lines = StraightGrids(setGrids);
            if (lines.Count == 0)
            {
                // Never "every grid": an empty set means nobody has said which
                // grids belong to this part yet.
                Skip(view.Name, $"no grids are set up for \"{scope}\"");
                continue;
            }

            _jobs.Add(new Job(view, _req.Base.With(entry.Settings), lines, $"\"{scope}\""));
            // every grid of the set, curved ones included: a grid in the set is
            // never hidden just because the steps cannot work on it
            _keepFor[Id(view)] = [.. setGrids.Select(Id)];
        }

        if (!Copying)
        {
            HashSet<string> warned = [];
            for (int i = 0; i < _jobs.Count; i++)
            {
                Job job = _jobs[i];
                if (!job.Settings.Spread || job.Settings.GapMm > 0)
                    continue;
                _jobs[i] = job with { Settings = job.Settings with { Spread = false } };
                if (warned.Add(job.Source))
                    _r.Log.Add($"STEP 4 needs a distance above 0 mm ({job.Source}) - those "
                               + "bubbles were not spread.");
            }
        }

        // When copying, the reference view is the set for hide / unhide / 2D:
        // what it shows is what each view should show. A view sharing no grid
        // with it has nothing copied and is left alone as a whole (RunCopy),
        // so an empty overlap can never hide everything it draws.
        if (Copying && (_req.HideOthers || _req.ShowSet || _req.All2d))
        {
            HashSet<long>? reference = GridsInView(_req.CopySource!);
            if (reference is null)
                _r.Log.Add($"Revit would not say which grids \"{_req.CopySource!.Name}\" shows, so the views "
                           + "are hidden / unhidden by their own sets only.");
            else
                foreach (Job job in _jobs)
                    _keepFor[Id(job.View)] = [.. reference];
        }

        // ---- the 3D guard: recorded before, compared after ----
        Dictionary<long, Line3d>? before3d = null;
        TransactionGroup? guard = null;
        if (_req.Views.Count > 0 && _jobs.Count > 0)
        {
            before3d = GridLines3d();
            try
            {
                guard = new TransactionGroup(_doc, "Adjust grids");
                guard.Start();
            }
            catch (Exception ex)
            {
                guard?.Dispose();
                guard = null;
                _r.Log.Add($"The 3D safety net could not be set up ({FirstLine(ex)}) - the run goes ahead "
                           + "and is still checked at the end.");
            }
        }

        Exception? runError = null;
        try
        {
            bool copied = false;
            if (_req.Views.Count == 0)
                _r.Log.Add("No view was picked - nothing was changed.");
            else if (_jobs.Count == 0)
                _r.Log.Add("None of the picked views had grids to work on - nothing was changed.");
            else if (Copying)
                copied = RunCopy(_req.CopySource!);
            else if (!_jobs.Any(j => j.Settings.AnyStep))
            {
                if (!(_req.HideOthers || _req.ShowSet || _req.All2d))
                    _r.Log.Add("No step is ticked - nothing was changed.");
            }
            else if (!_jobs.Any(j => j.Lines.Count > 0))
                _r.Log.Add("No straight grids were found to work on.");
            else
                RunSteps(baseLines.Count);

            bool mayContinue = _req.Views.Count > 0 && _jobs.Count > 0 && (!Copying || copied);

            if ((_req.HideOthers || _req.ShowSet) && mayContinue)
                RunHide();

            // Last, so it also catches grids of the set that the steps, the
            // copy or the unhide pass have just shown again.
            if (_req.All2d && mayContinue)
                RunAll2d();
        }
        catch (Exception ex)
        {
            // Anything that escapes: the `using` transactions have already
            // rolled back on the way out, and the group is rolled back below.
            runError = ex;
        }

        if (runError is not null)
        {
            if (guard is not null)
            {
                try { guard.RollBack(); } catch (Exception) { }
                UndoReport($"The run stopped on an error ({FirstLine(runError)}) and was rolled back as a "
                           + "whole: the model is exactly as it was.");
            }
            else
            {
                _r.Log.Add($"The run stopped on an error ({FirstLine(runError)}). Check the model and press "
                           + "Undo in Revit if anything changed.");
            }
        }
        else if (before3d is not null)
        {
            List<string> moved;
            try
            {
                moved = MovedIn3d(before3d);
            }
            catch (Exception ex)
            {
                moved = [$"(the 3D check itself failed: {FirstLine(ex)})"];
            }

            string names = string.Join(", ", moved.OrderBy(n => n, StringComparer.Ordinal).Take(10));
            if (moved.Count > 0)
            {
                if (guard is not null)
                {
                    guard.RollBack();
                    UndoReport($"3D CHECK FAILED - the 3D line of {moved.Count} grid(s) changed ({names}). The "
                               + "WHOLE run was rolled back: the model is exactly as it was.");
                }
                else
                {
                    _r.Log.Add($"3D CHECK FAILED - the 3D line of {moved.Count} grid(s) changed ({names}) and "
                               + "the run could not be rolled back: press Undo in Revit.");
                }
            }
            else
            {
                guard?.Assimilate();    // the whole run: one step in Undo
                _r.Log.Add($"3D check: the 3D lines of all {before3d.Count} grid(s) in the model are "
                           + "unchanged - only the picked views were touched.");
            }
        }

        guard?.Dispose();

        // The reasons tallied, so a long skipped list stays readable.
        foreach (IGrouping<string, string> g in _r.Reasons.GroupBy(x => x)
                     .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal))
            _r.Log.Add($"skipped {g.Count()} x  {g.Key}");

        return _r;
    }

    /// <summary>
    /// Commit - or, when Revit refused it with an error (rolled back by the
    /// <see cref="ErrorCollector"/> instead of a modal dialog mid-run), say so
    /// with Revit's own words. False means nothing of that transaction is in
    /// the model.
    /// </summary>
    private bool Committed(Transaction t, ErrorCollector errors)
    {
        if (t.Commit() == TransactionStatus.Committed)
            return true;

        _r.Log.Add($"Revit refused \"{t.GetName()}\" and it was rolled back: "
                   + (errors.Errors.Count > 0 ? string.Join("; ", errors.Errors.Distinct()) : "no reason given"));
        return false;
    }

    /// <summary>After a roll-back nothing of the run is in the model, so nothing it reported may stay.</summary>
    private void UndoReport(string message)
    {
        _r.Adjusted.Clear();
        _r.Skipped.Clear();
        _r.Reasons.Clear();
        _r.Log.Clear();
        _r.Log.Add(message);
    }

    /// <summary>(grid, model line) for the straight grids, deduped, read once per grid.</summary>
    private List<(Grid, Line)> StraightGrids(IEnumerable<Grid> grids)
    {
        List<(Grid, Line)> output = [];
        HashSet<long> taken = [];
        foreach (Grid g in grids)
        {
            long key = Id(g);
            if (!taken.Add(key))
                continue;
            if (!_lineCache.TryGetValue(key, out (Grid, Line)? cached))
            {
                Curve? curve = null;
                try { curve = g.Curve; } catch (Exception) { }
                cached = curve is Line line ? (g, line) : null;
                _lineCache[key] = cached;
            }

            if (cached is not null)
                output.Add(cached.Value);
        }

        return output;
    }

    // ---------------------------------------------------------------- the 3D guard

    private sealed record Line3d(string Name, XYZ A, XYZ B, XYZ? Low, XYZ? High);

    /// <summary>Every grid's 3D line and its vertical 3D extent (null if unread).</summary>
    private Dictionary<long, Line3d> GridLines3d()
    {
        Dictionary<long, Line3d> output = [];
        foreach (Grid g in new FilteredElementCollector(_doc).OfClass(typeof(Grid)).Cast<Grid>())
        {
            XYZ a, b;
            try
            {
                Curve c = g.Curve;
                a = c.GetEndPoint(0);
                b = c.GetEndPoint(1);
            }
            catch (Exception)
            {
                continue;
            }

            XYZ? low = null, high = null;
            try
            {
                Outline box = g.GetExtents();
                low = box.MinimumPoint;
                high = box.MaximumPoint;
            }
            catch (Exception) { }

            output[Id(g)] = new Line3d(g.Name, a, b, low, high);
        }

        return output;
    }

    /// <summary>Names of the grids whose 3D line is not where it was.</summary>
    private List<string> MovedIn3d(Dictionary<long, Line3d> before)
    {
        List<string> moved = [];
        Dictionary<long, Line3d> after = GridLines3d();
        foreach (KeyValuePair<long, Line3d> kv in before)
        {
            Line3d was = kv.Value;
            if (!after.TryGetValue(kv.Key, out Line3d? now))
                moved.Add(was.Name + " (gone)");
            else if (now.A.DistanceTo(was.A) > 1e-6 || now.B.DistanceTo(was.B) > 1e-6)
                moved.Add(was.Name);
            else if (was.Low is not null && now.Low is not null && was.High is not null && now.High is not null
                     && (now.Low.DistanceTo(was.Low) > 1e-6 || now.High.DistanceTo(was.High) > 1e-6))
                moved.Add(was.Name + " (vertical extent)");
        }

        return moved;
    }

    // ---------------------------------------------------------------- geometry

    private sealed record Crop(Transform Forward, Transform Inverse, List<(double X, double Y)> Corners, ViewRect Rect);

    private static Crop CropData(View view)
    {
        BoundingBoxXYZ crop = view.CropBox;
        Transform t = crop.Transform;
        ViewRect rect = new(crop.Min.X, crop.Min.Y, crop.Max.X, crop.Max.Y);
        return new Crop(t, t.Inverse, WorldCorners(t, rect), rect);
    }

    /// <summary>CropData read once per view - CropBox is rebuilt on every read.</summary>
    private Crop CachedCrop(View view)
    {
        long key = Id(view);
        if (!_cropCache.TryGetValue(key, out Crop? crop))
            _cropCache[key] = crop = CropData(view);
        return crop;
    }

    private static List<(double X, double Y)> WorldCorners(Transform forward, ViewRect rect)
        => [.. rect.Corners().Select(c => forward.OfPoint(new XYZ(c.X, c.Y, 0)))
               .Select(p => (p.X, p.Y))];

    /// <summary>
    /// The model line cut to the view's crop rectangle - (End0 side, End1
    /// side) in world coordinates - or null when there is nothing to cut to
    /// (crop off or unreadable) or the line misses the crop. An end already
    /// inside is returned unchanged.
    /// </summary>
    private (XYZ, XYZ)? ClipToCrop(View view, Line modelLine)
    {
        Crop crop;
        try
        {
            if (!view.CropBoxActive)
                return null;
            crop = CachedCrop(view);
        }
        catch (Exception)
        {
            return null;
        }

        XYZ a = crop.Inverse.OfPoint(modelLine.GetEndPoint(0));
        XYZ b = crop.Inverse.OfPoint(modelLine.GetEndPoint(1));
        (double Low, double High)? t = GridMath.ClipToRect(a.X, a.Y, b.X, b.Y, crop.Rect);
        if (t is null)
            return null;

        XYZ At(double s) => crop.Forward.OfPoint(new XYZ(a.X + (b.X - a.X) * s, a.Y + (b.Y - a.Y) * s, a.Z));
        return (At(t.Value.Low), At(t.Value.High));
    }

    private Element? ViewScopeBox(View view)
    {
        foreach (Func<Parameter?> read in new Func<Parameter?>[]
                 {
                     () => view.get_Parameter(BuiltInParameter.VIEWER_VOLUME_OF_INTEREST_CROP),
                     () => view.LookupParameter("Scope Box"),
                 })
        {
            try
            {
                Parameter? p = read();
                if (p is null)
                    continue;
                ElementId id = p.AsElementId();
                if (RevitCompat.IdKey(id) > 0)
                    return _doc.GetElement(id);
            }
            catch (Exception) { }
        }

        return null;
    }

    /// <summary>
    /// The scope box's outline in VIEW coordinates. Read from its own edges,
    /// so a box rotated to the view comes out as exactly its outline; the
    /// bounding box (larger, world-aligned) is only the fallback.
    /// </summary>
    private static ViewRect? ScopeRectangle(Element scope, Transform inverse)
    {
        List<XYZ> points = [];
        try
        {
            GeometryElement? geo = scope.get_Geometry(new Options());
            if (geo is not null)
            {
                foreach (GeometryObject thing in geo)
                {
                    if (thing is Line l)
                    {
                        points.Add(l.GetEndPoint(0));
                        points.Add(l.GetEndPoint(1));
                    }
                }
            }
        }
        catch (Exception)
        {
            points.Clear();
        }

        if (points.Count == 0)
        {
            try
            {
                BoundingBoxXYZ box = scope.get_BoundingBox(null);
                foreach (double x in new[] { box.Min.X, box.Max.X })
                foreach (double y in new[] { box.Min.Y, box.Max.Y })
                    points.Add(new XYZ(x, y, box.Min.Z));
            }
            catch (Exception)
            {
                return null;
            }
        }

        List<XYZ> local = [.. points.Select(inverse.OfPoint)];
        ViewRect r = new(local.Min(p => p.X), local.Min(p => p.Y),
                         local.Max(p => p.X), local.Max(p => p.Y));
        if (r.MaxX - r.MinX < GridMath.MinLength || r.MaxY - r.MinY < GridMath.MinLength)
            return null;
        return r;
    }

    /// <summary>The ids of the grids Revit draws in the view, or null if it refused.</summary>
    private HashSet<long>? GridsInView(View view)
    {
        try
        {
            return [.. new FilteredElementCollector(_doc, view.Id).OfClass(typeof(Grid))
                .Select(Id)];
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static Line? ViewLine(Grid grid, View view)
    {
        try
        {
            foreach (Curve c in grid.GetCurvesInView(DatumExtentType.ViewSpecific, view))
            {
                if (c is Line l)
                    return l;
            }
        }
        catch (Exception) { }

        try
        {
            if (grid.Curve is Line l)
                return l;
        }
        catch (Exception) { }

        return null;
    }

    /// <summary>
    /// (End0, End1) of the grid as the view DRAWS it. An end still on the 3D
    /// extent keeps an old 2D line underneath, so its point comes from the
    /// model line - cut at the crop edge when it runs past the crop box,
    /// because that is where Revit draws it (copying the model end gave a
    /// site-wide grid its FULL length). A 2D end is taken as it is: it is a
    /// length somebody set on purpose.
    /// </summary>
    private XYZ[]? DrawnEnds(Grid grid, View view, Line modelLine)
    {
        Line? line = ViewLine(grid, view);
        if (line is null)
            return null;

        XYZ[] points = [line.GetEndPoint(0), line.GetEndPoint(1)];
        List<int> on3d = [];
        for (int i = 0; i < 2; i++)
        {
            try
            {
                if (grid.GetDatumExtentTypeInView(Ends[i], view) != DatumExtentType.ViewSpecific)
                {
                    points[i] = modelLine.GetEndPoint(i);
                    on3d.Add(i);
                }
            }
            catch (Exception)
            {
                // unreadable: the view line is the best there is
            }
        }

        if (on3d.Count > 0 && ClipToCrop(view, modelLine) is (XYZ, XYZ) clipped)
        {
            foreach (int i in on3d)
                points[i] = i == 0 ? clipped.Item1 : clipped.Item2;
        }

        return points;
    }

    /// <summary>
    /// Both ends of the grid 2D in the view and the given line - or the grid
    /// left EXACTLY as it was. Switching ends to 2D seeds them from the 3D
    /// extent (for a long grid its full length), so a refused line must not
    /// leave that behind. Every 2D write comes through here.
    /// </summary>
    private void Write2d(Grid grid, View view, Line line)
    {
        using SubTransaction step = new(_doc);
        step.Start();
        try
        {
            foreach (DatumEnds end in Ends)
                grid.SetDatumExtentType(end, view, DatumExtentType.ViewSpecific);
            grid.SetCurveInView(DatumExtentType.ViewSpecific, view, line);
            step.Commit();
        }
        catch (Exception ex)
        {
            try { step.RollBack(); } catch (Exception) { }
            throw new InvalidOperationException("left as it was, Revit refused the 2D line: " + FirstLine(ex));
        }
    }

    /// <summary>
    /// The base line grown to span the rectangle's corners plus the offsets,
    /// collinear with the datum and running in the DATUM's direction.
    /// </summary>
    private static Line? Stretched(
        Line baseLine, Line modelLine, List<(double X, double Y)> corners,
        Transform inverse, bool horizontal, SideOffsets offsets)
    {
        XYZ p1 = baseLine.GetEndPoint(0);
        XYZ p2 = baseLine.GetEndPoint(1);

        XYZ modelDirection = modelLine.GetEndPoint(1) - modelLine.GetEndPoint(0);
        if ((p2 - p1).DotProduct(modelDirection) < 0)
            (p1, p2) = (p2, p1);

        XYZ v1 = inverse.OfPoint(p1);
        XYZ v2 = inverse.OfPoint(p2);
        (double lowOffset, double highOffset) =
            GridMath.EndOffsets(v1.X, v1.Y, v2.X, v2.Y, horizontal, offsets);

        XYZ direction = (p2 - p1).Normalize();
        double z = p1.Z;
        (double Low, double High)? range = GridMath.StretchRange(
            corners.Select(c => direction.DotProduct(new XYZ(c.X, c.Y, z) - p1)),
            lowOffset, highOffset);
        if (range is null)
            return null;

        return Line.CreateBound(p1 + direction * range.Value.Low, p1 + direction * range.Value.High);
    }

    private static (DatumEnds Show, DatumEnds Hide) BubbleEnd(
        Line modelLine, Transform inverse, bool horizontal, bool wantLeft, bool wantTop)
    {
        XYZ a = inverse.OfPoint(modelLine.GetEndPoint(0));
        XYZ b = inverse.OfPoint(modelLine.GetEndPoint(1));
        return GridMath.BubbleOnEnd0(a.X, a.Y, b.X, b.Y, horizontal, wantLeft, wantTop)
            ? (DatumEnds.End0, DatumEnds.End1)
            : (DatumEnds.End1, DatumEnds.End0);
    }

    private static bool LooksDown(View view)
    {
        try { return Math.Abs(view.ViewDirection.Z) > 0.99; }
        catch (Exception) { return false; }
    }

    private static XYZ AtHeight(XYZ p, double z) => new(p.X, p.Y, z);

    // ---------------------------------------------------------------- steps

    private void RunSteps(int offeredCount)
    {
        using Transaction t = new(_doc, "Adjust grids to crop box");
        ErrorCollector errors = ErrorCollector.Start(t);

        int total = 0, onEnd0 = 0, onEnd1 = 0;
        int spreadMoved = 0, spreadKept = 0, spreadRefused = 0;
        SortedSet<long> gapsUsed = [];
        bool usedConvert = false, usedAlign = false, usedBubbles = false, usedSpread = false;
        int toBox = 0, toCrop = 0;

        try
        {
            foreach (Job job in _jobs)
            {
                GridSteps s = job.Settings;
                View view = job.View;
                if (!s.AnyStep)
                    continue;   // hide / unhide / 2D only, for this view

                if (!view.CropBoxActive)
                {
                    Leave(view, "its crop box is switched off");
                    continue;
                }

                // Everything below reads a grid off its PLAN line; in a section
                // STEP 2 would rewrite the 2D extent as a stub.
                if (!LooksDown(view))
                {
                    Leave(view, "sections and elevations are not supported - plan views only");
                    continue;
                }

                Crop crop;
                try
                {
                    crop = CropData(view);
                }
                catch (Exception ex)
                {
                    Leave(view, $"the crop box could not be read: {FirstLine(ex)}");
                    continue;
                }

                ViewRect rect = crop.Rect;
                List<(double X, double Y)> corners = crop.Corners;
                string stretchedTo = "";
                if (_req.ToScope && s.Align)
                {
                    Element? scope = ViewScopeBox(view) ?? _req.ChosenScope;
                    ViewRect? box = scope is null ? null : ScopeRectangle(scope, crop.Inverse);
                    if (box is not null)
                    {
                        rect = box.Value;
                        corners = WorldCorners(crop.Forward, rect);
                        stretchedTo = scope!.Name;
                    }
                    else if (scope is not null)
                    {
                        _r.Log.Add($"{view.Name}: the outline of scope box \"{scope.Name}\" could "
                                   + "not be read - stretched to the crop box instead.");
                    }
                }

                SideOffsets offsets = new(s.LeftMm / Mm, s.RightMm / Mm, s.TopMm / Mm, s.BottomMm / Mm);
                int done = 0, failures = 0;
                List<(Grid Grid, bool Horizontal)> touched = [];

                RevealSet(view);
                HashSet<long>? here = GridsInView(view);
                if (here is null)
                    _r.Log.Add($"{view.Name}: Revit would not say which grids this view shows, so "
                               + "they were chosen from the crop-box geometry alone.");
                else
                    ReportCurved(view, here);

                foreach ((Grid grid, Line modelLine) in job.Lines)
                {
                    // the safeguard first: a grid this view does not draw is never touched
                    if (here is not null && !here.Contains(Id(grid)))
                        continue;

                    XYZ a = crop.Inverse.OfPoint(modelLine.GetEndPoint(0));
                    XYZ b = crop.Inverse.OfPoint(modelLine.GetEndPoint(1));
                    if (!GridMath.CrossesRectangle(a.X, a.Y, b.X, b.Y, rect))
                        continue;

                    try
                    {
                        if (!grid.CanBeVisibleInView(view) || grid.IsHidden(view))
                            continue;
                    }
                    catch (Exception)
                    {
                        continue;
                    }

                    try
                    {
                        bool horizontal = GridMath.IsHorizontal(a.X, a.Y, b.X, b.Y);

                        if (s.Convert || s.Align)
                        {
                            // From what the view DRAWS, not the raw 2D curve: a 3D
                            // end past the crop box is drawn at the crop edge, and
                            // converting there moves nothing on the sheet.
                            XYZ[]? ends = DrawnEnds(grid, view, modelLine);
                            Line? shown = ViewLine(grid, view);
                            if (ends is null || shown is null)
                            {
                                Skip($"{grid.Name} in {view.Name}", "Revit returned no line for it");
                                continue;
                            }

                            double z = shown.GetEndPoint(0).Z;
                            Line? line = Line.CreateBound(AtHeight(ends[0], z), AtHeight(ends[1], z));
                            if (s.Align)
                            {
                                line = Stretched(line, modelLine, corners, crop.Inverse, horizontal, offsets);
                                if (line is null)
                                {
                                    Skip($"{grid.Name} in {view.Name}", "the offsets collapse the grid to nothing");
                                    continue;
                                }
                            }

                            // both ends 2D and the line, or the grid left exactly as it was
                            Write2d(grid, view, line);
                        }

                        if (s.Bubbles)
                        {
                            (DatumEnds show, DatumEnds hide) =
                                BubbleEnd(modelLine, crop.Inverse, horizontal, s.BubbleLeft, s.BubbleTop);
                            grid.ShowBubbleInView(show, view);
                            grid.HideBubbleInView(hide, view);
                            if (show == DatumEnds.End0) onEnd0++; else onEnd1++;
                        }

                        done++;
                        touched.Add((grid, horizontal));
                    }
                    catch (Exception ex)
                    {
                        failures++;
                        Skip($"{grid.Name} in {view.Name}", FirstLine(ex));
                    }
                }

                if (s.Spread && touched.Count > 0)
                {
                    try
                    {
                        // without it Revit can hand back the OLD view curves
                        _doc.Regenerate();
                        (int moved, int kept, int refused) =
                            SpreadBubbles(view, touched, crop.Forward, crop.Inverse, rect, s.GapMm / Mm);
                        spreadMoved += moved;
                        spreadKept += kept;
                        spreadRefused += refused;
                        gapsUsed.Add((long)Math.Round(s.GapMm, MidpointRounding.AwayFromZero));
                    }
                    catch (Exception ex)
                    {
                        _r.Log.Add($"{view.Name}: the bubbles could not be spread: {FirstLine(ex)}");
                    }
                }

                total += done;
                if (done > 0)
                {
                    usedConvert |= s.Convert;
                    usedAlign |= s.Align;
                    usedBubbles |= s.Bubbles;
                    usedSpread |= s.Spread;
                    if (s.Align)
                    {
                        if (stretchedTo.Length > 0) toBox++; else toCrop++;
                    }

                    _r.Adjusted.Add($"{view.Name}      {done} grid(s) from {job.Source}"
                                    + (stretchedTo.Length > 0 ? $", to scope box \"{stretchedTo}\"" : ""));
                }
                else if (failures == 0)
                {
                    Leave(view, "no grid crosses this view's crop box");
                }
            }
        }
        catch (Exception ex)
        {
            _r.Log.Add($"The run stopped early: {FirstLine(ex)}");
        }

        if (t.GetStatus() != TransactionStatus.Started)
        {
            _r.Log.Add("The transaction was already closed - nothing was written.");
            return;
        }

        if (total == 0)
        {
            t.RollBack();
            _revealedEarly = 0;     // the roll-back hid them again
            _r.Log.Add("The steps changed nothing - their transaction was rolled back.");
            return;
        }

        if (!Committed(t, errors))
        {
            _r.Adjusted.Clear();    // rolled back: nothing it adjusted is in the model
            return;
        }

        List<string> steps = [];
        if (usedConvert)
            steps.Add("converted to 2D");
        else if (usedAlign)
            steps.Add("converted to 2D (STEP 2 needs it)");
        if (usedAlign)
        {
            steps.Add(toBox > 0 && toCrop == 0 ? "stretched to the scope box"
                : toBox > 0 ? $"stretched to the scope box ({toBox} view(s)) or the crop box "
                              + $"({toCrop} view(s) with no scope box)"
                : "stretched to the crop box");
        }
        if (usedBubbles) steps.Add("bubbles set");
        if (usedSpread) steps.Add("clashing bubbles spread");

        _r.Log.Add($"{total} grid(s) across {_r.Adjusted.Count} view(s): {string.Join(", ", steps)}.");
        if (usedBubbles)
            _r.Log.Add($"bubbles: {onEnd0} on End0, {onEnd1} on End1 - the split is the grids being "
                       + "drawn in different directions, which is expected.");
        if (usedSpread)
        {
            string line = $"bubbles spread: {spreadMoved} moved onto an elbow, at least "
                          + $"{string.Join(" / ", gapsUsed)}mm apart.";
            if (spreadKept > 0)
                line += $" {spreadKept} already had a leader and were left as they are - including "
                        + "any placed by an earlier run, so changing the distance will not move those again.";
            if (spreadRefused > 0)
                line += $" {spreadRefused} Revit would not move.";
            _r.Log.Add(line);
        }

        if (_req.Plan is { Count: > 0 } plan)
        {
            int matched = _jobs.Count(j => plan.ContainsKey(Id(j.View)));
            _r.Log.Add($"{matched} view(s) used their own scope box's grids and settings, "
                       + $"{_jobs.Count - matched} used the ones on screen.");
        }
        else
        {
            _r.Log.Add($"Grids offered to the run: {offeredCount}"
                       + (_req.PickedGrids.Count > 0 ? " (a chosen set - ticks or a scope box)"
                           : " (every grid in the model)")
                       + ". Only the ones each view actually shows were touched.");
        }
    }

    /// <summary>
    /// Name the curved grids of the view's set that it draws. The steps work on
    /// straight grids only, and one quietly left alone reads as "the tool
    /// missed it" - so it is reported, with what to do.
    /// </summary>
    private void ReportCurved(View view, HashSet<long> here)
    {
        _keepFor.TryGetValue(Id(view), out HashSet<long>? keep);
        foreach (long key in here)
        {
            if (keep is not null && !keep.Contains(key))
                continue;
            if (!AllGrids().TryGetValue(key, out Grid? g))
                continue;
            bool straight;
            try { straight = g.Curve is Line; }
            catch (Exception) { continue; }
            if (!straight)
                Skip($"{g.Name} in {view.Name}", "a curved or multi-segment grid - the steps work on straight grids "
                                                 + "only, so adjust it by hand");
        }
    }

    // ---------------------------------------------------------------- STEP 4

    // HOW A GRID'S LEADER IS SHAPED - measured in Revit 2026, not assumed (the
    // gridtest harness, 2026-09-28). After AddLeader on a vertical grid whose
    // 2D line ends at y=1000:
    //     End     (x,     -219)   ON the grid line, further in
    //     Elbow   (x+609,  390)   between
    //     Anchor  (x+609, 1000)   the BUBBLE: sideways, at the line end's height
    // So the bubble sits at the ANCHOR, which is read-only and follows the
    // elbow's sideways position; the END must stay on the grid line. The
    // Dynamo graph's STEP 4 moved End sideways instead - off the line - which
    // Revit refuses every time, so no bubble was ever spread.

    /// <summary>Where the bubble at this end sits: its leader's Anchor, or the 2D line's end.</summary>
    private static XYZ? BubbleAnchor(Grid grid, View view, DatumEnds end)
    {
        try
        {
            Leader? leader = grid.GetLeader(end, view);
            if (leader is not null)
                return leader.Anchor;
        }
        catch (Exception) { }

        Line? line = ViewLine(grid, view);
        return line?.GetEndPoint(end == DatumEnds.End0 ? 0 : 1);
    }

    /// <summary>Is the bubble where it was sent (in plan)? Read back from Revit, never assumed.</summary>
    private static bool BubbleLandedAt(Grid grid, DatumEnds end, View view, XYZ target)
    {
        try
        {
            Leader? leader = grid.GetLeader(end, view);
            if (leader is null)
                return false;
            XYZ a = leader.Anchor;
            return Math.Abs(a.X - target.X) < LeaderTol && Math.Abs(a.Y - target.Y) < LeaderTol;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private sealed record Bubble(double Along, Grid Grid, DatumEnds End, XYZ Anchor, bool Horizontal, bool HasLeader);

    /// <summary>
    /// Push apart bubbles that land on top of each other, onto an elbow. Once
    /// STEP 2 has stretched a family of parallel grids, their bubbles sit on
    /// one line, so this is one-dimensional. Leaders are added for the whole
    /// view first and the document regenerated ONCE before elbows are set.
    /// Returns (moved, already had a leader, refused).
    /// </summary>
    private (int, int, int) SpreadBubbles(
        View view, List<(Grid Grid, bool Horizontal)> entries,
        Transform forward, Transform inverse, ViewRect rect, double gap)
    {
        Dictionary<(bool, bool), List<Bubble>> families = [];
        HashSet<((bool, bool), long)> seen = [];

        foreach ((Grid grid, bool horizontal) in entries)
        {
            foreach (DatumEnds end in Ends)
            {
                try
                {
                    if (!grid.IsBubbleVisibleInView(end, view))
                        continue;
                }
                catch (Exception)
                {
                    continue;
                }

                XYZ? anchor = BubbleAnchor(grid, view, end);
                if (anchor is null)
                    continue;

                XYZ here = inverse.OfPoint(anchor);
                (double along, bool side) = horizontal
                    ? (here.Y, here.X > rect.MidX)
                    : (here.X, here.Y > rect.MidY);
                (bool, bool) key = (horizontal, side);

                // one entry per grid per family: a short datum's two ends can
                // land in the same family and would read as clashing
                if (!seen.Add((key, Id(grid))))
                    continue;

                bool hasLeader = false;
                try { hasLeader = grid.GetLeader(end, view) is not null; } catch (Exception) { }

                if (!families.TryGetValue(key, out List<Bubble>? list))
                    families[key] = list = [];
                list.Add(new Bubble(along, grid, end, anchor, horizontal, hasLeader));
            }
        }

        int moved = 0, already = 0, refused = 0;
        List<(Grid Grid, DatumEnds End, XYZ Anchor, double Shift, bool Horizontal)> pending = [];

        foreach (List<Bubble> family in families.Values)
        {
            List<int> fixedIdx = [.. Enumerable.Range(0, family.Count).Where(i => family[i].HasLeader)];
            Dictionary<int, double> shifts =
                GridMath.SpacingShifts([.. family.Select(b => b.Along)], gap, fixedIdx, tolerance: LeaderTol);
            if (shifts.Count > 0)
                already += fixedIdx.Count;

            foreach (int i in shifts.Keys.OrderBy(k => k))
            {
                Bubble b = family[i];
                try
                {
                    b.Grid.AddLeader(b.End, view);
                }
                catch (Exception ex)
                {
                    refused++;
                    Skip($"{b.Grid.Name} in {view.Name}", $"a bubble leader was refused: {FirstLine(ex)}");
                    continue;
                }

                pending.Add((b.Grid, b.End, b.Anchor, shifts[i], b.Horizontal));
            }
        }

        if (pending.Count == 0)
            return (moved, already, refused);

        _doc.Regenerate();

        foreach ((Grid grid, DatumEnds end, XYZ anchor, double shift, bool horizontal) in pending)
        {
            try
            {
                Leader? leader = grid.GetLeader(end, view);
                if (leader is null)
                {
                    refused++;
                    Skip($"{grid.Name} in {view.Name}", "the leader did not survive the regeneration");
                    continue;
                }

                // Measured from the bubble position read BEFORE the leader was
                // added (the line end) - the same number the spacing used.
                XYZ baseP = anchor;
                Line? line = ViewLine(grid, view);
                if (line is null)
                {
                    refused++;
                    Skip($"{grid.Name} in {view.Name}", "Revit returned no line to put the elbow on");
                    continue;
                }

                // Inward: from this end along the grid towards the other end.
                XYZ other = line.GetEndPoint(end == DatumEnds.End0 ? 1 : 0);
                double length = baseP.DistanceTo(other);
                XYZ inward = (other - baseP).Normalize();

                XYZ step = horizontal ? XYZ.BasisY : XYZ.BasisX;
                XYZ direction = forward.OfVector(step).Normalize();
                XYZ offset = direction * shift;
                XYZ target = baseP + offset;

                // Keep Revit's own proportions for the dog-leg (how far in the
                // elbow and the end sit), falling back to its defaults of 2 ft
                // and 4 ft, never past the far end of the grid.
                double atElbow = (leader.Elbow - baseP).DotProduct(inward);
                double atEnd = (leader.End - baseP).DotProduct(inward);
                if (!(atElbow > LeaderTol && atEnd > atElbow + LeaderTol))
                    (atElbow, atEnd) = (2.0, 4.0);
                atEnd = Math.Min(atEnd, length * 0.9);
                atElbow = Math.Min(atElbow, atEnd * 0.5);

                // The elbow carries the sideways shift (the bubble follows it);
                // the end stays ON the grid line, which Revit insists on.
                (bool placed, string reason) = PlaceLeader(
                    grid, end, view, baseP + inward * atElbow + offset, baseP + inward * atEnd);

                if (placed && BubbleLandedAt(grid, end, view, target))
                {
                    moved++;
                }
                else
                {
                    refused++;
                    Skip($"{grid.Name} in {view.Name}", "Revit would not put the bubble where it was sent"
                                                        + (reason.Length > 0 ? ": " + reason : ""));
                }
            }
            catch (Exception ex)
            {
                refused++;
                Skip($"{grid.Name} in {view.Name}", $"the bubble would not move: {FirstLine(ex)}");
            }
        }

        return (moved, already, refused);
    }

    // ---------------------------------------------------------------- copy

    /// <summary>
    /// Show the view's set grids that an earlier run hid, BEFORE the steps, so
    /// they are drawn again by the time the steps ask what the view shows.
    /// </summary>
    private void RevealSet(View view)
    {
        if (!(_req.HideOthers || _req.ShowSet))
            return;
        if (!_keepFor.TryGetValue(Id(view), out HashSet<long>? keep) || keep is null || keep.Count == 0)
            return;

        List<ElementId> back = [];
        foreach (long key in keep)
        {
            try
            {
                if (AllGrids().TryGetValue(key, out Grid? g) && g.IsHidden(view))
                    back.Add(g.Id);
            }
            catch (Exception) { }
        }

        if (back.Count == 0)
            return;

        try
        {
            view.UnhideElements(back);
            _doc.Regenerate();
        }
        catch (Exception ex)
        {
            Skip(view.Name, $"grids of its set that were hidden could not be shown again: {FirstLine(ex)}");
            return;
        }

        _revealedEarly += back.Count;
    }

    private static bool LeaderMatches(Grid grid, DatumEnds end, View view, XYZ elbow, XYZ bubble)
    {
        try
        {
            Leader? got = grid.GetLeader(end, view);
            return got is not null
                   && got.Elbow.DistanceTo(elbow) < LeaderTol
                   && got.End.DistanceTo(bubble) < LeaderTol;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Put an existing leader's elbow and bubble where wanted. Never the
    /// anchor - Revit keeps it read-only. Tried three ways (both together,
    /// bubble first, elbow first), each checked by reading back. An attempt
    /// that fails part-way is UNDONE before the next, so a leader is never
    /// left half copied; if all fail, the leader is as it was.
    /// </summary>
    private static (bool Ok, string Reason) PlaceLeader(
        Grid grid, DatumEnds end, View view, XYZ elbow, XYZ bubble)
    {
        string reason = "";
        (XYZ Elbow, XYZ End)? original = null;
        try
        {
            Leader? before = grid.GetLeader(end, view);
            if (before is not null)
                original = (before.Elbow, before.End);
        }
        catch (Exception) { }

        void Restore()
        {
            if (original is null)
                return;
            try
            {
                if (LeaderMatches(grid, end, view, original.Value.Elbow, original.Value.End))
                    return;
                Leader leader = grid.GetLeader(end, view);
                leader.Elbow = original.Value.Elbow;
                leader.End = original.Value.End;
                grid.SetLeader(end, view, leader);
            }
            catch (Exception) { }
        }

        void Write(bool setElbow, bool setEnd)
        {
            Leader leader = grid.GetLeader(end, view)
                            ?? throw new InvalidOperationException("the view has no elbow at that end");
            if (setElbow) leader.Elbow = elbow;
            if (setEnd) leader.End = bubble;
            grid.SetLeader(end, view, leader);
        }

        Action[][] attempts =
        [
            [() => Write(true, true)],
            [() => Write(false, true), () => Write(true, false)],
            [() => Write(true, false), () => Write(false, true)],
        ];

        foreach (Action[] steps in attempts)
        {
            try
            {
                foreach (Action a in steps)
                    a();
            }
            catch (Exception ex)
            {
                reason = FirstLine(ex);
            }

            if (LeaderMatches(grid, end, view, elbow, bubble))
                return (true, "");
            Restore();
        }

        return (false, reason);
    }

    /// <summary>
    /// (elbow, end, shift) giving the reference's leader SHAPE at this view's
    /// own anchor, or null when the anchors already agree. The anchor is
    /// read-only, so when it differs the reference's exact elbow is not
    /// "between" here and Revit refuses it; shifting by the anchor difference
    /// keeps the same jog, and the end is put back ON the grid line, within
    /// its two ends.
    /// </summary>
    private static (XYZ Elbow, XYZ End, double Shift)? ShapeAtOwnAnchor(
        Grid grid, DatumEnds end, View view, Leader wanted, double z)
    {
        Leader? mine = grid.GetLeader(end, view);
        if (mine is null)
            return null;
        XYZ delta = mine.Anchor - AtHeight(wanted.Anchor, z);
        if (delta.GetLength() < LeaderTol)
            return null;
        Line? line = ViewLine(grid, view);
        if (line is null)
            return null;

        XYZ a = line.GetEndPoint(0), b = line.GetEndPoint(1);
        double length = a.DistanceTo(b);
        XYZ direction = (b - a).Normalize();
        XYZ movedEnd = AtHeight(wanted.End, a.Z) + delta - a;
        double t = Math.Max(0.0, Math.Min(length, movedEnd.DotProduct(direction)));
        XYZ onLine = a + direction * t;
        return (AtHeight(wanted.Elbow, z) + delta, AtHeight(onLine, z), delta.GetLength());
    }

    /// <summary>
    /// Measurements (mm) that tell apart why Revit refused an elbow, plus
    /// whether the reference's elbow or end lies outside this view's crop box
    /// (Revit wants every point IN the view).
    /// </summary>
    private string ElbowClues(Grid grid, DatumEnds end, View view, Leader wanted, double z)
    {
        List<string> clues = [];
        try
        {
            Leader? mine = grid.GetLeader(end, view);
            if (mine is not null)
                clues.Add($"anchor off by {mine.Anchor.DistanceTo(AtHeight(wanted.Anchor, z)) * Mm:0}");
        }
        catch (Exception) { }

        try
        {
            Line? line = ViewLine(grid, view);
            if (line is not null)
            {
                XYZ a = line.GetEndPoint(0);
                XYZ direction = (line.GetEndPoint(1) - a).Normalize();
                XYZ p = AtHeight(wanted.End, a.Z) - a;
                XYZ off = p - direction * p.DotProduct(direction);
                clues.Add($"end off line by {off.GetLength() * Mm:0}");
            }
        }
        catch (Exception) { }

        List<string> outside = [];
        try
        {
            if (view.CropBoxActive)
            {
                Crop crop = CachedCrop(view);
                foreach ((string name, XYZ point) in new[] { ("elbow", wanted.Elbow), ("end", wanted.End) })
                {
                    XYZ p = crop.Inverse.OfPoint(AtHeight(point, z));
                    if (!(crop.Rect.MinX - GridMath.Tol <= p.X && p.X <= crop.Rect.MaxX + GridMath.Tol
                          && crop.Rect.MinY - GridMath.Tol <= p.Y && p.Y <= crop.Rect.MaxY + GridMath.Tol))
                        outside.Add(name);
                }
            }
        }
        catch (Exception)
        {
            outside.Clear();
        }

        string text = clues.Count > 0 ? $"  [mm: {string.Join(", ", clues)}]" : "";
        if (outside.Count > 0)
            text += $"  [OUTSIDE this view's crop box: {string.Join(" and ", outside)}]";
        return text;
    }

    /// <summary>
    /// Make every grid in the view look as it does in the source: the drawn
    /// line (at this view's height), which end shows the bubble, and the
    /// elbows. An elbow the target has and the source does not cannot be
    /// removed through the API, so those are counted, not guessed at.
    /// Returns (grids copied, elbows set, elbows left, elbows shaped at own anchor).
    /// </summary>
    private (int, int, int, int) CopySetup(View source, View view, List<(Grid Grid, Line Line)> refLines,
        HashSet<long> sourceHere)
    {
        RevealSet(view);
        HashSet<long>? here = GridsInView(view);
        int copied = 0, kept = 0;
        List<(Grid Grid, DatumEnds End, Leader Wanted, double Z)> pending = [];

        // Every straight grid the reference shows is offered, so a grid both
        // views draw that is not offered is curved - no set could change that.
        HashSet<long> offered = [.. refLines.Select(m => Id(m.Grid))];
        List<Grid> both;
        try
        {
            both = [.. new FilteredElementCollector(_doc, view.Id).OfClass(typeof(Grid)).Cast<Grid>()
                .Where(g => sourceHere.Contains(Id(g)) && !offered.Contains(Id(g)))];
        }
        catch (Exception)
        {
            both = [];
        }

        foreach (Grid g in both)
            Skip($"{g.Name} in {view.Name}", "a curved or multi-segment grid, which cannot be copied");

        foreach ((Grid grid, Line modelLine) in refLines)
        {
            long key = Id(grid);
            if (!sourceHere.Contains(key) || (here is not null && !here.Contains(key)))
                continue;
            try
            {
                if (grid.IsHidden(view))
                    continue;
            }
            catch (Exception) { }

            try
            {
                Line? targetLine = ViewLine(grid, view);
                if (targetLine is null)
                {
                    Skip($"{grid.Name} in {view.Name}", "Revit returned no line for it in this view");
                    continue;
                }

                double z = targetLine.GetEndPoint(0).Z;
                XYZ[]? sourceEnds = DrawnEnds(grid, source, modelLine);
                if (sourceEnds is null)
                {
                    Skip($"{grid.Name} in {view.Name}", "Revit returned no line for it in the source view");
                    continue;
                }

                // 2D, both ends, in a sub-transaction: refused, the grid is
                // left exactly as it was. Never undone to 3D.
                Write2d(grid, view, Line.CreateBound(AtHeight(sourceEnds[0], z), AtHeight(sourceEnds[1], z)));

                foreach (DatumEnds end in Ends)
                {
                    if (grid.IsBubbleVisibleInView(end, source))
                        grid.ShowBubbleInView(end, view);
                    else
                        grid.HideBubbleInView(end, view);
                }

                foreach (DatumEnds end in Ends)
                {
                    Leader? wanted = grid.GetLeader(end, source);
                    Leader? current = grid.GetLeader(end, view);
                    if (wanted is null)
                    {
                        if (current is not null)
                        {
                            kept++;
                            _r.ExtraElbows.Add(new ExtraElbow(grid.Id, grid.Name, view.Id, view.Name,
                                end == DatumEnds.End0 ? "start" : "end"));
                        }

                        continue;
                    }

                    if (current is null)
                        grid.AddLeader(end, view);
                    pending.Add((grid, end, wanted, z));
                }

                copied++;
            }
            catch (Exception ex)
            {
                Skip($"{grid.Name} in {view.Name}", FirstLine(ex));
            }
        }

        int elbows = 0, shaped = 0;
        if (pending.Count > 0)
        {
            // a leader added above is not usable until the document regenerates
            _doc.Regenerate();
            foreach ((Grid grid, DatumEnds end, Leader wanted, double z) in pending)
            {
                try
                {
                    if (grid.GetLeader(end, view) is null)
                    {
                        Skip($"{grid.Name} in {view.Name}", "the elbow did not survive the regeneration");
                        continue;
                    }

                    (bool placed, string reason) = PlaceLeader(
                        grid, end, view, AtHeight(wanted.Elbow, z), AtHeight(wanted.End, z));
                    if (placed)
                    {
                        elbows++;
                        continue;
                    }

                    // the exact elbow was refused: if it is the read-only anchor
                    // that differs, place the reference's SHAPE at this view's own
                    // anchor instead - same jog, same offset
                    if (ShapeAtOwnAnchor(grid, end, view, wanted, z) is { } shape
                        && PlaceLeader(grid, end, view, shape.Elbow, shape.End).Ok)
                    {
                        shaped++;
                        _shapedNotes.Add($"{grid.Name} in {view.Name}: shifted {shape.Shift * Mm:0} mm with its anchor");
                        continue;
                    }

                    Skip($"{grid.Name} in {view.Name}",
                        "Revit would not put the elbow where the source has it"
                        + (reason.Length > 0 ? ": " + reason : "")
                        + ElbowClues(grid, end, view, wanted, z));
                }
                catch (Exception ex)
                {
                    Skip($"{grid.Name} in {view.Name}", $"the elbow could not be copied: {FirstLine(ex)}");
                }
            }
        }

        return (copied, elbows, kept, shaped);
    }

    /// <summary>True when the copy was committed; the later passes follow only then.</summary>
    private bool RunCopy(View source)
    {
        if (!LooksDown(source))
        {
            _r.Log.Add($"\"{source.Name}\" is not a plan view - only plan views can be copied from. "
                       + "Nothing was changed.");
            return false;
        }

        HashSet<long>? sourceHere = GridsInView(source);
        if (sourceHere is null || sourceHere.Count == 0)
        {
            _r.Log.Add($"Revit draws no grids in \"{source.Name}\", so there is nothing to copy. "
                       + "Nothing was changed.");
            return false;
        }

        // Every grid the reference shows - not a view's own set. A scope box
        // set left grids the reference shows uncopied, and with hiding on,
        // hidden: the copy then did not look like the reference.
        List<(Grid, Line)> refLines;
        try
        {
            refLines = StraightGrids(
                new FilteredElementCollector(_doc, source.Id).OfClass(typeof(Grid)).Cast<Grid>().ToList());
        }
        catch (Exception ex)
        {
            _r.Log.Add($"Revit would not list the grids \"{source.Name}\" shows: {FirstLine(ex)} - nothing "
                       + "was changed.");
            return false;
        }

        using Transaction t = new(_doc, "Copy grid set-up from a view");
        ErrorCollector errors = ErrorCollector.Start(t);

        int total = 0, elbows = 0, kept = 0, shaped = 0;
        try
        {
            foreach (Job job in _jobs)
            {
                View view = job.View;
                if (Id(view) == Id(source))
                {
                    Leave(view, "it is the view being copied from");
                    continue;
                }

                if (!LooksDown(view))
                {
                    Leave(view, "sections and elevations are not supported - plan views only");
                    continue;
                }

                (int done, int setElbows, int left, int reshaped) = CopySetup(source, view, refLines, sourceHere);
                total += done;
                elbows += setElbows;
                kept += left;
                shaped += reshaped;
                if (done > 0)
                    _r.Adjusted.Add($"{view.Name}      {done} grid(s) from \"{source.Name}\"");
                else
                    // left alone as a whole: with the reference as its set, the
                    // hide pass would otherwise hide every grid this view draws
                    Leave(view, $"no grid it shows is also in \"{source.Name}\"");
            }
        }
        catch (Exception ex)
        {
            _r.Log.Add($"The run stopped early: {FirstLine(ex)}");
        }

        if (t.GetStatus() != TransactionStatus.Started)
        {
            _r.Log.Add("The transaction was already closed - nothing was written.");
            return false;
        }

        if (total == 0)
        {
            t.RollBack();
            _revealedEarly = 0;
            _r.Log.Add("Nothing was changed - the transaction was rolled back.");
            return false;
        }

        if (!Committed(t, errors))
        {
            _r.Adjusted.Clear();    // rolled back: nothing was copied
            return false;
        }
        _r.Log.Add($"Copied the grid set-up of \"{source.Name}\" - 2D extents, bubble ends and elbows - "
                   + $"to {total} grid(s) across {_r.Adjusted.Count} view(s).");
        if (elbows > 0)
            _r.Log.Add($"{elbows} elbow(s) placed to match.");
        if (shaped > 0)
        {
            _r.Log.Add($"{shaped} elbow(s) placed with the reference's shape at their view's own anchor - that "
                       + "anchor is read-only, so they sit shifted along the grid. For an exact match, drag "
                       + "those elbows back onto the grid line in Revit and copy again:");
            foreach (string note in _shapedNotes.Take(20))
                _r.Log.Add("    " + note);
            if (_shapedNotes.Count > 20)
                _r.Log.Add($"    ... and {_shapedNotes.Count - 20} more");
        }
        if (kept > 0)
            _r.Log.Add($"{kept} elbow(s) in the target views have no counterpart in the source and were "
                       + "left: the Revit API cannot remove an elbow, so delete those by hand if they "
                       + "should go.");
        return true;
    }

    // ---------------------------------------------------------------- hide / unhide

    /// <summary>
    /// Per view: show its set's hidden grids (unhide) and/or hide the grids
    /// Revit draws there that are not in its set. A grid in the set is never
    /// hidden. Read AFTER the steps ran.
    /// </summary>
    private void RunHide()
    {
        bool hide = _req.HideOthers, show = _req.ShowSet;
        using Transaction t = new(_doc, hide ? "Hide grids not in the set" : "Show the hidden grids of the set");
        ErrorCollector errors = ErrorCollector.Start(t);

        int hidden = 0, shown = 0, inViews = 0;
        Dictionary<long, Grid> everything = AllGrids();

        try
        {
            foreach (Job job in _jobs)
            {
                View view = job.View;
                if (_leftAlone.Contains(Id(view)))
                    continue;   // skipped as a whole, already reported
                if (Copying && Id(view) == Id(_req.CopySource!))
                    continue;   // the view being copied from

                _keepFor.TryGetValue(Id(view), out HashSet<long>? keep);
                if (keep is null)
                {
                    Skip(view.Name, "no grid set is chosen for it, so no grid was "
                                    + (!show ? "hidden" : !hide ? "shown" : "hidden or shown"));
                    continue;
                }

                List<Grid> drawn;
                try
                {
                    drawn = [.. new FilteredElementCollector(_doc, view.Id).OfClass(typeof(Grid)).Cast<Grid>()];
                }
                catch (Exception ex)
                {
                    Skip(view.Name, $"Revit would not say which grids it shows, so none was hidden: {FirstLine(ex)}");
                    continue;
                }

                // the set's own grids that an earlier run hid here are shown again
                List<ElementId> back = [];
                foreach (long key in keep)
                {
                    try
                    {
                        if (everything.TryGetValue(key, out Grid? g) && g.IsHidden(view))
                            back.Add(g.Id);
                    }
                    catch (Exception) { }
                }

                if (back.Count > 0)
                {
                    try
                    {
                        view.UnhideElements(back);
                        shown += back.Count;
                    }
                    catch (Exception ex)
                    {
                        Skip(view.Name, $"grids of its set that were hidden could not be shown again: {FirstLine(ex)}");
                    }
                }

                List<ElementId> ids = [];
                foreach (Grid g in hide ? drawn : [])
                {
                    if (keep.Contains(Id(g)))
                        continue;
                    try
                    {
                        if (!g.CanBeHidden(view))
                        {
                            Skip($"{g.Name} in {view.Name}", "Revit says it cannot be hidden in this view");
                            continue;
                        }
                    }
                    catch (Exception) { }
                    ids.Add(g.Id);
                }

                if (ids.Count == 0)
                {
                    if (back.Count > 0)
                        inViews++;
                    continue;
                }

                try
                {
                    view.HideElements(ids);
                }
                catch (Exception ex)
                {
                    Skip(view.Name, $"the grids outside its set could not be hidden: {FirstLine(ex)}");
                    continue;
                }

                hidden += ids.Count;
                inViews++;
            }
        }
        catch (Exception ex)
        {
            _r.Log.Add($"Hiding stopped early: {FirstLine(ex)}");
        }

        int totalShown = shown + _revealedEarly;
        if (t.GetStatus() != TransactionStatus.Started)
        {
            _r.Log.Add("The hide transaction was already closed - nothing was hidden.");
        }
        else if (hidden == 0 && shown == 0)
        {
            t.RollBack();
            if (hide)
                _r.Log.Add("No grid needed hiding - every grid each view shows is in its set.");
            if (show && _revealedEarly == 0)
                _r.Log.Add("No grid of the set was hidden - nothing to show.");
        }
        else
        {
            if (!Committed(t, errors))
                hidden = shown = 0;
            if (hidden > 0)
                _r.Log.Add($"Hid {hidden} grid(s) that are not in the set, across {inViews} view(s). To "
                           + "bring one back: Reveal Hidden Elements in that view, select it, then "
                           + "Unhide in View.");
        }

        if (totalShown > 0)
            _r.Log.Add($"Showed {totalShown} grid(s) of the set again that had been hidden in those views"
                       + (_revealedEarly > 0 ? " - and adjusted them in this run" : "") + ".");
    }

    // ---------------------------------------------------------------- every grid to 2D

    /// <summary>
    /// Convert every grid of its set each picked view shows to 2D, both ends -
    /// hidden ones too, since hidden they are still 3D and, revealed and
    /// dragged later, would move in every view. Only ends still on the 3D
    /// extent are touched, each at the point the view draws it, so nothing
    /// moves on the sheet. Curved grids cannot be cut at the crop edge and
    /// are reported instead.
    /// </summary>
    private void RunAll2d()
    {
        using Transaction t = new(_doc, "Convert grids to 2D");
        ErrorCollector errors = ErrorCollector.Start(t);
        int converted = 0, inViews = 0;

        try
        {
            foreach (Job job in _jobs)
            {
                View view = job.View;
                if (Copying && Id(view) == Id(_req.CopySource!))
                    continue;   // the reference is only ever read
                if (_leftAlone.Contains(Id(view)))
                    continue;   // skipped as a whole, already reported
                if (!LooksDown(view))
                    continue;   // reported by the steps / the copy

                List<Grid> drawn;
                try
                {
                    drawn = [.. new FilteredElementCollector(_doc, view.Id).OfClass(typeof(Grid)).Cast<Grid>()];
                }
                catch (Exception ex)
                {
                    Skip(view.Name, $"Revit would not say which grids it shows, so none was converted to 2D: {FirstLine(ex)}");
                    continue;
                }

                // Plus the grids HIDDEN in it - the collector leaves them out,
                // but hidden they are still 3D. Only the set's grids can be
                // converted, so with a set only those are asked about: asking
                // every grid in the model was one IsHidden call per grid per
                // view, nearly all of them discarded a few lines below.
                _keepFor.TryGetValue(Id(view), out HashSet<long>? keep);
                HashSet<long> seen = [.. drawn.Select(Id)];
                IEnumerable<Grid> candidates = keep is null
                    ? AllGrids().Values
                    : keep.Select(k => AllGrids().TryGetValue(k, out Grid? g) ? g : null).OfType<Grid>();
                foreach (Grid g in candidates)
                {
                    try
                    {
                        if (!seen.Contains(Id(g)) && g.IsHidden(view))
                            drawn.Add(g);
                    }
                    catch (Exception) { }
                }

                int here = 0;
                foreach (Grid grid in drawn)
                {
                    if (keep is not null && !keep.Contains(Id(grid)))
                        continue;   // not in this view's set: left as it is

                    bool already;
                    try
                    {
                        already = Ends.All(e => grid.GetDatumExtentTypeInView(e, view) == DatumExtentType.ViewSpecific);
                    }
                    catch (Exception)
                    {
                        already = false;
                    }

                    if (already)
                        continue;

                    try
                    {
                        if (grid.Curve is not Line modelLine)
                        {
                            Skip($"{grid.Name} in {view.Name}", "a curved or multi-segment grid - NOT converted to "
                                                                + "2D, still 3D in this view: convert it by hand");
                            continue;
                        }

                        XYZ[]? ends = DrawnEnds(grid, view, modelLine);
                        Line? line = ViewLine(grid, view);
                        if (ends is null || line is null)
                        {
                            Skip($"{grid.Name} in {view.Name}", "Revit returned no line for it, so not converted to 2D");
                            continue;
                        }

                        double z = line.GetEndPoint(0).Z;
                        Write2d(grid, view, Line.CreateBound(AtHeight(ends[0], z), AtHeight(ends[1], z)));
                        here++;
                    }
                    catch (Exception ex)
                    {
                        // one grid, not the rest of the pass
                        Skip($"{grid.Name} in {view.Name}", "could not be converted to 2D, " + FirstLine(ex));
                    }
                }

                converted += here;
                if (here > 0)
                    inViews++;
            }
        }
        catch (Exception ex)
        {
            _r.Log.Add($"Converting to 2D stopped early: {FirstLine(ex)}");
        }

        if (t.GetStatus() != TransactionStatus.Started)
        {
            _r.Log.Add("The 2D transaction was already closed - nothing was converted.");
        }
        else if (converted == 0)
        {
            t.RollBack();
            _r.Log.Add("Every grid of the set in the picked views was already 2D.");
        }
        else if (Committed(t, errors))
        {
            _r.Log.Add($"Converted {converted} grid(s) of the set to 2D across {inViews} view(s) - none of them "
                       + "can be moved in 3D from there.");
        }
    }
}
