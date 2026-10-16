using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Autodesk.Revit.DB;
using Ribbit.Geometry.Clash;
using Ribbit.Native.Revit;
using Ribbit.Native.Settings;
// Autodesk.Revit.DB has a Grid too (the datum kind); the WPF one wins here.
using Grid = System.Windows.Controls.Grid;

namespace Ribbit.Native.Ui;

internal sealed record FederatedClashChoice(
    List<ClashSource> Sources,
    string SetAKeyword,
    string SetBKeyword,
    List<(string Param, string Value)> SetAValues,
    List<(string Param, string Value)> SetBValues,
    List<BuiltInCategory> SetABics,
    List<BuiltInCategory> SetBBics,
    // IDS, not names. The library's whole promise is that a set survives being
    // renamed, which only holds while the id is what gets carried. Anything a
    // person reads must go through ClashSetLibrary.NamesFor - a field called
    // "names" holding ids is how a report came to print a column of hex.
    List<string> SetAIds,
    List<string> SetBIds,
    string PadText,
    string PadValue,
    string PadUnit,
    string ToleranceText,
    string ToleranceValue,
    string ToleranceUnit,
    bool DescriptionCableTrayOnly,
    string CsvFolder,
    string TestId,
    bool AllModels,

    // The RAW inputs, before the chosen sets' own keywords and values were
    // folded in. Saving a search as a test must store these, not the merged
    // ones: a test that has its sets' contents copied into its own fields stops
    // tracking those sets, so removing a value from a set no longer removes it
    // from the test. The merged forms above are what the SEARCH uses; these are
    // what the test REMEMBERS.
    string SetAKeywordTyped,
    string SetBKeywordTyped,
    List<(string Param, string Value)> SetAPicked,
    List<(string Param, string Value)> SetBPicked,

    // HOW each side is built: the pieces FederatedSides.Compose made, one per
    // set that leaves worksets out plus one for everything else. The merged
    // fields above describe the same side folded flat; these are what search.
    List<SidePart> SetAParts,
    List<SidePart> SetBParts);

/// <summary>
/// Federated Clash — both sides search the host model AND every ticked link at
/// once, the way Navisworks searches an appended scene.
///
/// THE FEDERATION LIST IS ONE LIST, AT THE TOP. Not a scope dropdown per side:
/// the sources are a property of the run, and the sets decide what is selected
/// from them. Sets constrain WHAT; the federation decides WHERE.
///
/// This tool is deliberately separate from the three older clash buttons rather
/// than replacing them. It has its own product tag and therefore its own status
/// ledger, so nothing already triaged is disturbed — and that separation is what
/// let it be MODEL-scoped where the others are active-view scoped.
/// </summary>
internal sealed class FederatedClashWindow : Window
{
    private const string DialogTitle = "Federated Clash";

    /// <summary>Used only until a pad has been saved for this product.</summary>
    private const string DefaultPad = "0";

    /// <summary>
    /// Tolerance defaults to zero — every overlap reported.
    ///
    /// Deliberately not a "sensible" 5 mm: a tolerance removes clashes from the
    /// report, and a default that quietly hides some would make the first run
    /// look better than the model is.
    /// </summary>
    private const string DefaultTolerance = "0";

    private readonly TextBox _keywordA;
    private readonly TextBox _keywordB;
    private readonly WrapPanel _chipsA;
    private readonly WrapPanel _chipsB;
    private readonly PadField _pad;
    private readonly PadField _tolerance;
    private readonly TextBox _csvFolder;
    private readonly TextBlock _summary;
    private readonly TextBlock _sourceCount;

    private readonly SetSidePicker _setsA;
    private readonly SetSidePicker _setsB;
    private readonly ClashSetLibrary _library;

    private readonly List<(ClashSource Source, CheckBox Box)> _sourceBoxes = [];

    private readonly List<(string Param, string Value)> _pickedA;
    private readonly List<(string Param, string Value)> _pickedB;

    private readonly bool _trayOnly;

    private readonly ClashTestLibrary _testLibrary;
    private readonly string _modelTitle;
    private ComboBox _tests = new();
    private TextBlock _testInfo = new();
    private List<ClashTest> _testOrder = [];
    private string _activeTestId = "";

    /// <summary>
    /// Stops the dropdown's own SelectionChanged firing while the list is being
    /// rebuilt, which would otherwise re-apply a test mid-refresh.
    /// </summary>
    private bool _suppressTestSelection;

    /// <summary>
    /// Scans the ticked sources for values. Takes the Description rule, and the
    /// EXTRA categories the caller needs covered beyond the 24 MEP ones — a set
    /// being built around Structural Framing has to be able to see its values.
    /// </summary>
    private readonly Func<
        IReadOnlyList<ClashSource>, bool, IReadOnlyList<BuiltInCategory>, List<ValueChoice>> _scan;

