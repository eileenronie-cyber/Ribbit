using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Ribbit.Native.Settings;

namespace Ribbit.Native.Ui;

/// <summary>
/// The shared set library: create, edit, duplicate and delete the named sets
/// every clash tool draws on.
///
/// Opened from any clash window, and edits are saved as they are made rather than
/// on a final OK. The window sits over a clash form that is itself mid-edit, so a
/// Cancel here would raise the question of what it undoes — the set you just
/// renamed, or the one you deleted two steps ago. Saving each action outright
/// keeps that unambiguous, and Delete is the only destructive one, which asks.
/// </summary>
internal sealed class SetLibraryWindow : Window
{
    private const string DialogTitle = "Set Library";

    private readonly ClashSetLibrary _library;
    private readonly StackPanel _listHost;
    private readonly TextBlock _count;

    /// <summary>
    /// Passed through to the editor so it can count matches and flag stale
    /// values. Null when no model scan is available.
    /// </summary>
    private readonly SetScanServices? _services;

    public SetLibraryWindow(ClashSetLibrary library, SetScanServices? services = null)
    {
        _library = library;
        _services = services;

        Title = DialogTitle;
        Width = 620;
        Height = 560;
        MinWidth = 460;
        MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        new WindowInteropHelper(this).Owner =
            System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;

        StackPanel panel = new() { Margin = new Thickness(FormParts.Gutter) };

        panel.Children.Add(FormParts.Section("Sets", first: true));
        panel.Children.Add(FormParts.Hint(
            "Named, reusable sets of values, keywords and categories, shared by every clash tool. "
            + "Sets that contain categories can be ticked in a clash window to force everything in "
            + "them into Set B."));

        _count = new TextBlock
        {
            FontSize = 11,
            Foreground = SystemColors.GrayTextBrush,
            VerticalAlignment = VerticalAlignment.Center,
        };

        Button add = FormParts.Primary("New set...");
        add.Margin = new Thickness(0, 0, 0, 0);
        add.Click += (_, _) => NewSet();

        Button restore = new()
        {
            Content = "Restore built-ins",
            MinWidth = 118,
            MinHeight = 24,
            FontSize = 11,
            Padding = new Thickness(8, 2, 8, 2),
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "Re-add any of the six original sets that are missing. "
                      + "Your own sets are left alone.",
        };
        restore.Click += (_, _) => RestoreBuiltIns();

        static Button Small(string text, string tip)
            => new()
            {
                Content = text,
                MinWidth = 74,
                MinHeight = 24,
                FontSize = 11,
                Padding = new Thickness(8, 2, 8, 2),
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = tip,
            };

        Button export = Small(
            "Export...",
            "Write these sets to a file to share or keep. The file holds the rules only.");
        export.Click += (_, _) => ExportSets();

        Button import = Small(
            "Import...",
            "Read sets from an exported file. Each one arrives as a set of this library's own.");
        import.Click += (_, _) => ImportSets();

        DockPanel head = new() { Margin = new Thickness(0, 0, 0, 9) };
        DockPanel.SetDock(add, Dock.Right);
        DockPanel.SetDock(restore, Dock.Right);
        DockPanel.SetDock(import, Dock.Right);
        DockPanel.SetDock(export, Dock.Right);
        head.Children.Add(add);
        head.Children.Add(restore);
        head.Children.Add(import);
        head.Children.Add(export);
        head.Children.Add(_count);
        panel.Children.Add(head);

        _listHost = new StackPanel();

        panel.Children.Add(new Border
        {
            BorderBrush = SystemColors.ControlDarkBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            Background = SystemColors.WindowBrush,
            Padding = new Thickness(4, 4, 4, 4),
            Child = new ScrollViewer
            {
                Height = 340,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = _listHost,
            },
        });

        panel.Children.Add(FormParts.Divider());
        panel.Children.Add(FormParts.ButtonBar(FormParts.Secondary("Close", true)));

        Content = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = panel,
        };

