using System.Text.RegularExpressions;

namespace Ribbit.Geometry.Clash;

/// <summary>
/// Pad normalization and CSV naming for the clash search buttons. Ported from
/// the pure helpers in CDMEPvsLink.py.
/// </summary>
public static class ClashSearchNaming
{
    private static readonly Regex BareNumber = new(@"^[-+]?\d*\.?\d+$", RegexOptions.Compiled);

    /// <summary>
    /// Longest a single picked value may be inside a filename. The name already
    /// carries model title, product tag, date, category list and a counter, and
    /// Windows' 260-character path limit is closer than it looks.
    /// </summary>
    private const int FilenameValueChars = 24;

    /// <summary>
    /// A bare number typed into the pad box means MILLIMETRES, so " mm" is
    /// appended before it reaches <see cref="UnitParse.ToFeet"/>.
    ///
    /// This matters: UnitParse reads a bare number as FEET. Without this step
    /// "25" would become a 25-FOOT clash pad rather than 25 mm — a 300x error
    /// that would report essentially every element as clashing.
    /// </summary>
    public static string? NormalizePadText(string? text)
    {
        if (text is null)
            return null;

        string s = text.Trim();
        return BareNumber.IsMatch(s) ? s + " mm" : text;
    }

    /// <summary>
    /// Filename-safe description of what Set A searched for.
    ///
    /// A typed keyword wins when present; otherwise the first Browse-picked
    /// value plus a count, so a picker-only search — a normal way to use the
    /// tool — does not produce a "nonevs..." filename that says nothing.
    /// </summary>
    public static string SetALabel(
        string? keywordText, IReadOnlyList<(string Param, string Value)>? pickedValues)
    {
        string text = (keywordText ?? "").Trim();
        if (text.Length > 0)
            return ClashStatus.SanitizeFilenamePart(text);

        if (pickedValues is null || pickedValues.Count == 0)
            return "none";

        string first = ClashStatus.SanitizeFilenamePart(pickedValues[0].Value);
        if (first.Length > FilenameValueChars)
            first = first.Substring(0, FilenameValueChars);
        first = first.Trim('_');
        if (first.Length == 0)
            first = "picked";

        return pickedValues.Count > 1 ? $"{first}_plus{pickedValues.Count - 1}" : first;
    }

    /// <summary>
    /// The "{keyword}vs{categories}" tag for a full report filename. Set B can
    /// use several link/category pairs, joined with "+".
    /// </summary>
    public static string ReportTag(
        string? keywordText,
        IEnumerable<string> categoryNames,
        IReadOnlyList<(string Param, string Value)>? pickedValues = null)
    {
        string kw = SetALabel(keywordText, pickedValues);

        List<string> cats =
        [
            .. categoryNames
                .Where(c => !string.IsNullOrEmpty(c))
                .Select(ClashStatus.SanitizeFilenamePart)
        ];

        string catPart = cats.Count > 0 ? string.Join("+", cats) : "none";
        return $"{kw}vs{catPart}";
    }

    /// <summary>
    /// The "{setA}vs{setB}_{n}links" tag for the Clash Detect Link report, where
    /// BOTH sides are keyword searches rather than a keyword against a category
    /// list.
    ///
    /// The link COUNT rather than the link names: a report over four links would
    /// otherwise carry four file names in a filename that already holds the
    /// model title, product tag, date, both search labels and a counter.
    /// </summary>
    public static string LinkReportTag(
        string? setAKeyword,
        IReadOnlyList<(string Param, string Value)>? setAValues,
        string? setBKeyword,
        IReadOnlyList<(string Param, string Value)>? setBValues,
        int linkCount)
    {
        string a = SetALabel(setAKeyword, setAValues);
        string b = SetALabel(setBKeyword, setBValues);
        return $"{a}vs{b}_{linkCount}links";
    }
}
