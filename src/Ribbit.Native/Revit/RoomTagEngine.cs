using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Ribbit.Geometry.Rooms;

namespace Ribbit.Native.Revit;

/// <summary>A level-based view that can carry room tags.</summary>
internal sealed record RtView(long Id, string Name, string LevelName, long LevelId, string Sheet, bool Active)
{
    public string Label => $"{Name}   -   {LevelName}   -   {(Sheet.Length > 0 ? Sheet : "not on a sheet")}";
}

/// <summary>One level of one loaded link that actually holds rooms.</summary>
internal sealed record RtSource(long LinkId, string LinkLabel, string LinkTitle, long LevelId, string LevelName, int Rooms)
{
    public string Label => $"{LevelName}   ({Rooms} room{(Rooms == 1 ? "" : "s")})";
}

/// <summary>What the scan found, and every link that is NOT offered, with why.</summary>
internal sealed class RtScan
{
    public List<RtView> Views { get; } = [];
    public List<RtSource> Sources { get; } = [];
    public string Description { get; set; } = "";
    public List<string> Notes { get; } = [];
    public int TagTypes { get; set; }

    /// <summary>Every Room Tag type, "Family : Type", and which one Revit places by default.</summary>
    public List<(long Id, string Name)> TagTypeList { get; } = [];
    public long DefaultTagType { get; set; } = -1;
}

/// <summary>What the run does. <paramref name="TagTypeId"/> -1 = the model's default Room Tag type.</summary>
internal sealed record RtOptions(bool TagMissing, bool OnlyInCrop, bool DeleteDuplicates, bool ShowRooms, long TagTypeId = -1);

/// <summary>One planned change.</summary>
internal sealed record RtTag(long ViewId, string ViewName, long LinkId, long RoomId, string Room, double X, double Y);
internal sealed record RtDelete(long ViewId, string ViewName, long TagId, long KeepId, string Room);

/// <summary>The whole plan: settled with no transaction open, so the window can show it before anything is written.</summary>
internal sealed class RtPlan
{
    public List<string> Report { get; } = [];
    public List<RtTag> Tags { get; } = [];
    public List<RtDelete> Deletes { get; } = [];
    public List<string> Skipped { get; } = [];
    public List<string> Log { get; } = [];

    /// <summary>View id -> the categories switched off in it that a linked room tag needs.</summary>
    public Dictionary<long, List<string>> Hidden { get; } = [];
    public int ViewsChecked { get; set; }
    public int Missing { get; set; }
    public int Duplicated { get; set; }
}

internal sealed class RtResult
{
    public int Placed { get; set; }
    public int Deleted { get; set; }
    public int Orphaned { get; set; }
    public List<string> Tagged { get; } = [];
    public List<string> Skipped { get; } = [];
    public List<string> Log { get; } = [];
}

/// <summary>
/// Room Tag Checker - the Revit half (was "Room Tag Checker v3.dyn"): finds rooms
/// in a linked model, on the level a plan view is PAIRED with, that have no tag
/// in that view, and tags them there; finds rooms carrying more than one tag and
/// deletes the extras.
///
/// Rules kept from the graph:
///   - the level is never guessed: the window pairs each host level with a
///     linked level (by name where the names agree, by asking where not);
///   - only rooms inside the view's crop (a split or edited crop tested as a
///     polygon, not its bounding box), placed (an unplaced room has no location);
///   - tags are matched on the link INSTANCE too - two instances of one file share
///     room ids;
///   - no new tag within 1 ft of a tag head already in the view;
///   - a duplicate tag borrowed by another user is never deleted;
///   - Rooms / Room Tags / RVT Links switched back on before tagging, so the tags
///     are not created invisible;
///   - every link not offered is named with its reason.
/// </summary>
internal static class RoomTagEngine
{
    private static readonly (BuiltInCategory Cat, string Label)[] TagNeeds =
    [
        (BuiltInCategory.OST_Rooms, "Rooms"),
        (BuiltInCategory.OST_RoomTags, "Room Tags"),
        (BuiltInCategory.OST_RvtLinks, "RVT Links"),
    ];

