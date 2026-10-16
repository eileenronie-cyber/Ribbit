using System.IO;
using System.Text.Json;
using Ribbit.Geometry;

namespace Ribbit.Native.Settings;

/// <summary>
/// Shared, persisted Drop Connect settings. Ported from lib/droplib/settings.py.
///
/// UNITS: everything stored here is in MILLIMETRES (or degrees).
/// <see cref="AsFeet"/> is the single conversion point to Revit internal feet.
/// </summary>
public sealed class DropSettings
{
    // ------------------------------------------------------------- editable

    /// <summary>
    /// Plan offset at or below which a head is fed by a straight drop off the
    /// bottom of the branch instead of a return bend over the top. THE setting:
    /// it decides which layout every head gets.
    /// </summary>
    public double DirectDropTolMm { get; set; } = 25.0;

    /// <summary>
    /// How far the return bend rises above the branch centreline before turning
    /// into the armover. A real design input — it sets how high the armover runs
    /// over the branch — so it stays editable. The solver warns when it is too
    /// short to clear the pipe.
    /// </summary>
    public double RiserHeightMm { get; set; } = 300.0;

    /// <summary>Keep tees this far off a segment's own endpoints.</summary>
    public double MinMainMarginMm { get; set; } = 30.0;

    /// <summary>
    /// A drop shorter than this means the head is basically sitting on the
    /// branch — a modelling mistake, not a layout. Sanity check, keep it small.
    /// </summary>
    public double MinDropLengthMm { get; set; } = 50.0;

    /// <summary>
    /// Whether a head sitting directly under the branch gets a straight DIRECT
    /// drop. OFF by default: a bare pendent-off-the-branch detail isn't used
    /// here, so an in-line head is expected to be an END BEND at the run's end.
    ///
    /// The Python stores this as a number purely so its text-box-only dialog
    /// could carry it; the C# dialog has a real checkbox, so it is a bool here.
    /// </summary>
    public bool AllowDirectDrops { get; set; }

    // ---------------------------------------------------------------- fixed

    public const double MinTeeSpacingMm = 150.0;

    /// <summary>Armovers longer than this get a note. 0 disables the check.</summary>
    public const double MaxArmoverLengthMm = 3600.0;

    /// <summary>
    /// NFPA 13 wants a hanger on an unsupported armover past 24 in (610 mm) —
    /// 12 in (305 mm) above 100 psi. Advisory only: the tool doesn't know the
    /// system pressure. 0 turns it off.
    /// </summary>
    public const double ArmoverHangerWarnMm = 610.0;

    /// <summary>
    /// Extra air demanded between the drop/armover and the branch pipe in the
    /// pipe-through-pipe clearance checks.
    /// </summary>
    public const double ClearanceMm = 25.0;

    public const double LeanWarnDeg = 1.0;

    /// <summary>
    /// Past this lean, a head goes to a RETURN BEND instead of a direct drop,
    /// however small its plan offset. 0 disables the cap.
    ///
    /// Matches <c>SprigSettings.MaxDirectLeanDeg</c> deliberately — the two
    /// tools face the identical choice and answering it differently would be a
    /// difference nobody could explain. The direct-drop tolerance limits the
    /// INPUT (how far off the branch a head may be); this limits the RESULT
    /// (how far off plumb the pipe ends up), which is the part anyone sees.
    /// </summary>
    public const double MaxDirectLeanDeg = 15.0;

    /// <summary>
    /// Diameter fallback, used only when a head's own connector size can't be
    /// read. Normally the riser, armover and drop are all sized from the head
    /// itself, because the pipe running down to a head has to match what the
    /// head takes.
    /// </summary>
    public const double FallbackDiameterMm = 25.0;

    public static DropSettings Defaults => new();

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
            return Path.Combine(dir, "dropconnect_settings.json");
        }
    }

    public static DropSettings Load()
    {
        try
        {
            string path = SettingsPath;
            if (!File.Exists(path))
                return Defaults;

            return JsonSerializer.Deserialize<DropSettings>(File.ReadAllText(path)) ?? Defaults;
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
            // Not being able to persist a preference must never take the tool down.
        }
    }

    public DropParams AsFeet() => new(
        DirectTol: DirectDropTolMm / Units.MmPerFt,
        MinDrop: MinDropLengthMm / Units.MmPerFt,
        MainMargin: MinMainMarginMm / Units.MmPerFt,
        MaxArmover: MaxArmoverLengthMm / Units.MmPerFt,
        MinTeeSpacing: MinTeeSpacingMm / Units.MmPerFt,
        LeanWarnDeg: LeanWarnDeg,
        RiserHeight: RiserHeightMm / Units.MmPerFt,
        HangerWarn: ArmoverHangerWarnMm / Units.MmPerFt,
        Clearance: ClearanceMm / Units.MmPerFt,
        AllowDirect: AllowDirectDrops,
        MaxDirectLeanDeg: MaxDirectLeanDeg);
}
