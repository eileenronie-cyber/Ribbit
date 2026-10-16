using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Autodesk.Revit.DB;
using Ribbit.Geometry.Sheets;
using Ribbit.Native.Revit;
using Ribbit.Native.Settings;
// Autodesk.Revit.DB has a Grid too (the datum kind); the WPF one wins here.
using Grid = System.Windows.Controls.Grid;

namespace Ribbit.Native.Ui;

internal sealed record SheetMakerChoice(
    string FilePath,
    string DataSheet,
    string MappingSheet,
    List<FamilySymbol> Titleblocks,
    bool UpdateExisting,
    bool BlankAsSpacer,
    bool DryRun,
    string TitleblockColumn,
    Dictionary<string, string> Mapping,
    string DefaultTitleblock,
    Dictionary<string, string> TitleblockBySheet);

/// <summary>
/// SheetMaker's input form — the eight fields that drove the Python bundle onto
/// IronPython in the first place (its CPython3 engine had no WPFWindow). That
/// constraint is gone here; this is plain WPF.
///
/// The form plans the workbook as you fill it in and shows the outcome in a band
/// that never scrolls away. Every field here feeds those counts, so the answer to
/// "did I pick the right worksheet" is on screen rather than a dry run and a
/// report window away.
/// </summary>
internal sealed class SheetMakerWindow : Window
{
    private readonly TextBox _filePath;
    private readonly ComboBox _dataSheet;
    private readonly ComboBox _mappingSheet;
    private readonly TitleblockAssigner _assigner;
    private readonly List<string> _titleblockNames;
    private readonly CheckBox _updateExisting;
    private readonly CheckBox _blankSpacer;
    private readonly CheckBox _dryRun;
    private readonly TextBlock _workbookStatus;
    private readonly TextBlock _previewSummary;
    private readonly TextBlock _previewDetail;
    private readonly TextBlock _previewProblem;
    private readonly Button _run;

    private readonly Dictionary<string, FamilySymbol> _symbolByName = [];
    private readonly HashSet<string> _existingSheetNumbers;

    /// <summary>Checked out by somebody else, so this run must leave them alone.</summary>
    private readonly HashSet<string> _lockedSheetNumbers;

    private readonly ComboBox _titleblockColumn;
    private readonly MappingEditor _mappingEditor;

    /// <summary>
    /// Held so their headers can carry a count. Tabs hide what they contain, so
    /// "Mapping (10)" is what stops a tabbed form from also hiding the fact that
    /// a mapping exists at all.
    /// </summary>
    private readonly TabItem _mappingTab;
    private readonly TabItem _titleblockTab;

    /// <summary>
    /// The model's writable parameters, for the editor's left-hand dropdown.
    /// Empty when the caller could not say (the render harness, chiefly).
    /// </summary>
    private readonly IReadOnlyList<SheetParameterInfo> _catalog;

    /// <summary>
    /// Null when the model could not answer, which switches the check off rather
    /// than reporting every mapped parameter as missing.
    /// </summary>
    private readonly HashSet<string>? _sheetParameters;

    /// <summary>
    /// The workbook behind the last successful read, and the (path, data,
    /// mapping) it was read for. Toggling an option re-plans off this rather
    /// than reopening the zip.
    /// </summary>
    private string _loadedKey = "";
    private List<XlsxRow> _dataRows = [];
    private Dictionary<string, string> _mapping = [];
    private string? _loadError;

    /// <summary>
    /// Suppresses preview work while the worksheet dropdowns are being
    /// repopulated — assigning ItemsSource resets the selection and would
    /// otherwise re-plan the workbook once per combo, mid-rebuild.
    /// </summary>
    private bool _suspendPreview;

    /// <summary>
    /// Blocks preview work until the whole window exists.
    ///
    /// Populating a control fires its own change events, and those handlers
    /// reach for controls further down the constructor that have not been built
    /// yet — the titleblock default's SelectionChanged reached the Run button
    /// and threw a NullReferenceException before the form could open. Distinct
    /// from <see cref="_suspendPreview"/>, which is released and re-taken during
    /// normal use and so cannot answer "is the window finished?".
    /// </summary>
    private bool _built;

