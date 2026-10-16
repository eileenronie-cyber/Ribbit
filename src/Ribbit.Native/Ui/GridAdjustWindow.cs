using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using Autodesk.Revit.DB;
using Ribbit.Geometry.Grids;
using Ribbit.Geometry.Sheets;
using Ribbit.Native.Revit;
using Ribbit.Native.Settings;
using Binding = System.Windows.Data.Binding;
using Color = System.Windows.Media.Color;
using Control = System.Windows.Controls.Control;
using Grid = System.Windows.Controls.Grid;
using Visibility = System.Windows.Visibility;
using ToggleButtonLike = System.Windows.Controls.Primitives.ToggleButton;
using View = Autodesk.Revit.DB.View;

namespace Ribbit.Native.Ui;

/// <summary>What the Adjust Grids window hands back.</summary>
internal sealed class GridAdjustChoice
{
    public List<string> ViewUids { get; init; } = [];

    /// <summary>Empty = every grid in the model (only ever when nothing limited it).</summary>
    public List<string> GridUids { get; init; } = [];

    public GridSteps Base { get; init; } = new();

    /// <summary>View id -> its scope box's set, or null when that option is off.</summary>
    public Dictionary<long, GridScopePlanEntry>? Plan { get; init; }

    public string CopySourceUid { get; init; } = "";
    public bool HideOthers { get; init; }
    public bool ShowSet { get; init; }
    public bool All2d { get; init; }
    public bool ToScope { get; init; }
    public string ChosenScopeUid { get; init; } = "";
}

/// <summary>
/// Adjust Grids, ported from the "Pick views and grids, then configure"
/// WinForms node of Grid_v2.dyn.
///
/// Left, every view that could hold a grid, searchable and sortable, with
/// its sheet number(s), crop state and scope box. Right, every grid, limited
/// by a scope box if one is chosen. Below, the four steps, the offsets laid
/// out round a crop-box diagram, the bubble sides, and "copy another view".
///
/// Tick state lives on the row objects, never on what is on screen, so
/// searching never loses a tick.
/// </summary>
internal sealed class GridAdjustWindow : Window
{
    internal static readonly Brush Green = new SolidColorBrush(Color.FromRgb(222, 244, 222));
    internal static readonly Brush Red = new SolidColorBrush(Color.FromRgb(190, 60, 40));

    private const string Caption = "Adjust grids to the crop box";

    private static readonly (string Caption, string Hint)[] SearchIn =
    [
        ("Search in: everything", "Search views, sheet numbers and scope boxes - e.g. Sheet 6  or  L1 power  or  A-101"),
        ("Search in: view name", "Search view names - e.g. L1 power"),
        ("Search in: sheet number", "Search sheet numbers - e.g. 6  or  E-101"),
        ("Search in: scope box", "Search scope boxes - e.g. Part 1"),
    ];

    private static readonly string[] ShowChoices =
        ["All views", "Crop box on", "Crop box off", "On a sheet", "Not on a sheet"];

    private readonly Document _doc;
    private readonly List<GridViewRow> _views;
    private readonly List<GridRow> _grids;
    private readonly List<ScopeBoxRow> _scopes;
    private readonly Dictionary<long, string> _scopeNames;
    private readonly Dictionary<string, GridPreset> _remembered;
    private readonly string _modelTitle;
    private readonly string? _username;

    private List<GridViewRow> _shownViews = [];
    private List<GridRow> _shownGrids = [];
    private (int Column, bool Descending) _viewSort = (1, false);
    private bool _bulk;
    private GridViewRow? _copySource;

    // views pane
    private readonly TextBox _viewSearch;
    private readonly TextBlock _viewSearchGhost;
    internal readonly ComboBox _searchIn = new();
    private readonly ComboBox _typeFilter = new();
    internal readonly ComboBox _showFilter = new();
    internal readonly ListView _viewList;
    internal readonly CheckBox _matchScope = new() { Content = "Each view uses its own scope box" };
    private readonly TextBlock _viewStatus = FormParts.Status();

    // grids pane
    private readonly TextBox _gridSearch;
    internal readonly ComboBox _scopeFilter = new();
    internal readonly ListView _gridList;
    // "In every ticked view, also" - these apply in BOTH modes, so they sit in a
    // box of their own rather than in the grids list or under STEP 1.
    internal readonly CheckBox _hideOthers = new() { Content = "Hide the grids that are not in the set" };
    internal readonly CheckBox _showSet = new() { Content = "Unhide the hidden grids of the set" };

    // On by default: the safety net - a grid left 3D in a view can be dragged
    // there and move in EVERY view.
    internal readonly CheckBox _all2d = new() { Content = "Convert every grid of the set to 2D (crop box or not)", IsChecked = true };
    private readonly TextBlock _gridStatus = FormParts.Status();

    // options
    internal readonly CheckBox _step1 = new() { Content = "STEP 1   Convert to 2D", IsChecked = true };
    internal readonly CheckBox _step2 = new() { Content = "STEP 2   Stretch to the crop box", IsChecked = true };
    internal readonly CheckBox _toScope = new() { Content = "to the scope box, where there is one", IsChecked = true };
    internal readonly CheckBox _step3 = new() { Content = "STEP 3   Move the bubble", IsChecked = true };
    internal readonly CheckBox _step4 = new() { Content = "STEP 4   Spread clashing bubbles" };
    internal readonly TextBox _gap = Box("3000");
    internal readonly TextBox _left = Box("3000"), _right = Box("3000"), _top = Box("3000"), _bottom = Box("3000");
    internal readonly RadioButton _bubbleLeft = new() { Content = "LEFT", GroupName = "h", IsChecked = true };
    internal readonly RadioButton _bubbleRight = new() { Content = "RIGHT", GroupName = "h" };
    internal readonly RadioButton _bubbleTop = new() { Content = "TOP", GroupName = "v", IsChecked = true };
    internal readonly RadioButton _bubbleBottom = new() { Content = "BOTTOM", GroupName = "v" };
    // What to do: one of two modes, and each shows only its own options - greyed
    // out, the other mode's boxes were still there to read past.
    internal readonly RadioButton _modeAdjust = new() { Content = "Adjust the grids", GroupName = "mode", IsChecked = true, FontWeight = FontWeights.SemiBold };
    internal readonly RadioButton _modeCopy = new() { Content = "Copy another view's grid set-up", GroupName = "mode", FontWeight = FontWeights.SemiBold, Margin = new Thickness(18, 0, 0, 0) };
    private readonly TextBlock _copyLabel = FormParts.Hint("No view picked yet");
    private readonly TextBlock _presetStatus = FormParts.Hint("");
    internal readonly TextBlock _problem = FormParts.Problem();
    internal readonly Button _run = FormParts.Primary("Adjust grids");
    private GroupBox _stepsBox = null!, _offsetsBox = null!, _bubblesBox = null!, _copyBox = null!;
    private bool _built;

    public GridAdjustChoice? Result { get; private set; }

