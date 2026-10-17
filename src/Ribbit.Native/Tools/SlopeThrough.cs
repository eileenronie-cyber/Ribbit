using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using Ribbit.Geometry;
using Ribbit.Native.Revit;
using Ribbit.Native.Ui;
using OperationCanceledException = Autodesk.Revit.Exceptions.OperationCanceledException;

namespace Ribbit.Native.Tools;

/// <summary>
/// One straight leg of the developed branch: a set of collinear pipe segments,
/// plus how it was solved and what hangs off it.
///
/// Legs are the nodes of a tree rooted at the fixed end. Every leg but the root
/// has a PARENT leg and the CORNER elbow that joins them; the root anchors at
/// the fixed end itself.
/// </summary>
internal sealed class Leg(int index, List<Element> elements)
{
    public int Index { get; } = index;

    /// <summary>Pipe elements, one plan axis.</summary>
    public List<Element> Elements { get; } = elements;

    public SlopeRun? Run { get; set; }
    public RePitch? Pitch { get; set; }
    public List<SlopeGroup> Groups { get; set; } = [];
    public List<Joint> Joints { get; set; } = [];

    /// <summary>
    /// Picks dropped before the run was assembled — a link instance, a fitting
    /// picked instead of pipe, no readable axis. The Python keeps these and the
    /// run-level rejects in one list; they are split here only because the two
    /// carry different payloads. Both belong in the report.
    /// </summary>
    public List<SprigEngine.Rejected> RejectedPicks { get; set; } = [];

    /// <summary>Segments BuildRun dropped — steep, off-axis, or overlapping.</summary>
    public List<(SlopeSegment Segment, string Reason)> RejectedSegments { get; set; } = [];

    public HashSet<long> BranchIds { get; set; } = [];

    public Leg? Parent { get; set; }
    public long? CornerElemId { get; set; }
    public Vec3? CornerPoint { get; set; }

    /// <summary>
    /// Set when this leg meets its parent at a TEE or CROSS rather than an
    /// elbow: a selected branch teed off the main (or the main, when the fixed
    /// end is out on a branch). Null for an elbow corner.
    /// </summary>
    public Junction? Junction { get; set; }

    /// <summary>
    /// Corner elbows only: the point the roll turns about - the elbow's
    /// PARENT-side connector after the pitch rotation - so the roll cannot move
    /// the parent's own joint. Null when the elbow could not be read.
    /// </summary>
    public Vec3? CornerRollPivot { get; set; }

    /// <summary>
    /// Corner elbows only: how far the elbow drops, chosen so its parent-side
    /// connector lands exactly on the parent's new line. Null when the elbow
    /// could not be read (the chord-midpoint drop is used then).
    /// </summary>
    public double? CornerDrop { get; set; }

    public string? Error { get; set; }

    /// <summary>
    /// Degrees still off, on THIS leg's own connector, after the best available
    /// roll of the shared corner elbow. Null until that corner is attached.
    /// </summary>
    public double? CornerResidualDeg { get; set; }

    /// <summary>
    /// ROOT leg only: the plan for rebuilding the FIXED end's own fitting fresh
    /// after the re-pitch, or null if the fixed pick isn't a plain 2-connector
    /// fitting directly on the branch.
    /// </summary>
    public FixedRebuild? FixedRebuild { get; set; }

    /// <summary>
    /// ROOT leg only, and the 3-connector counterpart of
    /// <see cref="FixedRebuild"/> — exactly one of the two ever applies.
    ///
    /// Only safe to plan because <see cref="SlopeThrough.RebuildFixedTee"/> is
    /// survivable: Revit refuses roughly two thirds of these, and before that
    /// refusal was made non-fatal, planning one turned a cosmetic sliver into a
    /// whole-run rollback.
    /// </summary>
    public FixedTeeRebuild? FixedTeeRebuild { get; set; }

    public bool Ok => Error is null && Pitch is not null;
}

/// <summary>
/// One connector of a junction fitting, read before anything moves: where it is,
/// which way it points, what it is joined to, and which pipe that path finally
/// reaches once any straight-through fitting in between (the transition Revit
/// slips in where the branch is smaller than the main, a coupling, a valve) has
/// been stepped through.
/// </summary>
internal sealed class JunctionPort(
    Vec3 origin, Vec3 direction, long? neighbourId, List<long> passThrough, long? reachedId)
{
    public Vec3 Origin { get; } = origin;

    /// <summary>Out of the fitting, as Revit has it now.</summary>
    public Vec3 Direction { get; } = direction;

    /// <summary>The element joined straight to this connector, or null when open.</summary>
    public long? NeighbourId { get; } = neighbourId;

    /// <summary>Straight-through fittings between this connector and <see cref="ReachedId"/>.</summary>
    public List<long> PassThrough { get; } = passThrough;

    /// <summary>The pipe this path reaches, or null when it reaches anything else.</summary>
    public long? ReachedId { get; } = reachedId;

    /// <summary>The leg that pipe belongs to, or null when it is not on any leg.</summary>
    public int? LegIndex { get; set; }
}

/// <summary>
/// A tee or cross joining two or more legs. Unlike a corner elbow, which has
/// exactly one connector per leg, a junction usually carries its PARENT leg
/// straight through (two connectors on one line) with the child teed off it.
/// </summary>
internal sealed class Junction(long fittingId, List<JunctionPort> ports)
{
    public long FittingId { get; } = fittingId;
    public List<JunctionPort> Ports { get; } = ports;

    public List<int> Legs => [.. Ports.Where(p => p.LegIndex is not null)
                                      .Select(p => p.LegIndex!.Value).Distinct()];
}

/// <summary>
/// What is needed to delete the fixed end's old fitting and build a fresh one in
/// its place.
/// </summary>
internal sealed record FixedRebuild(
    long ElbowId, ElementId TypeId, XYZ OtherOrigin, long OtherElemId);

/// <summary>
/// The same thing for a TEE — three connectors instead of two.
///
/// WHY A SEPARATE SHAPE. A re-pitched run leaves its anchored end at the same
/// POINT but at a new ANGLE, so the pipe's end face and the fitting's socket
/// face no longer lie parallel: they share a centre and tilt apart, leaving a
/// wedge-shaped sliver on one side. Revit's own Trim never shows this because it
/// makes Revit re-create the fitting; deleting and rebuilding is what closes it,
/// and it is exactly what <see cref="FixedRebuild"/> already does for an elbow.
///
/// A tee additionally carries the two RUN connections along the branch line, and
/// those must come back attached to the same elements at the same points — which
/// is the reason a tee was originally left alone rather than rebuilt.
/// </summary>
internal sealed record FixedTeeRebuild(
    long TeeId,
    ElementId TypeId,
    XYZ BranchOrigin, long BranchElemId,
    XYZ Run1Origin, long Run1ElemId,
    XYZ Run2Origin, long Run2ElemId);

/// <summary>
/// The whole developed branch: its ordered legs, the combined joint list, and
/// whether it could be built at all.
/// </summary>
internal sealed class SlopePlan
{
    public List<Leg> Legs { get; set; } = [];

    /// <summary>Every joint, across every leg + corner.</summary>
    public List<Joint> Joints { get; set; } = [];

    public Dictionary<long, ElementId> IdMap { get; } = [];
    public string? Error { get; set; }
    public List<long> SeedIds { get; set; } = [];
    public long? FixedId { get; set; }

    /// <summary>
    /// True when discovery stopped because the leg cap was reached with more of
    /// the branch still unwalked — a truncation, not a completion.
    /// </summary>
    public bool HitLegCap { get; set; }

    /// <summary>
    /// Legs that were discovered but have no corner path back to the fixed end,
    /// so the ordering walk never reached them.
    ///
    /// They are correctly excluded — a run with no elbow chain to the tie-in
    /// cannot take a developed slope from it — but they used to be dropped by
    /// overwriting the leg list, which discarded the fact that they existed at
    /// all. Window-selecting two separate branch runs then sloped one and said
    /// nothing whatever about the other.
    /// </summary>
    public List<Leg> UnreachedLegs { get; set; } = [];

    /// <summary>
    /// True when the second pick landed ON the branch: the click then only names
    /// the fixed END, and nothing is pinned. False when it landed outside, which
    /// additionally pins that element's group.
    /// </summary>
    public bool FixedInBranch { get; set; }

    /// <summary>Every tee/cross joining two or more of the selected legs, by fitting id.</summary>
    public Dictionary<long, Junction> Junctions { get; set; } = [];

    /// <summary>
    /// Unselected branches carried along as one piece by the fitting they hang
    /// off, for the report and the confirmation: (fitting or group root, element count).
    /// </summary>
    public List<(long RootId, int Elements)> CarriedBranches { get; set; } = [];

    internal Dictionary<int, List<(int Other, long ElbowId, Vec3 Corner)>> Adjacency { get; set; } = [];
}

/// <summary>
/// Slope Through — one sloped branch line that turns corners. Ported from
/// lib/slopelib/through.py.
///
/// THE MODEL — A DEVELOPED RUN. Slope on a drawing is rise over run measured
/// ALONG the pipe. Across a corner the pipe keeps going, so the run that matters
/// is the DEVELOPED (plan) length. Every point's target elevation is
/// `fixed elevation + slope x developed plan distance from the fixed end`, which
/// falls out of solving each leg in turn and CHAINING the anchor: leg 1 anchors
/// at the fixed end and keeps its elevation; every leg after it anchors at the
/// elbow it shares with the leg before, and takes its anchor ELEVATION from
/// where that upstream leg leaves the corner.
///
/// STRAIGHT RUNS TOO — a branch with no corners comes out as ONE leg, and a
/// one-leg solve IS Slope Branch: the same BuildRun, the same RePitch, the same
/// CollectGroups, the same applier. Nothing about how one leg is re-pitched is
/// reimplemented here. What is new is only the topology.
///
/// WHAT STOPS IT WALKING THE WHOLE FLOOR — discovery never crosses the fixed
/// element you pick, and it only turns corners on its own through two-connector
/// elbows. A branch almost always TEES into its cross main, and a tee is not
/// walked through as a corner, so the main is left alone.
///
/// A TREE OF SELECTED BRANCHES — a tee or cross IS crossed when the pipe on the
/// other side is on a leg the user selected too. So window-selecting a main and
/// the branches teed off it slopes them all as one developed tree: every point
/// sits at `fixed elevation + slope x developed distance`, measured along the
/// main to each tee and out along the branch. A branch NOT selected is carried
/// along as one piece by its tee, keeping its own slope - but only when it is
/// provably a dead end (<see cref="CheckCarriedBranches"/>).
/// </summary>
internal static class SlopeThrough
{
    /// <summary>
    /// More legs than this is not one tree, it is a mistake in the pick — stop
    /// rather than swallow a system. A tree that reaches it is REFUSED, never
    /// solved as far as it got: discovery takes the picks in whatever order they
    /// came, so a truncated tree can be missing its own main. Proven 2026-10-17:
    /// at the old cap of 40, a main with 41 selected branches filled the cap with
    /// branches, never reached the main, and sloped ONE branch and committed it.
    /// A floor of sprinkler branch lines is hundreds of legs, so the cap is
    /// sized for that. Settable only so the in-Revit suite can hit it cheaply.
    /// </summary>
    internal static int LegCap { get; set; } = DefaultLegCap;

