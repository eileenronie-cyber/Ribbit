using System.Text.RegularExpressions;

namespace Ribbit.Geometry.Sheets;

/// <summary>One sheet as the matcher sees it.</summary>
public sealed record SheetName(string Number, string Name);

/// <summary>
/// Matches a view's name to the sheet it belongs on. Ported from the name
/// matcher in "Placing Views to Sheets v3.dyn", rule for rule:
///
///   - words are lower-cased, and the spellings of one storey become one token:
///     "L1", "LVL 01", "Level 1" and "1st floor" are all "lvl1";
///   - a trailing plural s is dropped ("SECTIONS" finds "SECTION");
///   - rarer words count for more: weight 1 / (1 + ln(1 + sheets having it)), and a
///     word no sheet has at all weighs 0.5;
///   - the score is half the share of the VIEW's words the sheet has, plus half the
///     share of the SHEET's words the view has - the second half is what tells
///     "... Level 00, Overall" from "... Level 00, Sheet 1";
///   - sharing only bare numbers is no match, and an identical name scores +1;
///   - a sheet is SUGGESTED only when it scores at least 0.60 and leads the
///     runner-up by 0.02 - L1 and L2 of one discipline scoring the same means the
///     matcher cannot tell them apart, and a wrong suggestion is worse than none.
/// </summary>
public sealed class ViewSheetMatcher
{
    public const double MatchFloor = 0.60;
    public const double MatchGap = 0.02;
    private const double UnknownWeight = 0.5;

    private static readonly Regex LevelWords = new(@"\b(?:l|lv|lvl|level|flr|floor)\s*[-_ ]?\s*0*(\d+)\b");
    private static readonly Regex OrdinalFloor = new(@"\b0*(\d+)(?:st|nd|rd|th)?\s+(?:floor|flr)\b");
    private static readonly Regex NonWord = new("[^0-9a-z]+");

    private readonly IReadOnlyList<SheetName> _sheets;
    private readonly List<HashSet<string>> _sheetTokens;
    private readonly List<string> _sheetNamesLower;
    private readonly List<double> _sheetTotals;
    private readonly Dictionary<string, double> _weights = new(StringComparer.Ordinal);

    public ViewSheetMatcher(IReadOnlyList<SheetName> sheets)
    {
        _sheets = sheets;
        _sheetTokens = [.. sheets.Select(s => Tokens(s.Number + " " + s.Name))];
        _sheetNamesLower = [.. sheets.Select(s => s.Name.Trim().ToLowerInvariant())];

        Dictionary<string, int> df = new(StringComparer.Ordinal);
        foreach (HashSet<string> toks in _sheetTokens)
            foreach (string t in toks)
                df[t] = df.TryGetValue(t, out int n) ? n + 1 : 1;
        foreach (KeyValuePair<string, int> kv in df)
            _weights[kv.Key] = 1.0 / (1.0 + Math.Log(1.0 + kv.Value));

        // Fixed once the weights are: summed per sheet here, not once per view x sheet.
        _sheetTotals = [.. _sheetTokens.Select(toks => toks.Sum(Weight))];
    }

    /// <summary>Lower-case words, with the spellings of one storey made one token.</summary>
    public static HashSet<string> Tokens(string text)
    {
        string lower = (text ?? "").ToLowerInvariant();
        lower = OrdinalFloor.Replace(lower, m => " lvl" + m.Groups[1].Value + " ");
        lower = LevelWords.Replace(lower, m => " lvl" + m.Groups[1].Value + " ");
        HashSet<string> output = new(StringComparer.Ordinal);
        foreach (string raw in NonWord.Split(lower))
        {
            string t = raw;
            if (t.Length == 0)
                continue;
            if (t.Length > 3 && t.EndsWith("s", StringComparison.Ordinal) && !t.EndsWith("ss", StringComparison.Ordinal)
                && t.All(c => c is >= 'a' and <= 'z'))
                t = t.Substring(0, t.Length - 1);
            output.Add(t);
        }
        return output;
    }

