using System.Windows;

namespace Ribbit.Native.Ui;

/// <summary>
/// Every message box the Adjust Grids window and its dialogs show, in one
/// place. A Windows message box is a native modal nobody can answer in an
/// unattended in-Revit test, so the tests set <see cref="Hook"/>: it records
/// the question and gives the answer the test chose. Unset - always, outside
/// a test - this is exactly MessageBox.Show.
/// </summary>
internal static class Prompt
{
    /// <summary>(text, caption, buttons) -> the answer. Tests only.</summary>
    internal static Func<string, string, MessageBoxButton, MessageBoxResult>? Hook { get; set; }

    public static MessageBoxResult Show(Window owner, string text, string caption,
        MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage icon = MessageBoxImage.None)
        => Hook is { } hook ? hook(text, caption, buttons) : MessageBox.Show(owner, text, caption, buttons, icon);
}