    internal const int DefaultLegCap = 400;

    /// <summary>
    /// A run of more pipes than this is not a branch line, it is a mistake —
    /// stop walking rather than swallow a whole floor.
    /// </summary>
    private const int ExtendCap = 200;

    // ------------------------------------------------------------------ picking

    /// <summary>
    /// (element, click point) for the second pick, or nulls.
    ///
    /// PickObject rather than PickPoint: a Reference carries a real 3D
    /// GlobalPoint on whatever was hit, which works identically in plan, section
    /// and 3D. Here BOTH parts matter — the point says which end is fixed, and
    /// the element decides whether that end also gets pinned.
    /// </summary>
    public static (Element? Element, XYZ? Point) PickFixed(Document doc, IPicker picker, string prompt)
    {
        (Element? elem, XYZ? point) = picker.PickElement(prompt, new AnyElement());
        if (elem is null)
            return (null, null);

        elem = Picking.ResolveHost(doc, elem);

        // Some pick/view combinations hand back a Reference with no GlobalPoint;
        // the element's own midpoint still names an end.
        point ??= ReferencePoint(elem)?.ToXYZ();

        return (elem, point);
    }

    /// <summary>(seed pipes, fixed element, fixed point) or nulls on Esc.</summary>
    /// <summary>The fixed-end pick takes anything: a main, a fitting, or the branch itself.</summary>
    private sealed class AnyElement : Autodesk.Revit.UI.Selection.ISelectionFilter
    {
        public bool AllowElement(Element elem) => true;

        public bool AllowReference(Reference reference, XYZ position) => true;
    }

    /// <summary>(seed pipes, fixed element, fixed point) or nulls on Esc.</summary>
    public static (List<Element>? Seeds, Element? Fixed, XYZ? Point) Pick(Document doc, IPicker picker)
    {
        List<Element>? seeds = picker.PickElements(
            "Slope Through: pick the BRANCH LINE - ONE pipe is enough, the rest of the run and "
            + "every leg past the elbows is found automatically. To slope a MAIN and its branches "
            + "together, pick the main plus one pipe of every branch that should take the slope "
            + "(window-selecting works); a branch left out moves with its tee as one piece "
            + "(Finish when done; Esc cancels)");
        if (seeds is null || seeds.Count == 0)
            return (null, null, null);

        (Element? fixedElem, XYZ? point) = PickFixed(doc, picker,
            "Now click the END THAT MUST NOT MOVE. Either click the cross main or the tee/elbow "
            + "the branch ties into - that gets PINNED - or just click the branch itself on the "
            + "half that stays put (Esc cancels)");
        if (fixedElem is null || point is null)
            return (null, null, null);

        return ([.. seeds.Select(s => Picking.ResolveHost(doc, s))], fixedElem, point);
    }

    // ----------------------------------------------------------------- geometry

    /// <summary>
    /// A single point standing for the reference element. Only its position
    /// ALONG the run matters — it decides which end is anchored — so the
    /// midpoint of a long cross main is as good as any.
    /// </summary>
    public static Vec3? ReferencePoint(Element elem)
    {
        (Vec3? p0, Vec3? p1, _) = SlopeEngine.GetAxisPoints(elem);
        if (p0 is not null && p1 is not null)
            return p0.Value.Add(p1.Value).Multiply(0.5);

        if ((elem.Location as LocationPoint)?.Point is XYZ point)
            return point.ToVec3();

        List<Connector> conns = MepConnectors.GetEndConnectors(elem);
        if (conns.Count > 0)
        {
            Vec3 total = Vec3.Zero;
            foreach (Connector c in conns)
                total = total.Add(c.Origin.ToVec3());
            return total.Multiply(1.0 / conns.Count);
        }

        return null;
    }

    /// <summary>Every element joined to this one at an End connector.</summary>
    public static List<Element> Connected(Element elem)
    {
        List<Element> outp = [];
        foreach (Connector conn in MepConnectors.GetEndConnectors(elem))
        {
            ConnectorSet refs;
            try
            {
                refs = conn.AllRefs;
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException)
            {
                continue;
            }

            foreach (Connector other in refs)
            {
                Element? owner;
                try
                {
                    if (other.ConnectorType != ConnectorType.End)
                        continue;
                    owner = other.Owner;
                }
                catch (Autodesk.Revit.Exceptions.ApplicationException)
                {
                    continue;
                }

                if (owner is not null)
                    outp.Add(owner);
            }
        }
        return outp;
    }

    /// <summary>
    /// Every pipe on the same straight run as the seed, seed included.
    ///
    /// Walks the connections outward, stepping THROUGH fittings without ever
    /// collecting them — the fittings are found again later as groups — and
    /// keeping any pipe that is parallel to the seed, sits on its axis line, and
    /// is not steep. A tee's sprig fails by being steep, a cross main fails by
    /// being perpendicular, so the walk stops itself at the ends of the branch
    /// without needing to know what any fitting is.
    ///
    /// Two rules live testing forced: STEEPNESS rather than exact verticality (a
    /// sprig canted a hair off plumb has a sliver of plan direction that can line
    /// up with the run, and sits on the axis by construction), and fittings go
    /// through the FRONTIER, never into the collection (stepping exactly one
    /// fitting deep let a tee-plus-coupling gap block the walk, and a coupling
    /// reached as the far side of another fitting slipped INTO the collection
    /// because two end connectors on the run axis satisfy the same test a short
    /// pipe does).
    /// </summary>
    public static (List<Element> Elements, string? Reason) ExtendRun(
        Element seed, SlopeParams parameters)
    {
        double planOffsetTol = parameters.PlanOffsetTol;

        (Vec3? p0, Vec3? p1, _) = SlopeEngine.GetAxisPoints(seed);
        if (p0 is null || p1 is null)
            return ([], $"{SprigEngine.DescribeElem(seed)} has no straight axis to follow");

        Vec3 plan = new(p1.Value.X - p0.Value.X, p1.Value.Y - p0.Value.Y, 0.0);
        if (plan.GetLength() < SlopeSolver.LenEps
            || Math.Abs(p1.Value.Z - p0.Value.Z) > plan.GetLength() * SlopeSolver.SteepRate)
        {
            return ([],
                $"{SprigEngine.DescribeElem(seed)} is vertical or steeper than "
                + $"{SlopeSolver.SteepDeg:F0} deg - pick a horizontal pipe on the branch, not a "
                + "riser or a sprig");
        }

        Vec3 axis = plan.Normalize();
        Vec3 origin = new(p0.Value.X, p0.Value.Y, 0.0);
        double cosTol = Math.Cos(parameters.AngleTolDeg * Math.PI / 180.0);

        bool OnAxis(Vec3 point)
        {
            double station = (point.X - origin.X) * axis.X + (point.Y - origin.Y) * axis.Y;
            double px = origin.X + axis.X * station;
            double py = origin.Y + axis.Y * station;
            return Math.Sqrt((point.X - px) * (point.X - px) + (point.Y - py) * (point.Y - py))
                   <= planOffsetTol;
        }

        bool Belongs(Element elem)
        {
            (Vec3? q0, Vec3? q1, _) = SlopeEngine.GetAxisPoints(elem);
            if (q0 is null || q1 is null)
                return false;

            Vec3 qPlan = new(q1.Value.X - q0.Value.X, q1.Value.Y - q0.Value.Y, 0.0);
            if (qPlan.GetLength() < SlopeSolver.LenEps)
                return false;
            if (Math.Abs(q1.Value.Z - q0.Value.Z) > qPlan.GetLength() * SlopeSolver.SteepRate)
                return false;
            if (Math.Abs(qPlan.Normalize().DotProduct(axis)) < cosTol)
                return false;

            return OnAxis(q0.Value) && OnAxis(q1.Value);
        }

        long seedId = RevitCompat.IdKey(seed.Id);
        Dictionary<long, Element> collected = new() { [seedId] = seed };
        HashSet<long> visited = [seedId];
        Stack<Element> frontier = new();
        frontier.Push(seed);

        while (frontier.Count > 0 && collected.Count < ExtendCap)
        {
            Element elem = frontier.Pop();
            foreach (Element neighbour in Connected(elem))
            {
                long nid = RevitCompat.IdKey(neighbour.Id);
                if (!visited.Add(nid))
                    continue;

                if (SlopeEngine.IsFittingLike(neighbour))
                {
                    // Pass-through node: walk beyond it, never collect it.
                    frontier.Push(neighbour);
                }
                else if (Belongs(neighbour))
                {
                    collected[nid] = neighbour;
                    frontier.Push(neighbour);
                }
            }
        }

        return ([.. collected.Values], null);
    }

    private static Vec3 Avg(List<Vec3> points)
    {
        Vec3 total = Vec3.Zero;
        foreach (Vec3 p in points)
            total = total.Add(p);
        return total.Multiply(1.0 / points.Count);
    }

    /// <summary>
    /// A pipe with a real plan direction and shallower than the steep cutoff —
    /// i.e. a candidate leg, never a riser or sprig.
    /// </summary>
    /// <summary>Two pipes of the same diameter (within 0.5 mm).</summary>
    private static bool SameSize(Element a, Element b)
        => a is Autodesk.Revit.DB.Plumbing.Pipe pa && b is Autodesk.Revit.DB.Plumbing.Pipe pb
           && Math.Abs(pa.Diameter - pb.Diameter) < 0.5 / 304.8;

    private static bool HorizontalPipe(Element elem)
    {
        if (elem is not Autodesk.Revit.DB.Plumbing.Pipe)
            return false;

        (Vec3? p0, Vec3? p1, _) = SlopeEngine.GetAxisPoints(elem);
        if (p0 is null || p1 is null)
            return false;

        return !SlopeSolver.IsSteep(new SlopeSegment(0, p0.Value, p1.Value));
    }

