using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Autodesk.Revit.DB;
using Ribbit.Geometry.Clash;
using Ribbit.Native.Revit;
using Ribbit.Native.Settings;

namespace Ribbit.Native.Ui;

/// <summary>
/// What a set editor can ask the model, when it was opened from somewhere that
/// has one.
///
/// Bundled rather than passed as three separate delegates through four windows:
/// they are always supplied together, and a null bundle says "no model here"
/// once instead of three times. Opened cold, the editor still edits — it just
/// cannot browse or count.
///
/// EACH SCAN TAKES THE CATEGORIES THE SET BEING EDITED CURRENTLY NAMES. Without
/// them a scan only ever covered the 24 MEP categories, so a set built around
/// Structural Framing or Walls could SELECT those elements while never offering
/// their parameter values to pick from — the search honoured the category and
/// the picker did not. An empty list means "MEP only", which is what every scan
/// did before and what the three non-federated tools still ask for.
///
/// <c>WorksetAudit</c> is FEDERATED ONLY: given the set as currently edited, every
/// workset across the ticked models with how many of the set's elements sit on
/// it. Null everywhere else, which disables the audit button with a reason.
/// </summary>
internal sealed record SetScanServices(
    Func<IReadOnlyList<BuiltInCategory>, List<ValueChoice>>? Values = null,
    Func<IReadOnlyList<BuiltInCategory>, List<ParameterChoice>>? Parameters = null,
    Func<string, IReadOnlyList<BuiltInCategory>, List<ValueChoice>>? ValuesForParameter = null,
    Func<List<Category>>? AllCategories = null,
    Func<ClashSet, List<WorksetUsage>>? WorksetAudit = null);

/// <summary>
/// The workset audit cannot run, and the message says why ("no model is ticked") -
/// shown to the person exactly as written.
///
/// Its own type so the editor can tell a deliberate reason from a failure: any
/// other exception from the audit is reported as "couldn't read the worksets",
/// never as if it were an explanation.
/// </summary>
internal sealed class AuditUnavailableException(string message) : Exception(message);

/// <summary>
/// Edits one named set: exact values, keywords and whole categories.
///
/// COUNTS ARE ON DEMAND, not on open. Counting means scanning the model, which
/// on a large one is the single most expensive thing these tools do — opening a
/// set to rename it must not pay for that. "Count matches" runs it once, and the
/// tools memoize the scan, so a Browse already done makes it instant.
///
/// The count column exists mainly for the stale case: a value renamed since the
/// set was saved matches nothing, silently. That is detectable today only by
/// reading the "picked value matched nothing" line AFTER a full clash run.
/// </summary>
internal sealed class SetEditorWindow : Window
{
    private const string DialogTitle = "Edit set";

    private readonly ClashSetLibrary _library;
    private readonly ClashSet? _editing;
    private readonly Func<IReadOnlyList<BuiltInCategory>, List<ValueChoice>>? _scan;
    private readonly SetScanServices? _services;

    private readonly TextBox _name;
    private readonly StackPanel _rulesHost;
    private readonly TextBlock _preview;

    private readonly List<SetValue> _values;
    private readonly List<string> _keywords;
    private readonly List<string> _categories;

    /// <summary>
    /// Worksets the set leaves out. Carried through Save even from a tool that
    /// cannot audit them - an edit made in Clash Detect must not wipe a list it
    /// never showed.
    /// </summary>
    private readonly List<string> _excluded;

    private readonly StackPanel _worksetsHost;

    /// <summary>The audit button, for a test to read whether it is enabled.</summary>
    internal Button AuditButton { get; }

    /// <summary>The left-out worksets as currently edited.</summary>
    internal IReadOnlyList<string> Excluded => _excluded;

    /// <summary>Populated by "Count matches"; null until then, so counts read "-".</summary>
    private List<ValueChoice>? _scanned;

    public ClashSet? Result { get; private set; }

