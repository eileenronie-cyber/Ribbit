using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Autodesk.Revit.DB;
using Ribbit.Native.Revit;
using Ribbit.Native.Settings;
// Autodesk.Revit.DB has a Grid too (the datum kind); the WPF one wins here.
using Grid = System.Windows.Controls.Grid;

namespace Ribbit.Native.Ui;

internal sealed record ClashDetectChoice(
    string SetAKeyword,
    string SetBKeyword,
    List<(string Param, string Value)> SetAValues,
    List<(string Param, string Value)> SetBValues,
    List<BuiltInCategory> ForceBics,
    List<BuiltInCategory> SetAForceBics,
    // IDS, not names - see FederatedClashChoice. Anything a person reads goes
    // through ClashSetLibrary.NamesFor.
    List<string> SetAIds,
    List<string> SetBIds,
    string PadText,
    string PadValue,
    string PadUnit,
    bool DescriptionCableTrayOnly,
    string CsvFolder);

/// <summary>
/// The Clash Detect input form — host model against itself.
///
/// LAID OUT AS A PAIRING. A clash run is A against B, so the two sets sit side by
/// side rather than stacked, each headed by a large A or B the way a callout is
/// marked on a drawing. Stacked and near-identical, they read as one long form
/// and you had to check the labels to tell which was which.
///
/// THE CATEGORY TOGGLES LIVE INSIDE SET B. They only ever add to Set B — Set A
/// never receives them — but they used to sit at the very bottom of the form,
/// below the CSV folder, detached from the thing they modify. That relationship
/// existed only in a hint. Now it is where you can see it, and Set A carries a
/// one-line note saying it is keyword-only.
///
/// THE PAD CARRIES ITS UNIT. A bare number meant millimetres, a convention that
/// lived in a hint: get it wrong and "25" became a 25-FOOT pad that reports
/// essentially the whole model as clashing. The unit is now picked, not implied.
/// The two controls are still combined into the same single string the engine
/// has always parsed, so nothing downstream changes.
/// </summary>
internal sealed class ClashDetectWindow : Window
{
    private const string DialogTitle = "Clash Detect";

    /// <summary>Used only until a pad has been saved for this product.</summary>
    private const string DefaultPad = "100";

    private readonly TextBox _keywordA;
    private readonly TextBox _keywordB;
    private readonly WrapPanel _chipsA;
    private readonly WrapPanel _chipsB;
    private readonly PadField _pad;
    private readonly TextBox _csvFolder;

    /// <summary>
    /// The Description rule, no longer shown as a checkbox.
    ///
    /// It stays a stored preference and still governs both the Browse scan and
    /// the search — it simply has no control on this form any more. When rules
    /// gain a per-rule category scope, this becomes one of those instead of a
    /// window-wide switch.
    /// </summary>
    private readonly bool _trayOnly;

    private readonly SetSidePicker _setsA;
    private readonly SetSidePicker _setsB;
    private readonly TextBlock _summary;

    private readonly ClashSetLibrary _library;

    private readonly List<(string Param, string Value)> _pickedA;
    private readonly List<(string Param, string Value)> _pickedB;

    /// <summary>Takes the Description rule, because the offered list depends on it.</summary>
    private readonly Func<bool, List<ValueChoice>> _scanHost;

    public ClashDetectChoice? Result { get; private set; }

