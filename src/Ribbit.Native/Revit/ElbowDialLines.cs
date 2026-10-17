using Autodesk.Revit.DB;
using Ribbit.Geometry;

namespace Ribbit.Native.Revit;

/// <summary>
/// Draws an <see cref="ElbowDial"/> in ONE 3D view as temporary MODEL LINES while
/// Elbow (Click Dir) waits for the direction click, and deletes them again when the
/// returned handle is disposed.
///
/// WHY NOT TRANSIENT GRAPHICS (DirectContext3D), which the dial used until 2026-10-17.
/// Proven live on Revit 2026 with an Intel Iris Xe (DirectX 11): while ANY
/// DirectContext3D server is active in a view, that view stops erasing between frames -
/// a drag leaves a trail of rubber-band rectangles and a zoom leaves the dial repeated
/// like an array. A server that drew nothing trailed the same, and so did one claiming
/// the whole view in both the opaque and the transparent pass. Model lines are ordinary
/// geometry and redraw like everything else.
///
/// They are real elements, so they are made inside the command's transaction group: Esc
/// or a failure rolls them back with the group, and Dispose deletes them (with the sketch
/// planes made for them) before the elbow is built, so a finished elbow is still ONE undo
/// step and leaves nothing behind. Colour and weight are per-element overrides in this
/// view only. A view that hides the Lines category gets it shown for the pick and hidden
/// again; a view whose TEMPLATE hides Lines cannot show the dial (<see cref="CanDraw"/>),
/// and the click still works there, without the picture.
/// </summary>
internal static class ElbowDialLines
{
    private static readonly ElementId LinesCategory = new(BuiltInCategory.OST_Lines);

    /// <summary>Teal for a sideways turn, orange for up or down (their planes paler), grey for the click-again circle.</summary>
    internal static Color Colour(DialInk ink) => ink switch
    {
        DialInk.Sideways90 or DialInk.Sideways45 => new Color(15, 140, 105),
        DialInk.SidewaysPlane => new Color(120, 200, 175),
        DialInk.UpDown90 or DialInk.UpDown45 => new Color(215, 85, 35),
        DialInk.UpDownPlane => new Color(240, 165, 135),
        _ => new Color(120, 120, 120),
    };

    /// <summary>Arrows and targets drawn heavy enough to find at a glance; planes and the click-again circle thin.</summary>
    internal static int Weight(DialInk ink) => ink is DialInk.SidewaysPlane or DialInk.UpDownPlane or DialInk.ClickAgain ? 1 : 5;

    /// <summary>False only when the view's template hides Lines: the dial cannot be shown there.</summary>
    public static bool CanDraw(View view) => !view.GetCategoryHidden(LinesCategory) || !TemplateControlsVisibility(view);

    private static bool TemplateControlsVisibility(View view)
    {
        if (view.ViewTemplateId == ElementId.InvalidElementId || view.Document.GetElement(view.ViewTemplateId) is not View template)
            return false;
        return !template.GetNonControlledTemplateParameterIds().Contains(new ElementId(BuiltInParameter.VIS_GRAPHICS_MODEL));
    }

    /// <summary>
    /// Draws the dial and returns what deletes it. Never throws: a dial that cannot be drawn
    /// costs the user the picture, not the elbow.
    /// </summary>
    public static IDisposable Show(Document doc, View view, ElbowDial dial)
    {
        if (!CanDraw(view))
            return Nothing.Instance;

        List<ElementId> made = [];
        bool unhid = false;
        try
        {
            using Transaction t = new(doc, "Elbow By Click - Show Dial");
            ErrorCollector.Start(t);
            if (view.GetCategoryHidden(LinesCategory))
            {
                view.SetCategoryHidden(LinesCategory, false);
                unhid = true;
            }

            XYZ origin = dial.Origin.ToXYZ();
            double shortest = doc.Application.ShortCurveTolerance * 1.5;
            Dictionary<DialInk, OverrideGraphicSettings> looks = [];

            // Everything but the two turn planes lies in the screen plane through the open
            // end; each turn plane is its own plane through it, found from its first chord.
            foreach (IGrouping<DialInk, DialStroke> group in dial.Strokes.GroupBy(s => s.Ink))
            {
                XYZ normal = group.Key is DialInk.SidewaysPlane or DialInk.UpDownPlane
                    ? (group.First().A.ToXYZ() - origin).CrossProduct(group.First().B.ToXYZ() - origin).Normalize()
                    : view.ViewDirection;
                SketchPlane plane = SketchPlane.Create(doc, Plane.CreateByNormalAndOrigin(normal, origin));
                made.Add(plane.Id);

                CurveArray curves = new();
                foreach (DialStroke s in group)
                {
                    XYZ a = s.A.ToXYZ(), b = s.B.ToXYZ();
                    if (a.DistanceTo(b) > shortest)
                        curves.Append(Line.CreateBound(a, b));
                }

                if (curves.IsEmpty)
                    continue;

                if (!looks.TryGetValue(group.Key, out OverrideGraphicSettings? look))
                {
                    look = new OverrideGraphicSettings()
                        .SetProjectionLineColor(Colour(group.Key))
                        .SetProjectionLineWeight(Weight(group.Key));
                    looks[group.Key] = look;
                }

                foreach (ModelCurve mc in doc.Create.NewModelCurveArray(curves, plane))
                {
                    made.Add(mc.Id);
                    view.SetElementOverrides(mc.Id, look);
                }
            }

            if (t.Commit() != TransactionStatus.Committed)
                return Nothing.Instance;
        }
        catch (Exception)
        {
            return Nothing.Instance;
        }

        return new Handle(doc, view, made, unhid);
    }

    /// <summary>Deletes what <see cref="Show"/> made, and hides Lines again if it showed them.</summary>
    private sealed class Handle(Document doc, View view, List<ElementId> made, bool unhid) : IDisposable
    {
        private bool _done;

        public void Dispose()
        {
            if (_done)
                return;
            _done = true;
            try
            {
                using Transaction t = new(doc, "Elbow By Click - Hide Dial");
                ErrorCollector.Start(t);
                List<ElementId> left = [.. made.Where(id => doc.GetElement(id) is not null)];
                if (left.Count > 0)
                    doc.Delete(left);
                if (unhid)
                    view.SetCategoryHidden(LinesCategory, true);
                t.Commit();
            }
            catch (Exception)
            {
                // Nothing more to do here: on every path but success the command's group
                // rolls the lines back anyway, and the suite checks none survive a success.
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
