using Autodesk.Revit.DB;
using Ribbit.Geometry.Clash;

namespace Ribbit.Native.Revit;

/// <summary>
/// Noise filter for a FEDERATED run, where both sides draw from the host model
/// and every ticked link at once.
///
/// WHY THIS IS NOT <see cref="ClashNoiseFilter"/>. That one keys on the raw
/// ElementId with no document component, which is correct for Clash Detect
/// because Clash Detect is single-document. Here it would be actively dangerous:
/// ElementIds are per-document, so a host element and a DIFFERENT linked element
/// routinely share an id. A bare-id filter would read that as "the same element
/// on both sides" and silently drop a real clash — under-reporting, which is the
/// worst failure a clash tool has because a clean run looks like good news.
///
/// So every key here is (Document, ElementId), the same key
/// <see cref="ClashSetBuilder.DedupeById"/> already uses for exactly this reason.
///
/// The insulation and connector tests are additionally skipped unless both
/// elements are in the SAME document. Neither relationship can span one: a
/// link's connector graph stops at its own document boundary, and insulation
/// wraps a host inside its own model. Comparing those ids across documents can
/// only produce false positives.
/// </summary>
internal sealed class FederatedNoiseFilter
{
    /// <summary>Connected owner keys per element, scoped by document.</summary>
    private readonly Dictionary<(Document Doc, long Id), HashSet<long>> _connected = [];

    /// <summary>
    /// Insulation pairs dropped because the host already reported the same
    /// clash. Counted separately from the self/joined/mirrored drops because it
    /// is the one a reader may want to argue with — see
    /// <see cref="DropInsulationAlreadyReportedByItsHost"/>.
    /// </summary>
    public int InsulationDuplicatesDropped { get; private set; }

