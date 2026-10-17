using System.Text;
using Autodesk.Revit.DB;
using Ribbit.Geometry;
using Ribbit.Native.Settings;
using Ribbit.Native.Tools;
using Ribbit.Native.Ui;
// Autodesk.Revit.DB also has a Units type; ours wins inside this file.
using Units = Ribbit.Geometry.Units;

namespace Ribbit.Native.Revit;

/// <summary>
/// Slope Through's dry-run report. Ported from the report halves of
/// lib/slopelib/engine.py and lib/slopelib/through.py. Returns an HTML body
/// string for <see cref="RibbitUi.ShowReport"/>.
/// </summary>
internal static class SlopeReport
{
    internal static string Link(long? idInt, IReadOnlyDictionary<long, ElementId> idMap)
    {
        if (idInt is null)
            return "-";

        return idMap.TryGetValue(idInt.Value, out ElementId? eid)
            ? ReportHtml.Link(idInt.Value.ToString(), eid)
            : ReportHtml.Link(idInt.Value.ToString(), SlopeThrough.MakeElementId(idInt.Value));
    }

    internal static string Mm(double? ft) => ReportHtml.Escape(Units.Mm(ft));

    internal static string Mm1(double? ft) => ReportHtml.Escape(SlopeSolver.Mm1(ft));

    internal static string Slope(double? rate) => ReportHtml.Escape(SlopeSolver.FmtSlope(rate));

    public static string Build(
        SlopePlan plan, SlopeSettings settingsIn, IReadOnlyDictionary<long, ElementId> idMap)
    {
        StringBuilder sb = new();

        ReportHtml.Note(sb,
            "<strong>Nothing has been modified yet.</strong> This is the selected branch line(s) put on a "
            + "single developed slope across every elbow and selected tee. Check the fixed end and the far end "
            + "elevations below first - if the branch tips the wrong way, the fixed end or the "
            + "fall direction is wrong.");

        List<Leg> good = [.. plan.Legs.Where(l => l.Ok)];

        HashSet<long> known = [];
        foreach (Leg leg in good)
        {
            known.UnionWith(leg.BranchIds);
            if (leg.CornerElemId is not null)
                known.Add(leg.CornerElemId.Value);
        }

        Overview(sb, plan, good, settingsIn, idMap);
        Corners(sb, good, idMap);
        DroppedLegs(sb, plan);

        foreach (Leg leg in good)
        {
            sb.Append("<hr/>");
            string parent = leg.Parent is null ? "fixed end" : $"leg {leg.Parent.Index + 1}";
            sb.Append($"<h2>Leg {leg.Index + 1} - {leg.Run!.Segments.Count} segment(s), "
                      + $"anchored at {ReportHtml.Escape(parent)}</h2>");

            Headline(sb, leg.Pitch!);
            Segments(sb, leg.Pitch!, idMap);
            Groups(sb, leg.Pitch!, leg.Groups, idMap);
            Orphans(sb, OrphanJoints(leg.Groups, leg.Joints, known), idMap);
            Rejects(sb, leg, idMap);
        }

        SettingsTable(sb, settingsIn);

        ReportHtml.Note(sb,
            "<em>Nothing has moved at this point. The confirmation prompt decides whether it "
            + "does. Slope and every tolerance above are shared with Slope Branch - Shift+Click "
            + "either button to change them.</em>");

        return sb.ToString();
    }

