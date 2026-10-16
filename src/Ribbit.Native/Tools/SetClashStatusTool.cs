using System.IO;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Ribbit.Geometry.Clash;
using Ribbit.Native.Revit;
using Ribbit.Native.Settings;
using Ribbit.Native.Ui;

namespace Ribbit.Native.Tools;

/// <summary>
/// Set Clash Status. Ported from the three near-identical status editors —
/// SetClashStatus.py, SetClashStatusLink.py and SetCDMEPClashStatus.py.
///
/// ONE implementation, three <see cref="ClashProduct"/> descriptors. The Python
/// had three copies, and the largest of them had also drifted into carrying its
/// own private copies of the ledger functions rather than importing the shared
/// ones — so a fix to the merge logic would have had to be made twice.
///
/// Reads the most recent FULL report for this model (a file read, no clash
/// re-run), shows one row per clashing element with three buckets, and on Save
/// re-exports the full report with the new statuses applied — same columns, same
/// rows, only Status changes — so the saved file is always a complete current
/// snapshot rather than a diff you have to cross-reference.
///
/// READ-ONLY with respect to the model: this never opens a Transaction.
/// </summary>
internal static class SetClashStatusTool
{
    private const string DialogTitle = "Set Clash Status";

    public static void Run(UIDocument uidoc, ClashProduct product)
    {
        Document doc = uidoc.Document;

        ClashSettings settings = ClashSettings.Load(product.ConfigSection);
        string csvFolder = settings.CsvFolder;
        // Only Federated normalises, so only Federated pays for the user list -
        // it lists the whole clash folder.
        List<string> users = product == ClashProduct.Federated
            ? KnownUsers.For(doc.Application?.Username, csvFolder, product.ProductTag)
            : [];

        // Federated normalises the title so a model opened as a local, detached,
        // or straight from central resolves to ONE set of reports and one ledger.
        // The other three keep the raw title: their files have been written under
        // it for months and renaming them here would orphan the lot.
        //
        // No migration call - Federated Clash itself does that, and the editor
        // must not reorganise a folder just by being opened.
        string modelTitle = product == ClashProduct.Federated
            ? ModelTitle.Normalize(doc.Title, users)
            : doc.Title;

        if (modelTitle.Length == 0)
            modelTitle = doc.Title;

        List<string> reports = StatusLedger.ListFullReports(
            csvFolder, modelTitle, product.ProductTag);

        if (reports.Count == 0 && product == ClashProduct.Federated)
        {
            // "None found" would be a lie when the reports are sitting there
            // under an earlier name of this model. The editor deliberately does
            // not merge them itself - reorganising a folder is not something a
            // status editor should do just by being opened - so it says who will.
            List<string> variants = FederatedTitleMigration.FindVariants(
                csvFolder, modelTitle, users, product.ProductTag);

            if (variants.Count > 0)
            {
                RibbitUi.Alert(
                    $"This model has reports saved under {variants.Count} earlier name(s), from "
                    + "when it was opened as a local or detached copy:\n\n"
                    + string.Join("\n", variants.Select(v => $"  {v}"))
                    + "\n\nOpen Federated Clash once and they will be merged under one name, "
                    + "along with their statuses and notes. Then come back here.",
                    DialogTitle);

                return;
            }
        }

        if (reports.Count == 0)
        {
            RibbitUi.Alert(
                $"No clash-detection report found yet for this model in:\n{csvFolder}\n\n"
                + $"Run the \"{product.DisplayName}\" button at least once first.",
                DialogTitle);
            return;
        }

        string? latest = PickReport(reports, product);
        if (latest is null)
            return;   // cancelled at the picker

        List<Dictionary<string, string>> reportRows = StatusLedger.ReadRows(latest);
        if (reportRows.Count == 0)
        {
            RibbitUi.Alert(
                $"The most recent report file is empty or unreadable:\n{latest}", DialogTitle);
            return;
        }

        ClashLedger ledger = StatusLedger.LoadLedger(
            csvFolder, modelTitle, product.ProductTag);

        List<StatusRow> rows = BuildRows(reportRows, ledger);
        if (rows.Count == 0)
        {
            RibbitUi.Alert("The most recent report has no rows to flag.", DialogTitle);
            return;
        }

        List<string> fieldNames = [.. reportRows[0].Keys];

        SetClashStatusWindow window = new(
            $"{DialogTitle} - {product.DisplayName}", rows, latest);

        // Read back off the file we opened, so the re-export keeps the run's own
        // name instead of being written as an anonymous one.
        string? testName = product == ClashProduct.Federated
            ? TestNameOf(latest, ClashTestLibrary.Load().Tests)
            : null;

        window.Saved += updates => Save(
            updates, reportRows, fieldNames, csvFolder, modelTitle, product, ledger, testName);

        window.DeleteReport = () => DeleteReport(latest);

        // Notes live beside the reports, keyed by clash pair like status. Loaded
        // once here and written on every edit, so a note is on disk the moment
        // its dialog closes rather than riding on the Save button.
        ClashComments comments = ClashComments.Load(csvFolder, modelTitle, product.ProductTag);

        window.ReadComment = comments.TextFor;
        window.WriteComment = (clashKey, text) =>
        {
            comments.Set(clashKey, text);
            return comments.Save(csvFolder, modelTitle, product.ProductTag);
        };

        // Modeless: Revit stays interactive. Safe because nothing past this
        // point touches the Revit API - the Saved handler is CSV I/O only.
        window.Show();
    }

