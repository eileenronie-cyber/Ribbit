using System.IO;
using System.Text.Json;
using Autodesk.Revit.DB;
using Ribbit.Native.Revit;

namespace Ribbit.Native.Settings;

/// <summary>
/// One exact parameter value inside a set — "[System Type] 52 - Vent".
///
/// The parameter travels WITH the value and is never flattened away. A value is
/// matched against the parameter it came from, through that parameter's own
/// reader; drop the pairing and a value stops matching what the picker counted.
/// </summary>
public sealed class SetValue
{
    public string Param { get; set; } = "";

    public string Value { get; set; } = "";
}

/// <summary>
/// A named, reusable set: exact values, keywords and whole categories, OR'd.
///
/// An element joins the set if ANY rule matches. The three kinds coexist because
/// a real set is usually one kind but occasionally mixed — "52 Series" is three
/// values, while "All MEP Devices/Equipment" is twelve categories.
///
/// Categories are stored by ENUM NAME, not by number. The numeric values of
/// BuiltInCategory are large negatives that mean nothing on inspection, and a
/// settings file a user may open should be readable. An unrecognised name is
/// dropped on load rather than failing the whole set, so a file written by a
/// newer build stays usable by an older one.
///
/// A VALUE SET IS JUST STRINGS, which is what makes it portable: "52 - Vent"
/// means the same thing in the host model and inside a link, so one set serves
/// both sides of a link clash.
/// </summary>
public sealed class ClashSet
{
    /// <summary>
    /// Stable identity, independent of the name.
    ///
    /// EVERYTHING THAT REFERENCES A SET USES THIS. Names used to be the
    /// reference, which meant renaming a set silently orphaned every saved
    /// selection and every test that named it — visible only as a "missing" chip
    /// once you happened to open the window. An id survives a rename, and
    /// survives someone editing the file by hand.
    ///
    /// Blank on a file written before ids existed; <see cref="ClashSetLibrary.Load"/>
    /// fills those in once and saves.
    /// </summary>
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    public List<string> Categories { get; set; } = [];

    /// <summary>Exact matches, each against the parameter it was picked from.</summary>
    public List<SetValue> Values { get; set; } = [];

    /// <summary>Case-insensitive substrings, matched like a typed keyword.</summary>
    public List<string> Keywords { get; set; } = [];

    /// <summary>
    /// Worksets this set LEAVES OUT, by name: an element the rules above select
    /// is dropped when it sits on one of these, in whichever model it lives.
    ///
    /// FEDERATED CLASH ONLY. The library is shared with the three older clash
    /// tools, which ignore this list - the set editor says so beside it. Kept on
    /// the SET rather than on a run because the reason a workset is left out
    /// ("MEP_ZZ_Typical is typical-floor copies") is a fact about the content,
    /// true for every test that uses the set.
    ///
    /// Names, compared trimmed and ignoring case: a WorksetId means nothing
    /// outside its own document. See <see cref="Ribbit.Geometry.Clash.WorksetAudit"/>.
    /// Missing from a file written before this existed, which loads as empty.
    /// </summary>
    public List<string> ExcludedWorksets { get; set; } = [];

    /// <summary>
    /// Takes every RULE from <paramref name="other"/> - values, keywords,
    /// categories and left-out worksets - keeping this set's id.
    ///
    /// THE ONE PLACE A SET'S RULES ARE COPIED OVER. Editing a set and updating
    /// one from an import each copied the fields one by one, so a field added
    /// later (the left-out worksets) was silently dropped by whichever copy was
    /// not updated - an edit made in Clash Detect, which never shows the list,
    /// would have wiped it.
    /// </summary>
    public void CopyRulesFrom(ClashSet other)
    {
        Categories = [.. other.Categories];
        Values = [.. other.Values.Select(v => new SetValue { Param = v.Param, Value = v.Value })];
        Keywords = [.. other.Keywords];
        ExcludedWorksets = Ribbit.Geometry.Clash.WorksetAudit.Normalize(other.ExcludedWorksets);
    }

