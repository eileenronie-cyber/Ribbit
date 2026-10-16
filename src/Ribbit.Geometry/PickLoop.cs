namespace Ribbit.Geometry;

/// <summary>
/// The pick loop shared by Connect / Connect Multi and Align / Align Multi: what the
/// first pick's prompt says, and whether to ask for another pair.
///
/// Single (multiple = false): keeps asking until ONE pair works, then ends on its own,
/// no Esc needed. A pair that could not be done asks again, saying why in the prompt;
/// it used to end the command without a word, which read exactly like a dead click.
///
/// Multi (multiple = true): keeps asking until Esc. The prompt counts what has been
/// done and carries the last failure's reason until the next success clears it,
/// because the prompt is the one place the user is already reading.
///
/// Revit-free so the decisions are unit-tested; the engines own the picking and the
/// Esc handling (an Esc at either pick ends the loop at once, in both modes).
/// </summary>
public sealed class PickLoop(string ask, string doneWord, bool multiple)
{
    /// <summary>How many pairs have worked so far.</summary>
    public int Done { get; private set; }

    /// <summary>Why the most recent pair failed, or null after a success (or before any pair).</summary>
    public string? LastNote { get; private set; }

    /// <summary>The prompt for the first pick of the next pair.</summary>
    public string Prompt => multiple
        ? $"{ask} ({Done} {doneWord}" + (LastNote is null ? "" : $"; last: {LastNote}") + ", Esc to finish)"
        : LastNote is null
            ? ask
            : $"{ask} (last pair: {LastNote}; Esc to quit)";

    /// <summary>
    /// Records one pair's outcome - <paramref name="failure"/> null means it worked - and
    /// says whether to ask for another pair.
    /// </summary>
    public bool Record(string? failure)
    {
        if (failure is null)
        {
            Done++;
            LastNote = null;
        }
        else
        {
            LastNote = failure;
        }

        return multiple || failure is not null;
    }
}