    /// <summary>
    /// Every elbow at this leg's ends that turns into another horizontal leg.
    ///
    /// Only two-connector fittings count. A tee is skipped: its straight-through
    /// was already collinear and taken into this same leg by ExtendRun, and its
    /// takeoff is an armover the grouping handles. The fixed element is never
    /// crossed.
    /// </summary>
    private static List<(Element Far, long ElbowId, Vec3 Corner)> CornerCandidates(
        IReadOnlyList<Element> legElems, ISet<long> legIds, long? fixedId)
    {
        List<(Element, long, Vec3)> outp = [];
        HashSet<long> seenFar = [];

        foreach (Element elem in legElems)
        {
            foreach (Element fitting in Connected(elem))
            {
                if (!SlopeEngine.IsFittingLike(fitting))
                    continue;

                long fid = RevitCompat.IdKey(fitting.Id);
                if (fid == fixedId)
                    continue;

                // An elbow has exactly two end connectors; a tee has three.
                List<Connector> fittingConns = MepConnectors.GetEndConnectors(fitting);
                if (fittingConns.Count != 2)
                    continue;

                Vec3 center = Avg([.. fittingConns.Select(c => c.Origin.ToVec3())]);

                foreach (Element far in Connected(fitting))
                {
                    long farId = RevitCompat.IdKey(far.Id);
                    if (legIds.Contains(farId) || seenFar.Contains(farId))
                        continue;

                    // The clicked fixed element is kept OUT of the branch - a main the
                    // branch L-joins through an elbow must be pinned, never walked as a
                    // leg. But the prompt also invites "click the branch itself on the
                    // half that stays put", and a click on a DOWNSTREAM leg used to land
                    // here too: that whole leg was pinned and the rest sloped backwards
                    // toward the main (in-Revit suite: "Only ONE leg", leg 1 re-pitched
                    // from the corner). A pipe the same size as the leg it meets is more
                    // branch, not a main - so that one is walked, and holds the fixed end.
                    if (farId == fixedId && !SameSize(far, elem))
                        continue;
                    if (!HorizontalPipe(far))
                        continue;

                    seenFar.Add(farId);
                    outp.Add((far, fid, center));
                }
            }
        }
        return outp;
    }

    // ---------------------------------------------------------------- discovery

    /// <summary>
    /// Build the whole developed branch from the picked pipe(s), walking leg by
    /// leg. Several seeds are allowed so window-selecting the whole branch works
    /// exactly like picking one pipe: each seed's leg is built once and the rest
    /// are skipped as already-assigned.
    /// </summary>
    public static SlopePlan Discover(
        IReadOnlyList<Element> seeds, Element fixedElem, SlopeParams parameters)
    {
        SlopePlan plan = new()
        {
            SeedIds = [.. seeds.Select(s => RevitCompat.IdKey(s.Id))],
            FixedId = RevitCompat.IdKey(fixedElem.Id),
        };

        List<Leg> legs = [];
        Dictionary<long, int> legOfPipe = [];
        List<(int FromIndex, long FarId, long ElbowId, Vec3 Corner)> rawEdges = [];
        Stack<Element> frontier = new();
        foreach (Element s in seeds)
            frontier.Push(s);

        while (frontier.Count > 0 && legs.Count < LegCap)
        {
            Element pipe = frontier.Pop();
            long pid = RevitCompat.IdKey(pipe.Id);
            if (legOfPipe.ContainsKey(pid))
                continue;

            (List<Element> elems, string? reason) = ExtendRun(pipe, parameters);
            if (reason is not null)
            {
                // A seed we cannot start a run from is simply not a leg; skip it.
                continue;
            }

            int legIndex = legs.Count;
            legs.Add(new Leg(legIndex, elems));

            HashSet<long> legIds = [.. elems.Select(e => RevitCompat.IdKey(e.Id))];
            foreach (long eId in legIds)
                legOfPipe[eId] = legIndex;

            foreach ((Element far, long elbowId, Vec3 corner) in
                     CornerCandidates(elems, legIds, plan.FixedId))
            {
                long farId = RevitCompat.IdKey(far.Id);
                rawEdges.Add((legIndex, farId, elbowId, corner));
                if (!legOfPipe.ContainsKey(farId))
                    frontier.Push(far);
            }
        }

        Dictionary<int, List<(int Other, long ElbowId, Vec3 Corner)>> adjacency = [];
        foreach (Leg leg in legs)
            adjacency[leg.Index] = [];

        foreach ((int fromIndex, long farId, long elbowId, Vec3 corner) in rawEdges)
        {
            if (!legOfPipe.TryGetValue(farId, out int toIndex) || toIndex == fromIndex)
                continue;
            Link(adjacency, fromIndex, toIndex, elbowId, corner);
        }

        // Tees and crosses. Only ever an EDGE between legs already found - never a
        // way to find new ones - so a branch is sloped exactly when it was
        // selected (or turns a corner off one that was), and an unselected one
        // teed off the main is left to its tee's group.
        plan.Junctions = FindJunctions(legs, legOfPipe, plan.FixedId);
        foreach (Junction junction in plan.Junctions.Values)
        {
            List<int> touched = junction.Legs;
            Vec3 center = Avg([.. junction.Ports.Select(p => p.Origin)]);
            for (int a = 0; a < touched.Count; a++)
            {
                for (int b = a + 1; b < touched.Count; b++)
                    Link(adjacency, touched[a], touched[b], junction.FittingId, center);
            }
        }

        plan.Legs = legs;
        plan.Adjacency = adjacency;

        // The frontier can also be empty because discovery genuinely finished at
        // the same moment the cap was reached - only call it a truncation when
        // there was more left to walk.
        plan.HitLegCap = legs.Count >= LegCap
                         && frontier.Any(e => !legOfPipe.ContainsKey(RevitCompat.IdKey(e.Id)));

        if (legs.Count == 0)
        {
            plan.Error = "no horizontal pipe leg could be built from the pick - pick a sloped "
                         + "horizontal pipe on the branch";
        }
        else if (plan.HitLegCap)
        {
            plan.Error = $"the selection has more than {LegCap} legs, so the tree was cut short "
                         + "before all of it was found - a part-found tree can be missing its own "
                         + "main, and sloping it would put the wrong pipe at the fixed end. Nothing "
                         + "was changed. Select a smaller part of the system.";
        }

        return plan;
    }

    /// <summary>Record an undirected corner edge once per direction.</summary>
    private static void Link(
        Dictionary<int, List<(int Other, long ElbowId, Vec3 Corner)>> adjacency,
        int i, int j, long elbowId, Vec3 corner)
    {
        foreach ((int a, int b) in new[] { (i, j), (j, i) })
        {
            if (!adjacency.TryGetValue(a, out List<(int, long, Vec3)>? list))
                adjacency[a] = list = [];

            if (!list.Any(e => e.Item1 == b && e.Item2 == elbowId))
                list.Add((b, elbowId, corner));
        }
    }

    /// <summary>How many straight-through fittings a junction port may step through.</summary>
    private const int PassThroughCap = 3;

    /// <summary>
    /// Every fitting with three or more connectors on a leg that joins it to at
    /// least one OTHER leg. The fixed element is never one: a tee clicked as the
    /// fixed end is pinned, not walked through.
    /// </summary>
    private static Dictionary<long, Junction> FindJunctions(
        List<Leg> legs, Dictionary<long, int> legOfPipe, long? fixedId)
    {
        Dictionary<long, Junction> outp = [];
        HashSet<long> scanned = [];

        foreach (Leg leg in legs)
        {
            foreach (Element elem in leg.Elements)
            {
                foreach (Element fitting in Connected(elem))
                {
                    if (!SlopeEngine.IsFittingLike(fitting))
                        continue;

                    long fid = RevitCompat.IdKey(fitting.Id);
                    if (fid == fixedId || !scanned.Add(fid))
                        continue;
                    if (MepConnectors.GetEndConnectors(fitting).Count < 3)
                        continue;

                    Junction junction = ScanJunction(fitting);
                    foreach (JunctionPort port in junction.Ports)
                    {
                        if (port.ReachedId is long rid && legOfPipe.TryGetValue(rid, out int li))
                            port.LegIndex = li;
                    }

                    if (junction.Legs.Count >= 2)
                        outp[fid] = junction;
                }
            }
        }
        return outp;
    }

    /// <summary>Every connector of a tee/cross, followed out to the pipe it reaches.</summary>
    private static Junction ScanJunction(Element fitting)
    {
        List<JunctionPort> ports = [];
        foreach (Connector conn in MepConnectors.GetEndConnectors(fitting))
        {
            Vec3 direction = SlopeEngine.ConnectorDirection(conn) ?? Vec3.Zero;
            Connector? entry = ConnectorNeighbor(conn);
            long? neighbourId = entry is null ? null : RevitCompat.IdKey(entry.Owner.Id);
            Element? reached = entry?.Owner;

            List<long> through = [];
            while (reached is not null && entry is not null && through.Count < PassThroughCap
                   && SlopeEngine.IsFittingLike(reached) && IsStraightThrough(reached))
            {
                through.Add(RevitCompat.IdKey(reached.Id));
                Connector? exit = MepConnectors.GetEndConnectors(reached)
                    .OrderByDescending(c => c.Origin.DistanceTo(entry.Origin))
                    .FirstOrDefault();
                entry = exit is null ? null : ConnectorNeighbor(exit);
                reached = entry?.Owner;
            }

            long? reachedId = reached is Autodesk.Revit.DB.Plumbing.Pipe
                ? RevitCompat.IdKey(reached.Id)
                : null;

            ports.Add(new JunctionPort(conn.Origin.ToVec3(), direction, neighbourId, through, reachedId));
        }
        return new Junction(RevitCompat.IdKey(fitting.Id), ports);
    }

    /// <summary>A two-connector fitting whose connectors face opposite ways: a coupling, reducer, valve.</summary>
    private static bool IsStraightThrough(Element fitting)
    {
        List<Connector> conns = MepConnectors.GetEndConnectors(fitting);
        return conns.Count == 2
               && Math.Abs(conns[0].CoordinateSystem.BasisZ.DotProduct(conns[1].CoordinateSystem.BasisZ))
               > StraightThroughCos;
    }

    /// <summary>The leg nearest the fixed point — leg 1, anchored at the tie-in.</summary>
    private static Leg? RootLeg(SlopePlan plan, Vec3 fixedPoint)
    {
        Leg? best = null;
        double? bestD = null;

        foreach (Leg leg in plan.Legs)
        {
            foreach (Element elem in leg.Elements)
            {
                (Vec3? p0, Vec3? p1, _) = SlopeEngine.GetAxisPoints(elem);
                if (p0 is null || p1 is null)
                    continue;

                foreach (Vec3 pt in new[] { p0.Value, p1.Value })
                {
                    double d = pt.DistanceTo(fixedPoint);
                    if (bestD is null || d < bestD)
                    {
                        best = leg;
                        bestD = d;
                    }
                }
            }
        }
        return best;
    }

    // -------------------------------------------------------------------- solve

