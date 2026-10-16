using System.Diagnostics;
using Autodesk.Revit.DB;
using Ribbit.Geometry.Clash;
using Ribbit.Native.Settings;

namespace Ribbit.Native.Revit;

/// <summary>One model a federated run may draw from: the host, or a link.</summary>
internal sealed record ClashSource(string Name, Document Document, bool IsHost)
{
    /// <summary>Shown wherever a source is named, so the host is never ambiguous.</summary>
    public string Label => IsHost ? $"{Name} (host)" : Name;
}

/// <summary>What one source cost, so a slow link is identifiable.</summary>
/// <param name="Categories">What this one collection fetched - so a report can tell work it reused from work it did not.</param>
internal sealed record SourceTiming(
    string Label, double Seconds, int Elements, IReadOnlyList<BuiltInCategory> Categories);

/// <summary>
/// The federation: the host document plus every loaded link, collected once and
/// shared by both sides of the search.
///
/// COLLECT ONCE, USE TWICE. Set A and Set B both draw from the same sources, so
/// bucketing per document is cached here rather than repeated per side — the
/// three older tools already cache this way and the federated one must not
/// regress it, since it pays the cost once per source rather than once.
///
/// Timing is per source, not per run. One aggregate "Scan: 9.3s" hides the fact
/// that eight of those seconds were one badly-modelled link, which is the only
/// thing you can actually act on.
/// </summary>
internal sealed class FederatedSources
{
    /// <summary>
    /// Every category ever collected, per document - a superset of what any one
    /// run declares. <see cref="BucketsFor"/> hands back only the categories in
    /// play now, so keeping more here never changes what a search sees.
    /// </summary>
    private readonly Dictionary<Document, Dictionary<BuiltInCategory, List<Element>>> _collected = [];
    private readonly List<SourceTiming> _timings = [];

    /// <summary>
    /// Categories beyond the 24 MEP ones, named by the sets actually chosen —
    /// Structural Framing, Walls, Floors.
    ///
    /// The 24 are an MEP list, so a structural category was previously
    /// uncollectable and any set naming one matched nothing at all. Widening the
    /// collection is safe HERE and nowhere else: Federated Clash has its own
    /// ledger and no earlier reports to make incomparable.
    /// </summary>
    private List<BuiltInCategory> _extra = [];

    /// <summary>
    /// Declares the extra categories this run needs, before anything is
    /// collected.
    ///
    /// Buckets gathered without a category cannot answer questions about it, so
    /// a newly declared category IS collected - but only that category: what is
    /// already collected is kept. It used to throw every bucket away whenever
    /// the list changed, so opening the workset audit for one set (which
    /// declares only that set's categories) made the next Browse or Run collect
    /// every model again.
    ///
    /// Narrowing collects nothing and hides what is no longer declared:
    /// <see cref="BucketsFor"/> returns exactly the categories in play, because
    /// a set's value rules run over every bucket it is handed and a category
    /// left visible from an earlier run would change what a search matches.
    /// </summary>
    public void UseExtraCategories(IEnumerable<BuiltInCategory> extra)
    {
        HashSet<BuiltInCategory> known = [.. ClashCategories.Bics];

        _extra =
        [
            .. extra.Distinct().Where(b => !known.Contains(b)).OrderBy(b => (long)b)
        ];
    }

    /// <summary>
    /// Every model available to a federated run: the host first, then each link
    /// whose document can actually be read.
    ///
    /// An unloaded link has no document, so it silently contributes nothing — it
    /// is listed by the window as unavailable rather than offered and then
    /// quietly ignored.
    /// </summary>
    public static List<ClashSource> Discover(Document host)
    {
        List<ClashSource> sources =
        [
            new ClashSource(SafeTitle(host), host, IsHost: true),
        ];

        List<RevitLinkInstance> links;
        try
        {
            links = ClashLinks.GetLinkInstances(host);
        }
        catch (Exception)
        {
            return sources;
        }

        // One entry per linked DOCUMENT, not per instance. A file placed twice is
        // one source to search; the engine applies both placements' transforms
        // when it builds the boxes.
        HashSet<Document> seen = [];

        foreach (RevitLinkInstance li in links)
        {
            try
            {
                Document? ldoc = li.GetLinkDocument();
                if (ldoc is null || !seen.Add(ldoc))
                    continue;

                sources.Add(new ClashSource(ClashLinks.DisplayName(li), ldoc, IsHost: false));
            }
            catch (Exception)
            {
                // A link that will not open is simply not a source.
            }
        }

        return sources;
    }