    /// <summary>How many runs back the picker offers. Older files stay on disk.</summary>
    private const int MaxReportChoices = 20;

    /// <summary>
    /// Which run to triage.
    ///
    /// FEDERATED ONLY, and only when there is something to choose between. The
    /// three original clash tools have no saved tests — every report of theirs is
    /// just "the last run", so a picker would be a dialog with one real answer.
    /// They go straight to the newest file exactly as before.
    ///
    /// The test behind a run is read off the FILENAME, which is where the run
    /// wrote it. Nothing else on disk records it: the ledger is keyed by clash
    /// pair and deliberately shared across tests, so it cannot say which test a
    /// row came from — and should not, since that sharing is what lets a clash
    /// stay Resolved no matter which test finds it next.
    ///
    /// A report written before names carried the test shows as "(no saved test)"
    /// rather than being hidden. It is still a real report.
    ///
    /// Returns null when the user cancelled, or deleted every report there was —
    /// in both cases there is nothing to open and the tool simply stops.
    /// </summary>
    private static string? PickReport(List<string> reports, ClashProduct product)
    {
        if (product != ClashProduct.Federated || reports.Count < 2)
            return reports[0];

        List<ClashTest> tests = ClashTestLibrary.Load().Tests;
        if (tests.Count == 0)
            return reports[0];

        List<ReportChoice> choices = [];

        foreach (string path in reports.Take(MaxReportChoices))
        {
            choices.Add(new ReportChoice(
                path,
                TestNameOf(path, tests) ?? "(no saved test)",
                $"{LastWrite(path):yyyy-MM-dd HH:mm}",
                Path.GetFileName(path)));
        }

        ReportPickWindow window = new($"{DialogTitle} - which run?", choices);
        window.DeleteReports = DeleteReports;

        return window.ShowDialog() == true ? window.Chosen : null;
    }

    /// <summary>
    /// The saved test a report file belongs to, or null if none claims it.
    ///
    /// LONGEST MATCH WINS, and that is the whole point of not using the first
    /// one. Tokens nest: a run of "55 vs 61 rev2" is written
    /// "..._55_vs_61_rev2_saved1.csv", which contains "_55_vs_61_" as a
    /// substring — so a first-match search hands rev2's runs to whichever of the
    /// two tests happens to sit earlier in the library. The longer token is
    /// always the more specific one.
    /// </summary>
    private static string? TestNameOf(string path, IReadOnlyList<ClashTest> tests)
    {
        string name = Path.GetFileName(path);

        string? best = null;
        int bestLength = 0;

        foreach (ClashTest test in tests)
        {
            string part = ClashStatus.TestNamePart(test.Name);

            if (part.Length > bestLength
                && name.Contains($"_{part}_", StringComparison.OrdinalIgnoreCase))
            {
                best = test.Name;
                bestLength = part.Length;
            }
        }

        return best;
    }

    /// <summary>
    /// Deletes several reports, answering with the ones that FAILED and why.
    ///
    /// Each is attempted independently. One report open in Excel must not stop
    /// the other ten from going — a delete that gives up on the first failure
    /// leaves the user to guess which ones it got to.
    /// </summary>
    private static IReadOnlyList<(string Path, string Error)> DeleteReports(
        IReadOnlyList<string> paths)
    {
        List<(string, string)> failed = [];

        foreach (string path in paths)
        {
            string? error = DeleteReport(path);
            if (error is not null)
                failed.Add((path, error));
        }

        return failed;
    }