    public SheetMakerChoice? Result { get; private set; }

    public SheetMakerWindow(
        IReadOnlyList<FamilySymbol> titleblockSymbols,
        SheetMakerSettings saved,
        IReadOnlyCollection<string> existingSheetNumbers,
        IReadOnlyList<SheetParameterInfo>? catalog = null,
        IReadOnlyCollection<string>? lockedSheetNumbers = null)
    {
        _existingSheetNumbers = [.. existingSheetNumbers];
        _catalog = catalog ?? [];
        _lockedSheetNumbers = new HashSet<string>(
            lockedSheetNumbers ?? [], StringComparer.OrdinalIgnoreCase);

        // An empty catalog means "this model could not say", which cannot
        // distinguish a good mapping from a bad one - so treat it as unknown
        // rather than reporting every mapped parameter as missing.
        _sheetParameters = _catalog.Count > 0
            ? SheetParameterCatalog.WritableNames(_catalog)
            : null;

        Title = "SheetMaker - Create/Update Sheets from Excel";
        Width = 680;
        Height = 720;
        MinWidth = 560;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = false;
        new WindowInteropHelper(this).Owner =
            System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;

        // Four tabs, one per decision. The form outgrew a single scrolling
        // column once the mapping editor arrived: a ten-row mapping pushed the
        // titleblock list and the options off the bottom, so choosing a
        // titleblock meant scrolling past the whole mapping to reach it.
        StackPanel panel = FormParts.TabPanel();          // Source
        StackPanel mappingPanel = FormParts.TabPanel();
        StackPanel titleblockPanel = FormParts.TabPanel();
        StackPanel optionsPanel = FormParts.TabPanel();

        // --- workbook -------------------------------------------------------
        panel.Children.Add(FormParts.Section("Source workbook", first: true));
        panel.Children.Add(FormParts.FieldLabel("Excel workbook"));

        (Grid pathHost, TextBox pathBox) =
            FormParts.PlaceholderInput("Path to a .xlsx or .xlsm workbook...", saved.FilePath);

        _filePath = pathBox;
        _filePath.LostFocus += (_, _) =>
        {
            _filePath.Text = SheetMakerSettings.CleanPath(_filePath.Text);
            RefreshSheetNames(_filePath.Text);
            RefreshPreview();
        };

        Button browse = new() { Content = "Browse..." };
        browse.Click += (_, _) => Browse();
        panel.Children.Add(FormParts.BrowseRow(pathHost, browse));

        // Reading the tab names is deliberately silent (see RefreshSheetNames),
        // but silence about WHY the dropdowns are empty is not the same thing as
        // not interrupting.
        _workbookStatus = FormParts.Status();
        panel.Children.Add(_workbookStatus);

        // --- worksheets -----------------------------------------------------
        // Both stay editable: picking a real tab is the default, but a
        // not-yet-existing name still works.
        panel.Children.Add(FormParts.Section("Worksheets"));

        panel.Children.Add(FormParts.FieldLabel("Data worksheet"));
        panel.Children.Add(FormParts.Hint("One row per sheet to create."));
        _dataSheet = Combo(saved.DataSheet);
        panel.Children.Add(_dataSheet);

        panel.Children.Add(FormParts.FieldLabel("Mapping worksheet (optional)"));
        panel.Children.Add(FormParts.Hint(
            "Two columns: Revit Parameter | Excel Column. Only used to seed the mapping below - "
            + "leave it blank and build the mapping here instead."));
        _mappingSheet = Combo(saved.MappingSheet);
        panel.Children.Add(_mappingSheet);

        // --- mapping ---------------------------------------------------------
        mappingPanel.Children.Add(FormParts.Section("Parameter mapping", first: true));
        mappingPanel.Children.Add(FormParts.Hint(
            "Which Excel column fills which Revit parameter. The left list is read from the "
            + "model - sheet parameters and the titleblock's own - and the right from your Data "
            + "worksheet headers, so neither has to be typed from memory."));

        _mappingEditor = new MappingEditor(RefreshPreview);
        mappingPanel.Children.Add(_mappingEditor.Content);

        Button importMapping = FormParts.Secondary("Load from MAPPING worksheet");
        importMapping.ToolTip = "Replace the mapping above with the workbook's own MAPPING sheet.";
        importMapping.Click += (_, _) => ImportMappingSheet();
        mappingPanel.Children.Add(importMapping);

        // --- titleblocks ----------------------------------------------------
        titleblockPanel.Children.Add(FormParts.Section("Titleblocks", first: true));

        List<string> names = [];
        HashSet<string> seen = [];
        foreach (FamilySymbol sym in titleblockSymbols)
        {
            string name = SheetMakerEngine.TitleblockDisplayName(sym);
            if (!seen.Add(name))
                name = $"{name} (id {sym.Id})";   // disambiguate duplicates
            seen.Add(name);
            names.Add(name);
            _symbolByName[name] = sym;
        }

        // Sorted, so a long titleblock list is scannable and the order the
        // selection will be applied in is predictable before you select anything.
        names.Sort(NaturalSort.Comparer);

        _titleblockNames = names;

        _assigner = new TitleblockAssigner(RefreshPreview);
        _assigner.SetTypes(names, saved.DefaultTitleblock);
        titleblockPanel.Children.Add(_assigner.Content);

        titleblockPanel.Children.Add(FormParts.Divider());
        titleblockPanel.Children.Add(FormParts.FieldLabel("Titleblock column (optional)"));
        titleblockPanel.Children.Add(FormParts.Hint(
            "Only if your Data worksheet already names a titleblock per row. Anything assigned "
            + "above wins over this; a name that matches nothing is reported as an error rather "
            + "than guessed at."));
        _titleblockColumn = Combo(saved.TitleblockColumn);
        titleblockPanel.Children.Add(_titleblockColumn);

        // --- options --------------------------------------------------------
        optionsPanel.Children.Add(FormParts.Section("Options", first: true));

        _updateExisting = Check("Update sheets that already exist (otherwise skip them)",
            saved.UpdateExisting);
        _blankSpacer = Check("Treat a blank Sheet Number as a spacer row, not an error",
            saved.BlankAsSpacer);
        _dryRun = Check("Dry run - report what would happen, change nothing", saved.DryRun);

        // The first two change what the counts mean, so the band must follow
        // them; the third changes what the button promises to do.
        _updateExisting.Click += (_, _) => RefreshPreview();
        _blankSpacer.Click += (_, _) => RefreshPreview();
        _dryRun.Click += (_, _) => RefreshRunButton();

        optionsPanel.Children.Add(_updateExisting);
        optionsPanel.Children.Add(_blankSpacer);
        optionsPanel.Children.Add(_dryRun);

        // --- preview band + buttons -----------------------------------------
        // Docked outside the ScrollViewer: this is the form's answer to "what
        // will this do", and it is worthless if it scrolls out of sight while
        // you are changing the fields that drive it.
        _previewSummary = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 3),
        };
        _previewDetail = FormParts.Hint("");
        _previewDetail.Margin = new Thickness(0, 0, 0, 0);
        _previewProblem = FormParts.Problem();
        _previewProblem.Margin = new Thickness(0, 3, 0, 0);

        StackPanel bandContent = new();
        bandContent.Children.Add(_previewSummary);
        bandContent.Children.Add(_previewDetail);
        bandContent.Children.Add(_previewProblem);

        Border band = new()
        {
            Background = SystemColors.ControlLightBrush,
            BorderBrush = SystemColors.ControlDarkBrush,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(FormParts.Gutter, 10, FormParts.Gutter, 10),
            Child = bandContent,
        };

        _run = FormParts.Primary("Create sheets");
        _run.Click += (_, _) => Accept();

        Border buttons = new()
        {
            Padding = new Thickness(FormParts.Gutter, 0, FormParts.Gutter, FormParts.Gutter),
            Child = FormParts.ButtonBar(_run, FormParts.Secondary("Cancel", true)),
        };

        StackPanel footer = new();
        footer.Children.Add(band);
        footer.Children.Add(buttons);

        _mappingTab = FormParts.Tab("Mapping", mappingPanel);
        _titleblockTab = FormParts.Tab("Titleblocks", titleblockPanel);

        TabControl tabs = new() { Margin = new Thickness(FormParts.Gutter, 12, FormParts.Gutter, 0) };
        tabs.Items.Add(FormParts.Tab("Source", panel));
        tabs.Items.Add(_mappingTab);
        tabs.Items.Add(_titleblockTab);
        tabs.Items.Add(FormParts.Tab("Options", optionsPanel));

        DockPanel root = new();
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        root.Children.Add(tabs);
        Content = root;

        // Every control now exists, so handlers may safely reach across the form.
        _built = true;

        RefreshSheetNames(SheetMakerSettings.CleanPath(_filePath.Text));

        // A saved mapping wins; otherwise the workbook's MAPPING sheet seeds it,
        // so an existing workbook opens with its mapping already in place and
        // nobody has to import anything to get back to where they were.
        if (saved.Mapping.Count > 0)
            _mappingEditor.Load(saved.Mapping);

        RefreshPreview();

        if (_mappingEditor.Count == 0 && _mapping.Count > 0)
        {
            _mappingEditor.Load(_mapping);
            RefreshPreview();
        }
    }

    private ComboBox Combo(string text)
    {
        ComboBox combo = new()
        {
            IsEditable = true,
            Text = text,
            Padding = new Thickness(4, 3, 4, 3),
            Margin = new Thickness(0, 0, 0, 12),
        };

        combo.SelectionChanged += (_, _) => RefreshPreview();
        combo.LostFocus += (_, _) => RefreshPreview();
        return combo;
    }

    private static CheckBox Check(string text, bool isChecked) => new()
    {
        Content = text,
        IsChecked = isChecked,
        Margin = new Thickness(0, 0, 0, 8),
    };

    /// <summary>
    /// Populate the worksheet dropdowns from the workbook's own tab names, so
    /// the user picks a real tab instead of typing one from memory.
    ///
    /// Deliberately silent when the file can't be read yet — nothing picked, or
    /// the path is mid-edit. This is a convenience, not a validation gate; Run
    /// still validates for real. (The Python's throwing overload shadowed its
    /// silent one here, so tabbing out of a half-typed path raised inside this
    /// very handler.)
    /// </summary>
    private void RefreshSheetNames(string filePath)
    {
        List<string> names = XlsxWorkbook.TryListSheetNames(filePath);

        _suspendPreview = true;
        try
        {
            foreach (ComboBox combo in new[] { _dataSheet, _mappingSheet })
            {
                string current = combo.Text;
                combo.ItemsSource = names;
                combo.Text = current;      // a typed-but-not-listed name survives
            }
        }
        finally
        {
            _suspendPreview = false;
        }

        _workbookStatus.Text = filePath.Length == 0
            ? "No workbook chosen yet - the worksheet lists fill in once you pick one."
            : names.Count > 0
                ? $"{names.Count} worksheet(s) found."
                : "Could not read any worksheets from that path - check it exists and is a "
                  + ".xlsx/.xlsm workbook. You can still type a worksheet name by hand.";
    }

    private void Browse()
    {
        Microsoft.Win32.OpenFileDialog dlg = new()
        {
            Filter = "Excel Workbooks (*.xlsx;*.xlsm)|*.xlsx;*.xlsm|All Files (*.*)|*.*",
            Title = "Select Excel Workbook",
        };

        string current = SheetMakerSettings.CleanPath(_filePath.Text);
        if (current.Length > 0)
        {
            string? folder = Path.GetDirectoryName(current);
            if (folder is not null && Directory.Exists(folder))
                dlg.InitialDirectory = folder;
        }

        if (dlg.ShowDialog() == true)
        {
            _filePath.Text = dlg.FileName;
            RefreshSheetNames(dlg.FileName);
            RefreshPreview();
        }
    }

    /// <summary>
    /// The Data worksheet's headers as WRITTEN, for the mapping editor's column
    /// list. Every row carries the same set, so the first one answers.
    /// </summary>
    private List<string> DataHeaders() =>
        _dataRows.Count > 0 ? [.. _dataRows[0].OriginalHeaders] : [];

    /// <summary>
    /// Replaces the mapping with the workbook's own MAPPING sheet — the bridge
    /// for workbooks already in circulation.
    /// </summary>
    private void ImportMappingSheet()
    {
        if (BlockingProblem() is not null || !EnsureWorkbookLoaded())
            return;

        if (_mapping.Count == 0)
        {
            RibbitUi.Alert(
                ComboText(_mappingSheet).Length == 0
                    ? "Choose a Mapping worksheet first."
                    : $"'{ComboText(_mappingSheet)}' has no usable "
                      + "'Revit Parameter' / 'Excel Column' rows.",
                "SheetMaker");
            return;
        }

        _mappingEditor.Load(_mapping);
        RefreshPreview();
    }

    private string FilePathText() => SheetMakerSettings.CleanPath(_filePath.Text);

    private static string ComboText(ComboBox combo) => (combo.Text ?? "").Trim();

    /// <summary>
    /// The reason the form cannot be run yet, or null. Ordered the way the form
    /// is filled in, so it names the next thing to do rather than the last thing
    /// missing.
    /// </summary>
    private string? BlockingProblem()
    {
        string path = FilePathText();

        if (path.Length == 0)
            return "Choose an Excel workbook to get started.";
        if (!File.Exists(path))
            return $"No file at that path:\n{path}";
        if (ComboText(_dataSheet).Length == 0)
            return "Choose the Data worksheet - the one with a row per sheet.";

        // The mapping worksheet is no longer required: an empty mapping still
        // creates sheets with their number and name, which is a legitimate run.
        return null;
    }

    /// <summary>
    /// Reads both worksheets, at most once per (path, data, mapping). Returns
    /// false with <see cref="_loadError"/> set when the workbook cannot be read.
    /// </summary>
    private bool EnsureWorkbookLoaded()
    {
        string key = $"{FilePathText()}␟{ComboText(_dataSheet)}␟{ComboText(_mappingSheet)}";
        if (key == _loadedKey)
            return _loadError is null;

        _loadedKey = key;
        _loadError = null;
        _dataRows = [];
        _mapping = [];

        try
        {
            using XlsxWorkbook workbook = XlsxWorkbook.Open(FilePathText());
            _dataRows = workbook.ReadWorksheet(ComboText(_dataSheet));

            // Optional now. A missing MAPPING tab is no longer a failure - it
            // just means there is nothing to seed the editor from.
            string mappingSheet = ComboText(_mappingSheet);
            if (mappingSheet.Length > 0)
            {
                try
                {
                    _mapping = SheetPlanner.BuildMappingDict(workbook.ReadWorksheet(mappingSheet));
                }
                catch (SheetMakerError)
                {
                    _mapping = [];
                }
            }

            List<string> headers = DataHeaders();
            _mappingEditor.SetSources(_catalog, headers);

            // The per-sheet list is the workbook's own rows, so it follows the
            // data rather than being typed twice.
            (string numberCol, string nameCol) =
                SheetPlanner.KeyColumns(_mappingEditor.Mapping());

            _assigner.SetSheets(
            [
                .. _dataRows.Select(r => (r.Get(numberCol).Trim(), r.Get(nameCol).Trim()))
            ]);

            // The titleblock column is a DATA column, so offer the same headers.
            // It was an empty editable box, which meant knowing the column name
            // from memory - the exact thing this rework set out to remove.
            _suspendPreview = true;
            try
            {
                string current = ComboText(_titleblockColumn);
                _titleblockColumn.ItemsSource = headers;
                _titleblockColumn.Text = current;
            }
            finally
            {
                _suspendPreview = false;
            }
        }
        catch (SheetMakerError ex)
        {
            _loadError = ex.Message;
        }
        catch (Exception ex)
        {
            _loadError = $"Could not read the workbook: {ex.Message}";
        }

        return _loadError is null;
    }

    /// <summary>
    /// Re-plans the workbook against the current settings and writes the result
    /// into the band. Also decides whether Run is available at all — the button
    /// going grey with a reason beside it beats a popup after the click.
    /// </summary>
    private void RefreshPreview()
    {
        if (!_built || _suspendPreview)
            return;

        RefreshRunButton();
        RefreshTabHeaders();

        string? blocked = BlockingProblem();
        if (blocked is not null)
        {
            Show(summary: "Nothing to preview yet.", detail: blocked, problem: null);
            return;
        }

        if (!EnsureWorkbookLoaded())
        {
            Show(summary: "Nothing to preview yet.",
                 detail: "",
                 problem: _loadError);
            return;
        }

        SheetPreview preview = SheetPreviewer.Build(
            _dataRows,
            _mappingEditor.Mapping(),
            _existingSheetNumbers,
            _updateExisting.IsChecked == true ? ExistingMode.Update : ExistingMode.Skip,
            _blankSpacer.IsChecked == true,
            _sheetParameters,
            ComboText(_titleblockColumn),
            _titleblockNames,
            _lockedSheetNumbers,
            _assigner.Default,
            _assigner.Assignments());

        Show(CountsLine(preview.Counts), DetailLine(preview),
             ProblemLine(preview, ComboText(_titleblockColumn)));
    }

    /// <summary>
    /// Puts each tab's state in its header, so a tab you are not looking at can
    /// still tell you it is empty or set up.
    /// </summary>
    private void RefreshTabHeaders()
    {
        int mapped = _mappingEditor.Count;
        _mappingTab.Header = mapped == 0 ? "Mapping" : $"Mapping ({mapped})";

        // The count is the OVERRIDES, not the types available - it answers "has
        // anything here been customised", which is what a collapsed tab hides.
        int assigned = _assigner.AssignedCount;
        _titleblockTab.Header = assigned == 0 ? "Titleblocks" : $"Titleblocks ({assigned})";
    }

    private void Show(string summary, string detail, string? problem)
    {
        _previewSummary.Text = summary;
        SetLine(_previewDetail, detail);
        SetLine(_previewProblem, problem ?? "");
    }

    /// <summary>
    /// An empty band line is hidden, not left blank — otherwise the band keeps
    /// the height of its worst case and the buttons shift as you type.
    /// </summary>
    private static void SetLine(TextBlock block, string text)
    {
        block.Text = text;
        block.Visibility = text.Length > 0
            ? System.Windows.Visibility.Visible
            : System.Windows.Visibility.Collapsed;
    }

    /// <summary>
    /// Only the non-zero outcomes, so the line reads as a sentence rather than a
    /// row of zeroes to decode.
    /// </summary>
    private static string CountsLine(SheetMakerReport counts)
    {
        if (counts.Rows == 0)
            return "The Data worksheet has no rows below its header - nothing to do.";

        List<string> parts = [];
        if (counts.Create > 0) parts.Add($"{counts.Create} to create");
        if (counts.Update > 0) parts.Add($"{counts.Update} to update");
        if (counts.Skip > 0) parts.Add($"{counts.Skip} skipped (already exist)");
        if (counts.Spacer > 0) parts.Add($"{counts.Spacer} spacer");
        if (counts.Locked > 0) parts.Add($"{counts.Locked} checked out");
        if (counts.Error > 0) parts.Add($"{counts.Error} with errors");

        return parts.Count == 0
            ? $"{counts.Rows} row(s) - nothing to do."
            : $"{counts.Rows} row(s): {string.Join(", ", parts)}.";
    }

    private static string DetailLine(SheetPreview preview) =>
        $"Sheet Number from \"{preview.NumberColumn}\", Sheet Name from \"{preview.NameColumn}\"  ·  "
        + $"{preview.MappedParameters} mapped parameter(s).";

    /// <summary>
    /// The column mismatches, which are the failure this band exists for: they
    /// do not throw, they just quietly produce blank sheets or a wall of
    /// "Sheet Number is blank".
    /// </summary>
    private static string? ProblemLine(SheetPreview preview, string titleblockColumn)
    {
        List<string> problems = [];

        if (!preview.NumberColumnPresent)
        {
            // Deliberately says what is WRONG, not what will happen: with spacers
            // on, every row goes quietly by as a spacer; with them off, every row
            // is an error. The counts line beside this already says which.
            problems.Add(
                $"The Data worksheet has no \"{preview.NumberColumn}\" column, so no sheet number "
                + "can be read from any row. Check the Sheet Number row on the Mapping worksheet.");
        }

        if (!preview.NameColumnPresent)
        {
            problems.Add(
                $"The Data worksheet has no \"{preview.NameColumn}\" column - sheets will be "
                + "created with Revit's default name.");
        }

        if (preview.MissingColumns.Count > 0)
        {
            problems.Add(
                "Mapped column(s) not on the Data worksheet, so they will be written blank: "
                + string.Join(", ", preview.MissingColumns));
        }

        if (preview.MissingParameters.Count > 0)
        {
            problems.Add(
                "Sheets in this model have no such parameter, so these will be skipped without "
                + "an error: " + string.Join(", ", preview.MissingParameters));
        }

        if (!preview.TitleblockColumnPresent)
        {
            problems.Add(
                $"The Data worksheet has no \"{titleblockColumn}\" column, so no row can name its "
                + "own titleblock - the selection above will be used for every sheet.");
        }

        if (preview.LockedSheets.Count > 0)
        {
            problems.Add(
                $"{preview.LockedSheets.Count} sheet(s) are checked out by another user and will "
                + "be left alone: " + string.Join(", ", preview.LockedSheets.Take(8))
                + (preview.LockedSheets.Count > 8 ? ", ..." : ""));
        }

        if (preview.Counts.Error > 0)
        {
            problems.Add(
                $"{preview.Counts.Error} row(s) will be reported as errors, at Excel "
                + (preview.ErrorRows.Count == 1 ? "row " : "rows ")
                + Rows(preview.ErrorRows) + ".");
        }

        return problems.Count == 0 ? null : string.Join("\n", problems);
    }

    /// <summary>
    /// Excel row numbers, capped — a workbook where every row is broken would
    /// otherwise push the band over the whole window.
    /// </summary>
    private static string Rows(IReadOnlyList<int> rows)
    {
        const int max = 12;

        if (rows.Count <= max)
            return string.Join(", ", rows);

        return string.Join(", ", rows.Take(max)) + $" and {rows.Count - max} more";
    }

    /// <summary>
    /// "Create sheets" is a lie when Dry run is ticked, and the checkbox is far
    /// enough up the form to have scrolled out of sight by the time you click.
    /// </summary>
    private void RefreshRunButton()
    {
        bool dry = _dryRun.IsChecked == true;
        _run.Content = dry ? "Dry run" : "Create sheets";
        _run.IsEnabled = BlockingProblem() is null;
        _run.ToolTip = dry
            ? "Report what would happen. Nothing in the model is changed."
            : "Create and update the sheets, then show the report.";
    }

    private void Accept()
    {
        // ALL loaded types, in the same order the names were offered in - the
        // planner returns an index into that list, so the two must agree.
        List<FamilySymbol> chosen = [];
        foreach (string name in _titleblockNames)
        {
            if (_symbolByName.TryGetValue(name, out FamilySymbol? sym))
                chosen.Add(sym);
        }

        Result = new SheetMakerChoice(
            FilePathText(),
            ComboText(_dataSheet),
            ComboText(_mappingSheet),
            chosen,
            _updateExisting.IsChecked == true,
            _blankSpacer.IsChecked == true,
            _dryRun.IsChecked == true,
            ComboText(_titleblockColumn),
            _mappingEditor.Mapping(),
            _assigner.Default,
            _assigner.Assignments());

        DialogResult = true;
    }
}