    private static void Overview(
        StringBuilder sb, SlopePlan plan, IReadOnlyList<Leg> good,
        SlopeSettings settingsIn, IReadOnlyDictionary<long, ElementId> idMap)
    {
        sb.Append("<h2>The developed branch</h2>");

        if (plan.HitLegCap)
        {
            ReportHtml.Note(sb,
                "<strong>Stopped at the leg cap with more of the branch still unwalked.</strong> "
                + "This branch has more corners than the cap allows, so it was CUT SHORT - the "
                + "legs below are a real but INCOMPLETE picture of the branch, not the whole "
                + "thing. Pick a smaller piece, or raise the cap if this really is one continuous "
                + "branch line.");
        }

        if (plan.UnreachedLegs.Count > 0)
        {
            // Excluding these is right - they have no elbow chain to the tie-in,
            // so a developed slope from it means nothing for them. Saying
            // nothing about them was not: the pick clearly included pipe that is
            // about to be ignored.
            int pipes = plan.UnreachedLegs.Sum(l => l.Elements.Count);
            ReportHtml.Note(sb,
                $"<strong>{plan.UnreachedLegs.Count} separate run(s) in your pick "
                + $"({pipes} pipe(s)) are NOT part of this branch</strong> and will be left "
                + "alone. They have no chain of elbows leading back to the fixed end, so a slope "
                + "developed from it does not reach them - most often this means two unconnected "
                + "runs were window-selected together. Slope them separately if they need it.");
        }

        if (good.Count == 0)
            return;

        List<IReadOnlyList<string>> rows = [];

        foreach (Leg leg in good)
        {
            RePitch pitch = leg.Pitch!;
            string anchor = leg.Parent is null
                ? "fixed end"
                : leg.Junction is not null
                    ? $"tee on leg {leg.Parent.Index + 1}"
                    : $"corner from leg {leg.Parent.Index + 1}";

            rows.Add(new[]
            {
                (leg.Index + 1).ToString(),
                Link(leg.BranchIds.FirstOrDefault(), idMap),
                leg.Run!.Segments.Count.ToString(),
                ReportHtml.Escape(anchor),
                Mm(leg.Run.PlanLength),
                // Where this leg's anchor sits along the developed run - which
                // includes the spans through the corner elbows, not just the
                // running total of pipe lengths.
                Mm(SlopeThrough.DevelopedAtAnchor(leg)),
                Mm1(pitch.AnchorZ),
                $"{Mm1(pitch.FarZOld)} -&gt; {Mm1(pitch.FarZNew)}",
            });
        }

        ReportHtml.Table(sb, "Legs in order from the fixed end",
            ["Leg", "A pipe", "Segments", "Anchored at", "Plan length",
             "Developed start", "Anchor Z", "Far end Z"], rows);

        sb.Append("<ul>");
        sb.Append($"<li>Developed length across all legs: <strong>"
                  + $"{Mm(SlopeThrough.DevelopedLength(plan))}</strong></li>");
        sb.Append($"<li>One slope applied along the whole path: <strong>"
                  + $"{Math.Abs(settingsIn.SlopeMmPerM):0.00} mm/m</strong>, "
                  + $"<strong>{(settingsIn.SlopeMmPerM < 0 ? "FALLING" : "RISING")}</strong> away from "
                  + "the fixed end</li>");

        int tees = plan.Junctions.Keys.Count(fid => good.Any(l => l.Junction?.FittingId == fid));
        if (tees > 0)
        {
            sb.Append($"<li><strong>{tees}</strong> tee(s)/cross(es) join selected branches to the "
                      + "leg they come off: each branch starts at that leg's NEW height at the tee "
                      + "and carries on at the same slope, so the developed distance runs along "
                      + "the main and out along the branch</li>");
        }

        if (plan.CarriedBranches.Count > 0)
        {
            sb.Append($"<li><strong>{plan.CarriedBranches.Count}</strong> branch(es) you did NOT "
                      + "select are carried along as ONE piece by the fitting they hang off - "
                      + "moved up or down with it, keeping their own slope: "
                      + string.Join(", ", plan.CarriedBranches.Select(c =>
                          $"{Link(c.RootId, idMap)} ({c.Elements} element(s))"))
                      + "</li>");
        }

        sb.Append($"<li><strong>{plan.Joints.Count}</strong> joint(s) will be broken and remade</li>");

        if (plan.FixedInBranch)
        {
            sb.Append("<li>Fixed end: you clicked <strong>on the branch</strong>, so the click "
                      + "only names which END holds still. Nothing is pinned, and the pipe you "
                      + "clicked moves with the rest.</li>");
        }
        else
        {
            sb.Append($"<li>Fixed end: you clicked <strong>off the branch</strong> (element "
                      + $"{plan.FixedId}), so that element's whole group is PINNED as never-move "
                      + "as well as naming the fixed end.</li>");

            Leg? rootLeg = good.FirstOrDefault(l => l.Parent is null);
            if (rootLeg?.FixedRebuild is not null)
            {
                sb.Append("<li>That fixed element is a plain 2-connector fitting sitting directly "
                          + "on the branch, so it will be <strong>DELETED and REBUILT fresh</strong> "
                          + "after the re-pitch instead of just pinned - a rigid fitting's "
                          + "branch-facing connector cannot be corrected to the new slope without "
                          + "also disturbing its OTHER connector, which must not move.</li>");
            }
            else if (rootLeg?.FixedTeeRebuild is not null)
            {
                // Deliberately states that it MAY not work: Revit accepts only
                // about a third of these, and promising a rebuild that usually
                // does not happen would be worse than promising nothing.
                sb.Append("<li>That fixed element is a <strong>TEE</strong>, so a rebuild will be "
                          + "ATTEMPTED after the re-pitch - deleting it and placing a fresh one at "
                          + "the new angle, which changes its element id. Revit refuses this on "
                          + "many tees; if it does, <strong>the tee is left exactly as it is</strong> "
                          + "and the run still commits, with a thin gap possible where the pipe "
                          + "meets it. Either way nothing else is affected.</li>");
            }
        }
        sb.Append("</ul>");

        if (good.Count == 1)
        {
            ReportHtml.Note(sb,
                "Only ONE leg - no elbow or selected tee led to a second horizontal leg, so this is a plain "
                + "straight run. That is handled here exactly as Slope Branch handles it, by the "
                + "same code; nothing about the result differs.");
        }
    }

