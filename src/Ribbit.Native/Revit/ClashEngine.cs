using Autodesk.Revit.DB;
using Ribbit.Geometry.Clash;

namespace Ribbit.Native.Revit;

/// <summary>
/// One clashing pair. Replaces the Python's positional
/// <c>[a_elem, b_elem, a_inst, b_inst, volume]</c> list, which every consumer
/// had to index by number.
///
/// A null instance means the element is in the host document; otherwise it is
/// the link instance the element was found through, and the same element can
/// appear once per instance.
/// </summary>
internal sealed record ClashPair(
    Element A,
    Element B,
    RevitLinkInstance? AInstance,
    RevitLinkInstance? BInstance,
    double Volume)
{
    /// <summary>
    /// For a CLEARANCE hit, the gap between the two in feet. Null for a hard
    /// clash, where the solids genuinely intersect.
    ///
    /// Optional and defaulted so every existing construction site keeps meaning
    /// exactly what it meant: a pair without a gap is a hard clash, which is all
    /// the engine could report before clearance existed.
    /// </summary>
    public double? GapFt { get; init; }

    /// <summary>
    /// True when the two are the same size in the same place — a duplicated
    /// element rather than a coordination problem.
    ///
    /// Still reported as a pair, just labelled: a duplicate IS worth knowing
    /// about, and hiding it would make two elements look like one.
    /// </summary>
    public bool IsDuplicate { get; init; }

    /// <summary>True when the two do not touch but are within the clearance.</summary>
    public bool IsClearance => GapFt is not null;

    /// <summary>
    /// Where the two meet, in host feet — the middle of the overlap, or for a
    /// clearance pair the midpoint of the gap. Null when it was not captured or
    /// could not be computed.
    /// </summary>
    public XYZ? Point { get; init; }
}

/// <summary>
/// Pipeline diagnostics. Replaces the Python's <c>info</c> dict, whose keys
/// varied depending on which early-out was taken.
///
/// <see cref="BPassedBroadPhase"/> is the load-bearing one: if it is 0, nothing
/// ever reached the solid test and the exclusion happened at the pre-filter. If
/// it is &gt; 0 while <see cref="ClashPairs"/> is 0, every pair that got through
/// had valid solids on both sides and the Boolean genuinely found no overlap —
/// a real narrow-phase miss, not a pre-filter exclusion.
/// </summary>
internal sealed record ClashInfo(
    int LenA,
    int LenB,
    double PadFt,
    double CellSizeFt,
    int GridCells,
    int CandidatesB,
    int NonCandidatesB,
    int ClashPairs,
    int BPassedBroadPhase,
    HashSet<long> APassedBroadPhase,
    int ASkippedNoBoundingBox,
    string? Reason = null)
{
    /// <summary>
    /// Pairs whose solids genuinely overlapped but not deeply enough to clear
    /// the tolerance.
    ///
    /// An init property rather than another positional field, so the existing
    /// construction sites keep working unchanged — and so a run with no
    /// tolerance reports zero without having to say so.
    ///
    /// Worth surfacing: a tolerance that silently removes most of the report is
    /// indistinguishable from a model that got better, which is exactly the kind
    /// of good news nobody should be given by accident.
    /// </summary>
    public int SuppressedByTolerance { get; init; }
}

/// <summary>
/// What one run found: the diagnostics, and the pairs.
///
/// It carried per-side element lists too, and they are GONE. Nothing ever
/// consumed them, and they deduped on a BARE element id — so in a federated or
/// link run, where two documents can each hold an id 12345, two different
/// elements collapsed into one. They were also filled inconsistently on the
/// early-out paths (one of them assigned Set B's elements to the A side). Every
/// caller works off <see cref="Pairs"/>, which carries the document through
/// <c>AInstance</c>/<c>BInstance</c>; rebuild them from that if they are ever
/// wanted, keyed by (document, id).
/// </summary>
internal sealed record ClashResult(
    ClashInfo Info,
    List<ClashPair> Pairs);

