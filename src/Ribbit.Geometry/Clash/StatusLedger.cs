using System.IO;
using System.Text;

namespace Ribbit.Geometry.Clash;

/// <summary>
/// Reads and writes the CSV status ledger. Ported from the CSV half of
/// lib/clashlib/report.py.
///
/// Status lives in the CSV rather than in the model: no transaction, and it
/// works identically for host and linked elements.
/// </summary>
public static class StatusLedger
{
    public const string ClashKeyField = "Clash Key";

    /// <summary>The single authoritative ledger file for a model and product.</summary>
    public static string LedgerPath(string csvFolder, string? modelTitle, string productTag)
        => Path.Combine(csvFolder, ClashStatus.ModelCsvPrefix(modelTitle, productTag) + "ledger.csv");

    /// <summary>
    /// The statuses for this model and product.
    ///
    /// READS ONE FILE, not the whole folder. The previous behaviour globbed every
    /// report CSV matching the model prefix and fully parsed all of them before a
    /// run could show anything — and every run writes another one. On a job with
    /// months of history that is hundreds of full parses at the start of every
    /// single run, growing without bound and entirely silently.
    ///
    /// The first call after this change finds no ledger file and MIGRATES: it
    /// does the old folder-wide merge once, writes the result, and every run
    /// afterwards reads just the one file. Nothing is deleted — the report CSVs
    /// remain as the human deliverable, they are simply off the hot path.
    /// </summary>
    /// <summary>
    /// Extension for a status DELTA - one save's changes, nothing else.
    ///
    /// Deliberately NOT ".csv". Several things in this folder glob for csv files
    /// and decide what they are by name; a delta that any of them mistook for a
    /// report would be a nasty and very quiet bug. The contents are still csv, so
    /// the file opens fine in anything that is pointed at it.
    /// </summary>
    public const string DeltaExtension = ".delta";

    /// <summary>
    /// Deltas tolerated before the ledger is folded back into one file.
    ///
    /// High enough that ordinary triage never triggers it, low enough that the
    /// folder does not accumulate thousands of tiny files.
    /// </summary>
    private const int CompactThreshold = 25;

    /// <summary>
    /// Breaks timestamp ties between saves made in one session. Zero-padded, so
    /// it still sorts as text alongside the stamp it follows.
    /// </summary>
    private static int _sequence;

    public static ClashLedger LoadLedger(
        string? csvFolder, string? modelTitle, string productTag)
    {
        ClashLedger ledger = new();

        if (string.IsNullOrEmpty(csvFolder) || !Directory.Exists(csvFolder))
            return ledger;

        string path = LedgerPath(csvFolder!, modelTitle, productTag);

        if (File.Exists(path))
        {
            ReadInto(ledger, path);
        }
        else
        {
            MigrateFromReports(ledger, csvFolder!, modelTitle, productTag);

            try
            {
                WriteLedger(csvFolder!, modelTitle, productTag, ledger);
            }
            catch (IOException)
            {
                // A read-only or contended folder just means the migration runs
                // again next time - correct either way, only slower.
            }
        }

        // Then every delta, oldest first, so the most recent decision about a
        // clash is the one left standing.
        List<string> deltas = DeltaFiles(csvFolder!, modelTitle, productTag);
        foreach (string delta in deltas)
            ReadInto(ledger, delta);

        if (deltas.Count >= CompactThreshold)
            Compact(csvFolder!, modelTitle, productTag, ledger, deltas);

        return ledger;
    }

