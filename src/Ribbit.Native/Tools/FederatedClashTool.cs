using System.Diagnostics;
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
/// Federated Clash — both sides searched across the host model and every ticked
/// link at once.
///
/// THE FOURTH TOOL, NOT A REPLACEMENT. The three older clash buttons are
/// verified and stay untouched. This one has its own <see cref="ClashProduct"/>,
/// so it writes its own report and its own status ledger and cannot disturb
/// triage already recorded against the others.
///
/// MODEL SCOPE, NOT VIEW SCOPE. The other three pass the active view into the
/// engine, which makes a bounding box view-specific — anything hidden in the
/// current view is silently skipped. Here <c>activeView</c> is deliberately
/// null, giving model-wide boxes. That is what "federated" has to mean, and it
/// is only safe to choose because this is a new tool: changing it inside the
/// existing three would move every clash count they have ever produced.
/// </summary>
internal static class FederatedClashTool
{
    private const string DialogTitle = "Federated Clash";
    private const string BatchTitle = "Federated Clash - batch run";

    /// <summary>Filename token for a run that belonged to no saved test.</summary>
    private const string AdhocTestName = "adhoc";
    private const string DefaultPadText = "0 mm";

    private static readonly ClashProduct Product = ClashProduct.Federated;

    /// <summary>
    /// The model's name with Revit's open-method suffixes removed, and any files
    /// left under an earlier name folded in.
    ///
    /// FEDERATED ONLY. The three older tools keep using <c>doc.Title</c> raw:
    /// changing their prefix would orphan reports and ledgers they have been
    /// writing for months, and they were never the ones that needed this.
    ///
    /// The migration is called on the way past because this is the only moment
    /// the answer is needed and the folder is known. It does nothing after the
    /// first time, when no variant files remain.
    /// </summary>
    private static string NormalizedTitle(Document doc, string? csvFolder)
    {
        List<string> users = KnownUsers.For(doc.Application?.Username, csvFolder, Product.ProductTag);
        string normalized = ModelTitle.Normalize(doc.Title ?? "Model", users);

        if (normalized.Length == 0)
            normalized = doc.Title ?? "Model";

        EnsureMigrated(doc, csvFolder, normalized, DialogTitle, users);
        return normalized;
    }

    /// <summary>
    /// Folds earlier names of this model into the normalised one, in whichever
    /// folder is about to be written to.
    ///
    /// Called at EVERY folder, not just the saved default: a saved test carries
    /// its own <c>CsvFolder</c>, and a batch of eight tests can touch several. A
    /// folder that never gets merged stays split by title, which is the exact
    /// problem this was built to end.
    ///
    /// Cheap and idempotent - one directory listing, then nothing to do - so it
    /// is called freely rather than tracked.
    /// </summary>
    /// <param name="users">The user list already read for THIS folder, when the
    /// caller has it - so one run does not list the same folder twice.</param>
    private static void EnsureMigrated(
        Document doc, string? csvFolder, string modelTitle, string title,
        List<string>? users = null)
    {
        string? summary = FederatedTitleMigration.Run(
            csvFolder, modelTitle,
            users ?? KnownUsers.For(doc.Application?.Username, csvFolder, Product.ProductTag),
            Product.ProductTag);

        if (summary is not null)
            RibbitUi.Notify(summary, title);
    }