    private double Weight(string token) => _weights.TryGetValue(token, out double w) ? w : UnknownWeight;

    /// <summary>How well a view name and one sheet describe the same thing; 0 = no match, above 1 = identical name.</summary>
    public double Score(string viewName, int sheetIndex) => Score(new ViewKey(this, viewName), sheetIndex);

    /// <summary>
    /// Everything about the view that does not depend on the sheet, worked out once
    /// by the caller: ranking a view against every sheet used to re-tokenize its name
    /// per sheet (the graph did too) - V x S regex passes when the window opens on a
    /// big project - and to re-sum its weights and re-lower its name per sheet.
    /// </summary>
    private readonly struct ViewKey
    {
        public readonly HashSet<string> Tokens;
        public readonly double Total;
        public readonly string NameLower;

        public ViewKey(ViewSheetMatcher m, string viewName)
        {
            Tokens = ViewSheetMatcher.Tokens(viewName);
            Total = Tokens.Sum(m.Weight);
            NameLower = (viewName ?? "").Trim().ToLowerInvariant();
        }
    }

    private double Score(in ViewKey view, int sheetIndex)
    {
        HashSet<string> a = view.Tokens;
        HashSet<string> b = _sheetTokens[sheetIndex];
        if (a.Count == 0 || b.Count == 0)
            return 0.0;

        // The shared words, in the view's order (so the sum is the same as before),
        // without building a list per view x sheet.
        double got = 0.0;
        bool sharesAWord = false;
        foreach (string t in a)
        {
            if (!b.Contains(t))
                continue;
            got += Weight(t);
            if (!sharesAWord && !t.All(char.IsDigit))
                sharesAWord = true;
        }
        if (!sharesAWord)
            return 0.0;

        double viewTotal = view.Total;
        double sheetTotal = _sheetTotals[sheetIndex];
        if (viewTotal <= 0.0 || sheetTotal <= 0.0)
            return 0.0;

        double value = 0.5 * got / viewTotal + 0.5 * got / sheetTotal;
        if (view.NameLower == _sheetNamesLower[sheetIndex])
            value += 1.0;
        return value;
    }

    /// <summary>(score, sheet index) for every sheet scoring above 0, best first, ties in sheet-number order.</summary>
    public List<(double Score, int Sheet)> Ranked(string viewName)
    {
        List<(double Score, int Sheet)> output = [];
        ViewKey view = new(this, viewName);
        for (int i = 0; i < _sheets.Count; i++)
        {
            double v = Score(view, i);
            if (v > 0.0)
                output.Add((v, i));
        }
        output.Sort((x, y) =>
        {
            int c = y.Score.CompareTo(x.Score);
            return c != 0 ? c : NaturalSort.Comparer.Compare(_sheets[x.Sheet].Number, _sheets[y.Sheet].Number);
        });
        return output;
    }

    /// <summary>A sheet index only when one sheet is a clear winner; null otherwise.</summary>
    public int? Suggestion(string viewName)
    {
        // Only the best two scores matter, so no full Ranked sort per view. Equal top
        // scores fail the gap whichever sheet sorts first, so the tie order Ranked
        // applies cannot change the answer.
        ViewKey view = new(this, viewName);
        int best = -1;
        double top = 0.0, runner = 0.0;
        for (int i = 0; i < _sheets.Count; i++)
        {
            double v = Score(view, i);
            if (v <= 0.0)
                continue;
            if (best < 0 || v > top)
            {
                runner = top;
                top = v;
                best = i;
            }
            else if (v > runner)
                runner = v;
        }
        if (best < 0)
            return null;
        return top >= MatchFloor && top - runner >= MatchGap ? best : null;
    }

    /// <summary>A quick-to-read strength: 0 to 5 filled squares.</summary>
    public static string Stars(double score)
    {
        int filled = (int)Math.Round(Math.Min(score, 1.0) * 5, MidpointRounding.ToEven);
        return new string((char)0x25A0, filled) + new string((char)0x25A1, 5 - filled);
    }
}