    public GridAdjustWindow(Document doc)
    {
        _doc = doc;
        _scopes = GridAdjustCatalog.ScopeBoxes(doc);
        _scopeNames = _scopes.GroupBy(s => s.Id).ToDictionary(g => g.Key, g => g.First().Name);
        _views = GridAdjustCatalog.Views(doc, _scopeNames);
        _grids = GridAdjustCatalog.Grids(doc, _scopeNames);
        _modelTitle = SafeTitle(doc);
        _username = SafeUsername(doc);
        _remembered = GridScopeStore.Load(_modelTitle, _username);

        foreach (GridViewRow v in _views)
            v.CheckChanged = () => { if (!_bulk) RefreshViewStatus(); };
        foreach (GridRow g in _grids)
            g.CheckChanged = () => { if (!_bulk) RefreshGridStatus(); };

        Title = Caption;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        new WindowInteropHelper(this).Owner = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;
        FitToScreen();

        (Grid viewSearchHost, _viewSearch) = FormParts.PlaceholderInput(SearchIn[0].Hint);
        _viewSearchGhost = viewSearchHost.Children.OfType<TextBlock>().First();
        (Grid gridSearchHost, _gridSearch) = FormParts.PlaceholderInput("Search grids");

        _viewList = MakeList(
        [
            ("View", nameof(GridViewRow.Name), 176, null),
            ("Type", nameof(GridViewRow.Type), 88, null),
            ("Sheet", nameof(GridViewRow.SheetText), 66, null),
            ("Crop", nameof(GridViewRow.CropText), 44, nameof(GridViewRow.CropOn)),
            ("Scope box", nameof(GridViewRow.ScopeName), 90, null),
        ]);
        _gridList = MakeList(
        [
            ("Grid", nameof(GridRow.Name), 90, null),
            ("Scope box", nameof(GridRow.ScopeName), 170, nameof(GridRow.ScopeId)),
        ]);

        // Rows, not a DockPanel: docked, the lists got whatever the options
        // left over - five rows at the size the window really opens, and NONE
        // at its smallest (seen in the harness renders). The lists' row keeps
        // at least ~10 rows whatever the window size.
        Grid root = new() { Margin = new Thickness(12) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 360 });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });


        UIElement options = BuildOptions();
        Grid.SetRow(options, 2);
        root.Children.Add(options);

        UIElement footer = BuildFooter();
        Grid.SetRow(footer, 3);
        root.Children.Add(footer);

        Grid split = new();
        split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6, GridUnitType.Star), MinWidth = 300 });
        split.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4, GridUnitType.Star), MinWidth = 260 });
        UIElement viewsBox = BuildViewsBox(viewSearchHost);
        UIElement gridsBox = BuildGridsBox(gridSearchHost);
        GridSplitter splitter = new()
        {
            Width = 6,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Background = Brushes.Transparent,
        };
        Grid.SetColumn(viewsBox, 0);
        Grid.SetColumn(splitter, 1);
        Grid.SetColumn(gridsBox, 2);
        split.Children.Add(viewsBox);
        split.Children.Add(splitter);
        split.Children.Add(gridsBox);
        Grid.SetRow(split, 1);
        root.Children.Add(split);

        Content = root;

        _built = true;
        LoadSettings();
        RebuildViews();
        RebuildGrids();
        ToggleAll();
    }

    private static string SafeTitle(Document doc)
    {
        try { return doc.Title; }
        catch (Exception) { return "unknown model"; }
    }

    private static string? SafeUsername(Document doc)
    {
        try { return doc.Application.Username; }
        catch (Exception) { return null; }
    }

    /// <summary>
    /// As large as the screen REVIT is on allows - the two lists are where the
    /// work is. Not the primary monitor: on a smaller second screen that put
    /// the footer off the bottom.
    /// </summary>
    private void FitToScreen()
    {
        System.Windows.Size area = RevitScreen.WorkArea();
        double w = Math.Min(1500, area.Width - 40);
        double h = Math.Min(1300, area.Height - 16);
        Width = w;
        Height = h;
        MinWidth = Math.Min(980, w);
        MinHeight = Math.Min(720, h);
    }

    private static TextBox Box(string text) => new() { Text = text, Width = 70, Padding = new Thickness(4, 2, 4, 2) };

    // ------------------------------------------------------------ list plumbing

    /// <summary>
    /// A details list with a tick column. A ticked row is tinted green; a
    /// column with a "flag" path is drawn red where that value is false/null
    /// (crop box off, grid in no scope box).
    /// </summary>
    internal static ListView MakeList(IEnumerable<(string Header, string Path, double Width, string? Flag)> columns)
    {
        GridView gv = new();

        FrameworkElementFactory check = new(typeof(CheckBox));
        check.SetBinding(ToggleButton_IsChecked, new Binding(nameof(CheckRow.IsChecked)) { Mode = BindingMode.TwoWay });
        gv.Columns.Add(new GridViewColumn { Width = 30, CellTemplate = new DataTemplate { VisualTree = check } });

        foreach ((string header, string path, double width, string? flag) in columns)
        {
            FrameworkElementFactory text = new(typeof(TextBlock));
            text.Name = "t";
            text.SetBinding(TextBlock.TextProperty, new Binding(path));
            text.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
            DataTemplate template = new() { VisualTree = text };
            if (flag is not null)
            {
                foreach (object? bad in new object?[] { false, null })
                {
                    DataTrigger trigger = new() { Binding = new Binding(flag), Value = bad };
                    trigger.Setters.Add(new Setter(TextBlock.ForegroundProperty, Red, "t"));
                    template.Triggers.Add(trigger);
                }
            }

            gv.Columns.Add(new GridViewColumn
            {
                Header = new GridViewColumnHeader { Content = header, Tag = path },
                Width = width,
                CellTemplate = template,
            });
        }

        Style item = new(typeof(ListViewItem));
        item.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        // denser rows: the lists are scanned, and every pixel of padding was a
        // row the window could not show
        item.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(2, 0, 2, 0)));
        DataTrigger ticked = new() { Binding = new Binding(nameof(CheckRow.IsChecked)), Value = true };
        ticked.Setters.Add(new Setter(Control.BackgroundProperty, Green));
        item.Triggers.Add(ticked);

        ListView list = new()
        {
            View = gv,
            ItemContainerStyle = item,
            SelectionMode = SelectionMode.Extended,
            MinHeight = 160,
        };
        VirtualizingPanel.SetIsVirtualizing(list, true);

        // Space ticks every selected row, like a checkbox list should.
        list.KeyDown += (_, e) =>
        {
            if (e.Key != System.Windows.Input.Key.Space || list.SelectedItems.Count == 0)
                return;
            List<CheckRow> sel = [.. list.SelectedItems.OfType<CheckRow>()];
            bool state = !sel.All(r => r.IsChecked);
            foreach (CheckRow r in sel)
                r.IsChecked = state;
            e.Handled = true;
        };
        return list;
    }

    private static readonly DependencyProperty ToggleButton_IsChecked =
        System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty;

    private static Button SmallButton(string text, Action onClick)
    {
        Button b = new()
        {
            Content = text,
            MinHeight = 26,
            Padding = new Thickness(10, 2, 10, 2),
            Margin = new Thickness(0, 6, 6, 0),
        };
        b.Click += (_, _) => onClick();
        return b;
    }

    private static GroupBox Titled(string title, UIElement inner) => new()
    {
        Header = new TextBlock { Text = title, FontWeight = FontWeights.SemiBold },
        Content = inner,
        Padding = new Thickness(8, 6, 8, 8),
        Margin = new Thickness(0, 0, 10, 8),
    };

    private void BulkSet(IEnumerable<CheckRow> rows, bool state)
    {
        _bulk = true;
        try
        {
            foreach (CheckRow r in rows)
                r.IsChecked = state;
        }
        finally
        {
            _bulk = false;
        }
    }

    // ------------------------------------------------------------ views pane

    private UIElement BuildViewsBox(Grid searchHost)
    {
        foreach ((string c, _) in SearchIn)
            _searchIn.Items.Add(c);
        _searchIn.SelectedIndex = 0;
        _searchIn.SelectionChanged += (_, _) =>
        {
            _viewSearchGhost.Text = SearchIn[Math.Max(0, _searchIn.SelectedIndex)].Hint;
            RebuildViews();
        };

        _typeFilter.Items.Add("All view types");
        foreach (string t in _views.Select(v => v.Type).Distinct().OrderBy(t => t, StringComparer.OrdinalIgnoreCase))
            _typeFilter.Items.Add(t);
        _typeFilter.SelectedIndex = 0;
        _typeFilter.SelectionChanged += (_, _) => RebuildViews();

        foreach (string c in ShowChoices)
            _showFilter.Items.Add(c);
        _showFilter.SelectedIndex = 0;
        _showFilter.SelectionChanged += (_, _) => RebuildViews();

        _viewSearch.TextChanged += (_, _) => RebuildViews();

        _viewList.AddHandler(System.Windows.Controls.Primitives.ButtonBase.ClickEvent,
            new RoutedEventHandler((_, e) =>
            {
                if (e.OriginalSource is not GridViewColumnHeader { Tag: string } header)
                    return;
                int column = ((GridView)_viewList.View).Columns.IndexOf(header.Column);
                _viewSort = _viewSort.Column == column ? (column, !_viewSort.Descending) : (column, false);
                RebuildViews();
            }));

        _matchScope.Margin = new Thickness(10, 6, 0, 0);
        _matchScope.VerticalAlignment = VerticalAlignment.Top;
        _matchScope.ToolTip = "Every ticked view in a scope box gets that scope box's grids and its saved "
                              + "settings.\nOnly views with no scope box use the choice in the grids list.\n"
                              + "A scope box with no grids set up is skipped, never widened to every grid.";
        _matchScope.Checked += (_, _) => { RefreshViewStatus(); RefreshGridStatus(); };
        _matchScope.Unchecked += (_, _) => { RefreshViewStatus(); RefreshGridStatus(); };

        Grid filters = new() { Margin = new Thickness(0, 6, 0, 6) };
        for (int i = 0; i < 3; i++)
            filters.ColumnDefinitions.Add(new ColumnDefinition());
        foreach ((ComboBox box, int col) in new[] { (_searchIn, 0), (_typeFilter, 1), (_showFilter, 2) })
        {
            box.Margin = new Thickness(col == 0 ? 0 : 3, 0, col == 2 ? 0 : 3, 0);
            Grid.SetColumn(box, col);
            filters.Children.Add(box);
        }

        WrapPanel buttons = new();
        buttons.Children.Add(SmallButton("Tick shown", () => { BulkSet(_shownViews, true); RefreshViewStatus(); }));
        buttons.Children.Add(SmallButton("Untick all", () => { BulkSet(_shownViews, false); RefreshViewStatus(); }));
        buttons.Children.Add(SmallButton("Tick views in the chosen scope box", TickScopeViews));

        DockPanel panel = new();
        foreach (UIElement top in new UIElement[] { searchHost, filters })
        {
            DockPanel.SetDock(top, Dock.Top);
            panel.Children.Add(top);
        }

        // "Each view uses its own scope box" shares the status line: on the
        // button row it made the row wrap at the window's narrowest, and the
        // extra line came out of the list.
        DockPanel statusRow = new();
        DockPanel.SetDock(_matchScope, Dock.Right);
        statusRow.Children.Add(_matchScope);
        _viewStatus.Margin = new Thickness(2, 6, 0, 0);
        statusRow.Children.Add(_viewStatus);
        DockPanel.SetDock(statusRow, Dock.Bottom);
        panel.Children.Add(statusRow);
        DockPanel.SetDock(buttons, Dock.Bottom);
        panel.Children.Add(buttons);
        panel.Children.Add(_viewList);

        return Titled("1.  Views to work on  (click a heading to sort)", panel);
    }

    private string ViewText(GridViewRow r) => _searchIn.SelectedIndex switch
    {
        1 => r.Name,
        2 => r.Sheet,
        3 => r.ScopeId is null ? "" : r.ScopeName,
        _ => $"{r.Name} {r.Type} {r.Sheet} {r.ScopeName}",
    };

    private bool ViewVisible(GridViewRow r)
    {
        if (_typeFilter.SelectedIndex > 0 && r.Type != (string)_typeFilter.SelectedItem)
            return false;
        return _showFilter.SelectedIndex switch
        {
            1 => r.CropOn,
            2 => !r.CropOn,
            3 => r.Sheet.Length > 0,
            4 => r.Sheet.Length == 0,
            _ => true,
        };
    }

    private void RebuildViews()
    {
        List<GridViewRow> shown = GridSearch.Filter(_viewSearch.Text,
            _views.Where(ViewVisible).Select(r => (r, ViewText(r))));

        IComparer<string> nat = NaturalSort.Comparer;
        Comparison<GridViewRow> byName = (a, b) => nat.Compare(a.Name, b.Name);
        Comparison<GridViewRow> cmp = _viewSort.Column switch
        {
            2 => (a, b) => Chain(string.Compare(a.Type, b.Type, StringComparison.OrdinalIgnoreCase), byName(a, b)),
            // no sheet after the ones that have one
            3 => (a, b) => Chain((a.Sheet.Length == 0).CompareTo(b.Sheet.Length == 0),
                                 nat.Compare(a.Sheet, b.Sheet), byName(a, b)),
            // crop OFF first - that is what the column is worth clicking for
            4 => (a, b) => Chain(a.CropOn.CompareTo(b.CropOn), byName(a, b)),
            5 => (a, b) => Chain((a.ScopeId is null).CompareTo(b.ScopeId is null),
                                 nat.Compare(a.ScopeName, b.ScopeName), byName(a, b)),
            _ => byName,
        };
        shown.Sort(cmp);
        if (_viewSort.Descending)
            shown.Reverse();

        _shownViews = shown;
        _viewList.ItemsSource = shown;
        RefreshViewStatus();
    }

    private static int Chain(params int[] results) => results.FirstOrDefault(r => r != 0);

    private List<GridViewRow> CheckedViews => [.. _views.Where(v => v.IsChecked)];

    private void RefreshViewStatus()
    {
        if (_views.Count == 0)
        {
            _viewStatus.Text = "This model has no plan, elevation, section or 3D view.";
        }
        else
        {
            List<GridViewRow> ticked = CheckedViews;
            string text = $"{ticked.Count} of {_views.Count} view(s) ticked";
            if (_shownViews.Count != _views.Count)
                text += $"      showing {_shownViews.Count}";
            int off = ticked.Count(v => !v.CropOn);
            if (off > 0)
                text += $"      {off} of them have their crop box OFF and will be skipped";
            if (_matchScope.IsChecked == true && ticked.Count > 0)
                text += $"      {ticked.Count(v => v.ScopeId is not null)} use their own scope box";
            _viewStatus.Text = text;
        }

        RefreshFooter();
    }

    private void TickScopeViews()
    {
        long? limit = ChosenScopeId();
        if (limit is null)
        {
            Info("Pick a scope box in the grids dropdown first - the views whose own scope box is that "
                 + "one get ticked.");
            return;
        }

        List<GridViewRow> matching = [.. _views.Where(v => v.ScopeId == limit)];
        if (matching.Count == 0)
        {
            Info($"No view uses \"{_scopeFilter.SelectedItem}\" as its scope box.");
            return;
        }

        BulkSet(matching, true);
        RefreshViewStatus();
    }

    // ------------------------------------------------------------ grids pane

    private UIElement BuildGridsBox(Grid searchHost)
    {
        _scopeFilter.Items.Add("Any scope box");
        _scopeFilter.Items.Add("Not in a scope box");
        foreach (ScopeBoxRow s in _scopes)
            _scopeFilter.Items.Add(s.Name);
        _scopeFilter.SelectedIndex = 0;
        _scopeFilter.MinWidth = 180;
        _scopeFilter.Margin = new Thickness(6, 0, 0, 0);
        _scopeFilter.SelectionChanged += (_, _) =>
        {
            RebuildGrids();
            ApplyPreset();
        };

        _gridSearch.TextChanged += (_, _) => RebuildGrids();

        DockPanel top = new() { Margin = new Thickness(0, 0, 0, 6) };
        DockPanel.SetDock(_scopeFilter, Dock.Right);
        top.Children.Add(_scopeFilter);
        top.Children.Add(searchHost);

        WrapPanel buttons = new();
        buttons.Children.Add(SmallButton("Tick shown", () => { BulkSet(_shownGrids, true); RefreshGridStatus(); }));
        buttons.Children.Add(SmallButton("Untick all", () => { BulkSet(_shownGrids, false); RefreshGridStatus(); }));
        buttons.Children.Add(SmallButton("Set up this scope box...", SetUpScope));

        DockPanel panel = new();
        DockPanel.SetDock(top, Dock.Top);
        panel.Children.Add(top);
        DockPanel.SetDock(_gridStatus, Dock.Bottom);
        _gridStatus.Margin = new Thickness(2, 6, 0, 0);
        panel.Children.Add(_gridStatus);
        DockPanel.SetDock(buttons, Dock.Bottom);
        panel.Children.Add(buttons);
        panel.Children.Add(_gridList);

        return Titled("2.  Grids  (a scope box, ticks, or neither for all)", panel);
    }

    /// <summary>The scope box id chosen, or null for "any" / "not in a scope box".</summary>
    private long? ChosenScopeId()
    {
        int i = _scopeFilter.SelectedIndex - 2;
        return i >= 0 && i < _scopes.Count ? _scopes[i].Id : null;
    }

    private bool NoScopeChosen => _scopeFilter.SelectedIndex == 1;

    /// <summary>
    /// The grids of a scope box: a remembered set wins over the model's own
    /// Scope Box parameter - it is the more specific answer. An entry with
    /// settings but no grids is not a set.
    /// </summary>
    private HashSet<GridRow> ScopeGrids(long scope)
    {
        if (_remembered.TryGetValue(scope.ToString(), out GridPreset? entry) && entry.Grids.Count > 0)
        {
            HashSet<string> wanted = [.. entry.Grids];
            return [.. _grids.Where(g => wanted.Contains(g.Uid))];
        }

        return [.. _grids.Where(g => g.ScopeId == scope)];
    }

    private GridPreset? ScopeSettings(long scope)
        => _remembered.TryGetValue(scope.ToString(), out GridPreset? e) && e.HasSettings ? e : null;

    /// <summary>The grids the chosen scope box allows, or null for no limit.</summary>
    private HashSet<GridRow>? Allowed()
    {
        if (NoScopeChosen)
            return [.. _grids.Where(g => g.ScopeId is null)];
        return ChosenScopeId() is long s ? ScopeGrids(s) : null;
    }

    /// <summary>
    /// (grids this run may touch, is that a limit). Not a limit is the empty
    /// list, read downstream as every grid - so an empty LIMITED set is
    /// refused by Run, never sent.
    /// </summary>
    private (List<GridRow> Sending, bool Limited) EffectiveGrids()
    {
        HashSet<GridRow>? allowed = Allowed();
        List<GridRow> ticked = [.. _grids.Where(g => g.IsChecked)];
        if (ticked.Count > 0)
            return ([.. ticked.Where(g => allowed is null || allowed.Contains(g))], true);
        if (allowed is not null)
            return ([.. _grids.Where(allowed.Contains)], true);
        return ([], false);
    }

    private void RebuildGrids()
    {
        HashSet<GridRow>? allowed = Allowed();
        _shownGrids = GridSearch.Filter(_gridSearch.Text,
            _grids.Where(g => allowed is null || allowed.Contains(g)).Select(g => (g, $"{g.Name} {g.ScopeName}")));
        _gridList.ItemsSource = _shownGrids;
        RefreshGridStatus();
    }

    private string ScopeSource()
    {
        long? s = ChosenScopeId();
        return s is not null && _remembered.TryGetValue(s.Value.ToString(), out GridPreset? e) && e.Grids.Count > 0
            ? "a remembered set"
            : "the model's own Scope Box parameter";
    }

    private void RefreshGridStatus()
    {
        RefreshGridStatusText();
        RefreshFooter();    // what is sent decides whether Run can go
    }

    private void RefreshGridStatusText()
    {
        if (_grids.Count == 0)
        {
            _gridStatus.Text = "This model has no grids.";
            return;
        }

        if (Copying)
        {
            _gridStatus.Text = "Copying: every grid the source view shows is copied, and those grids are the set "
                               + "for hiding, unhiding and 2D  -  what is chosen here is not used";
            return;
        }

        if (_matchScope.IsChecked == true)
        {
            _gridStatus.Text = "Each view uses its own scope box  -  what is chosen here applies only to "
                               + "ticked views with no scope box";
            return;
        }

        (List<GridRow> sending, bool limited) = EffectiveGrids();
        if (!limited)
            _gridStatus.Text = $"Nothing chosen: all {_grids.Count} grid(s) are offered";
        else if (_grids.Any(g => g.IsChecked))
            _gridStatus.Text = $"{sending.Count} grid(s) ticked, and only those";
        else if (sending.Count == 0)
            _gridStatus.Text = $"No grid belongs to \"{_scopeFilter.SelectedItem}\" yet  -  use \"Set up grids "
                               + "for this scope box\"";
        else
            _gridStatus.Text = $"{sending.Count} grid(s) in \"{_scopeFilter.SelectedItem}\" from {ScopeSource()}"
                               + "  -  nothing outside it will be touched";
    }

    internal void SetUpScope()
    {
        long? limit = ChosenScopeId();
        if (limit is null)
        {
            Info("Pick a scope box in the dropdown first - that is the one the grids get remembered for.");
            return;
        }

        string name = (string)_scopeFilter.SelectedItem;
        GridSetDialog dialog = new(this, name, limit.Value, Allowed() ?? []);
        if (dialog.ShowDialog() != true)
            return;

        string key = limit.Value.ToString();
        if (!_remembered.TryGetValue(key, out GridPreset? entry))
            _remembered[key] = entry = new GridPreset();
        entry.Name = name;
        entry.Grids = [.. dialog.Chosen.OrderBy(g => g.Name, NaturalSort.Comparer).Select(g => g.Uid)];

        bool copiedSettings = false;
        if (dialog.TakenScope is long taken && ScopeSettings(taken) is GridPreset theirs)
        {
            if (Ask($"\"{(_scopeNames.TryGetValue(taken, out string? takenName) ? takenName : taken.ToString())}\" also has saved steps, offsets "
                    + $"and bubble sides. Copy those to \"{name}\" as well?"))
            {
                entry.TakeSettingsFrom(theirs);
                copiedSettings = true;
            }
        }

        if (!GridScopeStore.Save(_modelTitle, _username, _remembered))
            Warn($"The set is in use for this session, but it could not be written to\n{GridScopeStore.StorePath}");

        // ticks from the old set would silently narrow the new one
        BulkSet(_grids, false);
        RebuildGrids();
        if (copiedSettings)
            ApplyPreset();
    }

    // ------------------------------------------------------------ presets

    /// <summary>Put a scope box's remembered settings back - only what it holds.</summary>
    private void ApplyPreset()
    {
        long? limit = ChosenScopeId();
        if (limit is null || !_remembered.TryGetValue(limit.Value.ToString(), out GridPreset? p))
        {
            _presetStatus.Text = "";
            return;
        }

        List<string> restored = [];
        if (p.Steps is { Count: >= 3 } steps)
        {
            _step1.IsChecked = steps[0];
            _step2.IsChecked = steps[1];
            _step3.IsChecked = steps[2];
            if (steps.Count > 3)
                _step4.IsChecked = steps[3];
            restored.Add("steps");
        }

        if (p.GapMm is double gap && gap > 0)   // a saved 0 is not put back on screen
            _gap.Text = GridMath.PrettyMm(gap);

        if (p.Offsets is { Count: 4 } o)
        {
            _left.Text = GridMath.PrettyMm(o[0]);
            _right.Text = GridMath.PrettyMm(o[1]);
            _top.Text = GridMath.PrettyMm(o[2]);
            _bottom.Text = GridMath.PrettyMm(o[3]);
            restored.Add("offsets");
        }

        if (p.Bubbles is { Count: 2 } b)
        {
            (b[0] ? _bubbleLeft : _bubbleRight).IsChecked = true;
            (b[1] ? _bubbleTop : _bubbleBottom).IsChecked = true;
            restored.Add("bubble sides");
        }

        _presetStatus.Text = restored.Count > 0
            ? "Restored " + string.Join(", ", restored)
            : "Grids only - no settings saved yet";
    }

    private void SavePreset()
    {
        long? limit = ChosenScopeId();
        if (limit is null)
        {
            Info("Pick a scope box in the grids list first - the settings are kept against it.");
            return;
        }

        // Only when STEP 2 is on: with it off the offsets are greyed out and unused.
        List<string> bad = _step2.IsChecked == true ? BadOffsets() : [];
        if (bad.Count > 0)
        {
            Warn($"{string.Join(" and ", bad)} must be a number of millimetres before this can be saved.");
            return;
        }

        string name = (string)_scopeFilter.SelectedItem;
        string key = limit.Value.ToString();
        if (!_remembered.TryGetValue(key, out GridPreset? entry))
            _remembered[key] = entry = new GridPreset();
        entry.Name = name;
        entry.TakeSettingsFrom(ScreenSteps());

        if (GridScopeStore.Save(_modelTitle, _username, _remembered))
        {
            _presetStatus.Text = $"Saved for \"{name}\"";
        }
        else
        {
            _presetStatus.Text = "In use, but not written to disk";
            Warn($"The settings are in use for this session, but could not be written to\n{GridScopeStore.StorePath}");
        }
    }

    // ------------------------------------------------------------ options

    private UIElement BuildOptions()
    {
        WrapPanel row = new() { Margin = new Thickness(0, 4, 0, 0) };

        foreach (ToggleButtonLike c in new ToggleButtonLike[]
                 {
                     _step1, _step2, _step3, _step4, _modeAdjust, _modeCopy, _toScope, _all2d, _hideOthers, _showSet,
                     _bubbleLeft, _bubbleRight, _bubbleTop, _bubbleBottom,
                 })
        {
            c.Checked += (_, _) => ToggleAll();
            c.Unchecked += (_, _) => ToggleAll();
        }

        foreach (TextBox b in new[] { _left, _right, _top, _bottom, _gap })
            b.TextChanged += (_, _) => RefreshFooter();

        _toScope.Margin = new Thickness(20, 0, 0, 2);
        _toScope.ToolTip = "Stretch to the view's own scope box - or, for a view without one, the scope box "
                           + "chosen in the grids list -\ninstead of the crop box, so every view of that scope "
                           + "box gets identical grid lengths.\nA view with neither is stretched to its crop box.";

        // Compact on purpose: every line of explanation here is a line the two
        // lists above lose, and on a real screen they were down to five rows.
        // The explanations live in tooltips instead.
        _step1.ToolTip = "Convert both ends of each grid to its view-specific (2D) extent. Moves nothing.";
        _step2.ToolTip = "Stretch each grid to the crop box (or the scope box, below) plus the offsets.";
        _step3.ToolTip = "Show the bubble on the end chosen under Bubble ends, and hide the other.";
        _step4.ToolTip = "Two grids too close together put their bubbles on top of one another -\n"
                         + "this moves one aside onto an elbow until they are at least this far apart.";
        StackPanel steps = new();
        foreach (CheckBox c in new[] { _step1, _step2 })
        {
            c.Margin = new Thickness(0, 0, 0, 3);
            steps.Children.Add(c);
        }

        steps.Children.Add(_toScope);
        _step3.Margin = new Thickness(0, 3, 0, 3);
        steps.Children.Add(_step3);
        steps.Children.Add(_step4);
        StackPanel gapRow = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(20, 3, 0, 0) };
        gapRow.Children.Add(new TextBlock { Text = "at least ", VerticalAlignment = VerticalAlignment.Center });
        gapRow.Children.Add(_gap);
        gapRow.Children.Add(new TextBlock { Text = " mm apart", VerticalAlignment = VerticalAlignment.Center });
        steps.Children.Add(gapRow);
        row.Children.Add(_stepsBox = Titled("Steps", steps));

        Grid offsets = new();
        for (int i = 0; i < 3; i++)
        {
            offsets.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            offsets.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }

        void Place(UIElement e, int r, int c)
        {
            Grid.SetRow(e, r);
            Grid.SetColumn(e, c);
            offsets.Children.Add(e);
        }

        Place(Field("TOP", _top), 0, 1);
        Place(Field("LEFT", _left), 1, 0);
        Place(new Border
        {
            BorderBrush = SystemColors.GrayTextBrush,
            BorderThickness = new Thickness(1),
            Width = 70,
            Height = 46,
            Margin = new Thickness(6),
            Child = new TextBlock
            {
                Text = "crop\nbox",
                TextAlignment = TextAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                FontStyle = FontStyles.Italic,
                FontSize = 11,
                Foreground = SystemColors.GrayTextBrush,
            },
        }, 1, 1);
        Place(Field("RIGHT", _right), 1, 2);
        Place(Field("BOTTOM", _bottom), 2, 1);

        row.Children.Add(_offsetsBox = Titled("Offsets past the crop box (mm)", offsets));
        _offsetsBox.ToolTip = "Millimetres past the crop (or scope) box. 0 stops exactly at it;\n"
                              + "a negative number pulls that end inside it.";

        // two lines, each "which grids: which end"
        Grid bubbles = new();
        for (int i = 0; i < 3; i++)
            bubbles.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bubbles.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        bubbles.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        foreach ((UIElement e, int r, int c) in new (UIElement, int, int)[]
                 {
                     (new TextBlock { Text = "Horizontal grids:" }, 0, 0), (_bubbleLeft, 0, 1), (_bubbleRight, 0, 2),
                     (new TextBlock { Text = "Vertical grids:" }, 1, 0), (_bubbleTop, 1, 1), (_bubbleBottom, 1, 2),
                 })
        {
            if (e is FrameworkElement fe)
                fe.Margin = new Thickness(0, 4, 12, 4);
            Grid.SetRow(e, r);
            Grid.SetColumn(e, c);
            bubbles.Children.Add(e);
        }

        _bubblesBox = Titled("Bubble on the ... end", bubbles);

        // Copy mode's only box: where the set-up comes from.
        StackPanel copy = new() { MaxWidth = 330 };
        copy.Children.Add(FormParts.Hint("Every ticked view takes this view's 2D extents, bubble ends and elbows, "
                                         + "for every grid both views show. The grids list and scope boxes are "
                                         + "not used for a copy."));
        Button pick = SmallButton("Pick the view to copy from...", PickCopySource);
        pick.HorizontalAlignment = HorizontalAlignment.Left;
        pick.Margin = new Thickness(0, 0, 0, 4);
        copy.Children.Add(pick);
        copy.Children.Add(_copyLabel);
        row.Children.Add(_copyBox = Titled("Copy from", copy));

        // Both modes: what else happens in each ticked view.
        _all2d.ToolTip = "Convert every grid of the set each ticked view shows to 2D - not only those crossing "
                         + "the crop box -\nso none can be moved in 3D by dragging it there. Grids outside the set "
                         + "are left alone;\na view with no set counts every grid. A grid running past the crop "
                         + "box is converted at the crop edge, where it is drawn now.";
        _hideOthers.ToolTip = "Hide every grid Revit draws in the view that is not in its set -\nthe grids ticked, "
                              + "the scope box chosen, or with \"Each view uses its own scope box\" that view's "
                              + "own.\nA view with no set hides nothing. Uses Hide in View, so Reveal Hidden "
                              + "Elements brings a grid back.";
        _showSet.ToolTip = "Show again every grid of the view's set that is hidden there with Hide in View.\nA grid "
                           + "hidden by its category, a filter or a view template is not touched.";
        StackPanel also = new();
        foreach (CheckBox c in new[] { _all2d, _hideOthers, _showSet })
        {
            c.Margin = new Thickness(0, 2, 0, 4);
            also.Children.Add(c);
        }

        GroupBox alsoBox = Titled("In every ticked view, also", also);
        alsoBox.ToolTip = "\"The set\" is the grids ticked, the scope box chosen, or a view's own scope box -\n"
                          + "and when copying, the grids the source view shows.";

        // Bubble ends stacked over "also": three columns, not four, so at the
        // window's narrowest the boxes do not wrap onto a second line and push
        // the Run button off the bottom (seen in the harness render).
        StackPanel column = new();
        column.Children.Add(_bubblesBox);
        column.Children.Add(alsoBox);
        row.Children.Add(column);

        // What to do: one line above the boxes, the choice that decides which show.
        StackPanel modes = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 10, 0, 6) };
        modes.Children.Add(new TextBlock
        {
            Text = "3.  What to do:",
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 14, 0),
            VerticalAlignment = VerticalAlignment.Center,
        });
        modes.Children.Add(_modeAdjust);
        modes.Children.Add(_modeCopy);

        StackPanel area = new();
        area.Children.Add(modes);
        area.Children.Add(row);
        return area;
    }

    private static TextBlock Sub(string text)
    {
        TextBlock t = FormParts.Hint(text);
        t.Margin = new Thickness(20, 0, 0, 6);
        return t;
    }

    private static StackPanel Field(string caption, TextBox box)
    {
        StackPanel p = new() { Margin = new Thickness(6, 2, 6, 2) };
        p.Children.Add(new TextBlock { Text = caption, FontSize = 11, Foreground = SystemColors.GrayTextBrush });
        p.Children.Add(box);
        return p;
    }

    internal bool Copying => _modeCopy.IsChecked == true;

    /// <summary>For the tests: which mode's boxes are on screen.</summary>
    internal bool StepsShown => _stepsBox.Visibility == Visibility.Visible;

    internal bool CopyShown => _copyBox.Visibility == Visibility.Visible;

    /// <summary>
    /// Show only the chosen mode's boxes, and grey out what the ticked steps do
    /// not use.
    /// </summary>
    private void ToggleAll()
    {
        if (!_built)
            return;

        bool copying = Copying;
        Visibility adjust = copying ? Visibility.Collapsed : Visibility.Visible;
        _stepsBox.Visibility = _offsetsBox.Visibility = _bubblesBox.Visibility = adjust;
        _copyBox.Visibility = copying ? Visibility.Visible : Visibility.Collapsed;

        bool align = _step2.IsChecked == true;
        foreach (TextBox b in new[] { _left, _right, _top, _bottom })
            b.IsEnabled = align;
        _toScope.IsEnabled = align;
        foreach (RadioButton r in new[] { _bubbleLeft, _bubbleRight, _bubbleTop, _bubbleBottom })
            r.IsEnabled = _step3.IsChecked == true;
        _gap.IsEnabled = _step4.IsChecked == true;

        // a copy takes every grid the source shows: the per-view plan has no say
        _matchScope.IsEnabled = !copying;
        RefreshGridStatus();
        RefreshViewStatus();
    }

    internal void PickCopySource()
    {
        ViewPickDialog picker = new(this, _views,
            "Copy the grid set-up of which view?",
            "Pick the view whose grids are already right - their 2D extents, bubble ends and elbows are "
            + "copied to every ticked view, for the grids both views show.",
            "Copy from this view");
        if (picker.ShowDialog() != true || picker.Picked is null)
            return;

        UseCopySource(picker.Picked);
    }

    /// <summary>The view to copy from (the picker's answer; the tests set it directly).</summary>
    internal void UseCopySource(GridViewRow view)
    {
        _copySource = view;
        _copyLabel.Text = "From:  " + view.Name;
        _copyLabel.Foreground = SystemColors.ControlTextBrush;
        _copyLabel.FontWeight = FontWeights.SemiBold;
        _modeCopy.IsChecked = true;
        RefreshFooter();
    }

    // ------------------------------------------------------------ footer + run

    private UIElement BuildFooter()
    {
        Button save = FormParts.Secondary("Save these settings");
        save.ToolTip = "Keeps the steps, offsets and bubble sides against the scope box chosen in the grids "
                       + "list, so a part sheet is set up once and not again.";
        save.Click += (_, _) => SavePreset();
        _presetStatus.VerticalAlignment = VerticalAlignment.Center;
        _presetStatus.Margin = new Thickness(0);

        _run.MinWidth = 200;
        _run.Click += (_, _) => OnRun();

        DockPanel footer = new() { Margin = new Thickness(0, 8, 0, 0) };

        // Why Run is disabled, said where the eye already is - instead of a
        // message box after the click.
        _problem.Margin = new Thickness(0, 0, 0, 6);
        _problem.FontSize = 12;
        DockPanel.SetDock(_problem, Dock.Top);
        footer.Children.Add(_problem);

        StackPanel right = FormParts.ButtonBar(_run, FormParts.Secondary("Cancel", isCancel: true));
        right.Margin = new Thickness(0);
        DockPanel.SetDock(right, Dock.Right);
        footer.Children.Add(right);

        StackPanel left = new() { Orientation = Orientation.Horizontal };
        left.Children.Add(save);
        left.Children.Add(_presetStatus);
        footer.Children.Add(left);
        return footer;
    }

    /// <summary>
    /// The Run button's words, and whether it can be pressed at all: the first
    /// problem (the same checks a run makes) is shown in red above it, and a
    /// mistyped offset box is outlined.
    /// </summary>
    private void RefreshFooter()
    {
        if (!_built)
            return;

        int n = _views.Count(v => v.IsChecked);
        string verb = Copying ? "Copy to" : "Adjust";
        _run.Content = n > 0 ? $"{verb} {n} view(s)" : $"{verb} grids";

        string? problem = Prepare().Problem;
        _problem.Text = problem ?? "";
        _problem.Visibility = problem is null ? Visibility.Collapsed : Visibility.Visible;
        _run.IsEnabled = problem is null;

        bool checkOffsets = !Copying && _step2.IsChecked == true;
        foreach (TextBox b in new[] { _left, _right, _top, _bottom, _gap })
        {
            bool bad = GridMath.ParseMm(b.Text) is null
                       && (b == _gap ? !Copying && _step4.IsChecked == true : checkOffsets);
            b.BorderBrush = bad ? Red : SystemColors.ControlDarkBrush;
            b.BorderThickness = new Thickness(bad ? 2 : 1);
        }
    }

    private List<string> BadOffsets()
        => [.. new[] { ("LEFT", _left), ("RIGHT", _right), ("TOP", _top), ("BOTTOM", _bottom) }
            .Where(p => GridMath.ParseMm(p.Item2.Text) is null).Select(p => p.Item1)];

    private GridSteps ScreenSteps() => new()
    {
        Convert = _step1.IsChecked == true,
        Align = _step2.IsChecked == true,
        Bubbles = _step3.IsChecked == true,
        Spread = _step4.IsChecked == true,
        LeftMm = GridMath.ParseMm(_left.Text) ?? 0,
        RightMm = GridMath.ParseMm(_right.Text) ?? 0,
        TopMm = GridMath.ParseMm(_top.Text) ?? 0,
        BottomMm = GridMath.ParseMm(_bottom.Text) ?? 0,
        BubbleLeft = _bubbleLeft.IsChecked == true,
        BubbleTop = _bubbleTop.IsChecked == true,
        GapMm = GridMath.ParseMm(_gap.Text) ?? 0,
    };

    /// <summary>View id -> its scope box's set, for the ticked views in one; null when off.</summary>
    private Dictionary<long, GridScopePlanEntry>? ScopePlan()
    {
        if (_matchScope.IsChecked != true)
            return null;

        Dictionary<long, GridScopePlanEntry> plan = [];
        Dictionary<long, GridScopePlanEntry> cache = [];
        foreach (GridViewRow v in CheckedViews)
        {
            if (v.ScopeId is not long scope)
                continue;
            if (!cache.TryGetValue(scope, out GridScopePlanEntry? entry))
            {
                cache[scope] = entry = new GridScopePlanEntry(
                    v.ScopeName,
                    [.. ScopeGrids(scope).OrderBy(g => g.Name, NaturalSort.Comparer).Select(g => g.Uid)],
                    ScopeSettings(scope)?.Clone());
            }

            plan[v.Id] = entry;
        }

        return plan;
    }

    private static List<string> PlanSummary(Dictionary<long, GridScopePlanEntry> plan)
        => [.. plan.Values.GroupBy(e => e.ScopeName)
            .OrderBy(g => g.Key, NaturalSort.Comparer)
            .Select(g =>
            {
                GridScopePlanEntry e = g.First();
                return e.GridUids.Count == 0
                    ? $"  {g.Key}:  {g.Count()} view(s)  -  NO grids set up, will be skipped"
                    : $"  {g.Key}:  {g.Count()} view(s), {e.GridUids.Count} grid(s), "
                      + (e.Settings is not null ? "its saved settings" : "the settings on screen");
            })];

    /// <summary>The first reason a run would go wrong, or null.</summary>
    private string? SettingsProblem(Dictionary<long, GridScopePlanEntry> plan)
    {
        GridSteps screen = ScreenSteps();
        List<string> bad = BadOffsets();
        // a hide / unhide / 2D-only run needs no step
        bool hide = _hideOthers.IsChecked == true || _showSet.IsChecked == true || _all2d.IsChecked == true;
        List<(string Whose, GridSteps S, bool OffsetsFromScreen)> used = [];

        if (CheckedViews.Count > plan.Count)
            used.Add(("The settings on screen", screen, true));
        foreach (GridScopePlanEntry e in plan.Values.Where(e => e.GridUids.Count > 0)
                     .GroupBy(e => e.ScopeName).Select(g => g.First()))
        {
            used.Add((e.Settings is not null
                    ? $"\"{e.ScopeName}\"'s saved settings"
                    : $"The settings on screen (used by \"{e.ScopeName}\")",
                screen.With(e.Settings), e.Settings?.Offsets is null));
        }

        foreach ((string whose, GridSteps s, bool fromScreen) in used)
        {
            if (s.Problem(hide) is string problem)
                return $"{whose}: {problem}";
            // Caught here: a typo used to become a silent 0 offset.
            if (s.Align && fromScreen && bad.Count > 0)
                return $"{whose}: {string.Join(" and ", bad)} must be a number of millimetres.";
        }

        return null;
    }

    /// <summary>What else a crop-off view misses when steps are ticked.</summary>
    private string SkippedToo()
    {
        bool hide = _hideOthers.IsChecked == true, show = _showSet.IsChecked == true;
        return hide && show ? " - hiding and unhiding included"
            : hide ? " - hiding included"
            : show ? " - unhiding included"
            : "";
    }

    /// <summary>The confirmation's lines about 2D / hiding / unhiding.</summary>
    private string HideNote(Dictionary<long, GridScopePlanEntry> plan)
    {
        string lead = _all2d.IsChecked == true
            ? "\n\nEvery grid of the set in the ticked views ends up 2D (converted where it is drawn now - "
              + "nothing moves)."
            : "";
        return lead + VisibilityNote(plan);
    }

    private string VisibilityNote(Dictionary<long, GridScopePlanEntry> plan)
    {
        bool hide = _hideOthers.IsChecked == true, show = _showSet.IsChecked == true;
        if (!(hide || show))
            return "";
        (_, bool limited) = EffectiveGrids();
        int noSet = limited ? 0 : CheckedViews.Count - plan.Count;
        string note = "";
        if (Copying)
        {
            // copying: the reference view is the set, so every view has one
            noSet = 0;
            note += "\n\nEach view is made to show the grids the reference view shows (a view it shares no "
                    + "grid with is left as it is):";
        }

        if (show)
            note += "\n\nGrids of each view's set that are hidden in that view will be shown again.";
        if (hide)
            note += "\n\nGrids NOT in each view's set will be hidden in that view (Hide in View - Reveal "
                    + "Hidden Elements brings them back).";
        if (noSet > 0)
            note += $"\n{noSet} view(s) have no grid set chosen, so nothing is "
                    + (hide && show ? "hidden or shown" : hide ? "hidden" : "shown") + " in them.";
        return note;
    }

    /// <summary>What a run would do: the choice and its confirmation, or the reason it cannot run.</summary>
    internal sealed record Prepared(GridAdjustChoice? Choice, string? Problem, string Confirmation);

    /// <summary>
    /// Everything a run checks and would send, with NO dialogs - so the problem
    /// line can use it on every change, and the in-Revit tests can drive the
    /// window and see exactly what it would hand the engine.
    /// </summary>
    internal Prepared Prepare()
    {
        static Prepared Stop(string why) => new(null, why, "");

        List<GridViewRow> ticked = CheckedViews;
        if (ticked.Count == 0)
            return Stop("Tick at least one view on the left.");

        Dictionary<long, GridScopePlanEntry> plan = Copying ? [] : ScopePlan() ?? [];
        int unmatched = ticked.Count - plan.Count;
        (List<GridRow> sending, bool limited) = EffectiveGrids();

        string message;
        if (Copying)
        {
            if (_copySource is null)
                return Stop("Pick the view to copy from.");

            int targets = ticked.Count(v => v != _copySource);
            if (targets == 0)
                return Stop($"The only view ticked is \"{_copySource.Name}\", the one being copied from.");
            message = $"Copy the grid set-up of \"{_copySource.Name}\" - 2D extents, bubble ends and elbows - "
                      + $"to {targets} view(s)?";
            // the reference decides which grids - sets and scope boxes are not used for a copy
            message += "\n\nEvery grid the reference shows is copied - the grids ticked and the scope boxes "
                       + "are not used for a copy.";
            message += HideNote(plan);
            if (targets != ticked.Count)
                message += $"\n\n\"{_copySource.Name}\" itself is ticked and is left as it is.";
        }
        else
        {
            if (unmatched > 0 && limited && sending.Count == 0)
                return Stop("No grid is left to work on - what is ticked falls outside the scope box chosen. "
                            + "Clear one or the other.");

            if (SettingsProblem(plan) is string problem)
                return Stop(problem);

            GridSteps screen = ScreenSteps();
            List<string> steps = [];
            if (screen.Convert) steps.Add("convert to 2D");
            if (screen.Align) steps.Add("stretch to the crop box");
            if (screen.Bubbles) steps.Add("set the bubble end");
            if (screen.Spread) steps.Add("spread clashing bubbles");
            string stepText = steps.Count > 0 ? string.Join(", ", steps) : "hide only";
            string gridNote = limited ? $"{sending.Count} grid(s)" : "every grid in the model";

            if (plan.Count > 0)
            {
                message = $"Adjust {ticked.Count} view(s)?\n\nEach view in a scope box:\n"
                          + string.Join("\n", PlanSummary(plan));
                if (unmatched > 0)
                    message += $"\n\n{unmatched} view(s) with no scope box: {stepText}, touching {gridNote}.";
            }
            else if (steps.Count == 0)
            {
                List<string> doing = [];
                if (_all2d.IsChecked == true)
                    doing.Add("Convert the grids of the set to 2D");
                if (_showSet.IsChecked == true)
                    doing.Add(doing.Count > 0 ? "unhide the hidden grids of the set" : "Unhide the hidden grids of the set");
                if (_hideOthers.IsChecked == true)
                    doing.Add(doing.Count > 0 ? "hide the grids that are not in the set" : "Hide the grids that are not in the set");
                message = $"{string.Join(" and ", doing)} in {ticked.Count} view(s), keeping {gridNote}?";
            }
            else
            {
                message = $"Adjust {stepText} in {ticked.Count} view(s), touching {gridNote}?";
            }

            message += HideNote(plan);
            int off = ticked.Count(v => !v.CropOn);
            if (off > 0 && steps.Count > 0)
                message += $"\n\n{off} of those view(s) have their crop box switched off. There is no crop "
                           + "rectangle to work to, so they will be skipped" + SkippedToo() + ".";
        }

        long? chosen = ChosenScopeId();
        GridAdjustChoice choice = new()
        {
            ViewUids = [.. ticked.Select(v => v.Uid)],
            GridUids = [.. sending.Select(g => g.Uid)],
            Base = ScreenSteps(),
            // a copy takes every grid the reference shows - the plan has no say in it
            Plan = _matchScope.IsChecked == true && !Copying ? plan : null,
            CopySourceUid = Copying && _copySource is not null ? _copySource.Uid : "",
            HideOthers = _hideOthers.IsChecked == true,
            ShowSet = _showSet.IsChecked == true,
            All2d = _all2d.IsChecked == true,
            ToScope = _toScope.IsChecked == true,
            ChosenScopeUid = chosen is long c ? _scopes.First(s => s.Id == c).Uid : "",
        };
        return new Prepared(choice, null, message);
    }

    private void OnRun()
    {
        Prepared p = Prepare();
        if (p.Problem is not null || p.Choice is null)
        {
            Warn(p.Problem ?? "Nothing to run.");   // Run is disabled then; a keyboard Enter can still land here
            return;
        }

        if (!Ask(p.Confirmation))
            return;

        Result = p.Choice;
        SaveSettings();
        DialogResult = true;
    }

    // ------------------------------------------------------------ remembered settings

    private void LoadSettings()
    {
        GridAdjustSettings s = GridAdjustSettings.Load();
        (s.CopyMode ? _modeCopy : _modeAdjust).IsChecked = true;
        _step1.IsChecked = s.Convert;
        _all2d.IsChecked = s.All2d;
        _step2.IsChecked = s.Align;
        _toScope.IsChecked = s.ToScope;
        _step3.IsChecked = s.Bubbles;
        _step4.IsChecked = s.Spread;
        _left.Text = s.Left;
        _right.Text = s.Right;
        _top.Text = s.Top;
        _bottom.Text = s.Bottom;
        _gap.Text = s.Gap;
        (s.BubbleLeft ? _bubbleLeft : _bubbleRight).IsChecked = true;
        (s.BubbleTop ? _bubbleTop : _bubbleBottom).IsChecked = true;
        _matchScope.IsChecked = s.MatchScope;
        _hideOthers.IsChecked = s.HideOthers;
        _showSet.IsChecked = s.ShowSet;
        if (s.SearchIn >= 0 && s.SearchIn < _searchIn.Items.Count)
            _searchIn.SelectedIndex = s.SearchIn;
        if (s.ShowFilter >= 0 && s.ShowFilter < _showFilter.Items.Count)
            _showFilter.SelectedIndex = s.ShowFilter;
    }

    internal void SaveSettings() => new GridAdjustSettings
    {
        CopyMode = Copying,
        Convert = _step1.IsChecked == true,
        All2d = _all2d.IsChecked == true,
        Align = _step2.IsChecked == true,
        ToScope = _toScope.IsChecked == true,
        Bubbles = _step3.IsChecked == true,
        Spread = _step4.IsChecked == true,
        Left = _left.Text,
        Right = _right.Text,
        Top = _top.Text,
        Bottom = _bottom.Text,
        Gap = _gap.Text,
        BubbleLeft = _bubbleLeft.IsChecked == true,
        BubbleTop = _bubbleTop.IsChecked == true,
        MatchScope = _matchScope.IsChecked == true,
        HideOthers = _hideOthers.IsChecked == true,
        ShowSet = _showSet.IsChecked == true,
        SearchIn = _searchIn.SelectedIndex,
        ShowFilter = _showFilter.SelectedIndex,
    }.Save();

    // ------------------------------------------------------------ messages

    internal void Info(string text) => Prompt.Show(this, text, Caption, MessageBoxButton.OK, MessageBoxImage.Information);

    internal void Warn(string text) => Prompt.Show(this, text, Caption, MessageBoxButton.OK, MessageBoxImage.Warning);

    internal bool Ask(string text)
        => Prompt.Show(this, text, Caption, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    // ------------------------------------------------------------ helpers for the sub-dialogs

    internal Document Doc => _doc;
    internal IReadOnlyList<GridViewRow> AllViews => _views;
    internal IReadOnlyList<GridRow> AllGrids => _grids;
    internal IReadOnlyList<ScopeBoxRow> Scopes => _scopes;
    internal IReadOnlyDictionary<long, string> ScopeNames => _scopeNames;
    internal HashSet<GridRow> GridsOfScope(long scope) => ScopeGrids(scope);
}