    public static void Run(UIDocument uidoc)
    {
        Document doc = uidoc.Document;
        string modelTitle = NormalizedTitle(
            doc, ClashSettings.Load(Product.ConfigSection).CsvFolder);

        // One reader per run. Its caches are keyed by (Document, ElementId), so
        // it is safe across the host and every link in one federation.
        ClashParameterReader reader = new();
        FederatedSources federation = new();

        List<ClashSource> sources = FederatedSources.Discover(doc);

        double scanSeconds = 0.0;

        // Cached per (set of sources, Description rule, extra categories):
        // flipping any of them changes what the picker may offer, so a list
        // gathered under one must never be served under the other.
        Dictionary<string, List<ValueChoice>> scanCache = [];

        // The extra categories are part of the cache key AND have to be declared
        // to the federation before it collects: it hands back exactly the
        // categories declared, collecting any it has not seen yet. Declaring them
        // second would serve buckets without them.
        static string ExtraKey(IReadOnlyList<BuiltInCategory> extra)
            => string.Join(",", extra.Select(b => (long)b).OrderBy(v => v));

        List<ValueChoice> Scan(
            IReadOnlyList<ClashSource> ticked,
            bool trayOnly,
            IReadOnlyList<BuiltInCategory> extra)
        {
            string key = trayOnly + "|" + ExtraKey(extra) + "|"
                         + string.Join("|", ticked.Select(s => s.Label));

            if (scanCache.TryGetValue(key, out List<ValueChoice>? cached))
                return cached;

            federation.UseExtraCategories(extra);

            Stopwatch sw = Stopwatch.StartNew();
            List<List<ValueChoice>> perSource = [];

            using (ProgressDialog pb = new("Scanning models - {0} of {1}", cancellable: false))
            {
                for (int i = 0; i < ticked.Count; i++)
                {
                    ClashSource src = ticked[i];
                    pb.Message($"Scanning {src.Label} ({i + 1} of {ticked.Count})...");

                    perSource.Add(ClashLinkSetBuilder.CollectDistinctValuesForDoc(
                        reader, federation.BucketsFor(src), trayOnly, (j, n) => pb.Update(j, n)));
                }
            }

            List<ValueChoice> merged = ClashLinkSetBuilder.MergeChoices(perSource);
            scanSeconds += sw.Elapsed.TotalSeconds;
            scanCache[key] = merged;
            return merged;
        }

        // Parameter NAMES across the ticked models, sampled. Cached per source
        // set: discovery walks whole elements, which is the expensive kind of
        // read, and the answer does not change while the window is open.
        Dictionary<string, List<ParameterChoice>> paramCache = [];

        List<ParameterChoice> DiscoverParameters(
            IReadOnlyList<ClashSource> ticked, IReadOnlyList<BuiltInCategory> extra)
        {
            string key = ExtraKey(extra) + "|" + string.Join("|", ticked.Select(s => s.Label));
            if (paramCache.TryGetValue(key, out List<ParameterChoice>? cached))
                return cached;

            federation.UseExtraCategories(extra);

            Dictionary<(string, bool), ParameterChoice> merged = [];

            using (ProgressDialog pb = new("Reading parameters - {0} of {1}", cancellable: false))
            {
                foreach (ClashSource src in ticked)
                {
                    pb.Message($"Reading parameters in {src.Label}...");

                    foreach (ParameterChoice p in ClashParameterCatalog.Discover(
                                 federation.BucketsFor(src), (i, n) => pb.Update(i, n)))
                    {
                        (string, bool) k = (p.Name, p.OnType);
                        merged[k] = merged.TryGetValue(k, out ParameterChoice? existing)
                            ? existing with { ElementCount = existing.ElementCount + p.ElementCount }
                            : p;
                    }
                }
            }

            List<ParameterChoice> result = [.. merged.Values];
            result.Sort((a, b) =>
            {
                int byCount = b.ElementCount.CompareTo(a.ElementCount);
                return byCount != 0
                    ? byCount
                    : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            });

            paramCache[key] = result;
            return result;
        }

        List<ValueChoice> ScanParameter(
            IReadOnlyList<ClashSource> ticked,
            string paramName,
            IReadOnlyList<BuiltInCategory> extra)
        {
            federation.UseExtraCategories(extra);

            List<List<ValueChoice>> perSource = [];

            using (ProgressDialog pb = new("Scanning - {0} of {1}", cancellable: false))
            {
                foreach (ClashSource src in ticked)
                {
                    pb.Message($"Reading \"{paramName}\" in {src.Label}...");

                    perSource.Add(ClashParameterCatalog.ValuesFor(
                        reader, federation.BucketsFor(src), paramName, (i, n) => pb.Update(i, n)));
                }
            }

            return ClashLinkSetBuilder.MergeChoices(perSource);
        }

        ClashSettings settings = ClashSettings.Load(Product.ConfigSection);
        ClashSetLibrary library = ClashSetLibrary.Load();
        ClashTestLibrary tests = ClashTestLibrary.Load();

        FederatedClashWindow window = new(
            sources,
            settings.CsvFolder,
            settings.SetAKeyword,
            settings.SetBKeyword,
            ClashSettings.PairsOf(settings.SetAValues),
            ClashSettings.PairsOf(settings.SetBValues),
            settings.PadValue,
            settings.PadUnit,
            settings.ToleranceValue,
            settings.ToleranceUnit,
            settings.DescriptionCableTrayOnly,
            library,
            tests,
            modelTitle,
            library.ResolveIds(settings.SetAIds, settings.SetANames),
            library.ResolveIds(settings.SetBIds, settings.SetBNames),
            Scan,
            DiscoverParameters,
            ScanParameter,
            () => ClashCategories.AllModelCategories(doc),

            // The workset audit, over whatever is ticked when it is asked. The
            // SAME reader and federation as the search, so its counts reuse the
            // buckets and parameter reads a Browse already paid for.
            (ticked, set) =>
            {
                using ProgressDialog pb = new("Reading worksets - {0} of {1}", cancellable: false);
                pb.Message($"Counting \"{set.Name}\" by workset...");
                return FederatedWorksetAudit.Collect(
                    reader, federation, ticked, set, settings.DescriptionCableTrayOnly);
            });

        if (window.ShowDialog() != true)
            return;

        // Checked BEFORE the null Result test: a batch request closes with no
        // Result, which would otherwise read as a cancel.
        if (window.BatchRequested)
        {
            RunBatch(uidoc);
            return;
        }

        if (window.Result is null)
            return;

        FederatedClashChoice choice = window.Result;

        settings.CsvFolder = choice.CsvFolder;
        settings.SetAKeyword = choice.SetAKeyword;
        settings.SetBKeyword = choice.SetBKeyword;
        settings.SetAValues = ClashSettings.ToStored(choice.SetAValues);
        settings.SetBValues = ClashSettings.ToStored(choice.SetBValues);
        settings.PadValue = choice.PadValue;
        settings.PadUnit = choice.PadUnit;
        settings.ToleranceValue = choice.ToleranceValue;
        settings.ToleranceUnit = choice.ToleranceUnit;
        settings.DescriptionCableTrayOnly = choice.DescriptionCableTrayOnly;
        settings.SetAIds = choice.SetAIds;
        settings.SetBIds = choice.SetBIds;
        settings.Save(Product.ConfigSection);

        try
        {
            // The folder can be changed in the window, so it may not be the one
            // normalising already merged.
            EnsureMigrated(doc, choice.CsvFolder, modelTitle, DialogTitle);

            // The outcome is collected even though the report IS shown, purely so
            // the CSV path is known if this run gets saved as a test.
            FederatedRunOutcome outcome = new();

            List<string> keys = RunSearch(
                doc, reader, federation, choice, library, modelTitle, scanSeconds, outcome);

            RecordRun(tests, choice, keys, modelTitle, outcome.CsvPath, outcome.Actions);
        }
        catch (Exception ex)
        {
            RibbitUi.Alert(
                $"Federated Clash failed partway through:\n\n{ex.Message}", DialogTitle);
        }
    }

    // ------------------------------------------------------------------ batch

