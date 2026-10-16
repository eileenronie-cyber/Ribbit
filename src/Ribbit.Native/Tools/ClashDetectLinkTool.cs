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
/// Clash Detect Link. Ported from
/// _Ribbit[CD].tab/Clash Detection Link.panel/.../CDMEPLink.py.
///
/// Host MEP against one or more linked models, BOTH sides matched by keyword —
/// unlike the MEP vs Structure button, where Set B is chosen by category.
///
/// READ-ONLY with respect to the model — this never opens a Transaction.
/// </summary>
internal static class ClashDetectLinkTool
{
    private const string DialogTitle = "Clash Detect Link";

    private static readonly ClashProduct Product = ClashProduct.DetectLink;

    /// <summary>
    /// The pad the tool falls back to when the box holds something unparseable.
    ///
    /// 100 mm, NOT zero — this tool keeps its own default, and the form box is
    /// pre-filled to match. Silently dropping to a zero pad would quietly report
    /// fewer clashes than the user asked for.
    /// </summary>
    private const string DefaultPadText = "100 mm";

    public static void Run(UIDocument uidoc)
    {
        Document doc = uidoc.Document;
        RevitLinks.EnsureInitialized();

        // One reader and one set of caches per RUN. This is the first tool in
        // the extension that touches several documents (host + N links) in a
        // single run, and the Python's module-global caches had to be cleared by
        // hand at the top of main() or a second button click served stale
        // values. Per-run instances make that unrepresentable.
        ClashParameterReader reader = new();

        Dictionary<Document, Dictionary<BuiltInCategory, List<Element>>> bucketCache = [];
        // Keyed by the RULE as well as the document: the Description toggle
        // changes what the picker may offer, so a list scanned under one setting
        // must not be served after it is flipped.
        Dictionary<(Document Doc, bool TrayOnly), List<ValueChoice>> choiceCache = [];

        Dictionary<BuiltInCategory, List<Element>> BucketsFor(Document d)
        {
            if (!bucketCache.TryGetValue(d, out Dictionary<BuiltInCategory, List<Element>>? b))
                bucketCache[d] = b = ClashCategories.CollectByCategoryFast(d);
            return b;
        }

        // Accumulated across documents: Set B's picker can scan several links,
        // and all of that is scan cost. See ClashResultsTable.AppendTiming for
        // why it is reported apart from the clash itself.
        double scanSeconds = 0.0;

        List<ValueChoice> ChoicesFor(Document d, bool trayOnly, string title)
        {
            if (choiceCache.TryGetValue((d, trayOnly), out List<ValueChoice>? cached))
                return cached;

            Stopwatch sw = Stopwatch.StartNew();
            List<ValueChoice> res;

            using (ProgressDialog pb = new(title + " - {0} of {1}", cancellable: false))
            {
                res = ClashLinkSetBuilder.CollectDistinctValuesForDoc(
                    reader, BucketsFor(d), trayOnly, (i, n) => pb.Update(i, n));
            }

            scanSeconds += sw.Elapsed.TotalSeconds;
            choiceCache[(d, trayOnly)] = res;
            return res;
        }

        List<RevitLinkInstance> links = ClashLinks.GetLinkInstances(doc);
        if (links.Count == 0)
        {
            RibbitUi.Alert(
                "No Revit Links found in this model - Set B is searched inside linked models.",
                DialogTitle);
            return;
        }

        ClashSettings settings = ClashSettings.Load(Product.ConfigSection);

        // Shared by all three clash tools, unlike the per-product settings above.
        ClashSetLibrary library = ClashSetLibrary.Load();

        ClashLinkSearchWindow window = new(
            links,
            settings.CsvFolder,
            settings.SetAKeyword,
            settings.SetBKeyword,
            ClashSettings.PairsOf(settings.SetAValues),
            ClashSettings.PairsOf(settings.SetBValues),
            settings.PadValue,
            settings.PadUnit,
            settings.DescriptionCableTrayOnly,
            library,
            library.ResolveIds(settings.SetAIds, settings.SetANames),
            library.ResolveIds(settings.SetBIds, settings.SetBNames),
            trayOnly => ChoicesFor(doc, trayOnly, "Scanning host model"),
            (linkDocs, trayOnly) => ClashLinkSetBuilder.MergeChoices(
                linkDocs.Select(d => ChoicesFor(d, trayOnly, "Scanning link(s)"))));

        if (window.ShowDialog() != true || window.Result is null)
            return;

        ClashLinkSearchChoice choice = window.Result;

        settings.CsvFolder = choice.CsvFolder;
        settings.SetAKeyword = choice.SetAKeyword;
        settings.SetBKeyword = choice.SetBKeyword;
        settings.SetAValues = ClashSettings.ToStored(choice.SetAValues);
        settings.SetBValues = ClashSettings.ToStored(choice.SetBValues);
        settings.PadValue = choice.PadValue;
        settings.PadUnit = choice.PadUnit;
        settings.DescriptionCableTrayOnly = choice.DescriptionCableTrayOnly;
        settings.SetAIds = choice.SetAIds;
        settings.SetBIds = choice.SetBIds;
        settings.Save(Product.ConfigSection);

        try
        {
            RunSearch(doc, reader, BucketsFor, choice, library, scanSeconds);
        }
        catch (Exception ex)
        {
            RibbitUi.Alert($"Clash Detect Link failed partway through:\n\n{ex.Message}", DialogTitle);
        }
    }

