using System.IO;
using System.Text.Json;

namespace Ribbit.Native.Settings;

/// <summary>
/// Which clash tool a status editor belongs to.
///
/// The three Set Clash Status buttons are the same tool three times over,
/// differing ONLY in these values — which is what keeps each one reading and
/// writing its own product's CSVs instead of merging another's ledger.
/// <see cref="ConfigSection"/> must match the search button it pairs with, or
/// the two stop sharing a CSV folder.
/// </summary>
internal sealed record ClashProduct(string ConfigSection, string ProductTag, string DisplayName)
{
    public static readonly ClashProduct MepVsStructure =
        new("ClashMEPvsStruc_shared", "ClashMEPvsStruc", "Clash / MEP vs / Struc");

    public static readonly ClashProduct DetectLink =
        new("ClashDetectLink_shared", "ClashDetectLink", "Clash Detect Link");

    /// <summary>
    /// Clash Detect. The product tag is "ClashDetect", NOT "CDMEP" — the config
    /// SECTION is the one named after the script. Both Python scripts in this
    /// pair build their CSV prefix as "{Title}_ClashDetect_", so a tag of
    /// "CDMEP" here would silently orphan every report the Python tool has
    /// already written for this model: the ledger merge matches on that prefix.
    /// </summary>
    public static readonly ClashProduct Cdmep =
        new("CDMEP_shared", "ClashDetect", "Clash Detect");

    /// <summary>
    /// Federated Clash — the fourth product, added rather than folding the three
    /// together.
    ///
    /// ITS OWN TAG IS THE WHOLE POINT. Status lives in
    /// "{Model}_{ProductTag}_ledger.csv" and the ledger merge matches on that
    /// prefix, so a fourth tag means a fourth ledger: nothing the three tools
    /// have already triaged is orphaned, and no migration is needed. Collapsing
    /// them into one tag would have thrown away every existing status, including
    /// the CSVs the Python tools wrote.
    /// </summary>
    public static readonly ClashProduct Federated =
        new("ClashFederated_shared", "ClashFederated", "Federated Clash");
}

/// <summary>
/// The CSV folder each clash product reads and writes, persisted per product so
/// the search button and its status editor share one location.
/// </summary>
public sealed class ClashSettings
{
    public string CsvFolder { get; set; } = "";

    /// <summary>
    /// The last search typed into each keyword box, and the values picked from
    /// each Browse list, so a repeated search does not need retyping.
    ///
    /// Picks are stored as [parameter, value] pairs and are deliberately NOT
    /// re-validated against the model on restore — that would force a full scan
    /// on every window open. A stale pick simply matches nothing, and the run
    /// reports it as a value that matched no elements.
    ///
    /// Selected LINKS are never persisted: a RevitLinkInstance belongs to one
    /// document and cannot be meaningfully serialized across runs.
    /// </summary>
    public string SetAKeyword { get; set; } = "";

    public string SetBKeyword { get; set; } = "";

    public List<string[]> SetAValues { get; set; } = [];

    public List<string[]> SetBValues { get; set; } = [];

    /// <summary>
    /// The clearance pad, stored as value and unit SEPARATELY rather than as the
    /// single "100 mm" string the engine parses.
    ///
    /// Splitting a composed string back apart would have to guess where the
    /// number ends, and the guess is wrong for anything a user can legitimately
    /// type. Storing what the two controls actually held needs no guess.
    ///
    /// Blank means "never saved" — the window then applies its own default,
    /// which differs per tool (100 for the two Clash Detect tools, 0 for MEP vs
    /// Structure). Do NOT default these to "0"/"mm" here: that would silently
    /// change Clash Detect Link's 100 mm default on first run.
    /// </summary>
    public string PadValue { get; set; } = "";

    public string PadUnit { get; set; } = "";

    /// <summary>
    /// Minimum penetration depth before an overlap counts, for the tools that
    /// support it. Blank means no tolerance — every overlap counts, which is
    /// what the three original clash tools do and must keep doing.
    /// </summary>
    public string ToleranceValue { get; set; } = "";

