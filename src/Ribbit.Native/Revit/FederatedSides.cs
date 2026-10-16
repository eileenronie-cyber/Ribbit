using Autodesk.Revit.DB;
using Ribbit.Geometry.Clash;
using Ribbit.Native.Settings;

namespace Ribbit.Native.Revit;

/// <summary>
/// One independently built piece of a Federated Clash side.
///
/// A side is a UNION of pieces. Everything that leaves no workset out - the
/// typed keyword, the picked values and every chosen set without exclusions -
/// is ONE piece, merged exactly as the side always was. The sets that do leave
/// worksets out get pieces of their own - one per distinct list - because an
/// exclusion belongs to its set alone: "52 Series leaves out MEP_ZZ_Typical"
/// must not remove a Typical element that a different set on the same side
/// picks up.
/// </summary>
/// <param name="SetName">The set(s) this piece is, comma-joined when several leave out the same worksets; "" for the merged piece.</param>
internal sealed record SidePart(
    string SetName,
    string Keyword,
    List<(string Param, string Value)> Values,
    List<BuiltInCategory> Bics,
    List<string> ExcludedWorksets)
{
    public bool LeavesOut => ExcludedWorksets.Count > 0;

    public bool IsEmpty => Keyword.Trim().Length == 0 && Values.Count == 0 && Bics.Count == 0;
}

/// <summary>What leaving worksets out did to one piece, for the report.</summary>
/// <param name="Dropped">Distinct elements this piece's rules selected and its exclusion removed.</param>
/// <param name="StillIn">Of those, how many another piece of the side selected anyway.</param>
/// <param name="Stale">Excluded names no searched model has a workset for.</param>
internal sealed record SidePartStats(SidePart Part, int Dropped, int StillIn, List<string> Stale);

/// <summary>
/// Turns a side's inputs into the pieces that are searched - THE ONE PLACE.
///
/// The setup window and the batch runner each composed a side on their own,
/// held equal only by a comment saying they must be. A test that means one
/// thing by hand and another in a batch is the failure that invites, so both
/// now call <see cref="Compose"/>.
/// </summary>
internal static class FederatedSides
{
    /// <summary>
    /// The pieces for one side: the merged piece first (when it has anything),
    /// then one per set that leaves worksets out.
    ///
    /// With no exclusions anywhere this is a single piece built from exactly
    /// the inputs the side was always built from, so a library with no
    /// left-out worksets searches precisely what it did before.
    /// </summary>
    public static List<SidePart> Compose(
        string? typedKeyword,
        IEnumerable<(string Param, string Value)> picked,
        IEnumerable<ClashSet> sets)
    {
        Accumulator merged = new();
        merged.AddKeyword(typedKeyword);
        merged.AddValues(picked);

        // Sets that leave out the SAME worksets share a piece: dropping one list
        // of worksets from each set and then unioning is the same as unioning
        // first and dropping once - and six sets all leaving out MEP_ZZ_Typical
        // then cost one pass over the models, not six.
        List<(string Key, List<string> Names, Accumulator Rules, List<string> Excluded)> groups = [];

        foreach (ClashSet set in sets)
        {
            List<string> excluded = WorksetAudit.Normalize(set.ExcludedWorksets);

            if (excluded.Count == 0)
            {
                merged.AddSet(set);
                continue;
            }

            string key = string.Join("\n",
                excluded.Select(e => e.ToLowerInvariant()).OrderBy(e => e, StringComparer.Ordinal));

            int at = groups.FindIndex(g => g.Key == key);
            if (at < 0)
            {
                groups.Add((key, [], new Accumulator(), excluded));
                at = groups.Count - 1;
            }

            groups[at].Rules.AddSet(set);
            groups[at].Names.Add(set.Name);
        }

        List<SidePart> own =
        [
            .. groups.Select(g => g.Rules.ToPart(string.Join(", ", g.Names), g.Excluded))
        ];

        SidePart first = merged.ToPart("", []);

        return first.IsEmpty ? own : [first, .. own];
    }

    /// <summary>
    /// Every piece folded into one - for what a side is ABOUT rather than how it
    /// is built: the window's summary, "Set A is empty", the extra categories to
    /// collect, and which picked values matched nothing.
    /// </summary>
    public static (string Keyword, List<(string Param, string Value)> Values, List<BuiltInCategory> Bics)
        Merge(IEnumerable<SidePart> parts)
    {
        Accumulator a = new();

        foreach (SidePart p in parts)
        {
            a.AddKeyword(p.Keyword);
            a.AddValues(p.Values);
            a.AddBics(p.Bics);
        }

        SidePart all = a.ToPart("", []);
        return (all.Keyword, all.Values, all.Bics);
    }

    /// <summary>Folds rules together the way a side always has: OR, de-duplicated.</summary>
    private sealed class Accumulator
    {
        private readonly List<string> _keywords = [];
        private readonly List<(string Param, string Value)> _values = [];
        private readonly HashSet<(string, string)> _seenValues = [];
        private readonly List<BuiltInCategory> _bics = [];

        public void AddKeyword(string? keyword)
        {
            string k = (keyword ?? "").Trim();
            if (k.Length > 0 && !_keywords.Contains(k, StringComparer.OrdinalIgnoreCase))
                _keywords.Add(k);
        }

        public void AddValues(IEnumerable<(string Param, string Value)> values)
        {
            foreach ((string param, string value) in values)
            {
                // Case-insensitive, as the window and the batch both did.
                if (_seenValues.Add((param.ToLowerInvariant(), value.ToLowerInvariant())))
                    _values.Add((param, value));
            }
        }

        public void AddBics(IEnumerable<BuiltInCategory> bics)
        {
            foreach (BuiltInCategory b in bics)
            {
                if (!_bics.Contains(b))
                    _bics.Add(b);
            }
        }

        public void AddSet(ClashSet set)
        {
            foreach (string k in set.Keywords)
                AddKeyword(k);

            AddValues(set.Pairs());
            AddBics(set.Resolved());
        }

        public SidePart ToPart(string setName, List<string> excluded)
            => new(setName, string.Join(",", _keywords), [.. _values], [.. _bics], excluded);
    }
}
