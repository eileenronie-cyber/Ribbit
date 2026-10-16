using System.IO;
using System.Windows;
using System.Windows.Controls;
using Ribbit.Geometry.Clash;
using Ribbit.Native.Revit;
using Ribbit.Native.Settings;

namespace Ribbit.Native.Ui;

/// <summary>
/// The pieces the clash input forms share. Extracted so the keyword-based forms
/// cannot drift apart in how a picked value is recorded or where a report lands.
///
/// The visual primitives that used to live here (Header, Hint, KeywordRow, the
/// one-line selection label) moved to <see cref="FormParts"/> and to removable
/// chips when the three clash windows were redesigned onto one layout.
/// </summary>
internal static class ClashFormParts
{
    // The "Description applies to cable tray and conduit only" checkbox lived
    // here. The RULE is unchanged and still governs both the Browse scan and the
    // search — it is simply no longer shown, and reads from the stored
    // preference (default on). It belongs on a rule, not on the window, once
    // rules carry their own category scope.

    /// <summary>
    /// Turns what is already typed and picked into a named set.
    ///
    /// THIS IS THE PATH THAT FILLS THE LIBRARY. Building a set from scratch in a
    /// separate window is real work, and a library nobody fills is a library
    /// nobody uses; browsing and picking is something you already do on the way
    /// to running a clash, so this makes naming it one extra click.
    ///
    /// Opens the editor pre-filled rather than asking only for a name, so the
    /// selection can be trimmed before it is saved.
    /// </summary>
    public static void SaveAsSet(
        Window owner,
        ClashSetLibrary library,
        string keywordText,
        IReadOnlyList<(string Param, string Value)> picked,
        SetScanServices? services,
        string dialogTitle)
    {
        List<string> keywords = ClashStatus.ParseKeywords(keywordText);

        if (keywords.Count == 0 && picked.Count == 0)
        {
            RibbitUi.Alert(
                "Nothing to save yet - type a keyword or pick values with Browse first.",
                dialogTitle);
            return;
        }

        ClashSet seed = new()
        {
            Keywords = keywords,
            Values = [.. picked.Select(p => new SetValue { Param = p.Param, Value = p.Value })],
        };

        SetEditorWindow dlg = new(library, null, seed, services) { Owner = owner };

        if (dlg.ShowDialog() == true && dlg.Result is not null)
        {
            library.Sets.Add(dlg.Result);
            library.Save();

            RibbitUi.Notify($"Saved set \"{dlg.Result.Name}\".");
        }
    }

    /// <summary>
    /// The way into the set library, sat in the form header where it is the
    /// first thing seen rather than buried in one set's card.
    ///
    /// Accent-filled on purpose: the library is now the place sets are built,
    /// and a bordered grey button beside a section heading reads as a footnote.
    /// </summary>
    public static Button ManageSetsButton(Action onClick)
    {
        Button b = new()
        {
            Content = "Manage sets...",
            MinWidth = 132,
            MinHeight = 30,
            Padding = new Thickness(14, 4, 14, 4),
            Background = FormParts.Accent,
            Foreground = System.Windows.Media.Brushes.White,
            FontWeight = FontWeights.SemiBold,
            FontSize = 12.5,
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = "Create and edit the named sets shared by every clash tool",
        };
        b.Click += (_, _) => onClick();
        return b;
    }

    /// <summary>The form header with the set-library button docked to its right.</summary>
    public static DockPanel SectionWithManage(string title, Button manage)
    {
        DockPanel row = new() { Margin = new Thickness(0, 0, 0, 6) };

        TextBlock heading = new()
        {
            Text = title,
            FontWeight = FontWeights.SemiBold,
            FontSize = 14,
            VerticalAlignment = VerticalAlignment.Center,
        };

        DockPanel.SetDock(manage, Dock.Right);
        row.Children.Add(manage);
        row.Children.Add(heading);
        return row;
    }

    /// <summary>A small "Save as set..." button, identical in every clash card.</summary>
    public static Button SaveAsSetButton(Action onClick)
    {
        Button b = new()
        {
            Content = "Save as set...",
            MinWidth = 104,
            MinHeight = 24,
            FontSize = 11,
            Padding = new Thickness(8, 2, 8, 2),
            Margin = new Thickness(0, 6, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            ToolTip = "Save this keyword and these picked values as a reusable named set",
        };
        b.Click += (_, _) => onClick();
        return b;
    }

    /// <summary>
    /// Runs the picker and replaces <paramref name="picked"/> with the result.
    /// Leaves it untouched when the picker is cancelled, so a cancel does not
    /// silently wipe a previous selection.
    /// </summary>
    public static void PickInto(
        IReadOnlyList<ValueChoice> choices,
        List<(string Param, string Value)> picked,
        string pickerTitle,
        string dialogTitle)
    {
        if (choices.Count == 0)
        {
            RibbitUi.Alert("No System Type or Description values found.", dialogTitle);
            return;
        }

        List<string> labels =
        [
            .. choices.Select(c => $"[{c.ParamName}] {c.Display}   ({c.Count})")
        ];

        List<string>? chosen = RibbitUi.SelectManyFromList(labels, pickerTitle, "Select");
        if (chosen is null)
            return;

        HashSet<string> chosenSet = [.. chosen];

        picked.Clear();
        for (int i = 0; i < labels.Count; i++)
        {
            if (chosenSet.Contains(labels[i]))
                picked.Add(choices[i].Pair);
        }
    }

    /// <summary>
    /// Validates the folder, creating it if missing; falls back to Documents
    /// rather than ever blocking on a dialog.
    /// </summary>
    public static string ResolveFolder(string? text)
    {
        string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        string folder = (text ?? "").Trim();

        if (folder.Length == 0)
            return docs;
        if (Directory.Exists(folder))
            return folder;

        try
        {
            Directory.CreateDirectory(folder);
            return folder;
        }
        catch (Exception)
        {
            return docs;
        }
    }
}
