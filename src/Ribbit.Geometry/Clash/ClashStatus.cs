using System.Text.RegularExpressions;

namespace Ribbit.Geometry.Clash;

/// <summary>What produced a clash-pair volume. Ported from <c>match_type_label</c>.</summary>
public enum ClashMatchType
{
    /// <summary>A real Boolean intersection with measurable volume.</summary>
    SolidIntersection,

    /// <summary>
    /// The Boolean threw and was failed open into a clash. Exactly the threshold
    /// value, which a successful computation never returns.
    /// </summary>
    BooleanFailedOpen,

    /// <summary>
    /// Padded bounding boxes only — either side lacked usable solid geometry, or
    /// the intersection computed as negligible. The engine cannot distinguish
    /// those two without carrying an extra flag, and lumping them is enough to
    /// know the row needs a second look.
    /// </summary>
    BoundingBoxOnly,
}

/// <summary>The canonical coordination buckets a free-text status maps to.</summary>
public enum StatusBucket
{
    Resolved,
    Pending,
    Coordination,

    /// <summary>
    /// Reviewed and permanently allowed — a pipe through its own sleeve, a
    /// crossing with signed-off clearance.
    ///
    /// Distinct from Resolved on purpose. Resolved asserts the geometry was
    /// FIXED, so a Resolved pair that is still clashing is a contradiction worth
    /// flagging. Accepted asserts it was JUDGED, so it clashing again forever is
    /// the expected outcome and must not nag — without this bucket the warning
    /// fires every run on exactly the items already dealt with, which teaches
    /// people to skim past the warning.
    /// </summary>
    Accepted,
}

/// <summary>One ledger key: an element id paired with the link it came from.</summary>
public readonly record struct StatusKey(string ElementId, string LinkName);

/// <summary>
/// Clash status classification and labelling. Ported from the pure half of
/// lib/clashlib/report.py.
///
/// Status lives in the CSV, not in the model — no transaction needed, and it
/// works identically for host and linked elements. It is keyed by the CLASH
/// PAIR (see <see cref="ClashPairKey"/>) rather than by whichever run produced
/// it, so re-running a different search later still finds status set earlier.
///
/// Reports written before pair keys existed carry only an element id; those
/// still resolve through <see cref="ClashLedger"/>'s legacy map.
/// </summary>
public static class ClashStatus
{
    /// <summary>The volume the engine returns when a Boolean throws.</summary>
    public const double FailOpenVolume = 1e-6;

    public static ClashMatchType ClassifyVolume(double volume)
    {
        if (volume > FailOpenVolume)
            return ClashMatchType.SolidIntersection;
        if (volume == FailOpenVolume)
            return ClashMatchType.BooleanFailedOpen;
        return ClashMatchType.BoundingBoxOnly;
    }

    public static string MatchTypeLabel(double volume) => ClassifyVolume(volume) switch
    {
        ClashMatchType.SolidIntersection => "Solid intersection",
        ClashMatchType.BooleanFailedOpen => "Boolean op failed (fail-open)",
        _ => "No solid geometry (BB-only)",
    };

    /// <summary>
    /// SUBSTRING rules, not exact matches, and first match wins in this order.
    /// A person hand-typing a status writes "Resolve", "Resolved", "Fixed",
    /// "Pending review", "For coordination - see RFI 12" — not the one exact word
    /// an exact-match table would demand.
    ///
    /// The accepted tradeoff: a phrasing like "Not resolved yet" would match
    /// "resolv" and read as resolved. That is a known cost of substring matching
    /// on a free-text field, not something worth a parser for.
    /// </summary>
    private static readonly (string Needle, StatusBucket Bucket)[] BucketRules =
    [
        // "accept" comes FIRST because first match wins and a real-world
        // "Accepted - coordinated with structure" contains "coord" too. Accepted
        // is the stronger claim, so it has to win that overlap.
        ("accept", StatusBucket.Accepted),

        ("resolv", StatusBucket.Resolved),
        ("done", StatusBucket.Resolved),
        ("fixed", StatusBucket.Resolved),
        ("pend", StatusBucket.Pending),
        ("open", StatusBucket.Pending),
        ("coord", StatusBucket.Coordination),
    ];

    /// <summary>(text colour, background colour) per bucket.</summary>
    public static (string Text, string Background) Colors(StatusBucket bucket) => bucket switch
    {
        StatusBucket.Resolved => ("#0f5132", "#d1e7dd"),
        StatusBucket.Pending => ("#842029", "#f8d7da"),
        // Deliberately neutral grey, not a fourth colour competing for attention:
        // an accepted clash is closed business and should recede.
        StatusBucket.Accepted => ("#41464b", "#e2e3e5"),
        _ => ("#084298", "#cfe2ff"),
    };

