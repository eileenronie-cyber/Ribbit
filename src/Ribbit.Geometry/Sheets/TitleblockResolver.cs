namespace Ribbit.Geometry.Sheets;

/// <summary>
/// Turns whatever the Titleblock column says into one of the model's titleblock
/// types.
///
/// The old rule was positional — the Nth selected titleblock went to the Nth
/// row, last one repeating — which is invisible in the data and silently wrong
/// the moment a row is inserted in Excel. Naming the titleblock in the row
/// itself survives re-sorting and re-ordering, and lets one drawing list mix
/// A1 and A3 sheets.
/// </summary>
public static class TitleblockResolver
{
    /// <summary>
    /// The index into <paramref name="displayNames"/> ("Family : Type") that the
    /// cell names, or null if nothing matches.
    ///
    /// Tiers, strongest first: the full "Family : Type", then the type name
    /// alone (what people actually write), then the family name — the last only
    /// when it picks out exactly one type, since a family usually has several.
    /// </summary>
    public static int? Resolve(string? cell, IReadOnlyList<string> displayNames)
    {
        string want = MappingMatcher.Loose(cell);
        if (want.Length == 0)
            return null;

        for (int i = 0; i < displayNames.Count; i++)
        {
            if (MappingMatcher.Loose(displayNames[i]) == want)
                return i;
        }

        int? typeHit = SingleMatch(displayNames, want, TypePart);
        if (typeHit is not null)
            return typeHit;

        return SingleMatch(displayNames, want, FamilyPart);
    }

    /// <summary>
    /// The index whose part equals <paramref name="want"/>, but only when
    /// exactly one does. An ambiguous name must not quietly pick the first
    /// candidate — that stamps the wrong titleblock on a real drawing.
    /// </summary>
    private static int? SingleMatch(
        IReadOnlyList<string> displayNames, string want, Func<string, string> part)
    {
        int found = -1;
        for (int i = 0; i < displayNames.Count; i++)
        {
            if (MappingMatcher.Loose(part(displayNames[i])) != want)
                continue;

            if (found >= 0)
                return null;   // ambiguous
            found = i;
        }
        return found >= 0 ? found : null;
    }

    /// <summary>The "Type" half of "Family : Type"; the whole string if unsplit.</summary>
    private static string TypePart(string displayName)
    {
        // Substring, not a range: netstandard2.0 has no System.Index/Range.
        int cut = displayName.LastIndexOf(" : ", StringComparison.Ordinal);
        return cut >= 0 ? displayName.Substring(cut + 3) : displayName;
    }

    private static string FamilyPart(string displayName)
    {
        int cut = displayName.IndexOf(" : ", StringComparison.Ordinal);
        return cut >= 0 ? displayName.Substring(0, cut) : displayName;
    }
}
