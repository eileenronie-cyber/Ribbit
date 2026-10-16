using System.Diagnostics;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Ribbit.Geometry.Clash;
using Ribbit.Native.Revit;
using Ribbit.Native.Settings;
using Ribbit.Native.Ui;

namespace Ribbit.Native.Tools;

/// <summary>
/// Clash Detection — MEP vs Structure. Ported from
/// _Ribbit[CD].tab/MEP vs Structure.panel/.../CDMEPvsLink.py.
///
/// Set A is host MEP matched by keyword or exact picked value; Set B is
/// categories inside one or more Revit Links. Results go to a report window and
/// a CSV, and the CSV doubles as the status ledger the Set Clash Status button
/// reads.
///
/// READ-ONLY with respect to the model — this never opens a Transaction.
/// </summary>
internal static class ClashMepVsStructureTool
{
    private const string DialogTitle = "Clash Detection - MEP vs Structure";

    private static readonly ClashProduct Product = ClashProduct.MepVsStructure;

    public static void Run(UIDocument uidoc)
    {
        Document doc = uidoc.Document;
        RevitLinks.EnsureInitialized();

        // One reader per run. The Python needed an explicit clear_caches() here
        // because its caches were module globals that persisted across button
        // clicks - a model edited between two runs would be served stale values.
        ClashParameterReader reader = new();

        Dictionary<BuiltInCategory, List<Element>>? buckets = null;
        Dictionary<BuiltInCategory, List<Element>> Buckets() =>
            buckets ??= ClashCategories.CollectByCategoryFast(doc);

        List<RevitLinkInstance> links = ClashLinks.GetLinkInstances(doc);
        if (links.Count == 0)
        {
            RibbitUi.Alert(
                "No Revit Links found in this model - Set B is chosen from linked models.",
                DialogTitle);
            return;
        }

        List<Category> categories = ClashCategories.AllModelCategories(doc);
        ClashSettings settings = ClashSettings.Load(Product.ConfigSection);
        ClashSetLibrary library = ClashSetLibrary.Load();

        // Timed so the report can separate scan cost from clash cost - see
        // ClashResultsTable.AppendTiming for why that split matters.
        // Cached per RULE, not just once: the Description toggle changes what the
        // picker may offer, so flipping it has to rescan rather than serve a list
        // gathered under the old setting.
        Dictionary<bool, List<ValueChoice>> scanned = [];
        double scanSeconds = 0.0;

        List<ValueChoice> ScanValues(bool trayOnly)
        {
            if (scanned.TryGetValue(trayOnly, out List<ValueChoice>? cached))
                return cached;

            Stopwatch sw = Stopwatch.StartNew();
            List<ValueChoice> res;

            using (ProgressDialog pb = new("Scanning host model - {0} of {1}", cancellable: false))
            {
                res = ClashSetBuilder.CollectDistinctValues(
                    reader, Buckets(), trayOnly, (i, n) => pb.Update(i, n));
            }

            scanSeconds = sw.Elapsed.TotalSeconds;
            scanned[trayOnly] = res;
            return res;
        }

        ClashSearchWindow window = new(
            links,
            categories,
            settings.CsvFolder,
            settings.SetAKeyword,
            ClashSettings.PairsOf(settings.SetAValues),
            settings.PadValue,
            settings.PadUnit,
            settings.DescriptionCableTrayOnly,
            library,
            library.ResolveIds(settings.SetAIds, settings.SetANames),
            ScanValues);

        if (window.ShowDialog() != true || window.Result is null)
            return;

        ClashSearchChoice choice = window.Result;

        // Set B is (link, category) pairs, which cannot be persisted: a
        // RevitLinkInstance belongs to one document. Set A and the pad can.
        settings.CsvFolder = choice.CsvFolder;
        settings.SetAKeyword = choice.Keyword;
        settings.SetAValues = ClashSettings.ToStored(choice.PickedValues);
        settings.PadValue = choice.PadValue;
        settings.PadUnit = choice.PadUnit;
        settings.DescriptionCableTrayOnly = choice.DescriptionCableTrayOnly;
        settings.SetAIds = choice.SetAIds;
        settings.Save(Product.ConfigSection);

        try
        {
            RunSearch(doc, reader, Buckets(), choice, library, scanSeconds);
        }
        catch (Exception ex)
        {
            RibbitUi.Alert(
                $"Clash Detection failed partway through:\n\n{ex.Message}", DialogTitle);
        }
    }

