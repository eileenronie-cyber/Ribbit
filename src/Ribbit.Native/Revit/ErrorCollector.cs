using Autodesk.Revit.DB;

namespace Ribbit.Native.Revit;

/// <summary>
/// A transaction's failures, handled without a dialog: warnings are cleared,
/// ERRORS roll the transaction back and are kept here, to be reported in words.
///
/// Why not let Revit show them: in a batch over many views a modal error stops
/// the run halfway with no context, and in an unattended test run nobody is
/// there to click it - the in-Revit harness sat 17 minutes on a
/// "cannot be ignored" dialog before this existed. An application-wide
/// FailuresProcessing handler did NOT stop that dialog (the journal logged
/// "Error dialog has no callback"); a per-transaction preprocessor does.
/// </summary>
internal sealed class ErrorCollector : IFailuresPreprocessor
{
    public List<string> Errors { get; } = [];

    /// <summary>The warnings that were cleared, in words (for a caller that reports them).</summary>
    public List<string> Warnings { get; } = [];

    public FailureProcessingResult PreprocessFailures(FailuresAccessor fa)
    {
        bool error = false;
        foreach (FailureMessageAccessor f in fa.GetFailureMessages())
        {
            if (f.GetSeverity() == FailureSeverity.Warning)
            {
                Warnings.Add(f.GetDescriptionText());
                fa.DeleteWarning(f);
                continue;
            }

            error = true;
            Errors.Add(f.GetDescriptionText());
        }

        return error ? FailureProcessingResult.ProceedWithRollBack : FailureProcessingResult.Continue;
    }

    /// <summary>Starts <paramref name="t"/> with a new collector attached, and returns the collector.</summary>
    public static ErrorCollector Start(Transaction t)
    {
        t.Start();
        ErrorCollector c = new();
        FailureHandlingOptions o = t.GetFailureHandlingOptions();
        o.SetFailuresPreprocessor(c);
        o.SetForcedModalHandling(false);
        o.SetClearAfterRollback(true);
        t.SetFailureHandlingOptions(o);
        return c;
    }
}
