namespace Ribbit.Geometry.Clash;

/// <summary>
/// Publishing a file so that a concurrent reader never sees it half-written.
///
/// The clash folder is SHARED — that is the whole reason the status ledger is
/// append-only deltas rather than a rewritten file. But writing straight to the
/// final path undoes a good part of that: opening a StreamWriter with
/// <c>append: false</c> truncates the file at OPEN and the rows arrive
/// afterwards, and the default share mode lets readers in during the gap. A
/// reader landing in that window parses a truncated ledger, believes those
/// statuses do not exist, and — if it then compacts — writes its truncated view
/// back as the new base and deletes the deltas. Statuses that lived only in the
/// base file are gone at that point, with nothing to say so.
///
/// Writing to a temporary file in the SAME folder and then swapping it in fixes
/// it: same folder so the swap stays on one volume, where NTFS makes it atomic.
/// A reader sees the whole old file or the whole new one.
/// </summary>
public static class AtomicFile
{
    /// <summary>
    /// A temp path beside <paramref name="finalPath"/>. Same directory on
    /// purpose — a cross-volume move is a copy, and copies are not atomic.
    /// </summary>
    public static string TempPathFor(string finalPath)
    {
        string dir = Path.GetDirectoryName(finalPath) ?? ".";
        string name = Path.GetFileName(finalPath);
        return Path.Combine(dir, $".{name}.tmp-{Guid.NewGuid().ToString("N").Substring(0, 8)}");
    }

    /// <summary>
    /// Swaps a finished temp file into place, replacing whatever is there.
    ///
    /// File.Replace rather than the three-argument File.Move: this assembly
    /// targets netstandard2.0 for the Revit 2024 leg, where that overload does
    /// not exist. Replace needs the destination to already exist, hence the
    /// Move for a first write.
    ///
    /// On failure the temp file is removed and the exception is left to the
    /// caller — every caller here treats a failed write as "skip it, the data is
    /// still safe where it was".
    /// </summary>
    public static void Commit(string tempPath, string finalPath)
    {
        try
        {
            if (File.Exists(finalPath))
                File.Replace(tempPath, finalPath, null);
            else
                File.Move(tempPath, finalPath);
        }
        catch (Exception)
        {
            try
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
            catch (IOException)
            {
            }

            throw;
        }
    }
}