    private static void RunSearch(
        Document doc,
        ClashParameterReader reader,
        Func<Document, Dictionary<BuiltInCategory, List<Element>>> bucketsFor,
        ClashLinkSearchChoice choice,
        ClashSetLibrary library,
        double scanSeconds)
    {
        string modelTitle = doc.Title;

        ClashLedger ledger = StatusLedger.LoadLedger(
            choice.CsvFolder, modelTitle, Product.ProductTag);

        // Started BEFORE set building: this tool collects categories out of
        // every checked link and reads a parameter off each element, which is a
        // real part of what a run costs and is not the narrow phase.
        Stopwatch watch = Stopwatch.StartNew();

        // hit_pairs is shared across BOTH sets: a value picked for Set A that
        // only exists in a link still counts as hit, so it is not reported as
        // matching nothing.
        HashSet<(string Param, string Value)> hitPairs = [];

        List<Element> setA;
        (List<Element> Elements, List<string> Diagnostics) setBResult;

        // Both set-building phases share one dialog. Neither is instant on a
        // large model, and until this ran there was no feedback at all between
        // clicking Run and the first result - the narrow-phase bar below does
        // not appear until both sets already exist.
        using (ProgressDialog pb = new("Searching link {0} of {1}...", cancellable: false))
        {
            pb.Message("Building Set A from the host model...");

            setA = ClashLinkSetBuilder.BuildSet(
                reader, bucketsFor(doc), choice.SetAKeyword,
                choice.DescriptionCableTrayOnly, choice.SetAForceBics,
                choice.SetAValues, hitPairs);

            setBResult = ClashLinkSetBuilder.BuildSetBLinks(
                reader, choice.SelectedLinks, choice.SetBKeyword,
                choice.DescriptionCableTrayOnly, choice.ForceBics,
                bucketsFor, choice.SetBValues, hitPairs,
                (i, n, name) =>
                {
                    // 1-based, and reported BEFORE that link's own work, so the
                    // label names the link currently being searched.
                    pb.Update(i + 1, n);
                    pb.Message($"Searching link {i + 1} of {n}: \"{name}\"...");
                });
        }

        List<Element> setB = setBResult.Elements;

        StringBuilder sb = new();

        // A picked value matching nothing is worth surfacing: the form restores
        // selections from a previous run without re-validating them, so a value
        // renamed or absent from the currently-checked links would otherwise
        // quietly contribute zero elements. Deduped, so a value picked for both
        // sets is not listed twice.
        List<(string Param, string Value)> missed =
        [
            .. choice.SetAValues.Concat(choice.SetBValues)
                     .Distinct()
                     .Where(p => !hitPairs.Contains(p))
        ];

        if (missed.Count > 0)
        {
            ReportHtml.Note(sb,
                $"<strong>{missed.Count} selected value(s) matched no elements:</strong> "
                + ReportHtml.Escape(string.Join(", ", missed.Select(m => $"[{m.Param}] {m.Value}")))
                + " - these may have been renamed, or are not in the checked link(s)/host.");
        }

        sb.Append("<h2>Set B sources</h2><ul>");
        foreach (string line in setBResult.Diagnostics)
            sb.Append("<li>").Append(ReportHtml.Escape(line)).Append("</li>");
        sb.Append("</ul>");

        if (setA.Count == 0 || setB.Count == 0)
        {
            ReportHtml.Note(sb,
                $"<strong>Set A:</strong> {setA.Count} elements &nbsp;|&nbsp; "
                + $"<strong>Set B:</strong> {setB.Count} elements &nbsp;|&nbsp; "
                + "<strong>No clashes found.</strong>"
                + (setB.Count == 0
                    ? " See \"Set B sources\" above for why - a common cause is a checked link "
                      + "that is currently Unloaded, or a keyword that exists in the host but "
                      + "not inside the link."
                    : ""));

            // The stale-status table is deliberately NOT shown here: an empty
            // set means no real search ran, so "not in current results" is
            // trivially true for the whole historical ledger and would read as a
            // resolved-items report when it is really "the form wasn't filled in".
            RibbitUi.ShowReport(DialogTitle, sb.ToString());
            return;
        }

        double padFt = UnitParse.ToFeet(ClashSearchNaming.NormalizePadText(choice.PadText))
                       ?? UnitParse.ToFeet(DefaultPadText) ?? 0.0;
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
        // by Set A id (then Set B id) puts them in one contiguous block - a
        // display-order change only; it renumbers Clash # but does not affect
        // which pairs were found.
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
        sb.Append($"<li><strong>Set B:</strong> {setB.Count} elements from "
                  + $"{choice.SelectedLinks.Count} link(s)</li>");
        sb.Append($"<li><strong>Clashing pairs:</strong> {pairs.Count}</li>");
        ClashResultsTable.AppendTiming(sb, scanSeconds, clashSeconds, result.Info.CellSizeFt);
        ClashResultsTable.AppendSets(
            sb, library.NamesFor(choice.SetAIds), library.NamesFor(choice.SetBIds));
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
            // unless checked - otherwise an aborted run misreports as "no
            // clashes found".
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
            string tag = ClashSearchNaming.LinkReportTag(
                choice.SetAKeyword, choice.SetAValues,
                choice.SetBKeyword, choice.SetBValues,
                choice.SelectedLinks.Count);

            string path = StatusLedger.WriteFullStatus(
                choice.CsvFolder, modelTitle, Product.ProductTag, fields, csvRows);

            sb.Append("<p><code>").Append(ReportHtml.Escape(path)).Append("</code>")
              .Append(" &nbsp;<em>(search: ").Append(ReportHtml.Escape(tag)).Append(")</em></p>");

            ReportHtml.Note(sb,
                "Statuses set above are written to this same CSV. <strong>Set Status Link</strong> "
                + "on the ribbon reads it too - use that to come back to these results later, "
                + "once this window is closed.");
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
    /// Whether the results table should offer status pills at all. Only
    /// HOST-side rows can be flagged, matching the standalone editor — here that
    /// is Set A, since Set B lives inside the links.
    /// </summary>
    private static bool StatusEditable(IReadOnlyList<ClashReportRow> rows)
        => rows.Any(r => r.LinkName.Length == 0);

}