/// <summary>Pick one view.</summary>
internal sealed class ViewPickDialog : Window
{
    private readonly IReadOnlyList<GridViewRow> _views;
    private readonly TextBox _search;
    private readonly ListBox _list = new() { MinHeight = 300 };

    public GridViewRow? Picked { get; private set; }

    /// <summary>For the in-Revit tests, which type in it and pick from it.</summary>
    internal TextBox Search => _search;

    internal ListBox List => _list;

    public ViewPickDialog(Window owner, IReadOnlyList<GridViewRow> views, string title, string head, string useText)
    {
        _views = views;
        Owner = owner;
        Title = title;
        Width = 760;
        Height = 620;
        MinWidth = 520;
        MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        (Grid host, _search) = FormParts.PlaceholderInput("Search views and sheet numbers");
        _search.TextChanged += (_, _) => Rebuild();
        _list.MouseDoubleClick += (_, _) => Use();

        Button use = FormParts.Primary(useText);
        use.Click += (_, _) => Use();

        DockPanel root = new() { Margin = new Thickness(FormParts.Gutter) };
        TextBlock h = FormParts.Hint(head);
        DockPanel.SetDock(h, Dock.Top);
        root.Children.Add(h);
        host.Margin = new Thickness(0, 0, 0, 6);
        DockPanel.SetDock(host, Dock.Top);
        root.Children.Add(host);
        StackPanel bar = FormParts.ButtonBar(use, FormParts.Secondary("Cancel", isCancel: true));
        DockPanel.SetDock(bar, Dock.Bottom);
        root.Children.Add(bar);
        root.Children.Add(_list);
        Content = root;
        Rebuild();
    }