    /// <summary>
    /// Removes one report file. Returns null on success, or why it failed.
    ///
    /// TO THE RECYCLE BIN, not a hard delete. A report is cheap to regenerate but
    /// this is the one destructive control in the window, and a mis-click on a
    /// run someone spent an afternoon triaging should be recoverable by the means
    /// every Windows user already knows.
    ///
    /// THE LEDGER IS LEFT ALONE, deliberately. Status is keyed by clash pair and
    /// shared across every run and every saved test; the report is a snapshot of
    /// it. Cascading the delete into the ledger would turn "tidy up an old file"
    /// into "lose the triage", including for runs still on disk.
    /// </summary>
    private static string? DeleteReport(string path)
    {
        try
        {
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
                path,
                Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);

            return null;
        }
        catch (Exception ex) when (ex is IOException
                                      or UnauthorizedAccessException
                                      or FileNotFoundException)
        {
            return ex.Message;
        }
    }

    private static DateTime LastWrite(string path)
    {
        try
        {
            return File.GetLastWriteTime(path);
        }
        catch (IOException)
        {
            return DateTime.MinValue;
        }
    }

    /// <summary>
    /// One row per CLASH, not per element.
    ///
    /// A report row exists per element per pair, so each clash contributes two.
    /// Grouping by the report's Clash Key collapses those into one editable row
    /// naming both sides — which is the granularity status is now stored at, and
    /// the reason a pipe clashing with three beams can be triaged three
    /// different ways instead of one.
    ///
    /// A report written before Clash Key existed falls back to the old
    /// host-element-only behaviour, so an old CSV still opens and still edits.
    /// </summary>
    internal static List<StatusRow> BuildRows(
        IReadOnlyList<Dictionary<string, string>> reportRows,
        ClashLedger ledger)
    {
        List<StatusRow> rows = [];
        HashSet<string> seenClashes = [];
        HashSet<StatusKey> seenElements = [];

        // Both sides of a clash, so a row can name them. Kept in pieces rather
        // than pre-joined so the window can make each id clickable.
        Dictionary<string, List<StatusSide>> sidesByClash = [];
        foreach (Dictionary<string, string> r in reportRows)
        {
            string ck = Value(r, StatusLedger.ClashKeyField);
            if (ck.Length == 0)
                continue;

            if (!sidesByClash.TryGetValue(ck, out List<StatusSide>? sides))
                sidesByClash[ck] = sides = [];

            string name = Value(r, "Element Name");
            if (name.Length == 0)
                name = Value(r, "Name");

            StatusSide side = new(Value(r, "Element Id"), Value(r, "Link"), name);

            // Deduped on identity, not on the label: two rows for one element
            // are the same side even if one of them lost its name.
            if (!sides.Any(s => s.ElementId == side.ElementId && s.Link == side.Link))
                sides.Add(side);
        }

        foreach (Dictionary<string, string> r in reportRows)
        {
            string clashKey = Value(r, StatusLedger.ClashKeyField);
            string eid = Value(r, "Element Id");
            string link = Value(r, "Link");

            if (clashKey.Length > 0)
            {
                if (!seenClashes.Add(clashKey))
                    continue;

                string current = ledger.Lookup(clashKey, new StatusKey(eid, link));
                if (current.Length == 0)
                    current = Value(r, "Status");

                List<StatusSide> sides = sidesByClash[clashKey];
                rows.Add(new StatusRow(
                    clashKey, "",
                    string.Join("   ↔   ", sides.Select(Label)),
                    current,
                    sides));

                continue;
            }

            // ---- legacy report with no Clash Key --------------------------
            // Host side only, exactly as before: flagging one element per pair
            // kept this to one row per clash rather than two.
            if (eid.Length == 0 || link.Length > 0)
                continue;

            StatusKey key = new(eid, link);
            if (!seenElements.Add(key))
                continue;

            string legacyStatus = ledger.Lookup(null, key);
            if (legacyStatus.Length == 0)
                legacyStatus = Value(r, "Status");

            string legacyName = Value(r, "Element Name");
            if (legacyName.Length == 0)
                legacyName = Value(r, "Name");

            rows.Add(new StatusRow("", eid, legacyName, legacyStatus));
        }

        return rows;
    }

    /// <summary>
    /// One side as a single string — the fallback for a window that cannot
    /// render the pieces, and the shape the row read as before it could.
    /// </summary>
    internal static string Label(StatusSide side)
    {
        string label = $"{side.ElementId} {side.Name}".Trim();
        return side.Link.Length > 0 ? $"{label} (in link: {side.Link})" : label;
    }

    private static string Value(IReadOnlyDictionary<string, string> row, string key)
        => row.TryGetValue(key, out string? v) ? v.Trim() : "";

    internal static void Save(
        List<(string ClashKey, string ElementId, string Status)> updates,
        IReadOnlyList<Dictionary<string, string>> reportRows,
        IReadOnlyList<string> fieldNames,
        string csvFolder,
        string modelTitle,
        ClashProduct product,
        ClashLedger ledger,
        string? testName)
    {
        try
        {
            if (updates.Count == 0)
            {
                RibbitUi.Notify("No status changes to save.", DialogTitle);
                return;
            }

            Dictionary<string, string> byClash = [];
            Dictionary<string, string> byElement = [];

            foreach ((string clashKey, string eid, string status) in updates)
            {
                if (clashKey.Length > 0)
                {
                    byClash[clashKey] = status;
                    ledger.SetPair(clashKey, status);
                }
                else if (eid.Length > 0)
                {
                    byElement[eid] = status;
                    ledger.SetElement(new StatusKey(eid, ""), status);
                }
            }

            // The LEDGER is what future runs read, so it is written first and is
            // the real save. The report re-export below only keeps the CSV on
            // disk consistent with what was on screen.
            //
            // A DELTA, not the whole ledger. Writing the whole thing back from
            // the copy loaded when this window opened would erase anything saved
            // by anyone else in the meantime - including yourself in the report
            // window next door.
            StatusLedger.AppendDelta(
                csvFolder, modelTitle, product.ProductTag,
                updates.Select(u => (u.ClashKey, u.ElementId, "", u.Status)));

            // Both rows of a clash share its key, so both sides pick up the new
            // status. Untouched rows keep what they had, so the file stays a
            // complete current snapshot rather than a diff.
            List<Dictionary<string, string>> updated = [];
            foreach (Dictionary<string, string> r in reportRows)
            {
                Dictionary<string, string> copy = new(r);
                string ck = Value(r, StatusLedger.ClashKeyField);

                if (ck.Length > 0 && byClash.TryGetValue(ck, out string? byPair))
                    copy["Status"] = byPair;
                else if (byElement.TryGetValue(Value(r, "Element Id"), out string? byId))
                    copy["Status"] = byId;

                updated.Add(copy);
            }

            string path = StatusLedger.WriteFullStatus(
                csvFolder, modelTitle, product.ProductTag, fieldNames, updated, testName);

            RibbitUi.ShowReport($"{DialogTitle} - saved", BuildSavedReport(updates, path, product));
            RibbitUi.Notify($"Saved {updates.Count} status update(s).", DialogTitle);
        }
        catch (Exception ex)
        {
            RibbitUi.Alert($"{DialogTitle} failed to save:\n\n{ex.Message}", DialogTitle);
        }
    }

    private static string BuildSavedReport(
        IReadOnlyList<(string ClashKey, string ElementId, string Status)> updates,
        string path,
        ClashProduct product)
    {
        StringBuilder sb = new();

        ReportHtml.Note(sb,
            $"Saved <strong>{updates.Count}</strong> status update(s). The full report was "
            + "re-exported with the new statuses applied, so this file is a complete current "
            + $"snapshot.<br/><br/>This shows up next time either the {ReportHtml.Escape(product.DisplayName)} "
            + "report or this tool reads the status ledger for this model.");

        List<IReadOnlyList<string>> rows =
        [
            .. updates.Select(u => new[]
            {
                ReportHtml.Escape(u.ClashKey.Length > 0
                    ? ClashPairKey.Describe(u.ClashKey)
                    : u.ElementId),
                ClashStatus.BadgeHtml(u.Status),
            })
        ];

        ReportHtml.Table(sb, "Statuses written", ["Clash", "Status"], rows);

        sb.Append("<p><code>").Append(ReportHtml.Escape(path)).Append("</code></p>");
        return sb.ToString();
    }
}
