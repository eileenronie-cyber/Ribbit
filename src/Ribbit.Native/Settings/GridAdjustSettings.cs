using System.IO;
using System.Text.Json;

namespace Ribbit.Native.Settings;

/// <summary>
/// The Adjust Grids window as it was last run, so the next run opens set up
/// the same way instead of at the defaults every time. Per user, not per
/// model: scope-box presets are the per-model settings (GridScopeStore).
/// Offsets and the gap are kept as TEXT - what was typed - so a value that
/// was not a number comes back for correcting rather than as a silent 0.
/// </summary>
public sealed class GridAdjustSettings
{
    public bool CopyMode { get; set; }

    public bool Convert { get; set; } = true;
    public bool All2d { get; set; } = true;
    public bool Align { get; set; } = true;
    public bool ToScope { get; set; } = true;
    public bool Bubbles { get; set; } = true;
    public bool Spread { get; set; }

    public string Left { get; set; } = "3000";
    public string Right { get; set; } = "3000";
    public string Top { get; set; } = "3000";
    public string Bottom { get; set; } = "3000";
    public string Gap { get; set; } = "3000";

    public bool BubbleLeft { get; set; } = true;
    public bool BubbleTop { get; set; } = true;

    public bool MatchScope { get; set; }
    public bool HideOthers { get; set; }
    public bool ShowSet { get; set; }

    public int SearchIn { get; set; }
    public int ShowFilter { get; set; }

    /// <summary>Set by the in-Revit tests, so they never read or write the user's file.</summary>
    internal static string? PathOverride { get; set; }

    internal static string SettingsPath
    {
        get
        {
            if (!string.IsNullOrEmpty(PathOverride))
                return PathOverride!;
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Ribbit");
            return Path.Combine(dir, "gridadjust_settings.json");
        }
    }

    public static GridAdjustSettings Load()
    {
        try
        {
            string path = SettingsPath;
            return File.Exists(path)
                ? JsonSerializer.Deserialize<GridAdjustSettings>(File.ReadAllText(path)) ?? new GridAdjustSettings()
                : new GridAdjustSettings();
        }
        catch (Exception)
        {
            return new GridAdjustSettings();
        }
    }

    public void Save()
    {
        try
        {
            string path = SettingsPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception)
        {
            // Never take the tool down over a preference that would not persist.
        }
    }
}