    /// <summary>
    /// Order the legs from the fixed end and solve each one, chaining the corner
    /// elevations. Mutates the legs in place.
    ///
    /// ONE PICK, TWO MEANINGS — whether the second pick landed on the branch
    /// decides what it means, so there is no mode to choose. ON the branch it is
    /// a POINT: leg 1 anchors at whichever end of it the click is nearer, and
    /// nothing is pinned. OFF the branch it is the FIXED ELEMENT: leg 1 still
    /// anchors at the end nearest the click, and that element's whole group is
    /// additionally pinned as never-move. An element that is genuinely ambiguous
    /// falls to the PINNED reading, because declining to move something the user
    /// called fixed is the safe way to be wrong.
    /// </summary>
    public static SlopePlan Solve(
        Document doc, SlopePlan plan, Element fixedElem, Vec3 fixedPoint,
        SlopeParams parameters, SlopeGroupParams groupParams)
    {
        if (plan.Error is not null)
            return plan;

        HashSet<long> discovered = [];
        foreach (Leg leg in plan.Legs)
        {
            foreach (Element e in leg.Elements)
                discovered.Add(RevitCompat.IdKey(e.Id));
        }
        plan.FixedInBranch = plan.FixedId is not null && discovered.Contains(plan.FixedId.Value);

        Leg? root = RootLeg(plan, fixedPoint);
        if (root is null)
        {
            plan.Error = "no leg could be matched to the fixed end you clicked";
            return plan;
        }

        List<Leg> ordered = OrderFromRoot(plan, root);

        // A loop has no single developed distance - two paths from the fixed end
        // reach the same pipe at different lengths - and the walk would quietly
        // leave one fitting on it unattached, its joints open. Refuse instead.
        if (FindLoop(plan, ordered) is long loopAt)
        {
            plan.Error = $"the selection closes a LOOP at fitting {loopAt} - there are two paths from "
                         + "the fixed end to the same pipe, so no single slope can be measured along "
                         + "it. Leave one branch of the loop out of the selection.";
            return plan;
        }

        // Slope Through carries an unselected branch along with its tee as one
        // piece; CheckCarriedBranches below proves each one is safe to.
        groupParams = groupParams with { FollowDeadEnds = true };

        // All leg pipes AND all corner elbows are blocked during grouping, so no
        // leg's CollectGroups walks into another leg or tries to group a corner
        // elbow - the elbows are handled explicitly below.
        HashSet<long> globalBranchIds = [];
        foreach (Leg leg in ordered)
        {
            foreach (Element e in leg.Elements)
                globalBranchIds.Add(RevitCompat.IdKey(e.Id));
        }
        foreach (Leg leg in ordered)
        {
            if (leg.CornerElemId is not null)
                globalBranchIds.Add(leg.CornerElemId.Value);
        }

        List<Joint> extraJoints = [];
        foreach (Leg leg in ordered)
        {
            SolveLeg(doc, leg, fixedElem, fixedPoint, globalBranchIds,
                parameters, groupParams, plan.FixedInBranch);
        }

        // Corner elbows: each is an in-line fitting of its UPSTREAM (parent) leg.
        // Added after the parent is solved so its pitch is available.
        foreach (Leg leg in ordered)
        {
            if (leg.Parent is null || !leg.Ok || !leg.Parent.Ok || leg.Junction is not null)
                continue;
            AttachCorner(leg, plan.IdMap);
        }

        // Tees and crosses: the same idea, once per FITTING - a cross with a
        // branch on each side is one rigid body shared by two child legs.
        List<string> problems = [];
        foreach (IGrouping<long, Leg> atFitting in ordered
                     .Where(l => l.Junction is not null && l.Parent is not null && l.Ok && l.Parent.Ok)
                     .GroupBy(l => l.Junction!.FittingId))
        {
            List<Leg> children = [.. atFitting];
            (List<Joint> extra, string? problem) = AttachJunction(
                doc, children[0].Parent!, children, globalBranchIds, groupParams, plan.IdMap);
            extraJoints.AddRange(extra);
            if (problem is not null)
                problems.Add(problem);
        }

        // Before the joint list is gathered: a branch pinned here takes its
        // internal joints out of its leg's list.
        problems.AddRange(CheckCarriedBranches(doc, ordered, globalBranchIds, plan));

        List<Joint> allJoints = [.. ordered.Where(l => l.Ok).SelectMany(l => l.Joints), .. extraJoints];
        if (problems.Count > 0)
        {
            plan.Error = "an unselected branch cannot be carried along as one piece: "
                         + string.Join("; ", problems)
                         + ". Select that branch too so it is sloped with the rest, or leave it "
                         + "out of the tree and slope it separately.";
        }
        else if (LeftBehind(ordered) is { Count: > 0 } stranded)
        {
            plan.Error = $"{stranded.Count} group(s) joined to the run would stay where they are "
                         + "while the run moves away from them, leaving their joints OPEN: "
                         + string.Join("; ", stranded)
                         + ". Nothing was changed. See the report for each group.";
        }

        // Captured BEFORE the list is replaced - otherwise the only record that
        // these legs were ever found is gone.
        HashSet<int> reached = [.. ordered.Select(l => l.Index)];
        plan.UnreachedLegs = [.. plan.Legs.Where(l => !reached.Contains(l.Index))];

        plan.Legs = ordered;
        plan.Joints = allJoints;
        return plan;
    }

    /// <summary>
    /// Breadth-first from the root, wiring each child leg's parent and the corner
    /// elbow that joins them. Returns legs in visit order, so a parent is always
    /// solved before its children. Any legs the walk never reached (a detached
    /// picket) are dropped — they had no corner path back to the fixed end.
    /// </summary>
    private static List<Leg> OrderFromRoot(SlopePlan plan, Leg root)
    {
        List<Leg> ordered = [];
        HashSet<int> seen = [root.Index];
        Queue<Leg> queue = new();
        queue.Enqueue(root);

        while (queue.Count > 0)
        {
            Leg leg = queue.Dequeue();
            ordered.Add(leg);

            if (!plan.Adjacency.TryGetValue(leg.Index, out List<(int Other, long ElbowId, Vec3 Corner)>? edges))
                continue;

            foreach ((int otherIndex, long elbowId, Vec3 corner) in edges)
            {
                if (!seen.Add(otherIndex))
                    continue;

                Leg child = plan.Legs[otherIndex];
                child.Parent = leg;
                child.CornerElemId = elbowId;
                child.CornerPoint = corner;
                // A junction's true pivot depends on which leg is the parent, so
                // CornerPoint is refined once the parent's run is known (SolveLeg).
                child.Junction = plan.Junctions.TryGetValue(elbowId, out Junction? j) ? j : null;
                queue.Enqueue(child);
            }
        }

        return ordered;
    }

    private static void SolveLeg(
        Document doc, Leg leg, Element fixedElem, Vec3 fixedPoint,
        ISet<long> globalBranchIds, SlopeParams parameters,
        SlopeGroupParams groupParams, bool fixedInBranch)
    {
        if (leg.Parent is not null && !leg.Parent.Ok)
        {
            leg.Error = $"its parent leg (leg {leg.Parent.Index + 1}) could not be solved, so "
                        + "there is no corner elevation to continue from";
            return;
        }

        (List<SlopeSegment> segments, Dictionary<long, Element> elemById,
         List<SprigEngine.Rejected> rejected) = SlopeEngine.BuildSegments(doc, leg.Elements);

        leg.RejectedPicks = rejected;
        if (segments.Count == 0)
        {
            leg.Error = "no usable pipe segment on this leg";
            return;
        }

        (SlopeRun? run, List<(SlopeSegment Segment, string Reason)> runRejects) =
            SlopeSolver.BuildRun(segments, parameters);

        leg.RejectedSegments = runRejects;
        if (run is null)
        {
            leg.Error = "the pipes on this leg do not form one straight run - see the dropped picks";
            return;
        }

        if (leg.Parent is null)
        {
            // Leg 1: anchor at the end nearest the fixed element, keep its
            // current elevation. Exactly Slope Branch's single-run contract.
            leg.Pitch = SlopeSolver.Solve(run, fixedPoint, parameters, snapToEnd: true);
        }
        else if (leg.Junction is not null)
        {
            // Teed off its parent. The developed distance is measured from the
            // tee's centre ON THE PARENT'S LINE, so the anchor is that point's
            // station on this leg - unclamped: it sits a half-tee short of this
            // leg's first pipe, and RePitch carries the slope across that gap
            // exactly. If the tee sits partway along this leg (a cross with the
            // branch on both sides, or the main when the fixed end is out on a
            // branch), the anchor is mid-run and the leg rises away both ways.
            Vec3 corner = JunctionPivot(leg.Junction, leg.Parent);
            leg.CornerPoint = corner;
            double parentCornerZ = leg.Parent.Pitch!.NewZ(leg.Parent.Run!.Station(corner));
            leg.Pitch = new RePitch(run, run.Station(corner), parameters.Slope, anchorZ: parentCornerZ);
        }
        else
        {
            // A downstream leg: anchor at the corner it shares with its parent,
            // and take that corner's NEW elevation from the parent so the
            // developed run stays continuous.
            Vec3 corner = leg.CornerPoint!.Value;
            double cornerS = run.Station(corner);
            double anchorS = Math.Abs(cornerS - run.SMin) <= Math.Abs(cornerS - run.SMax)
                ? run.SMin
                : run.SMax;

            double parentCornerZ = leg.Parent.Pitch!.NewZ(leg.Parent.Run!.Station(corner));
            leg.Pitch = new RePitch(run, anchorS, parameters.Slope, anchorZ: parentCornerZ);

            // Then meet the elbow where it will REALLY be. The elbow is a rigid body
            // rotated and rolled about its chord midpoint, so its outlet to this leg
            // lands ABOVE the parent's line at the corner by roughly slope x elbow
            // size - and anchoring this leg's pipe end at the parent's corner height
            // left exactly that as a vertical gap. Proven 2026-10-17: 2.5-2.65 mm on
            // PRJ01's 100 mm bends at 25 mm/m, and 4.4 mm on a 200 mm bend at
            // 40 mm/m, past the 3 mm reconnect tolerance - a joint left OPEN. The
            // roll depends only on this leg's RATE, not its height, so the
            // provisional pitch above is enough to predict it.
            //
            // The roll turns about the elbow's PARENT-side connector (as the pitch
            // rotation leaves it), not its chord midpoint: rolled about the
            // midpoint, which is off the parent's centreline, the parent-side
            // connector lifted by half the elbow x the roll and opened the
            // PARENT's joint on Snowdon's 200 mm bend (2026-10-17).
            if (ElbowPorts(doc, leg.CornerElemId, run, anchorS) is (Vec3 parentPort, Vec3 outlet))
            {
                (double theta, Vec3 dParentNew, double roll, _) = CornerMotion(leg.Parent, leg.Pitch, corner);
                Vec3 parentAfter = SlopeSolver.RotateAbout(parentPort, corner, leg.Parent.Pitch.AxisDir, theta);
                leg.CornerRollPivot = parentAfter;

                // The drop that lands the PARENT-side connector on the parent's new
                // line, not the chord midpoint: the midpoint sits above that line by
                // half the rise across the elbow once a corner is sloped, so dropping
                // IT onto the line moved an already-sloped corner again on every
                // re-run (Snowdon, 2026-10-17: "already at this slope" never came).
                leg.CornerDrop = leg.Parent.Pitch.NewZ(leg.Parent.Run.Station(parentAfter)) - parentAfter.Z;
                Vec3 landed = SlopeSolver.RigidMove(outlet, corner, leg.Parent.Pitch.AxisDir, theta,
                    dParentNew, roll, leg.CornerDrop.Value, leg.CornerRollPivot);
                double anchorZ = landed.Z - parameters.Slope * Math.Abs(run.Station(landed) - anchorS);
                leg.Pitch = new RePitch(run, anchorS, parameters.Slope, anchorZ: anchorZ);
            }
        }

        leg.BranchIds = [.. run.Segments.Select(s => s.ElemId)];
        leg.Run = run;

        List<Element> branchElems =
            [.. run.Segments.Where(s => elemById.ContainsKey(s.ElemId))
                            .Select(s => elemById[s.ElemId])];

        (leg.Groups, leg.Joints) = SlopeEngine.CollectGroups(
            branchElems, globalBranchIds, run, leg.Pitch, groupParams);

        // Leg 1 holds the tie-in: pin whatever group carries the fixed element so
        // the heuristic can never decide to drag the cross main. Skipped when the
        // click landed ON the branch - there the pick was only ever a point
        // naming an end, and the thing it hit is pipe that MUST move.
        if (leg.Parent is null && !fixedInBranch)
        {
            long fixedId = RevitCompat.IdKey(fixedElem.Id);
            PinFixed(leg, fixedId);
            // Exactly one of these applies: an elbow has two connectors, a tee
            // three. Same pairing as SlopeMulti.BuildJob.
            //
            // THE TEE HALF IS ONLY SAFE BECAUSE RebuildFixedTee IS SURVIVABLE.
            // It was wired up once without that and reverted the same day:
            // PlanFixedTeeRebuild produces a plan for 393 of 400 real tees, but
            // NewTeeFitting only ACCEPTS about 5 in 15, and back then a refusal
            // threw and rolled the whole run back. Measured, not assumed - the
            // planner figure looked like verification and was not.
            //
            // Now a refusal rolls back just the fitting and reports, so the
            // worst case is exactly today's behaviour plus a warning.
            leg.FixedRebuild = PlanFixedRebuild(leg.BranchIds, fixedElem, fixedId);
            leg.FixedTeeRebuild = leg.FixedRebuild is null
                ? PlanFixedTeeRebuild(leg.BranchIds, fixedElem, fixedId)
                : null;
        }
    }

