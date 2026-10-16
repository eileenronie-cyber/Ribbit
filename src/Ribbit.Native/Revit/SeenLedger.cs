using System.IO;
using System.Text.Json;
using Ribbit.Geometry.Clash;

namespace Ribbit.Native.Revit;

/// <summary>
/// What each saved test saw last time it ran — the baseline behind the "New"
/// badge.
///
/// SEPARATE FROM THE STATUS LEDGER, deliberately. The status ledger records
/// clashes you have FLAGGED; it has no idea a clash existed unless you gave it
/// a status. Deriving "new" from it would badge every unflagged clash as new on
/// every run, forever.
///
/// Keyed by TEST. The most recent report on disk may belong to a different
/// search, and comparing against that would call a clash new because last time
/// you happened to run something else.
///
/// ONE FILE PER TEST, not one file for all of them. A single file made every
/// run a read-modify-write over something other people were also writing: run
/// your test, and a colleague who had loaded the file before you saved wipes
/// your baseline when they save theirs. Splitting by test means two people
/// running two tests never touch the same file, and two people running the SAME
/// test overwrite one entry that genuinely has one answer — the last time it
/// ran.
///
/// Written beside the reports rather than with the tests: it is per model, it
/// can be thousands of keys, and it is disposable — deleting it loses only the
/// "new since last run" marker, never a status.
/// </summary>
internal sealed class SeenLedger
{
    /// <summary>Test id to the clash keys that test saw.</summary>
    public Dictionary<string, List<string>> ByTest { get; set; } = [];

    /// <summary>
    /// Tests changed since load. Only these are written, which is the whole
    /// point: a save must not restate what it never touched.
    /// </summary>
    private readonly HashSet<string> _dirty = [];

    private static string Prefix(string? modelTitle, string productTag)
        => ClashStatus.ModelCsvPrefix(modelTitle, productTag) + "seen-";

    private static string PathFor(
        string csvFolder, string? modelTitle, string productTag, string testId)
        => Path.Combine(csvFolder, Prefix(modelTitle, productTag) + Safe(testId) + ".json");

    /// <summary>The single file every test used to share. Read once, then retired.</summary>
    private static string LegacyPath(string csvFolder, string? modelTitle, string productTag)
        => Path.Combine(
            csvFolder, ClashStatus.ModelCsvPrefix(modelTitle, productTag) + "seen.json");

    /// <summary>Test ids are generated hex, but a hand-edited file is not.</summary>
    private static string Safe(string testId)
    {
        char[] chars = testId.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            if (!char.IsLetterOrDigit(chars[i]) && chars[i] != '-' && chars[i] != '_')
                chars[i] = '_';
        }

