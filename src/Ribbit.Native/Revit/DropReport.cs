using System.Text;
using Autodesk.Revit.DB;
using Ribbit.Geometry;
using Ribbit.Native.Settings;
using Ribbit.Native.Ui;
// Autodesk.Revit.DB also has a Units type; ours wins inside this file.
using Units = Ribbit.Geometry.Units;

namespace Ribbit.Native.Revit;

/// <summary>
/// Drop Connect's dry-run report. Ported from the reporting half of
/// lib/droplib/engine.py. Returns an HTML body string for
/// <see cref="RibbitUi.ShowReport"/>.
/// </summary>
internal static class DropReport
{
    private static string Link(long idInt, IReadOnlyDictionary<long, ElementId> idMap)
        => idMap.TryGetValue(idInt, out ElementId? eid)
            ? ReportHtml.Link(idInt.ToString(), eid)
            : ReportHtml.Escape(idInt.ToString());

    private static string Mm(double? valueFt) => ReportHtml.Escape(Units.Mm(valueFt));

    /// <summary>
    /// Size plus WHERE IT CAME FROM. Every new pipe is sized from the head's own
    /// inlet connector rather than from a setting, so a surprising number here is
    /// nearly always the fixture's real connector — but that is only obvious if
    /// the report says so. "(head)" means Revit reported it; "(fallback)" means
    /// the connector was unreadable and the constant was used instead.
    /// </summary>
    private static string SizeCell(DropEngine.HeadInfo? info)
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

    public static string Build(
        IReadOnlyList<DropHeadPlan> plans,
        IReadOnlyList<DropSegment> segments,
        IReadOnlyList<SprigEngine.Rejected> mainRejected,
        IReadOnlyList<SprigEngine.Rejected> headRejected,
        IReadOnlyDictionary<long, DropEngine.HeadInfo> headInfo,
        DropSettings settingsIn,
        IReadOnlyDictionary<long, ElementId> idMap)
    {
        StringBuilder sb = new();

        ReportHtml.Note(sb,
            "<strong>Nothing has been modified yet.</strong> This is what the builder intends to "
            + "do; you will be asked to confirm before anything is created, and cancelling there "
            + "leaves the model untouched. Check the stations, the DIRECT/RETURN BEND split and "
            + "the clearance warnings below.");

        MainTable(sb, segments, idMap);
        Rejects(sb, mainRejected, headRejected, idMap);
        HeadTable(sb, plans, headInfo, idMap);
        SettingsTable(sb, settingsIn);
        Summary(sb, plans, segments);

        return sb.ToString();
    }

