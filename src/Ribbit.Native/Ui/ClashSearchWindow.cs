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

internal sealed record ClashSearchChoice(
    string Keyword,
    List<(string Param, string Value)> PickedValues,
    Dictionary<RevitLinkInstance, List<Category>> LinkCategories,
    List<BuiltInCategory> SetAForceBics,
    // IDS, not names - see FederatedClashChoice. Anything a person reads goes
    // through ClashSetLibrary.NamesFor.
    List<string> SetAIds,
    string PadText,
    string PadValue,
    string PadUnit,
    bool DescriptionCableTrayOnly,
    string CsvFolder);

/// <summary>
/// The clash search input form — host MEP against linked model categories.
///
/// Set B is chosen as (link, category) PAIRS rather than one flat list, so
/// "three categories from one link" and "different categories from two links"
/// are both expressible — the search unions every combination independently.
/// Only Set A gets a value picker; Set B is chosen by category, so it has no
/// free-text values to pick from.
///
/// Each pair is its own removable chip. The old form offered one all-or-nothing
/// "Clear Set B", so dropping a single wrong category meant rebuilding the whole
/// list; a category list of 40-odd entries makes that a real cost.
/// </summary>
internal sealed class ClashSearchWindow : Window
{
    private const string DialogTitle = "Clash Detection";

    /// <summary>
    /// Used only until a pad has been saved. Zero, unlike the two Clash Detect
    /// tools' 100 — structure is meant to be touched by nothing, so a clearance
    /// pad here reports the whole model.
    /// </summary>
    private const string DefaultPad = "0";

    private readonly TextBox _keyword;
    private readonly PadField _pad;
    private readonly TextBox _csvFolder;

    /// <summary>
    /// The Description rule, no longer shown as a checkbox. Still a stored
    /// preference, and still governs both the Browse scan and the search.
    /// </summary>
    private readonly bool _trayOnly;
    private readonly ListBox _links;
    private readonly ListBox _categories;
    private readonly TextBox _categoryFilter;
    private readonly WrapPanel _chipsA;
    private readonly StackPanel _pairHost;
    private readonly TextBlock _pairCount;
    private readonly TextBlock _summary;

    private readonly List<RevitLinkInstance> _linkList;
    private readonly List<Category> _categoryList;
    /// <summary>Takes the Description rule, because the offered list depends on it.</summary>
    private readonly Func<bool, List<ValueChoice>> _scanValues;

    private readonly ClashSetLibrary _library;
    private readonly SetSidePicker _setsA;

    private readonly Dictionary<RevitLinkInstance, List<Category>> _pairs = [];
    private readonly List<(string Param, string Value)> _picked;

    public ClashSearchChoice? Result { get; private set; }

