using System.Text;

namespace Ribbit.Geometry.Sheets;

/// <summary>
/// Guesses which Excel column feeds which Revit parameter, so the mapping does
/// not have to be typed out by hand.
///
/// The old design made you author the pairing inside the workbook, on a MAPPING
/// worksheet, which is both tedious and unverifiable until the tool runs. Real
/// parameter names carry an office prefix — <c>ABC_TB_ID_Project</c> — while the
/// spreadsheet column beside it is just <c>Project</c>, so an exact match finds
/// almost nothing and the tiers below it find almost everything.
/// </summary>
public static class MappingMatcher
{
    /// <summary>
    /// Lower-cased, with every non-alphanumeric character dropped, so
    /// "Sub-Discipline", "sub discipline" and "SUB_DISCIPLINE" all agree.
    /// </summary>
    public static string Loose(string? text)
    {
        StringBuilder sb = new();
        foreach (char c in SheetText.S(text))
        {
            if (char.IsLetterOrDigit(c))
                sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }

    /// <summary>
    /// The part after the last separator: <c>ABC_TB_ID_Project</c> -> "project".
    /// Only meaningful when the name actually has one, so a plain name returns
    /// itself and the caller's tiers collapse harmlessly.
    /// </summary>
    public static string LastSegment(string? text)
    {
        string s = SheetText.S(text);
        int cut = s.LastIndexOfAny(['_', '.', '-', ':', '/']);
        // Substring, not a range: netstandard2.0 has no System.Index/Range.
        return cut >= 0 && cut < s.Length - 1 ? Loose(s.Substring(cut + 1)) : Loose(s);
    }

    /// <summary>
    /// Pairs each parameter with a column, best match first and never reusing a
    /// column. Parameters with no confident match are simply absent — a wrong
    /// guess is worse than none, because it writes real data into the wrong
    /// field and looks deliberate.
    ///
    /// Tiers, strongest first:
    ///   1. loose equality            "Sub-Discipline"        = "sub discipline"
    ///   2. parameter tail = column   "ABC_TB_ID_Project"     = "Project"
    ///   3. column contains the tail  "ABC_TB_ID_Originator"  ⊂ "Originator Code"
    /// </summary>
    public static Dictionary<string, string> AutoMatch(
        IEnumerable<string> parameters,
        IEnumerable<string> columns)
    {
        List<string> paramList = [.. parameters.Select(SheetText.S).Where(p => p.Length > 0)];
        List<string> columnList = [.. columns.Select(SheetText.S).Where(c => c.Length > 0)];

        Dictionary<string, string> mapping = [];
        HashSet<string> takenColumns = new(StringComparer.OrdinalIgnoreCase);

        // Whole tiers in order, not best-per-parameter: a tier-1 match anywhere
        // must claim its column before a weaker tier-3 guess can take it.
        for (int tier = 1; tier <= 3; tier++)
        {
            foreach (string param in paramList)
            {
                if (mapping.ContainsKey(param))
                    continue;

                string? hit = null;
                foreach (string column in columnList)
                {
                    if (takenColumns.Contains(column))
                        continue;

                    if (Matches(param, column, tier))
                    {
                        // An ambiguous tier is no match at all - two columns that
                        // both look right mean the guess is a coin toss.
                        if (hit is not null)
                        {
                            hit = null;
                            break;
                        }
                        hit = column;
                    }
                }

                if (hit is not null)
                {
                    mapping[param] = hit;
                    takenColumns.Add(hit);
                }
            }
        }

        return mapping;
    }

    private static bool Matches(string param, string column, int tier)
    {
        string p = Loose(param);
        string c = Loose(column);
        if (p.Length == 0 || c.Length == 0)
            return false;

        return tier switch
        {
            1 => p == c,
            2 => LastSegment(param) == c,
            // Guard the length so a two-letter tail does not match half the sheet.
            3 => LastSegment(param).Length >= 4 && c.Contains(LastSegment(param)),
            _ => false,
        };
    }
}