    private static void MainTable(
        StringBuilder sb, IReadOnlyList<DropSegment> segments,
        IReadOnlyDictionary<long, ElementId> idMap)
    {
        if (segments.Count == 0)
        {
            sb.Append("<h2>Branch line</h2>");
            ReportHtml.Note(sb,
                "<strong>No usable branch-line segments.</strong> See the dropped picks below - "
                + "the reason names the element's class and category.");
            return;
        }

        List<IReadOnlyList<string>> rows = [];
        for (int i = 0; i < segments.Count; i++)
        {
            DropSegment seg = segments[i];
            string openEnds = (seg.OpenP0, seg.OpenP1) switch
            {
                (true, true) => "both",
                (true, false) => "start",
                (false, true) => "end",
                _ => "-",
            };

            rows.Add(new[]
            {
                (i + 1).ToString(),
                Link(seg.ElemId, idMap),
                ReportHtml.Escape(seg.Desc),
                Mm(seg.Length),
                seg.Diameter > 0.0 ? Mm(seg.Diameter) : "unknown",
                ReportHtml.Escape(FmtPitch(seg.SlopeDeg)),
                ReportHtml.Escape(openEnds),
            });
        }

        ReportHtml.Table(sb, "Branch-line segments (in pick order)",
            ["#", "Element", "What it is", "Length", "Diameter", "Pitch", "Open end(s)"], rows);

        int unknownDia = segments.Count(s => s.Diameter <= 0.0);
        if (unknownDia > 0)
        {
            ReportHtml.Note(sb,
                $"{unknownDia} segment(s) have no readable diameter, so the pipe-through-pipe "
                + "clearance checks are SKIPPED for heads on them rather than guessed at. Check "
                + "those drops by eye.");
        }

        int nonPipe = segments.Count(s => !s.IsPipe);
        if (nonPipe > 0)
        {
            ReportHtml.Note(sb,
                $"{nonPipe} segment(s) are not Revit <code>Pipe</code> elements. The geometry above "
                + "is still correct, but the builder relies on Pipe-only APIs "
                + "(<code>PlumbingUtils.BreakCurve</code>, <code>Pipe.Create</code>) and will need "
                + "a different approach for these.");
        }

        if (segments.Any(s => Math.Abs(s.SlopeDeg) > 1.0))
        {
            ReportHtml.Note(sb,
                "The branch is pitched more than 1 deg. Both vertical pipes are built PLUMB (the "
                + "riser and the drop), so the tee sits off square by the pitch rather than the "
                + "riser leaning.");
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
            rows.Add(new[] { "Branch", Link(RevitCompat.IdKey(r.Id), idMap), ReportHtml.Escape(r.Why) });
        foreach (SprigEngine.Rejected r in headRejected)
            rows.Add(new[] { "Head", Link(RevitCompat.IdKey(r.Id), idMap), ReportHtml.Escape(r.Why) });

        ReportHtml.Table(sb, "Picks that were dropped", ["Pick", "Element", "Reason"], rows);
    }

    private static void HeadTable(
        StringBuilder sb, IReadOnlyList<DropHeadPlan> plans,
        IReadOnlyDictionary<long, DropEngine.HeadInfo> headInfo,
        IReadOnlyDictionary<long, ElementId> idMap)
    {
        if (plans.Count == 0)
        {
            sb.Append("<h2>Heads</h2>");
            ReportHtml.Note(sb, "<strong>No heads to solve.</strong>");
            return;
        }

        List<IReadOnlyList<string>> rows = [];
        foreach (DropHeadPlan plan in plans)
        {
            headInfo.TryGetValue(plan.HeadId, out DropEngine.HeadInfo? info);

            List<string> notes = [];
            if (plan.Issues.Count > 0)
                notes.Add(plan.IssueText());
            if (info is not null && !info.Pendent)
                notes.Add("connector doesn't face up - is this a pendent head?");

            string statusClass = plan.Status switch
            {
                SprigStatus.Ok => "ok",
                SprigStatus.Warn => "warn",
                _ => "skip",
            };

            string kind = plan.Kind switch
            {
                DropLayout.Direct => "DIRECT",
                DropLayout.ReturnBend => "RETURN BEND",
                DropLayout.EndBend => "END BEND",
                _ => "-",
            };

            rows.Add(new[]
            {
                $"<span class=\"{statusClass}\">{plan.Status.ToString().ToUpperInvariant()}</span>",
                Link(plan.HeadId, idMap),
                ReportHtml.Escape(info?.Category ?? "-"),
                kind,
                plan.Segment is null ? "-" : Link(plan.Segment.ElemId, idMap),
                plan.Station is null ? "-" : Mm(plan.Station),
                Mm(plan.Offset),
                plan.Kind == DropLayout.Direct ? "-" : Mm(plan.RiserLength),
                plan.Kind == DropLayout.Direct ? "-" : Mm(plan.ArmoverLength),
                Mm(plan.DropLength),
                SizeCell(info),
                $"{plan.LeanDeg:F3}",
                notes.Count > 0 ? ReportHtml.Escape(string.Join("; ", notes)) : "-",
            });
        }

        ReportHtml.Table(sb, "Solved drop plan (nothing built)",
            ["Status", "Head", "Category", "Type", "Branch", "Station", "Offset d",
             "Riser", "Armover", "Drop", "Size", "Lean (deg)", "Notes"], rows);

        int fallback = plans.Count(p =>
            headInfo.TryGetValue(p.HeadId, out DropEngine.HeadInfo? i)
            && i.DiameterSource.Contains("fallback"));
        if (fallback > 0)
        {
            ReportHtml.Note(sb,
                $"{fallback} head(s) had no readable connector size, so the riser, armover and "
                + $"drop fall back to {DropSettings.FallbackDiameterMm:F0} mm. Every other head is "
                + "sized from its own inlet connector.");
        }

        int odd = plans.Count(p =>
            headInfo.TryGetValue(p.HeadId, out DropEngine.HeadInfo? i) && !i.IsSprinkler);
        if (odd > 0)
        {
            ReportHtml.Note(sb,
                $"{odd} head(s) are not in the Sprinklers category (see the Category column). That "
                + "is allowed - they were accepted because they each have exactly one open "
                + "connector - but worth confirming they are really the heads you meant.");
        }
    }

    private static void SettingsTable(StringBuilder sb, DropSettings s)
    {
        DropSettings d = DropSettings.Defaults;
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

        string allowText = s.AllowDirectDrops ? "allowed" : "OFF - direct heads are skipped";
        if (s.AllowDirectDrops != d.AllowDirectDrops)
        {
            anyChanged = true;
            allowText += "  &lt;- CHANGED";
        }

        List<IReadOnlyList<string>> rows =
        [
            ["Direct-drop threshold", Edited(s.DirectDropTolMm, d.DirectDropTolMm), "editable",
             "head closer than this to the branch line is fed by a straight drop, not a return bend"],
            ["Return-bend riser height", Edited(s.RiserHeightMm, d.RiserHeightMm), "editable",
             "how far the riser climbs above the branch before turning into the armover"],
            ["Tee margin off segment ends", Edited(s.MinMainMarginMm, d.MinMainMarginMm), "editable",
             "SKIPS a head whose tee lands this close to a pipe end"],
            ["Minimum drop length", Edited(s.MinDropLengthMm, d.MinDropLengthMm), "editable",
             "SKIPS a head whose drop would be shorter than this"],
            ["Allow DIRECT drops", allowText, "editable",
             "when off, a head sitting under the branch is skipped with a note instead of getting "
             + "a bare straight drop"],
            ["Minimum tee spacing", $"{DropSettings.MinTeeSpacingMm:F0} mm", "fixed",
             "warns when two tees land closer than this"],
            ["Maximum armover length", $"{DropSettings.MaxArmoverLengthMm:F0} mm", "fixed",
             "warns above this; 0 means no limit"],
            ["Unsupported-armover limit", $"{DropSettings.ArmoverHangerWarnMm:F0} mm", "fixed",
             "NFPA 13 hanger warning; drops to 305 mm above 100 psi"],
            ["Pipe clearance", $"{DropSettings.ClearanceMm:F0} mm", "fixed",
             "extra air demanded between the drop/armover and the branch pipe"],
            ["Lean warning", $"{DropSettings.LeanWarnDeg:F2} deg", "fixed",
             "warns when a DIRECT drop leans more than this off plumb"],
            ["Riser / armover / drop size", "per head", "derived",
             "read from each head's own inlet connector - see the Size column above"],
        ];

        ReportHtml.Table(sb, "Settings (Shift+Click the button to change the editable ones)",
            ["Setting", "Value", "Source", "What it does"], rows);

        if (anyChanged)
        {
            ReportHtml.Note(sb,
                "<em>Row(s) marked CHANGED differ from this tool's built-in defaults - worth a "
                + "second look if that wasn't deliberate.</em>");
        }
    }

    private static void Summary(
        StringBuilder sb, IReadOnlyList<DropHeadPlan> plans, IReadOnlyList<DropSegment> segments)
    {
        (int ok, int warn, int skip, int direct, int returnBend, int endBend) =
            DropSolver.Summarize(plans);

        sb.Append("<h2>Summary</h2><ul>");
        sb.Append($"<li><strong>{segments.Count}</strong> branch-line segment(s) usable</li>");
        sb.Append($"<li><strong>{ok}</strong> head(s) solved clean, <strong>{warn}</strong> with "
                  + $"warnings, <strong>{skip}</strong> skipped</li>");
        sb.Append($"<li>Would build <strong>{direct}</strong> DIRECT, "
                  + $"<strong>{returnBend}</strong> RETURN BEND and <strong>{endBend}</strong> "
                  + "END BEND drop(s)</li>");
        sb.Append("<li>Heads marked SKIP are never built; heads marked WARN are</li>");
        sb.Append("</ul>");

        ReportHtml.Note(sb,
            "<em>Nothing has been created at this point. The confirmation prompt decides whether "
            + "it gets built, and any failure during the build rolls the whole run back.</em>");
    }

    public static string BuildClearedWarnings(IReadOnlyList<string> cleared)
        => SprigReport.BuildClearedWarnings(cleared);
}