    /// <summary>Every category in the model, so a set can name Structural Framing.</summary>
    private readonly Func<List<Category>> _allCategories;

    /// <summary>
    /// The workset audit for a set, over the models ticked RIGHT NOW. Handed to
    /// the set editor; null leaves its audit button disabled.
    /// </summary>
    private readonly Func<IReadOnlyList<ClashSource>, ClashSet, List<WorksetUsage>>? _auditWorksets;

    /// <summary>Parameter names the ticked models carry, and one parameter's values.</summary>
    private readonly Func<
        IReadOnlyList<ClashSource>, IReadOnlyList<BuiltInCategory>, List<ParameterChoice>>
        _discoverParameters;

    private readonly Func<
        IReadOnlyList<ClashSource>, string, IReadOnlyList<BuiltInCategory>, List<ValueChoice>>
        _scanParameter;

    public FederatedClashChoice? Result { get; private set; }

    /// <summary>
    /// The user asked to batch-run saved tests instead of this search.
    ///
    /// Closes with <see cref="Result"/> still null, which is why the caller has
    /// to check this FIRST: null Result normally means "cancelled", and here it
    /// means "not this search, those ones".
    /// </summary>
    public bool BatchRequested { get; private set; }

    public FederatedClashWindow(
        IReadOnlyList<ClashSource> sources,
        string savedCsvFolder,
        string savedKeywordA,
        string savedKeywordB,
        IReadOnlyList<(string Param, string Value)> savedValuesA,
        IReadOnlyList<(string Param, string Value)> savedValuesB,
        string savedPadValue,
        string savedPadUnit,
        string savedToleranceValue,
        string savedToleranceUnit,
        bool savedDescriptionCableTrayOnly,
        ClashSetLibrary library,
        ClashTestLibrary testLibrary,
        string modelTitle,
        IEnumerable<string> savedSetAIds,
        IEnumerable<string> savedSetBIds,
        Func<IReadOnlyList<ClashSource>, bool, IReadOnlyList<BuiltInCategory>, List<ValueChoice>>
            scan,
        Func<IReadOnlyList<ClashSource>, IReadOnlyList<BuiltInCategory>, List<ParameterChoice>>
            discoverParameters,
        Func<IReadOnlyList<ClashSource>, string, IReadOnlyList<BuiltInCategory>,
            List<ValueChoice>> scanParameter,
        Func<List<Category>> allCategories,
        Func<IReadOnlyList<ClashSource>, ClashSet, List<WorksetUsage>>? auditWorksets = null)
    {
        _library = library;
        _testLibrary = testLibrary;
        _modelTitle = modelTitle;
        _scan = scan;
        _discoverParameters = discoverParameters;
        _scanParameter = scanParameter;
        _allCategories = allCategories;
        _auditWorksets = auditWorksets;
        _trayOnly = savedDescriptionCableTrayOnly;
        _pickedA = [.. savedValuesA];
        _pickedB = [.. savedValuesB];

        Title = DialogTitle;
        Width = 940;
        Height = 800;
        MinWidth = 640;
        MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = false;
        new WindowInteropHelper(this).Owner =
            System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;

        StackPanel panel = new() { Margin = new Thickness(FormParts.Gutter) };

        // ---- the federation -------------------------------------------------
        _sourceCount = new TextBlock
        {
            FontSize = 11,
            Foreground = SystemColors.GrayTextBrush,
            VerticalAlignment = VerticalAlignment.Center,
        };

        // ---- the saved test -------------------------------------------------
        panel.Children.Add(BuildTestBar());
        panel.Children.Add(FormParts.Divider());

        panel.Children.Add(ClashFormParts.SectionWithManage(
            "Models to search",
            ClashFormParts.ManageSetsButton(ManageSets)));

        panel.Children.Add(FormParts.Hint(
            "Both sets are searched across every ticked model at once - host and links together. "
            + "The sets decide WHAT is selected; this list decides WHERE."));

        panel.Children.Add(BuildSourceList(sources));

        // ---- the pairing ----------------------------------------------------
        panel.Children.Add(FormParts.Divider());
        panel.Children.Add(FormParts.Section("What to clash against what"));

        Grid sets = new() { Margin = new Thickness(0, 0, 0, 4) };
        sets.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        sets.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        sets.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        _keywordA = FormParts.Input(savedKeywordA);
        _keywordA.TextChanged += (_, _) => RefreshSummary();
        _chipsA = FormParts.ChipHost();

        _keywordB = FormParts.Input(savedKeywordB);
        _keywordB.TextChanged += (_, _) => RefreshSummary();
        _chipsB = FormParts.ChipHost();

        _setsA = new SetSidePicker(library, this, "Set A", savedSetAIds, RefreshSummary);
        _setsB = new SetSidePicker(library, this, "Set B", savedSetBIds, RefreshSummary);

        Border cardA = BuildSetCard("A", "First set", _keywordA, _chipsA, setA: true);
        Border cardB = BuildSetCard("B", "Second set", _keywordB, _chipsB, setA: false);

        Grid.SetColumn(cardA, 0);
        Grid.SetColumn(cardB, 2);
        sets.Children.Add(cardA);
        sets.Children.Add(cardB);
        panel.Children.Add(sets);

        // ---- clearance ------------------------------------------------------
        panel.Children.Add(FormParts.Divider());
        panel.Children.Add(FormParts.Section("Clearance"));
        panel.Children.Add(FormParts.Hint(
            "Counted as a clash when the two come within this distance. Zero finds only real "
            + "overlaps."));

        _pad = new PadField(DefaultPad, savedPadValue, savedPadUnit, RefreshSummary);
        panel.Children.Add(_pad.Row);

        // ---- tolerance ------------------------------------------------------
        panel.Children.Add(FormParts.Divider());
        panel.Children.Add(FormParts.Section("Tolerance"));
        panel.Children.Add(FormParts.Hint(
            "Ignore overlaps shallower than this. Two elements that graze by a millimetre are "
            + "usually a modelling artefact rather than a coordination problem, and on a large "
            + "model they can be most of the report. Zero reports every overlap."));

        _tolerance = new PadField(
            DefaultTolerance, savedToleranceValue, savedToleranceUnit, RefreshSummary);
        panel.Children.Add(_tolerance.Row);

        // ---- output ---------------------------------------------------------
        panel.Children.Add(FormParts.Divider());
        panel.Children.Add(FormParts.Section("Where the report goes"));

        (Grid folderHost, TextBox folderBox) = FormParts.PlaceholderInput(
            "Leave blank for your Documents folder...", savedCsvFolder);
        _csvFolder = folderBox;

        Button browseFolder = new() { Content = "Browse..." };
        browseFolder.Click += (_, _) => BrowseFolder();
        panel.Children.Add(FormParts.BrowseRow(folderHost, browseFolder));

        panel.Children.Add(FormParts.Hint(
            "Federated Clash keeps its own report and its own status ledger, separate from the "
            + "other three clash tools."));

        // ---- read-back + actions --------------------------------------------
        panel.Children.Add(FormParts.Divider());

        _summary = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Margin = new Thickness(11, 8, 11, 8),
        };

