using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ribbit.Native.Settings;

/// <summary>
/// One set as it travels between libraries: the RULES, and nothing else.
///
/// NO ID, DELIBERATELY. An id is this library's private bookkeeping — it exists
/// so a rename cannot orphan a saved test, and it only means anything inside the
/// library that minted it. Carrying it across would put one machine's ids into
/// another's library, where they reference nothing, and would make two people's
/// libraries fight over which set a shared id names. The importer mints its own.
///
/// Nothing else travels either: no last-used dates, no counts. A set is a rule,
/// and a rule is what is worth sending.
/// </summary>
public sealed class ClashSetTransfer
{
    public string Name { get; set; } = "";

    public List<string> Categories { get; set; } = [];

    public List<SetValue> Values { get; set; } = [];

    public List<string> Keywords { get; set; } = [];

    /// <summary>
    /// Worksets the set leaves out, by name - see <see cref="ClashSet.ExcludedWorksets"/>.
    ///
    /// NULL MEANS "THE FILE COULD NOT SAY", not "none". A file from a build that
    /// predates the list - or a JSON bundle without the property - cannot carry
    /// it, and reading that as "leaves nothing out" made Import > Update wipe
    /// the list off a set that had one. Null leaves the local list alone; an
    /// EMPTY list (from a file that can carry it) really does clear it.
    /// </summary>
    public List<string>? ExcludedWorksets { get; set; }

    /// <summary>The rules as a set, for <see cref="ClashSet.CopyRulesFrom"/>. No id: the importer mints one.</summary>
    public ClashSet ToSet() => new()
    {
        Name = Name,
        Categories = Categories ?? [],
        Values = Values ?? [],
        Keywords = Keywords ?? [],
        ExcludedWorksets = ExcludedWorksets ?? [],
    };
}

/// <summary>
/// The exported file. A wrapper rather than a bare array so a future version can
/// add a field without every older build failing to read it.
/// </summary>
public sealed class ClashSetBundle
{
    /// <summary>Bumped only for a change an older build could NOT read.</summary>
    public int Version { get; set; } = 1;

    /// <summary>Free text, so a file found later says where it came from.</summary>
    public string? Note { get; set; }

    public List<ClashSetTransfer> Sets { get; set; } = [];
}

/// <summary>
/// Sends sets between libraries as a plain, hand-readable JSON file.
///
/// WHY THIS IS NOT JUST COPYING clash_sets.json: that file is the live library,
/// carrying ids and whatever else the library needs to keep. Copying it over
/// somebody's own file would replace their sets wholesale and break every test
/// they had pointing at them. This moves the rules and leaves identity alone.
/// </summary>
public static class ClashSetTransferIo
{
    public const string FileFilter =
        "Clash set list (*.txt)|*.txt|Clash set bundle (*.json)|*.json|All files (*.*)|*.*";

    public const string DefaultFileName = "clash-sets.txt";

    /// <summary>The line shape for a left-out workset.</summary>
    public const string ExcludeWorksetPrefix = "exclude workset:";

    /// <summary>
    /// Written before the first set by a build that can carry left-out worksets.
    /// Without it a set with no "exclude workset" lines says nothing about them
    /// (null), and Import > Update leaves the local list alone.
    /// </summary>
    public const string FormatLine = "format: 2";

    /// <summary>The prefix the format line is recognised by.</summary>
    public const string FormatPrefix = "format:";

    /// <summary>The first format that carries left-out worksets; any later one does too.</summary>
    public const int WorksetsFormat = 2;

    /// <summary>
    /// The written format, and the reason it is not JSON.
    ///
    /// The file exists to be HANDED TO SOMEONE, and the first thing they will do
    /// is open it to see what they are about to import. JSON is readable in the
    /// sense that a programmer can read it; braces, quotes and commas around
    /// every value are noise to everyone else. A heading per set with its rules
    /// listed underneath says the same thing and needs no explaining.
    ///
    /// Deliberately line-based and forgiving, because a hand-edited file is an
    /// expected input, not an accident: blank lines and # comments are skipped,
    /// leading space is free, and a line that means nothing is IGNORED rather
    /// than failing the import — losing one malformed line beats rejecting a
    /// file somebody spent ten minutes writing.
    ///
    /// The two separators carry the meaning and must stay distinct:
    ///   <c>Param = Value</c>  an exact value, matched against that parameter
    ///   <c>keyword: text</c>  a substring keyword
    ///   <c>category: NAME</c> a whole BuiltInCategory, by enum name
    ///   <c>exclude workset: NAME</c> a workset the set leaves out (Federated Clash)
    /// Splitting a value line on the FIRST '=' only, so a value may contain one.
    /// The prefixed shapes are tested BEFORE that split, so a workset called
    /// "Zone=A" is not misread as a value of a parameter called "exclude workset: Zone".
    /// </summary>
    private const string Header =
        """
        Ribbit clash sets
        =================

        Each [name] below is one set. An element joins a set if ANY of its lines
        match, so a set is a list of alternatives, not a list of requirements.

          Param = Value    match that parameter exactly
          keyword: text    match anywhere in System Type or Description
          category: NAME   take the whole category

          exclude workset: NAME   leave out anything on that workset
                                  (Federated Clash only)

        Edit this by hand if you like. Blank lines and lines starting with # are
        ignored, and so is anything that does not fit the shapes above.
        Import it from Manage sets in any Ribbit clash tool.
        """;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,

