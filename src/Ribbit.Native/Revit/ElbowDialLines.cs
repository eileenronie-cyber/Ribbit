using Autodesk.Revit.DB;
using Ribbit.Geometry;

namespace Ribbit.Native.Revit;

/// <summary>
/// Draws an <see cref="ElbowDial"/> in ONE 3D view as temporary MODEL LINES while
/// Elbow (Click Dir) waits for the direction click, and takes them away again when
/// the returned handle is disposed.
///
/// WHY NOT TRANSIENT GRAPHICS (DirectContext3D), which the dial used until 2026-10-17.
/// Proven live on Revit 2026 with an Intel Iris Xe (DirectX 11): while ANY
/// DirectContext3D server is active in a view, that view stops erasing between frames -
/// a drag leaves a trail of rubber-band rectangles and a zoom leaves the dial repeated
/// like an array. A server that drew nothing trailed the same, and so did one claiming
/// the whole view in both the opaque and the transparent pass. Model lines are ordinary
/// geometry and redraw like everything else.
///
/// TAKEN AWAY BY ROLLING BACK, NOT BY DELETING. The lines are drawn inside a transaction
/// group of their own that Dispose rolls back, which restores the model exactly - the
/// lines, their sketch planes, a Lines category shown for the pick - and is faster than
/// a delete. Deleting was not exact: in a workshared model Revit adds a companion plane
/// for every sketch plane made, and a delete left those behind (proven on a workshared
/// project, 2026-10-17). Nothing may start a transaction between Show and Dispose
/// (the caller only waits for a click in between).
///
/// SPEED: circles are drawn as arcs, so a dial is a few dozen elements; as straight
/// segments it was 322, and drawing them took 867 ms on a large project.
///
/// Colour and weight are per-element overrides in this view only. A view whose
/// TEMPLATE hides Lines cannot show the dial (<see cref="CanDraw"/>); the click still
/// works there, without the picture.
/// </summary>
internal static class ElbowDialLines
{
    private static readonly ElementId LinesCategory = new(BuiltInCategory.OST_Lines);

    /// <summary>
    /// Teal for a sideways turn, orange for up or down; a 90 in the deep shade, a 45
    /// in the pale one (and dashed). The planes are paler still, the click-again circle grey.
    /// </summary>
    internal static Color Colour(DialInk ink) => ink switch
    {
        DialInk.Sideways90 => new Color(0, 115, 85),
        DialInk.Sideways45 => new Color(95, 195, 160),
        DialInk.UpDown90 => new Color(200, 65, 10),
        DialInk.UpDown45 => new Color(245, 155, 105),
        DialInk.SidewaysPlane => new Color(175, 222, 205),
        DialInk.UpDownPlane => new Color(248, 205, 182),
        _ => new Color(120, 120, 120),
    };

    /// <summary>A 90 heaviest, a 45 a step lighter; planes and the click-again circle thin.</summary>
    internal static int Weight(DialInk ink) => ink switch
    {
        DialInk.Sideways90 or DialInk.UpDown90 => 5,
        DialInk.Sideways45 or DialInk.UpDown45 => 4,
        _ => 1,
    };

    /// <summary>A dashed circle is cut into this many pieces, and every other one is drawn.</summary>
    internal const int DashedPieces = 24;

    /// <summary>False only when the view's template hides Lines: the dial cannot be shown there.</summary>
    public static bool CanDraw(View view) => !view.GetCategoryHidden(LinesCategory) || !TemplateControlsVisibility(view);

    private static bool TemplateControlsVisibility(View view)
    {
        if (view.ViewTemplateId == ElementId.InvalidElementId || view.Document.GetElement(view.ViewTemplateId) is not View template)
            return false;
        return !template.GetNonControlledTemplateParameterIds().Contains(new ElementId(BuiltInParameter.VIS_GRAPHICS_MODEL));
    }

    /// <summary>The arcs one circle is drawn as: two halves, or (dashed) every other of 24 pieces.</summary>
    internal static List<Arc> Arcs(DialCircle c)
    {
        XYZ centre = c.Centre.ToXYZ(), e1 = c.E1.ToXYZ(), e2 = c.E2.ToXYZ();
        List<Arc> arcs = [];
        if (!c.Dashed)
        {
            arcs.Add(Arc.Create(centre, c.Radius, 0, Math.PI, e1, e2));
            arcs.Add(Arc.Create(centre, c.Radius, Math.PI, 2 * Math.PI, e1, e2));
            return arcs;
        }

        for (int i = 0; i < DashedPieces; i += 2)
            arcs.Add(Arc.Create(centre, c.Radius, 2 * Math.PI * i / DashedPieces, 2 * Math.PI * (i + 1) / DashedPieces, e1, e2));
        return arcs;
    }