/// <summary>
/// The clash engine. Ported from lib/clashlib/clash_engine.py.
///
/// Broad phase: Set A's padded boxes go into a <see cref="ClashGrid"/>; each Set
/// B box is queried against it. Narrow phase: surviving pairs get a real
/// solid-vs-solid Boolean.
///
/// FAILS OPEN ON A MISSING BOX, per element rather than per run. The source
/// aborted the ENTIRE run the moment any one Set A element had no usable
/// bounding box — so a single equipment placeholder anywhere in a large Set A
/// silently zeroed out a whole multi-link search. A known, surfaced gap
/// (<see cref="ClashInfo.ASkippedNoBoundingBox"/>, which callers must report) is
/// better than a silent zero. An A side that ends up completely empty still
/// aborts: there would be nothing left to search against.
/// </summary>
internal static class ClashEngine
{
    /// <summary>
    /// A padded A box, with the link instance and transform it came through.
    ///
    /// <paramref name="Raw"/> is the SAME box before padding. Clearance measures
    /// the gap between the real extents; measuring between padded boxes would
    /// subtract the pad twice and report every near miss as touching.
    /// </summary>
    private sealed record ABox(
        Element Element, RevitLinkInstance? Instance, Transform? Tr, Aabb Box, Aabb Raw);

    /// <summary>Halfway between two boxes' centres — the clearance pair's "where".</summary>
    private static XYZ Midpoint(Aabb x, Aabb y)
        => new(
            (x.Min.X + x.Max.X + y.Min.X + y.Max.X) / 4.0,
            (x.Min.Y + x.Max.Y + y.Min.Y + y.Max.Y) / 4.0,
            (x.Min.Z + x.Max.Z + y.Min.Z + y.Max.Z) / 4.0);

