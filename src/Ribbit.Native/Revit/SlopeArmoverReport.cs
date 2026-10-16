using System.Text;
using Autodesk.Revit.DB;
using Ribbit.Geometry;
using Ribbit.Native.Settings;
using Ribbit.Native.Ui;

namespace Ribbit.Native.Revit;

/// <summary>
/// The Slope Armover dry run. Ported from multi.py's print_report, reworded for
/// armovers — every "branch" here is one armover and its fixed reference is the
/// auto-detected tee end rather than a second click.
///
/// Reuses <see cref="SlopeReport"/>'s per-run sections verbatim, so a group,
/// segment or settings table reads identically whichever slope tool produced it.
/// </summary>
internal static class SlopeArmoverReport
{
    public static string Build(
        IReadOnlyList<SlopeJob> jobs,
        IReadOnlyList<(SlopeJob A, SlopeJob B, List<long> Shared)> conflicts,
        IReadOnlyDictionary<int, string> detectedWhy,
        SlopeSettings settings,
        IReadOnlyDictionary<long, ElementId> idMap)
    {
        StringBuilder sb = new();

        ReportHtml.Note(sb,
            "<strong>Nothing has been modified yet.</strong> Every armover below is solved "
            + "against the model as it stands now, and none is applied until you confirm. Each "
            + "one's tee end is held fixed, so the joint into the branch line is undisturbed - "
            + "check the two end elevations before saying yes.");

        Overview(sb, jobs, detectedWhy, idMap);

        if (conflicts.Count > 0)
            Conflicts(sb, conflicts, idMap);

        List<SlopeJob> bad = [.. jobs.Where(j => !j.Ok)];
        if (bad.Count > 0)
        {
            List<IReadOnlyList<string>> rows =
            [
                .. bad.Select(j => new[]
                {
                    j.Index.ToString(),
                    SlopeReport.Link(j.SeedId, idMap),
                    j.ReferenceUnknown ? "-" : SlopeReport.Link(j.ReferenceId, idMap),
                    ReportHtml.Escape(j.Error ?? "-"),
                })
            ];

            ReportHtml.Table(sb, "Armovers that could not be solved",
                ["#", "Pipe picked", "Tee end found", "Why"], rows);
        }

        foreach (SlopeJob job in jobs)
        {
            if (!job.Ok)
                continue;

            sb.Append("<hr/>");
            sb.Append("<h2>Armover ").Append(job.Index)
              .Append(" - pipe ").Append(SlopeReport.Link(job.SeedId, idMap))
              .Append(", fixed at ").Append(SlopeReport.Link(job.ReferenceId, idMap))
              .Append("</h2>");

            // Its own sentence, not a fragment appended after "tee end". The
            // reasons grew from short labels ("picked by hand") into full
            // clauses, which turned this into "...; tee end the other end is
            // open, this one reaches 15...".
            string why = detectedWhy.TryGetValue(job.Index, out string? w) ? w : "";
            sb.Append("<p><em>")
              .Append(job.Run!.Segments.Count)
              .Append(" segment(s) found by following the run from the one pipe you picked.")
              .Append(why.Length > 0 ? $" Tee end: {ReportHtml.Escape(why)}." : "")
              .Append("</em></p>");

            // A dry run has to name everything that will change. Rebuilding the
            // anchored end's fitting REPLACES it - new element id - which is a
            // material change the elevation tables say nothing about.
            if (job.FixedTeeRebuild is not null || job.FixedRebuild is not null)
            {
                string what = job.FixedTeeRebuild is not null ? "tee" : "fitting";
                ReportHtml.Note(sb,
                    $"The fixed end's <strong>{what} (element {job.ReferenceId}) will be deleted "
                    + "and rebuilt</strong> in place, so it comes back at the new angle instead "
                    + "of leaving a sliver against the pipe's tilted end face. It keeps its type "
                    + "and its connections, but <strong>its element id will change</strong>. If "
                    + "Revit refuses the rebuild, the original is put back as it was, the armover "
                    + "is still sloped, and the result says so - a thin sliver may then show there.");
            }

            SlopeReport.Headline(sb, job.Pitch!);
            SlopeReport.Segments(sb, job.Pitch!, idMap);
            SlopeReport.Groups(sb, job.Pitch!, job.Groups, idMap);
            Rejects(sb, job, idMap);
        }

        SlopeReport.SettingsTable(sb, settings);

        ReportHtml.Note(sb,
            $"The same slope is applied to <strong>every</strong> armover above: "
            + $"<strong>{settings.SlopeMmPerM:+0.00;-0.00} mm/m</strong>, measured away from each "
            + "armover's own fixed tee end. Shift+Click the button to change it - the settings "
            + "are shared with Slope Branch.<br/><br/>"
            + "Nothing has moved at this point. The confirmation prompt decides whether it does.");

        return sb.ToString();
    }