        return new string(chars);
    }

    /// <summary>Every file this ledger owns, for callers that move a folder around.</summary>
    public static List<string> FilesFor(string? csvFolder, string? modelTitle, string productTag)
    {
        List<string> found = [];

        if (string.IsNullOrEmpty(csvFolder) || !Directory.Exists(csvFolder))
            return found;

        string legacy = LegacyPath(csvFolder!, modelTitle, productTag);
        if (File.Exists(legacy))
            found.Add(legacy);

        try
        {
            string prefix = Prefix(modelTitle, productTag);
            found.AddRange(
                Directory.GetFiles(csvFolder!, "*.json")
                    .Where(p => Path.GetFileName(p).StartsWith(prefix, StringComparison.Ordinal)));
        }
        catch (IOException)
        {
        }

        return found;
    }

    public static SeenLedger Load(string? csvFolder, string? modelTitle, string productTag)
    {
        SeenLedger ledger = new();

        if (string.IsNullOrEmpty(csvFolder) || !Directory.Exists(csvFolder))
            return ledger;

        // The old single file first, so a per-test file written since wins.
        string legacy = LegacyPath(csvFolder!, modelTitle, productTag);
        if (File.Exists(legacy))
        {
            try
            {
                SeenLedger? old = JsonSerializer.Deserialize<SeenLedger>(File.ReadAllText(legacy));
                foreach (KeyValuePair<string, List<string>> kv in old?.ByTest ?? [])
                {
                    ledger.ByTest[kv.Key] = kv.Value;
                    ledger._dirty.Add(kv.Key);   // rewrite it as a per-test file
                }
            }
            catch (Exception)
            {
                // A corrupt baseline costs a badge, not data.
            }
        }

        string prefix = Prefix(modelTitle, productTag);

        try
        {
            foreach (string path in Directory.GetFiles(csvFolder!, "*.json"))
            {
                string name = Path.GetFileName(path);
                if (!name.StartsWith(prefix, StringComparison.Ordinal))
                    continue;

                string testId = name.Substring(prefix.Length);
                testId = testId.Substring(0, testId.Length - ".json".Length);

                try
                {
                    List<string>? keys =
                        JsonSerializer.Deserialize<List<string>>(File.ReadAllText(path));

                    if (keys is not null)
                    {
                        ledger.ByTest[testId] = keys;
                        ledger._dirty.Remove(testId);   // already on disk in the new shape
                    }
                }
                catch (Exception)
                {
                }
            }
        }
        catch (IOException)
        {
        }

        return ledger;
    }

    /// <summary>
    /// When this test's baseline was last written, or <see cref="DateTime.MinValue"/>.
    ///
    /// For a merge that has to know which of two names ran a test more recently.
    /// A baseline still in the old shared file answers with that file's time.
    /// </summary>
    public static DateTime WrittenUtc(
        string? csvFolder, string? modelTitle, string productTag, string testId)
    {
        if (string.IsNullOrEmpty(csvFolder))
            return DateTime.MinValue;

        foreach (string path in new[]
                 {
                     PathFor(csvFolder!, modelTitle, productTag, testId),
                     LegacyPath(csvFolder!, modelTitle, productTag),
                 })
        {
            try
            {
                if (File.Exists(path))
                    return File.GetLastWriteTimeUtc(path);
            }
            catch (IOException)
            {
            }
        }

        return DateTime.MinValue;
    }

    /// <summary>The keys a test saw last time, or null when it has no history.</summary>
    public List<string>? Previous(string testId)
        => testId.Length > 0 && ByTest.TryGetValue(testId, out List<string>? keys) ? keys : null;

    /// <summary>
    /// Records what this test saw, replacing its previous entry and leaving
    /// every other test's alone.
    ///
    /// Does nothing without a test id: an ad-hoc search has no identity to
    /// record against, and inventing one would give the next ad-hoc search a
    /// baseline it never earned.
    /// </summary>
    public void Record(string testId, IEnumerable<string> clashKeys)
    {
        if (testId.Length == 0)
            return;

        ByTest[testId] = [.. clashKeys.Where(k => k.Length > 0).Distinct(StringComparer.Ordinal)];
        _dirty.Add(testId);
    }

    /// <summary>
    /// Drops a test's baseline, for when the test itself is deleted.
    ///
    /// Without this the keys outlive the test that made them and the file only
    /// ever grows — and a test deleted then re-created under the same name gets
    /// a fresh id anyway, so the old entry could never be reached again.
    /// </summary>
    public bool Forget(string testId)
    {
        if (testId.Length == 0 || !ByTest.Remove(testId))
            return false;

        _dirty.Add(testId);   // save deletes the file
        return true;
    }

    /// <summary>
    /// Writes only what changed, then retires the old shared file.
    ///
    /// A test nobody touched is not rewritten, so a save can never undo somebody
    /// else's run.
    /// </summary>
    public void Save(string? csvFolder, string? modelTitle, string productTag)
    {
        if (string.IsNullOrEmpty(csvFolder) || !Directory.Exists(csvFolder))
            return;

        foreach (string testId in _dirty)
        {
            string path = PathFor(csvFolder!, modelTitle, productTag, testId);

            try
            {
                if (ByTest.TryGetValue(testId, out List<string>? keys))
                {
                    // Temp-then-swap: this lands in the SHARED csv folder, and
                    // File.WriteAllText truncates before it writes. A colleague
                    // loading in that window reads an empty baseline and badges
                    // every clash as new. See AtomicFile.
                    string temp = AtomicFile.TempPathFor(path);
                    File.WriteAllText(temp, JsonSerializer.Serialize(keys));
                    AtomicFile.Commit(temp, path);
                }
                else if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception)
            {
                // Losing a baseline costs the next run its badges. It must never
                // cost the run itself.
            }
        }

        _dirty.Clear();

        // Only once everything it held has been written out per test.
        string legacy = LegacyPath(csvFolder!, modelTitle, productTag);

        try
        {
            if (File.Exists(legacy))
                File.Move(legacy, legacy + ".migrated");
        }
        catch (IOException)
        {
        }
    }
}
