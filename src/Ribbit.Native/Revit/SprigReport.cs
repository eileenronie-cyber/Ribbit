using System.Text;
using Autodesk.Revit.DB;
using Ribbit.Geometry;
using Ribbit.Native.Settings;
using Ribbit.Native.Ui;
// Autodesk.Revit.DB also has a Units type; ours wins inside this file.
using Units = Ribbit.Geometry.Units;

namespace Ribbit.Native.Revit;

/// <summary>
/// Sprig Connect's dry-run report. Ported from the reporting half of
/// lib/spriglib/engine.py.
///
/// Returns an HTML body string rather than printing as it goes — the caller
/// hands it to <see cref="RibbitUi.ShowReport"/>. Keeping the report as a
/// returned string (rather than a module-level `output` global) is what lets it
/// be built without a live output window.
/// </summary>
internal static class SprigReport
{
    /// <summary>Cell that selects the element when clicked, falling back to a bare number.</summary>
    private static string Link(long idInt, IReadOnlyDictionary<long, ElementId> idMap)
        => idMap.TryGetValue(idInt, out ElementId? eid)
            ? ReportHtml.Link(idInt.ToString(), eid)
            : ReportHtml.Escape(idInt.ToString());

    private static string Mm(double? valueFt) => ReportHtml.Escape(Units.Mm(valueFt));

    /// <summary>
    /// Pitch as both degrees and mm per metre — the second is the one that tells
    /// you at a glance whether the DIRECT-sprig lean matters (drainage pitch is
    /// ~2 mm/m and leans by ~0.12 deg).
    /// </summary>
    /// <summary>
    /// Size plus WHERE IT CAME FROM — see the note on DropReport's version. The
    /// sprig and armover are sized from the head's own inlet connector, so a
    /// surprising number is nearly always the fixture's real connector.
    /// </summary>
    private static string SizeCell(SprigEngine.HeadInfo? info)
    {
        if (info is null)
            return "-";

        string source = info.DiameterSource.Contains("fallback") ? "fallback" : "head";
        return ReportHtml.Escape($"{Units.Mm(info.DiameterFt)} ({source})");
    }

    private static string FmtPitch(double slopeDeg)
    {
        double mmPerM = Math.Tan(Math.Abs(slopeDeg) * Math.PI / 180.0) * 1000.0;
        return $"{Math.Abs(slopeDeg):F3} deg ({mmPerM:F1} mm per m)";
    }

    private static void Table(
        StringBuilder sb, string title, IReadOnlyList<string> columns,
        IReadOnlyList<IReadOnlyList<string>> rows)
        => ReportHtml.Table(sb, title, columns, rows);

    private static void Note(StringBuilder sb, string html) => ReportHtml.Note(sb, html);

    public static string Build(
        IReadOnlyList<HeadPlan> plans,
        IReadOnlyList<MainSegment> segments,
        IReadOnlyList<SprigEngine.Rejected> mainRejected,
        IReadOnlyList<SprigEngine.Rejected> headRejected,
        IReadOnlyDictionary<long, SprigEngine.HeadInfo> headInfo,
        SprigSettings settingsIn,
        IReadOnlyDictionary<long, ElementId> idMap)
    {
        StringBuilder sb = new();

        Note(sb, "<strong>Nothing has been modified yet.</strong> This is what the builder "
                 + "intends to do; you will be asked to confirm before anything is created, and "
                 + "cancelling there leaves the model untouched. Check the stations, the "
                 + "DIRECT/ARMOVER split and the tee angles below.");

        MainTable(sb, segments, idMap);
        Rejects(sb, mainRejected, headRejected, idMap);
        HeadTable(sb, plans, headInfo, idMap);
        MainMismatchHint(sb, plans);
        SettingsTable(sb, settingsIn);
        Summary(sb, plans, segments);

        return sb.ToString();
    }