    /// <summary>
    /// This save's changes, as their own file. THE SAVE PATH - callers must not
    /// write the whole ledger back.
    ///
    /// Rewriting the ledger from an in-memory copy is a read-modify-write over a
    /// file other people are also writing. Two people triaging the same model
    /// both load it, both save, and the second write silently drops everything
    /// the first one did - no error, no conflict, and no way to notice except
    /// that a morning's work is gone.
    ///
    /// A delta is APPEND-ONLY and uniquely named, so concurrent saves cannot
    /// collide and none of them can erase another. The ledger is whatever the
    /// base file plus every delta says, which no single writer has to know.
    ///
    /// Named with a UTC timestamp because ordering has to hold across MACHINES
    /// once a folder is shared, and local time does not.
    ///
    /// The timestamp alone is NOT enough to order saves, which is worth spelling
    /// out because it is easy to assume otherwise: Windows advances UtcNow only
    /// every ~15ms, so two saves a moment apart are frequently stamped
    /// identically. A sequence number breaks those ties, so saves made in one
    /// session always replay in the order they were made. Across two machines in
    /// the same 15ms window the order is still arbitrary - by then they are
    /// genuinely simultaneous and there is no honest answer.
    /// </summary>
    public static string AppendDelta(
        string csvFolder, string? modelTitle, string productTag,
        IEnumerable<(string ClashKey, string ElementId, string Link, string Status)> updates)
    {
        string name = ClashStatus.ModelCsvPrefix(modelTitle, productTag)
                      + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff")
                      + "-" + System.Threading.Interlocked.Increment(ref _sequence)
                          .ToString("D6")
                      + "-" + Guid.NewGuid().ToString("N").Substring(0, 8)
                      + DeltaExtension;

        string path = Path.Combine(csvFolder, name);
        List<string> fields = [ClashKeyField, "Element Id", "Link", "Status"];

        Write(path, fields, updates.Select(u => new Dictionary<string, string>
        {
            [ClashKeyField] = u.ClashKey,
            ["Element Id"] = u.ElementId,
            ["Link"] = u.Link,
            ["Status"] = u.Status,
        }));

        return path;
    }

    /// <summary>Deltas for this model and product, oldest first by their name.</summary>
    private static List<string> DeltaFiles(
        string csvFolder, string? modelTitle, string productTag)
    {
        string prefix = ClashStatus.ModelCsvPrefix(modelTitle, productTag);

        try
        {
            List<string> found =
            [
                .. Directory.GetFiles(csvFolder, "*" + DeltaExtension)
                    .Where(p => Path.GetFileName(p)
                        .StartsWith(prefix, StringComparison.Ordinal))
            ];

            // By NAME, which carries the UTC stamp. Modification time would be
            // the file's own history rather than the save's, and a copied folder
            // loses it entirely.
            found.Sort(StringComparer.Ordinal);
            return found;
        }
        catch (IOException)
        {
            return [];
        }
    }

