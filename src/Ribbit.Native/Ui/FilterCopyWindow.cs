using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using Ribbit.Geometry.Views;
using Ribbit.Native.Revit;
using Ribbit.Native.Settings;

namespace Ribbit.Native.Ui;

/// <summary>Where the filters come from.</summary>
internal sealed record FilterSourceChoice(int OpenModel, string? Path)
{
    public static FilterSourceChoice ThisModel { get; } = new(-1, null);
    public bool IsThisModel => OpenModel < 0 && Path is null;
}

/// <summary>What the window needs from Revit - a seam, so the window can be driven and rendered in a test.</summary>
internal interface IFilterCopyHost
{
    string ThisModel { get; }

    /// <summary>Titles of the other models open in this Revit session.</summary>
    IReadOnlyList<string> OpenModels { get; }

    List<TemplateEntry> TargetTemplates { get; }

    /// <summary>The source's templates and a status line ("opened detached", ...). Throws with a readable message.</summary>
    (List<TemplateEntry> Templates, string Status) LoadSource(FilterSourceChoice choice);

    List<FilterOverrideRow> ReadFilters(long templateId);

    /// <summary>Filter name -> what it is in THIS model ("not in this model", "in this model, DIFFERENT rules", ...).</summary>
    Dictionary<string, string> TargetStatus(IReadOnlyList<FilterOverrideRow> rows);

    string? BrowseFile(string current);
}

internal sealed class FcCheckRow<T>(T item, string text) : INotifyPropertyChanged
{
    public T Item { get; } = item;
    public string Text { get; } = text;
    private bool _checked;
    public bool IsChecked { get => _checked; set { if (_checked != value) { _checked = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked))); } } }
    public string Carries { get; init; } = "";
    public string Here { get; set; } = "";
    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// Copy View Filters (was "Filter v2.dyn" and "Filter_Revit_to_Revit_v2.dyn" - one
/// window for both): pick the source (this model, another open model, or a file
/// on disk, opened detached), the template to copy FROM, the filters, and every
/// template to copy TO - several at once, where the graphs did one per run.
///
/// Each filter shows what it carries and what it is in this model before
/// anything is written, so "this filter exists here with different rules" is
/// known up front rather than read off a log afterwards. Ticks live in the row
/// objects, so searching never drops one.
/// </summary>
internal sealed class FilterCopyWindow : Window
{
    internal const string DialogTitle = "Copy View Filters";

    internal readonly ComboBox SourceBox;
    internal readonly TextBox PathBox;
    internal readonly FrameworkElement PathRow;
    internal readonly TextBlock SourceStatus;
    internal readonly TextBox SourceSearch;
    internal readonly ListBox SourceList;
    internal readonly ListView FilterList;
    internal readonly TextBox TargetSearch;
    internal readonly ListBox TargetList;
    internal readonly CheckBox Lines;
    internal readonly CheckBox Patterns;
    internal readonly CheckBox Transparency;
    internal readonly CheckBox BringMissing;
    internal readonly CheckBox SyncRules;
    internal readonly TextBlock SyncWarning;
    internal readonly TextBlock Summary;
    internal readonly TextBlock Problem;
    internal readonly Button CopyButton;

    private readonly IFilterCopyHost _host;
    private List<TemplateEntry> _sourceTemplates = [];
    private readonly List<FcCheckRow<TemplateEntry>> _targets;
    private List<FcCheckRow<FilterOverrideRow>> _filters = [];
    private bool _building = true;

    internal TemplateEntry? SourceTemplate => SourceList.SelectedItem as TemplateEntry;
    internal FilterSourceChoice Choice { get; private set; } = FilterSourceChoice.ThisModel;
    internal bool SourceLoaded { get; private set; }
    internal List<long> TargetIds => [.. _targets.Where(t => t.IsChecked && Visible(t)).Select(t => t.Item.Id)];
    internal List<FilterOverrideRow> ChosenFilters => [.. _filters.Where(f => f.IsChecked).Select(f => f.Item)];
    internal List<FcCheckRow<FilterOverrideRow>> FilterRows => _filters;
    internal List<FcCheckRow<TemplateEntry>> TargetRows => _targets;
    internal FilterCopyOptions Options => new(Lines.IsChecked == true, Patterns.IsChecked == true, Transparency.IsChecked == true,
        BringMissing.IsChecked == true, SyncRules.IsChecked == true);

