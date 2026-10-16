using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;

namespace Ribbit.Native.Ui;

/// <summary>
/// Replacement for pyRevit's forms.SelectFromList, which has no Revit API
/// equivalent — TaskDialog caps out at four command links, and ElbowByClick's
/// fallback menu can offer sixteen turns.
///
/// Built in code rather than XAML so the whole picker is one file and needs no
/// resource plumbing; it is deliberately plain, matching what the Python dialog
/// actually gave the user.
/// </summary>
internal sealed class SelectFromListWindow : Window
{
    private readonly ListBox _list;
    private readonly bool _multiSelect;

    public string? Selected { get; private set; }

    /// <summary>The rows, for the in-Revit test to read and select.</summary>
    internal ListBox List => _list;

    /// <summary>Every chosen item, in list order. Single-select fills this too.</summary>
    public List<string> SelectedMany { get; } = [];

    public SelectFromListWindow(
        IReadOnlyList<string> items, string title, string buttonName, bool multiSelect = false)
    {
        _multiSelect = multiSelect;
        Title = title;
        Width = 620;
        Height = 460;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        // Parent to Revit's main window so the dialog is properly modal and
        // never gets lost behind it.
        new WindowInteropHelper(this).Owner = Process.GetCurrentProcess().MainWindowHandle;

        _list = new ListBox
        {
            Margin = new Thickness(12),
            SelectionMode = multiSelect ? SelectionMode.Extended : SelectionMode.Single,
        };
        foreach (string item in items)
            _list.Items.Add(item);

        // Pre-selecting the first row is a convenience for a single pick, but a
        // hidden default for a multi-pick - the user would commit to a sheet
        // they never chose.
        if (!multiSelect && _list.Items.Count > 0)
            _list.SelectedIndex = 0;

        // Double-click accepts a single pick. On a multi-select list it would
        // fight the drag/ctrl-click the user is in the middle of.
        if (!multiSelect)
            _list.MouseDoubleClick += (_, _) => Accept();

        Button ok = new()
        {
            Content = buttonName,
            IsDefault = true,
            MinWidth = 110,
            Margin = new Thickness(0, 0, 8, 0),
            Padding = new Thickness(10, 4, 10, 4),
        };
        ok.Click += (_, _) => Accept();

        Button cancel = new()
        {
            Content = "Cancel",
            IsCancel = true,
            MinWidth = 90,
            Padding = new Thickness(10, 4, 10, 4),
        };
        cancel.Click += (_, _) => DialogResult = false;

        StackPanel buttons = new()
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(12, 0, 12, 12),
        };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        DockPanel root = new();
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        root.Children.Add(_list);
        Content = root;
    }

    private void Accept()
    {
        if (_list.SelectedItem is null)
            return;

        Selected = _list.SelectedItem as string;

        SelectedMany.Clear();
        if (_multiSelect)
        {
            // SelectedItems is in click order, not list order; the caller wants
            // sheets back in the order it presented them.
            HashSet<string> chosen = [.. _list.SelectedItems.Cast<string>()];
            foreach (string item in _list.Items.Cast<string>())
            {
                if (chosen.Contains(item))
                    SelectedMany.Add(item);
            }
        }
        else if (Selected is not null)
        {
            SelectedMany.Add(Selected);
        }

        DialogResult = true;
    }
}