    /// <summary>
    /// Every corner elbow's residual mismatch, MEASURED after the best available
    /// fix. A single rigid elbow generally cannot satisfy two independently
    /// sloped legs at once; this is what is still off on the CHILD leg's side
    /// even after rolling the elbow as close as it can get. A live test found a
    /// real branch where this residual was large enough that nudging the elbow
    /// afterward snapped the parent leg's own pipe back to its pre-slope angle —
    /// so a large number here is a real risk, not a rounding curiosity.
    /// </summary>
    private static void Corners(
        StringBuilder sb, IReadOnlyList<Leg> good, IReadOnlyDictionary<long, ElementId> idMap)
    {
        List<IReadOnlyList<string>> rows = [];
        int overWarn = 0;

        foreach (Leg leg in good)
        {
            if (leg.Parent is null || leg.CornerResidualDeg is null)
                continue;

            double residual = leg.CornerResidualDeg.Value;
            bool over = residual > SlopeSettings.CornerResidualWarnDeg;
            if (over)
                overWarn++;

            // Marked in the row itself, not just counted above the table: the
            // point is that ONE bad corner among a dozen fine ones should not
            // read the same as the rest.
            rows.Add(new[]
            {
                ReportHtml.Escape($"leg {leg.Parent.Index + 1} -> leg {leg.Index + 1}"),
                Link(leg.CornerElemId, idMap),
                over
                    ? $"<strong>{residual:F4} deg - CHECK THIS</strong>"
                    : $"{residual:F4} deg",
            });
        }

        if (rows.Count == 0)
            return;

        sb.Append("<h2>Corner elbows and tees - residual mismatch</h2>");
        ReportHtml.Note(sb,
            "Each corner elbow is rotated to exactly match its PARENT leg, then rolled to bring "
            + "the CHILD leg's connector as close as possible to its own independently-required "
            + "direction - a single rigid elbow generally can't satisfy both sides exactly. This "
            + "is what's left over on the child side after that best effort. A small number is "
            + "probably absorbed by the elbow family's own slope tolerance; a large one is worth "
            + "checking closely - and nothing has moved yet, so this is the moment to decide "
            + "whether to proceed.");

        if (overWarn > 0)
        {
            ReportHtml.Note(sb,
                $"<strong>{overWarn} corner(s) are more than "
                + $"{SlopeSettings.CornerResidualWarnDeg:F1} deg out</strong> and are marked in "
                + "the table below. That is past what an elbow family's slope tolerance usually "
                + "absorbs, and a corner left this far off has been seen to snap its parent leg's "
                + "pipe back to the old angle when nudged afterwards. Worth opening those elbows "
                + "before committing.");
        }

        ReportHtml.Note(sb,
            "A TEE is rolled the same way, about the leg it sits on, so its branch outlet tilts to "
            + "the branch's slope - exact to a few thousandths of a degree on a square tee. A CROSS "
            + "with a branch on each side cannot be exact: rolling tips one outlet up and the other "
            + "down while both branches rise away from the main, so it is left split between them "
            + "and shows here as roughly the slope's own angle.");

        ReportHtml.Table(sb, "Corner elbows and tees", ["Corner", "Fitting", "Residual mismatch"], rows);
    }

