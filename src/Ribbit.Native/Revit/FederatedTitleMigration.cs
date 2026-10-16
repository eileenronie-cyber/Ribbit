using System.IO;
using Ribbit.Geometry.Clash;

namespace Ribbit.Native.Revit;

/// <summary>
/// Collapses one model's open-method variants onto its normalised title.
///
/// <see cref="ModelTitle"/> explains why the variants exist. This is what makes
/// normalising SAFE: without it, the day the prefix changes, every report, every
/// status and every note already written becomes unreachable — the tool would
/// look like it had forgotten everything.
///
/// FEDERATED ONLY. The three older clash tools keep their raw titles and are not
/// touched by any of this; their files carry a different product tag and are
/// never enumerated here.
///
/// NOTHING IS DELETED. Reports are renamed onto the normalised prefix; ledgers,
/// baselines and notes are merged and their originals renamed ".migrated". A
/// wrong merge is then recoverable by hand, which matters because the ledger is
/// months of somebody's triage.
///
/// Runs at most once per folder: after it, no variant files remain to find.
/// </summary>
internal static class FederatedTitleMigration
{
    /// <summary>
    /// Earlier names of this model with files still under them, or empty.
    ///
    /// Separate from <see cref="Run"/> so a caller can ASK without reorganising:
    /// the status editor needs to know whether "no reports" means none exist or
    /// none have been merged yet, and those want opposite messages.
    /// </summary>
    public static List<string> FindVariants(
        string? csvFolder, string normalizedTitle, string? username, string productTag)
        => FindVariants(csvFolder, normalizedTitle, [username], productTag);

    /// <inheritdoc cref="FindVariants(string?, string, string?, string)"/>
    public static List<string> FindVariants(
        string? csvFolder, string normalizedTitle, IEnumerable<string?>? usernames, string productTag)
    {
        List<string> variants = [];

        if (string.IsNullOrEmpty(csvFolder) || !Directory.Exists(csvFolder))
            return variants;

        List<string> files;

        try
        {
            files = [.. Directory.GetFiles(csvFolder!)];
        }
        catch (IOException)
        {
            return variants;
        }

        foreach (string path in files)
        {
            string name = Path.GetFileName(path);

            // RETIRED FILES ARE NOT VARIANTS. They still carry the old prefix, so
            // without this the migration finds its own output and runs forever -
            // and each pass recreated empty ledgers and overwrote the originals
            // it had just preserved. Caught by running it twice.
            if (name.EndsWith(RetiredSuffix, StringComparison.OrdinalIgnoreCase))
                continue;

            string? title = TitleOf(name, productTag);
            if (title is null)
                continue;

            // Compared as PREFIXES, not raw titles: what is in the filename has
            // already been through the prefix sanitiser, so comparing it against
            // an unsanitised title would never match.
            if (!ModelTitle.IsVariantOf(
                    title, ClashStatus.SanitizeModelTitle(normalizedTitle), usernames))
                continue;

            if (!variants.Contains(title, StringComparer.Ordinal))
                variants.Add(title);
        }

        return variants;
    }

    /// <summary>
    /// Merges any variants onto <paramref name="normalizedTitle"/>.
    ///
    /// Returns a one-line summary of what moved, or null when there was nothing
    /// to do — which is the normal case on every run after the first.
    /// </summary>
    public static string? Run(
        string? csvFolder, string normalizedTitle, string? username, string productTag)
        => Run(csvFolder, normalizedTitle, [username], productTag);

    /// <inheritdoc cref="Run(string?, string, string?, string)"/>
    public static string? Run(
        string? csvFolder, string normalizedTitle, IEnumerable<string?>? usernames, string productTag)
    {
        List<string> variants = FindVariants(csvFolder, normalizedTitle, usernames, productTag);

        if (variants.Count == 0)
            return null;

        int ledgers = MergeLedgers(csvFolder!, normalizedTitle, variants, productTag);
        int baselines = MergeBaselines(csvFolder!, normalizedTitle, variants, productTag);
        int notes = MergeNotes(csvFolder!, normalizedTitle, variants, productTag);

        // Reports move LAST. Loading a variant's ledger falls back to reading its
        // reports when it has no ledger file, so the reports have to still be
        // under the variant's name while the ledgers are being merged.
        int reports = MoveReports(csvFolder!, normalizedTitle, variants, productTag);

        return $"Merged {variants.Count} earlier name(s) of this model: "
               + $"{reports} report(s), {ledgers} status ledger(s), "
               + $"{baselines} baseline(s), {notes} note file(s).";
    }

