using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Ribbit.Geometry.Sheets;
using Ribbit.Native.Revit;
using Ribbit.Native.Settings;

namespace Ribbit.Native.Ui;

/// <summary>One line of the preview list.</summary>
internal sealed class DupLine
{
    public string Row { get; init; } = "";
    public string View { get; init; } = "";
    public string Action { get; init; } = "";
    public string Details { get; init; } = "";

    /// <summary>ok / warn / bad / muted: the colour of the Action cell.</summary>
    public string Tone { get; init; } = "";
    public bool NeedsAttention { get; init; }
    public DupStep Step { get; init; } = null!;
}

/// <summary>
/// Part Sheets Duplicator's one window: pick the workbook and worksheet, pick a
/// base view for each level that needs one, and see EVERY view the run will
/// make or change before anything is written.
///
/// The list is <see cref="DuplicatorPlanner"/>'s plan, recomputed on every
/// change, and Run carries out exactly that plan - so there is no "dry run"
/// to tick and read a report from: the window is the dry run.
///
/// ONE PICKER PER LEVEL, not a flat checklist: two picks landing on the same
/// level used to fail silently. A level whose views all exist needs no base
/// view at all, and its card says so instead of asking.
/// </summary>
internal sealed class DuplicatorWindow : Window
{
    internal const string DialogTitle = "Part Sheets Duplicator";

    /// <summary>Added to a dependent view's name in the base-view pickers.</summary>
    internal const string DependentMark = "   (dependent view - its crop is copied)";

    /// <summary>Tests only: answers the Browse button instead of the file dialog.</summary>
    internal static Func<string?>? BrowseHook { get; set; }

    internal readonly TextBox FilePath;
    internal readonly ComboBox Worksheet;
    internal readonly TextBlock FileStatus;
    internal readonly StackPanel LevelPanel;
    internal readonly RadioButton SkipExisting;
    internal readonly RadioButton UpdateExisting;
    internal readonly CheckBox OnlyAttention;
    internal readonly TextBlock ChoiceHint;
    internal readonly Button MakeIndependent;
    internal readonly ComboBox ParentPick;
    internal readonly Button MakeDependent;
    internal readonly Button SkipRows;
    internal readonly Button AutomaticRows;

    /// <summary>What the user chose for rows, by Excel row number. Rows not in it are Automatic.</summary>
    internal Dictionary<int, RowChoice> Choices { get; } = [];
    internal readonly ListView Preview;
    internal readonly TextBlock Summary;
    internal readonly TextBlock ProblemLine;
    internal readonly Button RunButton;

    /// <summary>Level key -> its base-view picker.</summary>
    internal Dictionary<string, ComboBox> LevelPickers { get; } = [];

    private readonly IReadOnlyList<DupView> _floorPlans;
    private readonly Dictionary<string, List<DupView>> _byLevel;
    private readonly DupModel _model;
    private readonly Dictionary<long, string> _labels;

    /// <summary>Level key -> base view label: remembered picks, kept up to date as the pickers change.</summary>
    private readonly Dictionary<string, string> _savedLevelMap;

    /// <summary>Remembered row choices (see DuplicatorSettings.RowChoices), kept up to date as the user chooses.</summary>
    private readonly Dictionary<string, string> _savedChoices;

    private List<DupRow>? _rows;
    private string _loadedPath = "";
    private string _loadedSheet = "";
    private bool _building = true;

    /// <summary>What Run will do now. Null until a worksheet is loaded.</summary>
    internal DupPlan? Plan { get; private set; }

    /// <summary>The lines the list shows now.</summary>
    internal List<DupLine> Lines { get; private set; } = [];

    internal string LoadedPath => _loadedPath;
    internal string LoadedSheet => _loadedSheet;
    internal ExistingMode Mode => UpdateExisting.IsChecked == true ? ExistingMode.Update : ExistingMode.Skip;

