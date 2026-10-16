using System.IO;
using System.Text.Json;

namespace Ribbit.Native.Settings;

/// <summary>
/// A saved clash test: a named Set A / Set B / sources / clearance combination
/// you can re-run.
///
/// Navisworks' central idea, and the thing that turns clash detection from "fill
/// the form in again" into "run last week's test and see what changed".
///
/// IT DOES NOT CARRY A LEDGER. Status is keyed by clash PAIR, so if two tests
/// both find pipe X against beam Y that is one physical clash with one status.
/// Per-test ledgers would let it read Resolved in one test and New in another,
/// which would be lying to whoever opened the second.
/// </summary>
public sealed class ClashTest
{
    /// <summary>Stable identity, so renaming a test breaks nothing.</summary>
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    /// <summary>
    /// Whether the test searches every loaded model, or a named list.
    ///
    /// A MODE, not just a list. "All loaded models" picks up a link added next
    /// month; a pinned list deliberately does not. Storing only a list would
    /// silently exclude new links, which is the same class of bug as a set that
    /// matches nothing.
    /// </summary>
    public bool AllModels { get; set; } = true;

    public List<string> SourceNames { get; set; } = [];

    /// <summary>Sets by stable id, so a rename in the library keeps the test working.</summary>
    public List<string> SetAIds { get; set; } = [];

    public List<string> SetBIds { get; set; } = [];

    public string SetAKeyword { get; set; } = "";

    public string SetBKeyword { get; set; } = "";

    public List<string[]> SetAValues { get; set; } = [];

    public List<string[]> SetBValues { get; set; } = [];

    public string PadValue { get; set; } = "";

    public string PadUnit { get; set; } = "";

    public string ToleranceValue { get; set; } = "";

    public string ToleranceUnit { get; set; } = "";

    public bool DescriptionCableTrayOnly { get; set; } = true;

    /// <summary>Stored so a test is re-runnable without reconfiguring anything.</summary>
    public string CsvFolder { get; set; } = "";

    /// <summary>The model it was built in — shown when a test is opened elsewhere.</summary>
    public string ModelTitle { get; set; } = "";

    /// <summary>Round-trip "o" format, or empty if never run.</summary>
    public string LastRunUtc { get; set; } = "";

    /// <summary>-1 when never run, so "0 clashes" and "not run" stay distinct.</summary>
    public int LastRunClashes { get; set; } = -1;

    /// <summary>"last run 2 Aug, 214 clashes" — how a dead test becomes obvious.</summary>
    public string LastRunSummary()
    {
        if (LastRunUtc.Length == 0 || LastRunClashes < 0)
            return "never run";

        if (!DateTime.TryParse(
                LastRunUtc, null, System.Globalization.DateTimeStyles.RoundtripKind,
                out DateTime when))
            return $"{LastRunClashes} clash(es)";

        return $"last run {when.ToLocalTime():d MMM HH:mm} · {LastRunClashes} clash(es)";
    }

    public ClashTest Copy() => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Name = Name,
        AllModels = AllModels,
        SourceNames = [.. SourceNames],
        SetAIds = [.. SetAIds],
        SetBIds = [.. SetBIds],
        SetAKeyword = SetAKeyword,
        SetBKeyword = SetBKeyword,
        SetAValues = [.. SetAValues.Select(v => (string[])v.Clone())],
        SetBValues = [.. SetBValues.Select(v => (string[])v.Clone())],
        PadValue = PadValue,
        PadUnit = PadUnit,
        ToleranceValue = ToleranceValue,
        ToleranceUnit = ToleranceUnit,
        DescriptionCableTrayOnly = DescriptionCableTrayOnly,
        CsvFolder = CsvFolder,
        ModelTitle = ModelTitle,
    };
}

/// <summary>
/// The saved tests, in one shared file beside the set library.
///
/// Same fail-open contract as <see cref="ClashSetLibrary"/>: a corrupt file
/// returns empty and is LEFT ON DISK to be inspected, never silently
/// overwritten. Losing a week of saved tests to a stray character would be worse
/// than any error message.
/// </summary>
public sealed class ClashTestLibrary
{
    public List<ClashTest> Tests { get; set; } = [];

