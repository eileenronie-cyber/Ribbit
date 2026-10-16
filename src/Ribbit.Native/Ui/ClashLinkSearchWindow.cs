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

internal sealed record ClashLinkSearchChoice(
    string SetAKeyword,
    string SetBKeyword,
    List<(string Param, string Value)> SetAValues,
    List<(string Param, string Value)> SetBValues,
    List<RevitLinkInstance> SelectedLinks,
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
/// The Clash Detect Link input form.
///
/// BOTH sets are keyword searches — Set A over the host document, Set B inside
/// whichever links are checked.
///
/// THE LINK LIST SITS AT THE TOP OF SET B, above its keyword box. Set B's
/// Browse... scans the CHECKED links, so it has nothing to offer until some are
/// ticked; the old layout put the list below the keyword and covered the gap
/// with a hint reading "tick them first". Ordering the panel the way the work
/// has to happen makes that structural instead of instructional.
///
/// The category toggles feed Set B only, never Set A — which is why they live
/// inside Set B's card rather than at the bottom of the form.
/// </summary>
internal sealed class ClashLinkSearchWindow : Window
{
    private const string DialogTitle = "Clash Detect Link";

    /// <summary>Used only until a pad has been saved for this product.</summary>
    private const string DefaultPad = "100";

    private readonly TextBox _keywordA;
    private readonly TextBox _keywordB;
    private readonly WrapPanel _chipsA;
    private readonly WrapPanel _chipsB;
    private readonly PadField _pad;
    private readonly TextBox _csvFolder;

    /// <summary>
    /// The Description rule, no longer shown as a checkbox. Still a stored
    /// preference, and still governs both the Browse scan and the search.
    /// </summary>
    private readonly bool _trayOnly;

    private readonly SetSidePicker _setsA;
    private readonly SetSidePicker _setsB;
    private readonly TextBlock _summary;
    private readonly TextBlock _linkCount;

    private readonly List<(RevitLinkInstance Link, CheckBox Box)> _linkBoxes = [];
    private readonly ClashSetLibrary _library;

    private readonly List<(string Param, string Value)> _pickedA;
    private readonly List<(string Param, string Value)> _pickedB;

    /// <summary>
    /// Host choices, and the link picker keyed by linked document. Both take the
    /// Description rule, because the offered list depends on it.
    /// </summary>
    private readonly Func<bool, List<ValueChoice>> _scanHost;
    private readonly Func<IReadOnlyList<Document>, bool, List<ValueChoice>> _scanLinks;

    public ClashLinkSearchChoice? Result { get; private set; }

    public ClashLinkSearchWindow(
        IReadOnlyList<RevitLinkInstance> links,
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
        Func<bool, List<ValueChoice>> scanHost,
        Func<IReadOnlyList<Document>, bool, List<ValueChoice>> scanLinks)
    {
        _scanHost = scanHost;
        _scanLinks = scanLinks;
        _library = library;
        _trayOnly = savedDescriptionCableTrayOnly;
        _pickedA = [.. savedValuesA];
        _pickedB = [.. savedValuesB];

        Title = DialogTitle;
        Width = 940;
        Height = 780;
        MinWidth = 640;
        MinHeight = 500;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = false;
        new WindowInteropHelper(this).Owner =
            System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;

        StackPanel panel = new() { Margin = new Thickness(FormParts.Gutter) };

        panel.Children.Add(ClashFormParts.SectionWithManage(
            "What to clash against what",
            ClashFormParts.ManageSetsButton(ManageSets)));

        panel.Children.Add(FormParts.Hint(
            "Comma-separate several keywords (e.g. 52 or 52,57). Typed keywords match as "
            + "substrings against System Type or Description. Browse... picks from the values that "
            + "actually exist - Set A from the host model, Set B from the link(s) checked above it "
            + "- and those match exactly. An element joins the set if either one hits."));

        Grid sets = new() { Margin = new Thickness(0, 0, 0, 4) };
        sets.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        sets.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        sets.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.15, GridUnitType.Star) });

        _keywordA = FormParts.Input(savedKeywordA);
        _keywordA.TextChanged += (_, _) => RefreshSummary();
        _chipsA = FormParts.ChipHost();

        _keywordB = FormParts.Input(savedKeywordB);
        _keywordB.TextChanged += (_, _) => RefreshSummary();
        _chipsB = FormParts.ChipHost();

        _linkCount = new TextBlock
        {
            FontSize = 11,
            Foreground = SystemColors.GrayTextBrush,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };

        _setsA = new SetSidePicker(library, this, "Set A", savedSetANames, RefreshSummary);
        _setsB = new SetSidePicker(library, this, "Set B", savedSetBNames, RefreshSummary);

        Border cardA = BuildSetACard();
        Border cardB = BuildSetBCard(links);

        Grid.SetColumn(cardA, 0);
        Grid.SetColumn(cardB, 2);
        sets.Children.Add(cardA);
        sets.Children.Add(cardB);
        panel.Children.Add(sets);

        // ---- clearance -----------------------------------------------------
        panel.Children.Add(FormParts.Divider());
        panel.Children.Add(FormParts.Section("Clearance"));
        panel.Children.Add(FormParts.Hint(
            "Counted as a clash when the two come within this distance."));

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
            "Remembered between runs; leave blank for your Documents folder. The CSV written here "
            + "is also the status ledger Set Status Link reads."));

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
                   + "next time. Checked links are not - they belong to this model.",
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
        RefreshLinkCount();
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

    private DockPanel KeywordRow(TextBox box, bool setA)
    {
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

        DockPanel row = new() { Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(clear, Dock.Right);
        DockPanel.SetDock(browse, Dock.Right);
        row.Children.Add(clear);
        row.Children.Add(browse);
        row.Children.Add(box);
        return row;
    }

    private static Border Card(UIElement body) => new()
    {
        BorderBrush = SystemColors.ControlDarkBrush,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(4),
        Padding = new Thickness(13, 12, 13, 13),
        Child = body,
    };

    private Border BuildSetACard()
    {
        StackPanel body = new();

        body.Children.Add(SetHead("A", "First set", new TextBlock
        {
            Text = "host model",
            FontSize = 11,
            Foreground = SystemColors.GrayTextBrush,
            VerticalAlignment = VerticalAlignment.Center,
        }));

        body.Children.Add(_setsA.Panel);
        body.Children.Add(Rule());

        body.Children.Add(FormParts.FieldLabel("Or type keywords - for a one-off"));
        body.Children.Add(KeywordRow(_keywordA, setA: true));
        body.Children.Add(FormParts.FieldLabel("Picked values - exact match"));
        body.Children.Add(_chipsA);
        body.Children.Add(ClashFormParts.SaveAsSetButton(() => SaveAsSet(setA: true)));

        return Card(body);
    }

    private Border BuildSetBCard(IReadOnlyList<RevitLinkInstance> links)
    {
        StackPanel body = new();
        body.Children.Add(SetHead("B", "Second set", _linkCount));

        // The link list comes FIRST - Browse below it scans whatever is ticked
        // here, so the order of the panel is the order of the work.
        body.Children.Add(FormParts.FieldLabel("Revit links to search"));

        StackPanel linkPanel = new();

        if (links.Count > 0)
        {
            foreach (RevitLinkInstance li in links)
            {
                string status = ClashLinks.LoadStatus(li);
                bool unloaded = !status.StartsWith("loaded", StringComparison.OrdinalIgnoreCase);

                CheckBox box = new()
                {
                    Margin = new Thickness(0, 2, 0, 2),
                    FontSize = 12,
                    // An unloaded link is present, tickable and contributes
                    // nothing - the commonest cause of a mysteriously empty Set
                    // B. Dimming it says so before the run rather than after.
                    Opacity = unloaded ? 0.55 : 1.0,
                    Content = $"{ClashLinks.DisplayName(li)}   [{status}]",
                    ToolTip = unloaded
                        ? "This link is not loaded, so it can contribute no elements."
                        : null,
                };

                box.Checked += (_, _) => { RefreshLinkCount(); RefreshSummary(); };
                box.Unchecked += (_, _) => { RefreshLinkCount(); RefreshSummary(); };

                linkPanel.Children.Add(box);
                _linkBoxes.Add((li, box));
            }
        }
        else
        {
            linkPanel.Children.Add(new TextBlock
            {
                Text = "No Revit Links loaded in this model.",
                FontSize = 12,
                FontStyle = FontStyles.Italic,
                Foreground = SystemColors.GrayTextBrush,
            });
        }

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
                Content = linkPanel,
            },
        });

        StackPanel linkButtons = new()
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 6, 0, 0),
        };
        linkButtons.Children.Add(SmallButton("Select all", () => SetAllLinks(true)));
        linkButtons.Children.Add(SmallButton("Clear all", () => SetAllLinks(false)));
        body.Children.Add(linkButtons);

        body.Children.Add(Rule());

        body.Children.Add(_setsB.Panel);
        body.Children.Add(Rule());

        body.Children.Add(FormParts.FieldLabel("Or type keywords - for a one-off"));
        body.Children.Add(KeywordRow(_keywordB, setA: false));
        body.Children.Add(FormParts.FieldLabel("Picked values - exact match"));
        body.Children.Add(_chipsB);
        body.Children.Add(ClashFormParts.SaveAsSetButton(() => SaveAsSet(setA: false)));

        return Card(body);
    }

    private static Border Rule() => new()
    {
        BorderBrush = SystemColors.ControlDarkBrush,
        BorderThickness = new Thickness(0, 1, 0, 0),
        Opacity = 0.4,
        Margin = new Thickness(0, 10, 0, 9),
    };

    private static Button SmallButton(string text, Action onClick)
        => FormParts.SmallButton(text, onClick);

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

    private void SetAllLinks(bool state)
    {
        foreach ((_, CheckBox box) in _linkBoxes)
            box.IsChecked = state;

        RefreshLinkCount();
        RefreshSummary();
    }

    private List<RevitLinkInstance> CheckedLinks()
        => [.. _linkBoxes.Where(p => p.Box.IsChecked == true).Select(p => p.Link)];

    /// <summary>Loaded documents behind the checked links, for the Set B picker.</summary>
    private List<Document> CheckedLinkDocuments()
    {
        List<Document> docs = [];

        foreach (RevitLinkInstance li in CheckedLinks())
        {
            try
            {
                Document? ld = li.GetLinkDocument();
                if (ld is not null)
                    docs.Add(ld);
            }
            catch (Exception)
            {
                // An unreadable link simply offers no values.
            }
        }

        return docs;
    }

    private void RefreshLinkCount()
        => _linkCount.Text = _linkBoxes.Count == 0
            ? "no links"
            : $"{CheckedLinks().Count} of {_linkBoxes.Count} links";

    private bool TrayOnly => _trayOnly;

    private void ManageSets()
    {
        SetLibraryWindow dlg = new(_library, new SetScanServices(_ => _scanHost(TrayOnly))) { Owner = this };
        dlg.ShowDialog();
    }

    /// <summary>
    /// Set B's values come from the CHECKED LINKS, so its picker scans those.
    /// Set A's come from the host. The set saved is the same either way — a
    /// value set is just strings and does not remember where it was found.
    /// </summary>
    private void SaveAsSet(bool setA)
        => ClashFormParts.SaveAsSet(
            this, _library,
            (setA ? _keywordA : _keywordB).Text ?? "",
            Picked(setA),
            new SetScanServices(setA
                ? _ => _scanHost(TrayOnly)
                : _ => _scanLinks(CheckedLinkDocuments(), TrayOnly)),
            DialogTitle);

    private void PickValues(bool setA)
    {
        List<ValueChoice> choices;

        try
        {
            if (setA)
            {
                choices = _scanHost(TrayOnly);
            }
            else
            {
                List<Document> linkDocs = CheckedLinkDocuments();
                if (linkDocs.Count == 0)
                {
                    RibbitUi.Alert(
                        "Check one or more loaded Revit Links first, then Browse picks Set B "
                        + "values from those link(s).",
                        DialogTitle);
                    return;
                }

                choices = _scanLinks(linkDocs, TrayOnly);
            }
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
    /// already takes — so choosing sets adds no new kind of query.
    /// </summary>
    private (string Keyword, List<(string Param, string Value)> Values, List<BuiltInCategory> Bics)
        SideInputs(bool setA)
    {
        SetSidePicker sets = setA ? _setsA : _setsB;

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

    private void RefreshSummary()
    {
        static string Parts(params string[] bits)
        {
            List<string> kept = [.. bits.Where(b => b.Length > 0)];
            return kept.Count == 0 ? "nothing yet" : string.Join(" + ", kept);
        }

        // Reads the MERGED inputs, so the line reflects what will actually run.
        (string kwA, List<(string, string)> vA, List<BuiltInCategory> cA) = SideInputs(setA: true);
        (string kwB, List<(string, string)> vB, List<BuiltInCategory> cB) = SideInputs(setA: false);

        string a = Parts(
            kwA.Length > 0 ? $"keyword \"{kwA}\"" : "",
            vA.Count > 0 ? $"{vA.Count} value{(vA.Count == 1 ? "" : "s")}" : "",
            cA.Count > 0 ? $"{cA.Count} categor{(cA.Count == 1 ? "y" : "ies")}" : "");

        int linkCount = CheckedLinks().Count;

        string b = Parts(
            linkCount > 0 ? $"{linkCount} link{(linkCount == 1 ? "" : "s")}" : "no links",
            kwB.Length > 0 ? $"keyword \"{kwB}\"" : "",
            vB.Count > 0 ? $"{vB.Count} value{(vB.Count == 1 ? "" : "s")}" : "",
            cB.Count > 0 ? $"{cB.Count} categor{(cB.Count == 1 ? "y" : "ies")}" : "");

        _summary.Text = $"Set A: {a}     Set B: {b}     Pad: {_pad.Text}";
    }

    // ------------------------------------------------------------------ accept

    private void Accept()
    {
        (string kwA, List<(string Param, string Value)> valuesA, List<BuiltInCategory> bicsA) =
            SideInputs(setA: true);
        (string kwB, List<(string Param, string Value)> valuesB, List<BuiltInCategory> bicsB) =
            SideInputs(setA: false);

        List<RevitLinkInstance> selected = CheckedLinks();

        if (kwA.Length == 0 && valuesA.Count == 0 && bicsA.Count == 0)
        {
            RibbitUi.Alert(
                "Set A is empty - choose a set, type a keyword, or pick exact values with Browse.",
                DialogTitle);
            return;
        }

        if (selected.Count == 0)
        {
            RibbitUi.Alert(
                "No Revit Links checked - Set B would be empty. Check at least one link.",
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

        Result = new ClashLinkSearchChoice(
            kwA, kwB, valuesA, valuesB, selected, bicsB, bicsA,
            _setsA.ChosenIds(), _setsB.ChosenIds(),
            _pad.Text, _pad.Value, _pad.Unit, TrayOnly,
            ClashFormParts.ResolveFolder(_csvFolder.Text));

        DialogResult = true;
    }
}