        // The file is meant to be opened and read, so an absent Note should not
        // sit there as a null asking to be explained.
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Writes the chosen sets, stripped to their rules.</summary>
    public static void Export(string path, IEnumerable<ClashSet> sets, string? note = null)
    {
        System.Text.StringBuilder sb = new();

        foreach (string line in Header.Split('\n'))
            sb.Append("# ").AppendLine(line.TrimEnd('\r'));

        if (!string.IsNullOrWhiteSpace(note))
            sb.Append("# ").AppendLine(note!.Trim());

        // Says this file CAN carry left-out worksets, so a set listing none
        // really leaves none out. Before any heading, where an older build's
        // parser ignores it.
        sb.AppendLine();
        sb.AppendLine("# Written by a build that knows \"exclude workset\" lines: a set with none leaves nothing out.");
        sb.AppendLine(FormatLine);

        foreach (ClashSet s in sets)
        {
            sb.AppendLine();
            sb.Append('[').Append(s.Name).AppendLine("]");

            foreach (SetValue v in s.Values)
            {
                if (!string.IsNullOrWhiteSpace(v.Value))
                    sb.Append("  ").Append(v.Param).Append(" = ").AppendLine(v.Value);
            }

            foreach (string k in s.Keywords)
            {
                if (!string.IsNullOrWhiteSpace(k))
                    sb.Append("  keyword: ").AppendLine(k);
            }

            foreach (string c in s.Categories)
            {
                if (!string.IsNullOrWhiteSpace(c))
                    sb.Append("  category: ").AppendLine(c);
            }

            foreach (string w in Ribbit.Geometry.Clash.WorksetAudit.Normalize(s.ExcludedWorksets))
                sb.Append("  ").Append(ExcludeWorksetPrefix).Append(' ').AppendLine(w);
        }

        // Same temp-then-swap as every other shared write here: the export may
        // well be aimed at the folder the team shares. See AtomicFile.
        string temp = Ribbit.Geometry.Clash.AtomicFile.TempPathFor(path);
        File.WriteAllText(temp, sb.ToString());
        Ribbit.Geometry.Clash.AtomicFile.Commit(temp, path);
    }

    /// <summary>
    /// Parses the written listing.
    ///
    /// Never throws on a bad LINE — only on a file with no sets in it at all,
    /// which is the one case where carrying on would silently import nothing.
    /// </summary>
    private static List<ClashSetTransfer> ParseText(string text)
    {
        List<ClashSetTransfer> outp = [];
        ClashSetTransfer? current = null;

        // Whether this file can carry left-out worksets (see FormatLine). Until
        // it says so, a set's list is null - "not said" - rather than empty.
        bool carriesWorksets = false;

        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();

            if (line.Length == 0 || line[0] == '#')
                continue;

            // "format: N" before any heading. Read as a NUMBER, so a later build's
            // "format: 3" (and a hand-typed "format:2") still counts as a file
            // that can carry left-out worksets - matching only the exact text
            // read a newer file as an old one and kept a list it meant to clear.
            if (current is null && line.StartsWith(FormatPrefix, StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(line[FormatPrefix.Length..].Trim(), out int version)
                    && version >= WorksetsFormat)
                    carriesWorksets = true;

                continue;
            }

            if (line[0] == '[')
            {
                // Last ']', so a name may contain one.
                int close = line.LastIndexOf(']');
                if (close <= 1)
                    continue;

                current = new ClashSetTransfer
                {
                    Name = line[1..close].Trim(),
                    ExcludedWorksets = carriesWorksets ? [] : null,
                };
                outp.Add(current);
                continue;
            }

            // A rule before any heading has no set to belong to.
            if (current is null)
                continue;

            if (line.StartsWith("keyword:", StringComparison.OrdinalIgnoreCase))
            {
                string k = line["keyword:".Length..].Trim();
                if (k.Length > 0)
                    current.Keywords.Add(k);
                continue;
            }

            if (line.StartsWith("category:", StringComparison.OrdinalIgnoreCase))
            {
                string c = line["category:".Length..].Trim();
                if (c.Length > 0)
                    current.Categories.Add(c);
                continue;
            }

            // Before the '=' split below - see the class remarks.
            if (line.StartsWith(ExcludeWorksetPrefix, StringComparison.OrdinalIgnoreCase))
            {
                string w = line[ExcludeWorksetPrefix.Length..].Trim();
                if (w.Length > 0)
                    (current.ExcludedWorksets ??= []).Add(w);
                continue;
            }

            // FIRST '=' only, so the value may contain one.
            int eq = line.IndexOf('=');
            if (eq <= 0)
                continue;

            string param = line[..eq].Trim();
            string value = line[(eq + 1)..].Trim();

            if (param.Length > 0 && value.Length > 0)
                current.Values.Add(new SetValue { Param = param, Value = value });
        }