    private const int MaxNesting = 4;

    // ------------------------------------------------------------------ scan

    public static RtScan Scan(Document doc)
    {
        RtScan scan = new();
        Dictionary<long, string> onSheet = [];
        foreach (Viewport vp in new FilteredElementCollector(doc).OfClass(typeof(Viewport)).Cast<Viewport>())
            if (doc.GetElement(vp.SheetId) is ViewSheet s)
                onSheet[vp.ViewId.Value] = s.Name.Length > 0 ? $"{s.SheetNumber} - {s.Name}" : s.SheetNumber;

        long active = doc.ActiveView?.Id.Value ?? -1;
        foreach (View v in new FilteredElementCollector(doc).OfClass(typeof(View)).WhereElementIsNotElementType().Cast<View>())
        {
            if (v.IsTemplate || v.GenLevel is not Level level || level.Name.Length == 0)
                continue;
            scan.Views.Add(new RtView(v.Id.Value, v.Name, level.Name, level.Id.Value,
                onSheet.TryGetValue(v.Id.Value, out string? sh) ? sh : "", v.Id.Value == active));
        }
        scan.Views.Sort((a, b) =>
        {
            int c = string.Compare(a.LevelName, b.LevelName, StringComparison.OrdinalIgnoreCase);
            return c != 0 ? c : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        });

        // Both collectors, merged by id: a model where one misses an instance still gets it offered.
        Dictionary<long, RevitLinkInstance> links = [];
        foreach (RevitLinkInstance l in new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>())
            links[l.Id.Value] = l;
        foreach (RevitLinkInstance l in new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_RvtLinks).WhereElementIsNotElementType().OfType<RevitLinkInstance>())
            links.TryAdd(l.Id.Value, l);

        Dictionary<long, List<(long Id, string Name, int Count)>?> byType = [];
        HashSet<string> hostKeys = [];
        List<(string Label, Document Doc)> loaded = [];
        List<string> empty = [], unloaded = [], failed = [], offered = [];
        HashSet<string> usedLabels = [];

        foreach (RevitLinkInstance link in links.Values.OrderBy(l => l.Name, StringComparer.OrdinalIgnoreCase))
        {
            string label = link.Name.Length > 0 ? link.Name : $"Link {link.Id.Value}";
            try
            {
                RevitLinkType? type = doc.GetElement(link.GetTypeId()) as RevitLinkType;
                if (type is { IsNestedLink: true })
                    label += $"  [nested in {doc.GetElement(type.GetParentId())?.Name ?? "another link"}]";
                Document? linkDoc = link.GetLinkDocument();
                if (linkDoc is null)
                {
                    unloaded.Add($"{label} - {WhyNotLoaded(doc, link, type)}");
                    continue;
                }
                loaded.Add((label, linkDoc));
                hostKeys.Add(DocKey(linkDoc));
                long typeKey = link.GetTypeId().Value;
                if (!byType.TryGetValue(typeKey, out List<(long, string, int)>? levels))
                    byType[typeKey] = levels = LevelsHoldingRooms(linkDoc);
                if (levels is null)
                {
                    failed.Add($"{label} - its rooms could not be read");
                    continue;
                }
                if (levels.Count == 0)
                {
                    empty.Add(label);
                    continue;
                }
                string unique = RoomTagMath.UniqueLabel(label, usedLabels);
                offered.Add(unique);
                foreach ((long id, string name, int count) in levels)
                    scan.Sources.Add(new RtSource(link.Id.Value, unique, TitleOf(linkDoc), id, name, count));
            }
            catch (Exception ex)
            {
                failed.Add($"{label} - {ex.Message}");
            }
        }

