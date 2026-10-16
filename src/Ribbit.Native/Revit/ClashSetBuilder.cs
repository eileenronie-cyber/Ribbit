using Autodesk.Revit.DB;
using Ribbit.Geometry.Clash;

namespace Ribbit.Native.Revit;

/// <summary>A distinct Set A parameter value found by the Browse scan.</summary>
internal sealed record ValueChoice(string ParamName, string Display, int Count)
{
    /// <summary>The pair the picker hands back, normalized for comparison.</summary>
    public (string Param, string Value) Pair => (ParamName, Display);
}

/// <summary>
/// Builds Set A and Set B for the clash search. Ported from the set-building
/// half of CDMEPvsLink.py.
/// </summary>
internal static class ClashSetBuilder
{
    /// <summary>
    /// The two Set A match parameters, each paired with the reader it MUST be
    /// read through.
    ///
    /// System Type uses the type-first reader and Description the
    /// instance-first one. The source graph wires a different lookup order to
    /// each; this is not a choice the port gets to "simplify" into one reader.
    /// The Browse scan and the exact-match test both go through this same
    /// pairing, or a picked value would not compare equal to what the search
    /// reads back and would silently match nothing.
    /// </summary>
    public const string SystemTypeParam = "System Type";
    public const string DescriptionParam = "Description";

    /// <summary>
    /// The categories Description is allowed to match on when the
    /// cable-tray-only rule is switched on.
    ///
    /// Revit gives pipes and ducts a real System Type; cable tray and conduit
    /// have none, which is exactly why a company standard reaches for a type
    /// parameter like Description to label them. Left unrestricted, one
    /// Description value is read off EVERY MEP element, so the Browse list mixes
    /// tray labels with whatever text happens to sit in a pipe's Description,
    /// and a keyword can drag pipes into a tray-only set.
    ///
    /// Kept as an explicit list rather than "any element with no System Type":
    /// a pipe whose System Type is merely blank would otherwise flip categories
    /// element by element, which is far harder to explain than a fixed list.
    /// </summary>
    public static readonly BuiltInCategory[] DescriptionCategories =
    [
        BuiltInCategory.OST_CableTray,
        BuiltInCategory.OST_CableTrayFitting,
        BuiltInCategory.OST_Conduit,
        BuiltInCategory.OST_ConduitFitting,
    ];

    private static readonly HashSet<long> DescriptionCategoryIds =
        [.. DescriptionCategories.Select(c => (long)c)];

    /// <summary>
    /// Whether Description should be read off this element at all.
    ///
    /// FAILS OPEN: an element that will not report its category is read the way
    /// it always was, so an unreadable category can never silently shrink a set.
    /// </summary>
    public static bool ReadsDescription(Element e, bool descriptionCableTrayOnly)
    {
        if (!descriptionCableTrayOnly)
            return true;

        try
        {
            return e.Category is not null
                   && DescriptionCategoryIds.Contains(RevitCompat.IdKey(e.Category.Id));
        }
        catch (Exception)
        {
            return true;
        }
    }

    private static object? ReadForParam(
        ClashParameterReader reader, Element e, string paramName)
        => paramName == SystemTypeParam
            ? reader.GetParamTypeFirst(e, paramName)
            : reader.GetParamInstanceFirst(e, paramName);

    private static string Norm(object? value)
        => value?.ToString()?.Trim().ToLowerInvariant() ?? "";

    private static string Display(object? value) => value?.ToString()?.Trim() ?? "";

    /// <summary>
    /// Deduplicated by (Document, ElementId), never a bare id.
    ///
    /// Set B can combine elements from two different links, and a bare int key
    /// would silently conflate two different physical elements that happen to
    /// share a raw ElementId in two documents. Set A is host-only, so the extra
    /// key component is a no-op there.
    /// </summary>
    public static List<Element> DedupeById(IEnumerable<Element> elements)
    {
        HashSet<(Document, long)> seen = [];
        List<Element> outp = [];

        foreach (Element e in elements)
        {
            try
            {
                if (seen.Add((e.Document, RevitCompat.IdKey(e.Id))))
                    outp.Add(e);
            }
            catch (Exception)
            {
                // An element that will not report its id or document is skipped.
            }
        }

        return outp;
    }

