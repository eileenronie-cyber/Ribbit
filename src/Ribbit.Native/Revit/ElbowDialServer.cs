using Autodesk.Revit.DB;
using Autodesk.Revit.DB.DirectContext3D;
using Autodesk.Revit.DB.ExternalService;
using Autodesk.Revit.UI;
using Ribbit.Geometry;

namespace Ribbit.Native.Revit;

/// <summary>
/// Paints an <see cref="ElbowDial"/> in ONE view with Revit's transient
/// graphics (DirectContext3D) while Elbow (Click Dir) waits for the direction
/// click.
///
/// Transient on purpose: nothing is added to the model, nothing lands in Undo,
/// and it draws in a 3D view, where a detail line could not be drawn at all (the
/// tool shows it in 3D views only). <see cref="Show"/> registers it and returns the
/// handle whose Dispose takes it off screen again - the caller disposes in a
/// finally, so an Esc or an exception clears it.
/// </summary>
internal sealed class ElbowDialServer : IDirectContext3DServer, IDisposable
{
    private static readonly Guid Id = new("6f0f6a4e-2b1c-4d8e-9a55-e1b0c0d1a7e2");

    private readonly ElbowDial _dial;
    private readonly ElementId _viewId;
    private readonly string _docKey;
    private VertexBuffer? _vb;
    private IndexBuffer? _ib;
    private int _vertices, _lines;

    internal ElbowDialServer(ElbowDial dial, View view)
    {
        _dial = dial;
        _viewId = view.Id;
        _docKey = view.Document.PathName + "|" + view.Document.Title;
    }

    /// <summary>
    /// The colour of each kind of stroke: teal for a sideways turn and its plane,
    /// orange for an up/down turn and its plane (the planes paler), grey for the
    /// click-again circle. Size is told by the dash, not by colour.
    /// </summary>
    internal static ColorWithTransparency Colour(DialInk ink) => ink switch
    {
        DialInk.Sideways90 or DialInk.Sideways45 => new ColorWithTransparency(15, 140, 105, 0),
        DialInk.SidewaysPlane => new ColorWithTransparency(120, 200, 175, 0),
        DialInk.UpDown90 or DialInk.UpDown45 => new ColorWithTransparency(215, 85, 35, 0),
        DialInk.UpDownPlane => new ColorWithTransparency(240, 165, 135, 0),
        _ => new ColorWithTransparency(120, 120, 120, 0),
    };

    // ------------------------------------------------------------ show / hide

    /// <summary>
    /// Puts the dial on screen in <paramref name="uidoc"/>'s active view, and
    /// returns what takes it off again. Never throws: a dial that cannot be
    /// shown costs the user a hint, not the elbow.
    /// </summary>
    public static IDisposable Show(UIDocument uidoc, View view, ElbowDial dial)
    {
        if (!Register(view, dial))
            return Nothing.Instance;

        try { uidoc.RefreshActiveView(); } catch (Exception) { }
        return new Handle(uidoc);
    }

    /// <summary>The handle for "no dial": disposing it does nothing.</summary>
    internal static IDisposable NoDial => Nothing.Instance;