    /// <summary>
    /// Runs several saved tests back to back and reports what each one found.
    ///
    /// Navisworks' "Update All", and the last big gap against it. A weekly
    /// coordination pass is not one test — it is eight, and running them one at a
    /// time means eight trips through the setup window and eight reports to hold
    /// in your head.
    ///
    /// SEQUENTIAL, and there is no choosing otherwise: the Revit API is
    /// single-threaded, so the tests run one after another however this is
    /// written. What batching actually buys is the setup and the reading, not the
    /// arithmetic.
    ///
    /// Each test writes its OWN CSV under its own name and updates its OWN
    /// baseline, exactly as if you had run it by hand — so the run picker, the
    /// status editor and the New badge all keep working afterwards. Only the
    /// report is different: one summary instead of eight windows.
    /// </summary>
    public static void RunBatch(UIDocument uidoc)
    {
        Document doc = uidoc.Document;
        string modelTitle = NormalizedTitle(
            doc, ClashSettings.Load(Product.ConfigSection).CsvFolder);

        ClashTestLibrary tests = ClashTestLibrary.Load();
        if (tests.Tests.Count == 0)
        {
            RibbitUi.Alert(
                "There are no saved tests to run yet.\n\n"
                + "Run a search, then say yes when it offers to save it as a test.",
                BatchTitle);
            return;
        }

        // NUMBERED, because the chosen LABEL is what comes back and it has to map
        // to exactly one test. Names are unique today only because the save path
        // checks - a rename path that forgot to would make two labels identical,
        // and this would then run one test twice and skip the other in silence.
        List<string> labels =
        [
            .. tests.Tests.Select((t, i) => $"{i + 1}.  {t.Name}   -   {t.LastRunSummary()}")
        ];

        List<string>? picked = RibbitUi.SelectManyFromList(
            labels, $"{BatchTitle} - which tests?", "Run");

        if (picked is null || picked.Count == 0)
            return;

        List<ClashTest> chosen =
            [.. picked.Select(p => tests.Tests[labels.IndexOf(p)])];

        ClashSetLibrary library = ClashSetLibrary.Load();
        List<ClashSource> available = FederatedSources.Discover(doc);

        List<(ClashTest Test, FederatedRunOutcome Outcome, string? Failure, List<string> Missing,
            List<string> MissingSets)> results = [];

        // ONE reader and ONE federation for the whole batch, so tests that search
        // the same models reuse the collected buckets and the parameter reads
        // instead of paying for them again. A test needing a category no earlier
        // test collected gets just that category collected, since buckets
        // gathered without it cannot answer questions about it.
        ClashParameterReader reader = new();
        FederatedSources federation = new();

        // Keyed by FOLDER. Tests each carry their own CSV folder, and a baseline
        // written to the wrong one is a test that silently stops badging.
        Dictionary<string, SeenLedger> seenByFolder = [];

        for (int i = 0; i < chosen.Count; i++)
        {
            ClashTest test = chosen[i];

            (FederatedClashChoice choice, List<string> missing, List<string> missingSets) =
                ChoiceFor(test, library, available);

            if (choice.Sources.Count == 0)
            {
                results.Add((
                    test, new FederatedRunOutcome(), "no models to search", missing, missingSets));
                continue;
            }

            FederatedRunOutcome outcome = new();

            try
            {
                // Per TEST, because each carries its own folder. Eight tests can
                // point at several, and only the default was merged on the way in.
                EnsureMigrated(doc, choice.CsvFolder, modelTitle, BatchTitle);

                List<string> keys = RunSearch(
                    doc, reader, federation, choice, library, modelTitle, 0.0, outcome,
                    showReport: false);

                // Loaded once per folder and saved after the loop. Reloading per
                // test would drop the baselines written by earlier tests in this
                // same batch that happen to share the folder.
                if (!seenByFolder.TryGetValue(choice.CsvFolder, out SeenLedger? seen))
                {
                    seen = SeenLedger.Load(choice.CsvFolder, modelTitle, Product.ProductTag);
                    seenByFolder[choice.CsvFolder] = seen;
                }

                seen.Record(test.Id, keys);

                test.LastRunUtc = DateTime.UtcNow.ToString("o");
                test.LastRunClashes = keys.Count;

                results.Add((test, outcome, null, missing, missingSets));
            }
            catch (Exception ex)
            {
                // ONE TEST FAILING MUST NOT LOSE THE OTHER SEVEN. The failure is
                // recorded and the batch carries on; the summary names it.
                results.Add((test, outcome, ex.Message, missing, missingSets));
            }
        }

        tests.Save();

        foreach ((string folder, SeenLedger ledger) in seenByFolder)
            ledger.Save(folder, modelTitle, Product.ProductTag);

        RibbitUi.ShowReport(BatchTitle, BuildBatchReport(results));
        RibbitUi.Notify($"Ran {results.Count} test(s).", BatchTitle);
    }

    /// <summary>
    /// A saved test as a runnable search, plus any models it names that are gone.
    ///
    /// The composition MUST match what the setup window does on Run, or a test
    /// would mean one thing by hand and another in a batch: the typed keyword and
    /// the sets' keywords are joined with commas, the picked values and the sets'
    /// values are unioned case-insensitively, and the categories come from the
    /// sets. A missing model is reported rather than silently dropped — a batch
    /// that quietly searched fewer models than you asked for would show fewer
    /// clashes and look like progress.
    /// </summary>
    internal static (FederatedClashChoice Choice, List<string> Missing, List<string> MissingSets)
        ChoiceFor(
            ClashTest test, ClashSetLibrary library, IReadOnlyList<ClashSource> available)
    {
        List<ClashSource> sources;
        List<string> missing = [];

        // Sets the test names that are no longer in the library. A batch shows no
        // per-test report, so a dropped set is otherwise invisible: the run just
        // searches on less and returns a smaller number that looks like progress.
        List<string> missingSets = [];

        if (test.AllModels)
        {
            sources = [.. available];
        }
        else
        {
            sources = [];
            List<string>? users = available.FirstOrDefault(s => s.IsHost) is { } host
                ? FederatedSources.UsersFor(host)
                : null;

            foreach (string name in test.SourceNames)
            {
                ClashSource? found = available.FirstOrDefault(
                    s => FederatedSources.Matches(s, name, users));

                if (found is null)
                    missing.Add(name);
                else
                    sources.Add(found);
            }
        }

        // The SAME composer the setup window uses - see FederatedSides.
        List<SidePart> partsA = FederatedSides.Compose(
            test.SetAKeyword, ClashSettings.PairsOf(test.SetAValues), SetsOf(test.SetAIds));
        List<SidePart> partsB = FederatedSides.Compose(
            test.SetBKeyword, ClashSettings.PairsOf(test.SetBValues), SetsOf(test.SetBIds));

        (string keywordA, List<(string, string)> valuesA, List<BuiltInCategory> bicsA) =
            FederatedSides.Merge(partsA);
        (string keywordB, List<(string, string)> valuesB, List<BuiltInCategory> bicsB) =
            FederatedSides.Merge(partsB);

        // NormalizePadText returns null ONLY for null input, and an interpolated
        // string is never null - so neither of these is. Asserted rather than
        // left as a permanent warning: three standing warnings in this file are
        // exactly where a real nullability bug would go unnoticed.
        string padText = ClashSearchNaming.NormalizePadText($"{test.PadValue} {test.PadUnit}")!;
        string toleranceText =
            ClashSearchNaming.NormalizePadText($"{test.ToleranceValue} {test.ToleranceUnit}")!;

        FederatedClashChoice choice = new(
            sources, keywordA, keywordB, valuesA, valuesB, bicsA, bicsB,
            [.. test.SetAIds], [.. test.SetBIds],
            padText, test.PadValue, test.PadUnit,
            toleranceText, test.ToleranceValue, test.ToleranceUnit,
            test.DescriptionCableTrayOnly,
            test.CsvFolder,
            test.Id,
            test.AllModels,

            // A batch never creates a test, but these carry the test's own rules
            // rather than the merged ones so the record means the same thing
            // whichever path built it.
            test.SetAKeyword,
            test.SetBKeyword,
            [.. ClashSettings.PairsOf(test.SetAValues)],
            [.. ClashSettings.PairsOf(test.SetBValues)],
            partsA,
            partsB);

        return (choice, missing, missingSets);

        // The test's sets that still exist. A missing one is recorded, never
        // silently dropped - see missingSets above.
        List<ClashSet> SetsOf(List<string> setIds)
        {
            List<ClashSet> found = [];

            foreach (string id in setIds)
            {
                ClashSet? set = library.FindById(id);
                if (set is null)
                    missingSets.Add(id);
                else
                    found.Add(set);
            }

            return found;
        }
    }

