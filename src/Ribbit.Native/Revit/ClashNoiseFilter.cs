using Autodesk.Revit.DB;
using Ribbit.Geometry.Clash;

namespace Ribbit.Native.Revit;

/// <summary>
/// Post-filter for the host-vs-host clash search. Ported from CDMEP.py's
/// is_noise_pair / get_connected_owner_ids.
///
/// WHY ONLY THIS TOOL NEEDS IT. <see cref="ClashEngine"/> is shared with the two
/// link tools, where Set A is host and Set B lives inside a link — two elements
/// in different documents can never be joined at a connector and one can never
/// be the other's insulation, so the engine has no concept of either. Clash
/// Detect draws BOTH sets from the same document, so both cases genuinely occur
/// and have to be removed here.
///
/// Without this the report is dominated by expected modelling geometry: every
/// pipe against its own fitting, every insulated pipe against its own
/// insulation, and — because Set A and Set B come from one pool — every element
/// against itself.
/// </summary>
internal sealed class ClashNoiseFilter
{
    /// <summary>
    /// Connected owner ids per element id. The connector walk is not cheap and
    /// the same element is asked about once per candidate pair it appears in.
    /// </summary>
    private readonly Dictionary<long, HashSet<long>> _connected = [];

    /// <summary>
    /// Ids directly connected to <paramref name="elem"/> through its connectors.
    /// </summary>
    private HashSet<long> ConnectedOwnerIds(Element elem)
    {
        long eid;
        try
        {
            eid = RevitCompat.IdKey(elem.Id);
        }
        catch (Exception)
        {
            return [];
        }

        if (_connected.TryGetValue(eid, out HashSet<long>? cached))
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
            // An element whose connectors will not enumerate has no neighbours
            // as far as this filter is concerned - it fails OPEN, keeping the
            // pair rather than dropping a possibly real clash.
        }

        _connected[eid] = owners;
        return owners;
    }

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
    /// True when the overlap is there by construction rather than being a
    /// coordination problem:
    ///
    ///   - the same element on both sides. Set A and Set B come from one pool,
    ///     so an element can appear in both; intersecting a solid with itself
    ///     has non-zero volume and would always "clash".
    ///   - one element is the other's insulation. Insulation wraps its host by
    ///     design.
    ///   - the two are joined at a shared connector. Joined pipes and fittings
    ///     are meant to meet - and so is insulation with whatever its host is
    ///     joined to.
    /// </summary>
    public bool IsNoisePair(Element a, Element b, long aId, long bId)
    {
        if (aId == bId)
            return true;

        // Insulation stands for the pipe or duct it wraps: an element and its own
        // lagging, and lagging against whatever its host is joined to, overlap
        // by construction. The same rule as FederatedNoiseFilter, where the
        // in-Revit suite proved the joined case was being reported.
        List<long> forA = StandsFor(a, aId);
        List<long> forB = StandsFor(b, bId);

        if (forA.Any(forB.Contains))
            return true;

        return JoinedFrom(a, aId, forA, forB) || JoinedFrom(b, bId, forB, forA);
    }

    private static List<long> StandsFor(Element e, long id)
    {
        long? host = InsulationHostId(e);
        return host is null ? [id] : [id, host.Value];
    }

    /// <summary>True when any element <paramref name="mine"/> names lists one of <paramref name="theirs"/> as connected.</summary>
    private bool JoinedFrom(Element e, long eId, List<long> mine, List<long> theirs)
    {
        foreach (long id in mine)
        {
            Element? el = e;
            if (id != eId)
            {
                try
                {
                    el = e.Document.GetElement(new ElementId(id));
                }
                catch (Exception)
                {
                    el = null;
                }
            }

            if (el is not null && theirs.Any(ConnectedOwnerIds(el).Contains))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Drops the noise pairs and collapses mirrored duplicates.
    ///
    /// The mirrored half matters as much as the noise: when Set A and Set B are
    /// drawn from the same pool, <see cref="ClashEngine"/> finds (X, Y) while
    /// walking Set B against X's candidates AND (Y, X) while walking it against
    /// Y's — one physical clash reported twice with the sides swapped. The
    /// engine cannot dedupe that itself, since for the link tools a pair and its
    /// mirror are genuinely different elements in different documents.
    /// </summary>
    public List<ClashPair> Apply(IEnumerable<ClashPair> pairs)
    {
        HashSet<(long, long)> seen = [];
        List<ClashPair> kept = [];

        foreach (ClashPair cp in pairs)
        {
            long aId, bId;
            try
            {
                aId = RevitCompat.IdKey(cp.A.Id);
                bId = RevitCompat.IdKey(cp.B.Id);
            }
            catch (Exception)
            {
                // A pair whose ids will not read cannot be judged; keep it
                // rather than silently discarding a possible clash.
                kept.Add(cp);
                continue;
            }

            if (IsNoisePair(cp.A, cp.B, aId, bId))
                continue;

            if (!seen.Add(ClashPairKey.Symmetric(aId, bId)))
                continue;

            kept.Add(cp);
        }

        return kept;
    }
}