    /// <summary>Points the library at a scratch file for a test; null is the real one.</summary>
    internal static string? PathOverride { get; set; }

    private static string PathFor()
    {
        if (!string.IsNullOrEmpty(PathOverride))
            return PathOverride!;

        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Ribbit");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "clash_tests.json");
    }

    public static ClashTestLibrary Load()
    {
        try
        {
            string path = PathFor();
            if (!File.Exists(path))
                return new ClashTestLibrary();

            ClashTestLibrary? loaded =
                JsonSerializer.Deserialize<ClashTestLibrary>(File.ReadAllText(path));

            if (loaded is not null)
            {
                loaded.Tests ??= [];
                loaded._loadedIds = [.. loaded.Tests.Select(t => t.Id)];

                // One-time repair of tests that froze their sets' contents. Both
                // are idempotent, and both save immediately - a repair redone on
                // every load would be no repair at all.
                bool changed = loaded.EnsureIds();
                changed |= loaded.StripSetOwnedRules(ClashSetLibrary.Load());

                if (changed)
                    loaded.Save();

                return loaded;
            }
        }
        catch (Exception)
        {
            // Corrupt or unreadable - hand back an empty library WITHOUT saving,
            // so the file survives for inspection.
            return new ClashTestLibrary();
        }

        return new ClashTestLibrary();
    }

    /// <summary>
    /// Drops rules from a test that its own chosen SETS already supply.
    ///
    /// Repairs tests written by the post-run "save this search as a test?" offer,
    /// which stored the MERGED keyword and values — the typed ones plus every
    /// keyword and value from the chosen sets. That froze the sets' contents into
    /// the test, so the test kept referencing the set AND carried a copy of it.
    ///
    /// The visible symptom is one-sided and easy to miss: ADDING a value to a set
    /// reaches those tests, because sets are merged in on load. REMOVING one does
    /// not, because the frozen copy is still there. The set library's whole
    /// promise is that a set is edited in one place.
    ///
    /// Safe to run repeatedly. Once a test's own list holds nothing its sets
    /// supply, later passes find nothing to remove.
    ///
    /// A value that was ALSO hand-picked is dropped too. That is correct rather
    /// than lossy: the set still supplies it, so the search is unchanged — and if
    /// the set is later removed from the test, it SHOULD stop matching.
    ///
    /// A set that no longer resolves contributes nothing, so anything it once
    /// supplied stays frozen. That is the honest outcome: with the set gone there
    /// is no way to tell its values from hand-picked ones.
    /// </summary>
    public bool StripSetOwnedRules(ClashSetLibrary sets)
    {
        bool changed = false;

        foreach (ClashTest test in Tests)
        {
            (bool a, string keywordA, List<string[]> valuesA) =
                Strip(sets, test.SetAIds, test.SetAKeyword, test.SetAValues);

            if (a)
            {
                test.SetAKeyword = keywordA;
                test.SetAValues = valuesA;
                changed = true;
            }

            (bool b, string keywordB, List<string[]> valuesB) =
                Strip(sets, test.SetBIds, test.SetBKeyword, test.SetBValues);

            if (b)
            {
                test.SetBKeyword = keywordB;
                test.SetBValues = valuesB;
                changed = true;
            }
        }

        return changed;
    }

    private static (bool Changed, string Keyword, List<string[]> Values) Strip(
        ClashSetLibrary sets, List<string> setIds, string keyword, List<string[]> values)
    {
        HashSet<(string, string)> ownedValues = [];
        HashSet<string> ownedKeywords = [];

        foreach (string id in setIds ?? [])
        {
            ClashSet? set = sets.FindById(id);
            if (set is null)
                continue;

            foreach ((string param, string value) in set.Pairs())
                ownedValues.Add((param.ToLowerInvariant(), value.ToLowerInvariant()));

            foreach (string word in set.Keywords)
                ownedKeywords.Add(word.Trim().ToLowerInvariant());
        }

        if (ownedValues.Count == 0 && ownedKeywords.Count == 0)
            return (false, keyword, values);

        List<string[]> keptValues =
        [
            .. (values ?? []).Where(v =>
                v.Length < 2
                || !ownedValues.Contains((v[0].ToLowerInvariant(), v[1].ToLowerInvariant())))
        ];

        List<string> keptWords =
        [
            .. (keyword ?? "").Split(',')
                .Select(w => w.Trim())
                .Where(w => w.Length > 0 && !ownedKeywords.Contains(w.ToLowerInvariant()))
        ];

        string newKeyword = string.Join(",", keptWords);

        return (keptValues.Count != (values?.Count ?? 0) || newKeyword != (keyword ?? ""),
                newKeyword,
                keptValues);
    }

