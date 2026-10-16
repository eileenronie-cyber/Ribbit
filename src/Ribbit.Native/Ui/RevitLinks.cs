using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Ribbit.Native.Revit;

namespace Ribbit.Native.Ui;

/// <summary>
/// The C# replacement for pyRevit's output.linkify().
///
/// The report window is modeless — the whole point is that it stays open while
/// you work the model — which means a link click arrives OUTSIDE Revit's API
/// context and cannot touch the document directly. So clicks are marshalled
/// back in through an ExternalEvent, which is the only supported way to reach
/// the API from a modeless window.
/// </summary>
internal sealed class SelectElementsHandler : IExternalEventHandler
{
    private readonly object _gate = new();
    private List<ElementId> _pending = [];

    public void Request(IEnumerable<ElementId> ids)
    {
        lock (_gate)
            _pending = [.. ids];
    }

    public void Execute(UIApplication app)
    {
        List<ElementId> ids;
        lock (_gate)
            ids = _pending;

        if (ids.Count == 0)
            return;

        UIDocument? uidoc = app.ActiveUIDocument;
        if (uidoc is null)
            return;

        try
        {
            // SELECT ONLY, deliberately.
            //
            // ShowElements used to run here as well. It zooms, and can switch
            // view — deciding for you where to look and discarding whatever you
            // had framed, which is worse the more clashes you click through.
            // Selecting alone leaves the view untouched and hands the elements
            // straight to Selection Box (BX) or Isolate, which is what you would
            // reach for next anyway.
            uidoc.Selection.SetElementIds(ids);
        }
        catch (Exception)
        {
            // The report can outlive the elements it names (undo, or a
            // different document fronted). A dead link should do nothing, not
            // tear down the handler for every later click.
        }
    }

    public string GetName() => "Ribbit report element selection";
}

internal static class RevitLinks
{
    /// <summary>URI scheme the report's element links use.</summary>
    public const string Scheme = "ribbit";

    private static SelectElementsHandler? _handler;
    private static ExternalEvent? _event;

    /// <summary>
    /// Must be called from a valid API context (a command's Execute, or
    /// OnStartup) before any report window is shown — ExternalEvent.Create
    /// cannot run from arbitrary threads.
    /// </summary>
    public static void EnsureInitialized()
    {
        if (_event is not null)
            return;

        _handler = new SelectElementsHandler();
        _event = ExternalEvent.Create(_handler);
    }

    /// <summary>Href for a link that selects these elements when clicked.</summary>
    public static string HrefFor(IEnumerable<ElementId> ids)
        => $"{Scheme}:select?ids=" + string.Join(",", ids.Select(id => RevitCompat.IdKey(id)));

    /// <summary>
    /// Handles a clicked report link. Returns true if the URI was ours (and so
    /// the browser navigation should be cancelled).
    /// </summary>
    public static bool TryHandle(string uri)
    {
        if (!uri.StartsWith(Scheme + ":", StringComparison.OrdinalIgnoreCase))
            return false;

        if (_handler is null || _event is null)
            return true;   // ours, but not wired up - swallow rather than navigate

        int idx = uri.IndexOf("ids=", StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
            return true;

        List<ElementId> ids = [];
        foreach (string part in uri[(idx + 4)..].Split(','))
        {
            if (long.TryParse(part, out long value))
                ids.Add(RevitCompat.IdFrom(value));
        }

        if (ids.Count > 0)
        {
            _handler.Request(ids);
            _event.Raise();
        }
        return true;
    }

    /// <summary>
    /// Selects these elements, for callers that are not an HTML link.
    ///
    /// The report goes through <see cref="TryHandle"/> because a WebView2 page
    /// can only talk back in URIs. A WPF window has no such constraint, so it
    /// calls this rather than building a uri for something to parse straight
    /// back apart.
    ///
    /// Selects only — no zoom, no view change, same as the report. See
    /// <see cref="SelectElementsHandler.Execute"/>.
    /// </summary>
    public static void Select(IEnumerable<long> ids)
    {
        if (_handler is null || _event is null)
            return;

        List<ElementId> list = [.. ids.Select(RevitCompat.IdFrom)];
        if (list.Count == 0)
            return;

        _handler.Request(list);
        _event.Raise();
    }
}