    private void Rebuild()
    {
        _list.ItemsSource = GridSearch.Filter(_search.Text,
                _views.Select(v => (v, $"{v.Name} {v.Type} {v.Sheet}")))
            .Select(v => new ListBoxItem
            {
                Tag = v,
                Content = $"{v.Name}      [{v.Type}]   sheet {v.SheetText}   crop {v.CropText}",
                Foreground = v.CropOn ? SystemColors.ControlTextBrush : GridAdjustWindow.Red,
            })
            .ToList();
    }

    private void Use()
    {
        if (_list.SelectedItem is not ListBoxItem { Tag: GridViewRow v })
        {
            Prompt.Show(this, "Pick a view in the list first.", Title);
            return;
        }

        Picked = v;
        DialogResult = true;
    }
}

/// <summary>
/// Tick the grids that belong to one scope box. Shows EVERY grid, not the
/// ones it already has - the point is to define that set.
/// </summary>
internal sealed class GridSetDialog : Window
{
    private readonly GridAdjustWindow _owner;
    private readonly long _scope;
    private readonly List<Pick> _rows;
    private readonly TextBox _search;
    // The same list the main window builds: tick column, green ticked rows,
    // Space to tick the selection, red where a grid has no scope box.
    private readonly ListView _list = GridAdjustWindow.MakeList(
    [
        ("Grid", nameof(Pick.Name), 120, null),
        ("Scope box in the model", nameof(Pick.ScopeName), 260, nameof(Pick.ScopeId)),
    ]);
    private readonly TextBlock _status = FormParts.Status();
    private string? _takenFrom;
    private bool _bulk;