    private static string BuildBatchReport(
        IReadOnlyList<(ClashTest Test, FederatedRunOutcome Outcome, string? Failure,
            List<string> Missing, List<string> MissingSets)> results)
    {
        StringBuilder sb = new();
        sb.Append("<h2>Federated Clash - batch run</h2>");

        int total = results.Sum(r => r.Outcome.Clashes);
        int totalNew = results.Sum(r => r.Outcome.New);
        double seconds = results.Sum(r => r.Outcome.Seconds);

        sb.Append("<ul>");
        sb.Append($"<li><strong>Tests run:</strong> {results.Count}</li>");
        sb.Append($"<li><strong>Clashes found:</strong> {total}</li>");
        sb.Append($"<li><strong>New since each test last ran:</strong> {totalNew}</li>");
        sb.Append($"<li><strong>Total time:</strong> {seconds:F1}s</li>");
        sb.Append("</ul>");

        sb.Append("<table><thead><tr>")
          .Append("<th>Test</th><th>Clashes</th><th>New</th><th>Time</th><th>Report</th>")
          .Append("</tr></thead><tbody>");

        foreach ((ClashTest test, FederatedRunOutcome outcome, string? failure, _, _) in results)
        {
            sb.Append("<tr><td>").Append(ReportHtml.Escape(test.Name)).Append("</td>");

            if (failure is not null)
            {
                sb.Append("<td colspan=\"4\"><strong>failed:</strong> ")
                  .Append(ReportHtml.Escape(failure))
                  .Append("</td></tr>");
                continue;
            }

            sb.Append("<td>").Append(outcome.Clashes).Append("</td>");

            // The New count is the reason to read this table at all, so it is
            // badged rather than left as a digit among digits.
            sb.Append("<td>")
              .Append(outcome.New > 0
                  ? $"<strong>{outcome.New}</strong>"
                  : "-")
              .Append("</td>");

            sb.Append("<td>").Append($"{outcome.Seconds:F1}s").Append("</td>");
            sb.Append("<td><code>")
              .Append(ReportHtml.Escape(
                  outcome.CsvPath.Length > 0
                      ? System.IO.Path.GetFileName(outcome.CsvPath)
                      : outcome.Note ?? "-"))
              .Append("</code></td></tr>");
        }

        sb.Append("</tbody></table>");

        List<string> missingAll =
            [.. results.SelectMany(r => r.Missing).Distinct(StringComparer.OrdinalIgnoreCase)];

        if (missingAll.Count > 0)
        {
            ReportHtml.Note(sb,
                $"<strong>{missingAll.Count} model(s) named by a test are not loaded</strong> and "
                + "were not searched: "
                + ReportHtml.Escape(string.Join(", ", missingAll))
                + " - those tests searched less than they were built to, so their counts are "
                + "lower than a full run would give.");
        }

        // Named by TEST, not by id. A set that is gone has no name left to print,
        // so the id would be the only thing on offer and it tells nobody which
        // test to go and fix.
        List<string> setGaps =
        [
            .. results.Where(r => r.MissingSets.Count > 0)
                      .Select(r => $"{r.Test.Name} ({r.MissingSets.Count})")
        ];

        if (setGaps.Count > 0)
        {
            ReportHtml.Note(sb,
                $"<strong>{setGaps.Count} test(s) name a set that no longer exists</strong> and "
                + "ran without it: "
                + ReportHtml.Escape(string.Join(", ", setGaps))
                + " - a deleted set contributes no keywords, values or categories, so those "
                + "tests searched for LESS than they were built to and their counts are lower "
                + "than a full run would give. Open the test and pick a replacement set.");
        }

        // A batch shows no per-test report, so a test whose sets left worksets
        // out would otherwise read as a plain smaller count.
        List<string> worksetGaps =
        [
            .. results.Where(r => r.Failure is null && r.Outcome.WorksetsLeftOut.Count > 0)
                      .Select(r => $"{r.Test.Name} ({string.Join("; ", r.Outcome.WorksetsLeftOut)})")
        ];

        if (worksetGaps.Count > 0)
        {
            ReportHtml.Note(sb,
                $"<strong>{worksetGaps.Count} test(s) ran with worksets left out</strong> by their sets: "
                + ReportHtml.Escape(string.Join(" | ", worksetGaps))
                + " - nothing on those worksets was searched, so clashes on them are not counted.");
        }

        ReportHtml.Note(sb,
            "Each test wrote its own CSV under its own name. Open any of them with "
            + "<strong>Set Clash Status</strong> to triage - the run picker lists them by test "
            + "and date.");

        return sb.ToString();
    }

    /// <summary>
    /// Renames a just-written "adhoc" report to carry the test it became.
    ///
    /// Only touches the one token, so the model, timestamp and counter the rest
    /// of the system matches on are untouched. Silent on failure - a report with
    /// the wrong name in it is worth far less than the run it describes.
    /// </summary>
    private static void RenameAdhocReport(string? reportPath, string testName)
    {
        if (string.IsNullOrEmpty(reportPath) || !File.Exists(reportPath))
            return;

        string part = ClashStatus.TestNamePart(testName);
        if (part.Length == 0)
            return;

        string name = Path.GetFileName(reportPath);
        if (!name.Contains($"_{AdhocTestName}_"))
            return;

        string target = Path.Combine(
            Path.GetDirectoryName(reportPath)!,
            name.Replace($"_{AdhocTestName}_", $"_{part}_"));

        try
        {
            if (!File.Exists(target))
                File.Move(reportPath, target);
        }
        catch (Exception)
        {
        }
    }