    /// <summary>Ids this instance loaded, so Save can tell an edit from a deletion.</summary>
    private HashSet<string> _loadedIds = [];

    /// <summary>The file as it stands now, or nothing when it cannot be read.</summary>
    private static List<ClashTest> ReadFromDisk()
    {
        try
        {
            string path = PathFor();

            return File.Exists(path)
                ? JsonSerializer.Deserialize<ClashTestLibrary>(File.ReadAllText(path))?.Tests ?? []
                : [];
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>
    /// Writes the tests, MERGED with whatever is on disk right now.
    ///
    /// Same reason as <see cref="ClashSetLibrary.Save"/>: two Revits are open at
    /// once and each holds its own copy, so a wholesale write means the last
    /// save wins and the other Revit's tests are gone. Merged per TEST by id,
    /// with tests this instance deleted staying deleted.
    /// </summary>
    public void Save()
    {
        try
        {
            // NOTHING REACHES DISK WITHOUT AN ID, exactly as in
            // ClashSetLibrary.Save. The merge below matches on id and skips
            // anything blank, so an id-less test would be dropped by the next
            // save from another Revit - and a blank id resolves to no test at
            // all. Both creation paths (SaveTest and the post-run offer) do mint
            // one today, so this is the guard that keeps a third one from
            // reintroducing what the set library actually suffered from.
            EnsureIds();

            List<ClashTest> merged = [.. Tests];
            HashSet<string> mine = [.. Tests.Select(t => t.Id)];

            foreach (ClashTest theirs in ReadFromDisk())
            {
                if (theirs.Id.Length > 0
                    && !mine.Contains(theirs.Id)
                    && !_loadedIds.Contains(theirs.Id))
                {
                    merged.Add(theirs);
                }
            }

            Tests = merged;
            _loadedIds = [.. merged.Select(t => t.Id)];

            // TEMP-THEN-SWAP, and it is the merge above that needs it.
            // WriteAllText truncates at open, so another Revit reading in that
            // window gets a partial file; ReadFromDisk fails OPEN to an empty
            // list, and this save would then write "mine only" - deleting that
            // instance's tests, which is the exact loss the merge exists to
            // prevent. An atomic swap means a reader sees the old file or the
            // new one, never half of one.
            string path = PathFor();
            string temp = Ribbit.Geometry.Clash.AtomicFile.TempPathFor(path);
            File.WriteAllText(temp,
                JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            Ribbit.Geometry.Clash.AtomicFile.Commit(temp, path);
        }
        catch (Exception)
        {
            // A preference that will not persist must not take the tool down.
        }
    }

    /// <summary>Gives every test an id and repairs duplicates. See ClashSetLibrary.EnsureIds.</summary>
    public bool EnsureIds()
    {
        HashSet<string> seen = [];
        bool changed = false;

        foreach (ClashTest test in Tests)
        {
            if (test.Id.Length == 0 || !seen.Add(test.Id))
            {
                test.Id = Guid.NewGuid().ToString("N");
                seen.Add(test.Id);
                changed = true;
            }
        }

        return changed;
    }

    public ClashTest? FindById(string id)
        => id.Length == 0
            ? null
            : Tests.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.Ordinal));

    public ClashTest? Find(string name)
        => Tests.FirstOrDefault(
            t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));

    public bool NameTaken(string name, ClashTest? except = null)
        => Tests.Any(t => !ReferenceEquals(t, except)
                          && string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
}