    /// <summary>
    /// Merges every variant ledger into the normalised one so that the most
    /// recent decision about a clash is the one that survives - WHICHEVER name
    /// it was made under.
    ///
    /// PER FILE, NOT PER TITLE. A title's ledger.csv plus its deltas were once
    /// judged "newer than the target" as a BLOCK, by the latest of the whole
    /// group's mtimes - so one fresh, unrelated write to a variant dragged
    /// every OTHER, older entry in that same variant along with it as if it
    /// were equally new, and could overwrite a decision the normalised name
    /// made in between. Every ledger.csv and every .delta - across every
    /// title, the normalised one included - is now one entry in ONE shared
    /// timeline, replayed oldest first exactly as <see cref="StatusLedger.LoadLedger"/>
    /// replays one title's own deltas, so a key's fate is decided by whichever
    /// SINGLE write actually set it last.
    ///
    /// THE NORMALISED NAME'S OWN FILES ARE IN THAT TIMELINE TOO, which is what
    /// makes this correct rather than merely finer-grained: without them, a
    /// variant entry with no rival ever "wins" outright instead of being
    /// compared against what the normalised name itself decided and when.
    ///
    /// LANDS AS A DELTA, NOT A REWRITTEN LEDGER. Status saves are deltas, and a
    /// delta replays on top of the base file on every load - so a merge written
    /// into the base file was undone by the normalised name's own older deltas
    /// the next time anyone loaded it. It also made this the one path that did
    /// the read-modify-write <see cref="StatusLedger.AppendDelta"/> exists to
    /// prevent. A delta written now sorts after every existing one.
    ///
    /// The variant's deltas are retired along with its ledger. Left behind, they
    /// keep the old name alive as a "variant", and every later run announced a
    /// merge that moved nothing.
    /// </summary>
    private static int MergeLedgers(
        string folder, string normalized, List<string> variants, string productTag)
    {
        // Only a variant that actually HAS a ledger or deltas. LoadLedger writes
        // one when it finds none - handy on a first run, but on a folder with
        // nothing left to migrate it would manufacture empty files under old
        // names and then retire them, growing the folder on every pass.
        List<string> relevantVariants =
        [
            .. variants.Where(v => File.Exists(LedgerPath(folder, v, productTag))
                                    || DeltasFor(folder, v, productTag).Count > 0)
        ];

        if (relevantVariants.Count == 0)
            return 0;

        // One row per FILE, not per title - see the class remarks. The target's
        // own base ledger and deltas are read here too, purely to place them on
        // the same timeline; they are never retired below.
        List<(string Path, DateTime When)> files =
        [
            .. relevantVariants
                .SelectMany(v => DeltasFor(folder, v, productTag).Append(LedgerPath(folder, v, productTag)))
                .Where(File.Exists)
                .Select(p => (p, Mtime(p))),
            .. DeltasFor(folder, normalized, productTag)
                .Append(LedgerPath(folder, normalized, productTag))
                .Where(File.Exists)
                .Select(p => (p, Mtime(p))),
        ];

        files.Sort((a, b) => a.When.CompareTo(b.When));

        Dictionary<string, string> pairs = new(StringComparer.Ordinal);
        Dictionary<StatusKey, string> elements = [];

        foreach ((string path, _) in files)
        {
            ClashLedger one = StatusLedger.ReadOne(path);

            foreach (KeyValuePair<string, string> kv in one.ByPair)
                pairs[kv.Key] = kv.Value;

            foreach (KeyValuePair<StatusKey, string> kv in one.ByElement)
                elements[kv.Key] = kv.Value;
        }

        // Read AFTER the timeline above, purely to know what the normalised
        // name already says - so only entries that CHANGE the answer are
        // written, never a restatement of what is already there.
        ClashLedger target = StatusLedger.LoadLedger(folder, normalized, productTag);

        List<(string ClashKey, string ElementId, string Link, string Status)> updates =
        [
            .. pairs
                .Where(kv => !target.ByPair.TryGetValue(kv.Key, out string? now) || now != kv.Value)
                .Select(kv => (kv.Key, "", "", kv.Value)),
            .. elements
                .Where(kv => !target.ByElement.TryGetValue(kv.Key, out string? now) || now != kv.Value)
                .Select(kv => ("", kv.Key.ElementId, kv.Key.LinkName, kv.Value)),
        ];

        // Written BEFORE anything is retired. If this throws, every variant is
        // still in place and the next run tries again.
        if (updates.Count > 0)
            StatusLedger.AppendDelta(folder, normalized, productTag, updates);

        int merged = 0;

        foreach (string title in relevantVariants)
        {
            bool retired = Retire(LedgerPath(folder, title, productTag));

            foreach (string delta in DeltasFor(folder, title, productTag))
                retired |= Retire(delta);

            if (retired)
                merged++;
        }

        return merged;
    }

