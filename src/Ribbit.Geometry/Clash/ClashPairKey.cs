namespace Ribbit.Geometry.Clash;

/// <summary>
/// Identity for a clash pair.
///
/// This is what status hangs off. Keying status on a single ELEMENT is wrong and
/// was inherited from the source: a pipe clashing with three beams has one
/// element and three clashes, so flagging the pipe "Resolved" silently flags all
/// three — including the two nobody looked at. The pair key fixes that by naming
/// both sides.
///
/// Deliberately NOT the report's "Clash #": that renumbers on every run, so it
/// identifies a row in one report rather than a clash over time.
/// </summary>
public static class ClashPairKey
{
    /// <summary>
    /// The pair's identity independent of which side it was found from — the
    /// two ids in ascending order.
    ///
    /// A host-vs-host search draws Set A and Set B from one pool, so the engine
    /// finds (X, Y) walking Set B against X's candidates and (Y, X) walking it
    /// against Y's. That is one physical clash reported twice with the sides
    /// swapped, and the two only collapse if the key ignores order.
    /// </summary>
    public static (long, long) Symmetric(long a, long b) => a <= b ? (a, b) : (b, a);

    /// <summary>
    /// The persisted pair key: both sides, order-independent, stable across runs.
    ///
    /// The LINK is part of each side because a raw element id is only unique
    /// within one document — two different physical elements in two links can
    /// share an id, and without the link they would share a status.
    /// </summary>
    public static string For(string idA, string? linkA, string idB, string? linkB)
    {
        string a = Side(idA, linkA);
        string b = Side(idB, linkB);
        return string.CompareOrdinal(a, b) <= 0 ? $"{a}|{b}" : $"{b}|{a}";
    }

    private static string Side(string id, string? link)
    {
        string trimmedId = (id ?? "").Trim();
        string trimmedLink = (link ?? "").Trim();
        return trimmedLink.Length == 0 ? trimmedId : $"{trimmedId}@{trimmedLink}";
    }

    /// <summary>
    /// The element ids named by a pair key, for scope checks. Link names are
    /// stripped — the caller compares against a set of bare ids.
    /// </summary>
    public static IEnumerable<string> IdsOf(string? clashKey)
    {
        if (string.IsNullOrEmpty(clashKey))
            yield break;

        foreach (string side in clashKey!.Split('|'))
        {
            int at = side.IndexOf('@');
            string id = at < 0 ? side : side.Substring(0, at);
            if (id.Length > 0)
                yield return id;
        }
    }

    /// <summary>A pair key rendered for a report cell.</summary>
    public static string Describe(string? clashKey)
        => string.IsNullOrEmpty(clashKey) ? "-" : clashKey!.Replace("|", "  ↔  ");
}