    private static void Overview(
        StringBuilder sb,
        IReadOnlyList<SlopeJob> jobs,
        IReadOnlyDictionary<int, string> detectedWhy,
        IReadOnlyDictionary<long, ElementId> idMap)
    {
        List<IReadOnlyList<string>> rows = [];

        foreach (SlopeJob job in jobs)
        {
            if (!job.Ok)
            {
                // A job can fail AFTER its tee end was found - "the reference is
                // one of the pipes on this branch", for instance. Showing which
                // element it settled on is exactly what makes that reason
                // actionable, so only a genuinely undetected reference prints
                // as "-".
                rows.Add(new[]
                {
                    job.Index.ToString(),
                    SlopeReport.Link(job.SeedId, idMap),
                    job.ReferenceUnknown ? "-" : SlopeReport.Link(job.ReferenceId, idMap),
                    "-", "-", "FAILED", "-", "-",
                });
                continue;
            }

            RePitch pitch = job.Pitch!;
            rows.Add(new[]
            {
                job.Index.ToString(),
                SlopeReport.Link(job.SeedId, idMap),
                SlopeReport.Link(job.ReferenceId, idMap),
                job.Run!.Segments.Count.ToString(),
                SlopeReport.Mm1(pitch.AnchorZ),
                $"{SlopeReport.Mm1(pitch.FarZOld)} &rarr; {SlopeReport.Mm1(pitch.FarZNew)}",
                SlopeReport.Slope(pitch.OldRateAway),
                ReportHtml.Escape(
                    detectedWhy.TryGetValue(job.Index, out string? w) ? w : "-"),
            });
        }

        ReportHtml.Table(sb, "One row per armover, in pick order",
            ["#", "Pipe picked", "Fixed tee end", "Segments", "Fixed end Z",
             "Free end Z", "Slope now", "How the tee end was found"], rows);
    }

    private static void Conflicts(
        StringBuilder sb,
        IReadOnlyList<(SlopeJob A, SlopeJob B, List<long> Shared)> conflicts,
        IReadOnlyDictionary<long, ElementId> idMap)
    {
        sb.Append("<h2>Armovers that overlap - NOTHING WILL BE APPLIED</h2>");

        ReportHtml.Note(sb,
            "Two of the armovers below would WRITE the same elements. Every armover is solved "
            + "against the model as it stands now, so applying one would silently invalidate "
            + "the other's plan while it still looked correct. Usually this is the same armover "
            + "picked twice, or two takeoffs sharing a tee - re-run with these picked "
            + "separately.");

        List<IReadOnlyList<string>> rows =
        [
            .. conflicts.Select(c => new[]
            {
                $"{c.A.Index} and {c.B.Index}",
                c.Shared.Count.ToString(),
                ReportHtml.Escape(string.Join(", ", c.Shared.Take(8))
                                  + (c.Shared.Count > 8 ? " ..." : "")),
            })
        ];

        ReportHtml.Table(sb, "Overlapping armovers",
            ["Armovers", "Shared elements", "Which"], rows);
    }

    private static void Rejects(
        StringBuilder sb, SlopeJob job, IReadOnlyDictionary<long, ElementId> idMap)
    {
        if (job.RejectedPicks.Count == 0 && job.RejectedSegments.Count == 0)
            return;

        List<IReadOnlyList<string>> rows = [];

        foreach (SprigEngine.Rejected r in job.RejectedPicks)
            rows.Add(new[] { SlopeReport.Link(RevitCompat.IdKey(r.Id), idMap), ReportHtml.Escape(r.Why) });

        foreach ((SlopeSegment seg, string reason) in job.RejectedSegments)
            rows.Add(new[] { SlopeReport.Link(seg.ElemId, idMap), ReportHtml.Escape(reason) });

        ReportHtml.Table(sb, "Dropped from this armover", ["Element", "Why"], rows);
    }
}