    /// <summary>
    /// Distinct values of Set A's two match parameters across the host model,
    /// with counts — what the Browse picker offers.
    ///
    /// Read through the same reader pairing the search uses, so a picked value
    /// lands on exactly the elements the picker counted. Also warms the shared
    /// parameter caches the run then reuses.
    /// </summary>
    public static List<ValueChoice> CollectDistinctValues(
        ClashParameterReader reader,
        IReadOnlyDictionary<BuiltInCategory, List<Element>> buckets,
        bool descriptionCableTrayOnly,
        Action<int, int>? progress = null)
    {
        List<Element> allMep = [.. buckets.Values.SelectMany(v => v)];

        Dictionary<(string Param, string Norm), int> counts = [];
        Dictionary<(string Param, string Norm), string> display = [];

        for (int i = 0; i < allMep.Count; i++)
        {
            try
            {
                progress?.Invoke(i, allMep.Count);
            }
            catch (Exception)
            {
                // Progress must never take the scan down.
            }

            bool readsDesc = ReadsDescription(allMep[i], descriptionCableTrayOnly);

            foreach (string pname in new[] { SystemTypeParam, DescriptionParam })
            {
                // Skipped BEFORE the read, so a suppressed Description costs
                // nothing and cannot reach the counts the picker displays.
                if (pname == DescriptionParam && !readsDesc)
                    continue;

                object? raw = ReadForParam(reader, allMep[i], pname);
                string norm = Norm(raw);
                if (norm.Length == 0)
                    continue;

                (string, string) key = (pname, norm);
                counts[key] = counts.TryGetValue(key, out int n) ? n + 1 : 1;
                if (!display.ContainsKey(key))
                    display[key] = Display(raw);
            }
        }

        List<ValueChoice> results =
        [
            .. counts.Select(kv => new ValueChoice(kv.Key.Param, display[kv.Key], kv.Value))
        ];

        // System Type group first, then Description, alphabetical within each.
        results.Sort((a, b) =>
        {
            int oa = a.ParamName == SystemTypeParam ? 0 : 1;
            int ob = b.ParamName == SystemTypeParam ? 0 : 1;
            if (oa != ob)
                return oa.CompareTo(ob);

            return string.Compare(a.Display, b.Display, StringComparison.OrdinalIgnoreCase);
        });

        return results;
    }

    /// <summary>
    /// Set A: host MEP matching the keyword or an exact picked value, plus the
    /// cable-tray fittings connected to those matches, plus mechanical equipment
    /// matching on its own two type parameters.
    ///
    /// <paramref name="hitPairs"/> collects which picked values actually matched
    /// something, so a value renamed or removed since it was picked is surfaced
    /// rather than silently shrinking Set A.
    /// </summary>
    public static List<Element> BuildSetA(
        ClashParameterReader reader,
        IReadOnlyDictionary<BuiltInCategory, List<Element>> buckets,
        string? keywordText,
        bool descriptionCableTrayOnly,
        IReadOnlyList<(string Param, string Value)>? exactPairs = null,
        HashSet<(string Param, string Value)>? hitPairs = null,
        IReadOnlyList<BuiltInCategory>? forceBics = null)
    {
        List<string> keywords = ClashStatus.ParseKeywords(keywordText);
        exactPairs ??= [];
        forceBics ??= [];

        // A forced category alone is a valid set: it adds whole categories
        // regardless of keywords, exactly as it does for the link tools.
        if (keywords.Count == 0 && exactPairs.Count == 0 && forceBics.Count == 0)
            return [];

        List<Element> allMep = [.. buckets.Values.SelectMany(v => v)];

        Dictionary<string, (string Param, string Value)> exactSystemType = [];
        Dictionary<string, (string Param, string Value)> exactDescription = [];

        // Anything a set was built on that is neither of the two above - Type
        // Name, Comments, a shared parameter. Grouped by parameter so each is
        // read once per element rather than once per value.
        Dictionary<string, Dictionary<string, (string Param, string Value)>> exactOther = [];

        foreach ((string param, string value) in exactPairs)
        {
            // An EMPTY picked value would be indexed under "", and every element
            // whose value is absent normalizes to "" as well - so one empty pair
            // would quietly pull in everything that simply has no System Type.
            // Every producer already filters these (the Browse scan skips empty
            // values, ClashSet.Pairs drops them), but ClashSettings.PairsOf does
            // not, and this is where an empty one would do its damage.
            if (Norm(value).Length == 0)
                continue;

            if (param == SystemTypeParam)
            {
                exactSystemType[Norm(value)] = (param, value);
            }
            else if (param == DescriptionParam)
            {
                exactDescription[Norm(value)] = (param, value);
            }
            else
            {
                if (!exactOther.TryGetValue(param,
                        out Dictionary<string, (string Param, string Value)>? byValue))
                    exactOther[param] = byValue = [];

                byValue[Norm(value)] = (param, value);
            }
        }

        // One pass. Each reader is called once per element and reused for both
        // the substring-keyword and the exact-pick test.
        List<Element> keywordMatches = [];
        foreach (Element e in allMep)
        {
            // The SAME rule the Browse scan applied. If these two ever disagree,
            // a value the picker offered would match nothing when searched.
            bool readsDesc = ReadsDescription(e, descriptionCableTrayOnly);

            object? stVal = reader.GetParamTypeFirst(e, SystemTypeParam);
            bool isMatch = ClashStatus.ContainsKeyword(ClashParameterReader.AsText(stVal), keywords);

            object? descVal = null;
            if (!isMatch && readsDesc)
            {
                descVal = reader.GetParamInstanceFirst(e, DescriptionParam);
                isMatch = ClashStatus.ContainsKeyword(
                    ClashParameterReader.AsText(descVal), keywords);
            }

            if (exactSystemType.Count > 0
                && exactSystemType.TryGetValue(Norm(stVal), out (string, string) stPair))
            {
                isMatch = true;
                hitPairs?.Add(stPair);
            }

            if (exactDescription.Count > 0 && readsDesc)
            {
                descVal ??= reader.GetParamInstanceFirst(e, DescriptionParam);
                if (exactDescription.TryGetValue(Norm(descVal), out (string, string) dPair))
                {
                    isMatch = true;
                    hitPairs?.Add(dPair);
                }
            }

            // ANY other parameter a set was built on.
            //
            // Without this a set made on, say, Comments matches in the other
            // three tools and silently matches NOTHING here — the worst kind of
            // inconsistency, because it looks like the model rather than the
            // tool. Purely additive: no set could name a third parameter before
            // this existed, so no previous search changes.
            foreach ((string param, Dictionary<string, (string, string)> byValue) in exactOther)
            {
                string vn = Norm(reader.FindParameter(e, param));
                if (vn.Length == 0)
                    continue;

                if (byValue.TryGetValue(vn, out (string, string) oPair))
                {
                    isMatch = true;
                    hitPairs?.Add(oPair);
                }
            }

            if (isMatch)
                keywordMatches.Add(e);
        }

        List<Element> matches = DedupeById(keywordMatches);

        // Cable tray fittings are collected separately from the trays, so a
        // keyword search over trays alone would miss the elbows and tees that
        // physically continue the run.
        List<Element> fittingCandidates =
        [
            .. buckets.TryGetValue(BuiltInCategory.OST_CableTrayFitting, out List<Element>? fits)
                ? fits.Where(ClashElementUtils.HasResolvableFamily)
                : []
        ];

        List<Element> connected =
            ClashElementUtils.ConnectedCableTrayFittings(matches, fittingCandidates);

        // Mechanical equipment matches on its own two TYPE parameters instead.
        //
        // DELIBERATELY NOT subject to the cable-tray-only rule. This is a
        // separate matching path, not the Set A Description lookup: equipment
        // carries no System Type either, so suppressing its Description here
        // would leave it matchable on "Equipment Primary Function" alone and
        // quietly break equipment searches that have nothing to do with how
        // cable tray is labelled.
        List<Element> equipMatches = [];
        if (buckets.TryGetValue(BuiltInCategory.OST_MechanicalEquipment, out List<Element>? equip))
        {
            foreach (Element e in equip)
            {
                bool hit = ClashStatus.ContainsKeyword(
                               ClashParameterReader.AsText(
                                   reader.GetTypeParameter(e, "Equipment Primary Function")), keywords)
                           || ClashStatus.ContainsKeyword(
                               ClashParameterReader.AsText(
                                   reader.GetTypeParameter(e, DescriptionParam)), keywords);

                if (hit)
                    equipMatches.Add(e);
            }
        }

        // Whole categories from a chosen set, added regardless of keywords.
        List<Element> forced = [];
        foreach (BuiltInCategory bic in forceBics)
        {
            if (buckets.TryGetValue(bic, out List<Element>? elems))
                forced.AddRange(elems);
        }

        return DedupeById([.. matches, .. connected, .. equipMatches, .. forced]);
    }