    private static void MainTable(
        StringBuilder sb, IReadOnlyList<MainSegment> segments,
        IReadOnlyDictionary<long, ElementId> idMap)
    {
        if (segments.Count == 0)
        {
            sb.Append("<h2>Branch line</h2>");
            Note(sb, "<strong>No usable branch-line segments.</strong> See the dropped picks "
                     + "below - the reason names the element's class and category.");
            return;
        }

        List<IReadOnlyList<string>> rows = [];
        for (int i = 0; i < segments.Count; i++)
        {
            MainSegment seg = segments[i];
            rows.Add(new[]
            {
                (i + 1).ToString(),
                Link(seg.ElemId, idMap),
                ReportHtml.Escape(seg.Desc),
                Mm(seg.Length),
                ReportHtml.Escape(FmtPitch(seg.SlopeDeg)),
                Mm(seg.P0.Z),
                Mm(seg.P1.Z),
            });
        }

        Table(sb, "Branch-line segments (in pick order)",
            ["#", "Element", "What it is", "Length", "Pitch", "Start Z", "End Z"], rows);

        int nonPipe = segments.Count(s => !s.IsPipe);
        if (nonPipe > 0)
        {
            Note(sb, $"{nonPipe} main segment(s) are not Revit <code>Pipe</code> elements. The "
                     + "geometry above is still correct, but the builder relies on Pipe-only APIs "
                     + "(<code>PlumbingUtils.BreakCurve</code>, <code>Pipe.Create</code>) and will "
                     + "need a different approach for these.");
        }

        if (segments.Any(s => Math.Abs(s.SlopeDeg) > 1.0))
        {
            Note(sb, "The branch is pitched more than 1 deg. Sprigs are built PLUMB (tee'd "
                     + "directly under each head), so the tee sits off square by the pitch rather "
                     + "than the sprig leaning. An on-axis head's sprig reads lean 0; a head off "
                     + "the centreline leans only by its own sideways offset - see the Lean column.");
        }
    }

    private static void Rejects(
        StringBuilder sb,
        IReadOnlyList<SprigEngine.Rejected> mainRejected,
        IReadOnlyList<SprigEngine.Rejected> headRejected,
        IReadOnlyDictionary<long, ElementId> idMap)
    {
        if (mainRejected.Count == 0 && headRejected.Count == 0)
            return;

        List<IReadOnlyList<string>> rows = [];
        foreach (SprigEngine.Rejected r in mainRejected)
            rows.Add(new[] { "Main", Link(RevitCompat.IdKey(r.Id), idMap), ReportHtml.Escape(r.Why) });
        foreach (SprigEngine.Rejected r in headRejected)
            rows.Add(new[] { "Head", Link(RevitCompat.IdKey(r.Id), idMap), ReportHtml.Escape(r.Why) });

        Table(sb, "Picks that were dropped", ["Pick", "Element", "Reason"], rows);
    }

