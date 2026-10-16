namespace Ribbit.Geometry;

/// <summary>
/// Decides which progress reports are worth putting on screen.
///
/// Callers report every item; a repaint costs a full UI round-trip, so on a
/// large model reporting all of them can cost more than the work being
/// measured. This caps a run at roughly <see cref="MaxRepaints"/> repaints
/// however many items it processes.
/// </summary>
public static class ProgressThrottle
{
    /// <summary>Roughly how many repaints a whole run is allowed.</summary>
    public const int MaxRepaints = 100;

    /// <summary>
    /// True when <paramref name="value"/> should actually be drawn, given the
    /// value last drawn for this same <paramref name="max"/>.
    ///
    /// Pass -1 as <paramref name="lastReported"/> when nothing has been drawn
    /// yet for this total.
    ///
    /// Two cases always draw regardless of the step: the first report, so the
    /// bar starts from something real, and the last item, so it finishes full
    /// rather than stopping just short of the end. A run shorter than
    /// <see cref="MaxRepaints"/> items is unaffected — the step falls to 1 and
    /// every item draws.
    /// </summary>
    public static bool ShouldReport(int value, int max, int lastReported)
    {
        if (value >= max - 1)
            return true;
        if (lastReported < 0)
            return true;

        return value - lastReported >= Math.Max(1, max / MaxRepaints);
    }
}
