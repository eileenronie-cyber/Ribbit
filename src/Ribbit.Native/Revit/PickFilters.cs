using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using Autodesk.Revit.Exceptions;
using OperationCanceledException = Autodesk.Revit.Exceptions.OperationCanceledException;

namespace Ribbit.Native.Revit;

/// <summary>
/// Where the pick tools (Connect, Align, Disconnect, Elbow) get their clicks.
///
/// In Revit it is <see cref="RevitPicker"/>, the user's own mouse. The in-Revit
/// test suite hands the same tool loops a scripted picker instead, so the loop
/// itself - prompts, Esc, Multi counting, retry after a failure - is tested
/// rather than only the geometry under it. Esc is (null, null) / null.
/// </summary>
internal interface IPicker
{
    (Element? Element, XYZ? Click) PickElement(string prompt, ISelectionFilter filter);

    XYZ? PickPoint(string prompt);

    /// <summary>
    /// Several elements at once (Revit's pick-then-Finish), raw - not host
    /// resolved. Null on Esc.
    /// </summary>
    List<Element>? PickElements(string prompt, ISelectionFilter? filter = null);

    /// <summary>
    /// How much of the view is on screen, in feet along the view's right/up
    /// directions (the larger of the two), or null when it cannot be read.
    /// </summary>
    double? VisibleExtent(View view, XYZ right, XYZ up);

    /// <summary>
    /// Shows Elbow's direction dial in <paramref name="view"/> until the
    /// returned handle is disposed. A null dial shows nothing.
    /// </summary>
    IDisposable ShowDial(View view, Ribbit.Geometry.ElbowDial? dial);
}

/// <summary>The real mouse: Revit's own pick prompts in the active view.</summary>
internal sealed class RevitPicker(UIDocument uidoc) : IPicker
{
    public (Element? Element, XYZ? Click) PickElement(string prompt, ISelectionFilter filter)
        => Picking.PickElement(uidoc, uidoc.Document, prompt, filter);