    private static void HeadTable(
        StringBuilder sb, IReadOnlyList<HeadPlan> plans,
        IReadOnlyDictionary<long, SprigEngine.HeadInfo> headInfo,
        IReadOnlyDictionary<long, ElementId> idMap)
    {
        if (plans.Count == 0)
        {
            sb.Append("<h2>Heads</h2>");
            Note(sb, "<strong>No heads to solve.</strong>");
            return;
        }

        List<IReadOnlyList<string>> rows = [];
        foreach (HeadPlan plan in plans)
        {
            headInfo.TryGetValue(plan.HeadId, out SprigEngine.HeadInfo? info);

            List<string> notes = [];
            if (plan.Issues.Count > 0)
                notes.Add(plan.IssueText());
            if (info is not null && !info.Upright)
                notes.Add("connector doesn't face down - is this an upright head?");

            string statusClass = plan.Status switch
            {
                SprigStatus.Ok => "ok",
                SprigStatus.Warn => "warn",
                _ => "skip",
            };
            string statusText = plan.Status.ToString().ToUpperInvariant();

            rows.Add(new[]
            {
                $"<span class=\"{statusClass}\">{statusText}</span>",
                Link(plan.HeadId, idMap),
                ReportHtml.Escape(info?.Category ?? "-"),
                plan.Kind is null ? "-" : plan.Kind.Value.ToString().ToUpperInvariant(),
                plan.Segment is null ? "-" : Link(plan.Segment.ElemId, idMap),
                Mm(plan.Station),
                Mm(plan.Offset),
                plan.Kind == SprigLayout.Armover ? Mm(plan.ArmoverLength) : "-",
                Mm(plan.SprigLength),
                SizeCell(info),
                $"{plan.LeanDeg:F3}",
                notes.Count > 0 ? ReportHtml.Escape(string.Join("; ", notes)) : "-",
            });
        }

        Table(sb, "Solved sprig plan (nothing built)",
            ["Status", "Head", "Category", "Type", "Branch", "Station", "Offset d",
             "Armover", "Sprig", "Size", "Lean (deg)", "Notes"], rows);

        int fallback = plans.Count(p =>
            headInfo.TryGetValue(p.HeadId, out SprigEngine.HeadInfo? i)
            && i.DiameterSource.Contains("fallback"));
        if (fallback > 0)
        {
            Note(sb, $"{fallback} head(s) had no readable connector size, so the sprig falls back "
                     + $"to {SprigSettings.FallbackDiameterMm:F0} mm. Every other head is sized "
                     + "from its own inlet connector.");
        }

        int odd = plans.Count(p =>
            headInfo.TryGetValue(p.HeadId, out SprigEngine.HeadInfo? i) && !i.IsSprinkler);
        if (odd > 0)
        {
            Note(sb, $"{odd} head(s) are not in the Sprinklers category (see the Category column). "
                     + "That is allowed - they were accepted because they each have exactly one "
                     + "open connector - but worth confirming they are really the heads you meant.");
        }
    }

    /// <summary>
    /// All heads landing on nearly the same station while sitting a long way off
    /// the main is the signature of picking a main that CROSSES the heads
    /// instead of running under them — by far the easiest pick to get wrong, and
    /// not obvious from the per-head rows alone.
    /// </summary>
    private static void MainMismatchHint(StringBuilder sb, IReadOnlyList<HeadPlan> plans)
    {
        List<HeadPlan> solved = [.. plans.Where(p => p.Segment is not null && p.Station is not null)];
        if (solved.Count < 2)
            return;

        double stationSpread = solved.Max(p => p.Station!.Value) - solved.Min(p => p.Station!.Value);
        double offsetSpread = solved.Max(p => p.Offset) - solved.Min(p => p.Offset);

        if (offsetSpread <= 1.0 || stationSpread >= 0.1 * offsetSpread)
            return;

        Note(sb, "<strong>Wrong pipe picked - this looks like a cross main, not the branch "
                 + $"line.</strong> All {solved.Count} heads project to within "
                 + $"<strong>{ReportHtml.Escape(Units.Mm(stationSpread))}</strong> of the same "
                 + "station, while their offsets range over "
                 + $"<strong>{ReportHtml.Escape(Units.Mm(offsetSpread))}</strong>. Heads spread "
                 + "along a branch line cannot all land on one point of the pipe that feeds them; "
                 + "that pattern means the pipe you picked CROSSES the heads rather than running "
                 + "under them. Re-run and pick the <strong>branch line</strong> - the pipe the "
                 + "sprinklers actually sit over.");
    }

