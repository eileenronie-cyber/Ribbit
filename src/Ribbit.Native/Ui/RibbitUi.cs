using Autodesk.Revit.UI;

namespace Ribbit.Native.Ui;

/// <summary>
/// The C# side of the `ribbitui` facade.
///
/// The pyRevit extension routes ~230 calls through pyRevit's own API surface
/// (forms.alert alone is 74 sites). Everything that used to reach for
/// `forms.*` or `script.*` should come through here instead, so the next tool
/// ported needs no new decisions about how a dialog gets shown — and so a
/// change of presentation is one edit rather than 74.
/// </summary>
internal static class RibbitUi
{
    /// <summary>
    /// Tests only: (kind, title, message) for every Alert/Notify/ShowReport,
    /// shown to nobody - a TaskDialog in an unattended run is a hang. Kind is
    /// "alert", "notify" or "report" (message = the report body). Unset, the
    /// real dialogs show.
    /// </summary>
    internal static Action<string, string, string>? Hook { get; set; }

    /// <summary>
    /// Tests only: answers every <see cref="Confirm"/> - (title, message) to
    /// yes/no - instead of a TaskDialog nobody can click. Unset, the real
    /// dialog shows.
    /// </summary>
    internal static Func<string, string, bool>? ConfirmHook { get; set; }

    /// <summary>Replaces pyRevit's forms.alert().</summary>
    public static void Alert(string message, string title = "Ribbit")
    {
        if (Hook is not null) { Hook("alert", title, message); return; }
        TaskDialog.Show(title, message);
    }

    /// <summary>
    /// Replaces pyRevit's forms.toast(), which has no Revit API equivalent.
    /// The Python callers already had to fall back to forms.alert() when toast
    /// was unavailable, so a dialog is the behaviour they were written against.
    /// If a real toast is wanted later, this is the one place to change.
    /// </summary>
    public static void Notify(string message, string title = "Ribbit")
    {
        if (Hook is not null) { Hook("notify", title, message); return; }
        TaskDialog.Show(title, message);
    }

    /// <summary>
    /// Replaces pyRevit's script.get_output(). Shows a modeless HTML report;
    /// returns immediately so the caller can go straight on to its confirmation
    /// prompt while the report stays readable behind it — the pyRevit flow.
    /// </summary>
    /// <param name="onMessage">
    /// Optional handler for actions the page posts back — copying an id, saving
    /// a status. Omitted, the report is display-only, which is what every
    /// non-clash report wants.
    /// </param>
    public static void ShowReport(
        string title, string bodyHtml, Func<string, string?>? onMessage = null)
    {
        if (Hook is not null) { Hook("report", title, bodyHtml); return; }
        RevitLinks.EnsureInitialized();
        ReportWindow window = new(title, ReportHtml.Document(title, bodyHtml), onMessage);
        window.Show();
    }

    /// <summary>
    /// Replaces pyRevit's forms.alert(..., yes=True, no=True). Returns true
    /// only on an explicit Yes — closing the dialog counts as No, matching the
    /// Python, where every caller treats a falsey result as "declined".
    /// </summary>
    public static bool Confirm(string message, string title = "Ribbit")
    {
        if (ConfirmHook is not null)
            return ConfirmHook(title, message);

        TaskDialog dialog = new(title)
        {
            MainInstruction = message,
            CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No,
            DefaultButton = TaskDialogResult.No,
        };
        return dialog.Show() == TaskDialogResult.Yes;
    }

    /// <summary>
    /// Replaces pyRevit's forms.SelectFromList.show(). Returns the chosen item,
    /// or null if the user cancelled.
    /// </summary>
    public static string? SelectFromList(
        IReadOnlyList<string> items, string title, string buttonName = "Select")
    {
        if (items.Count == 0)
            return null;

        SelectFromListWindow window = new(items, title, buttonName);
        if (TestOwner is not null)
            window.Owner = TestOwner;
        return window.ShowDialog() == true ? window.Selected : null;
    }

    /// <summary>
    /// Tests only: the WPF window list pickers are owned by, so the test's
    /// UiScript can find the REAL picker and click through it. Unset, they are
    /// owned by Revit's main window.
    /// </summary>
    internal static System.Windows.Window? TestOwner { get; set; }

    /// <summary>
    /// Multi-select variant. Returns the chosen items in the order they were
    /// presented, or null if the user cancelled.
    /// </summary>
    public static List<string>? SelectManyFromList(
        IReadOnlyList<string> items, string title, string buttonName = "Select")
    {
        if (items.Count == 0)
            return null;

        SelectFromListWindow window = new(items, title, buttonName, multiSelect: true);
        return window.ShowDialog() == true ? window.SelectedMany : null;
    }
}
