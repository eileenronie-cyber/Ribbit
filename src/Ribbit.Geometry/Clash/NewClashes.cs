namespace Ribbit.Geometry.Clash;

/// <summary>
/// Which clashes are NEW since a test last ran.
///
/// The subtlety worth testing is the empty baseline. A test that has never run
/// has no record of what it saw, and "everything is new" would be technically
/// true and completely useless — a first run would badge every row. Absent
/// evidence, this reports nothing as new.
///
/// Baselines are PER TEST on purpose. The latest report on disk may belong to a
/// different search entirely, and comparing against it would badge clashes as
/// new because last time you happened to run something else.
/// </summary>
public static class NewClashes
{
    /// <summary>
    /// Whether a baseline exists at all. Without one, nothing can honestly be
    /// called new.
    /// </summary>
    public static bool HasBaseline(IReadOnlyCollection<string>? previousKeys)
        => previousKeys is { Count: > 0 };

    /// <summary>
    /// The keys in <paramref name="currentKeys"/> that the previous run did not
    /// see. Empty when there is no baseline.
    /// </summary>
    public static HashSet<string> Compute(
        IReadOnlyCollection<string>? previousKeys, IEnumerable<string> currentKeys)
    {
        if (!HasBaseline(previousKeys))
            return [];

        HashSet<string> before = new(previousKeys!, StringComparer.Ordinal);
        HashSet<string> outp = new(StringComparer.Ordinal);

        foreach (string key in currentKeys)
        {
            if (key.Length > 0 && !before.Contains(key))
                outp.Add(key);
        }

        return outp;
    }
}
