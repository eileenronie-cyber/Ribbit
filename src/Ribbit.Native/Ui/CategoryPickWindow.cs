using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Autodesk.Revit.DB;
using Ribbit.Native.Revit;
// Autodesk.Revit.DB has a Grid too (the datum kind); the WPF one wins here.
using Grid = System.Windows.Controls.Grid;

namespace Ribbit.Native.Ui;

/// <summary>
/// Ticks categories for a set.
///
/// The list offered is the clash universe — the 24 categories
/// <see cref="ClashCategories.NameToBic"/> collects — because a category outside
/// it is never collected, so ticking it would add exactly nothing. Anything
/// already stored that falls outside (hand-edited, or written by a later build)
/// is carried through untouched rather than dropped on save.
/// </summary>
internal sealed class CategoryPickWindow : Window
{
    private readonly TextBox _filter;
    private readonly StackPanel _listHost;
    private readonly TextBlock _count;

    private readonly List<(BuiltInCategory Bic, string Name, CheckBox Box)> _boxes = [];
    private readonly List<string> _unknown = [];

    /// <summary>Enum names, ready to store.</summary>
    public List<string> Result { get; private set; } = [];

    /// <param name="allCategories">
    /// Every category in the model, when the caller can supply them. Federated
    /// Clash does, which is what makes Structural Framing and Walls choosable;
    /// it widens what that tool collects to match. Null falls back to the 24 MEP
    /// categories — the only ones the older tools ever gather, so offering more
    /// there would be offering a set that matches nothing.
    /// </param>
    public CategoryPickWindow(
        IReadOnlyList<string> current, IReadOnlyList<Category>? allCategories = null)
    {
        Title = "Categories";
        Width = 520;
        Height = 560;
        MinWidth = 380;
        MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        new WindowInteropHelper(this).Owner =
            System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;

        // The offered list: the 24 MEP categories FIRST, in their curated order,
        // then everything else alphabetically. The MEP ones are what gets picked
        // most, and burying them in an alphabetical list of 200 would make the
        // common case the slow one.
        List<(string Name, BuiltInCategory Bic)> offered = [.. ClashCategories.NameToBic];

        if (allCategories is not null)
        {
            HashSet<long> already = [.. offered.Select(p => (long)p.Bic)];
            List<(string Name, BuiltInCategory Bic)> rest = [];

            foreach (Category cat in allCategories)
            {
                try
                {
                    long id = RevitCompat.IdKey(cat.Id);
                    if (already.Contains(id))
                        continue;

                    string name = cat.Name ?? "";
                    if (name.Length == 0)
                        continue;

                    rest.Add((name, (BuiltInCategory)id));
                }
                catch (Exception)
                {
                    // A category that will not identify itself is not offered.
                }
            }

            rest.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            offered.AddRange(rest);
        }

        HashSet<BuiltInCategory> selected = [];
        HashSet<BuiltInCategory> known = [.. offered.Select(p => p.Bic)];

        foreach (string name in current)
        {
            if (!Enum.TryParse(name, out BuiltInCategory bic))
            {
                _unknown.Add(name);
                continue;
            }

            if (known.Contains(bic))
                selected.Add(bic);
            else
                _unknown.Add(name);
        }

        StackPanel panel = new() { Margin = new Thickness(FormParts.Gutter) };

        panel.Children.Add(FormParts.Section("Categories", first: true));
        panel.Children.Add(FormParts.Hint(
            "Everything in a ticked category joins the set, regardless of keywords."));

        (Grid filterHost, TextBox filterBox) = FormParts.PlaceholderInput("Filter categories...");
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

        foreach ((string name, BuiltInCategory bic) in offered)
        {
            CheckBox box = new()
            {
                Content = name,
                IsChecked = selected.Contains(bic),
                FontSize = 12,
                Margin = new Thickness(0, 2, 0, 2),
            };
            box.Checked += (_, _) => RefreshCount();
            box.Unchecked += (_, _) => RefreshCount();

            _boxes.Add((bic, name, box));
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
        bulk.Children.Add(Small("Select all shown", () => SetShown(true)));
        bulk.Children.Add(Small("Clear all shown", () => SetShown(false)));
        panel.Children.Add(bulk);

        if (_unknown.Count > 0)
        {
            panel.Children.Add(FormParts.Hint(
                $"Plus {_unknown.Count} outside the clash category list, kept as-is: "
                + string.Join(", ", _unknown)));
        }

        panel.Children.Add(FormParts.Divider());

        Button ok = FormParts.Primary("OK");
        ok.Click += (_, _) => Accept();
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
        => FormParts.SmallButton(text, onClick, minWidth: 116);

    /// <summary>
    /// Rebuilds the visible rows. Tick state lives on the CheckBox itself and the
    /// same instances are re-parented, so filtering never loses a selection.
    /// </summary>
    private void ApplyFilter()
    {
        string needle = (_filter.Text ?? "").Trim();
        _listHost.Children.Clear();

        foreach ((_, string name, CheckBox box) in _boxes)
        {
            if (needle.Length > 0 && !name.Contains(needle, StringComparison.OrdinalIgnoreCase))
                continue;

            _listHost.Children.Add(box);
        }

        if (_listHost.Children.Count == 0)
            _listHost.Children.Add(FormParts.EmptyChipNote($"No category matches \"{needle}\"."));
    }

    private void SetShown(bool state)
    {
        foreach (UIElement child in _listHost.Children)
        {
            if (child is CheckBox box)
                box.IsChecked = state;
        }

        RefreshCount();
    }

    private void RefreshCount()
    {
        int n = _boxes.Count(b => b.Box.IsChecked == true) + _unknown.Count;
        _count.Text = n == 0 ? "none ticked" : $"{n} selected";
    }

    private void Accept()
    {
        Result =
        [
            .. _boxes.Where(b => b.Box.IsChecked == true).Select(b => b.Bic.ToString()),
            .. _unknown,
        ];

        DialogResult = true;
    }
}