    /// <summary>
    /// Merges every variant's New-badge baselines, PER TEST, keeping whichever
    /// name ran that test most recently.
    ///
    /// The normalised name competes like any other. It used to be overwritten
    /// by every variant, so a test re-run yesterday under the normalised name
    /// got an older variant's baseline back and badged the wrong clashes as new.
    /// </summary>
    private static int MergeBaselines(
        string folder, string normalized, List<string> variants, string productTag)
    {
        SeenLedger target = SeenLedger.Load(folder, normalized, productTag);

        Dictionary<string, DateTime> newest = target.ByTest.Keys.ToDictionary(
            id => id,
            id => SeenLedger.WrittenUtc(folder, normalized, productTag, id),
            StringComparer.Ordinal);

        List<string> consumed = [];

        foreach (string title in variants)
        {
            SeenLedger source = SeenLedger.Load(folder, title, productTag);

            foreach (KeyValuePair<string, List<string>> kv in source.ByTest)
            {
                DateTime when = SeenLedger.WrittenUtc(folder, title, productTag, kv.Key);

                if (newest.TryGetValue(kv.Key, out DateTime current) && when <= current)
                    continue;

                target.Record(kv.Key, kv.Value);
                newest[kv.Key] = when;
            }

            // EVERY file the variant owned, not just the old shared one, or a
            // per-test baseline would be left behind under a name nothing reads.
            consumed.AddRange(SeenLedger.FilesFor(folder, title, productTag));
        }

        target.Save(folder, normalized, productTag);

        // Retired only once the merged baselines are written.
        int merged = 0;
        foreach (string path in consumed)
        {
            if (Retire(path))
                merged++;
        }

        return merged;
    }

    /// <summary>A name's status deltas - where every saved status actually lands.</summary>
    private static List<string> DeltasFor(string folder, string title, string productTag)
    {
        string prefix = ClashStatus.ModelCsvPrefix(title, productTag);

        try
        {
            return
            [
                .. Directory.GetFiles(folder, "*" + StatusLedger.DeltaExtension)
                    .Where(p => Path.GetFileName(p).StartsWith(prefix, StringComparison.Ordinal))
            ];
        }
        catch (IOException)
        {
            return [];
        }
    }


    private static int MergeNotes(
        string folder, string normalized, List<string> variants, string productTag)
    {
        ClashComments target = ClashComments.Load(folder, normalized, productTag);
        int merged = 0;
        bool carried = false;

        foreach (string title in variants)
        {
            ClashComments source = ClashComments.Load(folder, title, productTag);

            foreach (KeyValuePair<string, ClashComment> kv in source.ByClash)
            {
                // Notes carry their OWN timestamp, which beats the file's: two
                // variants can hold notes on one clash written days apart.
                if (target.ByClash.TryGetValue(kv.Key, out ClashComment? existing)
                    && string.CompareOrdinal(existing.WhenUtc, kv.Value.WhenUtc) >= 0)
                    continue;

                target.Put(kv.Key, kv.Value);
                carried = true;
            }

            if (Retire(CommentsPath(folder, title, productTag)))
                merged++;
        }

        // Nothing carried, nothing written: an empty notes file is clutter, and
        // a Save on an unreadable target would move it aside for no reason.
        if (carried)
            target.Save(folder, normalized, productTag);

        return merged;
    }

