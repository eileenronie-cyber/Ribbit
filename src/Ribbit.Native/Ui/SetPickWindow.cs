using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Ribbit.Native.Settings;
// Autodesk.Revit.DB has a Grid too (the datum kind); the WPF one wins here.
using Grid = System.Windows.Controls.Grid;

namespace Ribbit.Native.Ui;

/// <summary>
/// Chooses which named sets drive one side of a clash.
///
/// Multi-select and UNIONED: two sets chosen means an element joins if either
/// matches. That mirrors how the rules inside a single set already combine, so
/// there is only one rule to learn — everything ORs.
///
/// Shows each set's contents under its name. A list of bare names gives no way
/// to tell "52 Series" from "52 Series (old)" without opening both.
/// </summary>
internal sealed class SetPickWindow : Window
{
    private readonly TextBox _filter;
    private readonly StackPanel _listHost;
    private readonly TextBlock _count;

    private readonly List<(ClashSet Set, CheckBox Box)> _boxes = [];

    /// <summary>The chosen set IDS - stable across a rename.</summary>
    public List<string> Result { get; private set; } = [];

    public SetPickWindow(ClashSetLibrary library, string side, IReadOnlyList<string> chosen)
    {
        Title = $"Choose sets for {side}";
        Width = 560;
        Height = 580;
        MinWidth = 420;
        MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        new WindowInteropHelper(this).Owner =
            System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;

        HashSet<string> already = new(chosen, StringComparer.Ordinal);

        StackPanel panel = new() { Margin = new Thickness(FormParts.Gutter) };

        panel.Children.Add(FormParts.Section($"Sets for {side}", first: true));
        panel.Children.Add(FormParts.Hint(
            "Tick as many as you need - an element joins the side if ANY ticked set matches it. "
            + "Anything typed in the keyword box is ORed with these."));

        (Grid filterHost, TextBox filterBox) = FormParts.PlaceholderInput("Filter sets...");
        _filter = filterBox;
        _filter.TextChanged += (_, _) => ApplyFilter();

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

        _listHost = new StackPanel();

        foreach (ClashSet set in library.Sets)
        {
            ClashSet captured = set;

            StackPanel label = new();
            label.Children.Add(new TextBlock
            {
                Text = captured.Name,
                FontSize = 12.5,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            label.Children.Add(new TextBlock
            {
                Text = ClashSetLibrary.Describe(captured),
                FontSize = 10.5,
                Foreground = SystemColors.GrayTextBrush,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });

            CheckBox box = new()
            {
                Content = label,
                IsChecked = already.Contains(captured.Id),
                Margin = new Thickness(0, 4, 0, 4),
            };
            box.Checked += (_, _) => RefreshCount();
            box.Unchecked += (_, _) => RefreshCount();

            _boxes.Add((captured, box));
        }

        panel.Children.Add(new Border
        {
            BorderBrush = SystemColors.ControlDarkBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            Background = SystemColors.WindowBrush,
            Padding = new Thickness(9, 6, 9, 6),
            Child = new ScrollViewer
            {
                Height = 320,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = _listHost,
            },
        });

        StackPanel bulk = new()
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 7, 0, 0),
        };
        bulk.Children.Add(Small("Clear all", () =>
        {
            foreach ((_, CheckBox b) in _boxes)
                b.IsChecked = false;

            RefreshCount();
        }));
        panel.Children.Add(bulk);

        panel.Children.Add(FormParts.Divider());

        Button ok = FormParts.Primary("Use these sets");
        ok.Click += (_, _) =>
        {
            Result = [.. _boxes.Where(p => p.Box.IsChecked == true).Select(p => p.Set.Id)];
            DialogResult = true;
        };

        panel.Children.Add(FormParts.ButtonBar(ok, FormParts.Secondary("Cancel", true)));

        Content = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = panel,
        };

        ApplyFilter();
        RefreshCount();
    }

    private static Button Small(string text, Action onClick)
        => FormParts.SmallButton(text, onClick);

    /// <summary>
    /// Rebuilds the visible rows. Tick state lives on the CheckBox itself and the
    /// same instances are re-parented, so filtering never loses a selection.
    /// </summary>
    private void ApplyFilter()
    {
        string needle = (_filter.Text ?? "").Trim();
        _listHost.Children.Clear();

        foreach ((ClashSet set, CheckBox box) in _boxes)
        {
            if (needle.Length > 0
                && !set.Name.Contains(needle, StringComparison.OrdinalIgnoreCase))
                continue;

            _listHost.Children.Add(box);
        }

        if (_listHost.Children.Count == 0)
        {
            _listHost.Children.Add(FormParts.EmptyChipNote(
                _boxes.Count == 0
                    ? "No sets in the library yet - build one with Manage sets..."
                    : $"No set matches \"{needle}\"."));
        }
    }

    private void RefreshCount()
    {
        int n = _boxes.Count(p => p.Box.IsChecked == true);
        _count.Text = n == 0 ? "none chosen" : $"{n} chosen";
    }
}