    /// <summary>The categories that parse, in the order stored.</summary>
    public List<BuiltInCategory> Resolved()
    {
        List<BuiltInCategory> outp = [];

        foreach (string name in Categories)
        {
            if (Enum.TryParse(name, out BuiltInCategory bic) && !outp.Contains(bic))
                outp.Add(bic);
        }

        return outp;
    }

    /// <summary>The values in the (Param, Value) shape the search layer speaks.</summary>
    public List<(string Param, string Value)> Pairs()
        => [.. Values.Where(v => v.Value.Length > 0).Select(v => (v.Param, v.Value))];

    // Derived helpers (HasCategories, RuleCount) lived here and are gone: their
    // only consumer was the force-include picker, removed when sets became
    // choosable per side. Anything added back here MUST carry [JsonIgnore] —
    // without it a computed property is written into the library file as though
    // it were data, and a reader can believe a stale "HasCategories": false over
    // the categories sitting right above it.

    /// <summary>A copy is a NEW set — it gets its own id, not the original's.</summary>
    public ClashSet Copy()
    {
        ClashSet copy = new() { Id = Guid.NewGuid().ToString("N"), Name = Name };
        copy.CopyRulesFrom(this);
        return copy;
    }
}

/// <summary>
/// The shared library of named category sets.
///
/// ONE file for the whole add-in, deliberately NOT one per clash product. The
/// three clash tools each kept their own preferences, so a group defined in
/// Clash Detect was invisible in Clash Detect Link — the thing that made the six
/// built-in toggles have to be hardcoded in the first place, since there was
/// nowhere shared to put them.
///
/// Seeded on first run with exactly the six toggles that used to be compiled in,
/// so a fresh install behaves identically and "All MEP Devices/Equipment"
/// becomes an ordinary set you can open and edit.
/// </summary>
public sealed class ClashSetLibrary
{
    public List<ClashSet> Sets { get; set; } = [];

    /// <summary>Points the library at a scratch file for a test; null is the real one.</summary>
    internal static string? PathOverride { get; set; }

    private static string PathFor()
    {
        if (!string.IsNullOrEmpty(PathOverride))
            return PathOverride!;

        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Ribbit");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "clash_sets.json");
    }

    /// <summary>
    /// The six groups that were hardcoded as <c>ClashLinkSetBuilder.CategoryToggles</c>.
    ///
    /// Transcribed from that array rather than referenced, because the point of
    /// this work is that the list stops being fixed: once seeded, the user's copy
    /// is the truth and editing it must not be overwritten by a later build.
    /// </summary>
    public static ClashSetLibrary Seed() => new()
    {
        Sets =
        [
            Make("Pipe Accessories", BuiltInCategory.OST_PipeAccessory),
            Make("Mechanical Equipment", BuiltInCategory.OST_MechanicalEquipment),
            Make("Electrical Equipment", BuiltInCategory.OST_ElectricalEquipment),
            Make("Lighting Fixtures", BuiltInCategory.OST_LightingFixtures),
            Make("Security Devices", BuiltInCategory.OST_SecurityDevices),
            Make("All MEP Devices/Equipment",
                BuiltInCategory.OST_ElectricalFixtures,
                BuiltInCategory.OST_LightingFixtures,
                BuiltInCategory.OST_LightingDevices,
                BuiltInCategory.OST_DataDevices,
                BuiltInCategory.OST_CommunicationDevices,
                BuiltInCategory.OST_TelephoneDevices,
                BuiltInCategory.OST_NurseCallDevices,
                BuiltInCategory.OST_SecurityDevices,
                BuiltInCategory.OST_FireAlarmDevices,
                BuiltInCategory.OST_MechanicalEquipment,
                BuiltInCategory.OST_ElectricalEquipment,
                BuiltInCategory.OST_PlumbingFixtures),
        ],
    };