    private static (Document, long)? KeyOf(Element? elem)
    {
        try
        {
            if (elem?.Document is null)
                return null;

            return (elem.Document, RevitCompat.IdKey(elem.Id));
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Ids directly connected to <paramref name="elem"/>, within its own document.
    /// Raw ids are safe INSIDE this set because it is only ever compared against
    /// an element from the same document.
    /// </summary>
    private HashSet<long> ConnectedOwnerIds(Element elem, (Document Doc, long Id) key)
    {
        if (_connected.TryGetValue(key, out HashSet<long>? cached))
            return cached;

        HashSet<long> owners = [];
        try
        {
            foreach (Connector conn in MepConnectors.GetAllConnectors(elem))
            {
                foreach (Connector r in conn.AllRefs)
                {
                    try
                    {
                        Element? owner = r.Owner;
                        if (owner is not null)
                            owners.Add(RevitCompat.IdKey(owner.Id));
                    }
                    catch (Exception)
                    {
                        // Skip an unreadable reference.
                    }
                }
            }
        }
        catch (Exception)
        {
            // Fails OPEN - an element whose connectors will not enumerate keeps
            // its pairs rather than dropping a possibly real clash.
        }

        _connected[key] = owners;
        return owners;
    }

    // A hosted-element test lived here briefly, suppressing a family against the
    // wall it is hosted on. REMOVED: equipment recessed into an architectural or
    // structural wall is a penetration someone has to agree to, not noise, and
    // this tool exists to surface exactly that. Do not reintroduce it — the only
    // relationships treated as noise below are ones that cannot be a
    // coordination issue at all.

    /// <summary>The host an insulation element wraps, or null.</summary>
    private static long? InsulationHostId(Element elem)
    {
        try
        {
            if (elem is InsulationLiningBase lining)
            {
                ElementId hostId = lining.HostElementId;
                if (hostId is not null && hostId != ElementId.InvalidElementId)
                    return RevitCompat.IdKey(hostId);
            }
        }
        catch (Exception)
        {
            // Not resolvable - treat as not insulation.
        }

        return null;
    }

    /// <summary>
    /// One side of a pair: the element, its document, and WHICH PLACEMENT of
    /// that document it was found through - 0 for the host, otherwise the link
    /// instance's id.
    ///
    /// The placement is part of the identity. A link placed twice is two
    /// physical copies of every element in it, and a host pipe running through
    /// both copies of one linked box is two clashes - keyed on (document,
    /// element) alone, the second was dropped as a "mirror" of the first.
    /// </summary>
    private readonly record struct Side(Document Doc, long Id, long Placement)
    {
        public string Key => $"{DocKey(Doc)}|{Placement}|{Id}";
    }

    private static long PlacementOf(RevitLinkInstance? inst)
    {
        try
        {
            return inst is null ? 0 : RevitCompat.IdKey(inst.Id);
        }
        catch (Exception)
        {
            return 0;
        }
    }

    /// <summary>
    /// The ids an element stands for when asking "are these joined?": itself,
    /// and for insulation also the pipe or duct it wraps. Insulation follows its
    /// host's connections - at a joint the lagging overlaps the next pipe or
    /// fitting by construction, exactly as the bare pipes do.
    /// </summary>
    private static List<long> StandsFor(Element e, long id)
    {
        long? host = InsulationHostId(e);
        return host is null ? [id] : [id, host.Value];
    }

    /// <summary>The element an id names inside a document, or null.</summary>
    private static Element? ElementIn(Document doc, long id)
    {
        try
        {
            return doc.GetElement(new ElementId(id));
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// True when the overlap exists by construction rather than being a
    /// coordination problem. Every test is document- AND placement-aware.
    /// </summary>
    private bool IsNoisePair(Element a, Element b, Side sa, Side sb)
    {
        // HOSTING IS NOT NOISE. Equipment recessed into an architectural or
        // structural wall was briefly suppressed here on the reasoning that the
        // overlap is "by construction". That is wrong for coordination: a
        // penetration or recess through someone else's wall is exactly what the
        // architect and engineer need told about, and hiding it means it reaches
        // site undiscussed. Hosted overlaps are reported like any other.
        //
        // Every relationship below lives inside ONE copy of ONE model: across
        // documents, or across two placements of the same link, nothing is
        // joined, wrapped or the same element - two copies of one linked
        // element overlapping each other is a duplicate placement, and reported.
        if (!sa.Doc.Equals(sb.Doc) || sa.Placement != sb.Placement)
            return false;

        // The same element on both sides.
        if (sa.Id == sb.Id)
            return true;

        List<long> forA = StandsFor(a, sa.Id);
        List<long> forB = StandsFor(b, sb.Id);

        // An element and its own insulation (or two layers on one host).
        if (forA.Any(forB.Contains))
            return true;

        // Joined by connectors - the elements themselves, or the pipes their
        // insulation wraps. Asked from BOTH sides: connector lookup fails open
        // to "no neighbours", so a side whose connectors will not enumerate
        // must not decide on its own that two joined elements are a clash.
        return JoinedFrom(a, sa, forA, forB) || JoinedFrom(b, sb, forB, forA);
    }

    /// <summary>True when any element <paramref name="mine"/> names lists one of <paramref name="theirs"/> as connected.</summary>
    private bool JoinedFrom(Element e, Side side, List<long> mine, List<long> theirs)
    {
        foreach (long id in mine)
        {
            Element? el = id == side.Id ? e : ElementIn(side.Doc, id);
            if (el is null)
                continue;

            HashSet<long> joined = ConnectedOwnerIds(el, (side.Doc, id));
            if (theirs.Any(joined.Contains))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Drops noise pairs and collapses mirrored duplicates.
    ///
    /// The mirror problem is why this must run at all in a federated search: with
    /// both sides drawn from overlapping pools, the engine finds (X, Y) and
    /// (Y, X) for one physical clash. The dedup key is symmetric over the FULL
    /// document-, placement- and element-qualified keys, so two genuinely
    /// different pairs that happen to share raw ids - or that are the same
    /// elements in two placements of one link - are never collapsed into one.
    /// </summary>
    public List<ClashPair> Apply(IEnumerable<ClashPair> pairs)
    {
        HashSet<(string, string)> seen = [];
        List<ClashPair> kept = [];

        // The sides of each kept pair, positionally matched to kept, so the
        // insulation pass below can ask whether a HOST's pair also survived
        // without recomputing any of this. Null for a pair that would not
        // identify itself, which that pass then leaves alone.
        List<(Side A, Side B)?> keptSides = [];

        foreach (ClashPair cp in pairs)
        {
            (Document, long)? ka = KeyOf(cp.A);
            (Document, long)? kb = KeyOf(cp.B);

            if (ka is null || kb is null)
            {
                // A pair that will not identify itself cannot be judged; keep it
                // rather than silently discarding a possible clash.
                kept.Add(cp);
                keptSides.Add(null);
                continue;
            }

            Side a = new(ka.Value.Item1, ka.Value.Item2, PlacementOf(cp.AInstance));
            Side b = new(kb.Value.Item1, kb.Value.Item2, PlacementOf(cp.BInstance));

            if (IsNoisePair(cp.A, cp.B, a, b))
                continue;

            // Document identity enters the key as its PathName - two different
            // Document objects for the same file compare unequal by reference,
            // which would let a mirrored pair through.
            string sa = a.Key;
            string sb = b.Key;

            if (!seen.Add(string.CompareOrdinal(sa, sb) <= 0 ? (sa, sb) : (sb, sa)))
                continue;

            kept.Add(cp);
            keptSides.Add((a, b));
        }

        return DropInsulationAlreadyReportedByItsHost(kept, keptSides, seen);
    }

    /// <summary>
    /// Drops an insulation pair when the pipe or duct it wraps ALREADY reports a
    /// clash against the same counterpart.
    ///
    /// One physical problem, two rows. A federated set is built through
    /// <c>ClashLinkSetBuilder</c>, whose expansion pulls in the insulation around
    /// every matched pipe and duct — so where a bare pipe clashes with a beam,
    /// its insulation almost always clashes with that beam too, and both were
    /// reported. Measured on a real 396-clash run: 44 pairs had insulation on one
    /// side and 35 of those duplicated the host's own pair.
    ///
    /// ONLY WHEN THE HOST'S PAIR SURVIVED. An insulation clash whose host does
    /// NOT clash is the opposite of noise: it means the pipe itself fits and the
    /// lagging does not, which is a real coordination problem and one the
    /// non-federated tools cannot find at all, because they never put insulation
    /// in a set. In the same run 9 of the 44 were exactly that, and all 9 are
    /// kept.
    ///
    /// A SECOND PASS, because the question can only be asked once every pair that
    /// survives is known — the host's pair may appear after the insulation's.
    /// </summary>
    private List<ClashPair> DropInsulationAlreadyReportedByItsHost(
        List<ClashPair> kept,
        List<(Side A, Side B)?> sides,
        HashSet<(string, string)> seen)
    {
        List<ClashPair> outp = [];

        for (int i = 0; i < kept.Count; i++)
        {
            (Side A, Side B)? s = sides[i];

            if (s is null)
            {
                outp.Add(kept[i]);
                continue;
            }

            ClashPair cp = kept[i];

            // Either side may be the insulation: both sets are drawn from the
            // same pools here, so a wrapped pipe can land on B just as easily.
            bool redundant =
                HostPairAlreadyKept(cp.A, s.Value.A, s.Value.B, seen)
                || HostPairAlreadyKept(cp.B, s.Value.B, s.Value.A, seen);

            if (redundant)
                InsulationDuplicatesDropped++;
            else
                outp.Add(cp);
        }

        return outp;
    }

    /// <summary>
    /// True when <paramref name="maybeInsulation"/> wraps a host that is itself
    /// reported against <paramref name="other"/>.
    /// </summary>
    private static bool HostPairAlreadyKept(
        Element maybeInsulation, Side self, Side other, HashSet<(string, string)> seen)
    {
        long? hostId = InsulationHostId(maybeInsulation);
        if (hostId is null)
            return false;

        // Insulation wraps a host in its OWN document and its own placement,
        // so the host's side carries both.
        string host = (self with { Id = hostId.Value }).Key;
        string opposite = other.Key;

        return seen.Contains(
            string.CompareOrdinal(host, opposite) <= 0 ? (host, opposite) : (opposite, host));
    }

    /// <summary>
    /// A stable identity for a document. PathName is empty for an unsaved model,
    /// so Title backs it up; the pair of them is enough to tell the host from any
    /// link within one run.
    /// </summary>
    private static string DocKey(Document doc)
    {
        try
        {
            string path = doc.PathName ?? "";
            return path.Length > 0 ? path : doc.Title ?? "";
        }
        catch (Exception)
        {
            return "";
        }
    }
}