        // Rooms only in a NESTED link cannot be tagged through it - named, not offered.
        List<string> nestedOnly = [];
        HashSet<string> seen = [.. hostKeys];
        void Walk(string parentLabel, Document parent, int depth)
        {
            if (depth > MaxNesting)
                return;
            foreach (RevitLinkInstance child in new FilteredElementCollector(parent).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>())
            {
                Document? cd = child.GetLinkDocument();
                if (cd is null || !seen.Add(DocKey(cd)))
                    continue;
                int count = LevelsHoldingRooms(cd)?.Sum(l => l.Count) ?? 0;
                if (count > 0)
                    nestedOnly.Add($"{cd.Title} inside {parentLabel} ({count} rooms)");
                Walk(cd.Title, cd, depth + 1);
            }
        }
        foreach ((string label, Document d) in loaded)
            Walk(label, d, 1);

        foreach (FamilySymbol t in new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_RoomTags).WhereElementIsElementType().OfType<FamilySymbol>()
                     .OrderBy(t => t.FamilyName, StringComparer.OrdinalIgnoreCase).ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase))
            scan.TagTypeList.Add((t.Id.Value, $"{t.FamilyName} : {t.Name}"));
        scan.DefaultTagType = doc.GetDefaultFamilyTypeId(new ElementId(BuiltInCategory.OST_RoomTags)).Value;
        scan.TagTypes = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_RoomTags).WhereElementIsElementType().GetElementCount();

        if (scan.Views.Count == 0)
            scan.Description = "This model has no level-based views. Room tags are placed in a plan view, so there is nothing to check.";
        else if (scan.Sources.Count == 0)
            scan.Description = links.Count == 0 ? "This model has no Revit links. The rooms live in the linked architectural model, so there is nothing to check against."
                : loaded.Count == 0 ? $"None of the {links.Count} Revit link(s) is loaded. The rooms live in the linked architectural model, so there is nothing to check against."
                : nestedOnly.Count > 0 ? $"None of the {loaded.Count} loaded link(s) placed in this model holds a room - the rooms are in links nested inside them, listed below."
                : $"None of the {loaded.Count} loaded link(s) contains a single room. The rooms are probably in a link that is not loaded, or one you have not linked in.";
        else
            scan.Description = $"{scan.Views.Count} level-based view(s) on {scan.Views.Select(v => v.LevelId).Distinct().Count()} level(s). "
                               + $"{scan.Sources.Sum(s => s.Rooms)} room(s) across {scan.Sources.Count} level(s) in {offered.Count} of {links.Count} Revit link(s).";

        if (empty.Count > 0)
            scan.Notes.Add("Loaded but holding no rooms: " + string.Join("; ", empty.OrderBy(x => x)) + ".");
        if (unloaded.Count > 0)
            scan.Notes.Add("Not loaded, so not offered: " + string.Join("; ", unloaded.OrderBy(x => x)) + ".");
        if (failed.Count > 0)
            scan.Notes.Add("Could not be read: " + string.Join("; ", failed.OrderBy(x => x)) + ".");
        if (nestedOnly.Count > 0)
            scan.Notes.Add("Rooms in nested links, which Revit cannot tag through - link those files into this model directly: " + string.Join("; ", nestedOnly.OrderBy(x => x)) + ".");
        if (scan.TagTypes == 0)
            scan.Notes.Add("No Room Tag type is loaded - load a Room Tag family before tagging.");
        return scan;
    }

    /// <summary>The link file's name without ".rvt" - Title carries the extension only when Windows shows extensions.</summary>
    private static string TitleOf(Document d)
        => d.Title.EndsWith(".rvt", StringComparison.OrdinalIgnoreCase) ? d.Title.Substring(0, d.Title.Length - 4) : d.Title;

    private static string DocKey(Document d) => (d.PathName.Length > 0 ? d.PathName : d.Title).Trim().ToLowerInvariant();

    private static string WhyNotLoaded(Document doc, RevitLinkInstance link, RevitLinkType? type)
    {
        try
        {
            if (doc.IsWorkshared && doc.GetWorksetTable().GetWorkset(link.WorksetId) is { IsOpen: false } ws)
                return $"on closed workset '{ws.Name}'";
        }
        catch (Exception) { }
        try
        {
            return type?.GetLinkedFileStatus() switch
            {
                LinkedFileStatus.Unloaded => "unloaded",
                LinkedFileStatus.LocallyUnloaded => "unloaded for you only (Unload For Me)",
                LinkedFileStatus.NotFound => "file not found at its saved path",
                LinkedFileStatus.InClosedWorkset => "its workset is closed",
                LinkedFileStatus.Invalid => "the link is invalid",
                _ => "not loaded",
            };
        }
        catch (Exception)
        {
            return "not loaded";
        }
    }

    private static long RoomLevelId(Room r)
    {
        try
        {
            if (r.LevelId.Value > 0)
                return r.LevelId.Value;
        }
        catch (Exception) { }
        return r.get_Parameter(BuiltInParameter.ROOM_LEVEL_ID)?.AsElementId()?.Value ?? -1;
    }

    private static string RoomLevelName(Room r) => r.Level?.Name ?? r.Document.GetElement(r.LevelId)?.Name ?? "(no level)";

    /// <summary>The levels of a linked document that hold rooms, busiest first; null when the rooms could not be read.</summary>
    private static List<(long Id, string Name, int Count)>? LevelsHoldingRooms(Document linkDoc)
    {
        try
        {
            return [.. new FilteredElementCollector(linkDoc).OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType()
                .OfType<Room>()
                .GroupBy(RoomLevelId)
                .Where(g => g.Key > 0)
                .Select(g => (g.Key, RoomLevelName(g.First()), g.Count()))
                .OrderByDescending(x => x.Item3).ThenBy(x => x.Item2, StringComparer.OrdinalIgnoreCase)];
        }
        catch (Exception)
        {
            return null;
        }
    }

    // ------------------------------------------------------------------ plan

    public static string RoomLabel(Room r)
    {
        string name = r.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString() ?? "";
        return name.Length > 0 ? $"{r.Number} - {name}" : r.Number ?? "Unknown room";
    }

    /// <summary>(test, description). The test is null when nothing should be filtered out.</summary>
    private static (Func<XYZ, bool>? Test, string Note) CropTest(View view)
    {
        if (!view.CropBoxActive)
            return (null, "not cropped - the whole level is in scope");
        List<IReadOnlyList<Pt2>> loops = [];
        try
        {
            foreach (CurveLoop loop in view.GetCropRegionShapeManager().GetCropShape())
            {
                List<Pt2> pts = [.. loop.SelectMany(c => c.Tessellate()).Select(p => new Pt2(p.X, p.Y))];
                if (pts.Count >= 3)
                    loops.Add(pts);
            }
        }
        catch (Exception) { }
        if (loops.Count > 0)
            return (p => RoomTagMath.InAnyLoop(p.X, p.Y, loops), $"crop region ({loops.Count} loop(s))");

        BoundingBoxXYZ box = view.CropBox;
        Transform inverse = box.Transform.Inverse;
        return (p =>
        {
            XYZ l = inverse.OfPoint(p);
            return l.X >= box.Min.X && l.X <= box.Max.X && l.Y >= box.Min.Y && l.Y <= box.Max.Y;
        }, "crop box");
    }

    private static string ScopeBoxName(Document doc, View view)
    {
        ElementId? id = view.get_Parameter(BuiltInParameter.VIEWER_VOLUME_OF_INTEREST_CROP)?.AsElementId();
        return id is not null && doc.GetElement(id) is Element e ? e.Name : "";
    }

    private static List<string> HiddenNeeds(View view)
        => [.. TagNeeds.Where(n =>
        {
            try { return view.GetCategoryHidden(new ElementId(n.Cat)); }
            catch (Exception) { return false; }
        }).Select(n => n.Label)];

    private static string OwnerOf(Document doc, ElementId id)
    {
        try
        {
            if (!doc.IsWorkshared || WorksharingUtils.GetCheckoutStatus(doc, id) != CheckoutStatus.OwnedByOtherUser)
                return "";
            string owner = WorksharingUtils.GetWorksharingTooltipInfo(doc, id).Owner;
            return owner.Length > 0 ? owner : "another user";
        }
        catch (Exception)
        {
            return "";
        }
    }

    /// <summary>
    /// Plan every (view, room source) pair with nothing open. A 5000-room link
    /// checked across 20 plans reads its rooms once per (link, level), not per view.
    /// </summary>
    public static RtPlan Plan(Document doc, IReadOnlyList<(long ViewId, RtSource Source)> pairs, RtOptions opt)
    {
        RtPlan plan = new();
        Dictionary<(long, long), (List<Room> All, List<Room> OnLevel)> cache = [];

        foreach ((long viewId, RtSource src) in pairs)
        {
            if (doc.GetElement(new ElementId(viewId)) is not View view)
            {
                plan.Log.Add($"View {viewId} is no longer in the model.");
                continue;
            }
            if (doc.GetElement(new ElementId(src.LinkId)) is not RevitLinkInstance link || link.GetLinkDocument() is not Document linkDoc)
            {
                plan.Log.Add($"{view.Name}:  '{src.LinkLabel}' is no longer loaded. Load it and check again.");
                continue;
            }
            plan.ViewsChecked++;
            try
            {
                PlanView(doc, view, link, linkDoc, src, opt, plan, cache);
            }
            catch (Exception ex)
            {
                plan.Report.Add($"{view.Name}");
                plan.Report.Add($"   This view stopped early: {ex.Message}");
                plan.Report.Add("");
            }
        }

        if (plan.Skipped.Any(s => s.Contains("a tag head is already within")))
            plan.Log.Add($"A room is skipped as too close when a tag head sits within {RoomTagMath.DuplicateProximityFt:0} ft of it. A small untagged room "
                         + "(a shaft, a cupboard) packed against a tagged neighbour can be caught by that - check the Skipped list.");
        return plan;
    }

    private static void PlanView(Document doc, View view, RevitLinkInstance link, Document linkDoc, RtSource src, RtOptions opt,
        RtPlan plan, Dictionary<(long, long), (List<Room> All, List<Room> OnLevel)> cache)
    {
        List<string> block = [];
        if (!cache.TryGetValue((link.Id.Value, src.LevelId), out (List<Room> All, List<Room> OnLevel) rooms))
        {
            List<Room> all = [.. new FilteredElementCollector(linkDoc).OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType().OfType<Room>()];
            rooms = (all, [.. all.Where(r => RoomLevelId(r) == src.LevelId)]);
            cache[(link.Id.Value, src.LevelId)] = rooms;
        }

        Transform transform = link.GetTotalTransform();
        (Func<XYZ, bool>? crop, string cropNote) = opt.OnlyInCrop ? CropTest(view) : (null, "ignored, as asked - the whole level is in scope");

        Dictionary<long, (Room Room, XYZ Point)> inScope = [];
        int unplaced = 0, outside = 0;
        foreach (Room r in rooms.OnLevel)
        {
            if (r.Location is not LocationPoint lp)
            {
                unplaced++;
                continue;
            }
            XYZ p = transform.OfPoint(lp.Point);
            if (crop is not null && !crop(p))
            {
                outside++;
                continue;
            }
            inScope[r.Id.Value] = (r, p);
        }

        Dictionary<long, List<RoomTag>> tagsByRoom = [];
        List<Pt2> heads = [];
        int orphaned = 0, local = 0, otherInstance = 0;
        // Every tag the view OWNS, not the ones it shows. A view-scoped collector
        // (the graph's) skips elements the view does not draw, so in a plan with Rooms
        // or Room Tags switched off it found no tags at all and planned a SECOND tag
        // for every room that already had one - measured by the "roomtags" suite.
        foreach (RoomTag tag in new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_RoomTags).WhereElementIsNotElementType()
                     .WherePasses(new ElementOwnerViewFilter(view.Id)).OfType<RoomTag>())
        {
            if (tag.IsOrphaned)
            {
                orphaned++;
                continue;
            }
            // Every head counts for the proximity guard, whichever instance it belongs to.
            XYZ head = tag.TagHeadPosition;
            heads.Add(new Pt2(head.X, head.Y));
            if (!tag.IsTaggingLink)
            {
                local++;
                continue;
            }
            LinkElementId tagged = tag.TaggedRoomId;
            if (tagged.LinkInstanceId.Value != link.Id.Value)
            {
                otherInstance++;
                continue;
            }
            long roomId = tagged.LinkedElementId.Value;
            if (!tagsByRoom.TryGetValue(roomId, out List<RoomTag>? list))
                tagsByRoom[roomId] = list = [];
            list.Add(tag);
        }

        List<(Room Room, XYZ Point)> missing = [.. inScope.Values.Where(x => !tagsByRoom.ContainsKey(x.Room.Id.Value)).OrderBy(x => RoomLabel(x.Room), StringComparer.Ordinal)];
        List<(Room Room, List<RoomTag> Tags)> dupes = [.. inScope.Values.Where(x => tagsByRoom.TryGetValue(x.Room.Id.Value, out List<RoomTag>? t) && t.Count > 1)
            .Select(x => (x.Room, tagsByRoom[x.Room.Id.Value])).OrderBy(x => RoomLabel(x.Room), StringComparer.Ordinal)];
        int extra = dupes.Sum(d => d.Item2.Count - 1);
        plan.Missing += missing.Count;
        plan.Duplicated += dupes.Count;

        string viewLevel = view.GenLevel?.Name ?? "(none)";
        bool differ = RoomTagMath.Normalize(viewLevel) != RoomTagMath.Normalize(src.LevelName);
        string scope = ScopeBoxName(doc, view);
        string sheet = view.get_Parameter(BuiltInParameter.VIEWPORT_SHEET_NUMBER)?.AsString() ?? "";
        block.Add($"{view.Name}      ({(sheet.Length > 0 ? "sheet " + sheet : "no sheet")})");
        block.Add($"   View's level:  {viewLevel}");
        block.Add($"   Rooms taken from:  {src.LinkLabel}, level {src.LevelName}{(differ ? "   <- named differently from the view's level" : "")}");
        block.Add($"   View crop:  {cropNote}{(scope.Length > 0 ? $"   scope box '{scope}'" : "")}");
        block.Add($"   Rooms on that level:  {rooms.OnLevel.Count}   ({unplaced} unplaced, {outside} outside the crop)");
        block.Add($"   Rooms in scope:  {inScope.Count}   already tagged: {inScope.Count - missing.Count}   missing: {missing.Count}   duplicated: {dupes.Count} ({extra} extra tag(s))");
        if (local > 0)
            block.Add($"   Room tags on host-model rooms, left alone:  {local}");
        if (otherInstance > 0)
            block.Add($"   Room tags belonging to another link instance, left alone:  {otherInstance}");
        if (orphaned > 0)
            block.Add($"   Orphaned room tags in this view:  {orphaned}");

        List<string> hidden = HiddenNeeds(view);
        if (hidden.Count > 0)
        {
            plan.Hidden[view.Id.Value] = hidden;
            block.Add($"   Switched OFF in this view:  {string.Join(", ", hidden)} - a tag on a linked room is only drawn when these are on.");
        }
        try
        {
            RevitLinkGraphicsSettings? g = view.GetLinkOverrides(link.Id);
            if (g is not null && g.LinkVisibilityType != LinkVisibility.ByHostView)
                block.Add($"   Link display in this view:  {g.LinkVisibilityType} - not 'by host view', so check Visibility/Graphics > Revit Links if tags do not appear.");
        }
        catch (Exception) { }

        if (inScope.Count == 0)
        {
            plan.Log.Add(rooms.All.Count == 0 ? $"{view.Name}:  '{src.LinkLabel}' contains no rooms at all. Pick the architectural link."
                : outside > 0 ? $"{view.Name}:  all {outside} room(s) on '{src.LevelName}' sit outside this view's crop. Untick \"Only rooms inside the crop\" to take the whole level, or check the crop and scope box."
                : rooms.OnLevel.Count > 0 ? $"{view.Name}:  all {rooms.OnLevel.Count} room(s) on '{src.LevelName}' are unplaced - they have to be placed in the linked model first."
                : $"{view.Name}:  '{src.LinkLabel}' offered {src.Rooms} room(s) on '{src.LevelName}' when it was scanned, and none are there now. Close and run again.");
            plan.Report.AddRange(block);
            plan.Report.Add("");
            return;
        }
        if (missing.Count == 0 && dupes.Count == 0)
            block.Add("   Nothing to do - every room in scope has exactly one tag.");

        if (opt.TagMissing)
        {
            List<Pt2> sofar = [.. heads];
            foreach ((Room room, XYZ p) in missing)
            {
                Pt2 at = new(p.X, p.Y);
                if (RoomTagMath.NearAny(at, sofar, RoomTagMath.DuplicateProximityFt))
                {
                    plan.Skipped.Add($"{view.Name}  |  {RoomLabel(room)}  -  a tag head is already within {RoomTagMath.DuplicateProximityFt:0} ft of it");
                    continue;
                }
                plan.Tags.Add(new RtTag(view.Id.Value, view.Name, link.Id.Value, room.Id.Value, RoomLabel(room), p.X, p.Y));
                sofar.Add(at);
            }
        }

        if (opt.DeleteDuplicates)
        {
            foreach ((Room room, List<RoomTag> tags) in dupes)
            {
                (long keep, List<long> extras) = RoomTagMath.SplitDuplicates(tags.Select(t => t.Id.Value));
                foreach (long tagId in extras)
                {
                    string owner = OwnerOf(doc, new ElementId(tagId));
                    if (owner.Length > 0)
                        plan.Skipped.Add($"{view.Name}  |  {RoomLabel(room)}  -  duplicate tag {tagId} is borrowed by {owner}");
                    else
                        plan.Deletes.Add(new RtDelete(view.Id.Value, view.Name, tagId, keep, RoomLabel(room)));
                }
            }
        }

        plan.Report.AddRange(block);
        plan.Report.Add("");
    }

    // ------------------------------------------------------------------ apply

    /// <summary>
    /// Carry out a plan. ONE undo step: the tagging and the deletions run in a
    /// TransactionGroup. Each placement and each deletion has its own
    /// SubTransaction, so one element Revit refuses does not take the batch with it.
    /// </summary>
    public static RtResult Apply(Document doc, RtPlan plan, RtOptions opt)
    {
        RtResult res = new();
        if (plan.Tags.Count == 0 && plan.Deletes.Count == 0)
        {
            res.Log.Add("Nothing to change - the model was not touched.");
            return res;
        }

        using TransactionGroup group = new(doc, "Room Tag Checker");
        group.Start();

        if (plan.Tags.Count > 0)
        {
            using Transaction t = new(doc, "Tag missing rooms");
            ErrorCollector errors = ErrorCollector.Start(t);
            List<string> switched = [];
            List<ElementId> made = [];
            foreach (IGrouping<long, RtTag> byView in plan.Tags.GroupBy(x => x.ViewId))
            {
                View? view = doc.GetElement(new ElementId(byView.Key)) as View;
                if (view is null)
                {
                    res.Skipped.AddRange(byView.Select(x => $"{x.ViewName}  |  {x.Room}  -  the view is gone"));
                    continue;
                }
                if (plan.Hidden.TryGetValue(byView.Key, out List<string>? hidden))
                {
                    if (opt.ShowRooms)
                    {
                        List<string> on = [], stuck = [];
                        foreach ((BuiltInCategory cat, string label) in TagNeeds.Where(n => hidden.Contains(n.Label)))
                        {
                            try
                            {
                                ElementId cid = new(cat);
                                if (view.CanCategoryBeHidden(cid) && view.GetCategoryHidden(cid))
                                {
                                    view.SetCategoryHidden(cid, false);
                                    on.Add(label);
                                }
                                else
                                {
                                    stuck.Add(label);
                                }
                            }
                            catch (Exception)
                            {
                                stuck.Add(label);
                            }
                        }
                        if (on.Count > 0)
                            switched.Add($"Switched on in '{view.Name}':  {string.Join(", ", on)}.");
                        if (stuck.Count > 0)
                            res.Log.Add($"{view.Name}:  could not switch on {string.Join(", ", stuck)} (a view template controls it?) - tags placed there will not show until it is on.");
                    }
                    else
                    {
                        res.Log.Add($"{view.Name}:  {string.Join(" and ", hidden)} left switched off, as asked - the new tags exist but will not be drawn.");
                    }
                }

                foreach (RtTag x in byView)
                {
                    using SubTransaction sub = new(doc);
                    sub.Start();
                    RoomTag? tag = null;
                    string why = "Revit did not create a tag, and gave no reason";
                    try
                    {
                        tag = doc.Create.NewRoomTag(new LinkElementId(new ElementId(x.LinkId), new ElementId(x.RoomId)), new UV(x.X, x.Y), view.Id);
                    }
                    catch (Exception ex)
                    {
                        why = "Revit refused to tag it: " + ex.Message;
                    }
                    if (tag is null)
                    {
                        sub.RollBack();
                        res.Skipped.Add($"{x.ViewName}  |  {x.Room}  -  {why}");
                        continue;
                    }
                    if (opt.TagTypeId > 0 && tag.GetTypeId().Value != opt.TagTypeId)
                    {
                        try
                        {
                            tag.ChangeTypeId(new ElementId(opt.TagTypeId));
                        }
                        catch (Exception ex)
                        {
                            res.Log.Add($"{x.ViewName}  |  {x.Room}: tagged with the default type - the chosen one was refused ({ex.Message})");
                        }
                    }
                    sub.Commit();
                    made.Add(tag.Id);
                    res.Tagged.Add($"{x.ViewName}  |  {x.Room}");
                }
            }

            if (made.Count > 0)
            {
                // A tag whose room the view cannot resolve comes back orphaned - only after a regen.
                doc.Regenerate();
                res.Orphaned = made.Count(id => doc.GetElement(id) is RoomTag { IsOrphaned: true });
                if (t.Commit() == TransactionStatus.Committed)
                {
                    res.Placed = made.Count;
                    res.Log.AddRange(switched);
                    res.Log.Add($"{made.Count} of {plan.Tags.Count} planned tag(s) placed.");
                    if (res.Orphaned > 0)
                        res.Log.Add($"{res.Orphaned} of them came back ORPHANED - Revit made the tag but cannot resolve its room in that view: check the view range and the link's Visibility/Graphics.");
                }
                else
                {
                    res.Tagged.Clear();
                    res.Log.Add("Revit rolled the tagging back: " + string.Join("; ", errors.Errors.DefaultIfEmpty("no reason given")));
                }
            }
            else
            {
                t.RollBack();
                res.Log.Add("No tag could be placed - the tagging was rolled back.");
            }
        }

        if (plan.Deletes.Count > 0)
        {
            using Transaction t = new(doc, "Delete duplicate room tags");
            ErrorCollector errors = ErrorCollector.Start(t);
            int removed = 0;
            foreach (RtDelete d in plan.Deletes)
            {
                using SubTransaction sub = new(doc);
                sub.Start();
                try
                {
                    doc.Delete(new ElementId(d.TagId));
                    sub.Commit();
                    removed++;
                }
                catch (Exception ex)
                {
                    sub.RollBack();
                    res.Skipped.Add($"{d.ViewName}  |  {d.Room}  -  Revit refused to delete tag {d.TagId}: {ex.Message}");
                }
            }
            if (removed > 0 && t.Commit() == TransactionStatus.Committed)
            {
                res.Deleted = removed;
                res.Log.Add($"{removed} of {plan.Deletes.Count} duplicate tag(s) deleted.");
            }
            else
            {
                if (t.GetStatus() == TransactionStatus.Started)
                    t.RollBack();
                res.Log.Add(removed > 0 ? "Revit rolled the deletions back: " + string.Join("; ", errors.Errors.DefaultIfEmpty("no reason given"))
                    : "No duplicate tag could be deleted.");
            }
        }

        if (res.Placed > 0 || res.Deleted > 0)
            group.Assimilate();
        else
            group.RollBack();
        return res;
    }
}