    public FilterCopyWindow(IFilterCopyHost host, FilterCopySettings saved)
    {
        _host = host;
        _targets = [.. host.TargetTemplates.Select(t => new FcCheckRow<TemplateEntry>(t, t.Label))];

        Title = DialogTitle;
        Size area = RevitScreen.WorkArea();
        Width = Math.Min(1280, area.Width - 40);
        Height = Math.Min(800, area.Height - 40);
        MinWidth = 1000;
        MinHeight = 560;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = false;
        new WindowInteropHelper(this).Owner = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;

        // ---- 1 source ------------------------------------------------------------------
        StackPanel top = new() { Margin = new Thickness(FormParts.Gutter, FormParts.Gutter, FormParts.Gutter, 0) };
        top.Children.Add(FormParts.Section("1  Copy filters from", first: true));
        Grid srcRow = new();
        srcRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(360) });
        srcRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        SourceBox = new ComboBox { MinHeight = 26 };
        SourceBox.Items.Add($"This model - {host.ThisModel}");
        foreach (string m in host.OpenModels)
            SourceBox.Items.Add($"Open model - {m}");
        SourceBox.Items.Add("A model file on disk (.rvt)...");
        srcRow.Children.Add(SourceBox);

        (Grid pathHost, TextBox pathBox) = FormParts.PlaceholderInput("Path to the source .rvt - a workshared file is opened detached", saved.SourcePath);
        PathBox = pathBox;
        Button browse = new() { Content = "Browse..." };
        browse.Click += (_, _) =>
        {
            if (_host.BrowseFile(PathBox.Text) is string p)
            {
                PathBox.Text = p;
                LoadSource();
            }
        };
        Button load = new() { Content = "Open" };
        load.Click += (_, _) => LoadSource();
        DockPanel pathRow = FormParts.BrowseRow(pathHost, browse);
        load.MinWidth = 70;
        load.MinHeight = 26;
        load.Margin = new Thickness(6, 0, 0, 0);
        DockPanel.SetDock(load, Dock.Right);
        pathRow.Children.Insert(0, load);
        pathRow.Margin = new Thickness(10, 0, 0, 0);
        PathRow = pathRow;
        Grid.SetColumn(pathRow, 1);
        srcRow.Children.Add(pathRow);
        top.Children.Add(srcRow);
        SourceStatus = FormParts.Status();
        SourceStatus.Margin = new Thickness(0, 6, 0, 0);
        top.Children.Add(SourceStatus);

        // ---- 2 source template / 3 filters / 4 targets ----------------------------------------
        (DockPanel srcPane, SourceSearch, SourceList) = SearchPane("2  Template to copy FROM", "Search templates");
        SourceList.DisplayMemberPath = nameof(TemplateEntry.Label);
        SourceList.SelectionChanged += (_, _) => SourceTemplateChanged();
        SourceSearch.TextChanged += (_, _) => FillSourceList();

        DockPanel filterPane = new();
        StackPanel fHead = new();
        fHead.Children.Add(FormParts.Section("3  Filters", first: true));
        fHead.Children.Add(FormParts.Hint("Every filter on the template - also ones only switched off or with no override. Untick what you do not want."));
        DockPanel.SetDock(fHead, Dock.Top);
        filterPane.Children.Add(fHead);
        StackPanel fButtons = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        fButtons.Children.Add(Small("All", () => { foreach (FcCheckRow<FilterOverrideRow> f in _filters) f.IsChecked = true; Update(); }));
        fButtons.Children.Add(Small("None", () => { foreach (FcCheckRow<FilterOverrideRow> f in _filters) f.IsChecked = false; Update(); }));
        DockPanel.SetDock(fButtons, Dock.Bottom);
        filterPane.Children.Add(fButtons);
        FilterList = MakeFilterList();
        FilterList.SizeChanged += (_, _) => FormParts.FitColumns(FilterList, 34, 40, 26);
        filterPane.Children.Add(FilterList);

        (DockPanel tgtPane, TargetSearch, TargetList) = SearchPane("4  Templates to copy TO", "Search templates");
        TargetList.ItemTemplate = CheckTemplate();
        TargetSearch.TextChanged += (_, _) => FillTargets();
        StackPanel tButtons = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        tButtons.Children.Add(Small("All shown", () => { foreach (FcCheckRow<TemplateEntry> t in TargetList.Items) t.IsChecked = true; Update(); }));
        tButtons.Children.Add(Small("None", () => { foreach (FcCheckRow<TemplateEntry> t in _targets) t.IsChecked = false; Update(); }));
        DockPanel.SetDock(tButtons, Dock.Bottom);
        tgtPane.Children.Insert(1, tButtons);

        Grid middle = new() { Margin = new Thickness(FormParts.Gutter, 4, FormParts.Gutter, 0) };
        middle.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28, GridUnitType.Star) });
        middle.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        middle.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44, GridUnitType.Star) });
        middle.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        middle.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28, GridUnitType.Star) });
        middle.Children.Add(srcPane);
        Grid.SetColumn(filterPane, 2);
        middle.Children.Add(filterPane);
        Grid.SetColumn(tgtPane, 4);
        middle.Children.Add(tgtPane);

        // ---- options and footer ---------------------------------------------------------------
        WrapPanel opts = new() { Margin = new Thickness(FormParts.Gutter, 10, FormParts.Gutter, 0) };
        Lines = Option("Line overrides", saved.Lines, "Projection and cut line colour, pattern and weight.");
        Patterns = Option("Pattern overrides", saved.Patterns, "Surface and cut patterns - foreground and background, colour and visibility.");
        Transparency = Option("Transparency, halftone, detail level", saved.Transparency, "");
        BringMissing = Option("Bring missing filters and patterns across", saved.BringMissing,
            "A filter or pattern this model does not have is copied in from the source. Off: those filters are skipped.");
        SyncRules = Option("Overwrite this model's filter RULES to match the source", false,
            "For a filter that exists here under the same name with different categories or rules. Changes the filter MODEL-WIDE - every view and template using it.");
        foreach (CheckBox c in new[] { Lines, Patterns, Transparency, BringMissing, SyncRules })
        {
            opts.Children.Add(c);
            c.Click += (_, _) => Update();
        }
        SyncWarning = FormParts.Problem();
        SyncWarning.Margin = new Thickness(FormParts.Gutter, 2, FormParts.Gutter, 0);
        SyncWarning.Text = "Overwriting rules changes those filters everywhere in this model, not just on the templates ticked above.";

        Summary = new TextBlock { TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold };
        Problem = FormParts.Problem();
        Problem.Margin = new Thickness(0, 4, 0, 0);
        StackPanel band = new();
        band.Children.Add(Summary);
        band.Children.Add(Problem);

        CopyButton = FormParts.Primary("Copy");
        CopyButton.IsDefault = false;
        CopyButton.Click += (_, _) => Accept();

        StackPanel footer = new();
        footer.Children.Add(opts);
        footer.Children.Add(SyncWarning);
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
            Child = FormParts.ButtonBar(CopyButton, FormParts.Secondary("Cancel", true)),
        });

        DockPanel root = new();
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(top);
        root.Children.Add(footer);
        root.Children.Add(middle);
        Content = root;

        SourceBox.SelectionChanged += (_, _) => SourceChanged();
        _building = false;
        SourceBox.SelectedIndex = 0;
    }

    // ------------------------------------------------------------------ parts

    private static Button Small(string text, Action click)
    {
        Button b = new() { Content = text, Padding = new Thickness(10, 2, 10, 2), MinHeight = 24, Margin = new Thickness(0, 0, 6, 0) };
        b.Click += (_, _) => click();
        return b;
    }

    private static CheckBox Option(string text, bool on, string tip) => new()
    {
        Content = text,
        IsChecked = on,
        Margin = new Thickness(0, 2, 18, 2),
        ToolTip = tip.Length > 0 ? tip : null,
    };

    private static (DockPanel Pane, TextBox Search, ListBox List) SearchPane(string title, string placeholder)
    {
        DockPanel pane = new();
        StackPanel head = new();
        head.Children.Add(FormParts.Section(title, first: true));
        (Grid host, TextBox box) = FormParts.PlaceholderInput(placeholder);
        host.Margin = new Thickness(0, 0, 0, 6);
        head.Children.Add(host);
        DockPanel.SetDock(head, Dock.Top);
        pane.Children.Add(head);
        ListBox list = new() { MinHeight = 160 };
        pane.Children.Add(list);
        return (pane, box, list);
    }

    private static DataTemplate CheckTemplate()
    {
        FrameworkElementFactory box = new(typeof(CheckBox));
        box.SetBinding(ToggleButtonChecked, new Binding("IsChecked") { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
        box.SetBinding(ContentControl.ContentProperty, new Binding("Text"));
        return new DataTemplate { VisualTree = box };
    }

    private static readonly DependencyProperty ToggleButtonChecked = System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty;

    private ListView MakeFilterList()
    {
        GridView gv = new();
        FrameworkElementFactory box = new(typeof(CheckBox));
        box.SetBinding(ToggleButtonChecked, new Binding("IsChecked") { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
        box.SetBinding(ContentControl.ContentProperty, new Binding("Text"));
        box.AddHandler(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, new RoutedEventHandler((_, _) => Update()));
        gv.Columns.Add(new GridViewColumn { Header = "Filter", Width = 220, CellTemplate = new DataTemplate { VisualTree = box } });
        foreach ((string header, string path, double width) in new[] { ("Carries", "Carries", 230.0), ("In this model", "Here", 170.0) })
        {
            FrameworkElementFactory t = new(typeof(TextBlock));
            t.SetBinding(TextBlock.TextProperty, new Binding(path));
            t.SetBinding(FrameworkElement.ToolTipProperty, new Binding(path));
            t.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
            gv.Columns.Add(new GridViewColumn { Header = header, Width = width, CellTemplate = new DataTemplate { VisualTree = t } });
        }
        Style item = new(typeof(ListViewItem));
        DataTrigger differ = new() { Binding = new Binding("Here"), Value = "in this model, DIFFERENT rules" };
        differ.Setters.Add(new Setter(Control.ForegroundProperty, new SolidColorBrush(Color.FromRgb(0x9A, 0x60, 0x00))));
        item.Triggers.Add(differ);
        return new ListView { View = gv, ItemContainerStyle = item, MinHeight = 160 };
    }

    // ------------------------------------------------------------------ source

    private void SourceChanged()
    {
        if (_building)
            return;
        int i = SourceBox.SelectedIndex;
        bool file = i == SourceBox.Items.Count - 1;
        PathRow.Visibility = file ? Visibility.Visible : Visibility.Collapsed;
        if (file)
        {
            _sourceTemplates = [];
            SourceLoaded = false;
            SourceStatus.Text = PathBox.Text.Trim().Length > 0 ? "Press Open to read the file." : "Pick a .rvt with Browse...";
            FillSourceList();
            return;
        }
        Choice = i <= 0 ? FilterSourceChoice.ThisModel : new FilterSourceChoice(i - 1, null);
        LoadSource();
    }

    /// <summary>Read the chosen source's templates (opening the file when it is one).</summary>
    internal void LoadSource()
    {
        bool file = SourceBox.SelectedIndex == SourceBox.Items.Count - 1;
        if (file)
            Choice = new FilterSourceChoice(-1, PathBox.Text.Trim());
        try
        {
            Cursor = System.Windows.Input.Cursors.Wait;
            (List<TemplateEntry> templates, string status) = _host.LoadSource(Choice);
            _sourceTemplates = templates;
            SourceLoaded = true;
            SourceStatus.Text = templates.Count == 0 ? status + " - it has no view templates." : $"{status} - {templates.Count} view template(s).";
        }
        catch (Exception ex)
        {
            _sourceTemplates = [];
            SourceLoaded = false;
            SourceStatus.Text = "Could not read the source: " + ex.Message;
        }
        finally
        {
            Cursor = null;
        }
        FillSourceList();
        FillTargets();
    }

    private void FillSourceList()
    {
        string s = SourceSearch.Text ?? "";
        long keep = SourceTemplate?.Id ?? -1;
        List<TemplateEntry> shown = [.. _sourceTemplates.Where(t => Matches(t.Label, s))];
        SourceList.ItemsSource = shown;
        SourceList.SelectedItem = shown.FirstOrDefault(t => t.Id == keep);
        if (SourceList.SelectedItem is null)
            SourceTemplateChanged();
    }

    private static bool Matches(string text, string search)
        => search.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).All(w => text.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0);

    private void SourceTemplateChanged()
    {
        List<FilterOverrideRow> rows = SourceTemplate is { } t ? _host.ReadFilters(t.Id) : [];
        HashSet<string> wasOff = [.. _filters.Where(f => !f.IsChecked).Select(f => f.Item.Name)];
        Dictionary<string, string> here = rows.Count > 0 ? _host.TargetStatus(rows) : [];
        _filters = [.. rows.Select(r => new FcCheckRow<FilterOverrideRow>(r, r.Name)
        {
            IsChecked = !wasOff.Contains(r.Name),
            Carries = r.Describe(),
            Here = here.TryGetValue(r.Name, out string? h) ? h : "",
        })];
        FilterList.ItemsSource = _filters;
        FillTargets();
    }

    // ------------------------------------------------------------------ targets

    /// <summary>In the same model the source template is not a target.</summary>
    private bool Visible(FcCheckRow<TemplateEntry> t) => !(Choice.IsThisModel && SourceLoaded && SourceTemplate?.Id == t.Item.Id);

    private void FillTargets()
    {
        string s = TargetSearch.Text ?? "";
        TargetList.ItemsSource = _targets.Where(t => Visible(t) && Matches(t.Text, s)).ToList();
        Update();
    }

    /// <summary>Tests: tick targets by label.</summary>
    internal void TickTargets(params string[] names)
    {
        foreach (FcCheckRow<TemplateEntry> t in _targets)
            t.IsChecked = names.Contains(t.Item.Name);
        Update();
    }

    // ------------------------------------------------------------------ footer

    internal void Update()
    {
        if (_building || CopyButton is null)
            return;
        SyncWarning.Visibility = SyncRules.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        int filters = ChosenFilters.Count, targets = TargetIds.Count;
        int missing = _filters.Count(f => f.IsChecked && f.Here == "not in this model");
        int differ = _filters.Count(f => f.IsChecked && f.Here == "in this model, DIFFERENT rules");
        string? problem = !SourceLoaded ? "Choose where to copy from."
            : SourceTemplate is null ? "Pick the template to copy filters FROM."
            : _filters.Count == 0 ? $"\"{SourceTemplate.Name}\" has no filters on it."
            : filters == 0 ? "Tick at least one filter."
            : targets == 0 ? "Tick at least one template to copy TO."
            : null;
        Summary.Text = problem is not null ? ""
            : $"{filters} filter(s) onto {targets} template(s)."
              + (missing > 0 ? BringMissing.IsChecked == true ? $" {missing} will be copied into this model first." : $" {missing} are not in this model and will be skipped." : "")
              + (differ > 0 ? SyncRules.IsChecked == true ? $" {differ} will have their rules overwritten model-wide." : $" {differ} exist here with different rules - their graphics are copied, their rules kept." : "");
        Problem.Text = problem ?? "";
        Problem.Visibility = problem is null ? Visibility.Collapsed : Visibility.Visible;
        CopyButton.Content = problem is null ? $"Copy {filters} filter(s) to {targets} template(s)" : "Copy";
        CopyButton.IsEnabled = problem is null;
    }

    internal FilterCopySettings SettingsToSave() => new()
    {
        SourcePath = PathBox.Text.Trim(),
        Lines = Lines.IsChecked == true,
        Patterns = Patterns.IsChecked == true,
        Transparency = Transparency.IsChecked == true,
        BringMissing = BringMissing.IsChecked == true,
    };

    private void Accept()
    {
        Update();
        if (!CopyButton.IsEnabled)
            return;
        if (SyncRules.IsChecked == true && _filters.Any(f => f.IsChecked && f.Here == "in this model, DIFFERENT rules")
            && Prompt.Show(this, "Overwrite the rules of the filters that differ?\n\nThis changes those filters everywhere in this model - every view and template that uses them - not only the templates ticked here.",
                DialogTitle, MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        DialogResult = true;
    }
}
