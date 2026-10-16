using System.IO;
using System.Text.Json;
using Ribbit.Geometry;

namespace Ribbit.Native.Settings;

/// <summary>
/// Shared, persisted Sprig Connect settings. Ported from lib/spriglib/settings.py.
///
/// UNITS: everything stored here is in MILLIMETRES (or degrees) — always mm,
/// never feet or inches. Typing "25" always means 25 mm; nothing here goes
/// through Revit's own unit parsing, so there is no path by which a bare number
/// could be read as feet. <see cref="AsFeet"/> converts to Revit internal
/// decimal feet in one place, so no other module has to think about it.
/// </summary>
public sealed class SprigSettings
{
    // ------------------------------------------------------------- editable
    // Only settings a real job might actually need to change live here. The
    // dialog once got to eight fields, most of which nobody would ever retype -
    // and burying the two that mattered among six that didn't is exactly how a
    // min-sprig of 1000 mm and a max-armover of 0 once rejected a perfectly
    // good layout without either being noticed.

    /// <summary>
    /// Plan offset at or below which a head counts as sitting ON the branch
    /// line and gets a straight sprig instead of an armover. THE setting: it
    /// decides which of the two layouts every head gets.
    /// </summary>
    public double DirectSprigTolMm { get; set; } = 300.0;

    /// <summary>
    /// Keep tees this far off a branch segment's own endpoints, so there is
    /// something left to break.
    /// </summary>
    public double MinMainMarginMm { get; set; } = 30.0;

    /// <summary>
    /// A sprig shorter than this means the head is basically sitting on the
    /// branch — a modelling mistake, not a layout. Sanity check, so keep it small.
    /// </summary>
    public double MinSprigLengthMm { get; set; } = 50.0;

    // ---------------------------------------------------------------- fixed
    // Thresholds for advisory warnings. These only decide when a note appears
    // in the report - never whether anything is built - so there is no reason
    // to ask per run. The dry-run report prints them either way, so they stay
    // visible rather than becoming invisible magic numbers.

    /// <summary>Two tees closer than this on one segment may not leave room for both fittings.</summary>
    public const double MinTeeSpacingMm = 150.0;

    /// <summary>Armovers longer than this get a note. 0 disables the check.</summary>
    public const double MaxArmoverLengthMm = 3600.0;

    /// <summary>Warn when a DIRECT sprig leans more than this off plumb.</summary>
    public const double LeanWarnDeg = 1.0;

    /// <summary>
    /// Past this lean, a head goes to an ARMOVER instead of a direct sprig,
    /// however small its plan offset. 0 disables the cap.
    ///
    /// Fifteen degrees is a limit on the OUTCOME, where the direct-sprig
    /// tolerance is a limit on the INPUT — and only the outcome is what anyone
    /// looks at. On the shipped 300 mm tolerance a head 300 mm off a 300 mm rise
    /// used to be built as a 45-degree sprig, while one 350 mm off got a clean
    /// armover: being nearer the branch produced worse geometry. This is what
    /// stops that.
    /// </summary>
    public const double MaxDirectLeanDeg = 15.0;

    /// <summary>
    /// Diameter fallback, used only when a head's own connector size can't be
    /// read. Normally the sprig and armover are sized from the head itself,
    /// because the pipe running up to a head has to match what the head takes —
    /// a fact of the model rather than a preference.
    /// </summary>
    public const double FallbackDiameterMm = 25.0;

    public static SprigSettings Defaults => new();

    /// <summary>Tests only: read and write the settings here instead of %APPDATA%.</summary>
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
            return Path.Combine(dir, "sprigconnect_settings.json");
        }
    }

    /// <summary>
    /// Defaults overridden by whatever was last saved. A missing or corrupt
    /// settings file just falls back to defaults — this never throws.
    /// </summary>
    public static SprigSettings Load()
    {
        try
        {
            string path = SettingsPath;
            if (!File.Exists(path))
                return Defaults;

            return JsonSerializer.Deserialize<SprigSettings>(File.ReadAllText(path))
                   ?? Defaults;
        }
        catch (Exception)
        {
            return Defaults;
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
            // Not being able to persist a preference must never take the tool
            // down mid-run; the values still apply to this run.
        }
    }

    /// <summary>
    /// The solver's parameters: mm converted to Revit internal decimal feet,
    /// degrees left alone. The single conversion point.
    /// </summary>
    public SprigParams AsFeet() => new(
        DirectTol: DirectSprigTolMm / Units.MmPerFt,
        MinSprig: MinSprigLengthMm / Units.MmPerFt,
        MainMargin: MinMainMarginMm / Units.MmPerFt,
        MaxArmover: MaxArmoverLengthMm / Units.MmPerFt,
        MinTeeSpacing: MinTeeSpacingMm / Units.MmPerFt,
        LeanWarnDeg: LeanWarnDeg,
        MaxDirectLeanDeg: MaxDirectLeanDeg);
}
