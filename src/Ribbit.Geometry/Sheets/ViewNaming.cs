using System.Text.RegularExpressions;

namespace Ribbit.Geometry.Sheets;

/// <summary>
/// Level detection and view-name construction for Part Sheets Duplicator.
/// Ported from the naming half of lib/partsheetslib/duplicator.py.
///
/// All pure string work, and all of it decides which views get created — which
/// is why it lives here, testable, rather than buried in the Revit engine.
/// </summary>
public static class ViewNaming
{
    public static readonly Regex Sheet =
        new(@"\bsheet\s+\d+\s+of\s+\d+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static readonly Regex Overall =
        new(@"\boverall\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex PhaseIn =
        new(@"\bphase\s*[^\s,;:]+(?:\s*[^\s,;:]+)?", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex LevelToken =
        new(@"\b(?:level|lvl|floor|flr)\b[^\d]{0,3}0*([0-9]{1,2})\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex LevelAfterComma =
        new(@",\s*0*([0-9]{1,2})\b", RegexOptions.Compiled);

    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    /// <summary>
    /// Named (non-numeric) levels are matched by their literal wording rather
    /// than a fixed bucket vocabulary — a fixed "ROOF" bucket can't tell
    /// "Roof Level" and "Roof Gantry" apart.
    /// </summary>
    private static readonly string[] LevelKeywords =
    [
        "level", "lvl", "floor", "flr", "roof", "rf", "rooftop",
        "ground", "basement", "bmt", "storey", "story",
    ];

    /// <summary>
    /// Words that name a level only in a SHORT segment ("Mezzanine", "Plant
    /// Room"). They are ordinary words in a drawing title too - the real
    /// workbook's "Sh - 70 - MEP Primary Plant Layout, DC Building 1, Roof
    /// Level" read "plant" in the TITLE and keyed the row by the whole title
    /// instead of "roof", so it got a level card of its own.
    /// </summary>
    private static readonly string[] WeakLevelKeywords = ["mezzanine", "plant", "penthouse"];

    private const int WeakKeywordMaxWords = 3;

    private static readonly HashSet<string> GenericSuffixWords =
        ["level", "lvl", "floor", "flr"];

    private static string S(string? x) => x?.Trim() ?? "";

    /// <summary>
    /// Best-effort literal level phrase for non-numeric levels: the
    /// comma-separated segment containing a level-ish keyword, with one trailing
    /// generic word stripped when more than one word remains.
    ///
    /// "Roof Gantry Level" -> "roof gantry"; "Roof Level" -> "roof". Distinct
    /// from each other, and each matching the same normalization applied to the
    /// corresponding bare Revit level name, so a default can still be suggested.
    /// </summary>
    public static string? LevelPhraseFromText(string? lowText)
    {
        if (string.IsNullOrEmpty(lowText))
            return null;

        static bool Has(string seg, string[] words)
            => words.Any(w => Regex.IsMatch(seg, @"\b" + Regex.Escape(w) + @"\b"));

        List<string> segments =
        [
            .. lowText!.Split(',').Select(s => Whitespace.Replace(s, " ").Trim()).Where(s => s.Length > 0)
        ];

        // A plain level word anywhere wins over a weak one, so "Primary Plant
        // Layout, Roof Level" is the roof whichever segment comes first.
        string? found = segments.FirstOrDefault(s => Has(s, LevelKeywords))
                        ?? segments.FirstOrDefault(s => s.Split(' ').Length <= WeakKeywordMaxWords && Has(s, WeakLevelKeywords));
        if (found is null)
            return null;

        // words[^1] would need System.Index, which netstandard2.0 lacks -
        // this assembly targets it for the Revit 2024 leg.
        List<string> words = [.. found.Split(' ')];
        if (words.Count > 1 && GenericSuffixWords.Contains(words[words.Count - 1]))
            words.RemoveAt(words.Count - 1);

        return string.Join(" ", words);
    }

    /// <summary>
    /// Level numbers found in the text: explicit tokens ("Level 01", "Floor 1")
    /// first, else digits right after a comma (", 01") — which avoids false
    /// positives like "Sh - 58 -". Order-preserving, deduplicated.
    /// </summary>
    public static List<string> ExtractLevelNumbers(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return [];

        string s = text!.ToLowerInvariant();

        List<string> explicitNums = [];
        foreach (Match m in LevelToken.Matches(s))
        {
            string n = m.Groups[1].Value;
            if (!explicitNums.Contains(n))
                explicitNums.Add(n);
        }
        if (explicitNums.Count > 0)
            return explicitNums;

        List<string> afterComma = [];
        foreach (Match m in LevelAfterComma.Matches(s))
        {
            string n = m.Groups[1].Value;
            // The 1..60 window is what stops a job number or a dimension being
            // read as a storey.
            if (int.TryParse(n, out int v) && v > 0 && v <= 60 && !afterComma.Contains(n))
                afterComma.Add(n);
        }
        return afterComma;
    }

    /// <summary>The first level NUMBER, leading zeros stripped ("01" -> "1"), or null.</summary>
    public static string? NormalizeLevelName(string? name)
    {
        List<string> nums = ExtractLevelNumbers(name);
        if (nums.Count == 0)
            return null;

        return int.TryParse(nums[0], out int v) ? v.ToString() : null;
    }

    /// <summary>
    /// Level text -> key: a plain digit string for numbered levels
    /// ("Level 01" -> "1"), or a literal keyword-anchored phrase for named ones.
    /// Null when no level can be identified.
    /// </summary>
    public static string? NormalizeLevelKey(string? name)
    {
        if (string.IsNullOrEmpty(name))
            return null;

        string? n = NormalizeLevelName(name);
        return n ?? LevelPhraseFromText(name!.ToLowerInvariant());
    }

    /// <summary>Appends ", Phase" unless the text already names a phase.</summary>
    public static string EnsurePhaseAppended(string? fpvText, string? phaseText)
    {
        string based = S(fpvText);
        string p = S(phaseText);
        if (p.Length == 0)
            return based;
        if (PhaseIn.IsMatch(based))
            return based;

        return based.TrimEnd(' ', ',') + ", " + p;
    }

    /// <summary>
    /// The parent ("Overall") name for a dependent row: "Sheet 3 of 7" becomes
    /// "Overall", then the phase is appended if missing.
    /// </summary>
    public static string ParentNameFromFpv(string? fpvText, string? phaseText)
    {
        string based = S(fpvText);
        if (Sheet.IsMatch(based))
            based = Sheet.Replace(based, "Overall");

        return EnsurePhaseAppended(based, phaseText);
    }

    public static bool HasSheetToken(string? text) => Sheet.IsMatch(S(text));

    private static readonly Regex EmptySegments = new(@"\s*,(\s*,)+", RegexOptions.Compiled);

    /// <summary>
    /// A sheet view's name without its "Sheet N of M", lower case: the same for
    /// every sheet of one drawing ("..., Level 01, Sheet 1 of 3" and "..., Level
    /// 01, Sheet 3 of 4" -> "..., level 01"; a phase after it stays).
    /// </summary>
    public static string SheetFamilyKey(string? name)
    {
        string s = EmptySegments.Replace(Sheet.Replace(S(name), " "), ",");
        return Whitespace.Replace(s, " ").Trim().Trim(',', ' ').ToLowerInvariant();
    }

    public static bool HasOverallToken(string? text) => Overall.IsMatch(S(text));

    /// <summary>
    /// A name not already taken, suffixing " (n)" until one is free.
    ///
    /// Callers must decide BEFORE calling this whether an existing view should
    /// be skipped or updated — with auto-rename on, this never declines, so it
    /// cannot itself be used as an "already exists" test.
    /// </summary>
    public static string MakeUnique(string name, ISet<string> existing, ISet<string> created)
    {
        if (!existing.Contains(name) && !created.Contains(name))
            return name;

        int i = 1;
        while (true)
        {
            string candidate = $"{name} ({i})";
            if (!existing.Contains(candidate) && !created.Contains(candidate))
                return candidate;
            i++;
        }
    }

    /// <summary>A level key as a person reads it: "1" -> "Level 01", "roof" -> "Roof", "level rf" -> "Level RF".</summary>
    public static string LevelTitle(string key)
        => int.TryParse(key, out int n)
            ? $"Level {n:00}"
            : string.Join(" ", key.Split(' ').Select(w => w.Length <= 2 ? w.ToUpperInvariant() : char.ToUpperInvariant(w[0]) + w.Substring(1)));

    public sealed record LevelKeyEntry(string Key, string Example, int Count);

    /// <summary>
    /// One entry per distinct level key referenced by the rows, with an example
    /// and a row count, sorted by key for a stable UI order.
    ///
    /// The caller builds one explicit "this level -> this source view" picker per
    /// entry, rather than asking for a flat checklist and hoping the level
    /// grouping falls out right — two picks landing on the same level used to
    /// fail silently.
    /// </summary>
    public static List<LevelKeyEntry> LevelKeySummary(IEnumerable<XlsxRow> rows)
    {
        Dictionary<string, (string Example, int Count)> info = [];
        List<string> order = [];

        foreach (XlsxRow row in rows)
        {
            string fpv = row.Get("Floor Plan View Name");
            string? key = NormalizeLevelKey(fpv);
            if (key is null)
                continue;

            if (!info.TryGetValue(key, out (string Example, int Count) entry))
            {
                info[key] = (fpv, 1);
                order.Add(key);
            }
            else
            {
                info[key] = (entry.Example, entry.Count + 1);
            }
        }

        // Numbered levels in number order, then named ones alphabetically.
        order = [.. order.OrderBy(k => int.TryParse(k, out int n) ? 0 : 1)
                         .ThenBy(k => int.TryParse(k, out int n) ? n : 0)
                         .ThenBy(k => k, StringComparer.Ordinal)];
        return [.. order.Select(k => new LevelKeyEntry(k, info[k].Example, info[k].Count))];
    }
}
