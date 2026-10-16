using Autodesk.Revit.DB;
using Ribbit.Geometry.Clash;
using Ribbit.Native.Ui;

namespace Ribbit.Native.Revit;

/// <summary>
/// One report row — one element of one clash pair.
///
/// Carries BOTH the HTML cell for the on-screen table and the plain
/// <see cref="Id"/>/<see cref="LinkName"/> pair for the CSV, which must never
/// contain markup.
/// </summary>
internal sealed record ClashReportRow(
    int ClashNum,
    string ClashKey,
    string Set,
    long Id,
    string LinkName,
    string ElementCell,
    string Name,
    string SystemValue,
    string Level,
    string MatchType,
    string Status,
    string Point = "",
    bool IsNew = false)
{
    public string StatusBadge => ClashStatus.BadgeHtml(Status);

    /// <summary>
    /// The legacy element key. Still produced so reports written before status
    /// became pair-keyed keep resolving; <see cref="ClashKey"/> is what new
    /// flags are written against.
    /// </summary>
    public StatusKey Key => new(Id.ToString(), LinkName);

    public StatusBucket? Bucket => ClashStatus.Classify(Status);
}

/// <summary>
/// Which "system" column a tool's report carries, and the reader it must be
/// read through.
///
/// NOT interchangeable. The link tools report System Abbreviation via the
/// instance-first reader; Clash Detect reports System Type via FindParameter.
/// Swapping either half changes what appears in the report AND in the CSV
/// header, which is a shared on-disk format.
/// </summary>
internal sealed record SystemColumn(
    string Header, Func<ClashParameterReader, Element, object?> Read)
{
    public static readonly SystemColumn Abbreviation =
        new("System Abbreviation", (r, e) => r.GetParamInstanceFirst(e, "System Abbreviation"));

    public static readonly SystemColumn Type =
        new("System Type", (r, e) => r.FindParameter(e, ClashSetBuilder.SystemTypeParam));
}

/// <summary>
/// Builds the clash report's rows. Ported from the Revit half of
/// lib/clashlib/report.py.
/// </summary>
internal static class ClashReportRows
{
    /// <summary>
    /// The level an element belongs to, tried in the order the source used.
    ///
    /// Insulation is swapped for the pipe or duct it wraps first — insulation
    /// carries no level of its own, and reporting "-" for it would hide which
    /// floor the clash is actually on.
    /// </summary>
    public static string ReferenceLevelName(Document doc, Element? el)
    {
        if (el is null)
            return "-";

        // EVERY ID BELOW IS RESOLVED IN THE ELEMENT'S OWN DOCUMENT.
        //
        // `doc` is the HOST, and in a federated run half the rows are elements
        // living inside a link. A LevelId or an insulation HostElementId read
        // off a link element means nothing in the host: at best it resolves to
        // nothing and the level reads "-", at worst it hits an unrelated host
        // element of the same raw id and reports ITS name as the level - a
        // plausible-looking wrong answer, which is the worse outcome.
        //
        // The same bare-id-across-documents trap already fixed in the noise
        // filter and DedupeById. `doc` stays as the fallback for an element that
        // will not say which document it belongs to.
        Document elemDoc = doc;
        try
        {
            elemDoc = el.Document ?? doc;
        }
        catch (Exception)
        {
            // Keep the host document.
        }

        // Pipe/Duct insulation -> the host it wraps.
        if (el is InsulationLiningBase lining)
        {
            try
            {
                ElementId hostId = lining.HostElementId;
                if (hostId != ElementId.InvalidElementId && RevitCompat.IdKey(hostId) > 0)
                {
                    Element? host = elemDoc.GetElement(hostId);
                    if (host is not null)
                        el = host;
                }
            }
            catch (Exception)
            {
                // Keep the insulation element itself.
            }
        }

        // 1. ReferenceLevel
        try
        {
            if (el is MEPCurve curve && curve.ReferenceLevel is not null)
                return curve.ReferenceLevel.Name;
        }
        catch (Exception)
        {
            // Fall through.
        }

        // 2. LevelId
        try
        {
            string? byLevelId = LevelNameFromId(elemDoc, el.LevelId);
            if (byLevelId is not null)
                return byLevelId;
        }
        catch (Exception)
        {
            // Fall through.
        }

        // 3. Built-in level parameters
        foreach (BuiltInParameter bip in new[]
                 {
                     BuiltInParameter.RBS_START_LEVEL_PARAM,
                     BuiltInParameter.FAMILY_LEVEL_PARAM,
                 })
        {
            try
            {
                Parameter? p = el.get_Parameter(bip);
                if (p is not null)
                {
                    string? name = LevelNameFromId(elemDoc, p.AsElementId());
                    if (name is not null)
                        return name;
                }
            }
            catch (Exception)
            {
                // Try the next one.
            }
        }

        // 4. Fittings, accessories and cable-tray fittings carry no level of
        //    their own - borrow it from whatever they are connected to.
        try
        {
            foreach (Connector conn in MepConnectors.GetEndConnectors(el))
            {
                foreach (Connector r in conn.AllRefs)
                {
                    Element? owner = r.Owner;
                    if (owner is null || RevitCompat.IdKey(owner.Id) == RevitCompat.IdKey(el.Id))
                        continue;

                    try
                    {
                        string? name = LevelNameFromId(elemDoc, owner.LevelId);
                        if (name is not null)
                            return name;
                    }
                    catch (Exception)
                    {
                        // Try the parameter below.
                    }

                    try
                    {
                        Parameter? p = owner.get_Parameter(BuiltInParameter.RBS_START_LEVEL_PARAM);
                        if (p is not null)
                        {
                            string? name = LevelNameFromId(elemDoc, p.AsElementId());
                            if (name is not null)
                                return name;
                        }
                    }
                    catch (Exception)
                    {
                        // Try the next neighbour.
                    }
                }
            }
        }
        catch (Exception)
        {
            // Fall through to the name lookups.
        }

        // 5. Named parameters, last resort.
        foreach (string pname in new[] { "Reference Level", "Level", "Schedule Level", "Work Plane" })
        {
            try
            {
                Parameter? p = el.LookupParameter(pname);
                string? val = p?.AsValueString();
                if (!string.IsNullOrEmpty(val))
                    return val!;
            }
            catch (Exception)
            {
                // Try the next name.
            }
        }

        return "-";
    }