    private static void DroppedLegs(StringBuilder sb, SlopePlan plan)
    {
        List<Leg> bad = [.. plan.Legs.Where(l => !l.Ok)];
        if (bad.Count == 0)
            return;

        List<IReadOnlyList<string>> rows =
        [
            .. bad.Select(leg => new[]
            {
                (leg.Index + 1).ToString(),
                leg.Elements.Count.ToString(),
                ReportHtml.Escape(leg.Error ?? "-"),
            })
        ];

        ReportHtml.Table(sb, "Legs that could not be solved", ["Leg", "Pipes", "Why"], rows);

        List<Leg> opens = [.. bad.Where(l => l.Parent is not null && l.Parent.Ok)];
        if (opens.Count > 0)
        {
            ReportHtml.Note(sb,
                "A dropped leg's corner elbow is never moved - only a leg that solves gets its "
                + "corner attached to its parent - while the parent leg still re-pitches out from "
                + "under it. So the joint at that corner will very likely come back OPEN below, "
                + "listed as an open joint rather than as this leg's own failure. Affects leg(s): "
                + ReportHtml.Escape(string.Join(", ", opens.Select(l => l.Index + 1))) + ".");
        }
    }

    /// <summary>
    /// What happens to the anchor's height. Only the first leg truly keeps it;
    /// every leg after takes it from the leg before (at the elbow or tee they
    /// share), and saying "unchanged" there described the wrong leg.
    /// </summary>
    private static string AnchorWord(RePitch pitch)
        => Math.Abs(pitch.AnchorZ - pitch.AnchorZModel) * Units.MmPerFt < 0.05
            ? "unchanged"
            : "carried on from the leg before";