    public static ClashResult Run(
        Document doc,
        View? activeView,
        IReadOnlyList<Element> setA,
        IReadOnlyList<Element> setB,
        double padFt,
        IReadOnlyDictionary<Document, List<(RevitLinkInstance Instance, Transform Tr)>> linkInstMap,
        Action<int, int>? progress = null,
        bool reportClearance = false,
        double toleranceFt = 0.0,
        bool capturePoint = false,
        bool detectDuplicates = false)
    {
        Options geomOpts = ClashGeometry.MakeGeometryOptions();

        if (setA.Count == 0 || setB.Count == 0)
        {
            return new ClashResult(
                new ClashInfo(setA.Count, setB.Count, padFt, 0, 0, 0, 0, 0, 0, [], 0,
                    "empty input"),
                []);
        }

        // ---- A side: expanded, host-space boxes ----
        List<ABox> aBoxes = [];
        int aUnknown = 0;

        foreach (Element e in setA)
        {
            Document? eDoc = e.Document;

            if (eDoc is null || eDoc.Equals(doc))
            {
                Aabb? bb = ClashGeometry.GetBoundingBox(e, activeView, geomOpts);
                if (bb is null)
                {
                    aUnknown++;
                    continue;
                }
                aBoxes.Add(new ABox(e, null, null, bb.Value.Expand(padFt), bb.Value));
                continue;
            }

            if (!linkInstMap.TryGetValue(eDoc, out List<(RevitLinkInstance Instance, Transform Tr)>? insts)
                || insts.Count == 0)
            {
                aUnknown++;
                continue;
            }

            Aabb? baseBb = ClashGeometry.GetBoundingBox(e, activeView, geomOpts);
            if (baseBb is null)
            {
                aUnknown++;
                continue;
            }

            foreach ((RevitLinkInstance inst, Transform tr) in insts)
            {
                Aabb hostBb = baseBb.Value.Transform(ClashGeometry.AsMapper(tr));
                aBoxes.Add(new ABox(e, inst, tr, hostBb.Expand(padFt), hostBb));
            }
        }

        if (aBoxes.Count == 0)
        {
            return new ClashResult(
                new ClashInfo(setA.Count, setB.Count, padFt, 0, 0, 0, 0, 0, 0, [], aUnknown,
                    $"no A boxes - all {aUnknown} Set A element(s) had no usable bounding box"),
                []);
        }

        List<Aabb> boxes = [.. aBoxes.Select(a => a.Box)];
        ClashGrid grid = ClashGrid.Build(boxes);

        // Caches keyed by ELEMENT ID, not by the Element object.
        //
        // The Python keyed these on the element itself. Revit's Element has no
        // value semantics, so two wrappers around the same element miss the
        // cache - and the solids lookup below sits inside the per-B x per-A-hit
        // loop, where ElementSolids is by far the most expensive call in the
        // tool. A cache that never hits there re-extracts geometry for every
        // candidate PAIR rather than once per element.
        //
        // AND BY DOCUMENT. Ids are per-document, and a federated or link run mixes
        // documents on both sides: keyed on the bare id, a host element and a
        // linked one sharing that id shared ONE cache slot, so whichever was read
        // second was tested with the first one's box and solids. Measured on the
        // Snowdon federation: 655 link elements share an id with a host element,
        // and a real clash disappeared whenever its twin was checked first.
        Dictionary<(Document? Doc, long Id), Aabb?> bbCache = [];
        Dictionary<(Document? Doc, long Id), List<Solid>> solidCache = [];

        Aabb? LocalBox(Element e)
        {
            (Document?, long) key = (e.Document, RevitCompat.IdKey(e.Id));
            if (bbCache.TryGetValue(key, out Aabb? cached))
                return cached;

            Aabb? bb = ClashGeometry.GetBoundingBox(e, activeView, geomOpts);
            bbCache[key] = bb;
            return bb;
        }

        List<Solid> LocalSolids(Element e)
        {
            (Document?, long) key = (e.Document, RevitCompat.IdKey(e.Id));
            if (solidCache.TryGetValue(key, out List<Solid>? cached))
                return cached;

            List<Solid> sols = ClashGeometry.ElementSolids(e, geomOpts);
            solidCache[key] = sols;
            return sols;
        }

        List<(RevitLinkInstance? Instance, Transform? Tr)> InstancesFor(Document? d)
        {
            if (d is null || d.Equals(doc))
                return [(null, null)];

            return linkInstMap.TryGetValue(d, out List<(RevitLinkInstance Instance, Transform Tr)>? insts)
                   && insts.Count > 0
                ? [.. insts.Select(i => ((RevitLinkInstance?)i.Instance, (Transform?)i.Tr))]
                : [(null, null)];
        }

        List<Element> candidatesB = [];
        List<Element> nonCandidatesB = [];
        List<ClashPair> pairs = [];
        int suppressedByTolerance = 0;
        HashSet<long> aabbPassB = [];
        HashSet<long> aabbPassA = [];

        for (int bIndex = 0; bIndex < setB.Count; bIndex++)
        {
            Element e = setB[bIndex];

            try
            {
                progress?.Invoke(bIndex, setB.Count);
            }
            catch (Exception)
            {
                // Progress reporting must never take the run down.
            }

            Aabb? localBb = LocalBox(e);
            if (localBb is null)
            {
                // Fails open, same as the A side: an element whose extent cannot
                // be read is reported as a candidate rather than dropped.
                candidatesB.Add(e);
                continue;
            }

            bool anyClash = false;
            List<Solid>? localBSolids = null;

            foreach ((RevitLinkInstance? bInst, Transform? bTr) in InstancesFor(e.Document))
            {
                Aabb hostBb = bTr is null
                    ? localBb.Value
                    : localBb.Value.Transform(ClashGeometry.AsMapper(bTr));

                Aabb expanded = hostBb.Expand(padFt);

                List<int> hits = grid.Overlapping(expanded, boxes);
                if (hits.Count == 0)
                    continue;

                aabbPassB.Add(RevitCompat.IdKey(e.Id));
                foreach (int i in hits)
                    aabbPassA.Add(RevitCompat.IdKey(aBoxes[i].Element.Id));

                localBSolids ??= LocalSolids(e);
                bool bHasSolids = localBSolids.Count > 0;
                List<Solid> bSolidsHost = bHasSolids
                    ? ClashGeometry.TransformSolids(localBSolids, bTr)
                    : [];

                foreach (int ai in hits)
                {
                    ABox a = aBoxes[ai];
                    List<Solid> localASolids = LocalSolids(a.Element);

                    // No solids on either side means no Boolean is possible, so
                    // the padded-box hit IS the answer - reported with volume 0
                    // rather than dropped.
                    if (localASolids.Count == 0 || !bHasSolids)
                    {
                        anyClash = true;
                        pairs.Add(new ClashPair(a.Element, e, a.Instance, bInst, 0.0));
                        continue;
                    }

                    List<Solid> aSolidsHost = ClashGeometry.TransformSolids(localASolids, a.Tr);

                    bool pairHit = false;
                    bool tooShallow = false;

                    foreach (Solid sa in aSolidsHost)
                    {
                        if (pairHit)
                            break;

                        foreach (Solid sb in bSolidsHost)
                        {
                            ClashGeometry.SolidHit hit = ClashGeometry.Intersect(
                                sa, sb,
                                wantDepth: toleranceFt > 0.0,
                                wantPoint: capturePoint,
                                wantDuplicate: detectDuplicates,
                                depthCutoffFt: toleranceFt > 0.0 ? toleranceFt : null);

                            double vol = hit.Volume;
                            double depth = hit.MinDepthFt;
                            XYZ? point = hit.Point;

                            if (vol <= 0.0)
                                continue;

                            // Below tolerance: keep looking rather than breaking.
                            // An element can have several solids, and a shallow
                            // graze on one of them says nothing about whether
                            // another cuts deep. Breaking here would let the
                            // first solid examined decide the pair.
                            if (toleranceFt > 0.0 && depth < toleranceFt)
                            {
                                tooShallow = true;
                                continue;
                            }

                            anyClash = true;
                            pairs.Add(new ClashPair(a.Element, e, a.Instance, bInst, vol)
                            {
                                Point = point,
                                IsDuplicate = hit.DuplicateRatio >= ClashGeometry.DuplicateThreshold,
                            });
                            pairHit = true;
                            break;
                        }
                    }

                    // Counted only when NO solid pair survived, so a pair with
                    // one shallow and one deep overlap is reported, not counted
                    // as suppressed.
                    if (!pairHit && tooShallow)
                    {
                        suppressedByTolerance++;
                        continue;
                    }

                    // CLEARANCE. The padded boxes overlapped or this pair would
                    // never have been a candidate, but the solids do not touch.
                    //
                    // Without this the pad is inert for anything with geometry:
                    // it widens the candidate search and then the solid test
                    // throws the near misses away, so a 100mm pad returns exactly
                    // what a 0mm pad returns. That made "counted as a clash when
                    // the two come within this distance" untrue.
                    //
                    // The gap is measured between the UNPADDED host boxes, and is
                    // therefore a box gap, not a surface gap - it under-estimates
                    // the true separation and so errs towards reporting. Recorded
                    // as a clearance pair, distinct from a hard clash.
                    if (!pairHit && reportClearance && padFt > 0.0)
                    {
                        double gap = a.Raw.DistanceTo(hostBb);
                        if (gap <= padFt)
                        {
                            anyClash = true;

                            // Midway between the two extents: there is no overlap
                            // to take a centroid of, and the middle of the gap is
                            // the honest answer to "where is this near miss".
                            XYZ? mid = capturePoint ? Midpoint(a.Raw, hostBb) : null;

                            pairs.Add(new ClashPair(a.Element, e, a.Instance, bInst, 0.0)
                            {
                                GapFt = gap,
                                Point = mid,
                            });
                        }
                    }
                }
            }

            if (anyClash)
                candidatesB.Add(e);
            else
                nonCandidatesB.Add(e);
        }

        ClashInfo info = new(
            LenA: setA.Count,
            LenB: setB.Count,
            PadFt: padFt,
            CellSizeFt: grid.CellSizeFt,
            GridCells: grid.CellCount,
            CandidatesB: candidatesB.Count,
            NonCandidatesB: nonCandidatesB.Count,
            ClashPairs: pairs.Count,
            BPassedBroadPhase: aabbPassB.Count,
            APassedBroadPhase: aabbPassA,
            ASkippedNoBoundingBox: aUnknown)
        {
            SuppressedByTolerance = suppressedByTolerance,
        };

        return new ClashResult(info, pairs);
    }
}