    /// <summary>
    /// Draws the dial and returns what takes it away. Never throws: a dial that cannot be
    /// drawn costs the user the picture, not the elbow.
    /// </summary>
    public static IDisposable Show(Document doc, View view, ElbowDial dial)
    {
        if (!CanDraw(view))
            return Nothing.Instance;

        TransactionGroup group = new(doc, "Elbow By Click - Dial");
        try
        {
            group.Start();
            using (Transaction t = new(doc, "Elbow By Click - Show Dial"))
            {
                ErrorCollector.Start(t);
                if (view.GetCategoryHidden(LinesCategory))
                    view.SetCategoryHidden(LinesCategory, false);

                Draw(doc, view, dial);

                if (t.Commit() != TransactionStatus.Committed)
                    throw new InvalidOperationException("the dial's lines were rolled back");
            }

            return new Handle(group);
        }
        catch (Exception)
        {
            if (group.HasStarted() && !group.HasEnded())
                group.RollBack();
            group.Dispose();
            return Nothing.Instance;
        }
    }

    private static void Draw(Document doc, View view, ElbowDial dial)
    {
        XYZ origin = dial.Origin.ToXYZ();
        double shortest = doc.Application.ShortCurveTolerance * 1.5;

        // Three sketch planes at most - the screen plane through the open end, and
        // each turn plane - so in a workshared model only three companions appear.
        SketchPlane screen = SketchPlane.Create(doc, Plane.CreateByNormalAndOrigin(view.ViewDirection, origin));
        Dictionary<DialInk, SketchPlane> planes = [];
        SketchPlane PlaneOf(DialCircle c)
        {
            if (c.Ink is not (DialInk.SidewaysPlane or DialInk.UpDownPlane))
                return screen;
            if (!planes.TryGetValue(c.Ink, out SketchPlane? sp))
            {
                sp = SketchPlane.Create(doc, Plane.CreateByOriginAndBasis(c.Centre.ToXYZ(), c.E1.ToXYZ(), c.E2.ToXYZ()));
                planes[c.Ink] = sp;
            }

            return sp;
        }

        // Collected per plane and made in ONE call each - three calls for the whole
        // dial. On a large project every creation call costs tens of milliseconds
        // whatever it holds (a NewModelCurve per curve took 4.1 s for 62 curves, one
        // call per plane and ink 610 ms), so the colours are set afterwards, from
        // the ink kept beside each curve.
        Dictionary<SketchPlane, (CurveArray Curves, List<DialInk> Inks)> batches = [];
        void Add(Curve curve, SketchPlane on, DialInk ink)
        {
            if (!batches.TryGetValue(on, out (CurveArray Curves, List<DialInk> Inks) batch))
            {
                batch = (new CurveArray(), []);
                batches[on] = batch;
            }

            batch.Curves.Append(curve);
            batch.Inks.Add(ink);
        }

        foreach (DialStroke s in dial.Strokes)
        {
            XYZ a = s.A.ToXYZ(), b = s.B.ToXYZ();
            if (a.DistanceTo(b) > shortest)
                Add(Line.CreateBound(a, b), screen, s.Ink);
        }

        foreach (DialCircle c in dial.Circles)
        {
            if (Math.PI * c.Radius / DashedPieces <= shortest)
                continue;   // too small to draw at this zoom
            SketchPlane on = PlaneOf(c);
            foreach (Arc arc in Arcs(c))
                Add(arc, on, c.Ink);
        }

        Dictionary<DialInk, OverrideGraphicSettings> looks = [];
        foreach (KeyValuePair<SketchPlane, (CurveArray Curves, List<DialInk> Inks)> batch in batches)
        {
            ModelCurveArray made = doc.Create.NewModelCurveArray(batch.Value.Curves, batch.Key);
            for (int i = 0; i < made.Size; i++)
            {
                DialInk ink = batch.Value.Inks[i];
                if (!looks.TryGetValue(ink, out OverrideGraphicSettings? look))
                {
                    look = new OverrideGraphicSettings().SetProjectionLineColor(Colour(ink)).SetProjectionLineWeight(Weight(ink));
                    looks[ink] = look;
                }

                view.SetElementOverrides(made.get_Item(i).Id, look);
            }
        }
    }

    /// <summary>Rolls the dial's group back: every line, plane and visibility change, exactly.</summary>
    private sealed class Handle(TransactionGroup group) : IDisposable
    {
        public void Dispose()
        {
            try
            {
                if (group.HasStarted() && !group.HasEnded())
                    group.RollBack();   // takes the dial away, exactly
            }
            catch (Exception)
            {
                // On every path but success the command's own group rolls back too.
            }
            finally
            {
                group.Dispose();
            }
        }
    }

    private sealed class Nothing : IDisposable
    {
        public static readonly Nothing Instance = new();

        public void Dispose() { }
    }

    /// <summary>The handle for "no dial": disposing it does nothing.</summary>
    internal static IDisposable NoDial => Nothing.Instance;
}
