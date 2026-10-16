using System.Text.RegularExpressions;

namespace Ribbit.Geometry.Text;

/// <summary>How the body of a note is numbered.</summary>
public enum NumberMode
{
    /// <summary>Revit's own numbered list: hanging indent, renumbers itself as you edit.</summary>
    List,

    /// <summary>Numbers typed into the text: "1.&lt;tab&gt;Text".</summary>
    Typed,

    /// <summary>Take the numbering off: typed numbers stripped, Revit list switched off.</summary>
    Remove,
}

/// <summary>What the user asked for. <see cref="Format"/> holds one "{n}" where the number goes.</summary>
public sealed record NumberSettings(int SkipLines, int Start, NumberMode Mode, string Format)
{
    public static NumberSettings Default { get; } = new(1, 1, NumberMode.List, "{n}.");
}

/// <summary>
/// The new text of one note, and where its parts sit - everything the Revit
/// side needs to write it and to put the bold / italic / underline back.
/// </summary>
public sealed class NotePlan
{
    /// <summary>Header paragraphs, as written (a blank one kept under them when the note had it).</summary>
    public IReadOnlyList<string> Head { get; init; } = [];

    /// <summary>The body items, in order, with their numbers when the mode types them.</summary>
    public IReadOnlyList<string> Body { get; init; } = [];

    /// <summary>The whole new plain text, paragraphs joined by '\r' (Revit's separator).</summary>
    public string NewText { get; init; } = "";

    /// <summary>Where the body (the list) starts in <see cref="NewText"/>.</summary>
    public int BodyStart { get; init; }

    /// <summary>Length of the header in the new text, without the blank line under it.</summary>
    public int NewHeaderLength { get; init; }

    /// <summary>
    /// True when the text itself is already right and only the list setting
    /// needs writing - the note then keeps every bold word it had.
    /// </summary>
    public bool TextUnchanged { get; init; }

    /// <summary>Where the header and the body sat in the OLD text, for reading their styles.</summary>
    public int OldHeaderStart { get; init; }
    public int OldHeaderLength { get; init; }
    public int OldBodyStart { get; init; }
    public int OldBodyEnd { get; init; }

    /// <summary>A gap in the typed numbers, said plainly; "" when there is none.</summary>
    public string Warning { get; init; } = "";

    public int Count => Body.Count;
}

/// <summary>
/// Auto-numbers the paragraphs of a text note (general notes and the like).
/// Ported from NumberTextNotes v6.dyn; the behaviour is the graph's, rule for rule:
///
///   - the first <c>SkipLines</c> non-blank paragraphs are a header and are left alone,
///     and a blank line right under the header stays;
///   - numbers typed by hand ("1.", "2)", "(3)", or in any format this tool can write)
///     are stripped first, so re-running is safe - but the "3." of "3.5 m cover" and the
///     "2-" of "2-way valve" are not numbers;
///   - Shift+Enter breaks inside a paragraph are joined back up, unless the next piece
///     starts with a typed number;
///   - in a hand-numbered note, a line without a number is the rest of the item above
///     ("...ABC-DWG-" + "ME-03-100" joins with no space, after a hyphen);
///   - invisible characters pasted from Word or a PDF are dropped;
///   - a gap in the typed numbers (1, 2, 4) is reported: the missing one was not
///     recognised and its line was joined to the item above.
/// </summary>
public static class NoteNumbering
{
    /// <summary>The formats offered in the window (any text with one "{n}" is accepted).</summary>
    public static readonly IReadOnlyList<string> Formats = ["{n}.", "{n})", "({n})", "{n}-", "{n}:"];

    public const string Placeholder = "{n}";

    /// <summary>"1.", "12)", "3.&lt;tab&gt;" - but not the "3." of "3.5 m" (a digit must not follow).</summary>
    private const string GenericNumber = @"\d{1,3}\s*[.)](?!\d)";

    /// <summary>A line break inside a paragraph (Shift+Enter).</summary>
    private static readonly Regex SoftBreak = new("[" + (char)0x0B + (char)0x2028 + "]");

    /// <summary>Characters pasted in from Word / PDF that would hide a typed number.</summary>
    private static readonly Regex Invisible = new("[" + (char)0xAD + (char)0x200B + "-" + (char)0x200F + (char)0x2060 + (char)0xFEFF + "]");

    private static readonly Regex Digits = new(@"\d+");

    /// <summary>Null when the settings are usable, else what is wrong with them.</summary>
    public static string? Validate(NumberSettings s)
    {
        if (s.SkipLines < 0)
            return "header lines to skip cannot be negative";
        if (s.Start < 0)
            return "the start number cannot be negative";
        if (s.Mode == NumberMode.Typed && !FormatOk(s.Format))
            return $"number format must contain {{n}} once, e.g. \"{{n}}.\" (got \"{s.Format}\")";
        return null;
    }