    public static string Label(StatusBucket bucket) => bucket switch
    {
        StatusBucket.Resolved => "Resolved",
        StatusBucket.Pending => "Pending",
        StatusBucket.Accepted => "Accepted",
        _ => "Coordination",
    };

    /// <summary>
    /// Which bucket a free-text status belongs to, or null when blank or
    /// unrecognized. The status editor uses this to pre-select a button, so it
    /// must be the same classification the badge colouring uses rather than a
    /// second copy that could drift.
    /// </summary>
    public static StatusBucket? Classify(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
            return null;

        string key = status!.Trim().ToLowerInvariant();
        foreach ((string needle, StatusBucket bucket) in BucketRules)
        {
            if (key.Contains(needle))
                return bucket;
        }
        return null;
    }

    /// <summary>
    /// A coloured pill. An unrecognized status falls back to plain text rather
    /// than being hidden — a status you cannot miss beats one that silently
    /// vanishes because nobody taught the classifier that wording.
    /// </summary>
    public static string BadgeHtml(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
            return "-";

        string label = status!.Trim();
        StatusBucket? bucket = Classify(status);
        if (bucket is null)
            return label;

        (string text, string bg) = Colors(bucket.Value);
        return $"<span style=\"display:inline-block;padding:2px 10px;border-radius:10px;"
               + $"font-weight:600;font-size:0.85em;color:{text};background:{bg};\">{label}</span>";
    }

    // ------------------------------------------------------------ filenames

    /// <summary>
    /// Lowercase, anything outside a-z0-9 collapsed to a single underscore — for
    /// embedding free text (keywords, category names) into a filename.
    /// </summary>
    public static string SanitizeFilenamePart(string? text)
    {
        string s = (text ?? "").Trim().ToLowerInvariant();
        s = Regex.Replace(s, "[^a-z0-9]+", "_").Trim('_');
        return s.Length > 0 ? s : "none";
    }

    /// <summary>
    /// The prefix every CSV for one model+tool shares. Must match
    /// <see cref="BuildCsvFilename"/>'s sanitizing exactly, or files this tool
    /// wrote will not be recognized as belonging to this model.
    /// </summary>
    public static string ModelCsvPrefix(string? modelTitle, string productTag)
        => $"{SanitizeModelTitle(modelTitle)}_{productTag}_";

    /// <summary>
    /// A model title as it appears inside a filename.
    ///
    /// Extracted so a caller reading a title back OUT of a filename can compare
    /// like with like. Recovering it by trimming a prefix built with a dummy tag
    /// worked, and was the kind of thing that breaks the day the format moves.
    /// </summary>
    public static string SanitizeModelTitle(string? modelTitle)
        => Regex.Replace(
            string.IsNullOrEmpty(modelTitle) ? "Untitled" : modelTitle!, "[^A-Za-z0-9_-]+", "_");

    /// <summary>Longest test name allowed in a filename. See BuildCsvFilename.</summary>
    public const int MaxTestNameLength = 40;

    /// <summary>
    /// A saved test's name as it appears inside a filename, or "" for no test.
    ///
    /// Shared with the READER so the two cannot drift: the status editor finds a
    /// test's reports by looking for this token, and a token built one way and
    /// searched for another would just quietly find nothing.
    ///
    /// Note <see cref="SanitizeFilenamePart"/> answers "none" for empty input,
    /// which is a legitimate token elsewhere but here would invent a test — so
    /// blank is short-circuited before it gets there.
    /// </summary>
    public static string TestNamePart(string? testName)
    {
        if (string.IsNullOrWhiteSpace(testName))
            return "";

        string part = SanitizeFilenamePart(testName);
        return part.Length > MaxTestNameLength
            ? part.Substring(0, MaxTestNameLength).TrimEnd('_')
            : part;
    }

    /// <summary>
    /// {ModelTitle}_{productTag}_{YYYYMMDD-HHMM}_{test}_{tag}{N}.csv, where N
    /// starts at 1 and increments past whatever already exists — so writing the
    /// same thing twice in a minute never collides or overwrites.
    ///
    /// THE {Model}_{productTag}_ PREFIX IS LOAD-BEARING and stays first: the
    /// ledger migration and FindLatestFullReport both match on it, so a name
    /// that leads with anything else orphans every report already written. The
    /// trailing <paramref name="tag"/> stays last for the same reason — that is
    /// where "statusupdate" is recognised.
    ///
    /// The TIME is included because a date alone cannot order two runs made on
    /// the same day, and yyyyMMdd-HHmm sorts chronologically as plain text: a
    /// folder listing comes out in run order without sorting by date modified.
    ///
    /// <paramref name="testName"/> is capped. An unbounded test name, plus an
    /// already long model title, plus a path, is how Windows' 260-character
    /// limit gets met — and the failure lands on the write, after the run.
    ///
    /// <paramref name="existingNames"/> is injected rather than read from disk so
    /// the numbering is testable.
    /// </summary>
    public static string BuildCsvFilename(
        string? modelTitle, string tag, string productTag,
        ISet<string> existingNames, DateTime? now = null, string? testName = null)
    {
        string safe = Regex.Replace(
            string.IsNullOrEmpty(modelTitle) ? "Untitled" : modelTitle!, "[^A-Za-z0-9_-]+", "_");
        string date = (now ?? DateTime.Now).ToString("yyyyMMdd-HHmm");

        string part = TestNamePart(testName);
        string test = part.Length > 0 ? $"{part}_" : "";

        string baseName = $"{safe}_{productTag}_{date}_{test}{tag}";

        int n = 1;
        while (existingNames.Contains($"{baseName}{n}.csv"))
            n++;

        return $"{baseName}{n}.csv";
    }