    /// <summary>
    /// Every value that shaped this run, editable ones flagged against the
    /// defaults. An earlier version printed only some of them, which hid a
    /// min-sprig of 1000 mm and a max-armover of 0 that between them rejected an
    /// otherwise perfect layout.
    /// </summary>
    private static void SettingsTable(StringBuilder sb, SprigSettings s)
    {
        SprigSettings d = SprigSettings.Defaults;
        bool anyChanged = false;

        string Edited(double value, double dflt)
        {
            string text = $"{value:F0} mm";
            if (Math.Abs(value - dflt) > 1.0e-9)
            {
                anyChanged = true;
                text += $"  (default {dflt:F0} mm) &lt;- CHANGED";
            }
            return text;
        }

        List<IReadOnlyList<string>> rows =
        [
            ["Direct-sprig threshold", Edited(s.DirectSprigTolMm, d.DirectSprigTolMm), "editable",
             "head closer than this to the branch line gets a straight sprig instead of an armover"],
            ["Tee margin off segment ends", Edited(s.MinMainMarginMm, d.MinMainMarginMm), "editable",
             "SKIPS a head whose tee lands this close to a pipe end"],
            ["Minimum sprig length", Edited(s.MinSprigLengthMm, d.MinSprigLengthMm), "editable",
             "SKIPS a head whose sprig would be shorter than this"],
            ["Minimum tee spacing", $"{SprigSettings.MinTeeSpacingMm:F0} mm", "fixed",
             "warns when two tees land closer than this"],
            ["Maximum armover length", $"{SprigSettings.MaxArmoverLengthMm:F0} mm", "fixed",
             "warns above this; 0 means no limit"],
            ["Lean warning", $"{SprigSettings.LeanWarnDeg:F2} deg", "fixed",
             "warns when a DIRECT sprig leans more than this off plumb"],
            ["Sprig / armover size", "per head", "derived",
             "read from each head's own inlet connector - see the Size column above"],
        ];

        Table(sb, "Settings (Shift+Click the button to change the editable ones)",
            ["Setting", "Value", "Source", "What it does"], rows);

        if (anyChanged)
        {
            Note(sb, "<em>Row(s) marked CHANGED differ from this tool's built-in defaults - worth "
                     + "a second look if that wasn't deliberate.</em>");
        }
    }

    private static void Summary(
        StringBuilder sb, IReadOnlyList<HeadPlan> plans, IReadOnlyList<MainSegment> segments)
    {
        (int ok, int warn, int skip, int direct, int armover) = SprigSolver.Summarize(plans);

        sb.Append("<h2>Summary</h2><ul>");
        sb.Append($"<li><strong>{segments.Count}</strong> branch-line segment(s) usable</li>");
        sb.Append($"<li><strong>{ok}</strong> head(s) solved clean, <strong>{warn}</strong> with "
                  + $"warnings, <strong>{skip}</strong> skipped</li>");
        sb.Append($"<li>Would build <strong>{direct}</strong> DIRECT sprig(s) and "
                  + $"<strong>{armover}</strong> ARMOVER drop(s)</li>");
        sb.Append("<li>Heads marked SKIP are never built; heads marked WARN are</li>");
        sb.Append("</ul>");

        Note(sb, "<em>Nothing has been created at this point. The confirmation prompt decides "
                 + "whether it gets built, and any failure during the build rolls the whole run "
                 + "back.</em>");
    }

    /// <summary>
    /// Logs, after the build, every WARNING the swallower cleared so the run
    /// could stay unattended — so a suppressed disconnect or overlap warning is
    /// diagnosable after the fact instead of just vanishing.
    /// </summary>
    public static string BuildClearedWarnings(IReadOnlyList<string> cleared)
    {
        StringBuilder sb = new();
        Note(sb, $"{cleared.Count} Revit warning(s) were cleared so the build could run "
                 + "unattended. Only WARNING severity is cleared - nothing here stopped the build, "
                 + "and an error would still have rolled it back - but check these for anything "
                 + "that needs a manual look.");

        List<IReadOnlyList<string>> rows = [];
        for (int i = 0; i < cleared.Count; i++)
            rows.Add(new[] { (i + 1).ToString(), ReportHtml.Escape(cleared[i]) });

        Table(sb, "Cleared warnings", ["#", "Description"], rows);
        return sb.ToString();
    }
}