    /// <summary>Registers and activates the painter for this dial. False if Revit would not take it.</summary>
    internal static bool Register(View view, ElbowDial dial)
    {
        try
        {
            MultiServerService? svc = Service();
            if (svc is null)
                return false;

            if (svc.IsRegisteredServerId(Id))
                Hide();   // a leftover from a run that could not tidy up

            svc.AddServer(new ElbowDialServer(dial, view));
            List<Guid> active = [.. svc.GetActiveServerIds()];
            if (!active.Contains(Id))
                active.Add(Id);
            svc.SetActiveServers(active);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Is the dial registered AND active right now? (The test's show/leak check.)</summary>
    internal static bool IsActive => Service() is { } s && s.IsRegisteredServerId(Id) && s.GetActiveServerIds().Contains(Id);

    internal static void Hide()
    {
        try
        {
            MultiServerService? svc = Service();
            if (svc is null || !svc.IsRegisteredServerId(Id))
                return;

            // Taken before removal: once unregistered, Revit no longer hands it out, and its
            // vertex and index buffers (native graphics memory) would never be released.
            IExternalServer? server = svc.GetServer(Id);

            List<Guid> active = [.. svc.GetActiveServerIds()];
            if (active.Remove(Id))
                svc.SetActiveServers(active);
            svc.RemoveServer(Id);

            (server as IDisposable)?.Dispose();
        }
        catch (Exception)
        {
            // Nothing more can be done; a stale dial is cleared by the next Show.
        }
    }

    private static MultiServerService? Service()
        => ExternalServiceRegistry.GetService(ExternalServices.BuiltInExternalServices.DirectContext3DService) as MultiServerService;

    private sealed class Handle(UIDocument uidoc) : IDisposable
    {
        private bool _done;

        public void Dispose()
        {
            if (_done)
                return;
            _done = true;
            Hide();
            try { uidoc.RefreshActiveView(); } catch (Exception) { }
        }
    }

    private sealed class Nothing : IDisposable
    {
        public static readonly Nothing Instance = new();

        public void Dispose() { }
    }

    /// <summary>
    /// Releases the buffers. Called by <see cref="Hide"/> after the server is unregistered, so
    /// Revit can no longer ask it to draw with them.
    /// </summary>
    public void Dispose()
    {
        _vb?.Dispose();
        _ib?.Dispose();
        _vb = null;
        _ib = null;
    }

    // ------------------------------------------------------------ IExternalServer

    public Guid GetServerId() => Id;
    public ExternalServiceId GetServiceId() => ExternalServices.BuiltInExternalServices.DirectContext3DService;
    public string GetName() => "Ribbit Elbow direction dial";
    public string GetVendorId() => "Ribbit";
    public string GetDescription() => "Shows which way Elbow (Click Dir) will turn for a click in each direction.";

    // ------------------------------------------------------------ IDirectContext3DServer

    public string GetApplicationId() => "";
    public string GetSourceId() => "";
    public bool UsesHandles() => false;
    public bool UseInTransparentPass(View view) => false;

    /// <summary>Only in the view the pick is happening in, of the document it belongs to.</summary>
    public bool CanExecute(View view)
        => view.Id == _viewId && view.Document.PathName + "|" + view.Document.Title == _docKey;

    public Outline GetBoundingBox(View view)
    {
        // The furthest target circle reaches MaxReach + TargetSize; the planes only the radius.
        double r = _dial.Radius * (ElbowDial.MaxReach + ElbowDial.TargetSize) * 1.05;
        XYZ o = new(_dial.Origin.X, _dial.Origin.Y, _dial.Origin.Z);
        return new Outline(o - new XYZ(r, r, r), o + new XYZ(r, r, r));
    }

    public void RenderScene(View view, DisplayStyle displayStyle)
    {
        try
        {
            if (_vb is null || _ib is null || !_vb.IsValid() || !_ib.IsValid())
                Build();

            DrawContext.FlushBuffer(_vb, _vertices, _ib, _lines * IndexLine.GetSizeInShortInts(),
                new VertexFormat(VertexFormatBits.PositionColored), new EffectInstance(VertexFormatBits.PositionColored),
                PrimitiveType.LineList, 0, _lines);
        }
        catch (Exception)
        {
            // A paint that fails must never take the view down with it.
        }
    }

    private void Build()
    {
        List<DialStroke> strokes = _dial.Strokes;
        _lines = strokes.Count;
        _vertices = _lines * 2;

        _vb = new VertexBuffer(_vertices * VertexPositionColored.GetSizeInFloats());
        _vb.Map(_vertices * VertexPositionColored.GetSizeInFloats());
        VertexStreamPositionColored vs = _vb.GetVertexStreamPositionColored();
        foreach (DialStroke s in strokes)
        {
            ColorWithTransparency c = Colour(s.Ink);
            vs.AddVertex(new VertexPositionColored(new XYZ(s.A.X, s.A.Y, s.A.Z), c));
            vs.AddVertex(new VertexPositionColored(new XYZ(s.B.X, s.B.Y, s.B.Z), c));
        }

        _vb.Unmap();

        _ib = new IndexBuffer(_lines * IndexLine.GetSizeInShortInts());
        _ib.Map(_lines * IndexLine.GetSizeInShortInts());
        IndexStreamLine ls = _ib.GetIndexStreamLine();
        for (int i = 0; i < _lines; i++)
            ls.AddLine(new IndexLine(2 * i, (2 * i) + 1));
        _ib.Unmap();
    }
}
