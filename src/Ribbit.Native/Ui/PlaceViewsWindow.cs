using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Ribbit.Geometry.Sheets;
using Ribbit.Native.Revit;
using Ribbit.Native.Settings;

namespace Ribbit.Native.Ui;

/// <summary>One view in the left list. Sheet and Suggested change in place, so selection survives.</summary>
internal sealed class PvViewRow(int index, PlaceViewRow view) : INotifyPropertyChanged
{
    public int Index { get; } = index;
    public PlaceViewRow View { get; } = view;
    public string Name => View.Name;
    public string Type => View.Type;

    private string _sheet = "", _suggested = "";
    public string Sheet { get => _sheet; set { _sheet = value; Changed(nameof(Sheet)); } }
    public string Suggested { get => _suggested; set { _suggested = value; Changed(nameof(Suggested)); } }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed(string p) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
}

/// <summary>One sheet in the right list, rebuilt whenever the ranking or the search changes.</summary>
internal sealed record PvSheetRow(int Index, string Match, string Number, string Name, string Placing, string OnIt, string Tone);

/// <summary>
/// Place Views on Sheets (was the matching form in "Placing Views to Sheets v3.dyn").
///
/// Left, the views: a view is placed if, and only if, its Sheet column is filled
/// in - there is no separate tick to forget. Suggested is the name matcher's
/// guess, never applied until you accept it.
///
/// Right, the sheets: click ONE view and they re-order by how well each sheet's
/// number and name match it, best first, with the strength shown - the right
/// sheet is almost always in the top two or three rows. The best is yellow, the
/// one the view already has is green.
///
/// Assigning, fastest first: select views and press Accept suggestions
/// (Ctrl+Enter); double-click a sheet, or select it and press Enter; type in the
/// sheet search and press Enter for the top row. Each applies to every selected
/// view, and after a single view the selection moves on to the next view with no
/// sheet. Delete clears. Enter never places.
/// </summary>
internal sealed class PlaceViewsWindow : Window
{
    internal const string DialogTitle = "Place Views on Sheets";

    internal static readonly string[] ShowChoices = ["All views", "No sheet yet", "Has a sheet", "No sheet, but a suggestion"];

    internal readonly TextBox ViewSearch;
    internal readonly ComboBox TypeFilter;
    internal readonly ComboBox ShowFilter;
    internal readonly ListView ViewList;
    internal readonly TextBox SheetSearch;
    internal readonly ListView SheetList;
    internal readonly TextBlock SheetHint;
    internal readonly RadioButton AutoPlace;
    internal readonly RadioButton XyPlace;
    internal readonly TextBox XBox;
    internal readonly TextBox YBox;
    internal readonly TextBlock Summary;
    internal readonly TextBlock Problem;
    internal readonly Button PlaceButton;
    internal readonly Button AcceptButton;

    private readonly List<PlaceViewRow> _views;
    private readonly List<PlaceSheetRow> _sheets;
    private readonly ViewSheetMatcher _matcher;
    private readonly List<PvViewRow> _rows;
    private readonly Dictionary<int, int> _suggested = [];
    private readonly Dictionary<int, int> _assigned = [];
    private (string Key, bool Descending) _sort = ("Type", false);
    private bool _building = true;

    /// <summary>What Place does: (view id, sheet id) per assigned view, in list order.</summary>
    internal List<(long View, long Sheet)> Pairs => [.. _assigned.OrderBy(kv => kv.Key).Select(kv => (_views[kv.Key].Id, _sheets[kv.Value].Id))];

    internal Placement? Placement { get; private set; }

    internal IReadOnlyDictionary<int, int> Assigned => _assigned;
    internal IReadOnlyDictionary<int, int> Suggestions => _suggested;
    internal List<PvViewRow> ShownViews => [.. ViewList.Items.Cast<PvViewRow>()];
    internal List<PvSheetRow> ShownSheets => [.. SheetList.Items.Cast<PvSheetRow>()];