    internal static void Headline(StringBuilder sb, RePitch pitch)
    {
        SlopeRun run = pitch.Run;
        List<IReadOnlyList<string>> rows = [];

        if (pitch.IsEndAnchor)
        {
            string anchorWord = pitch.AnchorAtMin ? "start" : "end";
            rows.Add(new[]
            {
                "Fixed end",
                ReportHtml.Escape($"station {Units.Mm(pitch.AnchorStation)} ({anchorWord} of the run axis)"),
                $"elevation {Mm1(pitch.AnchorZ)} - {AnchorWord(pitch)}",
            });
            rows.Add(new[]
            {
                "Free end",
                ReportHtml.Escape($"station {Units.Mm(pitch.FarStation)}, "
                                  + $"{Units.Mm(Math.Abs(pitch.FarStation - pitch.AnchorStation))} away"),
                $"{Mm1(pitch.FarZOld)} -&gt; {Mm1(pitch.FarZNew)}  (moves "
                + $"{Mm1(pitch.FarZNew - pitch.FarZOld)})",
            });
        }
        else
        {
            // Mid-run anchor: the fixed reference tees in partway along, so the
            // run makes a V - it rises (or falls) away from the fixed point in
            // BOTH directions, and both ends move.
            rows.Add(new[]
            {
                "Fixed point",
                ReportHtml.Escape($"station {Units.Mm(pitch.AnchorStation)} - MID-RUN; the run "
                                  + "extends both ways from here"),
                $"elevation {Mm1(pitch.AnchorZ)} - {AnchorWord(pitch)}",
            });
            rows.Add(new[]
            {
                ReportHtml.Escape($"End at {Units.Mm(pitch.NearStation)}"),
                ReportHtml.Escape($"{Units.Mm(Math.Abs(pitch.NearStation - pitch.AnchorStation))} "
                                  + "from the fixed point"),
                $"{Mm1(pitch.NearZOld)} -&gt; {Mm1(pitch.NearZNew)}  (moves "
                + $"{Mm1(pitch.NearZNew - pitch.NearZOld)})",
            });
            rows.Add(new[]
            {
                ReportHtml.Escape($"End at {Units.Mm(pitch.FarStation)}"),
                ReportHtml.Escape($"{Units.Mm(Math.Abs(pitch.FarStation - pitch.AnchorStation))} "
                                  + "from the fixed point"),
                $"{Mm1(pitch.FarZOld)} -&gt; {Mm1(pitch.FarZNew)}  (moves "
                + $"{Mm1(pitch.FarZNew - pitch.FarZOld)})",
            });
        }

        // Both stated AWAY FROM THE FIXED POINT, the way the slope was typed, so
        // "now" and "after" are directly comparable and a sign change means a
        // real change of direction.
        rows.Add(new[]
        {
            "Slope now", Slope(pitch.OldRateAway),
            "to the far end, measured away from the fixed point - the per-segment column below "
            + "tells the rest",
        });
        rows.Add(new[]
        {
            "Slope after", Slope(pitch.NewRateAway),
            "what you asked for, away from the fixed point"
            + (pitch.IsEndAnchor ? "" : " in BOTH directions"),
        });
        rows.Add(new[]
        {
            "Fitting rotation",
            $"{pitch.Theta * 180.0 / Math.PI:+0.0000;-0.0000} deg (representative)",
            "each in-line fitting rotates by its own station's value - see the groups table",
        });
        rows.Add(new[]
        {
            "Run length", Mm(run.PlanLength),
            ReportHtml.Escape($"plan length, {run.Segments.Count} segment(s)"),
        });

        ReportHtml.Table(sb, "Re-pitch", ["", "Where", "Elevation / detail"], rows);
    }

    internal static void Segments(
        StringBuilder sb, RePitch pitch, IReadOnlyDictionary<long, ElementId> idMap)
    {
        List<IReadOnlyList<string>> rows = [];
        for (int idx = 0; idx < pitch.Run.Segments.Count; idx++)
        {
            SlopeSegment seg = pitch.Run.Segments[idx];
            double s0 = pitch.Run.Station(seg.P0);
            double s1 = pitch.Run.Station(seg.P1);

            rows.Add(new[]
            {
                (idx + 1).ToString(),
                Link(seg.ElemId, idMap),
                ReportHtml.Escape(seg.Desc),
                Mm(seg.PlanLength),
                ReportHtml.Escape($"{Units.Mm(Math.Min(s0, s1))} - {Units.Mm(Math.Max(s0, s1))}"),
                Slope(pitch.SegmentRateAway(seg)),
                $"{Mm1(seg.P0.Z)} -&gt; {Mm1(pitch.NewZ(s0))}",
                $"{Mm1(seg.P1.Z)} -&gt; {Mm1(pitch.NewZ(s1))}",
                Mm1(Math.Max(Math.Abs(pitch.Dz(seg.P0)), Math.Abs(pitch.Dz(seg.P1)))),
            });
        }

        ReportHtml.Table(sb, "Run segments (plan position is never changed - Z only)",
            ["#", "Element", "What it is", "Plan length", "Station range", "Slope now",
             "Start Z", "End Z", "Largest move"], rows);

        int nonPipe = pitch.Run.Segments.Count(s => !s.IsPipe);
        if (nonPipe > 0)
        {
            ReportHtml.Note(sb,
                $"{nonPipe} segment(s) are not Revit <code>Pipe</code> elements. The geometry "
                + "above is still correct, but re-pitching works by rewriting the element's "
                + "LocationCurve, and a fabrication part placed by POINT has none - those will "
                + "fail and roll the whole run back.");
        }
    }