    /// <summary>
    /// Folds the deltas back into the base ledger and removes the ones folded in.
    ///
    /// ONLY the files that were actually read are deleted, by name. A delta
    /// written by somebody else while this was running is not one of them, so it
    /// survives to be merged next time. That is what makes compaction safe to do
    /// without a lock.
    ///
    /// Any failure leaves everything where it was: the deltas are still there and
    /// still merged on load, so compaction is an optimisation that can be skipped,
    /// never a step correctness depends on.
    /// </summary>
    private static void Compact(
        string csvFolder, string? modelTitle, string productTag,
        ClashLedger merged, List<string> consumed)
    {
        try
        {
            WriteLedger(csvFolder, modelTitle, productTag, merged);
        }
        catch (IOException)
        {
            return;
        }

        foreach (string path in consumed)
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
                // Left behind, merged again next load. Harmless.
            }
        }
    }

    /// <summary>
    /// Reads exactly ONE ledger or delta file - never the merge <see cref="LoadLedger"/>
    /// does across every delta for a title.
    ///
    /// For a caller that has to attribute an entry to the SPECIFIC write that set
    /// it, such as a cross-title merge where a title's aggregate "last touched"
    /// time is too coarse: one recent write to a title must not drag every OTHER,
    /// older entry in that same title along with it as if it were equally recent.
    /// Reading file by file lets each entry carry its own file's timestamp.
    /// </summary>
    public static ClashLedger ReadOne(string path)
    {
        ClashLedger ledger = new();

        if (File.Exists(path))
            ReadInto(ledger, path);

        return ledger;
    }

    private static void ReadInto(ClashLedger ledger, string path)
    {
        foreach (Dictionary<string, string> row in ReadRows(path))
        {
            string status = Value(row, "Status");
            if (status.Length == 0)
                continue;

            string clashKey = Value(row, ClashKeyField);
            if (clashKey.Length > 0)
            {
                ledger.SetPair(clashKey, status);
                continue;
            }

            string eid = Value(row, "Element Id");
            if (eid.Length > 0)
                ledger.SetElement(new StatusKey(eid, Value(row, "Link")), status);
        }
    }

    /// <summary>Writes the ledger, pair entries first.</summary>
    public static string WriteLedger(
        string csvFolder, string? modelTitle, string productTag, ClashLedger ledger)
    {
        string path = LedgerPath(csvFolder, modelTitle, productTag);
        List<string> fields = [ClashKeyField, "Element Id", "Link", "Status"];
        List<Dictionary<string, string>> rows = [];

        foreach (KeyValuePair<string, string> kv in ledger.ByPair)
        {
            rows.Add(new Dictionary<string, string>
            {
                [ClashKeyField] = kv.Key,
                ["Element Id"] = "",
                ["Link"] = "",
                ["Status"] = kv.Value,
            });
        }

        foreach (KeyValuePair<StatusKey, string> kv in ledger.ByElement)
        {
            rows.Add(new Dictionary<string, string>
            {
                [ClashKeyField] = "",
                ["Element Id"] = kv.Key.ElementId,
                ["Link"] = kv.Key.LinkName,
                ["Status"] = kv.Value,
            });
        }

        // Written aside and swapped in, never edited in place. Two people can now
        // compact at the same moment, and a half-written ledger read by a third
        // would look like a folder that had lost most of its triage.
        string temp = path + ".tmp";
        Write(temp, fields, rows);

        try
        {
            if (File.Exists(path))
                File.Replace(temp, path, null);
            else
                File.Move(temp, path);
        }
        catch (IOException)
        {
            // Replace can fail if someone else swapped a new file in first.
            // Theirs is as good as ours - both hold every delta merged so far -
            // so the temp is dropped rather than forced over the top.
            try
            {
                File.Delete(temp);
            }
            catch (IOException)
            {
            }
        }

        return path;
    }

    /// <summary>
    /// The one-time rebuild: every report CSV for this model, oldest first, so a
    /// later edit to the same clash still wins. This is the old folder-wide
    /// merge, now run once instead of on every run.
    ///
    /// Filtered to the model's own title: two Revit projects can share a CSV
    /// folder, and element ids are only unique within one document, so merging
    /// across projects would silently mix unrelated elements.
    ///
    /// Ordered by filesystem MODIFICATION TIME, not by filename. The filename
    /// encodes a human-readable search description, not a precise timestamp, so
    /// two searches run on the same day cannot be ordered by name — mtime is the
    /// only thing reflecting when a file was actually written.
    /// </summary>
    private static void MigrateFromReports(
        ClashLedger ledger, string csvFolder, string? modelTitle, string productTag)
    {
        string prefix = ClashStatus.ModelCsvPrefix(modelTitle, productTag);

        List<string> candidates;
        try
        {
            candidates =
            [
                .. Directory.GetFiles(csvFolder!)
                    .Where(p =>
                    {
                        string name = Path.GetFileName(p);
                        return name.StartsWith(prefix, StringComparison.Ordinal)
                               && name.EndsWith(".csv", StringComparison.OrdinalIgnoreCase);
                    })
            ];
        }
        catch (IOException)
        {
            return;
        }

        candidates.Sort((a, b) => Mtime(a).CompareTo(Mtime(b)));

        foreach (string path in candidates)
            ReadInto(ledger, path);
    }

    /// <summary>
    /// The most recently modified FULL-REPORT CSV for this model, or null.
    ///
    /// DELIBERATELY excludes "_statusupdate" files. Those are the small
    /// three-column supplementary files, and being the most recently modified
    /// thing in the folder — which by design is exactly how a status change gets
    /// picked up — does NOT make one of them "the last full set of results".
    /// <see cref="Load"/> still reads them for the merged ledger; only this
    /// function, which chooses where the descriptive columns come from, skips them.
    /// </summary>
    public static string? FindLatestFullReport(
        string? csvFolder, string? modelTitle, string productTag)
    {
        List<string> all = ListFullReports(csvFolder, modelTitle, productTag);
        return all.Count == 0 ? null : all[0];
    }

    /// <summary>
    /// Every full report for this model and product, NEWEST FIRST.
    ///
    /// Split out of <see cref="FindLatestFullReport"/> so a caller can CHOOSE a
    /// report instead of being handed the newest. Once a run's filename carries
    /// the saved test that produced it, "the newest report" and "the report for
    /// the test I want to triage" stop being the same file.
    ///
    /// Same exclusions as before, in one place: a rule that skipped a status
    /// update here but not there would show a three-column file as a report.
    /// </summary>
    public static List<string> ListFullReports(
        string? csvFolder, string? modelTitle, string productTag)
    {
        if (string.IsNullOrEmpty(csvFolder) || !Directory.Exists(csvFolder))
            return [];

        string prefix = ClashStatus.ModelCsvPrefix(modelTitle, productTag);

        List<string> candidates;
        try
        {
            candidates =
            [
                .. Directory.GetFiles(csvFolder!)
                    .Where(p =>
                    {
                        string name = Path.GetFileName(p);
                        return name.StartsWith(prefix, StringComparison.Ordinal)
                               && name.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)
                               && !name.Contains("_statusupdate")
                               // The ledger is a status store, not a report - it
                               // has no descriptive columns to offer, and being
                               // the newest file is exactly what it always is.
                               && !name.EndsWith("ledger.csv", StringComparison.OrdinalIgnoreCase);
                    })
            ];
        }
        catch (IOException)
        {
            return [];
        }

        candidates.Sort((a, b) => Mtime(b).CompareTo(Mtime(a)));
        return candidates;
    }

    /// <summary>Names already in the folder, for the filename counter.</summary>
    private static HashSet<string> ExistingNames(string csvFolder)
    {
        try
        {
            return [.. Directory.GetFiles(csvFolder).Select(Path.GetFileName)!];
        }
        catch (IOException)
        {
            return [];
        }
    }

    /// <summary>
    /// A small supplementary CSV of just Element Id / Link / Status.
    ///
    /// Tagged "statusupdate" so it is visually distinguishable from a full
    /// export, but picked up by the same merge-by-mtime logic either way —
    /// <see cref="Load"/> only ever reads those three columns regardless of what
    /// else a file contains, so no extra reading code exists for this.
    /// </summary>
    public static string WriteStatusUpdates(
        string csvFolder, string? modelTitle, string productTag,
        IEnumerable<(string ElementId, string Link, string Status)> updates,
        string? testName = null)
    {
        string filename = ClashStatus.BuildCsvFilename(
            modelTitle, "statusupdate", productTag, ExistingNames(csvFolder), null, testName);

        string path = Path.Combine(csvFolder, filename);
        List<string> fields = ["Element Id", "Link", "Status"];

        Write(path, fields, updates.Select(u => new Dictionary<string, string>
        {
            ["Element Id"] = u.ElementId,
            ["Link"] = u.Link,
            ["Status"] = u.Status,
        }));

        return path;
    }

    /// <summary>
    /// A FULL copy of the report — same columns, same rows, same order — with
    /// the Status column already updated by the caller.
    ///
    /// NOT tagged "_statusupdate", so <see cref="FindLatestFullReport"/> picks it
    /// up as the new latest full report, which it should be: it is the most
    /// current complete picture. <see cref="Load"/> merges it either way, since
    /// it only reads the three columns by name.
    ///
    /// This exists alongside <see cref="WriteStatusUpdates"/> because opening a
    /// three-column file side by side with the full report — different row
    /// count, nothing to cross-reference — was confusing in practice.
    /// </summary>
    public static string WriteFullStatus(
        string csvFolder, string? modelTitle, string productTag,
        IReadOnlyList<string> fieldNames,
        IEnumerable<IReadOnlyDictionary<string, string>> rows,
        string? testName = null)
    {
        string filename = ClashStatus.BuildCsvFilename(
            modelTitle, "saved", productTag, ExistingNames(csvFolder), null, testName);

        string path = Path.Combine(csvFolder, filename);
        Write(path, fieldNames, rows);
        return path;
    }

    private static DateTime Mtime(string path)
    {
        try
        {
            return File.GetLastWriteTimeUtc(path);
        }
        catch (IOException)
        {
            return DateTime.MinValue;
        }
    }

    private static string Value(IReadOnlyDictionary<string, string> row, string key)
        => row.TryGetValue(key, out string? v) ? v.Trim() : "";

    // ------------------------------------------------------------------ csv

    /// <summary>
    /// Rows keyed by header. A file that cannot be read yields nothing rather
    /// than aborting the merge — one unreadable CSV must not lose the ledger.
    /// </summary>
    public static List<Dictionary<string, string>> ReadRows(string path)
    {
        List<Dictionary<string, string>> rows = [];

        try
        {
            using StreamReader reader = new(path, Encoding.UTF8);

            List<string>? headers = null;
            foreach (List<string> fields in ReadCsv(reader))
            {
                if (headers is null)
                {
                    headers = fields;
                    continue;
                }

                Dictionary<string, string> row = [];
                for (int i = 0; i < headers.Count; i++)
                    row[headers[i]] = i < fields.Count ? fields[i] : "";

                rows.Add(row);
            }
        }
        catch (IOException)
        {
            return rows;
        }

        return rows;
    }

    /// <summary>
    /// A minimal RFC4180 reader: quoted fields, doubled quotes, and embedded
    /// newlines. Written out rather than split(',') because a clash comment
    /// routinely contains a comma, and a naive split silently shifts every later
    /// column — including Status.
    /// </summary>
    private static IEnumerable<List<string>> ReadCsv(TextReader reader)
    {
        List<string> fields = [];
        StringBuilder field = new();
        bool inQuotes = false;
        bool anyContent = false;

        int c;
        while ((c = reader.Read()) >= 0)
        {
            char ch = (char)c;

            if (inQuotes)
            {
                if (ch == '"')
                {
                    if (reader.Peek() == '"')
                    {
                        reader.Read();
                        field.Append('"');
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    field.Append(ch);
                }
                anyContent = true;
                continue;
            }

            switch (ch)
            {
                case '"':
                    inQuotes = true;
                    anyContent = true;
                    break;

                case ',':
                    fields.Add(field.ToString());
                    field.Clear();
                    anyContent = true;
                    break;

                case '\r':
                    break;   // handled by the \n

                case '\n':
                    fields.Add(field.ToString());
                    field.Clear();
                    yield return fields;
                    fields = [];
                    anyContent = false;
                    break;

                default:
                    field.Append(ch);
                    anyContent = true;
                    break;
            }
        }

        if (anyContent || field.Length > 0 || fields.Count > 0)
        {
            fields.Add(field.ToString());
            yield return fields;
        }
    }

    /// <summary>Quotes a field only when it needs it, doubling any inner quotes.</summary>
    public static string EscapeCsv(string? value)
    {
        string s = value ?? "";
        bool needsQuotes = s.IndexOfAny([',', '"', '\n', '\r']) >= 0;
        if (!needsQuotes)
            return s;

        return "\"" + s.Replace("\"", "\"\"") + "\"";
    }

    /// <summary>
    /// Writes rows as CSV. UTF-8 WITH a BOM: Excel opens a BOM-less UTF-8 file
    /// as the system codepage, which mangles any non-ASCII in a comment or a
    /// level name.
    /// </summary>
    /// <summary>
    /// Writes via a temp file and swaps it in — see <see cref="AtomicFile"/> for
    /// why. Every file this class produces lands in a SHARED folder that other
    /// people read while it is being written.
    /// </summary>
    public static void Write(
        string path, IReadOnlyList<string> fieldNames,
        IEnumerable<IReadOnlyDictionary<string, string>> rows)
    {
        string temp = AtomicFile.TempPathFor(path);

        using (StreamWriter w = new(temp, append: false, new UTF8Encoding(true)))
        {
            w.WriteLine(string.Join(",", fieldNames.Select(EscapeCsv)));

            foreach (IReadOnlyDictionary<string, string> row in rows)
            {
                w.WriteLine(string.Join(",", fieldNames.Select(
                    f => EscapeCsv(row.TryGetValue(f, out string? v) ? v : ""))));
            }
        }

        AtomicFile.Commit(temp, path);
    }
}
