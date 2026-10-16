using Autodesk.Revit.DB;

namespace Ribbit.Native.Revit;

/// <summary>Where a parameter lives, which decides how it reads and displays.</summary>
internal enum ParameterKind
{
    Instance,
    Type,
    Shared,
}

/// <summary>One parameter name found in the model, and how common it is.</summary>
internal sealed record ParameterChoice(string Name, int ElementCount, ParameterKind Kind)
{
    public bool OnType => Kind == ParameterKind.Type;

    public string KindLabel => Kind switch
    {
        ParameterKind.Type => "type",
        ParameterKind.Shared => "shared",
        _ => "instance",
    };

    public string Display => $"{Name}  ({KindLabel})";
}

/// <summary>
/// What parameters the model actually has, so a set can be built on any of them
/// rather than only System Type and Description.
///
/// SAMPLED, NOT EXHAUSTIVE. Listing every parameter name means walking
/// <c>element.Parameters</c>, which loads the full element — the slow-filter cost
/// the whole scan design avoids. Every instance of a type carries the same
/// parameter names, so reading a handful per category finds the same NAMES as
/// reading all of them, for a fraction of the work. The counts are therefore
/// "how many of the sampled elements had it", which is enough to sort the common
/// ones to the top and is labelled as approximate wherever it is shown.
///
/// A name missed by sampling is not lost: "add by hand" takes any name, and the
/// search reads whatever a rule asks for.
/// </summary>
internal static class ClashParameterCatalog
{
    /// <summary>
    /// Every parameter name in the searched elements — instance, type and
    /// shared — most common first.
    ///
    /// COMPLETE, AND STILL CHEAP, because it walks one element PER TYPE rather
    /// than a sample or every element. Every instance of a type carries the same
    /// parameter names, so one instance answers for all of them; a model with
    /// 80,000 elements usually has a few hundred types, and the cost follows the
    /// types. An earlier version sampled 30 elements per category, which was
    /// faster still but could miss a parameter that only an unusual family
    /// carries — and a search box over an incomplete list is worse than no
    /// search box, because a name that is absent looks like a name that does not
    /// exist.
    ///
    /// Instance, type and shared are listed separately: the same name can exist
    /// as more than one, they read through different lookups, and which one a
    /// value came from decides whether it matches.
    ///
    /// The count is elements represented, not elements inspected — each type
    /// contributes its whole instance count, so "commonest first" is honest.
    /// </summary>
    public static List<ParameterChoice> Discover(
        IReadOnlyDictionary<BuiltInCategory, List<Element>> buckets,
        Action<int, int>? progress = null)
    {
        Dictionary<(string Name, ParameterKind Kind), int> counts = [];

        // One representative element per type, and how many elements it stands
        // for. Instances with no type still need representing, keyed by 0.
        Dictionary<long, (Element Sample, int Count)> byType = [];

        foreach (Element el in buckets.Values.SelectMany(v => v))
        {
            long typeKey;
            try
            {
                ElementId typeId = el.GetTypeId();
                typeKey = typeId is null || typeId == ElementId.InvalidElementId
                    ? 0
                    : RevitCompat.IdKey(typeId);
            }
            catch (Exception)
            {
                typeKey = 0;
            }

            byType[typeKey] = byType.TryGetValue(typeKey, out (Element Sample, int Count) existing)
                ? (existing.Sample, existing.Count + 1)
                : (el, 1);
        }

        List<KeyValuePair<long, (Element Sample, int Count)>> types = [.. byType];

        for (int i = 0; i < types.Count; i++)
        {
            try
            {
                progress?.Invoke(i, types.Count);
            }
            catch (Exception)
            {
                // Progress must never take discovery down.
            }

            (Element sample, int count) = types[i].Value;

            AddNames(sample, onType: false, count, counts);

            try
            {
                ElementId typeId = sample.GetTypeId();
                if (typeId is not null && typeId != ElementId.InvalidElementId)
                {
                    Element? type = sample.Document?.GetElement(typeId);
                    if (type is not null)
                        AddNames(type, onType: true, count, counts);
                }
            }
            catch (Exception)
            {
                // An element whose type will not resolve contributes only its
                // instance parameters.
            }
        }

        List<ParameterChoice> result =
        [
            .. counts.Select(kv => new ParameterChoice(kv.Key.Name, kv.Value, kv.Key.Kind))
        ];

        // Commonest first, then alphabetical - the parameter you want is nearly
        // always one most elements carry.
        result.Sort((a, b) =>
        {
            int byCount = b.ElementCount.CompareTo(a.ElementCount);
            return byCount != 0
                ? byCount
                : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        });

        return result;
    }

    /// <summary>
    /// Adds one element's parameter names, crediting them with the number of
    /// elements this one stands for.
    ///
    /// A shared parameter is reported as SHARED rather than by where it sits.
    /// That is the distinction a modeller thinks in — "our shared parameter"
    /// versus "the built-in one" — and it is the one most likely to be the
    /// answer when two parameters have confusingly similar names.
    /// </summary>
    private static void AddNames(
        Element el,
        bool onType,
        int represents,
        Dictionary<(string Name, ParameterKind Kind), int> counts)
    {
        try
        {
            foreach (Parameter p in el.Parameters)
            {
                try
                {
                    string name = p.Definition?.Name ?? "";
                    if (name.Length == 0)
                        continue;

                    ParameterKind kind = p.IsShared
                        ? ParameterKind.Shared
                        : onType ? ParameterKind.Type : ParameterKind.Instance;

                    (string, ParameterKind) key = (name, kind);
                    counts[key] = counts.TryGetValue(key, out int n)
                        ? n + represents
                        : represents;
                }
                catch (Exception)
                {
                    // Skip a parameter that will not name itself.
                }
            }
        }
        catch (Exception)
        {
            // An element whose parameters will not enumerate contributes none.
        }
    }

    /// <summary>
    /// Distinct values of ONE named parameter across the buckets, with counts —
    /// the second half of picking a value for a parameter that is not System
    /// Type or Description.
    ///
    /// Read through <see cref="ClashParameterReader.FindParameter"/>, the same
    /// reader the search uses for an arbitrary name, so a value offered here
    /// matches what the run reads back. Using a different lookup would produce a
    /// list of values that silently match nothing.
    /// </summary>
    public static List<ValueChoice> ValuesFor(
        ClashParameterReader reader,
        IReadOnlyDictionary<BuiltInCategory, List<Element>> buckets,
        string paramName,
        Action<int, int>? progress = null)
    {
        List<Element> all = [.. buckets.Values.SelectMany(v => v)];

        Dictionary<string, int> counts = [];
        Dictionary<string, string> display = [];

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

            object? raw = reader.FindParameter(all[i], paramName);
            string text = raw?.ToString()?.Trim() ?? "";
            if (text.Length == 0)
                continue;

            string norm = text.ToLowerInvariant();
            counts[norm] = counts.TryGetValue(norm, out int n) ? n + 1 : 1;
            if (!display.ContainsKey(norm))
                display[norm] = text;
        }

        List<ValueChoice> result =
        [
            .. counts.Select(kv => new ValueChoice(paramName, display[kv.Key], kv.Value))
        ];

        result.Sort((a, b)
            => string.Compare(a.Display, b.Display, StringComparison.OrdinalIgnoreCase));

        return result;
    }
}