    internal static void Groups(
        StringBuilder sb, RePitch pitch, IReadOnlyList<SlopeGroup> groups,
        IReadOnlyDictionary<long, ElementId> idMap)
    {
        sb.Append("<h3>Everything else connected to this leg</h3>");

        if (groups.Count == 0)
        {
            ReportHtml.Note(sb,
                "Nothing is connected to the picked segments. That is worth a second look on a "
                + "sprinklered branch - if the tees and sprigs are not showing up here, they are "
                + "not actually connected in the model, and re-pitching the pipe will leave them "
                + "behind.");
            return;
        }

        static string Label(GroupKind kind) => kind switch
        {
            GroupKind.Inline => "IN-LINE",
            GroupKind.Rider => "RIDER",
            GroupKind.Foreign => "FOREIGN",
            GroupKind.Missed => "MISSED RUN",
            GroupKind.NonRigid => "NON-RIGID",
            _ => kind.ToString(),
        };

        List<SlopeGroup> ordered =
            [.. groups.OrderBy(g => g.Pivot is null ? 0.0 : pitch.Run.Station(g.Pivot.Value))];

        List<IReadOnlyList<string>> rows = [];
        foreach (SlopeGroup group in ordered)
        {
            string station = group.Pivot is null
                ? "-"
                : Mm(pitch.Run.Station(group.Pivot.Value));

            string move;
            if (!SlopeGroupUtil.Moves(group.Kind))
            {
                move = "not moved";
            }
            else
            {
                (double low, double high) = group.DzRange();
                move = Math.Abs(high - low) < 1.0e-9
                    ? Mm1(low)
                    : $"{Mm1(low)} to {Mm1(high)}";
            }

            rows.Add(new[]
            {
                Label(group.Kind),
                Link(group.RootId, idMap),
                group.Size.ToString(),
                group.Parts.Count > 0 ? $"{group.Parts.Count} in line" : "-",
                group.InternalJoints.Count > 0
                    ? $"{group.Attachments.Count} + {group.InternalJoints.Count} inner"
                    : group.Attachments.Count.ToString(),
                station,
                move,
                group.Rotate && group.Pivot is not null
                    ? $"{pitch.ThetaAt(pitch.Run.Station(group.Pivot.Value)) * 180.0 / Math.PI:+0.0000;-0.0000} deg"
                    : "-",
                group.Notes.Count > 0
                    ? ReportHtml.Escape(string.Join("; ", group.Notes))
                    : "-",
            });
        }

        ReportHtml.Table(sb, "Connected groups, in run order",
            ["Kind", "Root element", "Elements", "Fittings", "Joints", "Station",
             "Vertical move", "Rotation", "Notes"], rows);

        int stuck = groups.Count(g => !SlopeGroupUtil.Moves(g.Kind));
        if (stuck > 0)
        {
            ReportHtml.Note(sb,
                $"<strong>{stuck} group(s) will NOT be moved</strong> (see Kind above). Every "
                + "joint between the run and one of those groups is broken and remade in place, so "
                + "any of them sitting where the pipe actually moves ends up strained or refusing "
                + "to reconnect - the run happens anyway and the result says which ones failed. At "
                + "the FIXED end this is normal and harmless: that end does not move.");
        }

        int cutShort = groups.Count(g => g.Truncated);
        if (cutShort > 0)
        {
            ReportHtml.Note(sb,
                $"<strong>{cutShort} group(s) were cut short at the element cap.</strong> The walk "
                + "that works out what is connected to what stopped before it finished, so those "
                + "groups may be missing elements - and an element that should have belonged to "
                + "one of them can end up in a group of its own and be MOVED. Raise "
                + "<strong>Max group size</strong> (Shift+Click the button) and run the dry run "
                + "again before committing anything.");
        }

        int missed = groups.Count(g => g.Kind == GroupKind.Missed);
        if (missed > 0)
        {
            ReportHtml.Note(sb,
                $"<strong>Stop and re-pick.</strong> {missed} group(s) look like more of this same "
                + "branch line that was not included. Re-pitching part of a run leaves a kink at "
                + "the join that is invisible in plan.");
        }
    }

