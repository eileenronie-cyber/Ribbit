using System.IO;
using System.Text.Json;
using Ribbit.Geometry.Text;

namespace Ribbit.Native.Settings;

/// <summary>
/// One JSON file per tool under %APPDATA%\Ribbit, read leniently (a missing or
/// broken file is the defaults) and written without ever taking the tool down.
/// The four tools ported from the 2026-10 Dynamo graphs share this instead of
/// each carrying its own copy of the same forty lines.
/// </summary>
internal static class SettingsFile
{
    /// <summary>Tests only: a folder to read and write every one of these files in instead.</summary>
    internal static string? DirOverride { get; set; }

    internal static string PathOf(string file) => Path.Combine(Dir, file);

    /// <summary>The settings folder (%APPDATA%\Ribbit, or the test override), created if missing.</summary>
    internal static string Dir
    {
        get
        {
            string dir = DirOverride ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Ribbit");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static T Load<T>(string file) where T : new()
    {
        try
        {
            string path = PathOf(file);
            return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path)) ?? new T() : new T();
        }
        catch (Exception)
        {
            return new T();
        }
    }

    public static void Save<T>(string file, T value)
    {
        try
        {
            string path = PathOf(file);
            string temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
            File.Copy(temp, path, overwrite: true);
            File.Delete(temp);
        }
        catch (Exception)
        {
            // A preference that would not persist is never worth failing a run over.
        }
    }
}

/// <summary>Number Text Notes: the last settings used.</summary>
public sealed class NumberNotesSettings
{
    private const string File = "numbertextnotes_settings.json";

    public int SkipLines { get; set; } = 1;
    public int Start { get; set; } = 1;
    public string Mode { get; set; } = "list";
    public string Format { get; set; } = "{n}.";

    public NumberSettings ToSettings() => new(
        Math.Max(0, SkipLines), Math.Max(0, Start),
        Mode switch { "text" => NumberMode.Typed, "remove" => NumberMode.Remove, _ => NumberMode.List },
        NoteNumbering.FormatOk(Format) ? Format : "{n}.");

    public static NumberNotesSettings From(NumberSettings s) => new()
    {
        SkipLines = s.SkipLines,
        Start = s.Start,
        Mode = s.Mode switch { NumberMode.Typed => "text", NumberMode.Remove => "remove", _ => "list" },
        Format = s.Format,
    };

    public static NumberNotesSettings Load() => SettingsFile.Load<NumberNotesSettings>(File);
    public void Save() => SettingsFile.Save(File, this);
}

/// <summary>Place Views on Sheets: placement and the view list's filter.</summary>
public sealed class PlaceViewsSettings
{
    private const string File = "placeviews_settings.json";

    /// <summary>"auto" = centre of the largest title block; "xy" = the point below.</summary>
    public string Placement { get; set; } = "auto";
    public double XMm { get; set; } = 532.5;
    public double YMm { get; set; } = 405.0;
    public int ShowFilter { get; set; }

    public static PlaceViewsSettings Load() => SettingsFile.Load<PlaceViewsSettings>(File);
    public void Save() => SettingsFile.Save(File, this);
}

/// <summary>Copy View Filters: the last options and source file.</summary>
public sealed class FilterCopySettings
{
    private const string File = "copyfilters_settings.json";

    public string SourcePath { get; set; } = "";
    public bool Lines { get; set; } = true;
    public bool Patterns { get; set; } = true;
    public bool Transparency { get; set; } = true;
    public bool BringMissing { get; set; } = true;

    /// <summary>Never remembered on: it rewrites filters model-wide, so it is asked for each time.</summary>
    public static FilterCopySettings Load() => SettingsFile.Load<FilterCopySettings>(File);
    public void Save() => SettingsFile.Save(File, this);
}

/// <summary>Room Tag Checker: the options and the level pairings made by hand, per link file.</summary>
public sealed class RoomTagSettings
{
    private const string File = "roomtagchecker_settings.json";

    public bool TagMissing { get; set; } = true;
    public bool OnlyInCrop { get; set; } = true;
    public bool DeleteDuplicates { get; set; } = true;
    public bool ShowRooms { get; set; } = true;

    /// <summary>The Room Tag type last chosen, "Family : Type"; "" = the model's default.</summary>
    public string TagType { get; set; } = "";

    /// <summary>"link title|host level name" (lower case) -> link level name, for names that do not agree.</summary>
    public Dictionary<string, string> Pairings { get; set; } = [];

    public static RoomTagSettings Load() => SettingsFile.Load<RoomTagSettings>(File);
    public void Save() => SettingsFile.Save(File, this);
}