    public HashSet<GridRow> Chosen => [.. _rows.Where(r => r.IsChecked).Select(r => r.Row)];

    /// <summary>The scope box the set was copied from, if it was.</summary>
    public long? TakenScope { get; private set; }

    /// <summary>For the in-Revit tests: every row with its tick, the rows on screen, the search, the status line.</summary>
    internal IReadOnlyList<(GridRow Row, CheckRow Tick)> Items => [.. _rows.Select(p => (p.Row, (CheckRow)p))];

    internal int ShownCount => _shown.Count;

    internal TextBox Search => _search;

    internal string StatusText => _status.Text;

    private sealed class Pick : CheckRow
    {
        public GridRow Row { get; init; } = null!;
        public string Name => Row.Name;
        public string ScopeName => Row.ScopeName;
        public long? ScopeId => Row.ScopeId;
    }

    public GridSetDialog(GridAdjustWindow owner, string caption, long scope, IEnumerable<GridRow> preticked)
    {
        _owner = owner;
        _scope = scope;
        HashSet<GridRow> pre = [.. preticked];
        _rows = [.. owner.AllGrids.Select(g => new Pick { Row = g, IsChecked = pre.Contains(g) })];
        foreach (Pick p in _rows)
            p.CheckChanged = () => { if (!_bulk) RefreshStatus(); };

        Owner = owner;
        Title = $"Grids for {caption}";
        Width = 540;
        Height = 660;
        MinWidth = 430;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        (Grid host, _search) = FormParts.PlaceholderInput("Search grids");
        _search.TextChanged += (_, _) => Rebuild();

        _list.MinHeight = 300;

        WrapPanel buttons = new() { Margin = new Thickness(0, 6, 0, 0) };
        foreach ((string text, Action a) in new (string, Action)[]
                 {
                     ("Tick shown", () => SetShown(true)),
                     ("Untick all", () => SetShown(false)),
                     ("Take grids from a view...", FromView),
                     ("Take from a scope box...", FromScope),
                 })
        {
            Button b = new() { Content = text, MinHeight = 26, Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(0, 0, 6, 6) };
            b.Click += (_, _) => a();
            buttons.Children.Add(b);
        }

        Button save = FormParts.Primary("Remember these grids");
        save.Click += (_, _) => Save();

        DockPanel root = new() { Margin = new Thickness(FormParts.Gutter) };
        TextBlock head = FormParts.Hint("Tick every grid that belongs to this scope box. It is remembered for "
                                        + "next time, and nothing is written to the model.");
        DockPanel.SetDock(head, Dock.Top);
        root.Children.Add(head);
        host.Margin = new Thickness(0, 0, 0, 6);
        DockPanel.SetDock(host, Dock.Top);
        root.Children.Add(host);
        StackPanel bar = FormParts.ButtonBar(save, FormParts.Secondary("Cancel", isCancel: true));
        DockPanel.SetDock(bar, Dock.Bottom);
        root.Children.Add(bar);
        DockPanel.SetDock(_status, Dock.Bottom);
        root.Children.Add(_status);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        root.Children.Add(_list);
        Content = root;
        Rebuild();
    }

