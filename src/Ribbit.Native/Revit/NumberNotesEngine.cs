using Autodesk.Revit.DB;
using Ribbit.Geometry.Text;

namespace Ribbit.Native.Revit;

/// <summary>One text note as the window sees it: no Revit object, so the window needs no document.</summary>
internal sealed record NoteInfo(long Id, string Label, string Plain);

/// <summary>What happened to one note.</summary>
internal sealed record NoteResult(long Id, string Label, bool Changed, string Status);

/// <summary>
/// Writes Number Text Notes' plans into Revit: the text, the list setting, and
/// the bold / italic / underline the note had.
///
/// SetPlainText does not keep formatting per paragraph - Revit gives ALL the
/// new text the formatting of the first character, so a bold, underlined
/// "NOTES" title made the whole note bold. So the header's and the body's
/// styles are read first and set explicitly afterwards (the graph's rule).
/// A note whose text is already right only has its list setting written, and
/// keeps every bold word it had.
/// </summary>
internal static class NumberNotesEngine
{
    private static readonly (string Name, Func<FormattedText, TextRange, FormatStatus> Get, Action<FormattedText, TextRange, bool> Set)[] Styles =
    [
        ("Bold", (f, r) => f.GetBoldStatus(r), (f, r, v) => f.SetBoldStatus(r, v)),
        ("Italic", (f, r) => f.GetItalicStatus(r), (f, r, v) => f.SetItalicStatus(r, v)),
        ("Underline", (f, r) => f.GetUnderlineStatus(r), (f, r, v) => f.SetUnderlineStatus(r, v)),
    ];

    public static NoteInfo Describe(Document doc, TextNote note)
    {
        string plain = note.GetFormattedText().GetPlainText();
        string first = NoteNumbering.RawParagraphs(plain).Select(p => p.Trim()).FirstOrDefault(p => p.Length > 0) ?? "(empty)";
        if (first.Length > 48)
            first = first.Substring(0, 45) + "...";
        string where = doc.GetElement(note.OwnerViewId) is View v ? v.Name : "";
        return new NoteInfo(note.Id.Value, where.Length > 0 ? $"{first}   ({where})" : first, plain);
    }

    /// <summary>Text notes among the elements, in the order given, each once.</summary>
    public static List<TextNote> NotesIn(IEnumerable<Element?> elements)
    {
        HashSet<long> seen = [];
        return [.. elements.OfType<TextNote>().Where(n => seen.Add(n.Id.Value))];
    }

    /// <summary>
    /// Every note, in ONE transaction (one undo). A note Revit refuses is rolled
    /// back on its own and reported; the others still go in.
    /// <paramref name="edits"/>: the user's own preview text per note id.
    /// </summary>
    public static List<NoteResult> Apply(Document doc, IReadOnlyList<TextNote> notes, NumberSettings s,
        IReadOnlyDictionary<long, string> edits, out List<string> revitErrors)
    {
        List<NoteResult> results = [];
        using Transaction t = new(doc, "Number Text Notes");
        ErrorCollector errors = ErrorCollector.Start(t);
        foreach (TextNote note in notes)
        {
            NoteInfo info = Describe(doc, note);
            using SubTransaction sub = new(doc);
            sub.Start();
            try
            {
                (bool changed, string status) = ApplyOne(note, s, edits.TryGetValue(info.Id, out string? e) ? e : null);
                sub.Commit();
                results.Add(new NoteResult(info.Id, info.Label, changed, status));
            }
            catch (Exception ex)
            {
                if (sub.GetStatus() == TransactionStatus.Started)
                    sub.RollBack();
                results.Add(new NoteResult(info.Id, info.Label, false, "failed: " + ex.Message));
            }
        }

        if (results.Any(r => r.Changed))
        {
            if (t.Commit() != TransactionStatus.Committed)
            {
                revitErrors = errors.Errors.DefaultIfEmpty("Revit rolled the change back and gave no reason.").ToList();
                return [.. results.Select(r => r.Changed ? r with { Changed = false, Status = "rolled back by Revit" } : r)];
            }
        }
        else
        {
            t.RollBack();
        }

        revitErrors = errors.Errors;
        return results;
    }

    /// <summary>Number one note. Returns (whether anything was written, the outcome in words).</summary>
    public static (bool Changed, string Status) ApplyOne(TextNote note, NumberSettings s, string? edited)
    {
        FormattedText ft = note.GetFormattedText();
        string oldText = ft.GetPlainText();
        NotePlan? plan = NoteNumbering.Plan(oldText, s, edited);
        if (plan is null)
            return (false, "no body text - nothing to number below the header");

        ListType list = s.Mode == NumberMode.List ? ListType.ArabicNumbers : ListType.None;
        int bodyLength = plan.NewText.Length - plan.BodyStart;

        if (plan.TextUnchanged)
        {
            // Nothing to rewrite: SetPlainText would wipe every bold word for no reason.
            if (bodyLength > 0 && ft.GetListType(new TextRange(plan.BodyStart, bodyLength)) == list)
                return (false, "already right - nothing to change");
            ft.SetListType(new TextRange(plan.BodyStart, bodyLength), list);
            note.SetFormattedText(ft);
            return (true, NoteNumbering.Outcome(plan, s, false));
        }

        bool lost = false;
        HashSet<string> headStyles = [], bodyStyles = [];
        try
        {
            foreach ((string name, Func<FormattedText, TextRange, FormatStatus> get, _) in Styles)
            {
                if (plan.OldHeaderLength > 0 && get(ft, new TextRange(plan.OldHeaderStart, plan.OldHeaderLength)) == FormatStatus.All)
                    headStyles.Add(name);
                int oldBody = plan.OldBodyEnd - plan.OldBodyStart;
                if (oldBody > 0 && get(ft, new TextRange(plan.OldBodyStart, oldBody)) == FormatStatus.All)
                    bodyStyles.Add(name);
            }
        }
        catch (Exception)
        {
            // Cosmetic only - never block the numbering over it.
            lost = true;
            headStyles.Clear();
            bodyStyles.Clear();
        }

        ft.SetPlainText(plan.NewText);

        try
        {
            foreach ((string name, _, Action<FormattedText, TextRange, bool> set) in Styles)
            {
                set(ft, new TextRange(0, plan.NewText.Length), false);
                if (headStyles.Contains(name) && plan.NewHeaderLength > 0)
                    set(ft, new TextRange(0, plan.NewHeaderLength), true);
                if (bodyStyles.Contains(name) && bodyLength > 0)
                    set(ft, new TextRange(plan.BodyStart, bodyLength), true);
            }
        }
        catch (Exception)
        {
            lost = true;
        }

        // The list setting is written for every mode. In Revit 2026 SetPlainText
        // already leaves the new text out of any list (the "notes" suite measured
        // it), so for typed / remove this is belt and braces, not a fix.
        if (bodyLength > 0)
            ft.SetListType(new TextRange(plan.BodyStart, bodyLength), list);

        note.SetFormattedText(ft);   // ft is a copy: nothing changes unless we get here
        return (true, NoteNumbering.Outcome(plan, s, lost));
    }
}