    /// <summary>
    /// Renames each variant's reports onto the normalised prefix, keeping the
    /// rest of the filename — so the run picker still shows the test and the time
    /// they were written under.
    /// </summary>
    private static int MoveReports(
        string folder, string normalized, List<string> variants, string productTag)
    {
        string targetPrefix = ClashStatus.ModelCsvPrefix(normalized, productTag);
        int moved = 0;

        foreach (string title in variants)
        {
            string sourcePrefix = ClashStatus.ModelCsvPrefix(title, productTag);

            List<string> paths;
            try
            {
                // REPORTS ONLY. Everything else under this prefix has either been
                // merged already or is a ".migrated" original, and dragging those
                // onto the normalised name produced files called
                // "ledger.csv_m2.migrated" - recoverable, but nobody could tell
                // what they were.
                paths =
                [
                    .. Directory.GetFiles(folder)
                        .Where(p =>
                        {
                            string n = Path.GetFileName(p);
                            return n.StartsWith(sourcePrefix, StringComparison.Ordinal)
                                   && n.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)
                                   && !n.EndsWith("ledger.csv", StringComparison.OrdinalIgnoreCase);
                        })
                ];
            }
            catch (IOException)
            {
                continue;
            }

            foreach (string path in paths)
            {
                string tail = Path.GetFileName(path)[sourcePrefix.Length..];
                string destination = Path.Combine(folder, targetPrefix + tail);

                try
                {
                    // A name already taken means both variants produced a report
                    // in the same minute. Numbering keeps both rather than
                    // choosing one.
                    int n = 2;
                    while (File.Exists(destination))
                    {
                        string stem = Path.GetFileNameWithoutExtension(tail);
                        destination = Path.Combine(
                            folder, $"{targetPrefix}{stem}_m{n}{Path.GetExtension(tail)}");
                        n++;
                    }

                    File.Move(path, destination);
                    moved++;
                }
                catch (Exception)
                {
                    // A file held open by Excel stays where it is. The rest move.
                }
            }
        }

        return moved;
    }

    /// <summary>Suffix marking a file this migration has already consumed.</summary>
    internal const string RetiredSuffix = ".migrated";

    /// <summary>
    /// The model-title prefix of a clash file's name, or null when the name is
    /// not one of this product's files. Shared with KnownUsers, which reads the
    /// same names and must split them exactly as this migration does.
    /// </summary>
    internal static string? TitleOf(string fileName, string productTag)
    {
        int idx = fileName.IndexOf($"_{productTag}_", StringComparison.Ordinal);
        return idx <= 0 ? null : fileName.Substring(0, idx);
    }

    /// <summary>
    /// Renames a consumed file aside. NEVER deletes, and never overwrites an
    /// earlier one.
    ///
    /// It used to delete a colliding aside before moving. That is fine right up
    /// until something makes the migration run twice, at which point the delete
    /// lands on the ONLY copy of the original.
    /// </summary>
    private static bool Retire(string path)
    {
        try
        {
            if (!File.Exists(path))
                return false;

            string aside = path + RetiredSuffix;

            int n = 2;
            while (File.Exists(aside))
                aside = $"{path}.{n++}{RetiredSuffix}";

            File.Move(path, aside);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string LedgerPath(string folder, string title, string productTag)
        => Path.Combine(folder, ClashStatus.ModelCsvPrefix(title, productTag) + "ledger.csv");

    // No SeenPath: a baseline is one file per TEST now, so SeenLedger.FilesFor is
    // the only thing that knows which files a variant owns.

    private static string CommentsPath(string folder, string title, string productTag)
        => Path.Combine(folder, ClashStatus.ModelCsvPrefix(title, productTag) + "comments.json");

    private static DateTime Mtime(string path)
    {
        try
        {
            return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
        }
        catch (IOException)
        {
            return DateTime.MinValue;
        }
    }
}