    private List<Pick> _shown = [];

    private void Rebuild()
    {
        _shown = GridSearch.Filter(_search.Text, _rows.Select(r => (r, $"{r.Name} {r.ScopeName}")));
        _list.ItemsSource = _shown;
        RefreshStatus();
    }

    private void RefreshStatus()
    {
        string text = $"{_rows.Count(r => r.IsChecked)} of {_rows.Count} grid(s) ticked";
        if (_shown.Count != _rows.Count)
            text += $"      showing {_shown.Count}";
        if (_takenFrom is not null)
            text += $"      taken from \"{_takenFrom}\" - check it before saving";
        _status.Text = text;
    }

    private void SetShown(bool state) => Bulk(_shown, _ => state);

    private void SetOnly(HashSet<GridRow> rows) => Bulk(_rows, p => rows.Contains(p.Row));

    /// <summary>Tick many rows with ONE status refresh, not one per row.</summary>
    private void Bulk(IEnumerable<Pick> picks, Func<Pick, bool> state)
    {
        _bulk = true;
        try
        {
            foreach (Pick p in picks)
                p.IsChecked = state(p);
        }
        finally
        {
            _bulk = false;
        }

        RefreshStatus();
    }

    /// <summary>Take the set from a view that is already right - it only ticks.</summary>
    private void FromView()
    {
        ViewPickDialog picker = new(this, _owner.AllViews,
            "Take the grids from which view?",
            "Pick a view that IS set up correctly. Whatever Revit draws in it becomes the set, replacing what "
            + "is ticked - so a view with grids bleeding in from the next part would bring those along. You get "
            + "to check the result before it is saved.",
            "Use this view's grids");
        if (picker.ShowDialog() != true || picker.Picked is null)
            return;

        if (_owner.Doc.GetElement(picker.Picked.Uid) is not View view)
        {
            Prompt.Show(this, "That view is no longer in the model.", Title);
            return;
        }

        HashSet<string> drawn;
        try
        {
            drawn = GridAdjustCatalog.GridsDrawnIn(_owner.Doc, view);
        }
        catch (Exception ex)
        {
            Prompt.Show(this, $"Revit would not say which grids that view shows:\n{ex.Message}", Title);
            return;
        }

        if (drawn.Count == 0)
        {
            Prompt.Show(this, $"Revit draws no grids in \"{picker.Picked.Name}\", so there is nothing to take. "
                                  + "If its crop box is off, or grids are switched off in its Visibility/Graphics, "
                                  + "that would explain it.", Title);
            return;
        }

        SetOnly([.. _owner.AllGrids.Where(g => drawn.Contains(g.Uid))]);
        _takenFrom = picker.Picked.Name;
        TakenScope = null;  // no longer from a scope box, so do not offer its settings
        RefreshStatus();
    }

