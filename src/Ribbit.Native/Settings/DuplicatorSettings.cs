using System.IO;
using System.Text.Json;
using Ribbit.Geometry.Sheets;

namespace Ribbit.Native.Settings;

/// <summary>
/// Persisted Part Sheets Duplicator form state. Ported from script.py's
/// pyRevit per-script config.
/// </summary>
public sealed class DuplicatorSettings
{
    public string FilePath { get; set; } = "";
    public string SheetName { get; set; } = "";

    /// <summary>"skip" leaves existing views alone; "update" re-applies templates.</summary>
    public bool UpdateExisting { get; set; }

    /// <summary>
    /// level key -> chosen base view's DISPLAY NAME. Names rather than
    /// ElementIds because view ids are not stable across model sessions.
    /// </summary>
    public Dictionary<string, string> LevelMap { get; set; } = [];

    /// <summary>
    /// What the user chose for rows, kept for the next run: "worksheet|row name"
    /// (lower case) -> "independent", "skip" or "dependent|parent view name".
    /// By name, not row number, so inserting a row in Excel moves no choice.
    /// </summary>
    public Dictionary<string, string> RowChoices { get; set; } = [];

    public ExistingMode Mode => UpdateExisting ? ExistingMode.Update : ExistingMode.Skip;

    public static string FormatChoice(RowChoice c) => c.Kind switch
    {
        RowChoiceKind.Independent => "independent",
        RowChoiceKind.Skip => "skip",
        RowChoiceKind.DependentOf => "dependent|" + c.Parent,
        _ => "",
    };

    public static RowChoice? ParseChoice(string? text)
    {
        if (text is null)
            return null;
        if (text == "independent")
            return new RowChoice(RowChoiceKind.Independent);
        if (text == "skip")
            return new RowChoice(RowChoiceKind.Skip);
        if (text.StartsWith("dependent|", StringComparison.Ordinal) && text.Length > "dependent|".Length)
            return new RowChoice(RowChoiceKind.DependentOf, text.Substring("dependent|".Length));
        return null;
    }

    /// <summary>Tests only: read and write this file instead of the user's.</summary>
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
            return Path.Combine(dir, "partsheetsduplicator_settings.json");
        }
    }

    public static DuplicatorSettings Load()
    {
        try
        {
            string path = SettingsPath;
            if (!File.Exists(path))
                return new DuplicatorSettings();

            return JsonSerializer.Deserialize<DuplicatorSettings>(File.ReadAllText(path))
                   ?? new DuplicatorSettings();
        }
        catch (Exception)
        {
            return new DuplicatorSettings();
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
            // Never take the tool down over a preference that would not persist.
        }
    }
}