    private static void PinFixed(Leg leg, long fixedId)
    {
        foreach (SlopeGroup group in leg.Groups)
        {
            if (!group.MemberIds.Contains(fixedId))
                continue;

            // CollectGroups may already have classified this closure as a movable
            // in-line assembly (and computed internal joints across its own
            // members) before this override ever runs. Those stale joints have to
            // come out of leg.Joints here, not just off the group object, or the
            // break/remake machinery would disconnect and reconnect a web of
            // connections inside a group that never moves.
            leg.Joints = SlopeGroupUtil.StripGroupJoints(leg.Joints, group);
            group.Kind = GroupKind.Foreign;
            group.Rotate = false;
            group.Parts = [];
            group.InternalJoints = [];
            group.AddNote(
                $"holds element {fixedId}, which you picked as the FIXED end - pinned, not guessed");
        }
    }

    // ------------------------------------------------------- fixed-end rebuild

    /// <summary>The single connector joined to this one at an End connection, or null.</summary>
    private static Connector? ConnectorNeighbor(Connector conn)
    {
        ConnectorSet refs;
        try
        {
            refs = conn.AllRefs;
        }
        catch (Autodesk.Revit.Exceptions.ApplicationException)
        {
            return null;
        }

        foreach (Connector other in refs)
        {
            try
            {
                if (other.ConnectorType != ConnectorType.End)
                    continue;
                if (other.Owner is not null)
                    return other;
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException)
            {
                continue;
            }
        }
        return null;
    }

    /// <summary>
    /// The element's OPEN connector nearest the point, within tolerance. Used
    /// only when rebuilding the fixed end's fitting, where connectors have to be
    /// re-found fresh after a delete/regenerate.
    /// </summary>
    private static Connector? ConnectorAt(Element elem, XYZ point, double tol = 0.02)
    {
        Connector? best = null;
        double? bestD = null;

        foreach (Connector c in MepConnectors.GetEndConnectors(elem))
        {
            if (c.IsConnected)
                continue;

            double d = c.Origin.DistanceTo(point);
            if (bestD is null || d < bestD)
            {
                bestD = d;
                best = c;
            }
        }

        return best is not null && bestD <= tol ? best : null;
    }

    /// <summary>
    /// If the FIXED pick is a plain 2-connector fitting sitting directly on the
    /// root leg's own branch connector, plan to rebuild it fresh after the
    /// re-pitch instead of leaving the old rigid body in place.
    ///
    /// WHY: pinned "never move" has always also meant never ROTATED — the
    /// fitting's branch-facing connector stays at its old angle while the branch
    /// pipe touching it tilts to the new slope, because only the anchor POINT is
    /// held fixed, not the pipe's DIRECTION. Its two connectors are rigidly fixed
    /// relative to each other, so unlike a corner there is no rotation that fixes
    /// the branch side without ALSO rotating the OTHER (protected) side. Live
    /// testing confirmed the mismatch is real: nudging the fitting afterward
    /// snapped the branch pipe's slope back to flat. Deleting it and letting a
    /// fresh one be built fixed it — a freshly placed fitting's internal angle is
    /// whatever the two connectors it is asked to join actually need.
    ///
    /// Returns null if the fixed pick is not a plain 2-connector fitting directly
    /// on the branch; a tee or anything with more connections stays on the
    /// pin-and-never-touch path, since rebuilding those safely would also have to
    /// preserve every OTHER connection they carry.
    /// </summary>
    /// <remarks>
    /// Takes the branch ids rather than a <see cref="Leg"/> so Slope Armover can
    /// reuse it: its fixed reference is the same kind of thing — the fitting just
    /// beyond the run's anchored end — and deserves identical treatment. On a
    /// TEE this still returns null, which is why an armover teed off a branch
    /// keeps its tee untouched.
    /// </remarks>
    internal static FixedRebuild? PlanFixedRebuild(
        ISet<long> branchIds, Element fixedElem, long fixedId)
    {
        if (!SlopeEngine.IsFittingLike(fixedElem))
            return null;

        List<Connector> conns = MepConnectors.GetEndConnectors(fixedElem);
        if (conns.Count != 2)
            return null;

        // A STRAIGHT-THROUGH two-connector fitting - a reducer or a coupling - is
        // not an elbow, and can never be rebuilt as one. The in-Revit suite found
        // the commonest case: an armover smaller than its branch line, joined
        // through the transition Revit inserts at the tee. That transition was
        // taken as "the fixed end's fitting", deleted, and NewElbowFitting refused
        // to bridge two nearly collinear connectors - rolling back the whole
        // batch. Leave such a fitting in place; the joint is remade as usual.
        if (Math.Abs(conns[0].CoordinateSystem.BasisZ.DotProduct(conns[1].CoordinateSystem.BasisZ))
            > StraightThroughCos)
        {
            return null;
        }

        Connector? branchConn = null;
        Connector? otherConn = null;
        Connector? otherNeighbor = null;

        foreach (Connector c in conns)
        {
            Connector? nb = ConnectorNeighbor(c);
            if (nb is null)
                return null;   // both ends must actually be joined to something

            if (branchIds.Contains(RevitCompat.IdKey(nb.Owner.Id)))
            {
                branchConn = c;
            }
            else
            {
                otherConn = c;
                otherNeighbor = nb;
            }
        }

        if (branchConn is null || otherConn is null || otherNeighbor is null)
            return null;

        return new FixedRebuild(
            fixedId,
            fixedElem.GetTypeId(),
            otherConn.Origin,
            RevitCompat.IdKey(otherNeighbor.Owner.Id));
    }

    /// <summary>
    /// Delete the fixed end's OLD fitting and build a fresh one in its place,
    /// joining the (already re-pitched, already reconnected) branch pipe to the
    /// untouched element on the other side.
    ///
    /// Runs AFTER the normal break/repitch/remake sequence, so the branch side is
    /// already rejoined to the OLD fitting by the ordinary machinery — this
    /// deliberately does not intercept that generic joint handling, it simply
    /// replaces what it left behind.
    /// </summary>
    /// <summary>Two connector directions within 10 degrees of opposite: a straight-through fitting.</summary>
    private static readonly double StraightThroughCos = Math.Cos(10.0 * Math.PI / 180.0);

    /// <summary>
    /// Rebuilds the fixed end's elbow; null when rebuilt, otherwise why it was
    /// left as it was. SURVIVABLE, like <see cref="RebuildFixedTee"/>: the
    /// delete-and-recreate runs in a SubTransaction, so a refusal rolls back just
    /// this fitting - the original comes back, joined - and the run carries on.
    /// It used to throw, on the grounds that NewElbowFitting is permissive; the
    /// in-Revit suite caught a refusal rolling back a whole Slope Armover batch.
    /// </summary>
    internal static string? RebuildFixedElbow(FixedRebuild rebuild, Document doc)
    {
        using SubTransaction sub = new(doc);
        sub.Start();
        try
        {
            RebuildFixedElbowCore(rebuild, doc);
            sub.Commit();
            return null;
        }
        catch (Exception ex)
        {
            if (sub.GetStatus() == TransactionStatus.Started)
                sub.RollBack();
            return ex.Message;
        }
    }

    private static void RebuildFixedElbowCore(FixedRebuild rebuild, Document doc)
    {
        ElementId elbowId = MakeElementId(rebuild.ElbowId);
        Element? elbow = doc.GetElement(elbowId);
        if (elbow is null)
        {
            throw new ApplyError(
                $"the fixed end's fitting (element {rebuild.ElbowId}) is no longer in the model - "
                + "could not rebuild it");
        }

        Connector? branchConn = null;
        Connector? otherConn = null;
        foreach (Connector c in MepConnectors.GetEndConnectors(elbow))
        {
            Connector? nb = ConnectorNeighbor(c);
            if (nb is not null && RevitCompat.IdKey(nb.Owner.Id) == rebuild.OtherElemId)
                otherConn = c;
            else
                branchConn = c;
        }

        if (branchConn is null || otherConn is null)
        {
            throw new ApplyError(
                $"could not find the fixed end fitting {rebuild.ElbowId}'s own connectors to "
                + "rebuild it");
        }

        Connector? branchNb = ConnectorNeighbor(branchConn);
        if (branchNb is null)
        {
            throw new ApplyError(
                $"the fixed end fitting {rebuild.ElbowId}'s branch-facing connector is no longer "
                + "joined to anything - could not rebuild it");
        }

        long pipeId = RevitCompat.IdKey(branchNb.Owner.Id);
        XYZ pipePoint = branchConn.Origin;
        XYZ otherPoint = rebuild.OtherOrigin;

        branchConn.DisconnectFrom(branchNb);
        Connector? otherNb = ConnectorNeighbor(otherConn);
        if (otherNb is not null)
            otherConn.DisconnectFrom(otherNb);
        doc.Regenerate();

        doc.Delete(elbowId);
        doc.Regenerate();

        Element? pipeElem = doc.GetElement(MakeElementId(pipeId));
        Element? otherElem = doc.GetElement(MakeElementId(rebuild.OtherElemId));
        if (pipeElem is null || otherElem is null)
        {
            throw new ApplyError(
                "the fixed end's pipe or its far neighbour disappeared while rebuilding the "
                + "fitting between them");
        }

        Connector? newPipeConn = ConnectorAt(pipeElem, pipePoint);
        Connector? newOtherConn = ConnectorAt(otherElem, otherPoint);
        if (newPipeConn is null || newOtherConn is null)
            throw new ApplyError("could not find the open connectors to rebuild the fixed end's fitting");

        FamilyInstance newElbow;
        try
        {
            newElbow = doc.Create.NewElbowFitting(newPipeConn, newOtherConn);
        }
        catch (Exception ex)
        {
            throw new ApplyError($"could not rebuild the fixed end's fitting ({ex.Message})");
        }

        try
        {
            if (newElbow.GetTypeId() != rebuild.TypeId)
                newElbow.ChangeTypeId(rebuild.TypeId);
        }
        catch (Exception)
        {
            // The fresh fitting's geometry is what matters; failing to restore
            // the exact type is cosmetic and must not abort the run.
        }

        doc.Regenerate();
    }