    /// <summary>
    /// Joints whose far side belongs to no group at all. A cross-check on the
    /// grouping, not a classification: every element a picked segment touches is
    /// supposed to end up in exactly one group, and anything that does not is an
    /// element the tool will disconnect without ever deciding what to do with it.
    /// </summary>
    private static List<(long AId, long BId, long Eid)> OrphanJoints(
        IReadOnlyList<SlopeGroup> groups, IReadOnlyList<Joint> joints, ISet<long> branchIds)
    {
        HashSet<long> known = [];
        foreach (SlopeGroup group in groups)
            known.UnionWith(group.MemberIds);

        List<(long, long, long)> outp = [];
        foreach (Joint j in joints)
        {
            foreach (long eid in new[] { j.AId, j.BId })
            {
                if (branchIds.Contains(eid) || known.Contains(eid))
                    continue;
                outp.Add((j.AId, j.BId, eid));
            }
        }
        return outp;
    }

    /// <summary>Silent when there are none, which is the normal case.</summary>
    private static void Orphans(
        StringBuilder sb, IReadOnlyList<(long AId, long BId, long Eid)> orphans,
        IReadOnlyDictionary<long, ElementId> idMap)
    {
        if (orphans.Count == 0)
            return;

        sb.Append("<h3>Connected, but in no group</h3>");
        ReportHtml.Note(sb,
            "<strong>This is a bug in the tool, not in your model.</strong> These elements are "
            + "joined to the run - so the joint gets broken - but they never landed in a group, so "
            + "nothing decides whether they should move. They will be disconnected and left where "
            + "they are. Send this table over.");

        List<IReadOnlyList<string>> rows =
        [
            .. orphans.Select(o => new[]
            {
                Link(o.Eid, idMap), Link(o.AId, idMap), Link(o.BId, idMap),
            })
        ];

        ReportHtml.Table(sb, "Ungrouped elements",
            ["Element", "Joint side A", "Joint side B"], rows);
    }

    private static void Rejects(
        StringBuilder sb, Leg leg, IReadOnlyDictionary<long, ElementId> idMap)
    {
        if (leg.RejectedPicks.Count == 0 && leg.RejectedSegments.Count == 0)
            return;

        List<IReadOnlyList<string>> rows = [];
        foreach (SprigEngine.Rejected r in leg.RejectedPicks)
            rows.Add(new[] { Link(RevitCompat.IdKey(r.Id), idMap), ReportHtml.Escape(r.Why) });
        foreach ((SlopeSegment seg, string why) in leg.RejectedSegments)
            rows.Add(new[] { Link(seg.ElemId, idMap), ReportHtml.Escape(why) });

        ReportHtml.Table(sb, "Picks that were dropped", ["Element", "Reason"], rows);
    }