    /// <summary>
    /// Stamps the run on its test, or offers to save the search as one.
    ///
    /// THE OFFER IS THE POINT. Building a test up front is work nobody does; you
    /// have just built the search anyway, so naming it is one click. This is the
    /// same trick that filled the set library — "Save as set..." beside the chips
    /// rather than a separate window nobody opened.
    ///
    /// Only offered when a real search ran. Being asked to name a test that found
    /// nothing is asking you to keep a mistake.
    /// </summary>
    private static void RecordRun(
        ClashTestLibrary tests,
        FederatedClashChoice choice,
        List<string> keys,
        string modelTitle,
        string? reportPath,
        ClashReportActions? actions)
    {
        int found = keys.Count;

        // Loaded here rather than passed through, so the baseline is written in
        // ONE place for both paths - the test that already existed, and the test
        // this offer is about to create.
        SeenLedger seen = SeenLedger.Load(choice.CsvFolder, modelTitle, Product.ProductTag);

        ClashTest? test = tests.FindById(choice.TestId);

        if (test is not null)
        {
            test.LastRunUtc = DateTime.UtcNow.ToString("o");
            test.LastRunClashes = found;
            tests.Save();

            // This run becomes next run's baseline.
            seen.Record(test.Id, keys);
            seen.Save(choice.CsvFolder, modelTitle, Product.ProductTag);
            return;
        }

        if (found <= 0)
            return;

        if (!RibbitUi.Confirm(
                $"Save this search as a test so it can be re-run?\n\n"
                + $"It found {found} clash(es). A test remembers the models, both sets, the "
                + "clearance and the tolerance - not the results.",
                DialogTitle))
            return;

        TextPromptWindow prompt = new("Save as test", null, "Test name", null);
        if (prompt.ShowDialog() != true || prompt.Value.Length == 0)
            return;

        if (tests.NameTaken(prompt.Value))
        {
            RibbitUi.Alert(
                $"A test called \"{prompt.Value}\" already exists - not saved.", DialogTitle);
            return;
        }

        string newId = Guid.NewGuid().ToString("N");

        tests.Tests.Add(new ClashTest
        {
            Id = newId,
            Name = prompt.Value,
            AllModels = choice.AllModels,

            // The HOST normalised, links raw - see the same choice in
            // FederatedClashWindow.Capture, which this mirrors.
            SourceNames = choice.AllModels
                ? []
                : [.. choice.Sources.Select(s => s.IsHost ? modelTitle : s.Name)],
            SetAIds = choice.SetAIds,
            SetBIds = choice.SetBIds,

            // The RAW inputs, not the merged ones the search ran on. Storing the
            // merged forms froze the chosen sets' contents into the test, so
            // deleting a value from a set left it still matching here - the set
            // library's one promise, broken by the convenience of reusing what
            // was already to hand.
            SetAKeyword = choice.SetAKeywordTyped,
            SetBKeyword = choice.SetBKeywordTyped,
            SetAValues = ClashSettings.ToStored(choice.SetAPicked),
            SetBValues = ClashSettings.ToStored(choice.SetBPicked),
            PadValue = choice.PadValue,
            PadUnit = choice.PadUnit,
            ToleranceValue = choice.ToleranceValue,
            ToleranceUnit = choice.ToleranceUnit,
            DescriptionCableTrayOnly = choice.DescriptionCableTrayOnly,
            CsvFolder = choice.CsvFolder,
            // NORMALISED, as the window's Save as... stores it. The raw host name
            // is doc.Title, which the window compares against the normalised one,
            // so a test made from this offer said "built in <another model>"
            // while open in the very model that built it.
            ModelTitle = modelTitle,
            LastRunUtc = DateTime.UtcNow.ToString("o"),
            LastRunClashes = found,
        });

        tests.Save();

        // SEED THE BASELINE FROM THE RUN THAT CREATED THE TEST.
        //
        // Without this a test made from the offer starts blind: its first rerun
        // has nothing to compare against, so nothing is badged and the run
        // silently looks unchanged. The run that just finished DID see these
        // clashes, so it is the honest baseline — and this was found in the
        // field, as a test that never badged anything.
        seen.Record(newId, keys);
        seen.Save(choice.CsvFolder, modelTitle, Product.ProductTag);

        // The report was written BEFORE this test existed, so it is named
        // "adhoc" - which is honest but leaves the run picker reporting "(no
        // saved test)" for the very run that created the test. Renaming it is
        // the only chance to say what it actually was.
        RenameAdhocReport(reportPath, prompt.Value);

        // The report is still open and can still save statuses, each one writing
        // a fresh CSV. Without this they would all be named "adhoc" until the
        // test was next re-run - which is exactly how this was reported.
        if (actions is not null)
            actions.TestName = prompt.Value;

        RibbitUi.Notify($"Saved test \"{prompt.Value}\".");
    }

    /// <summary>
    /// Builds one side across every ticked source and unions the result.
    ///
    /// PER DOCUMENT, THEN UNIONED — never one flattened pool. That is not an
    /// optimisation: <c>BuildSet</c>'s fitting and insulation expansion walks a
    /// connector graph, which stops at its own document boundary. Feeding it two
    /// documents at once would be asking it a question that has no answer.
    ///
    /// PIECE BY PIECE. A side is the pieces <see cref="FederatedSides.Compose"/>
    /// made: each is built across every source, and a piece whose set leaves
    /// worksets out drops what sits on them - each element judged by its OWN
    /// workset, in its own model, fittings and insulation the expansion pulled
    /// in included. The pieces are then unioned, so an element one set leaves
    /// out but another piece selects is still in: an exclusion belongs to its
    /// set, not to the side.
    ///
    /// <paramref name="stats"/>, when given, gets one entry per piece that left
    /// something out, for the report - a left-out workset is a clash nobody will
    /// see, so it is always said.
    /// </summary>
    internal static List<Element> BuildSide(
        ClashParameterReader reader,
        FederatedSources federation,
        IReadOnlyList<ClashSource> sources,
        IReadOnlyList<SidePart> parts,
        bool trayOnly,
        HashSet<(string Param, string Value)> hitPairs,
        WorksetLookup worksets,
        List<SidePartStats>? stats = null,
        IReadOnlyList<string>? existingWorksets = null)
    {
        List<Element> collected = [];
        List<(SidePart Part, List<Element> Dropped)> drops = [];

        foreach (SidePart part in parts)
        {
            List<Element> built = [];

            foreach (ClashSource src in sources)
            {
                built.AddRange(ClashLinkSetBuilder.BuildSet(
                    reader, federation.BucketsFor(src), part.Keyword, trayOnly, part.Bics,
                    part.Values, hitPairs));
            }

            if (!part.LeavesOut)
            {
                collected.AddRange(built);
                continue;
            }

            List<Element> dropped = [];
            foreach (Element e in built)
            {
                if (WorksetAudit.IsExcluded(part.ExcludedWorksets, worksets.NameOf(e)))
                    dropped.Add(e);
                else
                    collected.Add(e);
            }

            drops.Add((part, dropped));
        }

        // Keyed by (Document, ElementId), so two models sharing a raw id are
        // never mistaken for one element.
        List<Element> side = ClashSetBuilder.DedupeById(collected);

        if (stats is not null && drops.Count > 0)
        {
            HashSet<(Document, long)> kept = [.. side.Select(e => (e.Document, RevitCompat.IdKey(e.Id)))];
            // Passed in by RunSearch, which builds both sides over the same
            // sources - read once per run, not once per side.
            List<string> exist =
            [
                .. existingWorksets
                   ?? [.. sources.SelectMany(s => WorksetLookup.UserWorksets(s.Document)).Select(w => w.Name)]
            ];

            foreach ((SidePart part, List<Element> dropped) in drops)
            {
                List<Element> distinct = ClashSetBuilder.DedupeById(dropped);
                int stillIn = distinct.Count(e => kept.Contains((e.Document, RevitCompat.IdKey(e.Id))));

                stats.Add(new SidePartStats(
                    part, distinct.Count, stillIn, WorksetAudit.Stale(part.ExcludedWorksets, exist)));
            }
        }

        return side;
    }