    /// <summary>
    /// Plan for replacing a TEE at the anchored end, so it comes back at the
    /// re-pitched angle instead of leaving a sliver against the pipe's tilted
    /// end face.
    ///
    /// Null unless this is a 3-connector fitting with ALL THREE joined, exactly
    /// one of them to the run being re-pitched. Anything else keeps the old
    /// pin-and-never-touch treatment: a partially connected tee has nothing to
    /// rebuild against, and one joined to the run twice is not a takeoff.
    /// </summary>
    internal static FixedTeeRebuild? PlanFixedTeeRebuild(
        ISet<long> branchIds, Element fixedElem, long fixedId)
    {
        if (!SlopeEngine.IsFittingLike(fixedElem))
            return null;

        List<Connector> conns = MepConnectors.GetEndConnectors(fixedElem);
        if (conns.Count != 3)
            return null;

        Connector? branchConn = null;
        long branchElemId = 0;
        List<(XYZ Origin, long ElemId)> run = [];

        foreach (Connector c in conns)
        {
            Connector? nb = ConnectorNeighbor(c);
            if (nb is null)
                return null;   // every leg of the tee must actually be joined

            long nbId = RevitCompat.IdKey(nb.Owner.Id);

            if (branchIds.Contains(nbId))
            {
                if (branchConn is not null)
                    return null;   // joined to the run twice - not a takeoff

                branchConn = c;
                branchElemId = nbId;
            }
            else
            {
                run.Add((c.Origin, nbId));
            }
        }

        if (branchConn is null || run.Count != 2)
            return null;

        return new FixedTeeRebuild(
            fixedId, fixedElem.GetTypeId(),
            branchConn.Origin, branchElemId,
            run[0].Origin, run[0].ElemId,
            run[1].Origin, run[1].ElemId);
    }

    /// <summary>
    /// Delete the anchored end's TEE and build a fresh one joining the same three
    /// elements — the two along the branch line, plus the re-pitched run.
    ///
    /// Runs AFTER the ordinary remake, same as the elbow path: the generic
    /// machinery has already rejoined everything to the OLD tee, and this
    /// replaces what it left behind rather than intercepting it.
    ///
    /// A failure here throws, which rolls the whole batch back. That is the
    /// point: the tee is deleted before the new one is created, so a half-done
    /// rebuild must never be allowed to commit.
    /// </summary>
    /// <summary>
    /// Null when the tee was rebuilt; otherwise the reason it was left exactly as
    /// it was. SURVIVABLE BY DESIGN — a refusal here must not cost the run.
    ///
    /// The whole delete-and-recreate happens inside a SubTransaction, so a
    /// refusal rolls back only this fitting: the original tee and its three
    /// connections come back, and the caller carries on with a re-pitched run and
    /// a warning. That is the only way to make it non-fatal, because the old tee
    /// has to be deleted before <c>NewTeeFitting</c> is asked to place the new
    /// one — catching the failure without a rollback would leave a hole.
    ///
    /// It matters because Revit refuses this far more often than the plan
    /// suggests. Measured on a real model: <see cref="PlanFixedTeeRebuild"/>
    /// produced a plan for 393 of 400 tees, but only about 5 in 15 of those
    /// actually rebuilt — the rest came back "failed to insert tee." Throwing on
    /// that turned a cosmetic sliver into a whole-run rollback most of the time.
    /// </summary>
    internal static string? RebuildFixedTee(FixedTeeRebuild rebuild, Document doc)
    {
        ElementId teeId = MakeElementId(rebuild.TeeId);
        if (doc.GetElement(teeId) is null)
        {
            return $"the anchored end's tee (element {rebuild.TeeId}) is no longer in the model";
        }

        using SubTransaction sub = new(doc);
        sub.Start();

        try
        {
            Element tee = doc.GetElement(teeId);

            // Disconnect every leg before deleting, so the neighbours are left
            // with open connectors to rebuild against.
            foreach (Connector c in MepConnectors.GetEndConnectors(tee))
            {
                Connector? nb = ConnectorNeighbor(c);
                if (nb is not null)
                    c.DisconnectFrom(nb);
            }

            doc.Regenerate();
            doc.Delete(teeId);
            doc.Regenerate();

            Element? branchElem = doc.GetElement(MakeElementId(rebuild.BranchElemId));
            Element? run1Elem = doc.GetElement(MakeElementId(rebuild.Run1ElemId));
            Element? run2Elem = doc.GetElement(MakeElementId(rebuild.Run2ElemId));

            if (branchElem is null || run1Elem is null || run2Elem is null)
            {
                throw new ApplyError(
                    "one of the three elements around it disappeared while rebuilding");
            }

            Connector? branchConn = ConnectorAt(branchElem, rebuild.BranchOrigin);
            Connector? run1Conn = ConnectorAt(run1Elem, rebuild.Run1Origin);
            Connector? run2Conn = ConnectorAt(run2Elem, rebuild.Run2Origin);

            if (branchConn is null || run1Conn is null || run2Conn is null)
                throw new ApplyError("could not find the open connectors to rebuild it against");

            // Run connectors first, branch last - that is the order
            // NewTeeFitting expects, and swapping them yields a tee pointing the
            // wrong way.
            FamilyInstance newTee = doc.Create.NewTeeFitting(run1Conn, run2Conn, branchConn);

            try
            {
                if (newTee.GetTypeId() != rebuild.TypeId)
                    newTee.ChangeTypeId(rebuild.TypeId);
            }
            catch (Exception)
            {
                // Geometry is what matters; not restoring the exact type is
                // cosmetic and must not lose the rebuild.
            }

            doc.Regenerate();
            sub.Commit();
            return null;
        }
        catch (Exception ex)
        {
            // Puts the original tee and its three connections back.
            sub.RollBack();
            doc.Regenerate();

            return $"{ex.Message} - the tee was left exactly as it was, so the run is sloped but "
                   + "its anchored end may show a thin gap against the fitting";
        }
    }

    // ------------------------------------------------------------------ corners

    /// <summary>
    /// Turn the elbow between this leg and its parent into an in-line fitting of
    /// the PARENT leg: rotated by the parent's end-of-run angle, dropped to the
    /// corner's new elevation.
    ///
    /// ONE ROTATION IS NOT ENOUGH. The elbow's two connectors are rigidly fixed
    /// relative to each other, so a single rotation can only correctly satisfy
    /// ONE of them — here the PARENT-facing one. The CHILD leg's connector gets
    /// whatever direction that rotation happens to carry it to, which generally
    /// does not match what the CHILD leg's own, independently-computed RePitch
    /// needs. Live testing confirmed this is not theoretical: nudging a corner
    /// elbow afterward snapped the PARENT leg's own pipe back to its pre-slope
    /// angle, because the joint Revit accepted was never self-consistent — only
    /// positionally coincident.
    ///
    /// Second best, applied here: after the parent-exact rotation, ROLL the elbow
    /// about the axis of the (now-correct) parent connector's own new direction.
    /// That roll cannot disturb the parent connector — a vector is invariant
    /// under a rotation about an axis parallel to itself — so it only affects the
    /// child connector, and <see cref="SlopeSolver.CornerRoll"/> picks the angle
    /// that brings it as close as it can get. Exact for the parent side,
    /// best-effort for the child side. Whatever is still off is MEASURED, never
    /// assumed, and reported per corner in the dry run.
    /// </summary>
    private static void AttachCorner(Leg leg, Dictionary<long, ElementId> idMap)
    {
        Leg parent = leg.Parent!;
        long elbowId = leg.CornerElemId!.Value;
        Vec3 pivot = leg.CornerPoint!.Value;

        InlinePart part = new(elbowId, pivot, leg.CornerDrop ?? parent.Pitch!.Dz(pivot));

        (_, Vec3 dParentNew, double rollAngle, double residualDeg) = CornerMotion(parent, leg.Pitch!, pivot);
        if (Math.Abs(rollAngle) > 1.0e-9)
        {
            part.RollAxis = dParentNew;
            part.RollAngle = rollAngle;
            part.RollPivot = leg.CornerRollPivot;
        }

        leg.CornerResidualDeg = residualDeg;

        // A corner elbow has exactly two connectors, both taken by the two legs,
        // so nothing hangs off it to follow.
        SlopeGroup group = new(elbowId, [elbowId])
        {
            Kind = GroupKind.Inline,
            Rotate = true,
            Parts = [part],
            Pivot = pivot,
            Dz = part.Dz,
        };

        group.AddNote(
            $"corner elbow into leg {leg.Index + 1} - rotated with leg {parent.Index + 1} and "
            + $"dropped to the shared corner; {leg.CornerResidualDeg:F4} deg still off on leg "
            + $"{leg.Index + 1}'s side after the best available roll");

        parent.Groups.Add(group);

        if (!idMap.ContainsKey(elbowId))
            idMap[elbowId] = MakeElementId(elbowId);
    }

    /// <summary>
    /// How a corner elbow moves: the parent's pitch rotation at the corner, the
    /// parent's new direction (the roll axis), the roll that brings the child
    /// side as close as it can get, and what is still off on the child side.
    /// Only the child's RATES are read from <paramref name="childPitch"/>, so a
    /// provisional pitch gives the same answer as the final one.
    /// </summary>
    private static (double Theta, Vec3 DParentNew, double Roll, double ResidualDeg) CornerMotion(
        Leg parent, RePitch childPitch, Vec3 pivot)
    {
        RePitch pp = parent.Pitch!;
        double parentStation = pp.Run.Station(pivot);
        double theta = pp.ThetaAt(parentStation);
        Vec3 dParentNew = SlopeSolver.DirectionAtRate(parent.Run!.Axis, pp.NewRateAt(parentStation));

        double childStation = childPitch.Run.Station(pivot);
        Vec3 dChildOld = SlopeSolver.DirectionAtRate(childPitch.Run.Axis, childPitch.OldRateAt(childStation));
        Vec3 dChildTarget = SlopeSolver.DirectionAtRate(childPitch.Run.Axis, childPitch.NewRateAt(childStation));

        Vec3 afterTheta = SlopeSolver.RotateAbout(dChildOld, Vec3.Zero, pp.AxisDir, theta);
        double roll = SlopeSolver.CornerRoll(afterTheta, dChildTarget, dParentNew);
        Vec3 final = SlopeSolver.RotateAbout(afterTheta, Vec3.Zero, dParentNew, roll);

        return (theta, dParentNew, roll, final.Normalize().AngleTo(dChildTarget.Normalize()) * 180.0 / Math.PI);
    }

