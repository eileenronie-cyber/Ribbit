namespace Ribbit.Geometry.Clash;

/// <summary>
/// One model, one name — whatever Revit calls the document today.
///
/// <c>Document.Title</c> changes with HOW the file was opened. Revit appends
/// "_<username>" to a local and "_detached" to a detached copy, and detaching a
/// local gets you both. One model, opened three ways in an afternoon, produced
/// four titles in the field:
///
///   PRJ01-..._09-02-2026
///   PRJ01-..._09-02-2026_jdoe1234ABCD
///   PRJ01-..._09-02-2026_detached
///   PRJ01-..._09-02-2026_jdoe1234ABCD_detached
///
/// The title is the prefix on every clash file, so each of those got its own
/// status ledger, its own New-badge baseline and its own notes. A clash marked
/// Resolved as a local read as unflagged when the same model was opened
/// detached — the tool quietly forgetting a morning's triage because of how a
/// file was opened.
///
/// Stripping the suffixes collapses them to one name, and the same triage
/// follows the model however it is opened.
/// </summary>
public static class ModelTitle
{
    /// <summary>The suffix Revit adds when a copy is detached from central.</summary>
    public const string DetachedSuffix = "detached";

    /// <summary>The title with Revit's open-method suffixes removed, for one user.</summary>
    public static string Normalize(string? title, string? username)
        => Normalize(title, [username]);

    /// <summary>
    /// The title with Revit's open-method suffixes removed.
    ///
    /// Repeated until nothing more comes off, because the suffixes STACK and in
    /// either order — "_user_detached" is what a detached local looks like.
    ///
    /// The detached suffix may carry a NUMBER: detaching again names the copy
    /// "_detached2", and the field has "_detached26". Each of those used to
    /// start a ledger of its own.
    ///
    /// SEVERAL USERNAMES, not one: a copy detached from a colleague's local
    /// carries THEIR name ("..._jdoe1234ABCD_detached26"), which the
    /// person opening it now cannot strip with their own. Callers pass every
    /// username this PC has seen (KnownUsers).
    ///
    /// Matching is case-insensitive and requires the underscore, so a model
    /// genuinely called "PlantDetached" keeps its name. The last suffix is never
    /// stripped if it would leave nothing behind: a file named after the user
    /// alone is odd, but an empty prefix would collide with every other model.
    /// </summary>
    public static string Normalize(string? title, IEnumerable<string?>? usernames)
    {
        string result = (title ?? "").Trim();
        if (result.Length == 0)
            return result;

        List<string> users = (usernames ?? [])
            .Where(u => !string.IsNullOrWhiteSpace(u))
            .Select(u => u!.Trim())
            .ToList();

        bool stripped = true;
        while (stripped)
        {
            stripped = TryStripDetached(ref result);

            foreach (string user in users)
                stripped |= TryStrip(ref result, user);
        }

        return result;
    }

    /// <summary>True when this title carries a suffix that normalising removes.</summary>
    public static bool IsVariantOf(string? title, string? normalized, string? username)
        => IsVariantOf(title, normalized, [username]);

    /// <inheritdoc cref="IsVariantOf(string?, string?, string?)"/>
    public static bool IsVariantOf(
        string? title, string? normalized, IEnumerable<string?>? usernames)
        => title is not null
           && normalized is not null
           && !string.Equals(title, normalized, StringComparison.Ordinal)
           && string.Equals(Normalize(title, usernames), normalized, StringComparison.Ordinal);

    /// <summary>"_detached", optionally followed by a number.</summary>
    private static bool TryStripDetached(ref string title)
    {
        int end = title.Length;
        while (end > 0 && char.IsDigit(title[end - 1]))
            end--;

        string tail = "_" + DetachedSuffix;
        int start = end - tail.Length;

        // "<=" not "<": a title that is nothing BUT the suffix keeps its name.
        if (start <= 0)
            return false;

        if (string.Compare(title, start, tail, 0, tail.Length, StringComparison.OrdinalIgnoreCase) != 0)
            return false;

        title = title.Substring(0, start);
        return true;
    }

    private static bool TryStrip(ref string title, string suffix)
    {
        string tail = "_" + suffix;

        // "<=" not "<": a title that is nothing BUT the suffix keeps its name.
        if (title.Length <= tail.Length)
            return false;

        if (!title.EndsWith(tail, StringComparison.OrdinalIgnoreCase))
            return false;

        title = title.Substring(0, title.Length - tail.Length);
        return true;
    }
}
