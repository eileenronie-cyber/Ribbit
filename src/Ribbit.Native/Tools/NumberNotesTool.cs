using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using Ribbit.Geometry.Text;
using Ribbit.Native.Revit;
using Ribbit.Native.Settings;
using Ribbit.Native.Ui;

namespace Ribbit.Native.Tools;

/// <summary>
/// Number Text Notes (was NumberTextNotes v6.dyn): auto-number the paragraphs
/// of general notes. Text notes selected beforehand are picked up; "Pick
/// notes..." closes the window, picks, and reopens it as it was. Every note goes
/// in ONE transaction - one Ctrl+Z.
/// </summary>
internal static class NumberNotesTool
{
    /// <summary>Tests only: owner for the window, so a test can drive it.</summary>
    internal static System.Windows.Window? TestOwner { get; set; }

    /// <summary>The last run's results (tests read them).</summary>
    internal static List<NoteResult>? LastResults { get; private set; }

    private sealed class NoteFilter : ISelectionFilter
    {
        public bool AllowElement(Element elem) => elem is TextNote;
        public bool AllowReference(Reference reference, XYZ position) => false;
    }

    public static void Run(UIDocument uidoc)
        => Run(uidoc.Document, new RevitPicker(uidoc),
            [.. uidoc.Selection.GetElementIds().Select(uidoc.Document.GetElement)]);

    internal static void Run(Document doc, IPicker picker, IEnumerable<Element?> selected)
    {
        RevitLinks.EnsureInitialized();
        LastResults = null;
        List<TextNote> notes = NumberNotesEngine.NotesIn(selected);
        NumberSettings settings = NumberNotesSettings.Load().ToSettings();
        IReadOnlyDictionary<long, string>? edits = null;

        while (true)
        {
            NumberNotesWindow window = new([.. notes.Select(n => NumberNotesEngine.Describe(doc, n))], settings, edits);
            if (TestOwner is not null)
                window.Owner = TestOwner;
            if (window.ShowDialog() != true || window.Outcome == NotesOutcome.Cancel)
                return;

            settings = window.Settings ?? settings;
            edits = window.Edits;
            NumberNotesSettings.From(settings).Save();

            if (window.Outcome == NotesOutcome.Pick)
            {
                List<Element>? picked = picker.PickElements("Pick text notes, then click Finish", new NoteFilter());
                List<TextNote> got = NumberNotesEngine.NotesIn(picked ?? []);
                if (got.Count > 0)
                    notes = got;
                continue;
            }

            List<NoteResult> results = NumberNotesEngine.Apply(doc, notes, settings, edits, out List<string> errors);
            LastResults = results;
            RibbitUi.ShowReport($"{NumberNotesWindow.DialogTitle} - {results.Count(r => r.Changed)} of {results.Count} note(s) changed",
                Report(results, errors));
            return;
        }
    }

    private static string Report(IReadOnlyList<NoteResult> results, IReadOnlyList<string> errors)
    {
        StringBuilder sb = new();
        List<IReadOnlyList<string>> rows = [.. results.Select(r => (IReadOnlyList<string>)new[]
        {
            ReportHtml.Link(r.Label, new ElementId(r.Id)),
            $"<span class=\"{(r.Status.StartsWith("failed") || r.Status.StartsWith("rolled") ? "skip" : r.Status.Contains("WARNING") ? "warn" : r.Changed ? "ok" : "muted")}\">{ReportHtml.Escape(r.Status)}</span>",
        })];
        ReportHtml.Table(sb, "Notes", ["Note", "Result"], rows);
        if (errors.Count > 0)
            ReportHtml.Note(sb, "Revit reported: " + ReportHtml.Escape(string.Join("; ", errors)));
        ReportHtml.Note(sb, "Ctrl+Z in Revit undoes the whole run.");
        return sb.ToString();
    }
}
