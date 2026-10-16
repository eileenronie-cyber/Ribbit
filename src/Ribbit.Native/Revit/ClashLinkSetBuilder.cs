using Autodesk.Revit.DB;
using Ribbit.Geometry.Clash;

namespace Ribbit.Native.Revit;

/// <summary>
/// Set builders for Clash Detect Link — host MEP against one or more linked
/// models, BOTH sides matched by keyword. Ported from CDMEPLink.py.
///
/// Different from the MEP-vs-Structure tool in two ways that matter: Set B is
/// chosen by KEYWORD rather than by category, and matching goes through
/// <see cref="ClashParameterReader.FindParameter"/> for both parameters rather
/// than the type-first/instance-first pairing that tool uses. That reader is
/// doc-safe, so the identical rule applies to host-document and link-document
/// elements alike.
/// </summary>
internal static class ClashLinkSetBuilder
{
    /// <summary>
    /// The category toggles that force whole categories into Set B regardless of
    /// keyword. They only ever feed Set B — Set A is keyword-only.
    /// </summary>
    // The six force-include groups that used to be hardcoded here are now
    // ClashSetLibrary.Seed() — named sets the user can edit, shared by all three
    // clash tools. They were removed rather than left in place because two
    // copies of the same list drift, and the seeded file is the one that runs.
    // Verified identical, group for group, at the time of the move.

    private static string Norm(object? value)
        => value?.ToString()?.Trim().ToLowerInvariant() ?? "";

