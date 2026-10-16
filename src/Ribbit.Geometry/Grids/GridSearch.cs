using System.Text.RegularExpressions;

namespace Ribbit.Geometry.Grids;

/// <summary>
/// The search boxes of the Adjust Grids window, ported from make_search /
/// search_rows in Grid_v2.dyn.
///
/// Numbers are read as WHOLE numbers: "Sheet 6" is not "Sheet 16" or
/// "Sh - 61", but it is "Sheet 06". A word ending in a digit (L1, A-101) is
/// not L10 or A-1010.
///
/// Strict reading: a word followed by a bare number is one phrase ("sheet 6"
/// must be "sheet" then 6, with only spaces or - _ . between). Loose reading:
/// every word on its own, any order. <see cref="Filter{T}"/> tries strict
/// first and falls back to loose only when nothing matched strictly.
/// </summary>
public sealed class GridSearch
{
    private readonly List<Regex> _strict;
    private readonly List<Regex> _loose;

    /// <summary>True when the query holds a phrase, so the loose reading differs.</summary>
    public bool HasPhrase { get; }

    public GridSearch(string? query)
    {
        List<string> words =
            [.. (query ?? "").ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)];

        List<string> phrases = [];
        foreach (string word in words)
        {
            if (phrases.Count > 0 && IsDigits(word) && !phrases[phrases.Count - 1].Any(char.IsDigit))
                phrases[phrases.Count - 1] = phrases[phrases.Count - 1] + " " + word;
            else
                phrases.Add(word);
        }

        _strict = [.. phrases.Select(p => Compile(p.Split(' ')))];
        _loose = [.. words.Select(w => Compile([w]))];
        HasPhrase = phrases.Count != words.Count;
    }

    public bool Matches(string? text, bool loose = false)
    {
        string t = (text ?? "").ToLowerInvariant();
        return (loose ? _loose : _strict).All(r => r.IsMatch(t));
    }

    /// <summary>The keys of the (key, text) rows that match, strict first.</summary>
    public static List<T> Filter<T>(string? query, IEnumerable<(T Key, string Text)> rows)
    {
        GridSearch search = new(query);
        List<(T Key, string Text)> all = [.. rows];
        List<T> found = [.. all.Where(r => search.Matches(r.Text)).Select(r => r.Key)];
        if (found.Count > 0 || !search.HasPhrase)
            return found;

        return [.. all.Where(r => search.Matches(r.Text, loose: true)).Select(r => r.Key)];
    }

    private static bool IsDigits(string s) => s.Length > 0 && s.All(char.IsDigit);

    private static string PartPattern(string part)
    {
        if (IsDigits(part))
        {
            // a bare number: whole, and blind to leading zeros
            string trimmed = part.TrimStart('0');
            return @"(?<!\d)0*" + (trimmed.Length == 0 ? "0" : trimmed) + @"(?!\d)";
        }

        string body = Regex.Escape(part);
        if (char.IsDigit(part[0]))
            body = @"(?<!\d)" + body;
        if (char.IsDigit(part[part.Length - 1]))
            body += @"(?!\d)";
        return body;
    }

    private static Regex Compile(IEnumerable<string> parts)
        => new(string.Join(@"[\s\-_.]*", parts.Select(PartPattern)), RegexOptions.CultureInvariant);
}
