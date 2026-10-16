using System.IO;
using System.Text.Json;
using Ribbit.Geometry;
using Ribbit.Native.Revit;

namespace Ribbit.Native.Settings;

/// <summary>
/// Shared, persisted Slope Branch / Slope Through settings. Ported from
/// lib/slopelib/settings.py. Shared by both buttons on purpose.
///
/// UNITS: everything stored here is in MILLIMETRES (or degrees, or counts).
/// <see cref="AsFeet"/> is the single conversion point to Revit internal feet.
///
/// SIGN: positive slope means the run RISES as it goes AWAY from the fixed end
/// you clicked. Negative means it falls. There is no separate direction field on
/// purpose — one signed number is one thing to get wrong instead of two, and the
/// dry run prints the resulting elevation at both ends before anything moves.
/// </summary>
public sealed class SlopeSettings
{
    /// <summary>
    /// The target slope, mm of rise per metre of PLAN run, signed: positive
    /// rises away from the fixed end. 2.0 == 1:500.
    /// </summary>
    public double SlopeMmPerM { get; set; } = 2.0;

    /// <summary>
    /// How far off the run's plan axis a picked segment's endpoint may sit and
    /// still count as part of the same straight run.
    /// </summary>
    public double PlanOffsetTolMm { get; set; } = 5.0;

    /// <summary>
    /// How far a picked segment's plan direction may deviate from the run's axis
    /// before it is dropped as "not part of this run".
    /// </summary>
    public double AngleTolDeg { get; set; } = 0.5;

    /// <summary>
    /// A rider group is moved as a rigid body. If its attachment points want to
    /// move by amounts differing more than this, no single translation is right
    /// and the group is skipped.
    /// </summary>
    public double GroupTiltTolMm { get; set; } = 1.0;

    /// <summary>
    /// A group containing a pipe whose PLAN length exceeds this is taken to lead
    /// off into the rest of the system and is never moved. Sprigs are vertical
    /// (plan length 0) and armovers are short, so this only ever catches real runs.
    /// </summary>
    public double ForeignRunMm { get; set; } = 1500.0;

    /// <summary>
    /// A group containing a long pipe is still MOVED when its whole connected
    /// closure has no more than this many elements. This is what lets an armover
    /// longer than <see cref="ForeignRunMm"/> ride with the branch: an armover
    /// assembly terminates at its head after a handful of elements, where
    /// anything reaching the rest of the system keeps going.
    /// </summary>
    public double SmallGroupElements { get; set; } = 15.0;

    /// <summary>
    /// Belt and braces on the same idea. Deliberately generous: the walk stops at
    /// the first long pipe, so a group reaching this cap has found no long pipe in
    /// hundreds of elements. A tight cap here used to TRUNCATE the walk instead,
    /// which is worse than useless — a truncated closure can let part of the
    /// cross main be moved.
    /// </summary>
    public double MaxGroupElements { get; set; } = 400.0;

    /// <summary>
    /// Warn if anything would move further than this vertically. A wrong anchor
    /// end or a sign slip shows up here first.
    /// </summary>
    public double MaxMoveMm { get; set; } = 500.0;

    /// <summary>
    /// How close two open connectors must end up before a reconnect is attempted.
    /// Needs headroom over exact coincidence because an in-line fitting rotated
    /// to the new pitch does not land its outlet in exactly the old place.
    /// </summary>
    public double ReconnectTolMm { get; set; } = 3.0;

    /// <summary>
    /// A corner elbow left further off than this on the CHILD leg's side is
    /// called out rather than left as one number among many.
    ///
    /// The residual has always been measured and tabled; nothing judged it. A
    /// live test found a branch where it was large enough that nudging the elbow
    /// afterwards snapped the parent leg's pipe back to its pre-slope angle — so
    /// a report showing twelve corners at 0.0001 deg and one at 3.5 gave the one
    /// that mattered no more weight than the rest. Every other risky quantity in
    /// these tools warns at a threshold; this is that threshold.
    /// </summary>
    public const double CornerResidualWarnDeg = 0.5;

    public static SlopeSettings Defaults => new();

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
            return Path.Combine(dir, "slopebranch_settings.json");
        }
    }

    public static SlopeSettings Load()
    {
        try
        {
            string path = SettingsPath;
            if (!File.Exists(path))
                return Defaults;

            return JsonSerializer.Deserialize<SlopeSettings>(File.ReadAllText(path)) ?? Defaults;
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

    /// <summary>
    /// The geometry parameters. Slope comes out as a plain RISE OVER RUN ratio
    /// (dimensionless), so 2 mm/m becomes 0.002 whatever the internal length unit.
    /// </summary>
    public SlopeParams AsFeet() => new(
        AngleTolDeg: AngleTolDeg,
        PlanOffsetTol: PlanOffsetTolMm / Units.MmPerFt,
        Slope: SlopeMmPerM / 1000.0);

    /// <summary>The grouping parameters.</summary>
    internal SlopeGroupParams GroupParams() => new(
        ForeignRun: ForeignRunMm / Units.MmPerFt,
        SmallGroupElements: (int)SmallGroupElements,
        MaxGroupElements: (int)MaxGroupElements,
        GroupTiltTol: GroupTiltTolMm / Units.MmPerFt,
        AngleTolDeg: AngleTolDeg,
        PlanOffsetTol: PlanOffsetTolMm / Units.MmPerFt);

    public double ReconnectTolFt => ReconnectTolMm / Units.MmPerFt;

    public double MaxMoveFt => MaxMoveMm / Units.MmPerFt;
}