    /// <summary>
    /// What one run produced, for a caller that is not going to show its report.
    ///
    /// A BATCH RUN CANNOT OPEN EIGHT REPORT WINDOWS. Passing one of these to
    /// <see cref="RunSearch"/> switches it from "show the user the answer" to
    /// "hand the answer back": the CSV is still written, the ledger is still
    /// read, the baseline is still updated — only the window and the toast are
    /// suppressed. Null means the interactive path, unchanged.
    /// </summary>
    internal sealed class FederatedRunOutcome
    {
        public int Clashes { get; set; }

        public int New { get; set; }

        public string CsvPath { get; set; } = "";

        public double Seconds { get; set; }

        /// <summary>Set when the run produced nothing, saying why.</summary>
        public string? Note { get; set; }

        /// <summary>
        /// The open report's action handler, so a run that becomes a test can
        /// tell it. Null for a batch, which shows no report.
        /// </summary>
        public ClashReportActions? Actions { get; set; }

        /// <summary>
        /// The report as written, whether or not it was shown - so a batch row and
        /// a test can read what a person would have read.
        /// </summary>
        public string Html { get; set; } = "";

        /// <summary>
        /// "Set B - 52 Series: MEP_ZZ_Typical" for every set that left worksets
        /// out, so the batch summary can name the tests that searched less.
        /// </summary>
        public List<string> WorksetsLeftOut { get; set; } = [];

        /// <summary>
        /// The earlier collections the report's Collect line named as reused - so
        /// a test checks what the REPORT counted, not a re-derivation of it.
        /// </summary>
        public List<SourceTiming> Reused { get; set; } = [];
    }

