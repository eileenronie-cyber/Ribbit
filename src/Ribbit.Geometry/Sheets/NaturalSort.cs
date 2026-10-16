using System.Text.RegularExpressions;

namespace Ribbit.Geometry.Sheets;

/// <summary>
/// Human-order sorting for sheet numbers: "A2" before "A10", not after.
///
/// A plain string sort compares digit by digit, so "A10" lands before "A2"
/// because '1' &lt; '2'. Splitting into digit and non-digit chunks and comparing
/// the digit chunks NUMERICALLY is what fixes it.
/// </summary>
public static class NaturalSort
{
    private static readonly Regex Chunks = new(@"(\d+)", RegexOptions.Compiled);

    /// <summary>
    /// Comparer for sheet numbers and similar mixed alphanumeric labels.
    /// Case-insensitive, matching the Python's <c>chunk.lower()</c>.
    /// </summary>
    public static readonly IComparer<string> Comparer = new NaturalComparer();

    private sealed class NaturalComparer : IComparer<string>
    {
        public int Compare(string? a, string? b)
        {
            string[] left = Chunks.Split(a ?? "");
            string[] right = Chunks.Split(b ?? "");

            int count = Math.Min(left.Length, right.Length);
            for (int i = 0; i < count; i++)
            {
                string l = left[i];
                string r = right[i];
                if (l.Length == 0 && r.Length == 0)
                    continue;

                bool lNum = l.Length > 0 && char.IsDigit(l[0]);
                bool rNum = r.Length > 0 && char.IsDigit(r[0]);

                int result;
                if (lNum && rNum)
                {
                    // long, not int: a sheet number can carry a long numeric run
                    // (a date, a job code) that would overflow.
                    result = long.TryParse(l, out long lv) && long.TryParse(r, out long rv)
                        ? lv.CompareTo(rv)
                        : string.Compare(l, r, StringComparison.OrdinalIgnoreCase);
                }
                else
                {
                    result = string.Compare(l, r, StringComparison.OrdinalIgnoreCase);
                }

                if (result != 0)
                    return result;
            }

            return left.Length.CompareTo(right.Length);
        }
    }
}