    public XYZ? PickPoint(string prompt)
    {
        try
        {
            return uidoc.Selection.PickPoint(prompt);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    public List<Element>? PickElements(string prompt, ISelectionFilter? filter = null)
    {
        try
        {
            IList<Reference> refs = filter is null
                ? uidoc.Selection.PickObjects(ObjectType.Element, prompt)
                : uidoc.Selection.PickObjects(ObjectType.Element, filter, prompt);
            return [.. refs.Select(r => uidoc.Document.GetElement(r.ElementId))];
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// Only the right/up components of the zoom corners are used:
    /// GetZoomCorners() reports unreliable Z in orthographic projection.
    /// </summary>
    public double? VisibleExtent(View view, XYZ right, XYZ up)
    {
        try
        {
            foreach (UIView uiview in uidoc.GetOpenUIViews())
            {
                if (uiview.ViewId != view.Id)
                    continue;

                IList<XYZ> corners = uiview.GetZoomCorners();
                if (corners is null || corners.Count < 2)
                    return null;

                XYZ span = corners[1].Subtract(corners[0]);
                return Math.Max(Math.Abs(span.DotProduct(right)), Math.Abs(span.DotProduct(up)));
            }
        }
        catch (Autodesk.Revit.Exceptions.ApplicationException)
        {
            // Unreadable: the caller falls back to a size-based dead zone.
        }

        return null;
    }

    public IDisposable ShowDial(View view, Ribbit.Geometry.ElbowDial? dial)
        => dial is null ? ElbowDialServer.NoDial : ElbowDialServer.Show(uidoc, view, dial);
}

/// <summary>
/// Picking helpers shared by the Move/Connect/Align and Align Branch tools.
/// Ported from lib/mepalignlib/common.py.
/// </summary>
internal static class Picking
{
    /// <summary>
    /// If elem is pipe/duct insulation or lining (no geometry of its own),
    /// swap in the pipe/duct/fitting it wraps.
    /// </summary>
    public static Element ResolveHost(Document doc, Element elem)
    {
        if (elem is InsulationLiningBase lining)
        {
            ElementId hostId = lining.HostElementId;
            if (hostId != ElementId.InvalidElementId)
            {
                Element? host = doc.GetElement(hostId);
                if (host is not null)
                    return host;
            }
        }
        return elem;
    }

    public static string DescribeElem(Element elem)
    {
        string cat = elem.Category?.Name ?? "NoCategory";
        return $"{elem.Id} [{cat} / {elem.GetType().Name}]";
    }

    /// <summary>
    /// Pick one element. Returns (null, null) on Esc. The click point is the
    /// 3D location the user actually clicked, used to decide which end of a
    /// run they meant.
    /// </summary>
    public static (Element? Element, XYZ? Click) PickElement(
        UIDocument uidoc, Document doc, string prompt, ISelectionFilter selFilter)
    {
        Reference reference;
        try
        {
            reference = uidoc.Selection.PickObject(ObjectType.Element, selFilter, prompt);
        }
        catch (OperationCanceledException)
        {
            return (null, null);
        }

        Element? elem = doc.GetElement(reference.ElementId);

        XYZ? click = null;
        try
        {
            click = reference.GlobalPoint;
        }
        catch (Autodesk.Revit.Exceptions.ApplicationException)
        {
            // Not every reference carries a global point; the callers all
            // treat a missing click as "no hint", so this is not an error.
        }

        return (elem, click);
    }
}

/// <summary>
/// Pick filter that stays cheap under continuous hover callbacks.
///
/// Revit calls AllowElement for every element the cursor passes over, for as
/// long as a pick is active — so anything expensive in here makes the whole
/// view feel laggy (and makes Esc look slow to register). Two guards keep it
/// light:
///
///   1. a non-model-category reject, before any document lookup;
///   2. a per-element-id memo, reused across every pick of the command.
///
/// Subclasses implement <see cref="Test"/> and must only test something that
/// cannot change while the command runs, or the memo would go stale.
///
/// Reuse ONE instance for the whole command and just reassign the exclude key
/// between picks — that keeps the memo warm.
/// </summary>
internal abstract class CachedPickFilter : ISelectionFilter
{
    protected readonly Document Doc;
    private readonly Dictionary<long, bool> _cache = [];
    private long? _excludeKey;

    protected CachedPickFilter(Document doc) => Doc = doc;

    /// <summary>
    /// Bar one element from the next pick (e.g. the fixed element), along with
    /// anything that resolves to it.
    ///
    /// The id is resolved to its host HERE rather than relying on callers to do
    /// it. They do — but "remember to resolve before excluding" is an unwritten
    /// contract, and the failure it causes is silent: pass an insulation id and
    /// the pipe it wraps stays pickable, which is the whole bug this guards
    /// against. Resolving both ends makes the filter correct whatever it is
    /// handed.
    /// </summary>
    public void Exclude(ElementId? elemId)
    {
        if (elemId is null)
        {
            _excludeKey = null;
            return;
        }

        Element? elem = Doc.GetElement(elemId);
        _excludeKey = RevitCompat.IdKey(
            elem is null ? elemId : Picking.ResolveHost(Doc, elem).Id);
    }

    protected abstract bool Test(Element elem);

    public bool AllowElement(Element elem)
    {
        try
        {
            long key = RevitCompat.IdKey(elem.Id);
            if (_excludeKey is not null && key == _excludeKey)
                return false;

            // AND the same test against what this element RESOLVES to.
            //
            // Insulation and lining carry their own ElementId but stand in for
            // the pipe or duct they wrap, and every caller resolves that host
            // AFTER picking. Comparing raw ids alone therefore let the two picks
            // land on the same element by different routes: pick a duct, then
            // pick its insulation, and both become the duct. Live-confirmed —
            // insulation 73619832 and host 73285829 are different ids that both
            // pass this filter and resolve to one element.
            if (_excludeKey is not null
                && RevitCompat.IdKey(Picking.ResolveHost(Doc, elem).Id) == _excludeKey)
            {
                return false;
            }

            if (!_cache.TryGetValue(key, out bool allowed))
            {
                Category? cat = elem.Category;
                allowed = cat is not null
                          && cat.CategoryType == CategoryType.Model
                          && Test(elem);
                _cache[key] = allowed;
            }
            return allowed;
        }
        catch (Exception)
        {
            // Mirrors the Python filter: a filter that throws mid-hover would
            // abort the user's pick, so anything unexpected means "not pickable".
            return false;
        }
    }

    public bool AllowReference(Reference reference, XYZ point) => true;
}