    /// <summary>
    /// Whether any exact pair was picked from this parameter.
    ///
    /// Case-insensitive on purpose, while the dictionary itself stays
    /// case-sensitive: this only decides whether reading the parameter is
    /// worthwhile, so being generous here can cost a wasted read but can never
    /// skip one that was needed.
    /// </summary>
    private static bool NamesParam(
        Dictionary<string, Dictionary<string, (string Param, string Value)>> exactByParam,
        string paramName)
        => exactByParam.Keys.Any(k => string.Equals(k, paramName, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Elements matching EITHER rule, OR'd:
    ///   - keywords: case-insensitive SUBSTRING against System Type or Description
    ///   - exactPairs: an EXACT match against ONLY the parameter the value came from
    ///
    /// <paramref name="hitPairs"/> collects every exact pair that matched at
    /// least one element, so the caller can warn about a pick that matched
    /// nothing — for instance a value remembered from a previous run against a
    /// link that no longer contains it.
    /// </summary>
    public static List<Element> MatchKeywords(
        ClashParameterReader reader,
        IEnumerable<Element> elements,
        IReadOnlyList<string> keywords,
        bool descriptionCableTrayOnly,
        IReadOnlyList<(string Param, string Value)>? exactPairs = null,
        HashSet<(string Param, string Value)>? hitPairs = null)
    {
        exactPairs ??= [];
        if (keywords.Count == 0 && exactPairs.Count == 0)
            return [];

        Dictionary<string, Dictionary<string, (string Param, string Value)>> exactByParam = [];
        foreach ((string param, string value) in exactPairs)
        {
            // See ClashSetBuilder.BuildSetA: an empty picked value indexes under
            // "", which is also what an ABSENT value normalizes to, so one would
            // quietly match every element that simply carries nothing there.
            if (Norm(value).Length == 0)
                continue;

            if (!exactByParam.TryGetValue(param, out Dictionary<string, (string, string)>? byValue))
                exactByParam[param] = byValue = [];
            byValue[Norm(value)] = (param, value);
        }

        List<Element> matched = [];

        // Read only what some rule can actually use.
        //
        // A parameter is needed if ANY keyword exists (a keyword matches against
        // either) or if some exact pair names it. Otherwise the value is read,
        // compared against an empty keyword list, and looked up in a dictionary
        // that has no entry for it — pure cost. A set of System Type values only,
        // which is the common shape, halves the reads.
        //
        // Behaviour-preserving by construction: the skipped value could only have
        // reached ContainsKeyword with an empty list (always false) or a
        // per-parameter lookup that is never performed.
        bool anyKeywords = keywords.Count > 0;
        bool needSystemType = anyKeywords || NamesParam(exactByParam, ClashSetBuilder.SystemTypeParam);
        bool needDescription = anyKeywords || NamesParam(exactByParam, ClashSetBuilder.DescriptionParam);

        foreach (Element el in elements)
        {
            // The SAME rule CollectDistinctValuesForDoc applied, or a value the
            // picker offered would match nothing when searched.
            bool readsDesc = ClashSetBuilder.ReadsDescription(el, descriptionCableTrayOnly);

            object? stVal = needSystemType
                ? reader.FindParameter(el, ClashSetBuilder.SystemTypeParam)
                : null;

            object? descVal = readsDesc && needDescription
                ? reader.FindParameter(el, ClashSetBuilder.DescriptionParam)
                : null;

            bool isMatch =
                ClashStatus.ContainsKeyword(ClashParameterReader.AsText(stVal), keywords)
                || ClashStatus.ContainsKeyword(ClashParameterReader.AsText(descVal), keywords);

            if (exactByParam.Count > 0)
            {
                Dictionary<string, string> byParam = new()
                {
                    [ClashSetBuilder.SystemTypeParam] = Norm(stVal),
                    // Norm(null) is "", and the loop below skips empty values,
                    // so a suppressed Description cannot match an exact pick.
                    [ClashSetBuilder.DescriptionParam] = Norm(descVal),
                };

                foreach (KeyValuePair<string, Dictionary<string, (string, string)>> kv in exactByParam)
                {
                    // ANY parameter, not just the two above. A set can be built
                    // on whatever the model actually carries, so a name that is
                    // neither System Type nor Description is read on demand
                    // rather than silently matching nothing.
                    //
                    // Read through FindParameter, the same reader the value
                    // picker offered those values through - a different lookup
                    // here would produce values that never match what was picked.
                    if (!byParam.TryGetValue(kv.Key, out string? vn))
                    {
                        vn = Norm(reader.FindParameter(el, kv.Key));
                        byParam[kv.Key] = vn;
                    }

                    if (vn.Length == 0)
                        continue;

                    if (kv.Value.TryGetValue(vn, out (string, string) pair))
                    {
                        isMatch = true;
                        hitPairs?.Add(pair);
                    }
                }
            }

            if (isMatch)
                matched.Add(el);
        }

        return matched;
    }

    /// <summary>
    /// Connected cable-tray fittings and insulation for the matched elements,
    /// scoped to the SAME buckets — i.e. the same document — the matches came
    /// from.
    ///
    /// A link's connector graph and its insulation never reach outside that
    /// link's own document, so this must never be called with buckets and
    /// matches from two documents mixed together.
    /// </summary>
    private static (List<Element> Connected, List<Element> Insulation) ExpandMatches(
        IReadOnlyDictionary<BuiltInCategory, List<Element>> buckets,
        IReadOnlyList<Element> matched)
    {
        List<Element> fittingCandidates =
            buckets.TryGetValue(BuiltInCategory.OST_CableTrayFitting, out List<Element>? fits)
                ? fits
                : [];

        List<Element> connected =
            ClashElementUtils.ConnectedCableTrayFittings(matched, fittingCandidates);

        List<Element> insulation = [];
        foreach (Element el in matched)
        {
            Element? ins = ClashElementUtils.GetInsulation(el);
            if (ins is not null)
                insulation.Add(ins);
        }

        return (connected, insulation);
    }

    /// <summary>
    /// The full per-set pipeline inside ONE document: keyword match → connected
    /// cable-tray fittings → insulation → forced categories → dedupe.
    ///
    /// Every step after the match is identical whichever rule brought an element
    /// in, and identical for Set A and Set B — only <paramref name="forceBics"/>
    /// differs, and that is empty for Set A in every tool that uses this.
    ///
    /// A forced category enters the set even when no keyword was given: the
    /// toggles are a deliberate "everything of this kind" switch, not a filter
    /// over the keyword result.
    /// </summary>
    public static List<Element> BuildSet(
        ClashParameterReader reader,
        IReadOnlyDictionary<BuiltInCategory, List<Element>> buckets,
        string? keywordsText,
        bool descriptionCableTrayOnly,
        IReadOnlyList<BuiltInCategory>? forceBics = null,
        IReadOnlyList<(string Param, string Value)>? exactPairs = null,
        HashSet<(string Param, string Value)>? hitPairs = null)
    {
        List<string> keywords = ClashStatus.ParseKeywords(keywordsText);
        exactPairs ??= [];
        forceBics ??= [];

        if (keywords.Count == 0 && exactPairs.Count == 0 && forceBics.Count == 0)
            return [];

        List<Element> allMep = [.. buckets.Values.SelectMany(v => v)];
        List<Element> matched = MatchKeywords(
            reader, allMep, keywords, descriptionCableTrayOnly, exactPairs, hitPairs);

        (List<Element> connected, List<Element> insulation) = ExpandMatches(buckets, matched);

        List<Element> toggled = [];
        foreach (BuiltInCategory bic in forceBics)
        {
            if (buckets.TryGetValue(bic, out List<Element>? elems))
                toggled.AddRange(elems);
        }

        return ClashSetBuilder.DedupeById(
            [.. matched, .. connected, .. insulation, .. toggled]);
    }

    // BuildSetAHost lived here: BuildSet with an empty forced-category list,
    // because the toggles only ever fed Set B. A chosen set now means the same
    // thing on either side, so Set A can carry categories too and the wrapper's
    // premise no longer holds. Callers use BuildSet directly.

    /// <summary>
    /// Set B: every checked link searched INDEPENDENTLY — its own category
    /// collection, its own fitting/insulation expansion, its own forced
    /// categories — then unioned.
    ///
    /// Independence is not an optimisation, it is required: the expansion must
    /// stay inside one document, and a link's transform applies only to its own
    /// elements.
    ///
    /// The per-link diagnostics make an empty or small Set B traceable to a
    /// specific link rather than one silent zero.
    /// </summary>
    public static (List<Element> Elements, List<string> Diagnostics) BuildSetBLinks(
        ClashParameterReader reader,
        IReadOnlyList<RevitLinkInstance> selectedLinks,
        string? keywordsText,
        bool descriptionCableTrayOnly,
        IReadOnlyList<BuiltInCategory> forceBics,
        Func<Document, Dictionary<BuiltInCategory, List<Element>>> bucketsFor,
        IReadOnlyList<(string Param, string Value)>? exactPairs = null,
        HashSet<(string Param, string Value)>? hitPairs = null,
        Action<int, int, string>? progress = null)
    {
        List<Element> collected = [];
        List<string> diagnostics = [];

        if (selectedLinks.Count == 0)
        {
            diagnostics.Add("No Revit Links checked - Set B is empty.");
            return ([], diagnostics);
        }

        for (int i = 0; i < selectedLinks.Count; i++)
        {
            RevitLinkInstance li = selectedLinks[i];
            string linkName = ClashLinks.DisplayName(li);
            string status = ClashLinks.LoadStatus(li);

            // Reported BEFORE each link's own work, so several checked links
            // show live progress rather than one silent pause - the narrow-phase
            // progress bar does not appear until both sets are already built.
            progress?.Invoke(i, selectedLinks.Count, linkName);

            Document? linkDoc;
            try
            {
                linkDoc = li.GetLinkDocument();
            }
            catch (Exception)
            {
                linkDoc = null;
            }

            if (linkDoc is null)
            {
                diagnostics.Add($"Link \"{linkName}\" ({status}): could not be read - 0 element(s).");
                continue;
            }

            // The same pipeline as any other single-document set - which is the
            // whole reason each link is processed on its own.
            //
            // The reported count is this link's DEDUPED contribution, so an
            // element that both matched a keyword and sits in a forced category
            // counts once. The final Set B is unaffected either way; only the
            // diagnostic number is, and one-per-element is the honest reading.
            List<Element> combined = BuildSet(
                reader, bucketsFor(linkDoc), keywordsText, descriptionCableTrayOnly,
                forceBics, exactPairs, hitPairs);

            collected.AddRange(combined);

            diagnostics.Add(
                $"Link \"{linkName}\" ({status}): {combined.Count} element(s) contributed to Set B.");
        }

        return (ClashSetBuilder.DedupeById(collected), diagnostics);
    }

    /// <summary>
    /// Distinct System Type / Description values in ONE document — what the
    /// Browse picker offers for that document.
    ///
    /// Read through <see cref="ClashParameterReader.FindParameter"/>, the same
    /// reader <see cref="MatchKeywords"/> uses, so a picked value compares equal
    /// to what the search reads back — and the scan warms that reader's cache
    /// for the run that follows.
    ///
    /// One document at a time, because the caller caches per Document: Set A
    /// browses the host, Set B browses whichever links are checked, and a link
    /// browsed for Set B must not be re-scanned when the check boxes change.
    /// </summary>
    public static List<ValueChoice> CollectDistinctValuesForDoc(
        ClashParameterReader reader,
        IReadOnlyDictionary<BuiltInCategory, List<Element>> buckets,
        bool descriptionCableTrayOnly,
        Action<int, int>? progress = null)
    {
        List<Element> all = [.. buckets.Values.SelectMany(v => v)];

        Dictionary<(string Param, string Norm), int> counts = [];
        Dictionary<(string Param, string Norm), string> display = [];

        for (int i = 0; i < all.Count; i++)
        {
            try
            {
                progress?.Invoke(i, all.Count);
            }
            catch (Exception)
            {
                // Progress must never take the scan down.
            }

            bool readsDesc =
                ClashSetBuilder.ReadsDescription(all[i], descriptionCableTrayOnly);

            foreach (string pname in new[]
                     {
                         ClashSetBuilder.SystemTypeParam, ClashSetBuilder.DescriptionParam,
                     })
            {
                if (pname == ClashSetBuilder.DescriptionParam && !readsDesc)
                    continue;

                object? raw = reader.FindParameter(all[i], pname);
                string norm = Norm(raw);
                if (norm.Length == 0)
                    continue;

                (string, string) key = (pname, norm);
                counts[key] = counts.TryGetValue(key, out int n) ? n + 1 : 1;
                if (!display.ContainsKey(key))
                    display[key] = raw?.ToString()?.Trim() ?? "";
            }
        }

        return Sorted(counts.Select(kv => new ValueChoice(kv.Key.Param, display[kv.Key], kv.Value)));
    }

    /// <summary>
    /// Several documents' choices merged — Set B can span multiple checked
    /// links. The same (parameter, value) collapses across links with the counts
    /// SUMMED and the first spelling seen kept for display, so a value picked
    /// here matches in whichever checked link actually carries it.
    /// </summary>
    public static List<ValueChoice> MergeChoices(IEnumerable<List<ValueChoice>> perDocument)
    {
        Dictionary<(string Param, string Norm), int> counts = [];
        Dictionary<(string Param, string Norm), string> display = [];

        foreach (List<ValueChoice> choices in perDocument)
        {
            foreach (ValueChoice c in choices)
            {
                (string, string) key = (c.ParamName, Norm(c.Display));
                counts[key] = counts.TryGetValue(key, out int n) ? n + c.Count : c.Count;
                if (!display.ContainsKey(key))
                    display[key] = c.Display;
            }
        }

        return Sorted(counts.Select(kv => new ValueChoice(kv.Key.Param, display[kv.Key], kv.Value)));
    }

    /// <summary>System Type group first, then Description, alphabetical within each.</summary>
    private static List<ValueChoice> Sorted(IEnumerable<ValueChoice> choices)
    {
        List<ValueChoice> results = [.. choices];

        results.Sort((a, b) =>
        {
            int oa = a.ParamName == ClashSetBuilder.SystemTypeParam ? 0 : 1;
            int ob = b.ParamName == ClashSetBuilder.SystemTypeParam ? 0 : 1;
            return oa != ob
                ? oa.CompareTo(ob)
                : string.Compare(a.Display, b.Display, StringComparison.OrdinalIgnoreCase);
        });

        return results;
    }
}