    /// <summary>
    /// Set B: every (link, category) combination collected independently and
    /// unioned, so "three categories from one link" and "different categories
    /// from two links" both work.
    ///
    /// The diagnostics matter as much as the elements: an empty Set B is
    /// otherwise one silent zero, indistinguishable between "nothing was
    /// checked", "the link is unloaded" and "the link is loaded but has no
    /// elements of that category".
    /// </summary>
    public static (List<Element> Elements, List<string> Diagnostics) BuildSetB(
        IReadOnlyDictionary<RevitLinkInstance, List<Category>> linkCategories)
    {
        List<Element> collected = [];
        List<string> diagnostics = [];

        if (linkCategories.Count == 0)
        {
            diagnostics.Add("No links/categories checked - Set B is empty.");
            return ([], diagnostics);
        }

        foreach (KeyValuePair<RevitLinkInstance, List<Category>> kv in linkCategories)
        {
            string status = ClashLinks.LoadStatus(kv.Key);
            string linkName = ClashLinks.DisplayName(kv.Key);

            foreach (Category cat in kv.Value)
            {
                List<Element> found = ClashLinks.CollectCategoryInLink(kv.Key, cat);
                collected.AddRange(found);

                diagnostics.Add(
                    $"Link \"{linkName}\" ({status}), category \"{cat.Name}\" -> "
                    + $"{found.Count} element(s) found.");
            }
        }

        return (DedupeById(collected), diagnostics);
    }

    /// <summary>
    /// Element ids across the given sets — this search's scope, used to stop the
    /// stale-status table leaking elements flagged in a DIFFERENT search, since
    /// the ledger is model-wide rather than per-search.
    /// </summary>
    public static HashSet<string> ScopeIdsFor(params IEnumerable<Element>[] lists)
    {
        HashSet<string> ids = [];

        foreach (IEnumerable<Element> list in lists)
        {
            foreach (Element e in list)
            {
                try
                {
                    ids.Add(RevitCompat.IdKey(e.Id).ToString());
                }
                catch (Exception)
                {
                    // Skip an element that will not report an id.
                }
            }
        }

        return ids;
    }
}