    private static string? LevelNameFromId(Document doc, ElementId? levelId)
    {
        try
        {
            if (levelId is null || levelId == ElementId.InvalidElementId
                || RevitCompat.IdKey(levelId) <= 0)
            {
                return null;
            }

            return doc.GetElement(levelId)?.Name;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string DisplayName(Element el)
    {
        try
        {
            string name = el.Name;
            return string.IsNullOrEmpty(name) ? "-" : name;
        }
        catch (Exception)
        {
            return "-";
        }
    }

    public static string LinkDisplayName(RevitLinkInstance? link)
    {
        if (link is null)
            return "";

        try
        {
            return link.Name ?? "";
        }
        catch (Exception)
        {
            return "";
        }
    }

    /// <summary>
    /// The element cell: a clickable link for host elements, plain text naming
    /// the link for linked ones.
    ///
    /// Jumping INTO a link from the report is deliberately not claimed — it was
    /// never verified working, so the link side shows the id plainly rather than
    /// offering a control that might do nothing. The report host makes the id
    /// selectable either way.
    /// </summary>
    private static string ElementCell(Element el, RevitLinkInstance? linkInst, long eid)
    {
        if (linkInst is null)
            return ReportHtml.Link(eid.ToString(), el.Id);

        return ReportHtml.Escape($"{eid}  (in link: {LinkDisplayName(linkInst)})");
    }

    /// <summary>
    /// One row per element per clash pair — both sides, grouped by a clash
    /// number so they sit together.
    ///
    /// DELIBERATELY NOT deduped by element: an element clashing with several
    /// others should appear once per pair, not once in total, so you can see
    /// exactly who clashes with whom. That also means no bare-id dedup ever
    /// runs, so two different physical elements sharing a raw ElementId in two
    /// different link documents can never be conflated into one row.
    /// </summary>
    public static List<ClashReportRow> Build(
        Document doc,
        ClashParameterReader reader,
        IReadOnlyList<ClashPair> pairs,
        SystemColumn systemColumn,
        ClashLedger? statusLedger = null,
        ISet<string>? newKeys = null)
    {
        List<ClashReportRow> rows = [];
        int clashNum = 0;

        // Local so the "Match Type" column keeps one meaning across the report,
        // the CSV and the ledger - three places that must not disagree.

        foreach (ClashPair pair in pairs)
        {
            clashNum++;
            string matchType = MatchTypeLabel(pair);

            // DISPLAY ONLY - deliberately not a CSV column. The CSV is a shared
            // on-disk format the Python tools read too, and adding a column to
            // it would break that. The point is for finding the clash on screen;
            // the CSV is for tracking its status.
            string point = PointLabel(pair);

            // Both rows of a pair carry the SAME key - that is the whole point.
            // Status is a property of the clash, not of either element.
            string clashKey = ClashPairKey.For(
                RevitCompat.IdKey(pair.A.Id).ToString(), LinkDisplayName(pair.AInstance),
                RevitCompat.IdKey(pair.B.Id).ToString(), LinkDisplayName(pair.BInstance));

            foreach ((Element el, RevitLinkInstance? linkInst, string side) in new[]
                     {
                         (pair.A, pair.AInstance, "A"),
                         (pair.B, pair.BInstance, "B"),
                     })
            {
                long eid = RevitCompat.IdKey(el.Id);
                string linkName = LinkDisplayName(linkInst);
                string sysText = ClashParameterReader.AsText(systemColumn.Read(reader, el));

                string status = statusLedger?.Lookup(
                    clashKey, new StatusKey(eid.ToString(), linkName)) ?? "";

                rows.Add(new ClashReportRow(
                    ClashNum: clashNum,
                    ClashKey: clashKey,
                    Set: side,
                    Id: eid,
                    LinkName: linkName,
                    ElementCell: ElementCell(el, linkInst, eid),
                    Name: DisplayName(el),
                    SystemValue: sysText.Length > 0 ? sysText : "-",
                    Level: ReferenceLevelName(doc, el),
                    MatchType: matchType,
                    Status: status ?? "",
                    Point: point,
                    IsNew: newKeys is not null && newKeys.Contains(clashKey)));
            }
        }

        return rows;
    }

    /// <summary>
    /// The clash point as "x, y, z" in MILLIMETRES, host coordinates.
    ///
    /// Millimetres because that is what everything else in these tools is typed
    /// in; host coordinates because that is the space the engine works in and
    /// the space Revit's own coordinate readout matches. Rounded to whole
    /// millimetres — a clash location is not a precision measurement, and six
    /// decimals of a foot would just be noise.
    /// </summary>
    internal static string PointLabel(ClashPair pair)
    {
        if (pair.Point is null)
            return "";

        try
        {
            return $"{pair.Point.X * 304.8:F0}, {pair.Point.Y * 304.8:F0}, "
                   + $"{pair.Point.Z * 304.8:F0}";
        }
        catch (Exception)
        {
            return "";
        }
    }

    /// <summary>
    /// A pair's match type, clearance included.
    ///
    /// A clearance pair carries volume 0, which
    /// <see cref="ClashStatus.MatchTypeLabel"/> alone would read as "no solid
    /// geometry" — true of the volume, and completely misleading about the pair.
    /// The gap is stated in millimetres because that is the unit the pad is
    /// almost always typed in.
    /// </summary>
    internal static string MatchTypeLabel(ClashPair pair)
        => pair.IsDuplicate
            ? "Duplicate (same size, same place)"
            : pair.GapFt is double gapFt
            ? $"Clearance ({gapFt * 304.8:F0} mm gap)"
            : ClashStatus.MatchTypeLabel(pair.Volume);

    /// <summary>
    /// Rows as CSV field dictionaries — no HTML, matching the Python's
    /// export_csv, which writes the plain id and link name rather than the
    /// rendered cell.
    /// </summary>
    public static (List<string> Fields, List<Dictionary<string, string>> Rows) ToCsv(
        IReadOnlyList<ClashReportRow> rows, SystemColumn systemColumn)
    {
        // Column names match what the Python tools write, so a CSV from either
        // implementation is readable by the other - the status ledger is a
        // shared on-disk format, not a private one. That includes the system
        // column's HEADER, which differs per tool.
        List<string> fields =
        [
            "Clash #", StatusLedger.ClashKeyField, "Set", "Element Id", "Link", "Element Name",
            systemColumn.Header, "Level", "Match Type", "Status",
        ];

        List<Dictionary<string, string>> outp = [];
        foreach (ClashReportRow r in rows)
        {
            outp.Add(new Dictionary<string, string>
            {
                ["Clash #"] = r.ClashNum.ToString(),
                // Stable across runs, unlike Clash # which renumbers. This is
                // what a saved status is keyed on.
                [StatusLedger.ClashKeyField] = r.ClashKey,
                ["Set"] = r.Set,
                ["Element Id"] = r.Id.ToString(),
                ["Link"] = r.LinkName,
                ["Element Name"] = r.Name,
                [systemColumn.Header] = r.SystemValue,
                ["Level"] = r.Level,
                ["Match Type"] = r.MatchType,
                ["Status"] = r.Status,
            });
        }

        return (fields, outp);
    }
}