    internal static List<string> RunSearch(
        Document doc,
        ClashParameterReader reader,
        FederatedSources federation,
        FederatedClashChoice choice,
        ClashSetLibrary library,
        string modelTitle,
        double scanSeconds,
        FederatedRunOutcome? outcome = null,
        bool showReport = true)
    {
        StringBuilder sb = new();
        sb.Append("<h2>Federated Clash</h2>");
        sb.Append("<p>")
          .Append(ReportHtml.Escape(
              $"Searched {choice.Sources.Count} model(s): "
              + string.Join(", ", choice.Sources.Select(s => s.Label))))
          .Append("</p>");

        ClashLedger ledger = StatusLedger.LoadLedger(
            choice.CsvFolder, modelTitle, Product.ProductTag);

        // Names the report after the test that produced it. Ad-hoc runs say so
        // rather than being indistinguishable from a test whose name was blank.
        string testName =
            ClashTestLibrary.Load().FindById(choice.TestId)?.Name ?? AdhocTestName;

        // What this TEST saw last time - the baseline for the New badge. An
        // ad-hoc search has no id, so it gets no baseline and no badges, which
        // is the honest answer rather than badging everything.
        SeenLedger seen = SeenLedger.Load(choice.CsvFolder, modelTitle, Product.ProductTag);
        List<string>? previousKeys = seen.Previous(choice.TestId);

        // Declared BEFORE anything is collected. A category outside the 24 MEP
        // ones - Structural Framing, Walls - is otherwise never gathered, and a
        // set naming it would match nothing while looking like a clean run.
        federation.UseExtraCategories([.. choice.SetABics, .. choice.SetBBics]);

        // This run's collection only: the federation keeps what it collected
        // for the whole session, and its timings with it.
        int collectMark = federation.TimingMark;

        Stopwatch watch = Stopwatch.StartNew();
        HashSet<(string Param, string Value)> hitPairs = [];

        List<Element> setA;
        List<Element> setB;
        WorksetLookup worksets = new();
        List<SidePartStats> leftOutA = [];
        List<SidePartStats> leftOutB = [];

        // Once for both sides: they search the same models.
        bool anyWorkshared = choice.Sources.Any(s => s.Document.IsWorkshared);

        // Read only when some set leaves worksets out - the common run has none,
        // and nothing else needs the names.
        bool anyLeavesOut = choice.SetAParts.Concat(choice.SetBParts).Any(p => p.LeavesOut);
        List<string>? existingWorksets = anyLeavesOut
            ? [.. choice.Sources.SelectMany(s => WorksetLookup.UserWorksets(s.Document)).Select(w => w.Name)]
            : null;

        using (ProgressDialog pb = new("Building sets - {0} of {1}", cancellable: false))
        {
            pb.Message("Building Set A across all models...");
            setA = BuildSide(
                reader, federation, choice.Sources, choice.SetAParts,
                choice.DescriptionCableTrayOnly, hitPairs, worksets, leftOutA, existingWorksets);

            pb.Message("Building Set B across all models...");
            setB = BuildSide(
                reader, federation, choice.Sources, choice.SetBParts,
                choice.DescriptionCableTrayOnly, hitPairs, worksets, leftOutB, existingWorksets);
        }

        List<(string Side, SidePartStats Stats)> leftOut =
        [
            .. leftOutA.Select(s => ("Set A", s)),
            .. leftOutB.Select(s => ("Set B", s)),
        ];

        if (outcome is not null)
        {
            outcome.WorksetsLeftOut =
            [
                .. leftOut.Select(l =>
                    $"{l.Side} - {l.Stats.Part.SetName}: {string.Join(", ", l.Stats.Part.ExcludedWorksets)}")
            ];
        }

        List<(string Param, string Value)> missedPicks =
        [
            .. choice.SetAValues.Concat(choice.SetBValues)
                .Distinct()
                .Where(p => !hitPairs.Contains(p))
        ];

        double padFt = UnitParse.ToFeet(ClashSearchNaming.NormalizePadText(choice.PadText))
                       ?? UnitParse.ToFeet(DefaultPadText) ?? 0.0;
        if (padFt < 0.0)
            padFt = 0.0;

        // Unparseable tolerance means NO tolerance, never a guessed one: a
        // tolerance removes rows from the report, so a typo must fail towards
        // reporting more rather than fewer.
        double toleranceFt = UnitParse.ToFeet(
            ClashSearchNaming.NormalizePadText(choice.ToleranceText)) ?? 0.0;
        if (toleranceFt < 0.0)
            toleranceFt = 0.0;

        Dictionary<Document, List<(RevitLinkInstance Instance, Transform Tr)>> linkMap =
            ClashLinks.BuildTransformMap(doc);

        ClashResult result;

        using (ProgressDialog pb = new("Checking clashes - {0} of {1}", cancellable: false))
        {
            // activeView deliberately null - see the class remarks. Model scope
            // is the whole point of this tool.
            //
            // reportClearance: true is the other deliberate difference. In the
            // three older tools the pad only widens the candidate search and the
            // solid test then discards every near miss, so their clearance
            // setting does nothing for anything with geometry. Here a pair within
            // the pad but not touching is reported as a clearance clash.
            result = ClashEngine.Run(
                doc, null, setA, setB, padFt, linkMap, (i, n) => pb.Update(i, n),
                reportClearance: true, toleranceFt: toleranceFt, capturePoint: true,
                detectDuplicates: true);
        }

        // Document-aware: both sides draw from overlapping pools here, so an
        // element meets itself and every real clash is found twice with the sides
        // swapped. The plain ClashNoiseFilter cannot be used - it keys on a bare
        // ElementId, which collides across documents.
        FederatedNoiseFilter noise = new();
        List<ClashPair> filtered = noise.Apply(result.Pairs);

        List<ClashPair> pairs =
        [
            .. filtered
                .OrderBy(p => RevitCompat.IdKey(p.A.Id))
                .ThenBy(p => RevitCompat.IdKey(p.B.Id))
        ];

        watch.Stop();
        double clashSeconds = watch.Elapsed.TotalSeconds;

        sb.Append("<ul>");
        sb.Append($"<li><strong>Set A:</strong> {setA.Count} elements</li>");
        sb.Append($"<li><strong>Set B:</strong> {setB.Count} elements</li>");
        sb.Append($"<li><strong>Clashing pairs:</strong> {pairs.Count}");

        // The two kinds of drop are stated SEPARATELY. Self/joined/mirrored pairs
        // are not clashes at all; an insulation duplicate is a real clash that is
        // simply already on the list under the pipe that carries it. Rolling them
        // into one number would make the second look like more noise-suppression
        // than it is.
        int insulationDupes = noise.InsulationDuplicatesDropped;
        int dropped = result.Pairs.Count - filtered.Count - insulationDupes;

        if (dropped > 0)
            sb.Append($" &nbsp;<em>({dropped} self, joined or mirrored pair(s) removed)</em>");

        sb.Append("</li>");

        if (insulationDupes > 0)
        {
            sb.Append($"<li><strong>Insulation already covered:</strong> {insulationDupes} pair(s) "
                      + "hidden because the pipe or duct inside them reports the same clash. An "
                      + "insulation clash whose host does NOT clash is still listed - that is the "
                      + "case where the service fits and its lagging does not.</li>");
        }

        ClashResultsTable.AppendTiming(sb, scanSeconds, clashSeconds, result.Info.CellSizeFt);

        // Per SOURCE, not just a total: one aggregate number hides the fact that
        // most of a slow scan was one badly-modelled link, which is the only part
        // anyone can act on.
        // This run's own collection first. Whatever was collected EARLIER this
        // session - a Browse, a workset audit, parameter discovery - is what the
        // run reused: still named per source, because a slow link should be
        // visible whichever step happened to pay for it, and that time counts
        // in no other line of the report.
        List<string> collect = [];
        if (federation.CollectedSince(collectMark))
            collect.Add(federation.TimingSummary(collectMark));
        else
            collect.Add("nothing new");

        // Only earlier work this run actually REUSED: collections of the models
        // it searched that fetched a category it searched. An audit that
        // collected Structural Framing, or a link this run left unticked, was
        // earlier work - not this run's.
        List<SourceTiming> reused = federation.TimingsBetween(
            0, collectMark, [.. choice.Sources.Select(s => s.Label)], federation.CategoriesInPlay);

        if (outcome is not null)
            outcome.Reused = reused;

        if (reused.Count > 0)
            collect.Add("reused from earlier this session: " + FederatedSources.Summarize(reused));

        sb.Append("<li><strong>Collect:</strong> ")
          .Append(ReportHtml.Escape(string.Join(" - ", collect)))
          .Append("</li>");

        ClashResultsTable.AppendSets(
            sb, library.NamesFor(choice.SetAIds), library.NamesFor(choice.SetBIds));
        ClashResultsTable.AppendMatchRule(sb, choice.DescriptionCableTrayOnly);
        sb.Append("<li><strong>Scope:</strong> whole model (not limited to the active view)</li>");

        // Stated even when nothing is left out, like the tolerance: a workset
        // left out is clashes nobody will see, and a report that only mentions
        // it when it happens cannot be told apart from one that forgot to.
        sb.Append("<li><strong>Worksets left out:</strong> ");
        if (leftOut.Count == 0)
        {
            sb.Append("none (every workset searched)");
        }
        else
        {
            sb.Append(string.Join("; ", leftOut.Select(l =>
                ReportHtml.Escape(
                    $"{l.Side} - {l.Stats.Part.SetName}: {string.Join(", ", l.Stats.Part.ExcludedWorksets)} "
                    + $"({l.Stats.Dropped} element(s) left out"
                    + (l.Stats.StillIn > 0
                        ? $", {l.Stats.StillIn} of them still searched through another rule on {l.Side}"
                        : "")
                    + ")"))));
        }

        sb.Append("</li>");

        // Stated even when zero. A tolerance that removes most of the report is
        // otherwise indistinguishable from a model that got better.
        sb.Append("<li><strong>Tolerance:</strong> ")
          .Append(toleranceFt > 0.0
              ? $"{ReportHtml.Escape(choice.ToleranceText)} - "
                + $"{result.Info.SuppressedByTolerance} overlap(s) too shallow to report"
              : "off (every overlap reported, however shallow)")
          .Append("</li>");

        int clearanceCount = pairs.Count(p => p.IsClearance);
        sb.Append("<li><strong>Clearance:</strong> ")
          .Append(padFt > 0.0
              ? $"reported - {clearanceCount} pair(s) within the pad but not touching"
              : "off (pad is zero, so only real overlaps are reported)")
          .Append("</li>");

        sb.Append("</ul>");

        if (padFt > 0.0 && clearanceCount > 0)
        {
            ReportHtml.Note(sb,
                "<strong>Clearance gaps are measured between bounding boxes, not surfaces.</strong> "
                + "A bounding box is larger than the geometry inside it, so a reported gap is a "
                + "LOWER bound on the real separation - these pairs are at least this far apart, "
                + "possibly further. The other three clash tools do not report clearance at all.");
        }

        // A left-out name no searched model has: renamed since the set was
        // saved, or only in a model that is not ticked. It left NOTHING out, so
        // whatever it was meant to hide is in this report - say why.
        // When NO searched model is workshared every name is "missing", and the
        // reason is that - not a rename. Said once, instead of blaming each set.
        if (!anyWorkshared && leftOut.Count > 0)
        {
            ReportHtml.Note(sb,
                "<strong>None of the searched models is workshared,</strong> so there are no worksets "
                + "and nothing was left out - the sets' left-out worksets only apply to workshared "
                + "models.");
        }

        foreach ((string side, SidePartStats s) in leftOut.Where(l => anyWorkshared && l.Stats.Stale.Count > 0))
        {
            ReportHtml.Note(sb,
                $"<strong>{ReportHtml.Escape(side)} - {ReportHtml.Escape(s.Part.SetName)} leaves out "
                + $"{s.Stale.Count} workset(s) no searched model has:</strong> "
                + ReportHtml.Escape(string.Join(", ", s.Stale))
                + " - renamed, or only in a model that is not ticked. Nothing was left out for "
                + "them; audit the set's worksets to fix the names.");
        }

        if (missedPicks.Count > 0)
        {
            ReportHtml.Note(sb,
                $"<strong>{missedPicks.Count} picked value(s) matched nothing</strong> in any "
                + "searched model: "
                + ReportHtml.Escape(string.Join(", ", missedPicks.Select(p => $"[{p.Param}] {p.Value}")))
                + " - renamed, removed, or in a model that is not ticked.");
        }

        if (result.Info.ASkippedNoBoundingBox > 0)
        {
            ReportHtml.Note(sb,
                $"<strong>{result.Info.ASkippedNoBoundingBox} Set A element(s) skipped</strong> - "
                + "no usable bounding box, so they were never tested against Set B.");
        }

        if (pairs.Count == 0)
        {
            if (result.Info.Reason is not null)
            {
                ReportHtml.Note(sb,
                    "<strong>The clash engine stopped early and did not evaluate any pairs:</strong> "
                    + ReportHtml.Escape(result.Info.Reason));
            }
            else
            {
                ReportHtml.Note(sb, "<strong>No clashes found.</strong>");
            }

            if (showReport)
                RibbitUi.ShowReport(DialogTitle, sb.ToString());

            if (outcome is not null)
            {
                outcome.Seconds = clashSeconds;
                outcome.Note = result.Info.Reason ?? "no clashes";
                outcome.Html = sb.ToString();
            }

            return [];
        }

        // Clearance pairs are counted as one bucket rather than one per distinct
        // gap, which would make the summary a list of every measurement.
        Dictionary<string, int> matchCounts = [];
        foreach (ClashPair p in pairs)
        {
            string label = p.IsDuplicate
                ? "Duplicate (same size, same place)"
                : p.IsClearance
                ? "Clearance (within pad, not touching)"
                : ClashStatus.MatchTypeLabel(p.Volume);

            matchCounts[label] = matchCounts.TryGetValue(label, out int n) ? n + 1 : 1;
        }

        sb.Append("<p><strong>Match types:</strong> ")
          .Append(ReportHtml.Escape(string.Join("  |  ",
              matchCounts.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}: {kv.Value}"))))
          .Append("</p>");

        // Abbreviation, matching both link tools. Federated results are dominated
        // by link-side rows, and the CSV column has to mean one thing.
        // Hoisted out of the Build call so the count is available to a batch run,
        // which reports "3 new" per test and has no report window to read it off.
        HashSet<string> newKeys = NewClashes.Compute(
            previousKeys,
            pairs.Select(p => ClashPairKey.For(
                RevitCompat.IdKey(p.A.Id).ToString(), ClashReportRows.LinkDisplayName(p.AInstance),
                RevitCompat.IdKey(p.B.Id).ToString(), ClashReportRows.LinkDisplayName(p.BInstance))));

        List<ClashReportRow> rows = ClashReportRows.Build(
            doc, reader, pairs, SystemColumn.Abbreviation, ledger, newKeys);

        List<int> resolvedStillClashing =
        [
            .. rows.Where(r => ClashStatus.Classify(r.Status) == StatusBucket.Resolved)
                   .Select(r => r.ClashNum)
                   .Distinct()
                   .OrderBy(n => n)
        ];

        if (resolvedStillClashing.Count > 0)
        {
            ReportHtml.Note(sb,
                $"<strong>{resolvedStillClashing.Count} clash pair(s) marked \"Resolved\" but "
                + "still appearing in this run (Clash # "
                + ReportHtml.Escape(string.Join(", ", resolvedStillClashing))
                + ") - the fix may not have held, or it may have come back. Worth a second look "
                + "before trusting that status.</strong>");
        }

        // Notes written in Set Clash Status, shown where the results are read.
        // Read-only here: editing them needs a round trip through the report
        // host, and a note you can SEE beside its clash is most of the value.
        ClashComments comments = ClashComments.Load(
            choice.CsvFolder, modelTitle, Product.ProductTag);

        ClashResultsTable.Append(
            sb, rows, SystemColumn.Abbreviation.Header, StatusEditable(rows), comments.TextFor);
        ClashResultsTable.AppendAcceptedNote(sb, rows);
        ClashResultsTable.AppendStaleTable(
            sb, ledger, rows, ClashSetBuilder.ScopeIdsFor(setA, setB));

        (List<string> fields, List<Dictionary<string, string>> csvRows) =
            ClashReportRows.ToCsv(rows, SystemColumn.Abbreviation);

        try
        {
            string path = StatusLedger.WriteFullStatus(
                choice.CsvFolder, modelTitle, Product.ProductTag, fields, csvRows,
                testName);

            sb.Append("<p><code>").Append(ReportHtml.Escape(path)).Append("</code></p>");

            if (outcome is not null)
                outcome.CsvPath = path;

            ReportHtml.Note(sb,
                "Statuses set above are written to this CSV, which is Federated Clash's own "
                + "ledger - separate from the other three clash tools, so nothing they have "
                + "already triaged is affected.");
        }
        catch (Exception ex)
        {
            ReportHtml.Note(sb, "<strong>CSV export failed:</strong> "
                                + ReportHtml.Escape(ex.Message));

            // A batch never shows this html, so the failure has to travel on the
            // outcome or it reaches the summary as a blank Report cell - which
            // reads as "fine" rather than "your results were not written".
            if (outcome is not null)
                outcome.Note = "CSV export failed: " + ex.Message;
        }

        if (showReport)
        {
            ClashReportActions actions = new(
                csvRows, fields, choice.CsvFolder, modelTitle, Product, ledger, testName);

            // Handed back so that saving this run as a test can rename what the
            // still-open report window writes from here on.
            if (outcome is not null)
                outcome.Actions = actions;

            RibbitUi.ShowReport(DialogTitle, sb.ToString(), actions.Handle);
            RibbitUi.Notify(
                $"{pairs.Count} clashing pair(s) found in {clashSeconds:F1}s.", DialogTitle);
        }

        if (outcome is not null)
        {
            outcome.Clashes = pairs.Count;
            outcome.New = newKeys.Count;
            outcome.Seconds = clashSeconds;
            outcome.Html = sb.ToString();
        }

        return [.. rows.Select(r => r.ClashKey).Distinct(StringComparer.Ordinal)];
    }

    /// <summary>
    /// Only HOST-side rows can carry a status, matching the standalone editors.
    /// A federated run can legitimately produce a table with no host rows at all
    /// — link against link — in which case the pills are correctly absent.
    /// </summary>
    private static bool StatusEditable(IReadOnlyList<ClashReportRow> rows)
        => rows.Any(r => r.LinkName.Length == 0);
}