    public DuplicatorWindow(DupModel model, DuplicatorSettings saved)
    {
        _floorPlans = model.FloorPlans;
        _byLevel = ByLevel(model.FloorPlans);
        _model = model;
        _labels = model.FloorPlans.GroupBy(v => v.Id).ToDictionary(g => g.Key, g => g.First().Label);
        _savedLevelMap = new Dictionary<string, string>(saved.LevelMap);
        _savedChoices = new Dictionary<string, string>(saved.RowChoices);

        Title = DialogTitle;
        Size area = RevitScreen.WorkArea();
        Width = Math.Min(1180, area.Width - 40);
        Height = Math.Min(800, area.Height - 40);
        MinWidth = 820;
        MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = false;
        new WindowInteropHelper(this).Owner = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;

        // --- 1. the workbook -----------------------------------------------------------------
        StackPanel top = new() { Margin = new Thickness(FormParts.Gutter, FormParts.Gutter, FormParts.Gutter, 0) };
        top.Children.Add(FormParts.Section("1  Worksheet", first: true));

        Grid fileGrid = new();
        fileGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
        fileGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        fileGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 180 });

        StackPanel fileCol = new();
        fileCol.Children.Add(FormParts.FieldLabel("Excel workbook"));
        (Grid pathHost, TextBox pathBox) = FormParts.PlaceholderInput("Path to a .xlsx or .xlsm workbook...", saved.FilePath);
        FilePath = pathBox;
        FilePath.LostFocus += (_, _) => PathChanged();
        Button browse = new() { Content = "Browse..." };
        browse.Click += (_, _) => Browse();
        DockPanel browseRow = FormParts.BrowseRow(pathHost, browse);
        browseRow.Margin = new Thickness(0);
        fileCol.Children.Add(browseRow);
        fileGrid.Children.Add(fileCol);

        StackPanel sheetCol = new();
        sheetCol.Children.Add(FormParts.FieldLabel("Worksheet (tab)"));
        Worksheet = new ComboBox { MinHeight = 26, Padding = new Thickness(6, 4, 6, 4) };
        Worksheet.SelectionChanged += (_, _) =>
        {
            if (!_building && Worksheet.SelectedItem is string)
                Load();
        };
        sheetCol.Children.Add(Worksheet);
        Grid.SetColumn(sheetCol, 2);
        fileGrid.Children.Add(sheetCol);
        top.Children.Add(fileGrid);

        FileStatus = FormParts.Status();
        FileStatus.Margin = new Thickness(0, 6, 0, 0);
        top.Children.Add(FileStatus);

        // --- 2. levels (left) and 3. preview (right) ------------------------------------------
        StackPanel levelHead = new() { Margin = new Thickness(0, 0, 0, 0) };
        levelHead.Children.Add(FormParts.Section("2  Base view per level"));
        levelHead.Children.Add(FormParts.Hint(
            "Each new Overall (and every plain view) is duplicated, with detailing, from its level's base view. "
            + "\"Sheet N of M\" views are then made as dependents of their Overall."));

        LevelPanel = new StackPanel();
        ScrollViewer levelScroll = new()
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = LevelPanel,
            Padding = new Thickness(0, 0, 6, 0),
        };
        DockPanel left = new();
        DockPanel.SetDock(levelHead, Dock.Top);
        left.Children.Add(levelHead);
        left.Children.Add(levelScroll);

        StackPanel previewHead = new();
        previewHead.Children.Add(FormParts.Section("3  What will happen"));

        StackPanel existingRow = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        existingRow.Children.Add(new TextBlock { Text = "Views that already exist:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) });
        SkipExisting = new RadioButton { Content = "Leave them alone", IsChecked = !saved.UpdateExisting, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 0) };
        UpdateExisting = new RadioButton
        {
            Content = "Update them",
            IsChecked = saved.UpdateExisting,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "Re-apply the row's view template, and copy "
                      + string.Join(" / ", DuplicatorEngine.MirrorParams) + " from the Overall onto its dependents.",
        };
        SkipExisting.Checked += (_, _) => Replan();
        UpdateExisting.Checked += (_, _) => Replan();
        existingRow.Children.Add(SkipExisting);
        existingRow.Children.Add(UpdateExisting);
        previewHead.Children.Add(existingRow);

        OnlyAttention = new CheckBox { Content = "Show only rows that need attention", Margin = new Thickness(0, 0, 0, 6) };
        OnlyAttention.Click += (_, _) => Fill();
        previewHead.Children.Add(OnlyAttention);

        // --- what the selected rows do: names differ from project to project,
        // so any row's action can be set by hand.
        Border choiceBar = new()
        {
            Background = SystemColors.ControlLightBrush,
            BorderBrush = SystemColors.ControlDarkBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8, 6, 8, 6),
            Margin = new Thickness(0, 0, 0, 6),
        };
        StackPanel choiceBody = new();
        ChoiceHint = new TextBlock
        {
            FontSize = 11,
            Foreground = SystemColors.GrayTextBrush,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 5),
        };
        choiceBody.Children.Add(ChoiceHint);
        WrapPanel choiceRow = new();
        Button Small(string text, string tip, Action click)
        {
            Button b = new() { Content = text, ToolTip = tip, Padding = new Thickness(8, 2, 8, 2), MinHeight = 24, Margin = new Thickness(0, 0, 6, 4) };
            b.Click += (_, _) => click();
            choiceRow.Children.Add(b);
            return b;
        }
        MakeIndependent = Small("Independent view", "Copy it from its level's base view, whatever its name says.",
            () => Choose(new RowChoice(RowChoiceKind.Independent)));
        ParentPick = new ComboBox
        {
            IsEditable = true,
            IsTextSearchEnabled = true,
            Width = 300,
            MinHeight = 24,
            Margin = new Thickness(6, 0, 6, 4),
            ToolTip = "The view to put the selected rows under: any existing floor plan that is not itself a dependent, or a view this run makes.",
        };
        choiceRow.Children.Add(new TextBlock { Text = "Dependent of:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 4) });
        choiceRow.Children.Add(ParentPick);
        MakeDependent = Small("Put under it", "Make the selected rows dependents of the view chosen on the left.",
            () =>
            {
                string parent = (ParentPick.Text ?? "").Trim();
                if (parent.Length > 0)
                    Choose(new RowChoice(RowChoiceKind.DependentOf, parent));
            });
        SkipRows = Small("Skip", "Leave the selected rows out of this run.", () => Choose(new RowChoice(RowChoiceKind.Skip)));
        AutomaticRows = Small("Automatic", "Back to what the name says.", () => Choose(RowChoice.Auto));
        choiceBody.Children.Add(choiceRow);
        choiceBar.Child = choiceBody;
        previewHead.Children.Add(choiceBar);

        Preview = MakeList();
        Preview.SelectionMode = SelectionMode.Extended;
        Preview.SelectionChanged += (_, _) => SelectionChanged();
        Preview.SizeChanged += (_, _) => FitColumns();
        DockPanel right = new();
        DockPanel.SetDock(previewHead, Dock.Top);
        right.Children.Add(previewHead);
        right.Children.Add(Preview);

        Grid middle = new() { Margin = new Thickness(FormParts.Gutter, 0, FormParts.Gutter, 0) };
        middle.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(340) });
        middle.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        middle.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        middle.Children.Add(left);
        Grid.SetColumn(right, 2);
        middle.Children.Add(right);

        // --- footer ------------------------------------------------------------------------------
        Summary = new TextBlock { TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold };
        ProblemLine = FormParts.Problem();
        ProblemLine.Margin = new Thickness(0, 4, 0, 0);
        StackPanel band = new();
        band.Children.Add(Summary);
        band.Children.Add(ProblemLine);

        RunButton = FormParts.Primary("Nothing to do");
        RunButton.IsEnabled = false;
        RunButton.Click += (_, _) => Accept();

        StackPanel footer = new();
        footer.Children.Add(new Border
        {
            Background = SystemColors.ControlLightBrush,
            BorderBrush = SystemColors.ControlDarkBrush,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(FormParts.Gutter, 10, FormParts.Gutter, 10),
            Margin = new Thickness(0, 12, 0, 0),
            Child = band,
        });
        footer.Children.Add(new Border
        {
            Padding = new Thickness(FormParts.Gutter, 0, FormParts.Gutter, FormParts.Gutter),
            Child = FormParts.ButtonBar(RunButton, FormParts.Secondary("Cancel", true)),
        });

        DockPanel root = new();
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(top);
        root.Children.Add(footer);
        root.Children.Add(middle);
        Content = root;

        ShowEmpty("Choose a workbook and a worksheet above - the levels it uses appear here.");
        SelectionChanged();
        _building = false;

        // The remembered workbook fills in once the window is on screen: a big
        // worksheet takes a moment to read, and a window that opens and then
        // fills reads as responsive; one that hangs before its first frame does not.
        string remembered = saved.SheetName;
        Dispatcher.BeginInvoke(new Action(() => PathChanged(remembered)), DispatcherPriority.Loaded);
    }

    // ------------------------------------------------------------------ the file

    private void Browse()
    {
        string? picked;
        if (BrowseHook is not null)
        {
            picked = BrowseHook();
        }
        else
        {
            Microsoft.Win32.OpenFileDialog dlg = new()
            {
                Filter = "Excel Workbooks (*.xlsx;*.xlsm)|*.xlsx;*.xlsm|All Files (*.*)|*.*",
                Title = "Select the worksheet workbook",
            };
            string current = SheetMakerSettings.CleanPath(FilePath.Text);
            string? folder = current.Length > 0 ? Path.GetDirectoryName(current) : null;
            if (folder is not null && Directory.Exists(folder))
                dlg.InitialDirectory = folder;
            picked = dlg.ShowDialog(this) == true ? dlg.FileName : null;
        }

        if (picked is null)
            return;
        FilePath.Text = picked;
        PathChanged();
    }

    /// <summary>Re-read the tab list, keep (or pick) a worksheet, and load it.</summary>
    internal void PathChanged(string? preferredSheet = null)
    {
        string path = SheetMakerSettings.CleanPath(FilePath.Text);
        FilePath.Text = path;
        if (path == _loadedPath && Worksheet.Items.Count > 0 && preferredSheet is null)
            return;

        List<string> names = XlsxWorkbook.TryListSheetNames(path);
        _building = true;
        Worksheet.ItemsSource = names;
        _building = false;

        if (names.Count == 0)
        {
            _rows = null;
            _loadedPath = _loadedSheet = "";
            FileStatus.Text = path.Length == 0 ? "Choose the workbook the view list is in."
                : File.Exists(path) ? "That file has no readable worksheets - is it a real .xlsx/.xlsm workbook?"
                : "That file does not exist.";
            ShowEmpty(FileStatus.Text);
            Replan();
            return;
        }

        string want = preferredSheet ?? (Worksheet.SelectedItem as string) ?? _loadedSheet;
        _building = true;
        Worksheet.SelectedItem = names.Contains(want) ? want : names[0];
        _building = false;
        Load();
    }

    /// <summary>
    /// The worksheet's rows. Blank rows are skipped, not the end: clearing one
    /// row in the middle must not silently drop every row below it.
    /// </summary>
    internal static List<XlsxRow> ReadSheet(string path, string sheet)
    {
        using XlsxWorkbook wb = XlsxWorkbook.Open(path);
        return wb.ReadWorksheet(sheet, stopAtBlankRow: false);
    }

    /// <summary>Read the chosen worksheet and rebuild the level cards and the plan.</summary>
    internal void Load()
    {
        string path = SheetMakerSettings.CleanPath(FilePath.Text);
        string sheet = Worksheet.SelectedItem as string ?? "";
        _rows = null;
        _loadedPath = path;
        _loadedSheet = sheet;

        List<XlsxRow> read;
        try
        {
            read = ReadSheet(path, sheet);
        }
        catch (Exception ex)
        {
            FileStatus.Text = $"Could not read \"{sheet}\": {ex.Message}";
            ShowEmpty(FileStatus.Text);
            Replan();
            return;
        }

        IReadOnlyCollection<string> columns = read.FirstOrDefault()?.Columns ?? [];
        if (read.Count == 0)
        {
            FileStatus.Text = $"\"{sheet}\" has no rows below its header row.";
            ShowEmpty(FileStatus.Text);
            Replan();
            return;
        }
        if (!columns.Contains(SheetText.Normalize("Floor Plan View Name")))
        {
            FileStatus.Text = $"\"{sheet}\" has no \"Floor Plan View Name\" column - that column names each view "
                              + "and says its level, so the tool cannot work without it. Its columns: "
                              + string.Join(", ", read[0].OriginalHeaders) + ".";
            ShowEmpty(FileStatus.Text);
            Replan();
            return;
        }

        _rows = [.. read.Select(DupRow.From)];

        // The choices remembered for these rows, by name.
        Choices.Clear();
        foreach (DupRow r in _rows)
        {
            if (DuplicatorSettings.ParseChoice(_savedChoices.TryGetValue(ChoiceKey(r.ExcelRow), out string? c) ? c : null) is RowChoice rc)
                Choices[r.ExcelRow] = rc;
        }

        List<string> missing =
        [
            .. new[] { ("Template Name", "no view templates are applied"), ("Phase", "names get no phase added"), ("Series", "") }
                .Where(c => !columns.Contains(SheetText.Normalize(c.Item1)) && c.Item2.Length > 0)
                .Select(c => $"no \"{c.Item1}\" column, so {c.Item2}")
        ];
        FileStatus.Text = $"{_rows.Count} row(s) read from \"{sheet}\"."
                          + (missing.Count > 0 ? "  Note: " + string.Join("; ", missing) + "." : "");

        BuildLevelCards();
        Replan();
    }

    // ------------------------------------------------------------------ levels

    private void ShowEmpty(string message)
    {
        foreach (KeyValuePair<string, ComboBox> kv in LevelPickers)
        {
            if (kv.Value.SelectedItem is ComboBoxItem { Tag: DupView v })
                _savedLevelMap[kv.Key] = v.Label;
        }
        LevelPanel.Children.Clear();
        LevelPickers.Clear();
        _cardParts.Clear();
        LevelPanel.Children.Add(new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            Foreground = SystemColors.GrayTextBrush,
            FontStyle = FontStyles.Italic,
            Margin = new Thickness(4, 16, 4, 0),
        });
    }

    /// <summary>Floor plans grouped by their own level's key, to suggest a base view per level.</summary>
    internal static Dictionary<string, List<DupView>> ByLevel(IEnumerable<DupView> views)
    {
        Dictionary<string, List<DupView>> groups = [];
        foreach (DupView v in views)
        {
            string? key = ViewNaming.NormalizeLevelKey(v.LevelName);
            if (key is null)
                continue;
            if (!groups.TryGetValue(key, out List<DupView>? list))
                groups[key] = list = [];
            list.Add(v);
        }
        return groups;
    }

    internal static string LevelTitle(string key) => ViewNaming.LevelTitle(key);

    private void BuildLevelCards()
    {
        // Keep what was picked: a reload (or Run re-reading the file) must not
        // throw away the choices on screen.
        foreach (KeyValuePair<string, ComboBox> kv in LevelPickers)
        {
            if (kv.Value.SelectedItem is ComboBoxItem { Tag: DupView v })
                _savedLevelMap[kv.Key] = v.Label;
        }

        LevelPanel.Children.Clear();
        LevelPickers.Clear();
        _cardParts.Clear();
        if (_rows is null)
            return;

        foreach (ViewNaming.LevelKeyEntry e in ViewNaming.LevelKeySummary(_rows.Select(r => new XlsxRow(
                     new Dictionary<string, string> { [SheetText.Normalize("Floor Plan View Name")] = r.FloorPlanViewName }))))
        {
            _byLevel.TryGetValue(e.Key, out List<DupView>? onLevel);
            onLevel ??= [];
            HashSet<long> onIds = [.. onLevel.Select(v => v.Id)];

            ComboBox combo = new() { MinHeight = 26, HorizontalAlignment = HorizontalAlignment.Stretch };
            ComboBoxItem? pick = null;
            string? savedLabel = _savedLevelMap.TryGetValue(e.Key, out string? s) ? s : null;

            void Add(DupView v)
            {
                ComboBoxItem item = new() { Content = v.Label + (v.IsDependent ? DependentMark : ""), Tag = v };
                if (v.IsDependent)
                    item.Foreground = SystemColors.GrayTextBrush;
                combo.Items.Add(item);
                if (savedLabel is not null && v.Label == savedLabel)
                    pick = item;
            }

            // Dependent views last in each group, and marked: one is a cropped
            // piece of a plan, and its crop is copied into every view made from
            // it. (A real run duplicated a Roof Overall from a "Sheet 2 of 3".)
            static IEnumerable<DupView> Ordered(IEnumerable<DupView> views)
                => views.OrderBy(v => v.IsDependent).ThenBy(v => v.Label, NaturalSort.Comparer);

            foreach (DupView v in Ordered(onLevel))
                Add(v);
            List<DupView> others = [.. Ordered(_floorPlans.Where(v => !onIds.Contains(v.Id)))];
            if (onLevel.Count > 0 && others.Count > 0)
                combo.Items.Add(new ComboBoxItem { Content = "-- floor plans on other levels --", IsEnabled = false, FontStyle = FontStyles.Italic });
            foreach (DupView v in others)
                Add(v);

            // Only an UNAMBIGUOUS guess is made for you: exactly one independent
            // floor plan on the level (its dependents are pieces of it, not rivals).
            List<DupView> whole = [.. onLevel.Where(v => !v.IsDependent)];
            if (pick is null && whole.Count == 1)
                pick = combo.Items.OfType<ComboBoxItem>().First(i => i.Tag == whole[0]);
            if (pick is not null)
                combo.SelectedItem = pick;
            combo.SelectionChanged += (_, _) => Replan();

            LevelPickers[e.Key] = combo;
            LevelPanel.Children.Add(LevelCard(e, combo, onLevel.Count));
        }
    }

    private readonly Dictionary<string, (TextBlock Note, ComboBox Combo, Border Card)> _cardParts = [];

    private Border LevelCard(ViewNaming.LevelKeyEntry e, ComboBox combo, int onLevel)
    {
        StackPanel body = new();
        DockPanel head = new();
        Border pill = FormParts.Pill($"{e.Count} row{(e.Count == 1 ? "" : "s")}");
        pill.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(pill, Dock.Right);
        head.Children.Add(pill);
        head.Children.Add(new TextBlock { Text = LevelTitle(e.Key), FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        body.Children.Add(head);

        body.Children.Add(new TextBlock
        {
            Text = "e.g. " + e.Example,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 11,
            Foreground = SystemColors.GrayTextBrush,
            Margin = new Thickness(0, 2, 0, 6),
            ToolTip = e.Example,
        });

        TextBlock note = new() { FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
        body.Children.Add(combo);
        body.Children.Add(note);
        if (onLevel == 0)
            combo.ToolTip = "No floor plan in the model is on a level called this - pick the one to copy.";

        Border card = FormParts.RowCard(body);
        _cardParts[e.Key] = (note, combo, card);
        return card;
    }

    /// <summary>The chosen base view per level (level key -> view id).</summary>
    internal Dictionary<string, long> BaseViews()
    {
        Dictionary<string, long> map = [];
        foreach (KeyValuePair<string, ComboBox> kv in LevelPickers)
        {
            if (kv.Value.SelectedItem is ComboBoxItem { Tag: DupView v })
                map[kv.Key] = v.Id;
        }
        return map;
    }

    /// <summary>Choose a level's base view in its picker (by the label the picker shows).</summary>
    internal bool ChooseBaseView(string levelKey, string label)
    {
        if (!LevelPickers.TryGetValue(levelKey, out ComboBox? combo))
            return false;
        ComboBoxItem? item = combo.Items.OfType<ComboBoxItem>().FirstOrDefault(i => i.Tag is DupView v && v.Label == label);
        if (item is null)
            return false;
        combo.SelectedItem = item;
        return true;
    }

    // ------------------------------------------------------------------ the plan

    internal void Replan()
    {
        if (_building)
            return;

        if (_rows is null)
        {
            Plan = null;
            Lines = [];
            Preview.ItemsSource = null;
            Summary.Text = "Nothing loaded yet.";
            SetProblem("");
            RunButton.IsEnabled = false;
            RunButton.Content = "Nothing to do";
            return;
        }

        Plan = DuplicatorPlanner.Plan(_rows, _model, BaseViews(), Mode, Choices);
        UpdateCards(Plan);
        Fill();

        string typed = ParentPick.Text ?? "";
        ParentPick.ItemsSource = Plan.ParentChoices;
        ParentPick.Text = typed;

        DupCounts c = Plan.Counts;
        List<string> parts = [];
        if (c.CreateParents > 0) parts.Add($"{c.CreateParents} Overall/plain view(s)" + (c.Automatic > 0 && c.CreateParents > 0 ? $" ({Plan.Steps.Count(s => s.IsAutomatic && s.Creates)} with no row of their own)" : ""));
        if (c.CreateDependents > 0) parts.Add($"{c.CreateDependents} dependent view(s)");
        string make = parts.Count > 0 ? "To make: " + string.Join(" and ", parts) + "." : "Nothing new to make.";
        List<string> rest = [];
        if (c.Update > 0) rest.Add($"{c.Update} to update");
        if (c.Exists > 0) rest.Add($"{c.Exists} already there, left alone");
        if (c.Repeat > 0) rest.Add($"{c.Repeat} repeated row(s) ignored");
        if (c.Skipped > 0) rest.Add($"{c.Skipped} skipped by you");
        if (c.Error > 0) rest.Add($"{c.Error} with errors");
        Summary.Text = $"{c.Rows} row(s).  {make}" + (rest.Count > 0 ? "  " + string.Join(", ", rest) + "." : "");

        List<string> problems = [];
        if (Plan.MissingBaseViews.Count > 0)
            problems.Add("Choose a base view for " + string.Join(", ", Plan.MissingBaseViews.Select(LevelTitle))
                         + " - until then, the views on that level are not made.");
        if (Plan.MissingTemplates.Count > 0)
            problems.Add("Not in this model, so those views get no template: " + string.Join("; ", Plan.MissingTemplates.Select(t => $"\"{t}\"")) + ".");
        foreach ((string name, string kind) in Plan.OtherKindTemplates)
            problems.Add($"\"{name}\" is a {kind} template, not a Floor Plan one. Revit accepts it and it is applied as the worksheet asks - check it is the one you meant.");
        List<DupStep> noLevel = [.. Plan.Steps.Where(s => s.Action == DupAction.Error && s.LevelKey.Length == 0)];
        if (noLevel.Count > 0)
            problems.Add($"{noLevel.Count} row(s) have no level in their name (row {string.Join(", ", noLevel.Select(s => s.ExcelRow))}) and are skipped.");
        SetProblem(string.Join("\n", problems));

        List<string> verbs = [];
        if (c.Creates > 0) verbs.Add($"Create {c.Creates} view{(c.Creates == 1 ? "" : "s")}");
        int updates = Plan.Steps.Count(s => s.Action == DupAction.Update && s.Writes);
        if (updates > 0) verbs.Add((verbs.Count > 0 ? "update " : "Update ") + $"{updates}");
        RunButton.IsEnabled = verbs.Count > 0;
        RunButton.Content = verbs.Count > 0 ? string.Join(", ", verbs) : "Nothing to do";
    }

    private void UpdateCards(DupPlan plan)
    {
        foreach (DupLevel l in plan.Levels)
        {
            if (!_cardParts.TryGetValue(l.Key, out (TextBlock Note, ComboBox Combo, Border Card) p))
                continue;
            int makes = plan.Steps.Count(s => s.LevelKey == l.Key && s.Action == DupAction.CreateParent);
            if (!l.NeedsBaseView)
            {
                p.Note.Text = "Not needed: nothing on this level is duplicated from a base view (its Overall views already exist).";
                p.Note.Foreground = SystemColors.GrayTextBrush;
                p.Combo.Visibility = System.Windows.Visibility.Collapsed;
            }
            else if (l.BaseViewId == 0)
            {
                p.Note.Text = "Choose the floor plan to copy for this level.";
                p.Note.Foreground = Bad;
                p.Combo.Visibility = System.Windows.Visibility.Visible;
            }
            else
            {
                bool dependent = _floorPlans.Any(v => v.Id == l.BaseViewId && v.IsDependent);
                p.Note.Text = dependent
                    ? $"That is a DEPENDENT view: its crop is copied into the {makes} view(s) made from it. Pick the whole plan unless that is what you want."
                    : $"{makes} view(s) will be duplicated from it.";
                p.Note.Foreground = dependent ? Warn : SystemColors.GrayTextBrush;
                p.Combo.Visibility = System.Windows.Visibility.Visible;
            }
            p.Card.BorderBrush = l.NeedsBaseView && l.BaseViewId == 0 ? Bad : SystemColors.ControlDarkBrush;
        }
    }

    private void SetProblem(string text)
    {
        ProblemLine.Text = text;
        ProblemLine.Visibility = text.Length > 0 ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
    }

    private void Fill()
    {
        if (Plan is null)
            return;
        bool only = OnlyAttention.IsChecked == true;

        // Re-listing must not lose the rows the user has selected to change.
        HashSet<int> selected = [.. SelectedRows()];
        Lines = [.. Plan.InSheetOrder().Select(s => ToLine(s, _labels)).Where(l => !only || l.NeedsAttention)];
        Preview.ItemsSource = Lines;
        foreach (DupLine l in Lines.Where(l => l.Step.ExcelRow is int r && selected.Contains(r)))
            Preview.SelectedItems.Add(l);
        SelectionChanged();
    }

    /// <summary>The worksheet rows selected in the list (an automatic Overall has no row, so it is not one).</summary>
    internal List<int> SelectedRows()
        => [.. Preview.SelectedItems.OfType<DupLine>().Select(l => l.Step.ExcelRow).OfType<int>().Distinct()];

    private void SelectionChanged()
    {
        int n = SelectedRows().Count;
        ChoiceHint.Text = n == 0
            ? "Names differ from project to project: select rows in the list (Ctrl or Shift for several) to decide yourself what they make."
            : $"{n} row{(n == 1 ? "" : "s")} selected - choose what {(n == 1 ? "it makes" : "they make")}:";
        foreach (Button b in new[] { MakeIndependent, MakeDependent, SkipRows, AutomaticRows })
            b.IsEnabled = n > 0;
    }

    /// <summary>Key a row's choice is remembered by: the worksheet and the row's name, not its number.</summary>
    private string ChoiceKey(int excelRow)
        => $"{_loadedSheet}|{(_rows?.FirstOrDefault(r => r.ExcelRow == excelRow)?.FloorPlanViewName ?? "").Trim().ToLowerInvariant()}";

    /// <summary>Set the selected rows' choice (Automatic clears it), then re-plan.</summary>
    internal void Choose(RowChoice choice)
    {
        foreach (int row in SelectedRows())
        {
            if (choice.Kind == RowChoiceKind.Automatic)
            {
                Choices.Remove(row);
                _savedChoices.Remove(ChoiceKey(row));
            }
            else
            {
                Choices[row] = choice;
                _savedChoices[ChoiceKey(row)] = DuplicatorSettings.FormatChoice(choice);
            }
        }
        Replan();
    }

    internal static DupLine ToLine(DupStep s, IReadOnlyDictionary<long, string> labels)
    {
        List<string> lines = [];
        switch (s.Action)
        {
            case DupAction.CreateParent:
                lines.Add($"From base view: {BaseLabel(s)}");
                break;
            case DupAction.CreateDependent when s.ByYou:
                lines.Add($"Dependent of \"{s.ParentName}\"" + (s.ParentStep >= 0 ? ", made in this run" : ", already in the model"));
                break;
            case DupAction.CreateDependent:
                if (!s.ParentFromSisters)   // its note says where it goes instead
                    lines.Add(s.ParentStep >= 0 ? "Dependent of its Overall, made in this run" : "Dependent of its Overall, already in the model");
                break;
        }

        string? template = s.Template switch
        {
            TemplatePlan.Apply => $"Template: {s.TemplateName}",
            TemplatePlan.AlreadySet => $"Template: {s.TemplateName} (already set)",
            TemplatePlan.None when s.Creates => "Template: none",
            TemplatePlan.FromParent when s.Action is DupAction.CreateDependent or DupAction.Update
                => $"Template: {(s.TemplateName.Length > 0 ? s.TemplateName : "none")} (a dependent shows its parent's)",
            _ => null,
        };
        if (template is not null)
            lines.Add(template);
        if (s.MirrorFromParent && s.Action == DupAction.Update)
            lines.Add("Copies " + string.Join(" / ", DuplicatorEngine.MirrorParams) + " from its Overall");
        lines.AddRange(s.Notes);

        bool warn = s.Template == TemplatePlan.NotFound || s.TemplateKind.Length > 0 || s.RenamedFrom is not null || s.TemplateNotShown;
        string mine = s.ByYou ? " (your choice)" : "";
        (string action, string tone) = s.Action switch
        {
            DupAction.CreateParent when s.IsAutomatic => ("create (automatic)", warn ? "warn" : "ok"),
            DupAction.CreateParent => ((ViewNaming.HasOverallToken(s.Name) && !s.ByYou ? "create Overall" : "create view") + mine, warn ? "warn" : "ok"),
            DupAction.CreateDependent => ("create dependent" + mine, warn ? "warn" : "ok"),
            DupAction.Update => ((s.Writes ? "update" : "up to date") + mine, warn ? "warn" : s.Writes ? "ok" : "muted"),
            DupAction.Exists => ("exists - left alone" + mine, "muted"),
            DupAction.Repeat => ("repeat - ignored", "warn"),
            DupAction.Skipped => ("skipped (your choice)", "muted"),
            _ => ("error" + mine, "bad"),
        };

        return new DupLine
        {
            Row = s.ExcelRow?.ToString() ?? "auto",
            View = s.Name.Length > 0 ? s.Name : "(blank)",
            Action = action,
            Tone = tone,
            NeedsAttention = tone is "warn" or "bad",
            Details = string.Join("\n", lines),
            Step = s,
        };

        string BaseLabel(DupStep st)
            => labels.TryGetValue(st.BaseViewId, out string? l) ? l : $"view {st.BaseViewId}";
    }

    /// <summary>View and Details share the width the list has, so nothing hides behind a sideways scroll.</summary>
    private void FitColumns()
    {
        GridViewColumnCollection cols = ((GridView)Preview.View).Columns;
        double free = Preview.ActualWidth - cols[0].Width - cols[2].Width - SystemParameters.VerticalScrollBarWidth - 12;
        if (free < 300)
            return;
        cols[1].Width = Math.Floor(free * 0.46);
        cols[3].Width = Math.Floor(free * 0.54);
    }

    private static ListView MakeList()
    {
        GridView gv = new();
        gv.Columns.Add(Column("Row", nameof(DupLine.Row), 46, wrap: false));
        gv.Columns.Add(Column("View", nameof(DupLine.View), 330, wrap: true));
        gv.Columns.Add(ActionColumn());
        gv.Columns.Add(Column("Details", nameof(DupLine.Details), 380, wrap: true));

        Style item = new(typeof(ListViewItem));
        item.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        item.Setters.Add(new Setter(Control.VerticalContentAlignmentProperty, VerticalAlignment.Top));
        item.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(2, 3, 2, 3)));

        return new ListView { View = gv, ItemContainerStyle = item, MinHeight = 140 };
    }

    private static GridViewColumn Column(string header, string path, double width, bool wrap)
    {
        FrameworkElementFactory text = new(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new Binding(path));
        text.SetValue(TextBlock.TextWrappingProperty, wrap ? TextWrapping.Wrap : TextWrapping.NoWrap);
        if (!wrap)
            text.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        return new GridViewColumn { Header = header, Width = width, CellTemplate = new DataTemplate { VisualTree = text } };
    }

    private static readonly Brush Good = new SolidColorBrush(Color.FromRgb(0x1E, 0x7B, 0x34));
    private static readonly Brush Warn = new SolidColorBrush(Color.FromRgb(0x9A, 0x60, 0x00));
    private static readonly Brush Bad = new SolidColorBrush(Color.FromRgb(0xB3, 0x26, 0x1E));

    private static GridViewColumn ActionColumn()
    {
        FrameworkElementFactory text = new(typeof(TextBlock));
        text.Name = "t";
        text.SetBinding(TextBlock.TextProperty, new Binding(nameof(DupLine.Action)));
        text.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        text.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
        DataTemplate template = new() { VisualTree = text };
        foreach ((string tone, Brush brush) in new[] { ("ok", Good), ("warn", Warn), ("bad", Bad), ("muted", SystemColors.GrayTextBrush) })
        {
            DataTrigger trigger = new() { Binding = new Binding(nameof(DupLine.Tone)), Value = tone };
            trigger.Setters.Add(new Setter(TextBlock.ForegroundProperty, brush, "t"));
            template.Triggers.Add(trigger);
        }
        return new GridViewColumn { Header = "Action", Width = 130, CellTemplate = template };
    }

    // ------------------------------------------------------------------ run

    /// <summary>What to remember for next time: the file, the tab, the mode and every level's pick.</summary>
    internal DuplicatorSettings SettingsToSave()
    {
        DuplicatorSettings s = new()
        {
            FilePath = _loadedPath,
            SheetName = _loadedSheet,
            UpdateExisting = Mode == ExistingMode.Update,
            LevelMap = new Dictionary<string, string>(_savedLevelMap),
            RowChoices = new Dictionary<string, string>(_savedChoices),
        };
        foreach (KeyValuePair<string, ComboBox> kv in LevelPickers)
        {
            if (kv.Value.SelectedItem is ComboBoxItem { Tag: DupView v })
                s.LevelMap[kv.Key] = v.Label;
        }
        return s;
    }

    private void Accept()
    {
        // The worksheet may have been saved again in Excel since it was read.
        // Run what is on disk NOW only if it is what the list shows.
        if (Plan is null)
            return;
        string shown = Fingerprint(Plan);
        Load();
        if (Plan is null || Plan.Counts.Actionable == 0)
            return;
        if (Fingerprint(Plan) != shown)
        {
            SetProblem("The worksheet changed since this list was shown (saved again in Excel?). "
                       + "The list is now up to date - check it, then run again.");
            return;
        }
        DialogResult = true;
    }

    private static string Fingerprint(DupPlan plan)
        => string.Join("\u001f", plan.Steps.Select(s => $"{s.ExcelRow}|{s.Action}|{s.Name}|{s.TemplateId}|{s.BaseViewId}|{s.ParentStep}|{s.ParentId}"));
}