        Refresh();
    }

    private void Refresh()
    {
        _listHost.Children.Clear();
        _count.Text = _library.Sets.Count == 1 ? "1 set" : $"{_library.Sets.Count} sets";

        if (_library.Sets.Count == 0)
        {
            _listHost.Children.Add(FormParts.EmptyChipNote(
                "No sets yet - New set... creates one."));
            return;
        }

        foreach (ClashSet set in _library.Sets)
        {
            ClashSet captured = set;
            _listHost.Children.Add(Row(captured));
        }
    }

    private Border Row(ClashSet set)
    {
        StackPanel text = new() { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock
        {
            Text = set.Name,
            FontWeight = FontWeights.SemiBold,
            FontSize = 13,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        text.Children.Add(new TextBlock
        {
            Text = ClashSetLibrary.Describe(set),
            FontSize = 11,
            Foreground = SystemColors.GrayTextBrush,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });

        StackPanel actions = new()
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        actions.Children.Add(Small("Edit", () => EditSet(set)));
        actions.Children.Add(Small("Duplicate", () => DuplicateSet(set)));
        actions.Children.Add(Small("Delete", () => DeleteSet(set)));

        DockPanel row = new();
        DockPanel.SetDock(actions, Dock.Right);
        row.Children.Add(actions);
        row.Children.Add(text);

        return new Border
        {
            BorderBrush = SystemColors.ControlDarkBrush,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(9, 7, 7, 7),
            Child = row,
        };
    }

    private static Button Small(string text, Action onClick)
        => FormParts.SmallButton(text, onClick, minWidth: 68, margin: new Thickness(5, 0, 0, 0));

    /// <summary>
    /// Re-adds any of the six seeded sets that are missing, matched by NAME.
    ///
    /// The library is only seeded when its file does not exist, so a set deleted
    /// once was gone for good — there was no way back to "All MEP
    /// Devices/Equipment" short of retyping twelve categories. Re-seeding on
    /// every load would be worse: it would resurrect sets deliberately deleted.
    /// An explicit button is the honest middle.
    ///
    /// Matching on name means a built-in you have EDITED is left alone rather
    /// than reverted — your version keeps the name, so it is not missing.
    /// </summary>
    private void RestoreBuiltIns()
    {
        List<ClashSet> missing =
        [
            .. ClashSetLibrary.Seed().Sets.Where(s => _library.Find(s.Name) is null)
        ];

        if (missing.Count == 0)
        {
            RibbitUi.Alert(
                "All six built-in sets are already here, by name. Nothing to restore.",
                DialogTitle);
            return;
        }

        if (!RibbitUi.Confirm(
                $"Add back {missing.Count} missing built-in set"
                + $"{(missing.Count == 1 ? "" : "s")}?\n\n"
                + string.Join("\n", missing.Select(s => $"  {s.Name}"))
                + "\n\nYour own sets are not touched.",
                DialogTitle))
            return;

        _library.Sets.AddRange(missing);
        _library.Save();
        Refresh();
    }

    private void NewSet()
    {
        SetEditorWindow dlg = new(_library, null, null, _services) { Owner = this };

        if (dlg.ShowDialog() == true && dlg.Result is not null)
        {
            _library.Sets.Add(dlg.Result);
            _library.Save();
            Refresh();
        }
    }

    private void EditSet(ClashSet set)
    {
        SetEditorWindow dlg = new(_library, set, null, _services) { Owner = this };

        if (dlg.ShowDialog() == true && dlg.Result is not null)
        {
            // Edited in place so anything holding this instance - the calling
            // clash window's ticked list - keeps pointing at the same set.
            // CopyRulesFrom, not field by field: a field copied by hand here is
            // a field the next one added gets forgotten in.
            set.Name = dlg.Result.Name;
            set.CopyRulesFrom(dlg.Result);
            _library.Save();
            Refresh();
        }
    }

    private void DuplicateSet(ClashSet set)
    {
        ClashSet copy = set.Copy();

        // "Name (2)", "Name (3)" - the first free one.
        int n = 2;
        string baseName = copy.Name;
        while (_library.NameTaken(copy.Name))
            copy.Name = $"{baseName} ({n++})";

        _library.Sets.Insert(_library.Sets.IndexOf(set) + 1, copy);
        _library.Save();
        Refresh();
    }

    /// <summary>
    /// Writes every set to a file, rules only.
    ///
    /// ALL of them, not a selection: the list has no tick boxes and adding them
    /// for this alone would complicate the common case, which is handing over
    /// the whole library. Anything unwanted is deleted after import, where it is
    /// one click on a set you can see.
    /// </summary>
    private void ExportSets()
    {
        if (_library.Sets.Count == 0)
        {
            RibbitUi.Alert("There are no sets to export.", DialogTitle);
            return;
        }

        Microsoft.Win32.SaveFileDialog dlg = new()
        {
            Title = "Export clash sets",
            Filter = ClashSetTransferIo.FileFilter,
            FileName = ClashSetTransferIo.DefaultFileName,
            DefaultExt = ".txt",
            OverwritePrompt = true,
        };

        if (dlg.ShowDialog() != true)
            return;

        try
        {
            ClashSetTransferIo.Export(
                dlg.FileName, _library.Sets, $"Exported from Ribbit on {DateTime.Now:yyyy-MM-dd}.");

            RibbitUi.Notify(
                $"Exported {_library.Sets.Count} set(s). It is a plain text list you can open and "
                + "read - rules only, no ids - so importing it anywhere creates sets that "
                + "library owns.");
        }
        catch (Exception ex)
        {
            RibbitUi.Alert($"Couldn't export:\n\n{ex.Message}", DialogTitle);
        }
    }

    /// <summary>
    /// Reads a bundle and merges it, asking what to do about names already here.
    ///
    /// The question is asked ONCE for the whole file rather than per set: an
    /// import is normally one decision ("take theirs" / "keep mine"), and a
    /// prompt per set turns a ten-set file into ten dialogs.
    /// </summary>
    private void ImportSets()
    {
        Microsoft.Win32.OpenFileDialog dlg = new()
        {
            Title = "Import clash sets",
            Filter = ClashSetTransferIo.FileFilter,
            DefaultExt = ".txt",
            CheckFileExists = true,
        };

        if (dlg.ShowDialog() != true)
            return;

        List<ClashSetTransfer> incoming;
        try
        {
            incoming = ClashSetTransferIo.Import(dlg.FileName);
        }
        catch (Exception ex)
        {
            RibbitUi.Alert($"Couldn't read that file:\n\n{ex.Message}", DialogTitle);
            return;
        }

        if (incoming.Count == 0)
        {
            RibbitUi.Alert("That file contains no usable sets.", DialogTitle);
            return;
        }

        List<string> clashing =
            [.. incoming.Select(s => s.Name).Where(n => _library.Find(n) is not null)];

        ClashSetLibrary.ImportClash mode = ClashSetLibrary.ImportClash.KeepBoth;

        if (clashing.Count > 0)
        {
            string? choice = RibbitUi.SelectFromList(
                [
                    "Update them - keep my set, replace its rules",
                    "Keep both - bring the incoming ones in as \"(2)\"",
                    "Skip them - leave my versions alone",
                ],
                $"{clashing.Count} of {incoming.Count} set(s) already exist here by name: "
                + string.Join(", ", clashing.Take(6))
                + (clashing.Count > 6 ? ", ..." : "")
                + ". What should happen to those?",
                "Import");

            if (choice is null)
                return;

            mode = choice.StartsWith("Update", StringComparison.Ordinal)
                ? ClashSetLibrary.ImportClash.Update
                : choice.StartsWith("Skip", StringComparison.Ordinal)
                    ? ClashSetLibrary.ImportClash.Skip
                    : ClashSetLibrary.ImportClash.KeepBoth;
        }

        ClashSetLibrary.ImportOutcome outcome;
        try
        {
            outcome = _library.Import(incoming, mode);

            // Import merges in memory only, so the save is here - beside every
            // other mutation in this window.
            if (outcome.Added > 0 || outcome.Updated > 0)
                _library.Save();
        }
        catch (Exception ex)
        {
            RibbitUi.Alert($"Couldn't import:\n\n{ex.Message}", DialogTitle);
            return;
        }

        Refresh();

        List<string> parts = [];
        if (outcome.Added > 0) parts.Add($"{outcome.Added} added");
        if (outcome.Updated > 0) parts.Add($"{outcome.Updated} updated");
        if (outcome.Skipped > 0) parts.Add($"{outcome.Skipped} skipped");

        RibbitUi.Notify(parts.Count == 0 ? "Nothing to import." : string.Join(", ", parts) + ".");
    }

    private void DeleteSet(ClashSet set)
    {
        if (!RibbitUi.Confirm(
                $"Delete the set \"{set.Name}\"?\n\n"
                + "Any clash window that has it ticked will lose that selection.",
                DialogTitle))
            return;

        _library.Sets.Remove(set);
        _library.Save();
        Refresh();
    }
}