    private static void RunSearch(
        Document doc,
        ClashParameterReader reader,
        IReadOnlyDictionary<BuiltInCategory, List<Element>> buckets,
        ClashSearchChoice choice,
        ClashSetLibrary library,
        double scanSeconds)
    {
        string modelTitle = doc.Title;

        ClashLedger ledger = StatusLedger.LoadLedger(
            choice.CsvFolder, modelTitle, Product.ProductTag);

        // Started BEFORE set building, not just around the engine: building the
        // sets reads a parameter off every element in the model and is a real
        // part of what a run costs.
        Stopwatch watch = Stopwatch.StartNew();

        HashSet<(string Param, string Value)> hitPairs = [];
        List<Element> setA = ClashSetBuilder.BuildSetA(
            reader, buckets, choice.Keyword, choice.DescriptionCableTrayOnly,
            choice.PickedValues, hitPairs, choice.SetAForceBics);

        (List<Element> setB, List<string> setBDiagnostics) =
            ClashSetBuilder.BuildSetB(choice.LinkCategories);

        // A picked value matching nothing - renamed or removed since it was
        // picked - is surfaced rather than silently shrinking Set A.
        List<(string Param, string Value)> missed =
            [.. choice.PickedValues.Where(p => !hitPairs.Contains(p)).Distinct()];

        StringBuilder sb = new();

        if (missed.Count > 0)
        {
            ReportHtml.Note(sb,
                $"<strong>{missed.Count} selected Set A value(s) matched no elements:</strong> "
                + ReportHtml.Escape(string.Join(", ", missed.Select(m => $"[{m.Param}] {m.Value}")))
                + " - these may have been renamed or removed since they were picked.");
        }

        sb.Append("<h2>Set B sources</h2><ul>");
        foreach (string line in setBDiagnostics)
            sb.Append("<li>").Append(ReportHtml.Escape(line)).Append("</li>");
        sb.Append("</ul>");

        if (setA.Count == 0 || setB.Count == 0)
        {
            ReportHtml.Note(sb,
                $"<strong>Set A:</strong> {setA.Count} elements &nbsp;|&nbsp; "
                + $"<strong>Set B:</strong> {setB.Count} elements &nbsp;|&nbsp; "
                + "<strong>No clashes found.</strong>"
                + (setB.Count == 0
                    ? " See \"Set B sources\" above for why - a common cause is a link that is "
                      + "present but currently Unloaded."
                    : ""));

            // The stale-status table is deliberately NOT shown here. An empty
            // set means no real search ran, so "not in current results" is
            // trivially true for the entire historical ledger - dumping it reads
            // as a resolved-items report when it is really "the form wasn't
            // filled in".
            RibbitUi.ShowReport(DialogTitle, sb.ToString());
            return;
        }

        double padFt = UnitParse.ToFeet(ClashSearchNaming.NormalizePadText(choice.PadText)) ?? 0.0;
        if (padFt < 0.0)
            padFt = 0.0;

        Dictionary<Document, List<(RevitLinkInstance Instance, Transform Tr)>> linkMap =
            ClashLinks.BuildTransformMap(doc);

        View? activeView = ClashGeometry.GetSafeActiveView(doc);

        ClashResult result;

        using (ProgressDialog pb = new("Checking clashes - {0} of {1}", cancellable: false))
        {
            result = ClashEngine.Run(
                doc, activeView, setA, setB, padFt, linkMap, (i, n) => pb.Update(i, n));
        }

        // The engine's pair order follows its Set B outer loop, so one host
        // element's several clashes land scattered through the table. Grouping
        // by Set A id (then Set B id) puts every clash for a given element in one
        // contiguous block - a display-order change only; it renumbers Clash #
        // but does not affect which pairs were found.
        List<ClashPair> pairs =
        [
            .. result.Pairs
                .OrderBy(p => RevitCompat.IdKey(p.A.Id))
                .ThenBy(p => RevitCompat.IdKey(p.B.Id))
        ];

        watch.Stop();
        double clashSeconds = watch.Elapsed.TotalSeconds;

        sb.Append("<ul>");
        sb.Append($"<li><strong>Set A:</strong> {setA.Count} elements</li>");
        sb.Append($"<li><strong>Set B:</strong> {setB.Count} elements</li>");
        sb.Append($"<li><strong>Clashing pairs:</strong> {pairs.Count}</li>");
        ClashResultsTable.AppendTiming(sb, scanSeconds, clashSeconds, result.Info.CellSizeFt);
        ClashResultsTable.AppendSets(sb, library.NamesFor(choice.SetAIds), []);
        ClashResultsTable.AppendMatchRule(sb, choice.DescriptionCableTrayOnly);
        sb.Append("</ul>");

        // A per-element skip is a real gap in coverage, so it is surfaced rather
        // than staying silent behind the engine's fail-open behaviour.
        if (result.Info.ASkippedNoBoundingBox > 0)
        {
            ReportHtml.Note(sb,
                $"<strong>{result.Info.ASkippedNoBoundingBox} Set A element(s) skipped</strong> - "
                + "no usable bounding box, so they were never tested against Set B.");
        }

        if (pairs.Count == 0)
        {
            // A reason is only present when the engine bailed out early rather
            // than finishing and finding nothing. The two are indistinguishable
            // unless checked - otherwise "No clashes found" could mean "nothing
            // was actually checked".
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

            RibbitUi.ShowReport(DialogTitle, sb.ToString());
            return;
        }

        // Per-PAIR match types, not per-row: each pair contributes two rows, so
        // counting rows would double every number.
        Dictionary<string, int> matchCounts = [];
        foreach (ClashPair p in pairs)
        {
            string label = ClashStatus.MatchTypeLabel(p.Volume);
            matchCounts[label] = matchCounts.TryGetValue(label, out int n) ? n + 1 : 1;
        }

        sb.Append("<p><strong>Match types:</strong> ")
          .Append(ReportHtml.Escape(string.Join("  |  ",
              matchCounts.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}: {kv.Value}"))))
          .Append("</p>");

        List<ClashReportRow> rows = ClashReportRows.Build(
            doc, reader, pairs, SystemColumn.Abbreviation, ledger);

        // A row marked Resolved that is STILL clashing is a contradiction worth
        // surfacing on its own, rather than blending into the table behind a
        // reassuring green pill.
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
                + ") - the fix may not have held, it may have come back, or this element may be "
                + "carrying a \"Resolved\" flag set for a different pair. Worth a second look "
                + "before trusting that status.</strong>");
        }

        ClashResultsTable.Append(sb, rows, SystemColumn.Abbreviation.Header, StatusEditable(rows));

        ClashResultsTable.AppendAcceptedNote(sb, rows);
        ClashResultsTable.AppendStaleTable(
            sb, ledger, rows, ClashSetBuilder.ScopeIdsFor(setA, setB));

        (List<string> fields, List<Dictionary<string, string>> csvRows) =
            ClashReportRows.ToCsv(rows, SystemColumn.Abbreviation);

        // Silent CSV export - failure is reported inline in the report rather
        // than via a popup, so it never interrupts either way.
        try
        {
            IEnumerable<string> categoryNames = choice.LinkCategories.Values
                .SelectMany(v => v)
                .Select(c => c.Name)
                .Distinct()
                .OrderBy(n => n);

            string tag = ClashSearchNaming.ReportTag(
                choice.Keyword, categoryNames, choice.PickedValues);

            string path = StatusLedger.WriteFullStatus(
                choice.CsvFolder, modelTitle, Product.ProductTag, fields, csvRows);

            // The filename tag is descriptive; WriteFullStatus owns the counter
            // and the model/product prefix the ledger matches on.
            sb.Append("<p><code>").Append(ReportHtml.Escape(path)).Append("</code>")
              .Append(" &nbsp;<em>(search: ").Append(ReportHtml.Escape(tag)).Append(")</em></p>");
        }
        catch (Exception ex)
        {
            ReportHtml.Note(sb, "<strong>CSV export failed:</strong> "
                                + ReportHtml.Escape(ex.Message));
        }

        ClashReportActions actions = new(
            csvRows, fields, choice.CsvFolder, modelTitle, Product, ledger);

        RibbitUi.ShowReport(DialogTitle, sb.ToString(), actions.Handle);
        RibbitUi.Notify(
            $"{pairs.Count} clashing pair(s) found in {clashSeconds:F1}s.", DialogTitle);
    }

    /// <summary>
    /// Whether the results table should offer status pills at all.
    ///
    /// Only HOST-side rows can be flagged — the same rule the standalone editor
    /// uses. A search whose results are entirely link-side gets a read-only
    /// table rather than a save bar that could never write anything.
    /// </summary>
    private static bool StatusEditable(IReadOnlyList<ClashReportRow> rows)
        => rows.Any(r => r.LinkName.Length == 0);

}