    /// <summary>One "{n}" and no other brace: the number has exactly one place to go.</summary>
    public static bool FormatOk(string? format)
    {
        if (string.IsNullOrEmpty(format))
            return false;
        int at = format!.IndexOf(Placeholder, StringComparison.Ordinal);
        if (at < 0 || format.IndexOf(Placeholder, at + 1, StringComparison.Ordinal) >= 0)
            return false;
        string rest = format.Remove(at, Placeholder.Length);
        return rest.IndexOf('{') < 0 && rest.IndexOf('}') < 0;
    }

    public static string FormatNumber(string format, int n) => format.Replace(Placeholder, n.ToString());

    /// <summary>
    /// A leading number to strip: a hand-typed "1." / "2)", or any format this
    /// tool can write ("(3)", "4-", "5:", or the chosen one) followed by
    /// whitespace - so "2-way valve" is left alone.
    /// </summary>
    public static Regex NumberPattern(string format)
    {
        SortedSet<string> formats = new(Formats, StringComparer.Ordinal);
        if (FormatOk(format))
            formats.Add(format);

        List<string> alternatives = [GenericNumber];
        foreach (string f in formats)
        {
            string[] parts = f.Split([Placeholder], StringSplitOptions.None);
            alternatives.Add(string.Join(@"\d{1,3}", parts.Select(Regex.Escape)) + @"(?=\s)");
        }
        return new Regex(@"^\s*(?:" + string.Join("|", alternatives) + @")\s*");
    }

    /// <summary>Paragraphs as Revit separates them ('\r'); '\n' and "\r\n" count too.</summary>
    public static string[] RawParagraphs(string plain)
        => plain.Replace("\r\n", "\r").Replace('\n', '\r').Split('\r');

    /// <summary>Glue a wrapped line back on. A word or code broken at a hyphen ("DWG-" + "ME-03") gets no space.</summary>
    public static string JoinLine(string a, string b)
    {
        a = a.TrimEnd();
        b = b.Trim();
        if (a.Length == 0 || b.Length == 0)
            return a.Length > 0 ? a : b;
        if (a.EndsWith("-", StringComparison.Ordinal) && !a.EndsWith(" -", StringComparison.Ordinal))
            return a + b;
        return a + " " + b;
    }

    /// <summary>
    /// The non-blank paragraphs. Shift+Enter breaks are joined back up - unless
    /// the next piece starts with a typed number, which makes it a new item even
    /// without a real paragraph break.
    /// </summary>
    public static List<string> SplitParagraphs(string plain, Regex? strip)
    {
        List<string> paras = [];
        foreach (string p in RawParagraphs(Invisible.Replace(plain, "")))
        {
            if (p.Trim().Length == 0)
                continue;
            string text = "";
            foreach (string piece in SoftBreak.Split(p))
            {
                if (text.Length > 0 && strip is not null && strip.IsMatch(piece))
                {
                    paras.Add(text);
                    text = piece;
                }
                else
                {
                    text = text.Length > 0 ? JoinLine(text, piece) : piece;
                }
            }
            if (text.Trim().Length > 0)
                paras.Add(text);
        }
        return paras;
    }

    /// <summary>
    /// In a hand-numbered note, a line without a number is the rest of the item
    /// above it. A note with no typed numbers at all is one item per paragraph.
    /// </summary>
    public static List<string> JoinWrapped(IReadOnlyList<string> lines, Regex strip)
    {
        if (!lines.Any(strip.IsMatch))
            return [.. lines];
        List<string> items = [];
        foreach (string line in lines)
        {
            if (strip.IsMatch(line) || items.Count == 0)
                items.Add(line);
            else
                items[items.Count - 1] = JoinLine(items[items.Count - 1], line);
        }
        return items;
    }

    /// <summary>
    /// A hand-numbered note with a gap in its typed numbers (1, 2, 4, ...): the
    /// missing one was not recognised, so its line was joined to the item above.
    /// "" when all is well.
    /// </summary>
    public static string NumberWarning(string plain, int skip, string format)
    {
        Regex strip = NumberPattern(format);
        List<string> body = [.. SplitParagraphs(plain, strip).Skip(skip)];
        List<int> found = [];
        foreach (string line in body)
        {
            Match m = strip.Match(line);
            if (m.Success && int.TryParse(Digits.Match(m.Value).Value, out int n))
                found.Add(n);
        }
        if (found.Count < 2)
            return "";

        int first = found[0] <= 2 ? 1 : found[0];
        List<int> missing = [.. Enumerable.Range(first, Math.Max(0, found.Max() - first + 1)).Where(k => !found.Contains(k))];
        if (missing.Count == 0)
            return "";

        string msg = $"Typed number(s) {string.Join(", ", missing)} not recognised - joined to the item above.";
        Regex odd = new(@"^\W{0,4}(?:" + string.Join("|", missing) + @")\D");
        List<string> suspects = [.. body.Where(l => !strip.IsMatch(l) && odd.IsMatch(l)).Take(3)];
        if (suspects.Count > 0)
            msg += " Check: " + string.Join("; ", suspects.Select(l => "'" + (l.Length > 14 ? l.Substring(0, 14) : l) + "'"));
        return msg;
    }