    public PlaceViewsWindow(List<PlaceViewRow> views, List<PlaceSheetRow> sheets, int alreadyPlaced, PlaceViewsSettings saved)
    {
        _views = views;
        _sheets = sheets;
        _matcher = new ViewSheetMatcher([.. sheets.Select(s => new SheetName(s.Number, s.Name))]);
        _rows = [.. views.Select((v, i) => new PvViewRow(i, v))];
        for (int i = 0; i < views.Count; i++)
        {
            if (_matcher.Suggestion(views[i].Name) is int s)
            {
                _suggested[i] = s;
                _rows[i].Suggested = sheets[s].Label;
            }
        }

        Title = DialogTitle;
        Size area = RevitScreen.WorkArea();
        Width = Math.Min(1400, area.Width - 40);
        Height = Math.Min(860, area.Height - 40);
        MinWidth = 1000;
        MinHeight = 560;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = false;
        new WindowInteropHelper(this).Owner = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;

        TextBlock head = FormParts.Hint(
            $"{views.Count} view(s) can still be placed ({alreadyPlaced} already on a sheet), {sheets.Count} sheet(s). "
            + "Pick views on the left, then a sheet on the right - click a view and the sheets re-order so the closest names are on top.");
        head.Margin = new Thickness(FormParts.Gutter, FormParts.Gutter, FormParts.Gutter, 6);
        head.FontSize = 12;

        // ---- left: views ----------------------------------------------------------------
        DockPanel left = new();
        StackPanel leftHead = new();
        leftHead.Children.Add(FormParts.Section("Views", first: true));
        Grid filters = new() { Margin = new Thickness(0, 0, 0, 6) };
        filters.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        filters.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        filters.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });
        (Grid searchHost, TextBox search) = FormParts.PlaceholderInput("Search views - type a few words");
        ViewSearch = search;
        ViewSearch.TextChanged += (_, _) => FillViews();
        filters.Children.Add(searchHost);
        TypeFilter = new ComboBox { Margin = new Thickness(6, 0, 0, 0), MinHeight = 26, ToolTip = "Only views of this type" };
        TypeFilter.Items.Add("All types");
        foreach (string t in views.Select(v => v.Type).Distinct().OrderBy(t => t))
            TypeFilter.Items.Add(t);
        TypeFilter.SelectedIndex = 0;
        TypeFilter.SelectionChanged += (_, _) => FillViews();
        Grid.SetColumn(TypeFilter, 1);
        filters.Children.Add(TypeFilter);
        ShowFilter = new ComboBox { Margin = new Thickness(6, 0, 0, 0), MinHeight = 26 };
        foreach (string s in ShowChoices)
            ShowFilter.Items.Add(s);
        ShowFilter.SelectedIndex = Math.Max(0, Math.Min(ShowChoices.Length - 1, saved.ShowFilter));
        ShowFilter.SelectionChanged += (_, _) => FillViews();
        Grid.SetColumn(ShowFilter, 2);
        filters.Children.Add(ShowFilter);
        leftHead.Children.Add(filters);
        DockPanel.SetDock(leftHead, Dock.Top);
        left.Children.Add(leftHead);

        WrapPanel viewButtons = new() { Margin = new Thickness(0, 6, 0, 0) };
        Button selectAll = SmallButton("Select all shown", "Select every view the list is showing (Ctrl+A).", () => ViewList.SelectAll());
        AcceptButton = SmallButton("Accept suggestions", "Give each selected view its suggested sheet (Ctrl+Enter). With nothing selected: every view on show.", AcceptSuggestions);
        Button clear = SmallButton("Clear", "Take the sheet off the selected views (Delete).", ClearSelected);
        viewButtons.Children.Add(selectAll);
        viewButtons.Children.Add(AcceptButton);
        viewButtons.Children.Add(clear);
        DockPanel.SetDock(viewButtons, Dock.Bottom);
        left.Children.Add(viewButtons);

        ViewList = MakeList(
            [("View", nameof(PvViewRow.Name), 300), ("Type", nameof(PvViewRow.Type), 110),
             ("Sheet", nameof(PvViewRow.Sheet), 230), ("Suggested", nameof(PvViewRow.Suggested), 230)], greenWhenSheet: true);
        ViewList.SelectionMode = SelectionMode.Extended;
        ViewList.SizeChanged += (_, _) => FormParts.FitColumns(ViewList, 36, 0, 32, 32);
        ViewList.SelectionChanged += (_, _) => FillSheets();
        ViewList.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Delete) { ClearSelected(); e.Handled = true; }
        };
        ViewList.AddHandler(GridViewColumnHeader.ClickEvent, new RoutedEventHandler(HeaderClicked));
        left.Children.Add(ViewList);

        // ---- right: sheets --------------------------------------------------------------
        DockPanel right = new();
        StackPanel rightHead = new();
        rightHead.Children.Add(FormParts.Section("Sheets", first: true));
        (Grid sheetSearchHost, TextBox sheetSearch) = FormParts.PlaceholderInput("Search sheets - Enter gives the top row to the selected views");
        SheetSearch = sheetSearch;
        SheetSearch.Margin = new Thickness(0);
        SheetSearch.TextChanged += (_, _) => FillSheets();
        SheetSearch.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                if (SheetList.Items.Count > 0)
                    Assign(((PvSheetRow)SheetList.Items[0]).Index);
                e.Handled = true;
            }
        };
        sheetSearchHost.Margin = new Thickness(0, 0, 0, 6);
        rightHead.Children.Add(sheetSearchHost);
        SheetHint = FormParts.Status();
        SheetHint.Margin = new Thickness(0, 0, 0, 6);
        rightHead.Children.Add(SheetHint);
        DockPanel.SetDock(rightHead, Dock.Top);
        right.Children.Add(rightHead);

        SheetList = MakeList(
            [("Match", nameof(PvSheetRow.Match), 70), ("Number", nameof(PvSheetRow.Number), 90), ("Name", nameof(PvSheetRow.Name), 240),
             ("Placing", nameof(PvSheetRow.Placing), 170), ("Already on the sheet", nameof(PvSheetRow.OnIt), 200)], greenWhenSheet: false);
        SheetList.SelectionMode = SelectionMode.Single;
        SheetList.SizeChanged += (_, _) => FormParts.FitColumns(SheetList, 0, 0, 40, 30, 30);
        SheetList.MouseDoubleClick += (_, e) =>
        {
            if (SheetList.SelectedItem is PvSheetRow r && e.OriginalSource is DependencyObject d && ItemsControl.ContainerFromElement(SheetList, d) is ListViewItem)
                Assign(r.Index);
        };
        SheetList.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && SheetList.SelectedItem is PvSheetRow r)
            {
                Assign(r.Index);
                e.Handled = true;
            }
        };
        right.Children.Add(SheetList);

        Grid middle = new() { Margin = new Thickness(FormParts.Gutter, 0, FormParts.Gutter, 0) };
        middle.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56, GridUnitType.Star) });
        middle.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        middle.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44, GridUnitType.Star) });
        middle.Children.Add(left);
        GridSplitter splitter = new() { Width = 8, HorizontalAlignment = HorizontalAlignment.Stretch, Background = Brushes.Transparent };
        Grid.SetColumn(splitter, 1);
        middle.Children.Add(splitter);
        Grid.SetColumn(right, 2);
        middle.Children.Add(right);

        // ---- placement and footer ----------------------------------------------------------
        StackPanel place = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(FormParts.Gutter, 10, FormParts.Gutter, 0) };
        place.Children.Add(new TextBlock { Text = "Viewport centre:", FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) });
        AutoPlace = new RadioButton { Content = "Centre of the title block", IsChecked = saved.Placement != "xy", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0), ToolTip = "The centre of the largest title block on each sheet" };
        XyPlace = new RadioButton { Content = "This point on the sheet:", IsChecked = saved.Placement == "xy", VerticalAlignment = VerticalAlignment.Center };
        place.Children.Add(AutoPlace);
        place.Children.Add(XyPlace);
        XBox = MmBox(saved.XMm);
        YBox = MmBox(saved.YMm);
        place.Children.Add(new TextBlock { Text = "X", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 4, 0) });
        place.Children.Add(XBox);
        place.Children.Add(new TextBlock { Text = "mm    Y", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 4, 0) });
        place.Children.Add(YBox);
        place.Children.Add(new TextBlock { Text = "mm", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0) });
        foreach (RadioButton r in new[] { AutoPlace, XyPlace })
            r.Checked += (_, _) => UpdateFooter();
        foreach (TextBox b in new[] { XBox, YBox })
            b.TextChanged += (_, _) => UpdateFooter();

        Summary = new TextBlock { TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold };
        Problem = FormParts.Problem();
        Problem.Margin = new Thickness(0, 4, 0, 0);
        StackPanel band = new();
        band.Children.Add(Summary);
        band.Children.Add(Problem);

        PlaceButton = FormParts.Primary("Place");
        PlaceButton.IsDefault = false;   // Enter assigns a sheet; it must never place
        PlaceButton.Click += (_, _) => Accept();

        StackPanel footer = new();
        footer.Children.Add(place);
        footer.Children.Add(new Border
        {
            Background = SystemColors.ControlLightBrush,
            BorderBrush = SystemColors.ControlDarkBrush,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(FormParts.Gutter, 10, FormParts.Gutter, 10),
            Margin = new Thickness(0, 10, 0, 0),
            Child = band,
        });
        footer.Children.Add(new Border
        {
            Padding = new Thickness(FormParts.Gutter, 0, FormParts.Gutter, FormParts.Gutter),
            Child = FormParts.ButtonBar(PlaceButton, FormParts.Secondary("Cancel", true)),
        });

        DockPanel root = new();
        DockPanel.SetDock(head, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(head);
        root.Children.Add(footer);
        root.Children.Add(middle);
        Content = root;

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
            {
                AcceptSuggestions();
                e.Handled = true;
            }
        };

        _building = false;
        FillViews();
        FillSheets();
        UpdateFooter();
    }

    // ------------------------------------------------------------------ parts

    private static Button SmallButton(string text, string tip, Action click)
    {
        Button b = new() { Content = text, ToolTip = tip, Padding = new Thickness(10, 2, 10, 2), MinHeight = 26, Margin = new Thickness(0, 0, 6, 0) };
        b.Click += (_, _) => click();
        return b;
    }

    private static TextBox MmBox(double value) => new()
    {
        Text = value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
        Width = 70,
        MinHeight = 26,
        Padding = new Thickness(6, 3, 6, 3),
        VerticalContentAlignment = VerticalAlignment.Center,
    };

    private static readonly Brush GreenBack = new SolidColorBrush(Color.FromRgb(0xDE, 0xF4, 0xDE));
    private static readonly Brush BestBack = new SolidColorBrush(Color.FromRgb(0xFF, 0xF6, 0xCC));

    private static ListView MakeList(IEnumerable<(string Header, string Path, double Width)> columns, bool greenWhenSheet)
    {
        GridView gv = new();
        foreach ((string header, string path, double width) in columns)
        {
            FrameworkElementFactory text = new(typeof(TextBlock));
            text.SetBinding(TextBlock.TextProperty, new Binding(path));
            text.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
            text.SetBinding(FrameworkElement.ToolTipProperty, new Binding(path));
            gv.Columns.Add(new GridViewColumn { Header = header, Width = width, CellTemplate = new DataTemplate { VisualTree = text } });
        }

        Style item = new(typeof(ListViewItem));
        item.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(2, 2, 2, 2)));
        if (greenWhenSheet)
        {
            // A view with a sheet is green: what will be placed is visible at a glance.
            DataTrigger empty = new() { Binding = new Binding(nameof(PvViewRow.Sheet)), Value = "" };
            empty.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
            item.Setters.Add(new Setter(Control.BackgroundProperty, GreenBack));
            item.Triggers.Add(empty);
        }
        else
        {
            foreach ((string tone, Brush brush) in new[] { ("best", BestBack), ("current", GreenBack) })
            {
                DataTrigger t = new() { Binding = new Binding(nameof(PvSheetRow.Tone)), Value = tone };
                t.Setters.Add(new Setter(Control.BackgroundProperty, brush));
                item.Triggers.Add(t);
            }
        }
        return new ListView { View = gv, ItemContainerStyle = item, MinHeight = 160 };
    }

    // ------------------------------------------------------------------ views

    private static bool Matches(string text, string search)
        => search.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).All(w => text.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0);

    internal void FillViews()
    {
        if (_building)
            return;
        HashSet<int> selected = [.. ViewList.SelectedItems.Cast<PvViewRow>().Select(r => r.Index)];
        string search = ViewSearch.Text ?? "";
        string type = TypeFilter.SelectedIndex > 0 ? (string)TypeFilter.SelectedItem : "";
        int show = Math.Max(0, ShowFilter.SelectedIndex);

        IEnumerable<PvViewRow> rows = _rows.Where(r =>
            (type.Length == 0 || r.Type == type)
            && Matches(r.Name + " " + r.Type + " " + r.Sheet, search)
            && show switch
            {
                1 => !_assigned.ContainsKey(r.Index),
                2 => _assigned.ContainsKey(r.Index),
                3 => !_assigned.ContainsKey(r.Index) && _suggested.ContainsKey(r.Index),
                _ => true,
            });

        Func<PvViewRow, string> key = _sort.Key switch
        {
            "View" => r => r.Name,
            "Sheet" => r => r.Sheet,
            "Suggested" => r => r.Suggested,
            _ => r => r.Type,
        };
        IComparer<string> cmp = NaturalSort.Comparer;
        List<PvViewRow> list = _sort.Descending
            ? [.. rows.OrderByDescending(key, cmp).ThenBy(r => r.Name, cmp)]
            : [.. rows.OrderBy(key, cmp).ThenBy(r => r.Name, cmp)];

        _building = true;
        ViewList.ItemsSource = list;
        foreach (PvViewRow r in list.Where(r => selected.Contains(r.Index)))
            ViewList.SelectedItems.Add(r);
        _building = false;
        FillSheets();
    }

    private void HeaderClicked(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not GridViewColumnHeader { Column.Header: string header })
            return;
        _sort = (header, _sort.Key == header && !_sort.Descending);
        FillViews();
    }

    internal List<int> SelectedViews() => [.. ViewList.SelectedItems.Cast<PvViewRow>().Select(r => r.Index).OrderBy(i => i)];

    /// <summary>Tests: select views by name (each must be on show).</summary>
    internal void SelectViews(params string[] names)
    {
        ViewList.SelectedItems.Clear();
        foreach (PvViewRow r in ViewList.Items.Cast<PvViewRow>().Where(r => names.Contains(r.Name)))
            ViewList.SelectedItems.Add(r);
    }

    // ------------------------------------------------------------------ sheets

    internal void FillSheets()
    {
        if (_building)
            return;
        List<int> sel = SelectedViews();
        string search = SheetSearch.Text ?? "";
        Dictionary<int, double> scores = [];
        List<int> order;
        int? current = null;

        if (sel.Count == 1)
        {
            List<(double Score, int Sheet)> ranked = _matcher.Ranked(_views[sel[0]].Name);
            foreach ((double s, int i) in ranked)
                scores[i] = s;
            order = [.. ranked.Select(r => r.Sheet), .. Enumerable.Range(0, _sheets.Count).Where(i => !scores.ContainsKey(i))];
            current = _assigned.TryGetValue(sel[0], out int c) ? c : null;
            SheetHint.Text = ranked.Count > 0
                ? $"Ranked for \"{_views[sel[0]].Name}\" - best first."
                : $"No sheet shares a word with \"{_views[sel[0]].Name}\" - search for it instead.";
        }
        else
        {
            order = [.. Enumerable.Range(0, _sheets.Count)];
            SheetHint.Text = sel.Count == 0 ? "Click a view to rank the sheets for it."
                : $"{sel.Count} views selected - the sheet you pick goes to all of them.";
        }

        Dictionary<int, List<string>> placing = [];
        foreach (KeyValuePair<int, int> kv in _assigned)
        {
            if (!placing.TryGetValue(kv.Value, out List<string>? l))
                placing[kv.Value] = l = [];
            l.Add(_views[kv.Key].Name);
        }

        int? best = order.Count > 0 && scores.TryGetValue(order[0], out double top) && top > 0 ? order[0] : null;
        List<PvSheetRow> rows = [];
        foreach (int i in order)
        {
            PlaceSheetRow s = _sheets[i];
            if (!Matches(s.Number + " " + s.Name, search))
                continue;
            string tone = i == current ? "current" : i == best ? "best" : "";
            rows.Add(new PvSheetRow(i, scores.TryGetValue(i, out double sc) ? ViewSheetMatcher.Stars(sc) : "",
                s.Number, s.Name, placing.TryGetValue(i, out List<string>? p) ? string.Join(";  ", p) : "", s.OnIt, tone));
        }
        SheetList.ItemsSource = rows;
        UpdateFooter();
    }

    // ------------------------------------------------------------------ assigning

    /// <summary>Give the sheet to every selected view; after a single view, move on to the next with no sheet.</summary>
    internal void Assign(int sheetIndex)
    {
        List<int> sel = SelectedViews();
        if (sel.Count == 0)
        {
            SetProblem("Select one or more views on the left first.");
            return;
        }
        foreach (int v in sel)
        {
            _assigned[v] = sheetIndex;
            _rows[v].Sheet = _sheets[sheetIndex].Label;
        }

        if (sel.Count == 1)
        {
            List<PvViewRow> shown = ShownViews;
            int at = shown.FindIndex(r => r.Index == sel[0]);
            PvViewRow? next = shown.Skip(at + 1).Concat(shown.Take(Math.Max(0, at))).FirstOrDefault(r => !_assigned.ContainsKey(r.Index));
            FillViews();
            if (next is not null && ShownViews.Contains(next))
            {
                ViewList.SelectedItems.Clear();
                ViewList.SelectedItem = next;
                ViewList.ScrollIntoView(next);
            }
        }
        else
        {
            FillViews();
        }
        SheetSearch.Text = "";
        FillSheets();
    }

    /// <summary>The suggestion for every selected view - or, with none selected, every view on show.</summary>
    internal void AcceptSuggestions()
    {
        List<int> targets = SelectedViews();
        if (targets.Count == 0)
            targets = [.. ShownViews.Select(r => r.Index)];
        int n = 0;
        foreach (int v in targets)
        {
            if (_suggested.TryGetValue(v, out int s))
            {
                _assigned[v] = s;
                _rows[v].Sheet = _sheets[s].Label;
                n++;
            }
        }
        SetProblem(n == 0 ? "None of those views has a suggestion - give them a sheet from the list on the right." : "");
        FillViews();
    }

    internal void ClearSelected()
    {
        foreach (int v in SelectedViews())
        {
            _assigned.Remove(v);
            _rows[v].Sheet = "";
        }
        FillViews();
    }

    // ------------------------------------------------------------------ footer and run

    internal Placement? ReadPlacement(out string? why)
    {
        why = null;
        System.Globalization.CultureInfo inv = System.Globalization.CultureInfo.InvariantCulture;
        bool auto = AutoPlace.IsChecked == true;
        bool okX = double.TryParse(XBox.Text.Trim(), System.Globalization.NumberStyles.Float, inv, out double x);
        bool okY = double.TryParse(YBox.Text.Trim(), System.Globalization.NumberStyles.Float, inv, out double y);
        if (!auto && (!okX || !okY || x < -10000 || x > 10000 || y < -10000 || y > 10000))
        {
            why = "X and Y must be numbers in millimetres (e.g. 532.5).";
            return null;
        }
        // Auto still needs a fallback point for a sheet with no title block.
        return new Placement(auto, okX ? x : 532.5, okY ? y : 405.0);
    }

    private void UpdateFooter()
    {
        if (_building || PlaceButton is null)
            return;
        Placement? p = ReadPlacement(out string? why);
        int views = _assigned.Count;
        int sheets = _assigned.Values.Distinct().Count();
        List<string> crowded = [.. _assigned.GroupBy(kv => kv.Value).Where(g => g.Count() > 1)
            .Select(g => $"{_sheets[g.Key].Number} ({g.Count()})")];
        int legends = _assigned.Keys.Count(k => _views[k].IsLegend);
        Summary.Text = views == 0
            ? "No view has a sheet yet - nothing will be placed."
            : $"{views} view(s) will be placed on {sheets} sheet(s)."
              + (legends > 0 ? $" {legends} legend(s) - a legend can go on many sheets." : "")
              + (crowded.Count > 0 ? $" More than one view lands on: {string.Join(", ", crowded.Take(6))}{(crowded.Count > 6 ? ", ..." : "")} - they will sit on top of each other at one point." : "");
        SetProblem(why ?? "");
        PlaceButton.Content = views == 0 ? "Place" : $"Place {views} view(s)";
        PlaceButton.IsEnabled = views > 0 && p is not null;
    }

    private void SetProblem(string text)
    {
        Problem.Text = text;
        Problem.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    internal PlaceViewsSettings SettingsToSave()
    {
        Placement? p = ReadPlacement(out _);
        return new PlaceViewsSettings
        {
            Placement = AutoPlace.IsChecked == true ? "auto" : "xy",
            XMm = p?.XMm ?? 532.5,
            YMm = p?.YMm ?? 405.0,
            ShowFilter = Math.Max(0, ShowFilter.SelectedIndex),
        };
    }

    private void Accept()
    {
        Placement? p = ReadPlacement(out string? why);
        if (p is null || _assigned.Count == 0)
        {
            SetProblem(why ?? "No view has a sheet yet.");
            return;
        }
        List<IGrouping<int, KeyValuePair<int, int>>> crowded = [.. _assigned.GroupBy(kv => kv.Value).Where(g => g.Count() > 1)];
        if (crowded.Count > 0)
        {
            string list = string.Join("\n", crowded.Take(12).Select(g => $"  {_sheets[g.Key].Label}:  {g.Count()} views"));
            if (Prompt.Show(this, $"These sheets get more than one view, all centred on the same point:\n\n{list}"
                                  + (crowded.Count > 12 ? "\n  ..." : "") + "\n\nPlace them anyway? You can move the viewports afterwards.",
                    DialogTitle, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;
        }
        Placement = p;
        DialogResult = true;
    }
}
