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
/// Clash Detect. Ported from
/// _Ribbit[CD].tab/Clash Detection.panel/ClashDetection.pushbutton/CDMEP.py.
///
/// Host model against ITSELF — both sets are keyword searches over the same
/// document, and no Revit Link is involved anywhere. That single fact is what
/// makes this tool different from its two siblings: drawing both sets from one
/// pool means the raw engine output is full of pairs that are not coordination
/// problems at all, so <see cref="ClashNoiseFilter"/> runs over the results.
///
/// READ-ONLY with respect to the model — this never opens a Transaction.
/// </summary>
internal static class ClashDetectTool
{
    private const string DialogTitle = "Clash Detect";

    private static readonly ClashProduct Product = ClashProduct.Cdmep;

    /// <summary>The fallback pad when the box holds something unparseable.</summary>
    private const string DefaultPadText = "100 mm";

    public static void Run(UIDocument uidoc)
    {
        Document doc = uidoc.Document;
        RevitLinks.EnsureInitialized();

        // One reader per run - the Python's module-global caches had to be
        // cleared by hand at the top of main() or a second click served stale
        // values from an edited model.
        ClashParameterReader reader = new();

        Dictionary<BuiltInCategory, List<Element>>? buckets = null;
        Dictionary<BuiltInCategory, List<Element>> Buckets() =>
            buckets ??= ClashCategories.CollectByCategoryFast(doc);

        // Scanned lazily and once. If Browse is used the scan warms the reader's
        // caches and the set building that follows is nearly free off the back
        // of it; if it is never used, nothing is collected until Run.
        // Cached per RULE, not just once: the Description toggle changes what the
        // picker may offer, so flipping it has to rescan rather than serve a list
        // gathered under the old setting.
        Dictionary<bool, List<ValueChoice>> scanned = [];
        double scanSeconds = 0.0;

        List<ValueChoice> ScanHost(bool trayOnly)
        {
            if (scanned.TryGetValue(trayOnly, out List<ValueChoice>? cached))
                return cached;

            Stopwatch sw = Stopwatch.StartNew();
            List<ValueChoice> res;

            using (ProgressDialog pb = new("Scanning model - {0} of {1}", cancellable: false))
            {
                res = ClashLinkSetBuilder.CollectDistinctValuesForDoc(
                    reader, Buckets(), trayOnly, (i, n) => pb.Update(i, n));
            }

            scanSeconds = sw.Elapsed.TotalSeconds;
            scanned[trayOnly] = res;
            return res;
        }

        ClashSettings settings = ClashSettings.Load(Product.ConfigSection);

        // Shared by all three clash tools, unlike the per-product settings above.
        ClashSetLibrary library = ClashSetLibrary.Load();

        ClashDetectWindow window = new(
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
            ScanHost);

        if (window.ShowDialog() != true || window.Result is null)
            return;

        ClashDetectChoice choice = window.Result;

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
            RunSearch(doc, reader, Buckets, choice, library, scanSeconds);
        }
        catch (Exception ex)
        {
            RibbitUi.Alert($"Clash Detect failed partway through:\n\n{ex.Message}", DialogTitle);
        }
    }

    private static void RunSearch(
        Document doc,
        ClashParameterReader reader,
        Func<Dictionary<BuiltInCategory, List<Element>>> bucketsFor,
        ClashDetectChoice choice,
        ClashSetLibrary library,
        double scanSeconds)
    {
        string modelTitle = doc.Title;

        ClashLedger ledger = StatusLedger.LoadLedger(
            choice.CsvFolder, modelTitle, Product.ProductTag);

        Stopwatch watch = Stopwatch.StartNew();

        HashSet<(string Param, string Value)> hitPairs = [];
        List<Element> setA;
        List<Element> setB;

        using (ProgressDialog pb = new("Building sets - {0} of {1}", cancellable: false))
        {
            Dictionary<BuiltInCategory, List<Element>> buckets = bucketsFor();

            pb.Message("Building Set A...");
            setA = ClashLinkSetBuilder.BuildSet(
                reader, buckets, choice.SetAKeyword, choice.DescriptionCableTrayOnly,
                choice.SetAForceBics, choice.SetAValues, hitPairs);

            // Each side gets its OWN categories now, from the sets chosen for it.
            // They used to feed Set B alone, which was a limit of the old
            // force-include control rather than anything the search required.
            pb.Message("Building Set B...");
            setB = ClashLinkSetBuilder.BuildSet(
                reader, buckets, choice.SetBKeyword, choice.DescriptionCableTrayOnly,
                choice.ForceBics, choice.SetBValues, hitPairs);
        }

        StringBuilder sb = new();

        // A picked value matching nothing is worth surfacing: the form restores
        // selections from a previous run without re-validating them, so a value
        // renamed or removed since would otherwise quietly contribute zero
        // elements. Deduped, so a value picked for both sets is listed once.
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
                + " - these may have been renamed or removed since they were picked.");
        }

        if (setA.Count == 0 || setB.Count == 0)
        {
            ReportHtml.Note(sb,
                $"<strong>Set A:</strong> {setA.Count} elements &nbsp;|&nbsp; "
                + $"<strong>Set B:</strong> {setB.Count} elements &nbsp;|&nbsp; "
                + "<strong>No clashes found.</strong>");

            // The stale-status table is deliberately NOT shown here: an empty
            // set means no real search ran, so "not in current results" would be
            // trivially true for the entire historical ledger.
            RibbitUi.ShowReport(DialogTitle, sb.ToString());
            return;
        }

        double padFt = UnitParse.ToFeet(ClashSearchNaming.NormalizePadText(choice.PadText))
                       ?? UnitParse.ToFeet(DefaultPadText) ?? 0.0;
        if (padFt < 0.0)
            padFt = 0.0;

        View? activeView = ClashGeometry.GetSafeActiveView(doc);

        ClashResult result;

        // An EMPTY link map, deliberately: every element on both sides is a host
        // element, so there is no transform to apply to anything.
        using (ProgressDialog pb = new("Checking clashes - {0} of {1}", cancellable: false))
        {
            result = ClashEngine.Run(
                doc, activeView, setA, setB, padFt,
                new Dictionary<Document, List<(RevitLinkInstance, Transform)>>(),
                (i, n) => pb.Update(i, n));
        }

        int rawPairCount = result.Pairs.Count;

        // Both sets come from one document, so the engine's output contains
        // pairs that are expected geometry rather than coordination problems,
        // plus every real pair twice with the sides swapped. Neither can be
        // handled inside the shared engine - see ClashNoiseFilter.
        List<ClashPair> pairs = new ClashNoiseFilter().Apply(result.Pairs);
        int suppressed = rawPairCount - pairs.Count;

        // Display order only: the engine's order follows its Set B outer loop,
        // which scatters one element's several clashes through the table.
        pairs.Sort((x, y) =>
        {
            int byA = RevitCompat.IdKey(x.A.Id).CompareTo(RevitCompat.IdKey(y.A.Id));
            return byA != 0
                ? byA
                : RevitCompat.IdKey(x.B.Id).CompareTo(RevitCompat.IdKey(y.B.Id));
        });

        watch.Stop();
        double clashSeconds = watch.Elapsed.TotalSeconds;

        sb.Append("<ul>");
        sb.Append($"<li><strong>Set A:</strong> {setA.Count} elements</li>");
        sb.Append($"<li><strong>Set B:</strong> {setB.Count} elements</li>");
        sb.Append($"<li><strong>Clashing pairs:</strong> {pairs.Count}</li>");
        ClashResultsTable.AppendTiming(sb, scanSeconds, clashSeconds, result.Info.CellSizeFt);
        ClashResultsTable.AppendSets(
            sb, library.NamesFor(choice.SetAIds), library.NamesFor(choice.SetBIds));
        ClashResultsTable.AppendMatchRule(sb, choice.DescriptionCableTrayOnly);
        sb.Append("</ul>");

        if (suppressed > 0)
        {
            ReportHtml.Note(sb,
                $"{suppressed} raw pair(s) suppressed - an element against itself, against its "
                + "own insulation, against something it is directly joined to, or the same "
                + "clash already listed with the two sides swapped. These overlap by design "
                + "and are not coordination problems.");
        }

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
            // than finishing and finding nothing - otherwise an aborted run
            // misreports as "no clashes found".
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

        // Per-PAIR match types, not per-row: each pair contributes two rows.
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

        // System TYPE here, not System Abbreviation - this tool's report and CSV
        // have always carried that column, and it is read through the same
        // FindParameter the matching used.
        List<ClashReportRow> rows = ClashReportRows.Build(
            doc, reader, pairs, SystemColumn.Type, ledger);

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

        ClashResultsTable.Append(sb, rows, SystemColumn.Type.Header, StatusEditable(rows));

        ClashResultsTable.AppendAcceptedNote(sb, rows);
        ClashResultsTable.AppendStaleTable(
            sb, ledger, rows, ClashSetBuilder.ScopeIdsFor(setA, setB));

        (List<string> fields, List<Dictionary<string, string>> csvRows) =
            ClashReportRows.ToCsv(rows, SystemColumn.Type);

        try
        {
            string tag = ClashSearchNaming.ReportTag(
                choice.SetAKeyword,
                [ClashSearchNaming.SetALabel(choice.SetBKeyword, choice.SetBValues)],
                choice.SetAValues);

            string path = StatusLedger.WriteFullStatus(
                choice.CsvFolder, modelTitle, Product.ProductTag, fields, csvRows);

            sb.Append("<p><code>").Append(ReportHtml.Escape(path)).Append("</code>")
              .Append(" &nbsp;<em>(search: ").Append(ReportHtml.Escape(tag)).Append(")</em></p>");

            ReportHtml.Note(sb,
                "Statuses set above are written to this same CSV. <strong>Set Clash Status</strong> "
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
    /// Whether the results table should offer status pills at all. Host-vs-host,
    /// so both sides are host elements and every row is flaggable — the check
    /// stays for symmetry with the two link tools.
    /// </summary>
    private static bool StatusEditable(IReadOnlyList<ClashReportRow> rows)
        => rows.Any(r => r.LinkName.Length == 0);

}
