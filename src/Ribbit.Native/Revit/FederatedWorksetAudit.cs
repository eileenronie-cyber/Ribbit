using Autodesk.Revit.DB;
using Ribbit.Geometry.Clash;
using Ribbit.Native.Settings;

namespace Ribbit.Native.Revit;

/// <summary>
/// What the workset audit reads from the models: every user workset across the
/// ticked models, and how many of ONE set's elements sit on each.
///
/// The set is built with its exclusions IGNORED - the audit is how they are
/// chosen, so it has to show what each workset would cost, including the ones
/// already left out. Built through <see cref="ClashLinkSetBuilder.BuildSet"/>, the
/// search's own builder, per document, so the counts are what a run would see.
///
/// Aggregated BY NAME, the way exclusions apply. A model that is not workshared
/// contributes nothing: it has no worksets to leave out.
/// </summary>
internal static class FederatedWorksetAudit
{
    public static List<WorksetUsage> Collect(
        ClashParameterReader reader,
        FederatedSources federation,
        IReadOnlyList<ClashSource> sources,
        ClashSet set,
        bool trayOnly,
        WorksetLookup? lookup = null)
    {
        lookup ??= new WorksetLookup();

        List<BuiltInCategory> bics = set.Resolved();

        // Declared before collecting, as RunSearch does: a set built on
        // Structural Framing would otherwise count nothing.
        federation.UseExtraCategories(bics);

        Dictionary<string, Entry> byName = new(StringComparer.OrdinalIgnoreCase);
        string keywords = string.Join(",", set.Keywords);
        List<(string Param, string Value)> values = set.Pairs();

        foreach (ClashSource src in sources)
        {
            List<(string Name, bool Hidden)> worksets = WorksetLookup.UserWorksets(src.Document);
            if (worksets.Count == 0)
                continue;

            foreach ((string name, bool hidden) in worksets)
                EntryFor(byName, name).Seen(src.Label, hidden);

            List<Element> elements = ClashLinkSetBuilder.BuildSet(
                reader, federation.BucketsFor(src), keywords, trayOnly, bics, values, null);

            foreach (Element e in elements)
            {
                string name = lookup.NameOf(e);
                if (name.Length == 0)
                    continue;

                Entry entry = EntryFor(byName, name);
                entry.Seen(src.Label, false);
                entry.Count++;
            }
        }

        return [.. byName.Values.Select(v => new WorksetUsage(v.Name, v.Models, v.Count, v.Hidden))];
    }

    private static Entry EntryFor(Dictionary<string, Entry> byName, string name)
    {
        if (!byName.TryGetValue(name, out Entry? e))
            byName[name] = e = new Entry(name);

        return e;
    }

    private sealed class Entry(string name)
    {
        public string Name { get; } = name;

        public List<string> Models { get; } = [];

        public int Count { get; set; }

        public bool Hidden { get; private set; }

        public void Seen(string model, bool hidden)
        {
            if (!Models.Contains(model, StringComparer.OrdinalIgnoreCase))
                Models.Add(model);

            Hidden |= hidden;
        }
    }
}