    public ClashSearchWindow(
        IReadOnlyList<RevitLinkInstance> links,
        IReadOnlyList<Category> categories,
        string savedCsvFolder,
        string savedKeyword,
        IReadOnlyList<(string Param, string Value)> savedValues,
        string savedPadValue,
        string savedPadUnit,
        bool savedDescriptionCableTrayOnly,
        ClashSetLibrary library,
        IEnumerable<string> savedSetANames,
        Func<bool, List<ValueChoice>> scanValues)
    {
        _linkList = [.. links];
        _categoryList = [.. categories];
        _scanValues = scanValues;
        _library = library;
        _trayOnly = savedDescriptionCableTrayOnly;
        _picked = [.. savedValues];

        Title = "Clash Detection - MEP vs Structure";
        Width = 960;
        Height = 780;
        MinWidth = 660;
        MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = false;
        new WindowInteropHelper(this).Owner =
            System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;

        StackPanel panel = new() { Margin = new Thickness(FormParts.Gutter) };

        panel.Children.Add(ClashFormParts.SectionWithManage(
            "What to clash against what",
            ClashFormParts.ManageSetsButton(ManageSets)));

        panel.Children.Add(FormParts.Hint(
            "Set A is your host MEP, found by keyword. Set B is whole categories out of the "
            + "linked models. Every A element is tested against every B element."));

        Grid sets = new() { Margin = new Thickness(0, 0, 0, 4) };
        sets.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        sets.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        sets.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.3, GridUnitType.Star) });

        _keyword = FormParts.Input(savedKeyword);
        _keyword.TextChanged += (_, _) => RefreshSummary();
        _chipsA = FormParts.ChipHost();

        // Set A only. Set B here is (link, category) pairs against the LINKED
        // model's categories - a different universe from a host MEP value set.
        _setsA = new SetSidePicker(library, this, "Set A", savedSetANames, RefreshSummary);

        _links = new ListBox
        {
            Height = 150,
            FontSize = 12,
            ItemsSource = links.Select(ClashLinks.DisplayName).ToList(),
        };

        _categories = new ListBox
        {
            Height = 150,
            FontSize = 12,
            SelectionMode = SelectionMode.Extended,
            ItemsSource = categories.Select(c => c.Name).ToList(),
        };

        (Grid filterHost, TextBox filterBox) = FormParts.PlaceholderInput("Filter categories...");
        _categoryFilter = filterBox;
        _categoryFilter.TextChanged += (_, _) => ApplyCategoryFilter();

        _pairHost = new StackPanel();
        _pairCount = new TextBlock
        {
            FontSize = 11,
            Foreground = SystemColors.GrayTextBrush,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };

        Border cardA = BuildSetACard();
        Border cardB = BuildSetBCard(filterHost);

        Grid.SetColumn(cardA, 0);
        Grid.SetColumn(cardB, 2);
        sets.Children.Add(cardA);
        sets.Children.Add(cardB);
        panel.Children.Add(sets);

        // ---- clearance -----------------------------------------------------
        panel.Children.Add(FormParts.Divider());
        panel.Children.Add(FormParts.Section("Clearance"));
        panel.Children.Add(FormParts.Hint(
            "Counted as a clash when the two come within this distance. Zero means only real "
            + "overlaps count."));

        _pad = new PadField(DefaultPad, savedPadValue, savedPadUnit, RefreshSummary);
        panel.Children.Add(_pad.Row);

        // ---- output --------------------------------------------------------
        panel.Children.Add(FormParts.Divider());
        panel.Children.Add(FormParts.Section("Where the report goes"));

        (Grid folderHost, TextBox folderBox) = FormParts.PlaceholderInput(
            "Leave blank for your Documents folder...", savedCsvFolder);
        _csvFolder = folderBox;

        Button browseFolder = new() { Content = "Browse..." };
        browseFolder.Click += (_, _) => BrowseFolder();
        panel.Children.Add(FormParts.BrowseRow(folderHost, browseFolder));

        panel.Children.Add(FormParts.Hint(
            "CSV folder - reports and the status ledger are written here."));

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
            Text = "Your keyword, picked values, clearance and folder are remembered for "
                   + "next time. Set B is not - links belong to this model.",
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

        RefreshChips();
        RefreshPairs();
        RefreshSummary();
    }

    // ----------------------------------------------------------------- layout

    private static DockPanel SetHead(string letter, string name, UIElement role)
    {
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

        return head;
    }

    private static Border Card(UIElement body) => new()
    {
        BorderBrush = SystemColors.ControlDarkBrush,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(4),
        Padding = new Thickness(13, 12, 13, 13),
        Child = body,
    };

    private static Border Rule() => new()
    {
        BorderBrush = SystemColors.ControlDarkBrush,
        BorderThickness = new Thickness(0, 1, 0, 0),
        Opacity = 0.4,
        Margin = new Thickness(0, 10, 0, 9),
    };

    private Border BuildSetACard()
    {
        StackPanel body = new();

        body.Children.Add(SetHead("A", "Host MEP", new TextBlock
        {
            Text = "this model",
            FontSize = 11,
            Foreground = SystemColors.GrayTextBrush,
            VerticalAlignment = VerticalAlignment.Center,
        }));

        body.Children.Add(_setsA.Panel);
        body.Children.Add(Rule());

        body.Children.Add(FormParts.FieldLabel("Or type keywords - for a one-off"));

        Button browse = new()
        {
            Content = "Browse...",
            MinWidth = 78,
            MinHeight = 26,
            Margin = new Thickness(6, 0, 0, 0),
        };
        browse.Click += (_, _) => PickValues();

        // Set A had no Clear at all: a picked value could only be dropped by
        // reopening Browse and un-ticking it there.
        Button clear = new()
        {
            Content = "Clear",
            MinWidth = 58,
            MinHeight = 26,
            Margin = new Thickness(6, 0, 0, 0),
        };
        clear.Click += (_, _) =>
        {
            _picked.Clear();
            RefreshChips();
            RefreshSummary();
        };

        DockPanel row = new() { Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(clear, Dock.Right);
        DockPanel.SetDock(browse, Dock.Right);
        row.Children.Add(clear);
        row.Children.Add(browse);
        row.Children.Add(_keyword);
        body.Children.Add(row);

        body.Children.Add(FormParts.Hint(
            "Matched on System Type or Description, case-insensitive substring. "
            + "Comma-separate several keywords, or use Browse to pick exact values."));

        body.Children.Add(FormParts.FieldLabel("Picked values - exact match"));
        body.Children.Add(_chipsA);
        body.Children.Add(ClashFormParts.SaveAsSetButton(SaveAsSet));

        body.Children.Add(FormParts.Hint(
            "Picked values are ORed with the keyword above - an element joins Set A if either "
            + "one hits."));

        return Card(body);
    }

    private Border BuildSetBCard(UIElement filterHost)
    {
        StackPanel body = new();
        body.Children.Add(SetHead("B", "Linked categories", _pairCount));

        Grid pickers = new() { Margin = new Thickness(0, 0, 0, 8) };
        pickers.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        pickers.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        StackPanel leftCol = new() { Margin = new Thickness(0, 0, 6, 0) };
        leftCol.Children.Add(FormParts.FieldLabel("Link"));
        leftCol.Children.Add(_links);
        Grid.SetColumn(leftCol, 0);

        StackPanel rightCol = new() { Margin = new Thickness(6, 0, 0, 0) };
        rightCol.Children.Add(FormParts.FieldLabel("Categories (multi-select)"));
        rightCol.Children.Add(filterHost);
        rightCol.Children.Add(new Border { Height = 5 });
        rightCol.Children.Add(_categories);
        Grid.SetColumn(rightCol, 1);

        pickers.Children.Add(leftCol);
        pickers.Children.Add(rightCol);
        body.Children.Add(pickers);

        Button add = new()
        {
            Content = "Add to Set B",
            MinWidth = 130,
            MinHeight = 28,
            HorizontalAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(10, 4, 10, 4),
            Margin = new Thickness(0, 0, 0, 4),
        };
        add.Click += (_, _) => AddPair();
        body.Children.Add(add);

        body.Children.Add(FormParts.Hint(
            "Pick a link and one or more categories, then Add. Repeat for as many "
            + "link/category combinations as you need."));

        body.Children.Add(Rule());

        DockPanel pairHead = new() { Margin = new Thickness(0, 0, 0, 4) };
        Button clearAll = new()
        {
            Content = "Clear all",
            MinWidth = 74,
            MinHeight = 24,
            FontSize = 11,
            Padding = new Thickness(8, 2, 8, 2),
        };
        clearAll.Click += (_, _) =>
        {
            _pairs.Clear();
            RefreshPairs();
            RefreshSummary();
        };

        DockPanel.SetDock(clearAll, Dock.Right);
        pairHead.Children.Add(clearAll);
        pairHead.Children.Add(FormParts.FieldLabel("In Set B"));
        body.Children.Add(pairHead);

        body.Children.Add(new Border
        {
            BorderBrush = SystemColors.ControlDarkBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            Background = SystemColors.WindowBrush,
            Padding = new Thickness(8, 6, 8, 6),
            Child = new ScrollViewer
            {
                MaxHeight = 128,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = _pairHost,
            },
        });

        return Card(body);
    }

    // -------------------------------------------------------------- behaviour

    /// <summary>
    /// Narrows the category list to what matches the filter box.
    ///
    /// A structural model routinely offers 40-plus categories; scrolling to
    /// "Structural Framing" past every Analytical entry was the slowest part of
    /// setting up a run. Selections survive the filter — the underlying
    /// (link, category) pairs are already committed by Add.
    /// </summary>
    private void ApplyCategoryFilter()
    {
        string needle = (_categoryFilter.Text ?? "").Trim();

        _categories.ItemsSource = needle.Length == 0
            ? _categoryList.Select(c => c.Name).ToList()
            : _categoryList
                .Where(c => c.Name.Contains(needle, StringComparison.OrdinalIgnoreCase))
                .Select(c => c.Name)
                .ToList();
    }

    private void RefreshChips()
    {
        _chipsA.Children.Clear();

        if (_picked.Count == 0)
        {
            _chipsA.Children.Add(
                FormParts.EmptyChipNote("None picked - typed keywords only."));
            return;
        }

        foreach ((string param, string value) in _picked)
        {
            (string Param, string Value) entry = (param, value);

            _chipsA.Children.Add(FormParts.Chip(param, value, () =>
            {
                _picked.Remove(entry);
                RefreshChips();
                RefreshSummary();
            }));
        }
    }

    private bool TrayOnly => _trayOnly;

    private void ManageSets()
    {
        SetLibraryWindow dlg = new(_library, new SetScanServices(_ => _scanValues(TrayOnly))) { Owner = this };
        dlg.ShowDialog();
    }

    /// <summary>
    /// Set A only. Set B here is (link, category) pairs against the LINKED
    /// model's categories — a different universe from a host MEP value set, so
    /// there is nothing meaningful to save from it.
    /// </summary>
    private void SaveAsSet()
        => ClashFormParts.SaveAsSet(
            this, _library, _keyword.Text ?? "", _picked,
            new SetScanServices(_ => _scanValues(TrayOnly)), DialogTitle);

    private void PickValues()
    {
        List<ValueChoice> choices;
        try
        {
            choices = _scanValues(TrayOnly);
        }
        catch (Exception ex)
        {
            RibbitUi.Alert($"Could not scan the host model:\n\n{ex.Message}", DialogTitle);
            return;
        }

        if (choices.Count == 0)
        {
            RibbitUi.Alert(
                "No System Type or Description values found on the host MEP elements.",
                DialogTitle);
            return;
        }

        List<string> labels =
        [
            .. choices.Select(c => $"[{c.ParamName}] {c.Display}   ({c.Count})")
        ];

        List<string>? chosen = RibbitUi.SelectManyFromList(
            labels, "Pick exact Set A values", "Use these");

        if (chosen is null)
            return;

        _picked.Clear();
        HashSet<string> chosenSet = [.. chosen];
        for (int i = 0; i < labels.Count; i++)
        {
            if (chosenSet.Contains(labels[i]))
                _picked.Add(choices[i].Pair);
        }

        RefreshChips();
        RefreshSummary();
    }

    private void AddPair()
    {
        int linkIndex = _links.SelectedIndex;
        if (linkIndex < 0)
        {
            RibbitUi.Alert("Pick a link first.", DialogTitle);
            return;
        }

        List<Category> chosen = [];
        foreach (object item in _categories.SelectedItems)
        {
            if (item is string name)
            {
                Category? cat = _categoryList.FirstOrDefault(c => c.Name == name);
                if (cat is not null)
                    chosen.Add(cat);
            }
        }

        if (chosen.Count == 0)
        {
            RibbitUi.Alert("Pick at least one category.", DialogTitle);
            return;
        }

        RevitLinkInstance link = _linkList[linkIndex];
        if (!_pairs.TryGetValue(link, out List<Category>? existing))
            _pairs[link] = existing = [];

        // Merge, never duplicate — adding the same category twice must not make
        // the search run it twice.
        foreach (Category c in chosen)
        {
            if (!existing.Any(e => e.Id == c.Id))
                existing.Add(c);
        }

        RefreshPairs();
        RefreshSummary();
    }

    /// <summary>Drops one (link, category) pair, and the link with its last one.</summary>
    private void RemovePair(RevitLinkInstance link, Category category)
    {
        if (!_pairs.TryGetValue(link, out List<Category>? cats))
            return;

        cats.RemoveAll(c => c.Id == category.Id);
        if (cats.Count == 0)
            _pairs.Remove(link);

        RefreshPairs();
        RefreshSummary();
    }

    private int PairCount() => _pairs.Sum(kv => kv.Value.Count);

    private void RefreshPairs()
    {
        _pairHost.Children.Clear();

        int total = PairCount();
        _pairCount.Text = total == 0
            ? "nothing added"
            : $"{total} pair{(total == 1 ? "" : "s")}";

        if (total == 0)
        {
            _pairHost.Children.Add(FormParts.EmptyChipNote(
                "Nothing added yet - pick a link and category above, then Add to Set B."));
            return;
        }

        // Grouped by link, because that is how the search runs it: one link,
        // its categories, then the next link.
        foreach (KeyValuePair<RevitLinkInstance, List<Category>> kv in _pairs)
        {
            RevitLinkInstance link = kv.Key;

            _pairHost.Children.Add(new TextBlock
            {
                Text = ClashLinks.DisplayName(link),
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = SystemColors.GrayTextBrush,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 4, 0, 3),
            });

            WrapPanel row = FormParts.ChipHost();

            foreach (Category c in kv.Value)
            {
                Category captured = c;
                row.Children.Add(
                    FormParts.Chip("", captured.Name, () => RemovePair(link, captured)));
            }

            _pairHost.Children.Add(row);
        }
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
    /// Set A's inputs, with its chosen sets folded in. Set B is unaffected — it
    /// is (link, category) pairs, not a value search.
    /// </summary>
    private (string Keyword, List<(string Param, string Value)> Values, List<BuiltInCategory> Bics)
        SetAInputs()
    {
        List<string> keywords = [];
        string typed = (_keyword.Text ?? "").Trim();
        if (typed.Length > 0)
            keywords.Add(typed);

        keywords.AddRange(_setsA.Keywords());

        List<(string Param, string Value)> values = [.. _picked];
        HashSet<(string, string)> seen =
            [.. values.Select(v => (v.Param.ToLowerInvariant(), v.Value.ToLowerInvariant()))];

        foreach ((string param, string value) in _setsA.Values())
        {
            if (seen.Add((param.ToLowerInvariant(), value.ToLowerInvariant())))
                values.Add((param, value));
        }

        return (string.Join(",", keywords), values, _setsA.Categories());
    }

    private void RefreshSummary()
    {
        (string keyword, List<(string, string)> values, List<BuiltInCategory> bics) = SetAInputs();

        List<string> aBits = [];
        if (keyword.Length > 0)
            aBits.Add($"keyword \"{keyword}\"");
        if (values.Count > 0)
            aBits.Add($"{values.Count} value{(values.Count == 1 ? "" : "s")}");
        if (bics.Count > 0)
            aBits.Add($"{bics.Count} categor{(bics.Count == 1 ? "y" : "ies")}");

        string a = aBits.Count == 0 ? "nothing yet" : string.Join(" + ", aBits);

        int total = PairCount();
        string b = total == 0
            ? "nothing yet"
            : $"{total} pair{(total == 1 ? "" : "s")} across "
              + $"{_pairs.Count} link{(_pairs.Count == 1 ? "" : "s")}";

        _summary.Text = $"Set A: {a}     Set B: {b}     Pad: {_pad.Text}";
    }

    // ------------------------------------------------------------------ accept

    private void Accept()
    {
        (string keyword, List<(string Param, string Value)> values, List<BuiltInCategory> bics) =
            SetAInputs();

        if (keyword.Length == 0 && values.Count == 0 && bics.Count == 0)
        {
            RibbitUi.Alert(
                "Set A is empty - choose a set, type a keyword, or pick exact values with Browse.",
                DialogTitle);
            return;
        }

        if (_pairs.Count == 0)
        {
            RibbitUi.Alert(
                "Set B is empty - add at least one link/category combination.",
                DialogTitle);
            return;
        }

        List<string> missing = _setsA.MissingIds();
        if (missing.Count > 0
            && !RibbitUi.Confirm(
                $"{missing.Count} chosen set{(missing.Count == 1 ? " is" : "s are")} no longer in "
                + "the library and will contribute nothing:\n\n"
                + string.Join("\n", missing.Select(m => $"  {m}"))
                + "\n\nRun anyway?",
                DialogTitle))
            return;

        Result = new ClashSearchChoice(
            keyword, values,
            _pairs.ToDictionary(kv => kv.Key, kv => kv.Value),
            bics, _setsA.ChosenIds(),
            _pad.Text, _pad.Value, _pad.Unit, TrayOnly,
            ClashFormParts.ResolveFolder(_csvFolder.Text));

        DialogResult = true;
    }
}
