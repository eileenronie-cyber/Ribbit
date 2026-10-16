using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Ribbit.Native.Revit;
// Autodesk.Revit.DB has a Grid too (the datum kind); the WPF one wins here.
using Grid = System.Windows.Controls.Grid;

namespace Ribbit.Native.Ui;

/// <summary>
/// Picks one parameter out of everything the searched models carry.
///
/// A SEARCH BOX, NOT A SCROLL. A real model offers hundreds of parameter names,
/// and the one you want is usually one you can already name — typing three
/// letters of it beats hunting an alphabetical list. The generic list picker has
/// no filter, which is why this window exists rather than reusing it.
///
/// Instance, type and shared are shown as separate rows even when they share a
/// name, because they read through different lookups and picking the wrong one
/// produces a set that matches nothing.
/// </summary>
internal sealed class ParameterPickWindow : Window
{
    private readonly TextBox _filter;
    private readonly ListBox _list;
    private readonly TextBlock _count;
    private readonly List<ParameterChoice> _all;

    public ParameterChoice? Result { get; private set; }

    public ParameterPickWindow(IReadOnlyList<ParameterChoice> parameters)
    {
        _all = [.. parameters];

        Title = "Pick a parameter";
        Width = 560;
        Height = 620;
        MinWidth = 400;
        MinHeight = 380;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        new WindowInteropHelper(this).Owner =
            System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;

        StackPanel panel = new() { Margin = new Thickness(FormParts.Gutter) };

        panel.Children.Add(FormParts.Section("Parameters", first: true));
        panel.Children.Add(FormParts.Hint(
            "Every parameter found on the searched elements - instance, type and shared. "
            + "The number is roughly how many elements carry it, so the ones most of the model "
            + "uses sort to the top."));

        (Grid filterHost, TextBox filterBox) = FormParts.PlaceholderInput(
            "Search parameters...  e.g. type name");

        _filter = filterBox;
        _filter.TextChanged += (_, _) => ApplyFilter();

        // Enter accepts the highlighted row, so a search can be typed and taken
        // without reaching for the mouse.
        _filter.KeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Enter)
                Accept();
        };

        _count = new TextBlock
        {
            FontSize = 11,
            Foreground = SystemColors.GrayTextBrush,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0),
        };

        DockPanel filterRow = new() { Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(_count, Dock.Right);
        filterRow.Children.Add(_count);
        filterRow.Children.Add(filterHost);
        panel.Children.Add(filterRow);

        _list = new ListBox { Height = 400, FontSize = 12.5 };
        _list.MouseDoubleClick += (_, _) => Accept();
        panel.Children.Add(_list);

        panel.Children.Add(FormParts.Divider());

        Button ok = FormParts.Primary("Use this parameter");
        ok.Click += (_, _) => Accept();
        panel.Children.Add(FormParts.ButtonBar(ok, FormParts.Secondary("Cancel", true)));

        Content = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = panel,
        };

        ApplyFilter();
        Loaded += (_, _) => _filter.Focus();
    }

    /// <summary>The rows currently shown, in list order.</summary>
    private List<ParameterChoice> _shown = [];

    private void ApplyFilter()
    {
        string needle = (_filter.Text ?? "").Trim();

        _shown = needle.Length == 0
            ? [.. _all]
            : [.. _all.Where(p => p.Name.Contains(needle, StringComparison.OrdinalIgnoreCase))];

        _list.ItemsSource = _shown.Select(p => $"{p.Display}   ~{p.ElementCount}").ToList();

        // Preselect the first match so Enter is always meaningful.
        if (_shown.Count > 0)
            _list.SelectedIndex = 0;

        _count.Text = needle.Length == 0
            ? $"{_all.Count} parameters"
            : $"{_shown.Count} of {_all.Count}";
    }

    private void Accept()
    {
        int i = _list.SelectedIndex;
        if (i < 0 || i >= _shown.Count)
        {
            RibbitUi.Alert("Pick a parameter first.", "Pick a parameter");
            return;
        }

        Result = _shown[i];
        DialogResult = true;
    }
}