    /// <summary>True if the paragraph right after the header is empty (the gap under a "Notes" title).</summary>
    public static bool BlankAfterHeader(string plain, int skip)
    {
        if (skip <= 0)
            return false;
        int seen = 0;
        string[] paras = RawParagraphs(plain);
        for (int i = 0; i < paras.Length; i++)
        {
            if (paras[i].Trim().Length == 0)
                continue;
            seen++;
            if (seen == skip)
                return i + 1 < paras.Length && paras[i + 1].Trim().Length == 0;
        }
        return false;
    }

    /// <summary>
    /// (header, body) for the new text. <paramref name="join"/> false: every
    /// line is its own item (text edited in the preview, where Enter means a new item).
    /// </summary>
    public static (List<string> Header, List<string> Body) Renumber(string plain, NumberSettings s, bool join = true)
    {
        Regex strip = NumberPattern(s.Format);
        List<string> paras = SplitParagraphs(plain, strip);
        int skip = Math.Min(s.SkipLines, paras.Count);
        List<string> header = paras.GetRange(0, skip);
        List<string> body = paras.GetRange(skip, paras.Count - skip);
        if (join)
            body = JoinWrapped(body, strip);
        body = [.. body.Select(p => strip.Replace(p, "", 1).Trim()).Where(p => p.Length > 0)];
        if (s.Mode == NumberMode.Typed)
            body = [.. body.Select((p, i) => FormatNumber(s.Format, s.Start + i) + "\t" + p)];
        return (header, body);
    }

    /// <summary>
    /// What the window shows: the note as it will read, with the numbers drawn
    /// in for every mode. Feeding it back with join=false gives the same text,
    /// which is what lets the preview be edited and re-read.
    /// </summary>
    public static string PreviewText(string plain, NumberSettings s, bool join = true)
    {
        (List<string> header, List<string> body) = Renumber(plain, s with { Mode = NumberMode.List }, join);
        if (header.Count > 0 && body.Count > 0 && BlankAfterHeader(plain, s.SkipLines))
            header.Add("");
        IEnumerable<string> items = s.Mode switch
        {
            NumberMode.Typed => body.Select((p, i) => FormatNumber(s.Format, s.Start + i) + "  " + p),
            NumberMode.List => body.Select((p, i) => (i + 1) + ".  " + p),
            _ => body,
        };
        return string.Join("\n", header.Concat(items));
    }

    /// <summary>
    /// The plan for one note. <paramref name="edited"/> is the user's own text
    /// from the preview (one line per item), or null to number the note as it is.
    /// Null result: there is no body to number.
    /// </summary>
    public static NotePlan? Plan(string oldText, NumberSettings s, string? edited = null)
    {
        string source = edited ?? oldText;
        (List<string> header, List<string> body) = Renumber(source, s, join: edited is null);
        if (body.Count == 0)
            return null;

        string warning = edited is null ? NumberWarning(oldText, s.SkipLines, s.Format) : "";

        // The blank line under the header is kept; it is not part of the list.
        List<string> head = header.Count > 0 && BlankAfterHeader(source, s.SkipLines) ? [.. header, ""] : header;
        string newText = string.Join("\r", head.Concat(body));
        int bodyStart = string.Join("\r", head).Length + (head.Count > 0 ? 1 : 0);

        string oldTrimmed = oldText.TrimEnd('\r', '\n');
        List<string> oldHeader = Renumber(oldText, s).Header;
        int hstart = oldHeader.Count > 0 ? Math.Max(oldText.IndexOf(oldHeader[0], StringComparison.Ordinal), 0) : 0;
        int hlen = oldHeader.Count > 0 ? string.Join("\r", oldHeader).Length : 0;
        int bstart = oldHeader.Count > 0 ? Math.Min(hstart + hlen + 1, oldTrimmed.Length) : 0;

        return new NotePlan
        {
            Head = head,
            Body = body,
            NewText = newText,
            BodyStart = bodyStart,
            NewHeaderLength = string.Join("\r", header).Length,
            TextUnchanged = newText == oldTrimmed,
            OldHeaderStart = hstart,
            OldHeaderLength = Math.Min(hlen, Math.Max(0, oldTrimmed.Length - hstart)),
            OldBodyStart = bstart,
            OldBodyEnd = oldTrimmed.Length,
            Warning = warning,
        };
    }

    /// <summary>The one-line outcome for a note, as the report shows it.</summary>
    public static string Outcome(NotePlan plan, NumberSettings s, bool formattingLost)
    {
        string msg = s.Mode == NumberMode.Remove
            ? $"numbering removed from {plan.Count} paragraph(s)"
            : $"numbered {plan.Count} paragraph(s)";
        if (formattingLost)
            msg += " (bold/italic/underline could not be read back - check the formatting)";
        if (plan.Warning.Length > 0)
            msg += " WARNING: " + plan.Warning;
        return msg;
    }
}