    /// <summary>Take another scope box's set - two parts that share grids are set up once.</summary>
    private void FromScope()
    {
        List<(ScopeBoxRow Scope, int Count)> choices =
        [
            .. _owner.Scopes.Where(s => s.Id != _scope)
                .Select(s => (s, _owner.GridsOfScope(s.Id).Count))
                .Where(c => c.Count > 0),
        ];
        if (choices.Count == 0)
        {
            Prompt.Show(this, "No other scope box has grids to copy yet.", Title);
            return;
        }

        // Owned by THIS dialog, so it cannot open behind it.
        ComboBox combo = new() { Margin = new Thickness(0, 0, 0, 4) };
        foreach ((ScopeBoxRow s, int count) in choices)
            combo.Items.Add($"{s.Name}      {count} grid(s)");
        combo.SelectedIndex = 0;
        Window pick = new()
        {
            Owner = this,
            Title = "Copy the grids of which scope box?",
            Width = 460,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
        };
        Button use = FormParts.Primary("Copy its grids");
        use.Click += (_, _) => pick.DialogResult = true;
        StackPanel body = new() { Margin = new Thickness(FormParts.Gutter) };
        body.Children.Add(FormParts.Hint("Its grids replace what is ticked. You can still change them before "
                                         + "they are remembered."));
        body.Children.Add(combo);
        body.Children.Add(FormParts.ButtonBar(use, FormParts.Secondary("Cancel", isCancel: true)));
        pick.Content = body;
        if (pick.ShowDialog() != true || combo.SelectedIndex < 0)
            return;

        ScopeBoxRow scope = choices[combo.SelectedIndex].Scope;
        SetOnly(_owner.GridsOfScope(scope.Id));
        TakenScope = scope.Id;
        _takenFrom = $"{scope.Name} (scope box)";
        RefreshStatus();
    }

    private void Save()
    {
        if (!_rows.Any(r => r.IsChecked)
            && Prompt.Show(this, "Nothing is ticked. Save an empty set, forgetting what this scope box had?",
                Title, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        DialogResult = true;
    }
}