    /// <summary>
    /// A corner elbow's two connectors: the one facing the PARENT and the one
    /// facing this leg (nearest the leg's anchored end in plan), or null when the
    /// elbow cannot be read as a two-connector fitting.
    /// </summary>
    private static (Vec3 Parent, Vec3 Child)? ElbowPorts(Document doc, long? elbowId, SlopeRun run, double anchorS)
    {
        if (elbowId is null || doc.GetElement(MakeElementId(elbowId.Value)) is not Element elbow)
            return null;

        List<Vec3> ports = [.. MepConnectors.GetEndConnectors(elbow).Select(c => c.Origin.ToVec3())];
        if (ports.Count != 2)
            return null;

        Vec3 end = run.Origin.Add(run.Axis.Multiply(anchorS));
        double PlanD(Vec3 o) => Math.Sqrt((o.X - end.X) * (o.X - end.X) + (o.Y - end.Y) * (o.Y - end.Y));
        return PlanD(ports[0]) <= PlanD(ports[1]) ? (ports[1], ports[0]) : (ports[0], ports[1]);
    }

    // ---------------------------------------------------------------- junctions

    /// <summary>
    /// Where a tee/cross pivots, seen from its PARENT leg: the centre of the
    /// connectors that lead back to the parent. A main carried straight through
    /// gives two, whose midpoint is the tee centre on the main's own line; a
    /// parent that ends at the fitting gives one, its own pipe end.
    /// </summary>
    internal static Vec3 JunctionPivot(Junction junction, Leg parent)
    {
        List<Vec3> onParent = [.. junction.Ports.Where(p => p.LegIndex == parent.Index).Select(p => p.Origin)];
        return onParent.Count > 0 ? Avg(onParent) : Avg([.. junction.Ports.Select(p => p.Origin)]);
    }

    /// <summary>
    /// Turn a tee or cross shared by a parent leg and one or more child legs into
    /// an in-line fitting of the PARENT: rotated by the parent's change of pitch,
    /// then ROLLED about the parent's new direction so each child-facing outlet
    /// tilts to its own leg's slope, then dropped to the parent's new line.
    ///
    /// The roll is what makes a tee work. The parent's two connectors lie along
    /// the roll axis, so rolling cannot disturb them, while the branch outlet -
    /// at right angles to it - swings through exactly the vertical plane its
    /// branch pipe lives in. For a square tee the fit is exact to a few
    /// thousandths of a degree. A CROSS with a branch on each side cannot be
    /// exact: rolling tips one outlet up and the other down, while both branches
    /// rise away from the main. The roll then splits the difference, and what is
    /// left is MEASURED per child leg and reported, never assumed.
    ///
    /// Anything else hanging off the fitting (a connector to no leg) is
    /// translated by the displacement of its outlet, like a sprig off a tee.
    ///
    /// Returns the extra joints to break and remake - between the fitting and a
    /// straight-through fitting next to it, which no leg's own grouping records -
    /// and a problem when something hanging off it cannot safely be carried.
    /// </summary>
    private static (List<Joint> Joints, string? Problem) AttachJunction(
        Document doc, Leg parent, List<Leg> children, HashSet<long> globalBranchIds,
        SlopeGroupParams groupParams, Dictionary<long, ElementId> idMap)
    {
        Junction junction = children[0].Junction!;
        long fid = junction.FittingId;
        Vec3 pivot = JunctionPivot(junction, parent);
        RePitch pp = parent.Pitch!;

        double parentStation = pp.Run.Station(pivot);
        double theta = pp.ThetaAt(parentStation);
        Vec3 dParentNew = SlopeSolver.DirectionAtRate(parent.Run!.Axis, pp.NewRateAt(parentStation));
        double dz = pp.Dz(pivot);

        // Each child-facing outlet: where it points after the pitch rotation, and
        // where its own leg needs it to point.
        Dictionary<int, Leg> byIndex = children.ToDictionary(c => c.Index);
        List<(Leg Child, Vec3 After, Vec3 Target)> outlets = [];
        foreach (JunctionPort port in junction.Ports)
        {
            if (port.LegIndex is not int li || !byIndex.TryGetValue(li, out Leg? child))
                continue;
            if (port.Direction.GetLength() < SlopeSolver.LenEps)
                continue;

            RePitch cp = child.Pitch!;
            double rate = cp.NewRateAt(cp.Run.Station(port.Origin));
            Vec3 target = SlopeSolver.DirectionAtRate(child.Run!.Axis, rate);
            // The outlet may point down the leg's -axis; the same line, the other way.
            if (target.X * port.Direction.X + target.Y * port.Direction.Y < 0.0)
                target = SlopeSolver.DirectionAtRate(child.Run.Axis.Multiply(-1.0), -rate);

            Vec3 after = SlopeSolver.RotateAbout(port.Direction.Normalize(), Vec3.Zero, pp.AxisDir, theta);
            outlets.Add((child, after, target));
        }

        double roll = SlopeSolver.JunctionRoll(outlets.Select(o => (o.After, o.Target)).ToList(), dParentNew);

        foreach (Leg child in children)
        {
            List<double> residuals =
            [
                .. outlets.Where(o => o.Child == child).Select(o =>
                    SlopeSolver.RotateAbout(o.After, Vec3.Zero, dParentNew, roll)
                        .Normalize().AngleTo(o.Target.Normalize()) * 180.0 / Math.PI)
            ];
            child.CornerResidualDeg = residuals.Count > 0 ? residuals.Max() : 0.0;
        }

        Vec3 Transform(Vec3 point) => SlopeSolver.RigidMove(point, pivot, pp.AxisDir, theta, dParentNew, roll, dz);

        InlinePart part = new(fid, pivot, dz);
        if (Math.Abs(roll) > 1.0e-9)
        {
            part.RollAxis = dParentNew;
            part.RollAngle = roll;
        }

        List<Joint> extra = [];
        string? problem = null;
        HashSet<long> blocked = [.. globalBranchIds, fid];

        foreach (JunctionPort port in junction.Ports)
        {
            if (port.NeighbourId is not long nid)
                continue;

            bool toLeg = port.LegIndex == parent.Index
                         || (port.LegIndex is int li && byIndex.ContainsKey(li));

            if (toLeg)
            {
                // Straight onto a leg pipe: that leg's own grouping records the
                // joint. Through a transition: the transition is grouped with the
                // leg beyond it, and THIS joint is nobody else's.
                if (port.PassThrough.Count > 0)
                    extra.Add(new Joint(fid, port.Origin, nid, port.Origin));
                continue;
            }

            // Not a leg: whatever hangs here is carried by this outlet.
            Element? start = doc.GetElement(MakeElementId(nid));
            if (start is null)
                continue;

            SlopeEngine.ClosureResult sub = SlopeEngine.Closure(start, blocked, groupParams.MaxGroupElements);
            if (sub.HitCap || TouchesOtherLegs(sub.Seen.Values, globalBranchIds, fid))
            {
                problem = $"what hangs off fitting {fid} at its outlet toward element {nid} "
                          + (sub.HitCap
                              ? $"runs past {groupParams.MaxGroupElements} elements"
                              : "joins the sloped pipe again somewhere else");
                continue;
            }

            part.FollowSets.Add(([.. sub.Order], Transform(port.Origin).Subtract(port.Origin)));
            extra.Add(new Joint(fid, port.Origin, nid, port.Origin));
        }

        string into = string.Join(", ", children.Select(c => $"leg {c.Index + 1}"));
        SlopeGroup group = new(fid, [fid, .. part.FollowSets.SelectMany(f => f.Ids)])
        {
            Kind = GroupKind.Inline,
            Rotate = true,
            Parts = [part],
            Pivot = pivot,
            Dz = dz,
        };
        group.AddNote(
            $"junction into {into} - rotated with leg {parent.Index + 1}, rolled so the branch "
            + $"outlet takes the branch's slope, and dropped to leg {parent.Index + 1}'s new line; "
            + $"{children.Max(c => c.CornerResidualDeg ?? 0.0):F4} deg still off on the branch side");
        group.MaxMove = Math.Max(Math.Abs(dz),
            part.FollowSets.Select(f => Math.Abs(f.Delta.Z)).DefaultIfEmpty(0.0).Max());

        parent.Groups.Add(group);
        if (!idMap.ContainsKey(fid))
            idMap[fid] = MakeElementId(fid);

        return (extra, problem);
    }

    /// <summary>True when any element here is joined to a sloped pipe or fitting other than <paramref name="via"/>.</summary>
    private static bool TouchesOtherLegs(IEnumerable<Element> elems, HashSet<long> legIds, long via)
    {
        foreach (Element e in elems)
        {
            foreach (Element n in Connected(e))
            {
                long nid = RevitCompat.IdKey(n.Id);
                if (nid != via && legIds.Contains(nid))
                    return true;
            }
        }
        return false;
    }

    /// <summary>Within this distance of each other, two touches are one place on the run (~600 mm).</summary>
    private static readonly double OnePlaceFt = 600.0 / 304.8;