    public ClashDetectWindow(
        string savedCsvFolder,
        string savedKeywordA,
        string savedKeywordB,
        IReadOnlyList<(string Param, string Value)> savedValuesA,
        IReadOnlyList<(string Param, string Value)> savedValuesB,
        string savedPadValue,
        string savedPadUnit,
        bool savedDescriptionCableTrayOnly,
        ClashSetLibrary library,
        IEnumerable<string> savedSetANames,
        IEnumerable<string> savedSetBNames,
        Func<bool, List<ValueChoice>> scanHost)
    {
        _scanHost = scanHost;
        _library = library;
        _trayOnly = savedDescriptionCableTrayOnly;
        _pickedA = [.. savedValuesA];
        _pickedB = [.. savedValuesB];

        Title = DialogTitle;
        Width = 900;
        Height = 720;
        MinWidth = 620;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = false;
        new WindowInteropHelper(this).Owner =
            System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;

        StackPanel panel = new() { Margin = new Thickness(FormParts.Gutter) };

        // ---- the pairing ---------------------------------------------------
        panel.Children.Add(ClashFormParts.SectionWithManage(
            "What to clash against what",
            ClashFormParts.ManageSetsButton(ManageSets)));

        panel.Children.Add(FormParts.Hint(
            "Both sets are searched in this model. Comma-separate several keywords (e.g. 52 or "
            + "52,57); typed keywords match as substrings against System Type or Description. "
            + "Browse... picks from the values that actually exist, and those match exactly. An "
            + "element joins the set if either one hits."));

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

        _setsA = new SetSidePicker(library, this, "Set A", savedSetANames, RefreshSummary);
        _setsB = new SetSidePicker(library, this, "Set B", savedSetBNames, RefreshSummary);

        Border cardA = BuildSetCard("A", "First set", _keywordA, _chipsA, setA: true);
        Border cardB = BuildSetCard("B", "Second set", _keywordB, _chipsB, setA: false);

        Grid.SetColumn(cardA, 0);
        Grid.SetColumn(cardB, 2);
        sets.Children.Add(cardA);
        sets.Children.Add(cardB);
        panel.Children.Add(sets);

        // ---- clearance -----------------------------------------------------
        panel.Children.Add(FormParts.Divider());
        panel.Children.Add(FormParts.Section("Clearance"));
        panel.Children.Add(FormParts.Hint(
            "Counted as a clash when the two come within this distance. Zero finds only real "
            + "overlaps."));

        _pad = new PadField(DefaultPad, savedPadValue, savedPadUnit, RefreshSummary);
        panel.Children.Add(_pad.Row);

        // ---- output --------------------------------------------------------
        panel.Children.Add(FormParts.Divider());
        panel.Children.Add(FormParts.Section("Where the report goes"));

        (Grid folderHost, TextBox folderBox) = FormParts.PlaceholderInput(
            "Leave blank for your Documents folder...", savedCsvFolder);
        _csvFolder = folderBox;

        Button browse = new() { Content = "Browse..." };
        browse.Click += (_, _) => BrowseFolder();
        panel.Children.Add(FormParts.BrowseRow(folderHost, browse));

        panel.Children.Add(FormParts.Hint(
            "Remembered between runs; leave blank for your Documents folder. The CSV written here "
            + "is also the status ledger Set Clash Status reads."));

        // ---- read-back + actions -------------------------------------------
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

        panel.Children.Add(FormParts.ButtonBar(run, FormParts.Secondary("Cancel", true)));

        panel.Children.Add(new TextBlock
        {
            Text = "Your keywords, picked values, clearance and folder are remembered for "
                   + "next time.",
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
        RefreshSummary();
    }

    // ----------------------------------------------------------------- layout

    /// <summary>
    /// One set's card: its letter, keyword row and picked-value chips.
    ///
    /// Both cards are now identical — the force-include category list that used
    /// to hang off Set B has moved into the set library, reached from the header.
    /// </summary>
    private Border BuildSetCard(
        string letter, string name, TextBox keyword, WrapPanel chips, bool setA)
    {
        StackPanel body = new();

        // Header: the letter reads as a drawing callout, so A and B are
        // distinguishable at a glance rather than by reading their labels.
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
            Text = "host model",
            FontSize = 11,
            Foreground = SystemColors.GrayTextBrush,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };

        TextBlock title = new()
        {
            Text = name,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };

        DockPanel.SetDock(mark, Dock.Left);
        DockPanel.SetDock(role, Dock.Right);
        head.Children.Add(mark);
        head.Children.Add(role);
        head.Children.Add(title);
        body.Children.Add(head);

        // Sets first: the named, reusable thing leads, and the keyword box below
        // is the one-off. The other order made the library the afterthought.
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
        browse.Margin = new Thickness(6, 0, 0, 0);
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

    // -------------------------------------------------------------- behaviour

    private List<(string Param, string Value)> Picked(bool setA) => setA ? _pickedA : _pickedB;

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

    private bool TrayOnly => _trayOnly;

    private void ManageSets()
    {
        SetLibraryWindow dlg = new(_library, new SetScanServices(_ => _scanHost(TrayOnly))) { Owner = this };
        dlg.ShowDialog();
    }

    private void SaveAsSet(bool setA)
        => ClashFormParts.SaveAsSet(
            this, _library,
            (setA ? _keywordA : _keywordB).Text ?? "",
            Picked(setA),
            new SetScanServices(_ => _scanHost(TrayOnly)),
            DialogTitle);

    private void PickValues(bool setA)
    {
        List<ValueChoice> choices;
        try
        {
            choices = _scanHost(TrayOnly);
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
    /// One side's inputs, with its chosen sets folded in.
    ///
    /// A set is keywords, values and categories — exactly what the builder
    /// already takes — so choosing sets adds no new kind of query. Everything
    /// ORs, which is the single rule this whole design rests on.
    /// </summary>
    private (string Keyword, List<(string Param, string Value)> Values, List<BuiltInCategory> Bics)
        SideInputs(bool setA)
    {
        SetSidePicker sets = setA ? _setsA : _setsB;

        // Joined with commas because that is how the search layer splits a
        // keyword box; a keyword cannot meaningfully contain one.
        List<string> keywords = [];
        string typed = ((setA ? _keywordA : _keywordB).Text ?? "").Trim();
        if (typed.Length > 0)
            keywords.Add(typed);

        keywords.AddRange(sets.Keywords());

        List<(string Param, string Value)> values = [.. Picked(setA)];
        HashSet<(string, string)> seen =
            [.. values.Select(v => (v.Param.ToLowerInvariant(), v.Value.ToLowerInvariant()))];

        foreach ((string param, string value) in sets.Values())
        {
            if (seen.Add((param.ToLowerInvariant(), value.ToLowerInvariant())))
                values.Add((param, value));
        }

        return (string.Join(",", keywords), values, sets.Categories());
    }

    private int TickedToggleCount() => _setsB.Categories().Count;

    /// <summary>
    /// Says what is about to run, in one line, as it is typed.
    ///
    /// A clash pass on a large model is a long wait, and finding out afterwards
    /// that Set B was empty because a keyword was never typed is the expensive
    /// kind of mistake.
    /// </summary>
    private void RefreshSummary()
    {
        static string Describe(string keyword, int picked, int categories)
        {
            List<string> parts = [];

            if (keyword.Trim().Length > 0)
                parts.Add($"keyword \"{keyword.Trim()}\"");
            if (picked > 0)
                parts.Add($"{picked} picked value{(picked == 1 ? "" : "s")}");
            if (categories > 0)
                parts.Add($"{categories} categor{(categories == 1 ? "y" : "ies")}");

            return parts.Count == 0 ? "nothing yet" : string.Join(" + ", parts);
        }

        // Reads the MERGED inputs, so the line reflects what will actually run
        // rather than only what is typed in the boxes.
        (string kwA, List<(string, string)> vA, List<BuiltInCategory> cA) = SideInputs(setA: true);
        (string kwB, List<(string, string)> vB, List<BuiltInCategory> cB) = SideInputs(setA: false);

        string a = Describe(kwA, vA.Count, cA.Count);
        string b = Describe(kwB, vB.Count, cB.Count);

        _summary.Text = $"Set A: {a}     Set B: {b}     Pad: {_pad.Text}";
    }

    // ------------------------------------------------------------------ accept

    private void Accept()
    {
        (string kwA, List<(string Param, string Value)> valuesA, List<BuiltInCategory> bicsA) =
            SideInputs(setA: true);
        (string kwB, List<(string Param, string Value)> valuesB, List<BuiltInCategory> bicsB) =
            SideInputs(setA: false);

        if (kwA.Length == 0 && valuesA.Count == 0 && bicsA.Count == 0)
        {
            RibbitUi.Alert(
                "Set A is empty - choose a set, type a keyword, or pick exact values with Browse.",
                DialogTitle);
            return;
        }

        if (kwB.Length == 0 && valuesB.Count == 0 && bicsB.Count == 0)
        {
            RibbitUi.Alert(
                "Set B is empty - choose a set, type a keyword, or pick exact values with Browse.",
                DialogTitle);
            return;
        }

        // A chosen set that has since been deleted contributes nothing. Said
        // once, before the run, rather than leaving an unexplained short result.
        List<string> missing = [.. _setsA.MissingIds(), .. _setsB.MissingIds()];
        if (missing.Count > 0
            && !RibbitUi.Confirm(
                $"{missing.Count} chosen set{(missing.Count == 1 ? " is" : "s are")} no longer in "
                + "the library and will contribute nothing:\n\n"
                + string.Join("\n", missing.Select(m => $"  {m}"))
                + "\n\nRun anyway?",
                DialogTitle))
            return;

        Result = new ClashDetectChoice(
            kwA, kwB, valuesA, valuesB, bicsB, bicsA,
            _setsA.ChosenIds(), _setsB.ChosenIds(),
            _pad.Text, _pad.Value, _pad.Unit, TrayOnly,
            ClashFormParts.ResolveFolder(_csvFolder.Text));

        DialogResult = true;
    }
}