    /// <param name="seed">
    /// Pre-filled content for "Save as set..." — the keyword and picks already
    /// typed into a clash form. Null for an ordinary new set.
    /// </param>
    public SetEditorWindow(
        ClashSetLibrary library,
        ClashSet? editing,
        ClashSet? seed = null,
        SetScanServices? services = null)
    {
        _library = library;
        _editing = editing;
        _services = services;
        _scan = services?.Values;

        ClashSet source = editing ?? seed ?? new ClashSet();
        _values = [.. source.Values.Select(v => new SetValue { Param = v.Param, Value = v.Value })];
        _keywords = [.. source.Keywords];
        _categories = [.. source.Categories];
        _excluded = WorksetAudit.Normalize(source.ExcludedWorksets);

        Title = editing is null ? "New set" : $"Edit set - {editing.Name}";
        Width = 620;
        Height = 780;
        MinWidth = 460;
        MinHeight = 430;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        new WindowInteropHelper(this).Owner =
            System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;

        StackPanel panel = new() { Margin = new Thickness(FormParts.Gutter) };

        panel.Children.Add(FormParts.Section("Name", first: true));
        _name = FormParts.Input(source.Name);
        panel.Children.Add(_name);

        panel.Children.Add(FormParts.Divider());
        panel.Children.Add(FormParts.Section("Rules"));
        panel.Children.Add(FormParts.Hint(
            "An element joins the set if ANY rule matches. Values match exactly, against the "
            + "parameter they came from; keywords match as substrings."));

        _rulesHost = new StackPanel();

        panel.Children.Add(new Border
        {
            BorderBrush = SystemColors.ControlDarkBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            Background = SystemColors.WindowBrush,
            Padding = new Thickness(2),
            Child = new ScrollViewer
            {
                Height = 258,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = _rulesHost,
            },
        });

        WrapPanel adders = new() { Margin = new Thickness(0, 7, 0, 0) };
        adders.Children.Add(Small("+ Value...", AddValueByBrowse,
            "Pick from System Type and Description values that exist in this model"));
        adders.Children.Add(Small("+ Other parameter...", AddValueByParameter,
            "Choose any parameter the model carries, then pick from its values"));
        adders.Children.Add(Small("+ Value by hand...", AddValueByHand,
            "Add a value that is not in this model yet"));
        adders.Children.Add(Small("+ Keyword...", AddKeyword, "Match a substring"));
        adders.Children.Add(Small("+ Categories...", AddCategories, "Add whole categories"));
        panel.Children.Add(adders);

        _preview = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Margin = new Thickness(11, 7, 11, 7),
        };

        panel.Children.Add(new Border
        {
            Background = SystemColors.ControlLightBrush,
            BorderBrush = FormParts.Accent,
            BorderThickness = new Thickness(3, 0, 0, 0),
            Margin = new Thickness(0, 12, 0, 0),
            Child = _preview,
        });

        Button count = new()
        {
            Content = "Count matches",
            MinWidth = 116,
            MinHeight = 24,
            FontSize = 11,
            Padding = new Thickness(8, 2, 8, 2),
            Margin = new Thickness(0, 7, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            IsEnabled = _scan is not null,
            ToolTip = _scan is null
                ? "Open this set from a clash window to count matches."
                : "Scans the model once, then fills in the counts.",
        };
        count.Click += (_, _) => CountMatches();
        panel.Children.Add(count);

        // ---- worksets left out ---------------------------------------------
        panel.Children.Add(FormParts.Divider());
        panel.Children.Add(FormParts.Section("Worksets left out"));
        panel.Children.Add(FormParts.Hint(
            "Federated Clash only. Anything the rules above select is dropped when it sits on one of "
            + "these worksets, in every model searched - typical-floor copies, hidden duplicates, "
            + "builder's work openings. The other clash tools ignore this list."));

        _worksetsHost = new StackPanel();
        panel.Children.Add(new Border
        {
            BorderBrush = SystemColors.ControlDarkBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            Background = SystemColors.WindowBrush,
            Padding = new Thickness(2),
            Child = new ScrollViewer
            {
                MaxHeight = 150,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = _worksetsHost,
            },
        });

        bool canAudit = services?.WorksetAudit is not null;
        AuditButton = new Button
        {
            Content = "Audit worksets...",
            MinWidth = 116,
            MinHeight = 24,
            FontSize = 11,
            Padding = new Thickness(8, 2, 8, 2),
            Margin = new Thickness(0, 7, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            IsEnabled = canAudit,
            ToolTip = canAudit
                ? "List every workset in the ticked models with what this set has on it, "
                  + "and suggest which to leave out."
                : "Open this set from Federated Clash to audit worksets - only it searches by workset.",
        };
        AuditButton.Click += (_, _) => AuditWorksets();

        // Disabled controls show no tooltip unless told to.
        ToolTipService.SetShowOnDisabled(AuditButton, true);
        panel.Children.Add(AuditButton);

        panel.Children.Add(FormParts.Divider());

        Button save = FormParts.Primary("Save");
        save.Click += (_, _) => Accept();
        panel.Children.Add(FormParts.ButtonBar(save, FormParts.Secondary("Cancel", true)));

        Content = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = panel,
        };

        RefreshRules();
        RefreshWorksets();
    }