    /// <summary>
    /// Proves every unselected branch the grouping kept movable really is a dead
    /// end before it is carried. It must:
    ///   - touch the sloped pipe at ONE place, on its own leg (a branch that joins
    ///     the run again further along, or another leg, closes a loop: no single
    ///     rigid move is right for it);
    ///   - be no bigger than the pipe it hangs off (a bigger pipe is the way back
    ///     into the rest of the system, not a branch).
    /// At the fixed end itself nothing moves anyway, so a group there keeps the
    /// old never-move treatment rather than being judged.
    ///
    /// Returns one line per branch that fails; each failure is also pinned.
    /// </summary>
    private static List<string> CheckCarriedBranches(
        Document doc, List<Leg> ordered, HashSet<long> globalBranchIds, SlopePlan plan)
    {
        List<string> problems = [];
        plan.CarriedBranches = [];

        foreach (Leg leg in ordered.Where(l => l.Ok))
        {
            foreach (SlopeGroup group in leg.Groups.Where(g => g.DeadEnd && SlopeGroupUtil.Moves(g.Kind)))
            {
                Vec3 at = group.Pivot ?? leg.Run!.Segments[0].P0;

                if (leg.Parent is null
                    && Math.Abs(leg.Pitch!.Run.Station(at) - leg.Pitch.AnchorStation) < OnePlaceFt)
                {
                    Pin(leg, group, "sits at the fixed end, where nothing moves - left alone");
                    continue;
                }

                List<Element> members = [.. group.MemberIds
                    .Select(id => doc.GetElement(MakeElementId(id)))
                    .Where(e => e is not null)];

                List<(long Id, XYZ Point)> touches = [];
                foreach (Element m in members)
                {
                    foreach (Connector c in MepConnectors.GetEndConnectors(m))
                    {
                        Connector? nb = ConnectorNeighbor(c);
                        if (nb is null)
                            continue;
                        long nid = RevitCompat.IdKey(nb.Owner.Id);
                        if (globalBranchIds.Contains(nid))
                            touches.Add((nid, c.Origin));
                    }
                }

                HashSet<long> own = [.. leg.Elements.Select(e => RevitCompat.IdKey(e.Id))];
                string? why = null;
                if (touches.Any(t => !own.Contains(t.Id)))
                {
                    why = "it is also joined to another sloped leg or fitting";
                }
                else if (touches.Count > 1
                         && touches.Max(t => touches.Max(u => t.Point.DistanceTo(u.Point))) > OnePlaceFt)
                {
                    why = "it joins the sloped pipe at more than one place, so it closes a loop";
                }
                else
                {
                    double hangsOff = touches
                        .Select(t => doc.GetElement(MakeElementId(t.Id)))
                        .OfType<Autodesk.Revit.DB.Plumbing.Pipe>()
                        .Select(p => p.Diameter)
                        .DefaultIfEmpty(double.MaxValue)
                        .Max();
                    Autodesk.Revit.DB.Plumbing.Pipe? biggest = members
                        .OfType<Autodesk.Revit.DB.Plumbing.Pipe>()
                        .OrderByDescending(p => p.Diameter)
                        .FirstOrDefault();
                    if (biggest is not null && biggest.Diameter > hangsOff + 0.5 / 304.8)
                    {
                        why = $"element {RevitCompat.IdKey(biggest.Id)} in it is bigger than the pipe it "
                              + "hangs off, which is the way into the rest of the system, not a branch";
                    }
                }

                if (why is not null)
                {
                    Pin(leg, group, $"NOT carried: {why}");
                    problems.Add($"the one at element {group.RootId} ({why})");
                    continue;
                }

                plan.CarriedBranches.Add((group.RootId, group.Size));
            }
        }
        return problems;

        static void Pin(Leg leg, SlopeGroup group, string note)
        {
            leg.Joints = SlopeGroupUtil.StripGroupJoints(leg.Joints, group);
            group.Kind = GroupKind.Foreign;
            group.DeadEnd = false;
            group.Rotate = false;
            group.Parts = [];
            group.InternalJoints = [];
            group.AddNote(note);
        }
    }

    /// <summary>How far the run may move at a group that stays put before its joint counts as opened (1 mm).</summary>
    private static readonly double LeftBehindFt = 1.0 / 304.8;

    /// <summary>
    /// Every group that will NOT move (the rest of the system, a group past the
    /// size cap, an unpicked piece of the same line, one no single move suits)
    /// but touches the run where the run WILL move. Committing then leaves its
    /// joints open, which the old flow reported only afterwards, as open
    /// joints, in a transaction already committed. Proven 2026-10-17: an
    /// unselected branch past the group cap was left at its tee and the run
    /// committed with "2 joint(s) were left OPEN". At the fixed end the run
    /// does not move, so a pinned group there is fine and is not listed.
    /// </summary>
    private static List<string> LeftBehind(List<Leg> ordered)
    {
        List<string> outp = [];
        foreach (Leg leg in ordered.Where(l => l.Ok))
        {
            foreach (SlopeGroup group in leg.Groups.Where(g => !SlopeGroupUtil.Moves(g.Kind)))
            {
                double worst = group.Attachments
                    .Select(a => Math.Abs(leg.Pitch!.Dz(a.BranchOrigin)))
                    .DefaultIfEmpty(0.0)
                    .Max();
                if (worst <= LeftBehindFt)
                    continue;

                string why = group.Truncated
                    ? "it is bigger than the 'Max group size' setting, so it could not be walked to its end"
                    : group.Kind switch
                    {
                        GroupKind.Missed => "it looks like more of the same line that was not picked - pick it too",
                        GroupKind.NonRigid => "no single move suits all the places it touches the run",
                        _ => "it leads off into the rest of the system",
                    };
                outp.Add($"the one at element {group.RootId} on leg {leg.Index + 1} "
                         + $"(the run moves {SlopeSolver.Mm1(worst)} there; {why})");
            }
        }
        return outp;
    }

    /// <summary>
    /// The fitting that closes a loop among the reached legs, or null. Legs and
    /// fittings form a tree exactly when no fitting joins two legs that are
    /// already connected some other way.
    /// </summary>
    private static long? FindLoop(SlopePlan plan, List<Leg> ordered)
    {
        HashSet<int> reached = [.. ordered.Select(l => l.Index)];
        Dictionary<string, string> parentOf = [];

        string Find(string x)
        {
            while (parentOf.TryGetValue(x, out string? p) && p != x)
                x = p;
            return x;
        }

        HashSet<(int, long)> pairs = [];
        foreach ((int leg, List<(int Other, long ElbowId, Vec3 Corner)> edges) in plan.Adjacency)
        {
            if (!reached.Contains(leg))
                continue;
            foreach ((int other, long fid, _) in edges)
            {
                if (reached.Contains(other))
                    pairs.Add((leg, fid));
            }
        }

        foreach ((int leg, long fid) in pairs.OrderBy(p => p.Item2).ThenBy(p => p.Item1))
        {
            string a = Find("L" + leg);
            string b = Find("F" + fid);
            if (a == b)
                return fid;
            parentOf[a] = b;
        }
        return null;
    }

    // -------------------------------------------------------------------- apply

    /// <summary>
    /// Perform the whole developed re-pitch in ONE break / move / remake, reusing
    /// the applier's helpers so the writing is the same code Slope Branch commits.
    ///
    /// One combined pass, not leg by leg: the legs share corner elbows, and a
    /// sequential per-leg apply would move an elbow while it was still joined to
    /// the next leg, dragging it. So every joint is broken first, every leg
    /// re-pitched and every group (including the corner elbows) moved, then every
    /// joint remade.
    /// </summary>
    public static ApplyResult Apply(Document doc, SlopePlan plan, double reconnectTol)
    {
        ApplyResult result = new();
        List<Leg> good = [.. plan.Legs.Where(l => l.Ok)];

        List<(Leg Leg, SlopeGroup Group)> movable = [];
        foreach (Leg leg in good)
        {
            foreach (SlopeGroup group in SlopeGroupUtil.Movable(leg.Groups))
                movable.Add((leg, group));
        }

        int steps = 2 + movable.Count;
        Dictionary<(long A, long B), int> broken;

        using (ProgressDialog progress = new("Slope Through - step {0} of {1}", cancellable: true))
        {
            broken = SlopeApplier.BreakJoints(doc, plan.Joints, result);
            progress.Update(1, steps);
            if (progress.Cancelled)
                throw new ApplyError("cancelled before anything moved");

            foreach (Leg leg in good)
                SlopeApplier.RepitchSegments(doc, leg.Pitch!, result);
            doc.Regenerate();
            progress.Update(2, steps);

            for (int index = 0; index < movable.Count; index++)
            {
                if (progress.Cancelled)
                {
                    throw new ApplyError(
                        $"cancelled after {index} of {movable.Count} group(s) - rolling back");
                }

                (Leg leg, SlopeGroup group) = movable[index];
                SlopeApplier.MoveGroup(doc, group, leg.Pitch!, result);
                progress.Update(3 + index, steps);
            }
        }

        doc.Regenerate();
        SlopeApplier.RemakeJoints(doc, plan.Joints, reconnectTol, result, broken);
        doc.Regenerate();

        // Replace the fixed end's own fitting fresh, if planned. Runs after the
        // ordinary remake above, which already rejoined the branch to the OLD
        // fitting; this deletes that and builds a new one instead of trying to
        // intercept the generic joint machinery.
        foreach (Leg leg in good)
        {
            if (leg.FixedRebuild is not null)
            {
                string? why = RebuildFixedElbow(leg.FixedRebuild, doc);
                if (why is not null)
                    result.FittingsNotRebuilt.Add((leg.FixedRebuild.ElbowId, why));
            }
            else if (leg.FixedTeeRebuild is not null)
            {
                // Survivable: a refusal rolls back only the fitting. The elbow
                // above still throws - NewElbowFitting is far more permissive
                // and that path is live-verified.
                string? why = RebuildFixedTee(leg.FixedTeeRebuild, doc);
                if (why is not null)
                    result.FittingsNotRebuilt.Add((leg.FixedTeeRebuild.TeeId, why));
            }
        }

        return result;
    }

    // ------------------------------------------------------------------ helpers

    internal static ElementId MakeElementId(long value)
    {
#if REVIT2023_OR_EARLIER
        return new ElementId((int)value);
#else
        return new ElementId(value);
#endif
    }

    public static Dictionary<long, ElementId> BuildIdMap(SlopePlan plan)
    {
        foreach (Leg leg in plan.Legs)
        {
            foreach (Element e in leg.Elements)
                plan.IdMap[RevitCompat.IdKey(e.Id)] = e.Id;
        }

        if (plan.FixedId is not null && !plan.IdMap.ContainsKey(plan.FixedId.Value))
            plan.IdMap[plan.FixedId.Value] = MakeElementId(plan.FixedId.Value);

        return plan.IdMap;
    }

    /// <summary>
    /// How far along the developed run this leg's ANCHOR sits, measured from the
    /// fixed end.
    ///
    /// Not the running total of pipe plan lengths: each leg's anchor elevation is
    /// chained through the CORNER ELBOW's centre, which sits past the end of the
    /// parent's pipe (~100 mm on a typical elbow). Those spans are genuinely part
    /// of the slope — the pitch integrates across them — so a developed length
    /// that omits them does not reconcile with the elevations it sits next to.
    /// </summary>
    public static double DevelopedAtAnchor(Leg leg)
    {
        if (leg.Parent is null || !leg.Parent.Ok || leg.CornerPoint is null)
            return 0.0;

        RePitch parentPitch = leg.Parent.Pitch!;
        double cornerStation = parentPitch.Run.Station(leg.CornerPoint.Value);

        return DevelopedAtAnchor(leg.Parent)
               + Math.Abs(cornerStation - parentPitch.AnchorStation);
    }

    /// <summary>
    /// The longest developed path from the fixed end. A branch that forks has no
    /// single developed length, so the deepest chain is the one that matters —
    /// it is the one that determines the largest rise.
    /// </summary>
    public static double DevelopedLength(SlopePlan plan)
    {
        double longest = 0.0;
        foreach (Leg leg in plan.Legs.Where(l => l.Ok))
        {
            RePitch pitch = leg.Pitch!;
            double extent = DevelopedAtAnchor(leg)
                            + Math.Abs(pitch.FarStation - pitch.AnchorStation);
            longest = Math.Max(longest, extent);
        }
        return longest;
    }
}