        panel.Children.Add(new Border
        {
            Background = SystemColors.ControlLightBrush,
            BorderBrush = FormParts.Accent,
            BorderThickness = new Thickness(3, 0, 0, 0),
            Margin = new Thickness(0, 4, 0, 0),
            Child = _summary,
        });

        Button run = FormParts.Primary("Run clash detection");
        run.Click += (_, _) => Accept();

        // Batch lives HERE rather than on the ribbon. It is the saved tests that
        // get run in a batch, and this is the window the saved tests live in -
        // a fifth clash button would suggest a fifth kind of clash detection.
        Button batch = FormParts.Secondary("Run saved tests...");
        batch.ToolTip = "Run several saved tests one after another and summarise what each found.";
        batch.Click += (_, _) =>
        {
            BatchRequested = true;
            DialogResult = true;
        };

        panel.Children.Add(FormParts.ButtonBar(
            run, batch, FormParts.Secondary("Cancel", true)));

        panel.Children.Add(new TextBlock
        {
            // States the scope PLAINLY. It used to read "not just the active
            // view", written to contrast with the three older clash tools - but
            // they pass the active view only as a bounding-box FALLBACK and
            // collect model-wide too, so the contrast was never true. Measured
            // 2026-08-16.
            Text = "Searches every ticked model in full.",
            FontSize = 11,
            Foreground = SystemColors.GrayTextBrush,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 6, 0, 0),
        });

        Content = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = panel,
        };

        RefreshChips(setA: true);
        RefreshChips(setA: false);
        RefreshSourceCount();
        RefreshSummary();
    }

    // ------------------------------------------------------------- saved tests

    /// <summary>
    /// The test picker, at the very top: choose one, Save over it, Save as a new
    /// one, or Delete.
    ///
    /// Above the models and the sets because a test IS all of them — picking one
    /// rewrites everything below, and a control that rewrites the form should
    /// not be buried inside it.
    /// </summary>
    private StackPanel BuildTestBar()
    {
        StackPanel body = new();

        _tests = new ComboBox { MinHeight = 26, FontSize = 12.5 };
        _tests.SelectionChanged += (_, _) => OnTestSelected();

        Button save = new()
        {
            Content = "Save",
            MinWidth = 62,
            MinHeight = 26,
            FontSize = 11.5,
            Margin = new Thickness(6, 0, 0, 0),
            ToolTip = "Overwrite the chosen test with what is on this form",
        };
        save.Click += (_, _) => SaveTest(asNew: false);

        Button saveAs = new()
        {
            Content = "Save as...",
            MinWidth = 82,
            MinHeight = 26,
            FontSize = 11.5,
            Margin = new Thickness(6, 0, 0, 0),
            ToolTip = "Save this form as a new named test",
        };
        saveAs.Click += (_, _) => SaveTest(asNew: true);

        Button delete = new()
        {
            Content = "Delete",
            MinWidth = 66,
            MinHeight = 26,
            FontSize = 11.5,
            Margin = new Thickness(6, 0, 0, 0),
        };
        delete.Click += (_, _) => DeleteTest();

        DockPanel row = new();
        DockPanel.SetDock(delete, Dock.Right);
        DockPanel.SetDock(saveAs, Dock.Right);
        DockPanel.SetDock(save, Dock.Right);
        row.Children.Add(delete);
        row.Children.Add(saveAs);
        row.Children.Add(save);
        row.Children.Add(_tests);

        _testInfo = new TextBlock
        {
            FontSize = 11,
            Foreground = SystemColors.GrayTextBrush,
            Margin = new Thickness(0, 5, 0, 0),
        };

        body.Children.Add(FormParts.Section("Test", first: true));
        body.Children.Add(row);
        body.Children.Add(_testInfo);

        RefreshTestList(select: null);
        return body;
    }

    /// <summary>Rebuilds the dropdown, optionally selecting a test by id.</summary>
    private void RefreshTestList(string? select)
    {
        _suppressTestSelection = true;

        _testOrder = [.. _testLibrary.Tests];
        List<string> labels = ["(no test - ad hoc search)", .. _testOrder.Select(t => t.Name)];

        _tests.ItemsSource = labels;

        int index = 0;
        if (select is not null)
        {
            int found = _testOrder.FindIndex(t => t.Id == select);
            if (found >= 0)
                index = found + 1;
        }

        _tests.SelectedIndex = index;
        _activeTestId = index == 0 ? "" : _testOrder[index - 1].Id;

        _suppressTestSelection = false;
        RefreshTestInfo();
    }

    private void RefreshTestInfo()
    {
        ClashTest? test = _testLibrary.FindById(_activeTestId);

        if (test is null)
        {
            _testInfo.Text =
                "Not saved as a test. Fill the form in and use Save as... to keep it.";
            return;
        }

        string scope = test.AllModels
            ? "all loaded models"
            : $"{test.SourceNames.Count} named model(s)";

        string origin = test.ModelTitle.Length > 0
                        && !string.Equals(test.ModelTitle, _modelTitle, StringComparison.OrdinalIgnoreCase)
            ? $"  ·  built in \"{test.ModelTitle}\""
            : "";

        _testInfo.Text = $"{test.LastRunSummary()}  ·  {scope}{origin}";
    }

    private void OnTestSelected()
    {
        if (_suppressTestSelection)
            return;

        int i = _tests.SelectedIndex;

        if (i <= 0)
        {
            // "(no test)" leaves the form exactly as it is. Clearing it would
            // throw away a search someone is halfway through building.
            _activeTestId = "";
            RefreshTestInfo();
            return;
        }

        ClashTest test = _testOrder[i - 1];
        _activeTestId = test.Id;
        Apply(test);
        RefreshTestInfo();
    }

    /// <summary>Rewrites every control from a saved test.</summary>
    private void Apply(ClashTest test)
    {
        // Sources: "all models" ticks everything, so a link added since the test
        // was saved is included - which is what that mode means.
        List<string>? users = _sourceBoxes.Select(p => p.Source).FirstOrDefault(s => s.IsHost) is { } host
            ? FederatedSources.UsersFor(host)
            : null;

        foreach ((ClashSource src, CheckBox box) in _sourceBoxes)
        {
            box.IsChecked = test.AllModels
                || test.SourceNames.Any(n => FederatedSources.Matches(src, n, users));
        }

        _keywordA.Text = test.SetAKeyword;
        _keywordB.Text = test.SetBKeyword;

        _pickedA.Clear();
        _pickedA.AddRange(ClashSettings.PairsOf(test.SetAValues));
        _pickedB.Clear();
        _pickedB.AddRange(ClashSettings.PairsOf(test.SetBValues));

        _setsA.SetChosen(test.SetAIds);
        _setsB.SetChosen(test.SetBIds);

        _pad.Set(test.PadValue, test.PadUnit);
        _tolerance.Set(test.ToleranceValue, test.ToleranceUnit);

        if (test.CsvFolder.Length > 0)
            _csvFolder.Text = test.CsvFolder;

        RefreshChips(setA: true);
        RefreshChips(setA: false);
        RefreshSourceCount();
        RefreshSummary();
    }

    /// <summary>The current form as a test, ready to store.</summary>
    private ClashTest Capture(string name, string id)
    {
        List<ClashSource> ticked = TickedSources();
        bool all = ticked.Count == _sourceBoxes.Count;

        return new ClashTest
        {
            Id = id,
            Name = name,
            AllModels = all,

            // The HOST is stored NORMALISED, links by their raw name. A link's
            // name comes from the link instance and never varies with how the
            // HOST was opened, but the host's does - and storing it raw baked
            // one person's username into the file. Normalised, two people
            // pinning the same host converge on the same string, because each
            // one's Normalize strips their OWN username: FederatedSources.Matches
            // then only has to strip the CURRENT session's username to land on
            // the same canonical form, whoever saved it.
            SourceNames = all ? [] : [.. ticked.Select(s => s.IsHost ? _modelTitle : s.Name)],
            SetAIds = _setsA.ChosenIds(),
            SetBIds = _setsB.ChosenIds(),
            SetAKeyword = (_keywordA.Text ?? "").Trim(),
            SetBKeyword = (_keywordB.Text ?? "").Trim(),
            SetAValues = ClashSettings.ToStored(_pickedA),
            SetBValues = ClashSettings.ToStored(_pickedB),
            PadValue = _pad.Value,
            PadUnit = _pad.Unit,
            ToleranceValue = _tolerance.Value,
            ToleranceUnit = _tolerance.Unit,
            DescriptionCableTrayOnly = _trayOnly,
            CsvFolder = ClashFormParts.ResolveFolder(_csvFolder.Text),
            ModelTitle = _modelTitle,
        };
    }

    private void SaveTest(bool asNew)
    {
        ClashTest? existing = _testLibrary.FindById(_activeTestId);

        if (!asNew && existing is null)
        {
            RibbitUi.Alert(
                "No test is chosen. Use Save as... to create one.", DialogTitle);
            return;
        }

        string name;

        if (asNew)
        {
            TextPromptWindow prompt = new("Save as test", null, "Test name", null) { Owner = this };
            if (prompt.ShowDialog() != true)
                return;

            name = prompt.Value;
            if (name.Length == 0)
            {
                RibbitUi.Alert("Give the test a name.", DialogTitle);
                return;
            }

            if (_testLibrary.NameTaken(name))
            {
                RibbitUi.Alert($"A test called \"{name}\" already exists.", DialogTitle);
                return;
            }
        }
        else
        {
            name = existing!.Name;
        }

        ClashTest captured = Capture(
            name, asNew ? Guid.NewGuid().ToString("N") : existing!.Id);

        if (asNew)
        {
            _testLibrary.Tests.Add(captured);
        }
        else
        {
            // Overwritten in place, but the RUN HISTORY is kept: editing a
            // test's search does not make last week's run never have happened.
            captured.LastRunUtc = existing!.LastRunUtc;
            captured.LastRunClashes = existing.LastRunClashes;

            int at = _testLibrary.Tests.IndexOf(existing);
            _testLibrary.Tests[at] = captured;
        }

        _testLibrary.Save();
        RefreshTestList(select: captured.Id);
        RibbitUi.Notify($"Saved test \"{captured.Name}\".");
    }

    private void DeleteTest()
    {
        ClashTest? test = _testLibrary.FindById(_activeTestId);
        if (test is null)
        {
            RibbitUi.Alert("No test is chosen.", DialogTitle);
            return;
        }

        if (!RibbitUi.Confirm(
                $"Delete the test \"{test.Name}\"?\n\n"
                + "The sets it uses are not affected and no clash status is lost. Its record of "
                + "what it last saw goes too, so a replacement test starts without \"new\" "
                + "badges until it has run twice.",
                DialogTitle))
            return;

        _testLibrary.Tests.Remove(test);
        _testLibrary.Save();

        // The baseline would otherwise outlive the test forever: ids are never
        // reused, so nothing could ever read it again.
        //
        // THE TEST'S OWN FOLDER, not the form's. A test carries its own
        // CsvFolder and its baseline was written there - a batch of eight tests
        // can touch several folders. Reading the box instead only agreed because
        // selecting a test copies its folder into it, which stops being true the
        // moment someone edits the box, or for a test saved before it had one.
        // The mismatch left exactly the orphan this block exists to prevent.
        string folder = test.CsvFolder.Length > 0
            ? test.CsvFolder
            : ClashFormParts.ResolveFolder(_csvFolder.Text);
        SeenLedger seen = SeenLedger.Load(folder, _modelTitle, ClashProduct.Federated.ProductTag);

        if (seen.Forget(test.Id))
            seen.Save(folder, _modelTitle, ClashProduct.Federated.ProductTag);

        RefreshTestList(select: null);
    }

    // ----------------------------------------------------------------- layout

    private Border BuildSourceList(IReadOnlyList<ClashSource> sources)
    {
        StackPanel list = new();

        foreach (ClashSource src in sources)
        {
            ClashSource captured = src;

            CheckBox box = new()
            {
                // Everything on by default: this is a federated tool, and having
                // to tick five links before the first run would make the common
                // case the laborious one.
                IsChecked = true,
                FontSize = 12,
                Margin = new Thickness(0, 2, 0, 2),
                Content = captured.IsHost
                    ? $"{captured.Name}   [host model]"
                    : captured.Name,
            };

            box.Checked += (_, _) => { RefreshSourceCount(); RefreshSummary(); };
            box.Unchecked += (_, _) => { RefreshSourceCount(); RefreshSummary(); };

            list.Children.Add(box);
            _sourceBoxes.Add((captured, box));
        }

        if (_sourceBoxes.Count == 0)
        {
            list.Children.Add(FormParts.EmptyChipNote(
                "No readable models - this should not happen with a document open."));
        }

        StackPanel buttons = new()
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 6, 0, 0),
        };
        buttons.Children.Add(Small("Select all", () => SetAllSources(true)));
        buttons.Children.Add(Small("Host only", HostOnly));

        DockPanel head = new() { Margin = new Thickness(0, 0, 0, 5) };
        DockPanel.SetDock(_sourceCount, Dock.Right);
        head.Children.Add(_sourceCount);
        head.Children.Add(FormParts.FieldLabel("Host model and loaded links"));

        StackPanel body = new();
        body.Children.Add(head);
        body.Children.Add(new Border
        {
            BorderBrush = SystemColors.ControlDarkBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            Background = SystemColors.WindowBrush,
            Padding = new Thickness(8, 5, 8, 5),
            Child = new ScrollViewer
            {
                MaxHeight = 118,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = list,
            },
        });
        body.Children.Add(buttons);

        return new Border
        {
            BorderBrush = SystemColors.ControlDarkBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(13, 12, 13, 13),
            Child = body,
        };
    }

    private Border BuildSetCard(
        string letter, string name, TextBox keyword, WrapPanel chips, bool setA)
    {
        StackPanel body = new();

        DockPanel head = new() { Margin = new Thickness(0, 0, 0, 8) };

        TextBlock mark = new()
        {
            Text = letter,
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            FontSize = 19,
            FontWeight = FontWeights.Bold,
            Foreground = FormParts.Accent,
            Margin = new Thickness(0, 0, 9, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };

        TextBlock role = new()
        {
            Text = "all ticked models",
            FontSize = 11,
            Foreground = SystemColors.GrayTextBrush,
            VerticalAlignment = VerticalAlignment.Center,
        };

        DockPanel.SetDock(mark, Dock.Left);
        DockPanel.SetDock(role, Dock.Right);
        head.Children.Add(mark);
        head.Children.Add(role);
        head.Children.Add(new TextBlock
        {
            Text = name,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        });
        body.Children.Add(head);

        body.Children.Add((setA ? _setsA : _setsB).Panel);

        body.Children.Add(new Border
        {
            BorderBrush = SystemColors.ControlDarkBrush,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Opacity = 0.4,
            Margin = new Thickness(0, 11, 0, 9),
        });

        body.Children.Add(FormParts.FieldLabel("Or type keywords - for a one-off"));

        Button browse = new() { Content = "Browse...", MinWidth = 78, MinHeight = 26 };
        browse.Margin = new Thickness(6, 0, 0, 0);
        browse.Click += (_, _) => PickValues(setA);

        Button clear = new()
        {
            Content = "Clear",
            MinWidth = 58,
            MinHeight = 26,
            Margin = new Thickness(6, 0, 0, 0),
        };
        clear.Click += (_, _) => ClearPicked(setA);

        DockPanel keywordRow = new() { Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(clear, Dock.Right);
        DockPanel.SetDock(browse, Dock.Right);
        keywordRow.Children.Add(clear);
        keywordRow.Children.Add(browse);
        keywordRow.Children.Add(keyword);
        body.Children.Add(keywordRow);

        body.Children.Add(FormParts.FieldLabel("Picked values - exact match"));
        body.Children.Add(chips);
        body.Children.Add(ClashFormParts.SaveAsSetButton(() => SaveAsSet(setA)));

        return new Border
        {
            BorderBrush = SystemColors.ControlDarkBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(13, 12, 13, 13),
            Child = body,
        };
    }

    private static Button Small(string text, Action onClick)
        => FormParts.SmallButton(text, onClick);

    // -------------------------------------------------------------- behaviour

    private List<(string Param, string Value)> Picked(bool setA) => setA ? _pickedA : _pickedB;

    private List<ClashSource> TickedSources()
        => [.. _sourceBoxes.Where(p => p.Box.IsChecked == true).Select(p => p.Source)];

    internal void SetAllSources(bool state)
    {
        foreach ((_, CheckBox box) in _sourceBoxes)
            box.IsChecked = state;

        RefreshSourceCount();
        RefreshSummary();
    }

    private void HostOnly()
    {
        foreach ((ClashSource src, CheckBox box) in _sourceBoxes)
            box.IsChecked = src.IsHost;

        RefreshSourceCount();
        RefreshSummary();
    }

    private void RefreshSourceCount()
    {
        int n = TickedSources().Count;
        _sourceCount.Text = _sourceBoxes.Count == 0
            ? "none"
            : $"{n} of {_sourceBoxes.Count} ticked";
    }

    private void RefreshChips(bool setA)
    {
        WrapPanel host = setA ? _chipsA : _chipsB;
        List<(string Param, string Value)> picked = Picked(setA);

        host.Children.Clear();

        if (picked.Count == 0)
        {
            host.Children.Add(FormParts.EmptyChipNote("None picked - typed keywords only."));
            return;
        }

        foreach ((string param, string value) in picked)
        {
            (string Param, string Value) entry = (param, value);

            host.Children.Add(FormParts.Chip(param, value, () =>
            {
                Picked(setA).Remove(entry);
                RefreshChips(setA);
                RefreshSummary();
            }));
        }
    }

    private void ClearPicked(bool setA)
    {
        Picked(setA).Clear();
        RefreshChips(setA);
        RefreshSummary();
    }

    /// <summary>
    /// What the set editor may ask of the model, bound to the models ticked
    /// RIGHT NOW rather than at open — tick another link and the next browse
    /// covers it.
    /// </summary>
    internal SetScanServices Services() => new(
        Values: extra => _scan(TickedSources(), _trayOnly, extra),
        Parameters: extra => _discoverParameters(TickedSources(), extra),
        ValuesForParameter: (name, extra) => _scanParameter(TickedSources(), name, extra),
        AllCategories: _allCategories,
        WorksetAudit: _auditWorksets is null ? null : set =>
        {
            // Said as what it is. With nothing ticked the audit finds no worksets,
            // and the editor would otherwise blame worksharing for it.
            List<ClashSource> ticked = TickedSources();
            if (ticked.Count == 0)
                throw new AuditUnavailableException(
                    "No model is ticked in Federated Clash. The audit reads the ticked models - tick at "
                    + "least one and try again.");

            return _auditWorksets(ticked, set);
        });

    private void ManageSets()
    {
        SetLibraryWindow dlg = new(_library, Services())
        {
            Owner = this,
        };
        dlg.ShowDialog();
    }

    private void SaveAsSet(bool setA)
        => ClashFormParts.SaveAsSet(
            this, _library,
            (setA ? _keywordA : _keywordB).Text ?? "",
            Picked(setA),
            Services(),
            DialogTitle);

    private void PickValues(bool setA)
    {
        List<ClashSource> ticked = TickedSources();

        if (ticked.Count == 0)
        {
            RibbitUi.Alert("Tick at least one model to search first.", DialogTitle);
            return;
        }

        // Scanned with THIS SIDE's categories. The sets already chosen here are
        // part of what the side selects, so if one of them names Structural
        // Framing the values on offer have to include it - otherwise the search
        // reaches elements the picker cannot show you.
        List<BuiltInCategory> extra = SideInputs(setA).Bics;

        List<ValueChoice> choices;
        try
        {
            choices = _scan(ticked, _trayOnly, extra);
        }
        catch (Exception ex)
        {
            RibbitUi.Alert($"Couldn't scan for values:\n\n{ex.Message}", DialogTitle);
            return;
        }

        ClashFormParts.PickInto(
            choices, Picked(setA),
            $"Select values for Set {(setA ? "A" : "B")}", DialogTitle);

        RefreshChips(setA);
        RefreshSummary();
    }

    private void BrowseFolder()
    {
        Microsoft.Win32.OpenFolderDialog dlg = new() { Title = "Choose the CSV save folder" };

        string current = (_csvFolder.Text ?? "").Trim();
        if (current.Length > 0 && Directory.Exists(current))
            dlg.InitialDirectory = current;

        if (dlg.ShowDialog() == true)
            _csvFolder.Text = dlg.FolderName;
    }

    /// <summary>
    /// One side as the pieces that are searched - through <see cref="FederatedSides.Compose"/>,
    /// the same composer the batch runner uses, so a test means one thing either way.
    /// </summary>
    private List<SidePart> SideParts(bool setA)
        => FederatedSides.Compose(
            (setA ? _keywordA : _keywordB).Text,
            Picked(setA),
            (setA ? _setsA : _setsB).LiveSets());

    /// <summary>One side's inputs, with its chosen sets folded in.</summary>
    private (string Keyword, List<(string Param, string Value)> Values, List<BuiltInCategory> Bics)
        SideInputs(bool setA)
        => FederatedSides.Merge(SideParts(setA));

    private void RefreshSummary()
    {
        static string Describe(string keyword, int picked, int categories)
        {
            List<string> parts = [];

            if (keyword.Trim().Length > 0)
                parts.Add($"keyword \"{keyword.Trim()}\"");
            if (picked > 0)
                parts.Add($"{picked} value{(picked == 1 ? "" : "s")}");
            if (categories > 0)
                parts.Add($"{categories} categor{(categories == 1 ? "y" : "ies")}");

            return parts.Count == 0 ? "nothing yet" : string.Join(" + ", parts);
        }

        (string kwA, List<(string, string)> vA, List<BuiltInCategory> cA) = SideInputs(setA: true);
        (string kwB, List<(string, string)> vB, List<BuiltInCategory> cB) = SideInputs(setA: false);

        int n = TickedSources().Count;

        _summary.Text =
            $"{n} model{(n == 1 ? "" : "s")}     "
            + $"Set A: {Describe(kwA, vA.Count, cA.Count)}     "
            + $"Set B: {Describe(kwB, vB.Count, cB.Count)}     Pad: {_pad.Text}"
            + (_tolerance.Value is "0" or "" ? "" : $"     Tolerance: {_tolerance.Text}");
    }

    // ------------------------------------------------------------------ accept

    private void Accept()
    {
        List<ClashSource> ticked = TickedSources();

        (string kwA, List<(string Param, string Value)> valuesA, List<BuiltInCategory> bicsA) =
            SideInputs(setA: true);
        (string kwB, List<(string Param, string Value)> valuesB, List<BuiltInCategory> bicsB) =
            SideInputs(setA: false);

        if (ticked.Count == 0)
        {
            RibbitUi.Alert("Tick at least one model to search.", DialogTitle);
            return;
        }

        if (kwA.Length == 0 && valuesA.Count == 0 && bicsA.Count == 0)
        {
            RibbitUi.Alert(
                "Set A is empty - choose a set, type a keyword, or pick exact values.",
                DialogTitle);
            return;
        }

        if (kwB.Length == 0 && valuesB.Count == 0 && bicsB.Count == 0)
        {
            RibbitUi.Alert(
                "Set B is empty - choose a set, type a keyword, or pick exact values.",
                DialogTitle);
            return;
        }

        List<string> missing = [.. _setsA.MissingIds(), .. _setsB.MissingIds()];
        if (missing.Count > 0
            && !RibbitUi.Confirm(
                $"{missing.Count} chosen set{(missing.Count == 1 ? " is" : "s are")} no longer in "
                + "the library and will contribute nothing:\n\n"
                + string.Join("\n", missing.Select(m => $"  {m}"))
                + "\n\nRun anyway?",
                DialogTitle))
            return;

        Result = new FederatedClashChoice(
            ticked, kwA, kwB, valuesA, valuesB, bicsA, bicsB,
            _setsA.ChosenIds(), _setsB.ChosenIds(),
            _pad.Text, _pad.Value, _pad.Unit,
            _tolerance.Text, _tolerance.Value, _tolerance.Unit,
            _trayOnly,
            ClashFormParts.ResolveFolder(_csvFolder.Text),
            _activeTestId,
            ticked.Count == _sourceBoxes.Count,
            (_keywordA.Text ?? "").Trim(),
            (_keywordB.Text ?? "").Trim(),
            [.. Picked(setA: true)],
            [.. Picked(setA: false)],
            SideParts(setA: true),
            SideParts(setA: false));

        DialogResult = true;
    }
}
