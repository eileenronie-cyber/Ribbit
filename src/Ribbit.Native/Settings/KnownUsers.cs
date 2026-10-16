using System.IO;
using Ribbit.Geometry.Clash;
using Ribbit.Native.Revit;

namespace Ribbit.Native.Settings;

/// <summary>
/// Every Revit username this PC has seen - so a model title carrying a
/// COLLEAGUE'S name normalises like one carrying your own.
///
/// Revit appends the username of whoever made the local, and detaching keeps
/// it. A copy detached from someone else's local is titled
/// "..._jdoe1234ABCD_detached26" even when opened by asmith5678WXYZ,
/// and stripping only the current user's name left that copy on its own
/// ledger, apart from the months of triage under the plain name.
///
/// Two sources, because the journals only ever record the current user:
///   - every username Revit has reported here, remembered in
///     %APPDATA%\Ribbit\known_users.txt;
///   - names recovered from the clash folder's retired ".migrated" files. The
///     title migration only ever retired a name that was the plain title plus
///     usernames and "_detached", so what sits between the two IS a username.
///
/// Best effort throughout: a store that cannot be read or written costs only
/// the extra names, never the current user.
/// </summary>
internal static class KnownUsers
{
    /// <summary>Set by tests to a scratch file, so they never touch the real store.</summary>
    internal static string? PathOverride { get; set; }

    public static string StorePath
        => !string.IsNullOrEmpty(PathOverride)
            ? PathOverride!
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Ribbit", "known_users.txt");

    /// <summary>
    /// The current user first, then everyone remembered, then anyone recovered
    /// from <paramref name="csvFolder"/>. New names are remembered for next time.
    /// </summary>
    public static List<string> For(string? currentUser, string? csvFolder = null, string? productTag = null)
    {
        List<string> stored = Stored();

        List<string> users = [];
        Add(users, currentUser);

        foreach (string u in stored)
            Add(users, u);

        foreach (string u in FromRetiredFiles(csvFolder, productTag))
            Add(users, u);

        if (users.Any(u => !stored.Contains(u, StringComparer.OrdinalIgnoreCase)))
            Remember(users);

        return users;
    }

    private static void Add(List<string> users, string? user)
    {
        if (string.IsNullOrWhiteSpace(user))
            return;

        string u = user!.Trim();
        if (!users.Contains(u, StringComparer.OrdinalIgnoreCase))
            users.Add(u);
    }

    /// <summary>The last read of the store, kept until the file changes.</summary>
    private static (string Path, DateTime Stamp, List<string> Users)? _cache;

    /// <summary>
    /// The remembered names. Asked for on every host comparison a window makes,
    /// so it costs one timestamp check unless the file has changed - written by
    /// this session or another Revit on the same PC.
    /// </summary>
    private static List<string> Stored()
    {
        try
        {
            string path = StorePath;
            if (!File.Exists(path))
                return [];

            DateTime stamp = File.GetLastWriteTimeUtc(path);
            if (_cache is { } c && c.Path == path && c.Stamp == stamp)
                return [.. c.Users];

            List<string> users = [.. File.ReadAllLines(path).Select(l => l.Trim()).Where(l => l.Length > 0)];
            _cache = (path, stamp, users);
            return [.. users];
        }
        catch (Exception)
        {
            return [];
        }
    }

    private static void Remember(List<string> users)
    {
        try
        {
            string path = StorePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            List<string> all = Stored();
            foreach (string u in users)
                Add(all, u);

            string temp = AtomicFile.TempPathFor(path);
            File.WriteAllLines(temp, all);
            AtomicFile.Commit(temp, path);

            // Two writes inside one clock tick share a timestamp, so the cache
            // is set here rather than trusted to notice its own write.
            _cache = (path, File.GetLastWriteTimeUtc(path), all);
        }
        catch (Exception)
        {
            // Remembering is a convenience; this run already has the names.
        }
    }

    /// <summary>
    /// Usernames recovered from retired files: a retired title, less its
    /// detached suffixes, that is another title in the folder plus ONE more
    /// "_segment" - that segment was a username when the migration retired it.
    /// </summary>
    internal static List<string> FromRetiredFiles(string? csvFolder, string? productTag)
    {
        List<string> found = [];
        if (string.IsNullOrEmpty(csvFolder) || string.IsNullOrEmpty(productTag) || !Directory.Exists(csvFolder))
            return found;

        List<string> names;
        try
        {
            names = [.. Directory.EnumerateFiles(csvFolder).Select(Path.GetFileName).OfType<string>()];
        }
        catch (Exception)
        {
            return found;
        }

        // Titles that still have files in use, and titles only retired files carry.
        HashSet<string> live = new(StringComparer.OrdinalIgnoreCase);
        List<string> retired = [];

        foreach (string name in names)
        {
            // Split exactly as the migration splits, or the two could disagree on
            // where a title ends.
            string? title = FederatedTitleMigration.TitleOf(name, productTag!);
            if (title is null)
                continue;

            if (name.EndsWith(FederatedTitleMigration.RetiredSuffix, StringComparison.OrdinalIgnoreCase))
                retired.Add(title);
            else
                live.Add(title);
        }

        foreach (string title in retired)
        {
            string bare = ModelTitle.Normalize(title, (string?)null);

            // The retired name merged INTO something live. When that is "bare"
            // itself, only "_detached" was stripped and the last segment is part
            // of a real model name: "Plant_Annex_detached" retired onto
            // "Plant_Annex" must not teach that "Annex" is a user, or the next run
            // would fold Plant_Annex's ledger into Plant's.
            if (live.Contains(bare))
                continue;

            int cut = bare.LastIndexOf('_');
            if (cut <= 0 || cut == bare.Length - 1)
                continue;

            if (live.Contains(bare.Substring(0, cut)))
                Add(found, bare.Substring(cut + 1));
        }

        return found;
    }
}
