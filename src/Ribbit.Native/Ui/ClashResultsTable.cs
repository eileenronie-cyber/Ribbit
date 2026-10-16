using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ribbit.Geometry.Clash;
using Ribbit.Native.Revit;

namespace Ribbit.Native.Ui;

/// <summary>One results row as the page sees it. Short names — this ships per row.</summary>
internal sealed record ClashResultRowJson
{
    [JsonPropertyName("n")] public int ClashNum { get; init; }
    [JsonPropertyName("ck")] public string ClashKey { get; init; } = "";
    [JsonPropertyName("s")] public string Set { get; init; } = "";
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("lk")] public string Link { get; init; } = "";
    [JsonPropertyName("nm")] public string Name { get; init; } = "";
    [JsonPropertyName("sy")] public string System { get; init; } = "";
    [JsonPropertyName("lv")] public string Level { get; init; } = "";
    [JsonPropertyName("mt")] public string MatchType { get; init; } = "";
    [JsonPropertyName("st")] public string Status { get; init; } = "";
    [JsonPropertyName("pt")] public string Point { get; init; } = "";
    [JsonPropertyName("nw")] public bool IsNew { get; init; }
    [JsonPropertyName("cm")] public string Comment { get; init; } = "";
}

/// <summary>
/// The interactive clash results table.
///
/// Replaces the static <see cref="ReportHtml.Table"/> for clash results
/// specifically, and adds the three things the static table could not do:
/// copy an element id in one click, set a status without leaving the report,
/// and stay responsive on a report with thousands of pairs.
///
/// WHY THE ROWS ARE SHIPPED AS JSON rather than pre-rendered HTML. A large
/// model can produce thousands of clash pairs, and every pair is two rows. Baked
/// HTML would put all of them in the DOM at once, and then every filter
/// keystroke would walk that whole DOM. Sending the data and rendering a window
/// of it keeps the DOM to one page regardless of result size, and filtering runs
/// over a plain array instead.
/// </summary>
internal static class ClashResultsTable
{
    /// <summary>
    /// Rows rendered at a time. Big enough that most reports never page, small
    /// enough that a 20,000-row result still paints immediately.
    /// </summary>
    private const int PageSize = 400;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        // The DEFAULT encoder, deliberately: it escapes < and > to </>,
        // so an element name containing "</script>" cannot break out of the
        // script block it is embedded in.
        WriteIndented = false,
    };

    /// <summary>
    /// Appends the results section: toolbar, table and status bar.
    ///
    /// <paramref name="systemHeader"/> differs per tool (System Type vs System
    /// Abbreviation), so it is passed rather than assumed.
    /// </summary>
    /// <param name="comments">
    /// Note for a clash key, or "" — optional, so the three older reports keep
    /// their exact shape. Notes are keyed by clash PAIR, like status, which is
    /// why the lookup takes a key rather than being a field on the row: both of a
    /// pair's rows resolve to the same note without it being stored twice.
    /// </param>
    public static void Append(
        StringBuilder sb,
        IReadOnlyList<ClashReportRow> rows,
        string systemHeader,
        bool statusEditable,
        Func<string, string>? comments = null)
    {
        List<ClashResultRowJson> data =
        [
            .. rows.Select(r => new ClashResultRowJson
            {
                ClashNum = r.ClashNum,
                ClashKey = r.ClashKey,
                Set = r.Set,
                Id = r.Id.ToString(),
                Link = r.LinkName,
                Name = r.Name,
                System = r.SystemValue,
                Level = r.Level,
                MatchType = r.MatchType,
                Status = r.Status,
                Point = r.Point,
                IsNew = r.IsNew,
                Comment = comments?.Invoke(r.ClashKey) ?? "",
            })
        ];

        // The column appears only when a tool actually captured points, so the
        // three older reports keep their exact shape rather than growing an
        // empty column.
        bool hasPoint = rows.Any(r => r.Point.Length > 0);

        // Same rule for notes: no notes written yet means no column, rather than
        // an empty one on every report forever.
        bool hasComment = data.Any(d => d.Comment.Length > 0);

        // The New chip and badge only appear when something IS new - an empty
        // filter people can click and get nothing from is worse than no filter.
        bool hasNew = rows.Any(r => r.IsNew);

        sb.Append("<h2>Clashing elements</h2>");
        sb.Append(Style);

        sb.Append("<div class=\"rb-toolbar\">");
        sb.Append("<input id=\"rb-search\" type=\"search\" placeholder=\"Filter by id, name, "
                  + "system or level...\" autocomplete=\"off\">");
        // "Active" is the DEFAULT view and hides Accepted pairs. That is the
        // whole value of the Accepted bucket: a clash you have reviewed and
        // allowed stops competing for attention with the ones you have not.
        sb.Append("<div class=\"rb-chips\" id=\"rb-chips\">");
        sb.Append("<button class=\"rb-chip rb-on\" data-f=\"active\" "
                  + "title=\"Everything except Accepted\">Active</button>");
        sb.Append("<button class=\"rb-chip\" data-f=\"all\">All</button>");
        if (hasNew) sb.Append("<button class=\"rb-chip\" data-f=\"new\" "
            + "title=\"Only clashes this test had not seen before\">New</button>");
        sb.Append("<button class=\"rb-chip\" data-f=\"none\">Unflagged</button>");
        sb.Append("<button class=\"rb-chip\" data-f=\"Resolved\">Resolved</button>");
        sb.Append("<button class=\"rb-chip\" data-f=\"Pending\">Pending</button>");
        sb.Append("<button class=\"rb-chip\" data-f=\"Coordination\">Coordination</button>");
        sb.Append("<button class=\"rb-chip\" data-f=\"Accepted\">Accepted</button>");
        sb.Append("</div>");
        // Grouping. "One pipe run against forty beams" is forty rows you triage
        // one at a time; grouped it is one line you can collapse once you have
        // decided about it.
        sb.Append("<label class=\"rb-group-by\">Group "
                  + "<select id=\"rb-groupby\">"
                  + "<option value=\"none\">none</option>"
                  + "<option value=\"a\">by Set A element</option>"
                  + "<option value=\"lv\">by level</option>"
                  + "<option value=\"st\">by status</option>"
                  + "</select></label>");

        sb.Append("<button class=\"rb-btn\" id=\"rb-copy-all\" "
                  + "title=\"Copy every element id currently shown\">Copy ids</button>");
        sb.Append("<span class=\"rb-count muted\" id=\"rb-count\"></span>");
        sb.Append("</div>");

        sb.Append("<table class=\"rb-table\"><thead><tr>");
        sb.Append("<th>Clash #</th><th>Set</th><th>Element</th><th>Element Name</th>");
        sb.Append("<th>").Append(ReportHtml.Escape(systemHeader)).Append("</th>");
        sb.Append("<th>Reference Level</th><th>Match Type</th>");
        if (hasPoint) sb.Append("<th>Point (mm)</th>");
        if (hasComment) sb.Append("<th>Note</th>");
        sb.Append("<th>Status</th>");
        sb.Append("</tr></thead><tbody id=\"rb-body\"></tbody></table>");

        sb.Append("<div id=\"rb-more-wrap\" class=\"rb-more-wrap\" hidden>");
        sb.Append("<button class=\"rb-btn\" id=\"rb-more\"></button></div>");

        // Only rendered when something on screen can actually be edited, so the
        // link-vs-link case does not show a save bar that can never do anything.
        if (statusEditable)
        {
            sb.Append("<div class=\"rb-savebar\" id=\"rb-savebar\" hidden>");
            sb.Append("<span id=\"rb-dirty\"></span>");
            sb.Append("<button class=\"rb-btn rb-primary\" id=\"rb-save\">Save statuses</button>");
            sb.Append("<button class=\"rb-btn\" id=\"rb-revert\">Discard</button>");
            sb.Append("</div>");
        }

        sb.Append("<div class=\"rb-toast\" id=\"rb-toast\" hidden></div>");

        sb.Append("<script>const RB_ROWS=");
        sb.Append(JsonSerializer.Serialize(data, JsonOptions));
        sb.Append(";const RB_PAGE=").Append(PageSize);
        sb.Append(";const RB_POINT=").Append(hasPoint ? "true" : "false").Append(";const RB_EDIT=").Append(statusEditable ? "true" : "false");
        sb.Append(";const RB_NOTE=").Append(hasComment ? "true" : "false");
        sb.Append(";</script>");
        sb.Append("<script>").Append(Script).Append("</script>");
    }

    /// <summary>
    /// How many distinct clashes in this run are permanently accepted, and so
    /// hidden from the default view.
    ///
    /// Counted by CLASH KEY, not by row — every pair contributes two rows.
    /// </summary>
    public static int AcceptedCount(IReadOnlyList<ClashReportRow> rows)
        => rows.Where(r => r.Bucket == StatusBucket.Accepted)
               .Select(r => r.ClashKey)
               .Distinct()
               .Count();

    /// <summary>
    /// Says how many accepted pairs are being hidden. Without this the run's
    /// pair count and the visible row count disagree with no explanation.
    /// </summary>
    public static void AppendAcceptedNote(StringBuilder sb, IReadOnlyList<ClashReportRow> rows)
    {
        int accepted = AcceptedCount(rows);
        if (accepted == 0)
            return;

        ReportHtml.Note(sb,
            $"<strong>{accepted} clash(es) marked Accepted</strong> are hidden from the default "
            + "view - they were reviewed and permanently allowed. Use the <em>Accepted</em> or "
            + "<em>All</em> filter above to see them.");
    }

    /// <summary>
    /// Entries flagged before that are not clashing now — shown so a "Resolved"
    /// flag stays visible as confirmation rather than a fixed item silently
    /// vanishing with no trace it was tracked.
    ///
    /// One implementation for all three tools; it was three identical copies.
    /// </summary>
    public static void AppendStaleTable(
        StringBuilder sb,
        ClashLedger ledger,
        IReadOnlyList<ClashReportRow> rows,
        HashSet<string> scopeIds)
    {
        List<(string Label, string Status)> stale = ClashStatus.FindStale(
            ledger,
            rows.Select(r => r.ClashKey),
            rows.Select(r => r.Key),
            scopeIds);

        if (stale.Count == 0)
            return;

        List<IReadOnlyList<string>> tableRows =
        [
            .. stale.Select(s => new[]
            {
                ReportHtml.Escape(s.Label),
                ClashStatus.BadgeHtml(s.Status),
            })
        ];

        ReportHtml.Table(sb, "Previously flagged in this search, not currently clashing",
            ["Clash", "Status"], tableRows);

        ReportHtml.Note(sb,
            "These carry a status from an earlier run but did not clash this time. Restricted to "
            + "elements THIS search actually looked at, so a status set in an unrelated search "
            + "does not leak in. Accepted clashes are deliberately not listed.");
    }

    /// <summary>
    /// The timing line every clash report ends its summary with.
    ///
    /// SCAN AND CLASH ARE REPORTED SEPARATELY, plus their sum. A single
    /// "elapsed" would mislead: the clash timer starts after the input form
    /// closes, so a Browse scan done while the form was open falls outside it,
    /// and a run that used the picker would read FASTER than one that did not
    /// while the tool actually got no quicker.
    ///
    /// Scan reads 0.00s when Browse was never used — that work then happens
    /// inside set building and is already counted in Clash. Total is the
    /// like-for-like figure either way; it excludes only the time the form sat
    /// open waiting on input, which is not the tool's cost.
    /// </summary>
    public static void AppendTiming(
        StringBuilder sb, double scanSeconds, double clashSeconds, double cellSizeFt)
    {
        sb.Append($"<li><strong>Scan:</strong> {scanSeconds:F2}s &nbsp;|&nbsp; ")
          .Append($"<strong>Clash:</strong> {clashSeconds:F2}s &nbsp;|&nbsp; ")
          .Append($"<strong>Total:</strong> {scanSeconds + clashSeconds:F2}s</li>");

        sb.Append($"<li><strong>Grid cell size:</strong> {cellSizeFt:F2} ft</li>");
    }

    /// <summary>
    /// Which Description rule produced this report.
    ///
    /// Stated on EVERY report, in both states, because the rule changes which
    /// elements a keyword matches and therefore the clash count itself. Printed
    /// only when restricted, two reports of differing size would look like a
    /// model change rather than a setting change.
    /// </summary>
    /// <summary>
    /// Which named sets drove each side, when any did.
    ///
    /// A set is edited in the library between runs, so "52 Series" alone does not
    /// pin down what ran — but it does say WHICH set to open, which is the
    /// question someone reading an old report actually asks. Omitted entirely
    /// when neither side used a set, so keyword-only reports stay unchanged.
    /// </summary>
    public static void AppendSets(
        StringBuilder sb, IReadOnlyList<string> setA, IReadOnlyList<string> setB)
    {
        if (setA.Count == 0 && setB.Count == 0)
            return;

        static string Join(IReadOnlyList<string> names)
            => names.Count == 0 ? "none" : string.Join(", ", names);

        sb.Append("<li><strong>Sets:</strong> A = ")
          .Append(ReportHtml.Escape(Join(setA)))
          .Append(" &nbsp;|&nbsp; B = ")
          .Append(ReportHtml.Escape(Join(setB)))
          .Append("</li>");
    }

    public static void AppendMatchRule(StringBuilder sb, bool descriptionCableTrayOnly)
    {
        sb.Append("<li><strong>Description matched on:</strong> ")
          .Append(descriptionCableTrayOnly
              ? "cable tray and conduit only"
              : "all MEP categories")
          .Append("</li>");
    }

    private const string Style = """
        <style>
        .rb-toolbar { display:flex; flex-wrap:wrap; gap:8px; align-items:center;
                      margin:10px 0; position:sticky; top:0; z-index:3;
                      background:inherit; padding:6px 0; }
        #rb-search { flex:1 1 260px; min-width:200px; padding:5px 9px; font:inherit;
                     border:1px solid #c8c8c8; border-radius:4px;
                     background:transparent; color:inherit; }
        .rb-chips { display:flex; gap:4px; flex-wrap:wrap; }
        .rb-chip, .rb-btn {
          font:inherit; font-size:12px; padding:4px 10px; border-radius:12px;
          border:1px solid #c8c8c8; background:transparent; color:inherit;
          cursor:pointer; white-space:nowrap; }
        .rb-btn { border-radius:4px; }
        .rb-chip:hover, .rb-btn:hover { border-color:#0a66c2; }
        .rb-chip.rb-on { background:#0a66c2; border-color:#0a66c2; color:#fff; }
        .rb-primary { background:#0a66c2; border-color:#0a66c2; color:#fff; font-weight:600; }
        .rb-count { margin-left:auto; font-size:12px; }
        .rb-new { background:#0a66c2; color:#fff; font-size:9.5px; font-weight:700;
                  border-radius:3px; padding:1px 4px; margin-left:5px;
                  vertical-align:middle; letter-spacing:.04em; }
        .rb-note { max-width:260px; overflow:hidden; text-overflow:ellipsis; white-space:nowrap; }
        .rb-pair { text-decoration:none; font-size:13px; opacity:.65; }
        .rb-pair:hover { opacity:1; }
        .rb-group-by { font-size:12px; opacity:.85; display:inline-flex; align-items:center;
                       gap:4px; }
        .rb-group-by select { font-size:12px; padding:2px 4px; }
        .rb-grp td { background:rgba(128,128,128,.16); font-weight:600; font-size:12.5px;
                     padding:5px 8px; }
        .rb-grp-t { border:0; background:none; cursor:pointer; font-size:12px;
                    padding:0 5px 0 0; color:inherit; }
        .rb-table thead th { position:sticky; top:44px; z-index:2; background:#fff; }
        .rb-table td { vertical-align:middle; }
        /* Alternate shading per PAIR, not per row, so the two sides of one
           clash read as a unit. */
        tr.rb-alt td { background:#fafbfc; }
        tr.rb-changed td { background:#fff8e1 !important; }
        .rb-id { font-family:Consolas,monospace; }
        .rb-copy { border:none; background:transparent; cursor:pointer; opacity:.45;
                   font-size:12px; padding:0 4px; color:inherit; }
        .rb-copy:hover { opacity:1; }
        .rb-pills { display:inline-flex; gap:3px; }
        .rb-pill { font:inherit; font-size:11px; padding:2px 8px; border-radius:10px;
                   border:1px solid #c8c8c8; background:transparent; color:inherit;
                   cursor:pointer; white-space:nowrap; }
        .rb-pill:hover { border-color:#0a66c2; }
        .rb-pill.rb-sel { font-weight:600; border-color:transparent; }
        .rb-pill.rb-sel[data-v="Resolved"]     { background:#d1e7dd; color:#0f5132; }
        .rb-pill.rb-sel[data-v="Pending"]      { background:#f8d7da; color:#842029; }
        .rb-pill.rb-sel[data-v="Coordination"] { background:#cfe2ff; color:#084298; }
        .rb-pill.rb-sel[data-v="Accepted"]     { background:#e2e3e5; color:#41464b; }
        .rb-more-wrap { margin:0 0 16px; }
        .rb-savebar { position:sticky; bottom:0; z-index:4; display:flex; gap:8px;
                      align-items:center; padding:10px 14px; margin-top:8px;
                      background:#fff8e1; border-top:2px solid #d4a72c;
                      color:#1a1a1a; }
        .rb-toast { position:fixed; right:20px; bottom:20px; z-index:9;
                    padding:9px 14px; border-radius:4px; background:#1a7f37;
                    color:#fff; font-size:12px; box-shadow:0 2px 8px rgba(0,0,0,.3); }
        .rb-toast.rb-bad { background:#b3261e; }
        .rb-empty { padding:14px; font-style:italic; }
        @media (prefers-color-scheme: dark) {
          #rb-search, .rb-chip, .rb-btn, .rb-pill { border-color:#555; }
          .rb-table thead th { background:#1f1f1f; }
          tr.rb-alt td { background:#242628; }
          tr.rb-changed td { background:#3a3320 !important; }
          .rb-savebar { background:#3a3320; border-top-color:#d4a72c; color:#e6e6e6; }
        }
        </style>
        """;

    /// <summary>
    /// The page logic. Kept dependency-free — a strict CSP applies and there is
    /// no network here, so everything is hand-rolled rather than pulled in.
    /// </summary>
    private const string Script = """
        (function () {
          var STATUSES = ['Resolved', 'Pending', 'Coordination', 'Accepted'];
          var body = document.getElementById('rb-body');
          var search = document.getElementById('rb-search');
          var count = document.getElementById('rb-count');
          var moreWrap = document.getElementById('rb-more-wrap');
          var moreBtn = document.getElementById('rb-more');
          var saveBar = document.getElementById('rb-savebar');
          var dirtyLabel = document.getElementById('rb-dirty');
          var toast = document.getElementById('rb-toast');

          // clash key -> new status. Keyed on the PAIR, not the element: a pipe
          // clashing with three beams is three clashes, and flagging one of them
          // must not flag the other two.
          // Clash key -> the ids on it that live in the HOST model. Built once.
          //
          // A clash is two rows, so the pair's other id is only reachable by
          // looking across rows. Link-side ids are left out: they cannot be
          // selected from the host document, and selecting the one side that can
          // be beats refusing to select either.
          var pairIds = Object.create(null);
          for (var pi = 0; pi < RB_ROWS.length; pi++) {
            var pr = RB_ROWS[pi];
            if (!pr.ck || pr.lk) continue;
            (pairIds[pr.ck] = pairIds[pr.ck] || []).push(pr.id);
          }

          var changes = Object.create(null);
          var filtered = RB_ROWS;
          var COLS = (RB_POINT ? 9 : 8) + (RB_NOTE ? 1 : 0);
          var groupBy = 'none';
          var groupCounts = {};
          var collapsed = {};
          var lastGroup = null;
          var shown = 0;
          var statusFilter = 'active';
          var toastTimer = null;

          function keyOf(r) { return r.ck; }

          // A CLASH KEY IS REQUIRED, not just a host-side row. Pending edits are
          // held in `changes` keyed by exactly this, and the host drops any
          // update whose clashKey is empty - so without this test two keyless
          // rows would share the single changes[''] slot, show each other's
          // status, and be discarded on save while the page cleared them as
          // saved. ClashPairKey.For always returns at least '|', so nothing is
          // made uneditable today; this keeps the two ends agreeing by
          // construction rather than by luck.
          function editable(r) { return RB_EDIT && !r.lk && !!r.ck; }

          function statusOf(r) {
            var k = keyOf(r);
            return (k in changes) ? changes[k] : (r.st || '');
          }

          // Mirrors ClashStatus.Classify, including its rule ORDER - 'accept'
          // is tested first because "Accepted - coordinated" contains 'coord'
          // too and the stronger claim has to win.
          function bucketOf(s) {
            var t = (s || '').toLowerCase();
            if (!t) return '';
            if (t.indexOf('accept') >= 0) return 'Accepted';
            if (t.indexOf('resolv') >= 0 || t.indexOf('done') >= 0 || t.indexOf('fixed') >= 0)
              return 'Resolved';
            if (t.indexOf('pend') >= 0 || t.indexOf('open') >= 0) return 'Pending';
            if (t.indexOf('coord') >= 0) return 'Coordination';
            return '';
          }

          function esc(s) {
            return String(s == null ? '' : s).replace(/[&<>"]/g, function (c) {
              return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c];
            });
          }

          function post(msg) {
            try { window.chrome.webview.postMessage(JSON.stringify(msg)); }
            catch (e) { showToast('This action needs the report host.', true); }
          }

          function showToast(text, bad) {
            toast.textContent = text;
            toast.className = 'rb-toast' + (bad ? ' rb-bad' : '');
            toast.hidden = false;
            if (toastTimer) clearTimeout(toastTimer);
            toastTimer = setTimeout(function () { toast.hidden = true; }, 3200);
          }

          // Which Set A element each clash belongs to. Both rows of a pair share
          // a clash number, and exactly one of them is the A side, so this maps
          // the pair to the element people think of as "the one clashing".
          var aOf = {};
          for (var ai = 0; ai < RB_ROWS.length; ai++) {
            var ar = RB_ROWS[ai];
            if (ar.s === 'A' && !(ar.n in aOf)) aOf[ar.n] = ar;
          }

          function groupLabel(r) {
            if (groupBy === 'lv') return r.lv || '(no level)';
            if (groupBy === 'st') return bucketOf(statusOf(r)) || '(unflagged)';
            if (groupBy === 'a') {
              var a = aOf[r.n];
              if (!a) return '(unknown element)';
              return a.id + (a.nm ? '  ' + a.nm : '');
            }
            return '';
          }

          function applyFilter() {
            var q = search.value.trim().toLowerCase();
            filtered = RB_ROWS.filter(function (r) {
              if (statusFilter === 'new') { if (!r.nw) return false; }
              else if (statusFilter !== 'all') {
                var b = bucketOf(statusOf(r));
                if (statusFilter === 'active') { if (b === 'Accepted') return false; }
                else if (statusFilter === 'none') { if (b !== '') return false; }
                else if (b !== statusFilter) return false;
              }
              if (!q) return true;
              // Notes are searched too: "waiting on structural" typed once is
              // how you find every clash you parked, months later.
              return (r.id + ' ' + r.nm + ' ' + r.sy + ' ' + r.lv + ' ' + (r.cm || ''))
                .toLowerCase().indexOf(q) >= 0;
            });
            // Grouped rendering needs each group contiguous, so sort by label
            // BEFORE paging. Ties keep clash order, which keeps a pair's two
            // rows together.
            if (groupBy !== 'none') {
              filtered = filtered.slice().sort(function (x, y) {
                var lx = groupLabel(x), ly = groupLabel(y);
                if (lx < ly) return -1;
                if (lx > ly) return 1;
                return x.n - y.n;
              });

              // Clashes per group, not ROWS: every pair contributes two rows, so
              // counting rows would double every number on screen.
              groupCounts = {};
              var seenPair = {};
              for (var gi = 0; gi < filtered.length; gi++) {
                var gr = filtered[gi];
                var gk = groupLabel(gr);
                var pk = gk + '#' + gr.n;
                if (seenPair[pk]) continue;
                seenPair[pk] = 1;
                groupCounts[gk] = (groupCounts[gk] || 0) + 1;
              }
            }

            lastGroup = null;
            shown = 0;
            body.textContent = '';
            render();
          }

          function render() {
            if (!filtered.length) {
              body.innerHTML =
                '<tr><td colspan="' + COLS + '" class="rb-empty">Nothing matches this filter.</td></tr>';
              moreWrap.hidden = true;
              count.textContent = '0 of ' + RB_ROWS.length + ' rows';
              return;
            }

            var end = Math.min(shown + RB_PAGE, filtered.length);
            var html = [];

            for (var i = shown; i < end; i++) {
              var r = filtered[i];
              var st = statusOf(r);
              var changed = keyOf(r) in changes;
              var cls = (r.n % 2 === 0 ? 'rb-alt' : '') + (changed ? ' rb-changed' : '');

              // A group header whenever the label changes. lastGroup persists
              // across paged renders, so a group spanning a page boundary is not
              // re-announced halfway down.
              var glabel = groupBy === 'none' ? null : groupLabel(r);
              if (glabel !== null && glabel !== lastGroup) {
                lastGroup = glabel;
                var gn = groupCounts[glabel] || 0;
                html.push('<tr class="rb-grp" data-g="' + esc(glabel) + '">');
                html.push('<td colspan="' + COLS + '"><button class="rb-grp-t">' +
                          (collapsed[glabel] ? '&#9656;' : '&#9662;') + '</button> ' +
                          esc(glabel) + ' <span class="muted">- ' + gn +
                          ' clash' + (gn === 1 ? '' : 'es') + '</span></td></tr>');
              }

              html.push('<tr class="' + cls + '" data-i="' + i + '"' +
                        (glabel === null ? '' : ' data-gr="' + esc(glabel) + '"' +
                         (collapsed[glabel] ? ' hidden' : '')) + '>');
              // The clash number carries a "select the pair" link: both host
              // elements at once, so Selection Box in Revit frames the clash
              // itself instead of one element of it.
              var pids = pairIds[r.ck] || [];
              html.push('<td class="num">' + r.n +
                        (pids.length
                          ? ' <a class="rb-pair" href="ribbit:select?ids=' + esc(pids.join(',')) +
                            '" title="' + (pids.length > 1
                              ? 'Select both elements, then use Selection Box'
                              : 'Select the host element (the other side is in a link)') +
                            '">&#8853;</a>'
                          : '') +
                        (r.nw ? ' <span class="rb-new" title="Not seen the last time this test ran">NEW</span>' : '') + '</td>');
              html.push('<td>' + esc(r.s) + '</td>');

              // Id: a link that selects in Revit, plus a one-click copy. The
              // link side is not clickable - jumping into a link was never
              // verified - but its id is still copyable, which is the whole ask.
              html.push('<td><span class="rb-id">');
              html.push(r.lk
                ? esc(r.id) + ' <span class="muted">(in link: ' + esc(r.lk) + ')</span>'
                : '<a href="ribbit:select?ids=' + esc(r.id) + '">' + esc(r.id) + '</a>');
              html.push('</span><button class="rb-copy" data-copy="' + esc(r.id) +
                        '" title="Copy id">&#128203;</button></td>');

              html.push('<td>' + esc(r.nm) + '</td>');
              html.push('<td>' + esc(r.sy) + '</td>');
              html.push('<td>' + esc(r.lv) + '</td>');
              html.push('<td>' + esc(r.mt) + '</td>');
              if (RB_POINT) html.push('<td class="rb-pt">' + esc(r.pt || '-') + '</td>');

              // Trimmed in the cell, whole on hover. A note is a sentence and
              // the table is already wide; letting one wrap would give a pair's
              // two rows different heights for no gain.
              if (RB_NOTE) {
                html.push(r.cm
                  ? '<td class="rb-note" title="' + esc(r.cm) + '">' + esc(r.cm) + '</td>'
                  : '<td class="rb-note"><span class="muted">-</span></td>');
              }

              html.push('<td>');
              if (editable(r)) {
                var sel = bucketOf(st);
                html.push('<span class="rb-pills">');
                for (var j = 0; j < STATUSES.length; j++) {
                  var v = STATUSES[j];
                  html.push('<button class="rb-pill' + (sel === v ? ' rb-sel' : '') +
                            '" data-v="' + v + '" data-set="' + esc(keyOf(r)) + '">' +
                            v + '</button>');
                }
                html.push('</span>');
              } else {
                html.push(st ? esc(st) : '<span class="muted">-</span>');
              }
              html.push('</td></tr>');
            }

            body.insertAdjacentHTML('beforeend', html.join(''));
            shown = end;

            moreWrap.hidden = shown >= filtered.length;
            moreBtn.textContent =
              'Show ' + Math.min(RB_PAGE, filtered.length - shown) +
              ' more (' + (filtered.length - shown) + ' hidden)';
            count.textContent = filtered.length === RB_ROWS.length
              ? RB_ROWS.length + ' rows'
              : filtered.length + ' of ' + RB_ROWS.length + ' rows';
          }

          function refreshDirty() {
            if (!saveBar) return;
            var n = Object.keys(changes).length;
            saveBar.hidden = n === 0;
            dirtyLabel.textContent =
              n + ' clash' + (n === 1 ? '' : 'es') + ' changed, not yet saved.';
          }

          // One delegated listener for the whole table - with paging the rows
          // are replaced constantly, and per-row listeners would leak.
          body.addEventListener('click', function (ev) {
            var copy = ev.target.closest('[data-copy]');
            if (copy) {
              post({ action: 'copy', text: copy.getAttribute('data-copy') });
              return;
            }

            var pill = ev.target.closest('[data-set]');
            if (!pill) return;

            var key = pill.getAttribute('data-set');
            var val = pill.getAttribute('data-v');
            var current = filtered[+pill.closest('tr').getAttribute('data-i')];

            // Choosing what the ledger already said is not a change - so
            // clicking a pill back to its original value clears the dirty mark
            // instead of queueing a no-op write.
            if (bucketOf(current.st) === val) delete changes[key];
            else changes[key] = val;

            // Status is per ELEMENT, not per pair, and one element can appear in
            // several pairs. Every visible row for the same element has to move
            // together, or the same id would show two different statuses.
            var sel = bucketOf(statusOf(current));
            var dirty = key in changes;

            body.querySelectorAll('tr').forEach(function (tr) {
              var idx = tr.getAttribute('data-i');
              if (idx === null || keyOf(filtered[+idx]) !== key) return;

              tr.classList.toggle('rb-changed', dirty);
              tr.querySelectorAll('.rb-pill').forEach(function (p) {
                p.classList.toggle('rb-sel', p.getAttribute('data-v') === sel);
              });
            });

            refreshDirty();
          });

          search.addEventListener('input', applyFilter);

          document.getElementById('rb-chips').addEventListener('click', function (ev) {
            var chip = ev.target.closest('.rb-chip');
            if (!chip) return;
            statusFilter = chip.getAttribute('data-f');
            this.querySelectorAll('.rb-chip').forEach(function (c) {
              c.classList.toggle('rb-on', c === chip);
            });
            applyFilter();
          });

          var groupSel = document.getElementById('rb-groupby');
          if (groupSel) {
            groupSel.addEventListener('change', function () {
              groupBy = this.value;
              collapsed = {};
              applyFilter();
            });
          }

          // Collapse/expand. Rows carry their group label, so a group collapsed
          // now stays collapsed for rows that page in later.
          body.addEventListener('click', function (ev) {
            var head = ev.target.closest ? ev.target.closest('tr.rb-grp') : null;
            if (!head) return;

            var g = head.getAttribute('data-g');
            collapsed[g] = !collapsed[g];

            var tog = head.querySelector('.rb-grp-t');
            if (tog) tog.innerHTML = collapsed[g] ? '&#9656;' : '&#9662;';

            body.querySelectorAll('tr[data-gr]').forEach(function (tr) {
              if (tr.getAttribute('data-gr') === g) tr.hidden = collapsed[g];
            });
          });

          moreBtn.addEventListener('click', render);

          document.getElementById('rb-copy-all').addEventListener('click', function () {
            var seen = Object.create(null);
            var ids = [];
            for (var i = 0; i < filtered.length; i++) {
              if (seen[filtered[i].id]) continue;
              seen[filtered[i].id] = 1;
              ids.push(filtered[i].id);
            }
            post({ action: 'copy', text: ids.join('\n'), note: ids.length + ' id(s) copied.' });
          });

          if (saveBar) {
            document.getElementById('rb-save').addEventListener('click', function () {
              var updates = [];
              for (var k in changes) updates.push({ clashKey: k, status: changes[k] });
              if (!updates.length) return;
              post({ action: 'save', updates: updates });
            });

            document.getElementById('rb-revert').addEventListener('click', function () {
              changes = Object.create(null);
              refreshDirty();
              applyFilter();
            });
          }

          // Guarded: without it, a host that has not wired up the message
          // channel throws here and the table below never renders at all - the
          // report would come up blank over a feature nobody asked to use.
          try { window.chrome.webview.addEventListener('message', onHostReply); }
          catch (e) { /* display-only host; copy and save simply report it */ }

          function onHostReply(ev) {
            var res;
            try { res = JSON.parse(ev.data); } catch (e) { return; }
            if (!res) return;

            if (res.saved) {
              // The saved values are now the ledger's values, so the pending
              // set empties and those rows stop reading as unsaved.
              for (var k in changes) {
                for (var i = 0; i < RB_ROWS.length; i++) {
                  if (keyOf(RB_ROWS[i]) === k) RB_ROWS[i].st = changes[k];
                }
              }
              changes = Object.create(null);
              refreshDirty();
              applyFilter();
            }
            if (res.message) showToast(res.message, res.ok === false);
          }

          applyFilter();
          refreshDirty();
        })();
        """;
}
