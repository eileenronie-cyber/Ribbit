using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Ribbit.Geometry.Clash;

/// <summary>
/// One workset as the audit shows it: aggregated BY NAME across every model a
/// federated run searches, because a set's exclusion is by name too.
/// </summary>
/// <param name="Name">The workset name as the first model that has it spells it.</param>
/// <param name="Models">Every searched model that has a workset by this name.</param>
/// <param name="ElementsInSet">How many of the set's elements sit on it - what excluding it would cost.</param>
/// <param name="HiddenByDefault">Any model hides it by default. Typical and hidden content usually is.</param>
public sealed record WorksetUsage(
    string Name,
    IReadOnlyList<string> Models,
    int ElementsInSet,
    bool HiddenByDefault);

/// <summary>Why the audit flagged a workset, and whether it starts ticked.</summary>
public sealed record WorksetSuggestion(string Reason, bool PreTick);

/// <summary>
/// The rules behind a set's "Worksets left out" list, and the audit that
/// suggests them.
///
/// WHY WORKSETS. A coordination model parks content that clashes but is not a
/// clash on its own worksets - typical-floor copies, hidden duplicates, builder's
/// work openings, plant being replaced. Measured on testproject: 9 of its 16
/// worksets are hidden by default and hold elements, and MEP_ZZ_Typical alone is
/// 3,228 of them. Left in a set, every one of those reports.
///
/// BY NAME, NOT BY ID. A WorksetId is per document, and a federated run spans the
/// host and every link - the same "MEP_ZZ_Typical" has a different id in each.
/// A name is what a person reads in Revit and what carries between models.
/// Compared trimmed and case-insensitively, since workset names are typed.
///
/// THE AUDIT SUGGESTS, IT NEVER DECIDES. Under-reporting is the dangerous
/// direction for a clash tool - a clean run looks like good news - so only names
/// that clearly are not coordination content start ticked (typical, hidden,
/// builder's work openings). Plant being REPLACED, existing or temporary content
/// is flagged but left unticked: hiding it would hide the real thing when it
/// lands. The same rule LiveClash's provisional audit follows for placeholders.
/// </summary>
public static class WorksetAudit
{
    /// <summary>Tokens that mark a workset as not coordination content: start ticked.</summary>
    private static readonly (string[] Tokens, string Reason)[] PreTickRules =
    [
        (["typical", "typicals"], "typical content"),
        (["hidden", "hide"], "hidden content"),
        (["bwic"], "builder's work openings"),
        (["opening", "openings", "sleeve", "sleeves", "penetration", "penetrations"], "openings"),
    ];

    /// <summary>Tokens worth a second look, but left UNticked.</summary>
    private static readonly (string[] Tokens, string Reason)[] FlagRules =
    [
        (["replacement", "replace"], "replacement - may be real plant"),
        (["existing", "exist"], "existing"),
        (["demo", "demolish", "demolition", "demolished"], "demolition"),
        (["temp", "temporary"], "temporary"),
        (["placeholder", "placeholders", "tbc", "wip"], "placeholder"),
        (["future"], "future"),
        (["archive", "archived", "superseded", "old", "dnu"], "superseded"),
        (["reference", "ref"], "reference"),
        (["scope"], "scope boxes"),
    ];

    /// <summary>
    /// Trimmed, non-empty and distinct ignoring case, in first-seen order - the
    /// one shape every stored or typed list is brought to.
    /// </summary>
    public static List<string> Normalize(IEnumerable<string?>? names)
    {
        List<string> outp = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        foreach (string? raw in names ?? [])
        {
            string n = (raw ?? "").Trim();
            if (n.Length > 0 && seen.Add(n))
                outp.Add(n);
        }

        return outp;
    }

    /// <summary>True when <paramref name="worksetName"/> is on the list.</summary>
    public static bool IsExcluded(IEnumerable<string>? excluded, string? worksetName)
    {
        string n = (worksetName ?? "").Trim();
        if (n.Length == 0)
            return false;

        foreach (string e in excluded ?? [])
        {
            if (string.Equals((e ?? "").Trim(), n, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>
    /// "MEP_ZZ_MEPServices_Hidden" -> mep, zz, mep, services, hidden.
    ///
    /// Split on anything that is not a letter or digit, and on camelCase - so
    /// "PlantReplacement" and "ScopeBoxes" read as words. A run of capitals is
    /// one word up to the last capital before a lowercase ("MEPServices").
    /// Whole tokens are matched, so "Template" never reads as "temp".
    /// </summary>
    public static List<string> Tokenize(string? name)
    {
        List<string> tokens = [];
        StringBuilder cur = new();
        string s = name ?? "";

        void Flush()
        {
            if (cur.Length > 0)
                tokens.Add(cur.ToString().ToLowerInvariant());
            cur.Clear();
        }

        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];

            if (!char.IsLetterOrDigit(c))
            {
                Flush();
                continue;
            }

            if (cur.Length > 0)
            {
                char prev = s[i - 1];
                bool lowerToUpper = char.IsLower(prev) && char.IsUpper(c);
                bool acronymEnd = char.IsUpper(prev) && char.IsUpper(c)
                                  && i + 1 < s.Length && char.IsLower(s[i + 1]);
                bool letterDigit = char.IsLetter(prev) != char.IsLetter(c);

                if (lowerToUpper || acronymEnd || letterDigit)
                    Flush();
            }

            cur.Append(c);
        }

        Flush();
        return tokens;
    }

    /// <summary>
    /// Why this workset is worth a look, or null when nothing about it says so.
    ///
    /// A name rule wins over "hidden by default": "MEP_ZZ_Typical" is flagged as
    /// typical content (ticked), while "MEP_ZZ_PlantReplacement", hidden too, is
    /// flagged for a look but left unticked.
    /// </summary>
    public static WorksetSuggestion? Suggest(string? name, bool hiddenByDefault)
    {
        HashSet<string> tokens = [.. Tokenize(name)];

        foreach ((string[] words, string reason) in PreTickRules)
        {
            if (words.Any(tokens.Contains))
                return new WorksetSuggestion(reason, true);
        }

        foreach ((string[] words, string reason) in FlagRules)
        {
            if (words.Any(tokens.Contains))
                return new WorksetSuggestion(hiddenByDefault ? reason + ", hidden by default" : reason, false);
        }

        return hiddenByDefault ? new WorksetSuggestion("hidden by default", false) : null;
    }

    /// <summary>
    /// What the audit opens with ticked.
    ///
    /// A set that already leaves worksets out opens with EXACTLY those - someone
    /// already decided, and re-suggesting would undo an untick they made on
    /// purpose. A set with none opens with the pre-tick suggestions, and only
    /// those that would actually remove something from this set.
    /// </summary>
    public static List<string> InitialTicks(
        IReadOnlyList<WorksetUsage> rows, IReadOnlyList<string>? current)
    {
        List<string> existing = Normalize(current);
        if (existing.Count > 0)
            return existing;

        return Normalize(
            rows.Where(r => r.ElementsInSet > 0 && Suggest(r.Name, r.HiddenByDefault)?.PreTick == true)
                .Select(r => r.Name));
    }

    /// <summary>
    /// The excluded names no searched model has a workset for - renamed, or only
    /// in a model that is not ticked. Reported, never silently ignored.
    /// </summary>
    public static List<string> Stale(IEnumerable<string>? excluded, IEnumerable<string>? worksetsThatExist)
    {
        HashSet<string> exist = new(Normalize(worksetsThatExist), StringComparer.OrdinalIgnoreCase);
        return [.. Normalize(excluded).Where(e => !exist.Contains(e))];
    }
}
