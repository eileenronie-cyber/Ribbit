using System.IO;
using System.Text.Json;

namespace Ribbit.Native.Settings;

/// <summary>
/// Persisted SheetMaker form state. Ported from script.py's
/// <c>get_saved_settings</c> / <c>save_settings</c>, which used pyRevit's
/// per-script config.
/// </summary>
public sealed class SheetMakerSettings
{
    public string FilePath { get; set; } = "";
    public string DataSheet { get; set; } = "";
    public string MappingSheet { get; set; } = "";
    public bool UpdateExisting { get; set; } = true;
    public bool BlankAsSpacer { get; set; } = true;

    /// <summary>
    /// Data column naming each row's titleblock, or empty for the old positional
    /// behaviour. Lets one drawing list mix sheet sizes.
    /// </summary>
    public string TitleblockColumn { get; set; } = "";

    /// <summary>Titleblock type applied to any sheet not given one of its own.</summary>
    public string DefaultTitleblock { get; set; } = "";

    /// <summary>Sheet number -> titleblock type, for the sheets that differ.</summary>
    public Dictionary<string, string> TitleblockBySheet { get; set; } = [];

    /// <summary>
    /// Revit Parameter -> Excel Column, built in the window rather than on a
    /// MAPPING worksheet. Empty means "read the workbook's own MAPPING sheet",
    /// so existing workbooks keep working untouched.
    /// </summary>
    public Dictionary<string, string> Mapping { get; set; } = [];

    /// <summary>
    /// Defaults ON, but only on a genuine first run — see <see cref="Load"/>.
    /// A returning user who deliberately unticked it should not silently get a
    /// dry run again next time.
    /// </summary>
    public bool DryRun { get; set; } = true;

    // --- the sheet exchange (export to Excel, import back) -------------------

    /// <summary>Parameters last ticked for export, by name.</summary>
    public List<string> ExportParameters { get; set; } = [];

    public bool ExportTitleblock { get; set; } = true;

    /// <summary>
    /// The sheets ticked last time, by UniqueId - ticked again next time when the
    /// same model is open. Ids from another model simply match nothing.
    /// </summary>
    public List<string> ExportSheetIds { get; set; } = [];

    public bool OpenAfterExport { get; set; } = true;

    /// <summary>Where the last export went, so the next one starts there.</summary>
    public string ExportPath { get; set; } = "";

    /// <summary>Titleblock for new rows that name none, on import.</summary>
    public string ImportDefaultTitleblock { get; set; } = "";

    /// <summary>When a value changed in Revit AND Excel since export, write Excel's.</summary>
    public bool ImportExcelWins { get; set; }

    /// <summary>
    /// Tests only: read and write the settings here instead of the user's own
    /// file, so a test run never touches a person's remembered choices.
    /// </summary>
    internal static string? PathOverride { get; set; }

    private static string SettingsPath
    {
        get
        {
            if (PathOverride is not null)
                return PathOverride;

            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Ribbit");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "sheetmaker_settings.json");
        }
    }

    /// <summary>
    /// Saved state, or first-run defaults. The Python keyed "is this a first
    /// run?" off whether a file path had ever been written; here the presence of
    /// the settings file itself answers the same question more directly.
    /// </summary>
    public static SheetMakerSettings Load()
    {
        try
        {
            string path = SettingsPath;
            if (!File.Exists(path))
                return new SheetMakerSettings();

            return JsonSerializer.Deserialize<SheetMakerSettings>(File.ReadAllText(path))
                   ?? new SheetMakerSettings();
        }
        catch (Exception)
        {
            return new SheetMakerSettings();
        }
    }

    public void Save()
    {
        try
        {
            File.WriteAllText(SettingsPath,
                JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception)
        {
            // Not being able to persist form state must never take the tool down.
        }
    }

    /// <summary>
    /// Strip whitespace and surrounding quotes, e.g. from Explorer's "Copy as
    /// path" (which wraps the path in quotes) being pasted straight in.
    /// </summary>
    public static string CleanPath(string? text)
    {
        string s = (text ?? "").Trim();
        if (s.Length >= 2 && (s[0] == '"' || s[0] == '\'') && s[^1] == s[0])
            s = s[1..^1].Trim();
        return s;
    }
}
