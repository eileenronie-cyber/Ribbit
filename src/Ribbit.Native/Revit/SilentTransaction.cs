using Autodesk.Revit.DB;

namespace Ribbit.Native.Revit;

/// <summary>
/// Auto-resolves every warning raised while the transaction is open, so an
/// operation never pops up a modal dialog mid-run.
///
/// Only genuine errors are left alone — Revit handles those itself regardless,
/// and swallowing them would risk silently corrupting the model.
/// </summary>
internal sealed class SilentFailuresPreprocessor : IFailuresPreprocessor
{
    public FailureProcessingResult PreprocessFailures(FailuresAccessor failuresAccessor)
    {
        foreach (FailureMessageAccessor failure in failuresAccessor.GetFailureMessages())
        {
            if (failure.GetSeverity() == FailureSeverity.Warning)
                failuresAccessor.DeleteWarning(failure);
        }

        return FailureProcessingResult.Continue;
    }
}

internal static class SilentTransaction
{
    /// <summary>
    /// Starts a transaction whose warnings are swallowed. The caller still owns
    /// Commit/RollBack and disposal.
    /// </summary>
    public static Transaction Start(Document doc, string name)
    {
        Transaction t = new(doc, name);
        t.Start();

        FailureHandlingOptions opts = t.GetFailureHandlingOptions();
        opts.SetFailuresPreprocessor(new SilentFailuresPreprocessor());
        opts.SetForcedModalHandling(false);
        t.SetFailureHandlingOptions(opts);

        return t;
    }
}