    // --------------------------------------------------------------- stale

    /// <summary>
    /// Ledger entries flagged in a past run that are not clashing now — shown
    /// separately so a "Resolved" flag stays visible as confirmation instead of
    /// a fixed item silently vanishing with no trace it was ever tracked.
    ///
    /// <paramref name="scopeIds"/> restricts this to elements THIS search
    /// actually looked at. Without it the ledger is model-wide (it merges every
    /// CSV this tool wrote), so an element flagged in a completely different
    /// search surfaces here purely for being in the shared pool — confirmed live
    /// on 2026-07-24, refrigerant-tray statuses appearing in a fire-protection
    /// report. A blank element id is never in scope.
    /// </summary>
    /// <param name="currentClashKeys">Pair keys in the current results.</param>
    /// <param name="currentElementKeys">
    /// Element keys in the current results, for legacy element-flagged entries.
    /// </param>
    public static List<(string Label, string Status)> FindStale(
        ClashLedger ledger,
        IEnumerable<string> currentClashKeys,
        IEnumerable<StatusKey> currentElementKeys,
        ISet<string>? scopeIds = null)
    {
        HashSet<string> currentPairs = [.. currentClashKeys];
        HashSet<StatusKey> currentElements = [.. currentElementKeys];
        List<(string, string)> stale = [];

        foreach (KeyValuePair<string, string> kv in ledger.ByPair)
        {
            if (currentPairs.Contains(kv.Key))
                continue;

            // ACCEPTED pairs are never stale news. "Reviewed and allowed, and it
            // is not clashing right now" is the least interesting sentence in
            // the report, and listing them would refill the table this bucket
            // exists to empty.
            if (Classify(kv.Value) == StatusBucket.Accepted)
                continue;

            // In scope when EITHER side was looked at - a pair is this search's
            // business if it touched either element.
            if (scopeIds is not null
                && !ClashPairKey.IdsOf(kv.Key).Any(scopeIds.Contains))
            {
                continue;
            }

            stale.Add((ClashPairKey.Describe(kv.Key), kv.Value));
        }

        foreach (KeyValuePair<StatusKey, string> kv in ledger.ByElement)
        {
            if (currentElements.Contains(kv.Key))
                continue;
            if (Classify(kv.Value) == StatusBucket.Accepted)
                continue;
            if (scopeIds is not null && !scopeIds.Contains(kv.Key.ElementId))
                continue;

            string label = kv.Key.LinkName.Length > 0
                ? $"{kv.Key.ElementId} (in link: {kv.Key.LinkName})"
                : kv.Key.ElementId;

            stale.Add((label, kv.Value));
        }

        stale.Sort((a, b) =>
        {
            int byStatus = string.Compare(a.Item2 ?? "", b.Item2 ?? "", StringComparison.Ordinal);
            return byStatus != 0 ? byStatus : string.Compare(a.Item1, b.Item1, StringComparison.Ordinal);
        });

        return stale;
    }

    // ------------------------------------------------------------ keywords

    public static string NormalizeText(string? value) => (value ?? "").Trim().ToLowerInvariant();

    /// <summary>
    /// Comma-separated keywords, normalized and deduplicated, order preserved.
    /// </summary>
    public static List<string> ParseKeywords(string? raw)
    {
        List<string> cleaned = [];
        HashSet<string> seen = [];

        foreach (string part in (raw ?? "").Split(','))
        {
            string nk = NormalizeText(part);
            if (nk.Length > 0 && seen.Add(nk))
                cleaned.Add(nk);
        }

        return cleaned;
    }

    /// <summary>Case-insensitive substring match against any keyword.</summary>
    public static bool ContainsKeyword(string? textValue, IReadOnlyList<string> keywords)
    {
        string text = NormalizeText(textValue);
        if (text.Length == 0 || keywords.Count == 0)
            return false;

        foreach (string kw in keywords)
        {
            if (text.Contains(kw))
                return true;
        }
        return false;
    }
}