        return outp;
    }

    /// <summary>
    /// Reads a set file, or throws with a message worth showing.
    ///
    /// Takes the written listing, and STILL takes JSON — the bundle this used to
    /// write, a bare array, or a whole clash_sets.json. Files already handed
    /// round keep working, and someone will inevitably send one of the others.
    /// Any id in any of them is IGNORED.
    ///
    /// The shape is decided by the first meaningful character rather than by the
    /// extension, because the extension is whatever it got saved as.
    /// </summary>
    public static List<ClashSetTransfer> Import(string path)
    {
        string text = File.ReadAllText(path);
        string lead = text.TrimStart();

        if (lead.Length > 0 && lead[0] != '{' && lead[0] != '[')
        {
            List<ClashSetTransfer> listed = Clean(ParseText(text));
            if (listed.Count > 0)
                return listed;
        }
        else if (lead.StartsWith("[", StringComparison.Ordinal))
        {
            // Ambiguous: a JSON array and a listing whose first line is a set
            // heading both start with '['. JSON is tried below; if it does not
            // parse, fall back to reading it as a listing.
            try
            {
                List<ClashSetTransfer>? asArray =
                    JsonSerializer.Deserialize<List<ClashSetTransfer>>(text);

                if (asArray is { Count: > 0 })
                    return Clean(asArray);
            }
            catch (JsonException)
            {
                List<ClashSetTransfer> listed = Clean(ParseText(text));
                if (listed.Count > 0)
                    return listed;
            }
        }

        ClashSetBundle? bundle = null;

        try
        {
            bundle = JsonSerializer.Deserialize<ClashSetBundle>(text);
        }
        catch (JsonException)
        {
            // Try the other shapes below before giving up.
        }

        // A live library file deserializes into the bundle shape too, since both
        // have "Sets" - but its entries carry no rules under these names, so an
        // empty result here means "try again as a library".
        if (bundle?.Sets is { Count: > 0 })
            return Clean(bundle.Sets);

        try
        {
            List<ClashSetTransfer>? bare = JsonSerializer.Deserialize<List<ClashSetTransfer>>(text);
            if (bare is { Count: > 0 })
                return Clean(bare);
        }
        catch (JsonException)
        {
        }

        throw new InvalidDataException(
            "That file does not contain any clash sets. Expected a file exported from "
            + "Manage sets - a list of [set name] headings with their rules underneath.");
    }

    /// <summary>
    /// Drops what cannot be used and trims what can.
    ///
    /// An unnamed set has nothing to merge against and no way to be shown, and a
    /// set with no rules at all would match nothing — both are dropped here so
    /// the import prompt counts only sets that would really arrive.
    /// </summary>
    private static List<ClashSetTransfer> Clean(IEnumerable<ClashSetTransfer> sets)
    {
        List<ClashSetTransfer> outp = [];

        foreach (ClashSetTransfer s in sets)
        {
            if (s is null)
                continue;

            string name = (s.Name ?? "").Trim();
            if (name.Length == 0)
                continue;

            List<SetValue> values =
            [
                .. (s.Values ?? [])
                    .Where(v => v is not null && (v.Value ?? "").Trim().Length > 0)
                    .Select(v => new SetValue
                    {
                        Param = (v.Param ?? "").Trim(),
                        Value = v.Value.Trim(),
                    }),
            ];

            List<string> keywords =
                [.. (s.Keywords ?? []).Where(k => !string.IsNullOrWhiteSpace(k)).Select(k => k.Trim())];

            List<string> categories =
                [.. (s.Categories ?? []).Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c.Trim())];

            // Left-out worksets alone are not a set: they only ever REMOVE from
            // what the rules select, so with no rules there is nothing to remove.
            if (values.Count == 0 && keywords.Count == 0 && categories.Count == 0)
                continue;

            outp.Add(new ClashSetTransfer
            {
                Name = name,
                Values = values,
                Keywords = keywords,
                Categories = categories,
                // Null stays null: "the file could not say" - see ExcludedWorksets.
                ExcludedWorksets = s.ExcludedWorksets is null
                    ? null
                    : Ribbit.Geometry.Clash.WorksetAudit.Normalize(s.ExcludedWorksets),
            });
        }

        return outp;
    }
}