    /// <summary>
    /// True when a model name a saved test pinned refers to this source.
    ///
    /// THE HOST IS COMPARED NORMALISED. Its name is <c>doc.Title</c>, which
    /// changes with how the file was opened - detached, as a local - so a test
    /// pinned to the host one day read it as a missing model the next. A batch
    /// then searched nothing ("no models to search"), and the setup window
    /// silently unticked the host. Links keep an exact match: their names come
    /// from the link instance, which opening the host does not change.
    /// </summary>
    /// <param name="users">Every known username, when the caller compares many
    /// names in one go - read once rather than per comparison. Null reads it.</param>
    public static bool Matches(
        ClashSource source, string savedName, IReadOnlyList<string>? users = null)
    {
        if (string.Equals(source.Name, savedName, StringComparison.OrdinalIgnoreCase))
            return true;

        if (!source.IsHost)
            return false;

        // Every user seen here, not just this one: a saved set made on a copy
        // detached from a colleague's local carries THEIR name.
        users ??= UsersFor(source);
        return string.Equals(
            ModelTitle.Normalize(source.Name, users),
            ModelTitle.Normalize(savedName, users),
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Every username known on this PC, starting with the one this source's session runs as.</summary>
    public static List<string> UsersFor(ClashSource source)
    {
        string username = "";
        try
        {
            username = source.Document.Application?.Username ?? "";
        }
        catch (Exception)
        {
            // Without a username only the remembered ones are compared away.
        }

        return KnownUsers.For(username);
    }

    /// <summary>
    /// The category buckets for one source, collected on first use and cached.
    ///
    /// <see cref="ClashCategories.CollectByCategoryFast"/> does this in ONE
    /// multicategory pass rather than 24 collectors, and category is a quick
    /// filter — header information only, no element loading — so this stays cheap
    /// even across many links. The expensive half of a run is the parameter
    /// reads that follow, not this.
    /// </summary>
    public Dictionary<BuiltInCategory, List<Element>> BucketsFor(ClashSource source)
    {
        List<BuiltInCategory> wanted = [.. ClashCategories.Bics, .. _extra];

        if (!_collected.TryGetValue(source.Document, out Dictionary<BuiltInCategory, List<Element>>? have))
            _collected[source.Document] = have = [];

        // Two passes at most: the MEP baseline, then every extra category
        // together. A timing covers exactly what its pass fetched, so a run that
        // reuses only the baseline is not credited with the seconds a Structural
        // Framing audit spent - which one shared pass made impossible to
        // separate. Extras share a pass with each other: one more walk of a big
        // link per category would cost more than the report line is worth, and
        // every extra comes from the same chosen sets.
        HashSet<BuiltInCategory> baseline = [.. ClashCategories.Bics];
        List<BuiltInCategory> missing = [.. wanted.Where(b => !have.ContainsKey(b))];

        foreach (List<BuiltInCategory> pass in missing
                     .GroupBy(baseline.Contains)
                     .Select(g => g.ToList()))
            Collect(source, have, pass);

        // Exactly the categories in play - see UseExtraCategories.
        return wanted.ToDictionary(b => b, b => have[b]);
    }

    private void Collect(
        ClashSource source,
        Dictionary<BuiltInCategory, List<Element>> have,
        List<BuiltInCategory> missing)
    {
        Stopwatch sw = Stopwatch.StartNew();
        Dictionary<BuiltInCategory, List<Element>> got;

        try
        {
            got = ClashCategories.CollectByCategoryFast(source.Document, missing);
        }
        catch (Exception)
        {
            // A source that will not collect contributes nothing rather than
            // taking the whole run down with it.
            got = [];
        }

        // Recorded even when empty, so a category with no elements is not
        // asked for again on every call.
        foreach (BuiltInCategory b in missing)
            have[b] = got.TryGetValue(b, out List<Element>? list) ? list : [];

        _timings.Add(new SourceTiming(
            source.Label, sw.Elapsed.TotalSeconds, missing.Sum(b => have[b].Count), missing));
    }

    /// <summary>
    /// A bookmark in the collect timings: take it when a run starts, and
    /// <see cref="TimingSummary"/> from it reports only what that run collected.
    ///
    /// The federation now keeps what it collected for the whole session, so its
    /// timings do too - without a bookmark a run's report would include the
    /// collection an earlier audit or Browse did for categories the run never
    /// used.
    /// </summary>
    public int TimingMark => _timings.Count;

    /// <summary>Whether anything was collected since <paramref name="mark"/>.</summary>
    public bool CollectedSince(int mark) => _timings.Count > mark;

    /// <summary>The categories collected for a run right now: the 24 MEP ones plus the declared extras.</summary>
    public IReadOnlyList<BuiltInCategory> CategoriesInPlay => [.. ClashCategories.Bics, .. _extra];

    /// <summary>
    /// The collections between two bookmarks - optionally only those of the
    /// given sources that fetched at least one of the given categories.
    ///
    /// The filter is what lets a report say what a run REUSED: an audit that
    /// collected Structural Framing in a link the run never searched is earlier
    /// work, but not this run's.
    /// </summary>
    public List<SourceTiming> TimingsBetween(
        int since,
        int until = int.MaxValue,
        IReadOnlyCollection<string>? labels = null,
        IReadOnlyCollection<BuiltInCategory>? categories = null)
        => [.. _timings.Take(until).Skip(since).Where(t =>
            (labels is null || labels.Contains(t.Label))
            && (categories is null || t.Categories.Any(categories.Contains)))];

    /// <summary>
    /// "Host 0.8s · ST-Structure 2.1s · AR-Arch 6.4s" — for the report. One
    /// figure per source, however many top-ups its collection took.
    /// </summary>
    public static string Summarize(IEnumerable<SourceTiming> timings)
    {
        List<SourceTiming> list = [.. timings];

        return list.Count == 0
            ? "nothing collected"
            : string.Join(" · ", list
                .GroupBy(t => t.Label)
                .Select(g => $"{g.Key} {g.Sum(t => t.Seconds):F1}s"));
    }

    /// <summary>Every collection between two bookmarks, summarised per source.</summary>
    public string TimingSummary(int since = 0, int until = int.MaxValue)
        => Summarize(TimingsBetween(since, until));

    private static string SafeTitle(Document doc)
    {
        try
        {
            return doc.Title ?? "This model";
        }
        catch (Exception)
        {
            return "This model";
        }
    }
}