    public string ToleranceUnit { get; set; } = "";

    /// <summary>
    /// Whether Description only counts on cable tray and conduit.
    ///
    /// Defaults TRUE, which is a deliberate behaviour change rather than a
    /// preserved default: pipes and ducts carry a real System Type, so reading
    /// their Description too fills the Browse list with text that was never
    /// meant as a system label and lets a keyword pull a pipe into a
    /// cable-tray-only set. Untick it for the old everything-everywhere
    /// behaviour, which is what earlier reports and the Python tool used.
    /// </summary>
    public bool DescriptionCableTrayOnly { get; set; } = true;

    /// <summary>
    /// Which category sets were ticked, by NAME.
    ///
    /// Names rather than categories, so a set edited in the library takes effect
    /// on the next run instead of freezing whatever it contained when ticked —
    /// that indirection is the whole point of naming a set. A name that no
    /// longer exists is simply not ticked.
    ///
    /// Stored per product, while the sets themselves are shared: two tools
    /// legitimately want different sets ticked from the same library.
    /// </summary>
    public List<string> ForcedSetNames { get; set; } = [];

    /// <summary>
    /// The named sets chosen for each side, by NAME.
    ///
    /// Names rather than contents, for the same reason as above: a set edited in
    /// the library takes effect on the next run instead of freezing what it held
    /// when it was chosen. A name that no longer resolves is reported as missing
    /// rather than silently contributing nothing.
    /// </summary>
    /// <summary>
    /// LEGACY — the sets chosen, by name. Read once and migrated to
    /// <see cref="SetAIds"/> / <see cref="SetBIds"/>, then no longer written.
    /// Kept on the type so an existing file still deserializes.
    /// </summary>
    public List<string> SetANames { get; set; } = [];

    public List<string> SetBNames { get; set; } = [];

    /// <summary>
    /// The sets chosen for each side, by stable ID.
    ///
    /// Ids rather than names because renaming a set used to orphan every saved
    /// selection silently — the reference simply stopped resolving. An id
    /// survives a rename, and survives the file being edited by hand.
    /// </summary>
    public List<string> SetAIds { get; set; } = [];

    public List<string> SetBIds { get; set; } = [];

    /// <summary>The stored pairs, dropping any malformed entry.</summary>
    public static List<(string Param, string Value)> PairsOf(List<string[]>? stored)
        => stored is null
            ? []
            : [.. stored.Where(p => p is { Length: 2 }).Select(p => (p[0], p[1]))];

    public static List<string[]> ToStored(IEnumerable<(string Param, string Value)> pairs)
        => [.. pairs.Select(p => new[] { p.Param, p.Value })];

    private static string PathFor(string configSection)
    {
        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Ribbit");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, $"clash_{configSection}.json");
    }

    public static ClashSettings Load(string configSection)
    {
        try
        {
            string path = PathFor(configSection);
            if (File.Exists(path))
            {
                ClashSettings? s = JsonSerializer.Deserialize<ClashSettings>(File.ReadAllText(path));
                if (s is not null)
                {
                    // Only the FOLDER falls back to Documents when blank. The
                    // saved keywords and picks are returned either way - keying
                    // the whole restore on the folder being set would silently
                    // throw away a remembered search.
                    if (s.CsvFolder.Length == 0)
                        s.CsvFolder = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

                    // The force-include ticks were always a Set B concept, so
                    // they become Set B's chosen sets. Done once, and only when
                    // Set B has nothing of its own, so a later edit is never
                    // overwritten by the old field.
                    if (s.SetBNames.Count == 0 && s.ForcedSetNames.Count > 0)
                        s.SetBNames = [.. s.ForcedSetNames];

                    return s;
                }
            }
        }
        catch (Exception)
        {
            // Fall through to the default.
        }

        return new ClashSettings
        {
            CsvFolder = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
    }

    public void Save(string configSection)
    {
        try
        {
            File.WriteAllText(PathFor(configSection),
                JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception)
        {
            // A preference that will not persist must not take the tool down.
        }
    }
}