    /// <summary>
    /// Every setting, every run, flagged against the defaults. A saved value that
    /// drifted — a dropped digit, an old settings file left over from a
    /// since-changed default — shows up here, in the report everyone reads before
    /// confirming. Cheaper than bounds and it can't be wrong. This is the tool
    /// whose saved settings actually drifted, so it is exactly the one place that
    /// table must not be missing.
    /// </summary>
    internal static void SettingsTable(StringBuilder sb, SlopeSettings s)
    {
        SlopeSettings d = SlopeSettings.Defaults;
        bool anyChanged = false;

        string Row(double value, double dflt, string fmt)
        {
            string text = value.ToString(fmt);
            if (Math.Abs(value - dflt) > 1.0e-9)
            {
                anyChanged = true;
                text += $"  (default {dflt.ToString(fmt)}) &lt;- CHANGED";
            }
            return ReportHtml.Escape(text);
        }

        List<IReadOnlyList<string>> rows =
        [
            ["Slope", Row(s.SlopeMmPerM, d.SlopeMmPerM, "+0.00;-0.00' mm/m'"),
             "rise per metre of plan run, measured AWAY from the fixed end; negative falls"],
            ["Plan offset tolerance", Row(s.PlanOffsetTolMm, d.PlanOffsetTolMm, "0' mm'"),
             "a picked segment further than this off the run's centreline is a different branch "
             + "line and is dropped"],
            ["Angle tolerance", Row(s.AngleTolDeg, d.AngleTolDeg, "0.00' deg'"),
             "a picked segment further off the run's direction than this is dropped"],
            ["Group tilt tolerance", Row(s.GroupTiltTolMm, d.GroupTiltTolMm, "0.0' mm'"),
             "a group whose joints want to move by more than this much apart is left alone"],
            ["Foreign run length", Row(s.ForeignRunMm, d.ForeignRunMm, "0' mm'"),
             "a connected group containing a pipe longer than this in plan is the rest of the "
             + "system and is never moved - unless the whole group is small (next row)"],
            ["Small assembly size", Row(s.SmallGroupElements, d.SmallGroupElements, "0"),
             "a group containing a long pipe still MOVES when its complete closure has no more "
             + "than this many elements - this is what lets an armover longer than the limit ride "
             + "with the branch"],
            ["Max group size", Row(s.MaxGroupElements, d.MaxGroupElements, "0"),
             "same idea by element count"],
            ["Move warning", Row(s.MaxMoveMm, d.MaxMoveMm, "0' mm'"),
             "warns when anything would move further than this"],
            ["Reconnect tolerance", Row(s.ReconnectTolMm, d.ReconnectTolMm, "0.0' mm'"),
             "how close two open connectors must land before they are joined back up"],
        ];

        ReportHtml.Table(sb, "Settings (Shift+Click the button to change)",
            ["Setting", "Value", "What it does"], rows);

        if (anyChanged)
        {
            ReportHtml.Note(sb,
                "<em>Row(s) marked CHANGED differ from this tool's built-in defaults - worth a "
                + "second look if that wasn't deliberate.</em>");
        }
    }

    /// <summary>
    /// Printed after a run that finished with connectors it could not re-seat.
    /// Every one is a real open joint in the model, so it gets a clickable row
    /// rather than a count.
    /// </summary>
    public static string BuildOpenJoints(
        IReadOnlyList<(long AId, long BId, string Why)> openJoints,
        IReadOnlyDictionary<long, ElementId> idMap)
    {
        StringBuilder sb = new();
        ReportHtml.Note(sb,
            "The re-pitch itself succeeded - everything is where the dry run said it would be - "
            + "but these connectors could not be joined back up. Each is a genuine open joint: "
            + "click through, check it, and close it with the Move/Connect/Align button.");

        List<IReadOnlyList<string>> rows =
        [
            .. openJoints.Select(j => new[]
            {
                Link(j.AId, idMap), Link(j.BId, idMap), ReportHtml.Escape(j.Why),
            })
        ];

        ReportHtml.Table(sb, "Open joints", ["Element", "Element", "Why it stayed open"], rows);
        return sb.ToString();
    }

    /// <summary>
    /// Logs, after the run, every WARNING the silent transaction cleared so
    /// breaking/remaking joints could proceed unattended — a suppressed
    /// disconnect warning is the earliest sign a joint did not reseat.
    /// </summary>
    public static string BuildClearedWarnings(IReadOnlyList<string> cleared)
    {
        StringBuilder sb = new();
        ReportHtml.Note(sb,
            $"{cleared.Count} Revit warning(s) were cleared so breaking and remaking joints could "
            + "proceed without a modal dialog. Only WARNING severity is cleared - a genuine error "
            + "would still have rolled the whole run back - but check these, especially if a joint "
            + "shows up open above.");

        List<IReadOnlyList<string>> rows = [];
        for (int i = 0; i < cleared.Count; i++)
            rows.Add(new[] { (i + 1).ToString(), ReportHtml.Escape(cleared[i]) });

        ReportHtml.Table(sb, "Cleared warnings", ["#", "Description"], rows);
        return sb.ToString();
    }
}