    private static Button Small(string text, Action onClick, string? tip = null)
        => FormParts.SmallButton(text, onClick, tip, minWidth: 96, margin: new Thickness(0, 0, 6, 6));

    // ------------------------------------------------------------------- rows

    private static Border KindTag(string text, System.Windows.Media.Brush background) => new()
    {
        Background = background,
        CornerRadius = new CornerRadius(3),
        Padding = new Thickness(5, 1, 5, 1),
        Margin = new Thickness(0, 0, 8, 0),
        VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock
        {
            Text = text,
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            FontSize = 9.5,
            FontWeight = FontWeights.Bold,
            Foreground = System.Windows.Media.Brushes.White,
        },
    };

    private static readonly System.Windows.Media.Brush ValueBrush = FormParts.Accent;

    private static readonly System.Windows.Media.Brush KeywordBrush =
        new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(0xB5, 0x76, 0x2B));

    private static readonly System.Windows.Media.Brush CategoryBrush =
        new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(0x7A, 0x5B, 0xB5));

    private static readonly System.Windows.Media.Brush WorksetBrush =
        new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(0x9B, 0x3B, 0x3B));

    private Border RuleRow(
        string kind, System.Windows.Media.Brush brush,
        string? qualifier, string text, string countText, bool stale, Action remove)
    {
        DockPanel row = new();

        Button del = new()
        {
            Content = "✕",
            FontSize = 10,
            Foreground = SystemColors.GrayTextBrush,
            Background = System.Windows.Media.Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(7, 0, 4, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = "Remove this rule",
        };
        del.Click += (_, _) => remove();

        TextBlock count = new()
        {
            Text = countText,
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            FontSize = 11,
            Foreground = stale ? System.Windows.Media.Brushes.DarkGoldenrod : SystemColors.GrayTextBrush,
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 40,
            TextAlignment = TextAlignment.Right,
        };

        DockPanel.SetDock(del, Dock.Right);
        DockPanel.SetDock(count, Dock.Right);
        row.Children.Add(del);
        row.Children.Add(count);
        row.Children.Add(KindTag(kind, brush));

        StackPanel body = new()
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };

        if (qualifier is not null)
        {
            body.Children.Add(new TextBlock
            {
                Text = qualifier,
                FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                FontSize = 10.5,
                Foreground = SystemColors.GrayTextBrush,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 7, 0),
            });
        }

        body.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = 12.5,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });

        if (stale)
        {
            body.Children.Add(new TextBlock
            {
                Text = "  not found in this model",
                FontSize = 11,
                FontStyle = FontStyles.Italic,
                Foreground = System.Windows.Media.Brushes.DarkGoldenrod,
                VerticalAlignment = VerticalAlignment.Center,
            });
        }

        row.Children.Add(body);

        return new Border
        {
            BorderBrush = SystemColors.ControlDarkBrush,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(8, 6, 5, 6),
            Child = row,
        };
    }

    private void RefreshRules()
    {
        _rulesHost.Children.Clear();

        if (_values.Count == 0 && _keywords.Count == 0 && _categories.Count == 0)
        {
            _rulesHost.Children.Add(FormParts.EmptyChipNote(
                "No rules yet - add a value, a keyword or some categories below."));
            RefreshPreview();
            return;
        }

        foreach (SetValue v in _values)
        {
            SetValue captured = v;
            (string text, bool stale) = ValueCount(captured);

            _rulesHost.Children.Add(RuleRow(
                "VALUE", ValueBrush, $"[{captured.Param}]", captured.Value, text, stale,
                () => { _values.Remove(captured); RefreshRules(); }));
        }

        foreach (string k in _keywords)
        {
            string captured = k;

            _rulesHost.Children.Add(RuleRow(
                "KEYWORD", KeywordBrush, "contains", $"\"{captured}\"",
                KeywordCount(captured), false,
                () => { _keywords.Remove(captured); RefreshRules(); }));
        }

        foreach (string c in _categories)
        {
            string captured = c;
            string display = Enum.TryParse(captured, out BuiltInCategory bic)
                ? ClashSetLibrary.DisplayName(bic)
                : captured;

            _rulesHost.Children.Add(RuleRow(
                "CATEGORY", CategoryBrush, null, display, "", false,
                () => { _categories.Remove(captured); RefreshRules(); }));
        }

        RefreshPreview();
    }

    private void RefreshWorksets()
    {
        _worksetsHost.Children.Clear();

        if (_excluded.Count == 0)
        {
            _worksetsHost.Children.Add(FormParts.EmptyChipNote(
                "None - every workset is searched."));
            return;
        }

        foreach (string w in _excluded)
        {
            string captured = w;
            _worksetsHost.Children.Add(RuleRow(
                "WORKSET", WorksetBrush, "leave out", captured, "", false,
                () => { _excluded.Remove(captured); RefreshWorksets(); }));
        }
    }

    /// <summary>
    /// Runs the audit on the set AS EDITED - unsaved rules included - and takes
    /// back whatever is ticked.
    /// </summary>
    private void AuditWorksets()
    {
        if (_services?.WorksetAudit is null)
            return;

        ClashSet draft = new()
        {
            Name = (_name.Text ?? "").Trim(),
            Values = [.. _values],
            Keywords = [.. _keywords],
            Categories = [.. _categories],
            ExcludedWorksets = [.. _excluded],
        };

        List<WorksetUsage> rows;
        try
        {
            rows = _services.WorksetAudit(draft);
        }
        catch (Exception ex)
        {
            // AuditUnavailableException is the caller saying why it cannot
            // audit ("no model is ticked") - shown as it is, not as a failure.
            RibbitUi.Alert(
                ex is AuditUnavailableException
                    ? ex.Message
                    : $"Couldn't read the models' worksets:\n\n{ex.Message}",
                DialogTitle);
            return;
        }

        if (rows.Count == 0 && _excluded.Count == 0)
        {
            RibbitUi.Alert(
                "None of the ticked models is workshared, so there are no worksets to leave out.",
                DialogTitle);
            return;
        }

        WorksetAuditWindow dlg = new(rows, _excluded, draft.Name) { Owner = this };

        if (dlg.ShowDialog() == true)
        {
            _excluded.Clear();
            _excluded.AddRange(dlg.Result);
            RefreshWorksets();
        }
    }

    // ----------------------------------------------------------------- counts

    /// <summary>
    /// A value's count, and whether it is stale.
    ///
    /// Stale means the scan RAN and this value was not among the results — not
    /// merely that no scan has happened, which is why the flag is suppressed
    /// until <see cref="_scanned"/> is populated.
    /// </summary>
    private (string Text, bool Stale) ValueCount(SetValue v)
    {
        if (_scanned is null)
            return ("-", false);

        ValueChoice? hit = _scanned.FirstOrDefault(
            c => string.Equals(c.ParamName, v.Param, StringComparison.OrdinalIgnoreCase)
                 && string.Equals(c.Display, v.Value, StringComparison.OrdinalIgnoreCase));

        return hit is null ? ("0", true) : ($"{hit.Count}", false);
    }

    /// <summary>
    /// A keyword's count, summed over every distinct value containing it.
    ///
    /// Exact rather than an estimate: the scan counts every element that has a
    /// value, and an element with no value cannot match a substring anyway.
    /// </summary>
    private string KeywordCount(string keyword)
    {
        if (_scanned is null)
            return "-";

        int total = _scanned
            .Where(c => c.Display.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            .Sum(c => c.Count);

        return $"{total}";
    }

    private void CountMatches()
    {
        if (_scan is null)
            return;

        try
        {
            _scanned = _scan(EditedCategories());
        }
        catch (Exception ex)
        {
            RibbitUi.Alert($"Couldn't scan for values:\n\n{ex.Message}", DialogTitle);
            return;
        }

        RefreshRules();
    }

    private void RefreshPreview()
    {
        int rules = _values.Count + _keywords.Count + _categories.Count;

        if (rules == 0)
        {
            _preview.Text = "Empty set - it would add nothing to a search.";
            return;
        }

        List<string> parts = [];
        if (_values.Count > 0)
            parts.Add($"{_values.Count} value{(_values.Count == 1 ? "" : "s")}");
        if (_keywords.Count > 0)
            parts.Add($"{_keywords.Count} keyword{(_keywords.Count == 1 ? "" : "s")}");
        if (_categories.Count > 0)
            parts.Add($"{_categories.Count} categor{(_categories.Count == 1 ? "y" : "ies")}");

        string head = string.Join(" · ", parts);

        if (_scanned is null)
        {
            _preview.Text = $"{head}.  Count matches fills in the numbers.";
            return;
        }

        int stale = _values.Count(v => ValueCount(v).Stale);

        _preview.Text = stale == 0
            ? $"{head}.  Every value was found in this model."
            : $"{head}.  {stale} value{(stale == 1 ? "" : "s")} not found in this model - "
              + "renamed or removed since the set was saved.";
    }

    // ------------------------------------------------------------------- adds

    private void AddValueByBrowse()
    {
        if (_scan is null)
        {
            RibbitUi.Alert(
                "Open this set from a clash window to browse the model's values. "
                + "Use \"+ Value by hand...\" to type one instead.",
                DialogTitle);
            return;
        }

        List<ValueChoice> choices;
        try
        {
            choices = _scan(EditedCategories());
        }
        catch (Exception ex)
        {
            RibbitUi.Alert($"Couldn't scan for values:\n\n{ex.Message}", DialogTitle);
            return;
        }

        // Reuse the scan for the count column too - it is the same data.
        _scanned = choices;

        List<(string Param, string Value)> picked = [.. _values.Select(v => (v.Param, v.Value))];
        ClashFormParts.PickInto(choices, picked, "Add values to the set", DialogTitle);

        _values.Clear();
        foreach ((string param, string value) in picked)
            _values.Add(new SetValue { Param = param, Value = value });

        RefreshRules();
    }

    /// <summary>
    /// The categories this set names RIGHT NOW, so a scan covers the elements
    /// the set is actually about.
    ///
    /// Read live rather than at open: categories are added in this same window,
    /// and the useful order is to add "Structural Framing" and then go looking
    /// for a value on it. Anything that no longer parses as a
    /// <see cref="BuiltInCategory"/> is skipped, matching
    /// <c>ClashSet.Resolved</c>.
    /// </summary>
    private List<BuiltInCategory> EditedCategories()
    {
        List<BuiltInCategory> outp = [];

        foreach (string name in _categories)
        {
            if (Enum.TryParse(name, out BuiltInCategory bic) && !outp.Contains(bic))
                outp.Add(bic);
        }

        return outp;
    }

    /// <summary>
    /// Pick ANY parameter the model carries, then pick from its values.
    ///
    /// Two steps on purpose. Scanning every parameter's values up front is the
    /// scan-explosion the whole design avoids — discovery reads NAMES from a
    /// sample, and only the one parameter you choose has its values read.
    /// </summary>
    private void AddValueByParameter()
    {
        if (_services?.Parameters is null || _services.ValuesForParameter is null)
        {
            RibbitUi.Alert(
                "Open this set from a clash window to browse the model's parameters. "
                + "Use \"+ Value by hand...\" to type one instead.",
                DialogTitle);
            return;
        }

        List<ParameterChoice> parameters;
        try
        {
            parameters = _services.Parameters(EditedCategories());
        }
        catch (Exception ex)
        {
            RibbitUi.Alert($"Couldn't read the model's parameters:\n\n{ex.Message}", DialogTitle);
            return;
        }

        if (parameters.Count == 0)
        {
            RibbitUi.Alert("No parameters found on the searched elements.", DialogTitle);
            return;
        }

        ParameterPickWindow picker = new(parameters) { Owner = this };
        if (picker.ShowDialog() != true || picker.Result is null)
            return;

        string paramName = picker.Result.Name;

        List<ValueChoice> choices;
        try
        {
            choices = _services.ValuesForParameter(paramName, EditedCategories());
        }
        catch (Exception ex)
        {
            RibbitUi.Alert($"Couldn't scan \"{paramName}\":\n\n{ex.Message}", DialogTitle);
            return;
        }

        if (choices.Count == 0)
        {
            RibbitUi.Alert(
                $"No values found for \"{paramName}\" on the searched elements.", DialogTitle);
            return;
        }

        // Only THIS parameter's values are offered, so the picker starts from
        // what is already stored for it and leaves other parameters' values
        // untouched - picking Comments must not clear a System Type value.
        List<(string Param, string Value)> forParam =
        [
            .. _values.Where(v => string.Equals(v.Param, paramName, StringComparison.OrdinalIgnoreCase))
                      .Select(v => (v.Param, v.Value))
        ];

        ClashFormParts.PickInto(choices, forParam, $"Values for {paramName}", DialogTitle);

        _values.RemoveAll(
            v => string.Equals(v.Param, paramName, StringComparison.OrdinalIgnoreCase));

        foreach ((string param, string value) in forParam)
            _values.Add(new SetValue { Param = param, Value = value });

        RefreshRules();
    }

    private void AddValueByHand()
    {
        TextPromptWindow dlg = new(
            "Add a value by hand",
            "Parameter",
            "Value",
            [ClashSetBuilder.SystemTypeParam, ClashSetBuilder.DescriptionParam])
        { Owner = this };

        if (dlg.ShowDialog() != true)
            return;

        if (dlg.Value.Length == 0)
        {
            RibbitUi.Alert("Type a value.", DialogTitle);
            return;
        }

        if (_values.Any(v => string.Equals(v.Param, dlg.Param, StringComparison.OrdinalIgnoreCase)
                             && string.Equals(v.Value, dlg.Value, StringComparison.OrdinalIgnoreCase)))
            return;

        _values.Add(new SetValue { Param = dlg.Param, Value = dlg.Value });
        RefreshRules();
    }

    private void AddKeyword()
    {
        TextPromptWindow dlg = new("Add a keyword", null, "Keyword", null) { Owner = this };

        if (dlg.ShowDialog() != true)
            return;

        // Comma-separated, the same way a typed keyword box splits.
        foreach (string k in ClashStatus.ParseKeywords(dlg.Value))
        {
            if (!_keywords.Any(e => string.Equals(e, k, StringComparison.OrdinalIgnoreCase)))
                _keywords.Add(k);
        }

        RefreshRules();
    }

    private void AddCategories()
    {
        // The full model category list when the tool can supply one — that is
        // what makes Structural Framing or Walls choosable. Falling back to the
        // 24 MEP categories keeps the older tools offering exactly what they can
        // actually collect.
        List<Autodesk.Revit.DB.Category>? all = null;
        try
        {
            all = _services?.AllCategories?.Invoke();
        }
        catch (Exception)
        {
            // An unreadable category list degrades to the MEP set.
        }

        CategoryPickWindow dlg = new(_categories, all) { Owner = this };

        if (dlg.ShowDialog() == true)
        {
            _categories.Clear();
            _categories.AddRange(dlg.Result);
            RefreshRules();
        }
    }

    // ----------------------------------------------------------------- accept

    private void Accept()
    {
        string name = (_name.Text ?? "").Trim();

        if (name.Length == 0)
        {
            RibbitUi.Alert("Give the set a name.", DialogTitle);
            return;
        }

        if (_library.NameTaken(name, _editing))
        {
            RibbitUi.Alert($"A set called \"{name}\" already exists.", DialogTitle);
            return;
        }

        if (_values.Count == 0 && _keywords.Count == 0 && _categories.Count == 0)
        {
            RibbitUi.Alert(
                "Add at least one rule - an empty set would add nothing to a search.",
                DialogTitle);
            return;
        }

        Result = new ClashSet
        {
            // MINTED HERE, not on the next Load.
            //
            // Without this a new set went into the library with a blank id, and
            // a blank id resolves to nothing - so a set could be created and
            // chosen straight away, and every lookup treated it as deleted. The
            // chip reading "deleted" was the mild half: SetSidePicker.Live()
            // drops what it cannot resolve, so the set contributed no keywords,
            // values or categories and the search silently ran WITHOUT it.
            // Reopening the tool appeared to fix it only because Load's
            // EnsureIds backfilled the id by then.
            //
            // An EDIT carries the existing id through, so a set keeps its
            // identity - and therefore every saved test and stored selection
            // pointing at it - across a rename.
            Id = _editing is not null && _editing.Id.Length > 0
                ? _editing.Id
                : Guid.NewGuid().ToString("N"),
            Name = name,
            Values = [.. _values],
            Keywords = [.. _keywords],
            Categories = [.. _categories],
            ExcludedWorksets = [.. _excluded],
        };

        DialogResult = true;
    }
}