    private static ClashSet Make(string name, params BuiltInCategory[] bics)
        => new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = name,
            Categories = [.. bics.Select(b => b.ToString())],
        };

    public static ClashSetLibrary Load()
    {
        try
        {
            string path = PathFor();
            if (!File.Exists(path))
            {
                // First run: write the seed out immediately, so what the tool
                // uses is a file the user can edit rather than a hidden default
                // that silently reappears.
                ClashSetLibrary seeded = Seed();
                seeded.Save();
                return seeded;
            }

            ClashSetLibrary? loaded =
                JsonSerializer.Deserialize<ClashSetLibrary>(File.ReadAllText(path));

            if (loaded is not null)
            {
                loaded.Sets ??= [];

                // A hand-edited "ExcludedWorksets": null would otherwise reach
                // every reader as null rather than as "leaves nothing out".
                foreach (ClashSet s in loaded.Sets)
                    s.ExcludedWorksets = Ribbit.Geometry.Clash.WorksetAudit.Normalize(s.ExcludedWorksets);

                loaded._loadedIds = [.. loaded.Sets.Select(s => s.Id)];

                // One-time id backfill for a file written before ids existed.
                // Saved immediately, because a regenerated id on every load would
                // be no better than no id at all.
                if (loaded.EnsureIds())
                    loaded.Save();

                return loaded;
            }
        }
        catch (Exception)
        {
            // A corrupt or unreadable library must not take the tool down. The
            // seed is returned WITHOUT saving, so the damaged file is left in
            // place to be inspected rather than silently overwritten.
            return Seed();
        }

        return Seed();
    }

    /// <summary>
    /// The ids this instance loaded, so Save can tell an edit from a deletion.
    /// Never serialized - a private field is not - and empty on a seeded
    /// library, which is right: a seed deleted nothing.
    /// </summary>
    private HashSet<string> _loadedIds = [];

    /// <summary>
    /// Writes the library, MERGED with whatever is on disk right now.
    ///
    /// Two Revits are open at once here - 2024 and 2025 - and each holds its own
    /// copy of this file. Writing it back wholesale means whoever saves last
    /// silently discards every set the other one made: the same
    /// read-modify-write that used to lose clash statuses.
    ///
    /// Merged per SET, by id. This instance wins for ids it holds, sets it never
    /// knew about are kept, and a set it actually DELETED stays deleted - which
    /// is what the loaded ids are for. Without them a deletion and an absence
    /// look identical, and every delete would be undone by the other Revit's
    /// next save.
    /// </summary>
    public void Save()
    {
        try
        {
            // NOTHING REACHES DISK WITHOUT AN ID. The merge below matches on id
            // and skips anything blank, so an id-less set would be dropped by
            // the next person's save - and every lookup treats a blank id as a
            // set that does not exist. Creation sites mint their own id; this is
            // the choke point that makes a missed one impossible rather than
            // merely unlikely. Idempotent: a set that already has an id keeps it.
            EnsureIds();

            List<ClashSet> merged = [.. Sets];
            HashSet<string> mine = [.. Sets.Select(s => s.Id)];

            foreach (ClashSet theirs in ReadFromDisk())
            {
                // Not mine, and not one I deleted, so somebody else added it.
                if (theirs.Id.Length > 0
                    && !mine.Contains(theirs.Id)
                    && !_loadedIds.Contains(theirs.Id))
                {
                    merged.Add(theirs);
                }
            }

            Sets = merged;
            _loadedIds = [.. merged.Select(s => s.Id)];

            // TEMP-THEN-SWAP: the merge above reads the file, and a reader that
            // catches WriteAllText mid-truncate falls open to empty - which
            // would make the next save write "mine only" and delete the other
            // Revit's sets, the very loss the merge exists to prevent. See
            // ClashTestLibrary.Save, which carries the same guard.
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

    /// <summary>The file as it stands now, or nothing when it cannot be read.</summary>
    private static List<ClashSet> ReadFromDisk()
    {
        try
        {
            string path = PathFor();

            return File.Exists(path)
                ? JsonSerializer.Deserialize<ClashSetLibrary>(File.ReadAllText(path))?.Sets ?? []
                : [];
        }
        catch (Exception)
        {
            // Unreadable on disk means there is nothing to merge, not that this
            // save should fail. The in-memory copy is still written.
            return [];
        }
    }

    /// <summary>
    /// Gives every set an id, and reports whether anything changed.
    ///
    /// Also repairs a DUPLICATE id, which a hand-copied entry in the JSON would
    /// otherwise produce — two sets answering to one reference is worse than
    /// none, because the winner depends on list order.
    /// </summary>
    public bool EnsureIds()
    {
        HashSet<string> seen = [];
        bool changed = false;

        foreach (ClashSet set in Sets)
        {
            if (set.Id.Length == 0 || !seen.Add(set.Id))
            {
                set.Id = Guid.NewGuid().ToString("N");
                seen.Add(set.Id);
                changed = true;
            }
        }

        return changed;
    }

    public ClashSet? FindById(string id)
        => id.Length == 0
            ? null
            : Sets.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.Ordinal));

    /// <summary>
    /// Ids for a stored reference list, migrating a legacy list of NAMES.
    ///
    /// Anything that no longer resolves is passed through unchanged, so a
    /// dangling reference stays visible as missing rather than disappearing —
    /// silently dropping it would turn "this set is gone" into "you never chose
    /// one".
    /// </summary>
    public List<string> ResolveIds(IEnumerable<string>? ids, IEnumerable<string>? legacyNames)
    {
        List<string> stored = [.. ids ?? []];
        if (stored.Count > 0)
            return stored;

        List<string> outp = [];
        foreach (string name in legacyNames ?? [])
            outp.Add(Find(name)?.Id ?? name);

        return outp;
    }

    /// <summary>What an import did, so the caller can say so.</summary>
    public sealed record ImportOutcome(int Added, int Updated, int Skipped);

    /// <summary>
    /// How an imported set that shares a name with an existing one is handled.
    /// </summary>
    public enum ImportClash
    {
        /// <summary>Leave the existing set exactly as it is.</summary>
        Skip,

        /// <summary>
        /// Replace the existing set's RULES, keeping its id.
        ///
        /// Keeping the id is the whole point: every saved test and stored
        /// selection already points at it, so they pick up the new rules instead
        /// of being orphaned. This is what makes a set genuinely updatable from
        /// a colleague's export.
        /// </summary>
        Update,

        /// <summary>Bring it in alongside as "Name (2)", touching nothing.</summary>
        KeepBoth,
    }

    /// <summary>
    /// Merges imported sets into this library, minting a LOCAL id for each one
    /// that arrives.
    ///
    /// The incoming sets carry no ids by design — see <see cref="ClashSetTransfer"/>.
    /// Matching is therefore by NAME, which is the only thing two libraries can
    /// agree on, and is also how a person thinks about "the 52 Series set".
    ///
    /// DOES NOT SAVE. Mutating and persisting are the caller's to sequence, the
    /// same way <c>NewSet</c> and <c>EditSet</c> already do it — and a merge that
    /// wrote to the real settings file the moment it was called could not be
    /// tested without writing to the user's own library, which is exactly what
    /// happened the first time this saved for itself.
    /// </summary>
    public ImportOutcome Import(IEnumerable<ClashSetTransfer> incoming, ImportClash onClash)
    {
        int added = 0, updated = 0, skipped = 0;

        foreach (ClashSetTransfer s in incoming)
        {
            ClashSet? existing = Find(s.Name);

            if (existing is not null)
            {
                if (onClash == ImportClash.Skip)
                {
                    skipped++;
                    continue;
                }

                if (onClash == ImportClash.Update)
                {
                    // Edited IN PLACE, id untouched. A file that could not say
                    // which worksets the set leaves out (null) leaves the local
                    // list alone - an older export must not wipe it.
                    List<string> localWorksets = existing.ExcludedWorksets;
                    existing.CopyRulesFrom(s.ToSet());
                    if (s.ExcludedWorksets is null)
                        existing.ExcludedWorksets = localWorksets;

                    updated++;
                    continue;
                }
            }

            string name = s.Name;
            if (existing is not null)
            {
                int n = 2;
                while (NameTaken(name))
                    name = $"{s.Name} ({n++})";
            }

            // A FRESH LOCAL id. The bundle deliberately carries none, and reusing
            // a sender's id would point this library's references at something
            // another library also claims.
            ClashSet arrived = new() { Id = Guid.NewGuid().ToString("N"), Name = name };
            arrived.CopyRulesFrom(s.ToSet());
            Sets.Add(arrived);

            added++;
        }

        return new ImportOutcome(added, updated, skipped);
    }

    /// <summary>
    /// Names for a stored reference list, for anything a person reads.
    ///
    /// The mirror of <see cref="ResolveIds"/>: ids are what gets STORED, names
    /// are what gets SHOWN, and a report that prints the stored form shows a
    /// column of hex. Anything that no longer resolves is passed through
    /// unchanged for the same reason as there — a set that was deleted should
    /// read as a reference that is missing, not vanish from the record of what
    /// the search actually ran on.
    /// </summary>
    public List<string> NamesFor(IEnumerable<string>? ids)
        => [.. (ids ?? []).Select(id => FindById(id)?.Name ?? id)];

    /// <summary>Case-insensitive, since these are names a human types.</summary>
    public ClashSet? Find(string name)
        => Sets.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));

    public bool NameTaken(string name, ClashSet? except = null)
        => Sets.Any(s => !ReferenceEquals(s, except)
                         && string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// "3 values · 2 categories" — enough to recognise a set without opening it,
    /// and it never grows the row.
    ///
    /// A single-kind set names what is in it ("Pipe Accessories, Sprinklers"),
    /// because that is the common case and the names are more use than a count.
    /// A mixed set falls back to counts, which stay short.
    /// </summary>
    public static string Describe(ClashSet set)
    {
        string rules = DescribeRules(set);
        int left = set.ExcludedWorksets?.Count ?? 0;

        // Said on the row, so a set that quietly drops content is never mistaken
        // for one that does not without opening it. "in Federated" because this
        // description is shown by all four clash tools and only Federated Clash
        // applies the list - in Clash Detect it would otherwise promise an
        // exclusion that tool never makes.
        return left == 0
            ? rules
            : $"{rules} · {left} workset{(left == 1 ? "" : "s")} left out in Federated";
    }

    private static string DescribeRules(ClashSet set)
    {
        List<BuiltInCategory> bics = set.Resolved();
        int kinds = (set.Values.Count > 0 ? 1 : 0)
                    + (set.Keywords.Count > 0 ? 1 : 0)
                    + (bics.Count > 0 ? 1 : 0);

        if (kinds == 0)
            return "empty";

        if (kinds == 1)
        {
            if (set.Values.Count > 0)
                return Head([.. set.Values.Select(v => v.Value)], "value");
            if (set.Keywords.Count > 0)
                return Head([.. set.Keywords.Select(k => $"\"{k}\"")], "keyword");

            return Head([.. bics.Select(DisplayName)], "category");
        }

        List<string> parts = [];
        if (set.Values.Count > 0)
            parts.Add($"{set.Values.Count} value{(set.Values.Count == 1 ? "" : "s")}");
        if (set.Keywords.Count > 0)
            parts.Add($"{set.Keywords.Count} keyword{(set.Keywords.Count == 1 ? "" : "s")}");
        if (bics.Count > 0)
            parts.Add($"{bics.Count} categor{(bics.Count == 1 ? "y" : "ies")}");

        return string.Join(" · ", parts);
    }

    /// <summary>"A, B +3" — the first two names, then how many more.</summary>
    private static string Head(List<string> names, string noun)
    {
        if (names.Count == 0)
            return $"no {noun}s";

        string head = string.Join(", ", names.Take(2));
        return names.Count <= 2 ? head : $"{head} +{names.Count - 2}";
    }

    /// <summary>
    /// The friendly name from the clash category table, falling back to the enum
    /// name with its OST_ prefix stripped for anything outside it.
    /// </summary>
    public static string DisplayName(BuiltInCategory bic)
    {
        foreach ((string name, BuiltInCategory b) in ClashCategories.NameToBic)
        {
            if (b == bic)
                return name;
        }

        string raw = bic.ToString();
        return raw.StartsWith("OST_", StringComparison.Ordinal) ? raw[4..] : raw;
    }
}
