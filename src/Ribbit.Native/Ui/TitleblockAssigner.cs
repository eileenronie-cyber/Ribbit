using System.Windows;
using System.Windows.Controls;
using Ribbit.Geometry.Sheets;

namespace Ribbit.Native.Ui;

/// <summary>
/// Assigns a titleblock to each sheet in the window, from the types actually
/// loaded in the model.
///
/// Replaces two worse mechanisms. The original was POSITIONAL — the Nth selected
/// type went to the Nth row, last one repeating — which is invisible in the data
/// and silently wrong the moment a row is inserted in Excel. The interim one
/// required typing type names into a spreadsheet column from memory, with no way
/// to see what was available.
///
/// The data column still works and still wins over the default, for a drawing
/// list maintained in Excel; anything assigned here wins over both.
/// </summary>
internal sealed class TitleblockAssigner
{
    /// <summary>Shown for "no titleblock at all", which creates a placeholder sheet.</summary>
    public const string NoneLabel = "(none - placeholder sheet)";

    /// <summary>Shown per row for "whatever the default is".</summary>
    private const string DefaultLabel = "(default)";

    private readonly Action _changed;
    private readonly ComboBox _default = new() { Margin = new Thickness(0, 0, 0, 4) };
    private readonly ComboBox _bulk = new() { MinWidth = 200 };
    private readonly StackPanel _rows = new();
    private readonly TextBlock _status = FormParts.Status();

    private IReadOnlyList<string> _names = [];

    public UIElement Content { get; }

    public TitleblockAssigner(Action changed)
    {
        _changed = changed;

        _default.SelectionChanged += (_, _) => _changed();

        Button apply = FormParts.Secondary("Set every sheet");
        apply.ToolTip = "Assign the chosen type to every sheet listed below.";
        apply.Click += (_, _) => SetAll(_bulk.SelectedItem as string);

        Button clear = FormParts.Secondary("Clear assignments");
        clear.ToolTip = "Put every sheet back to the default.";
        clear.Click += (_, _) => SetAll(DefaultLabel);

        StackPanel bulkRow = new()
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 0, 8),
        };
        bulkRow.Children.Add(_bulk);
        bulkRow.Children.Add(apply);
        bulkRow.Children.Add(clear);

        StackPanel host = new();
        host.Children.Add(FormParts.FieldLabel("Default titleblock"));
        host.Children.Add(FormParts.Hint(
            "Used for every sheet that is not given one of its own below."));
        host.Children.Add(_default);
        host.Children.Add(_status);
        host.Children.Add(FormParts.Divider());
        host.Children.Add(FormParts.Section("Per sheet"));
        host.Children.Add(FormParts.Hint(
            "Override individual sheets - for a drawing list that mixes sheet sizes."));
        host.Children.Add(bulkRow);
        host.Children.Add(_rows);

        Content = host;
    }

    /// <summary>The type chosen as the default, or "" for placeholder sheets.</summary>
    public string Default =>
        _default.SelectedItem is string s && s != NoneLabel ? s : "";

    /// <summary>Only the sheets actually overridden — the default covers the rest.</summary>
    public Dictionary<string, string> Assignments()
    {
        Dictionary<string, string> map = [];
        foreach (SheetRow row in Rows())
        {
            if (row.Choice.Length > 0)
                map[row.SheetNumber] = row.Choice;
        }
        return map;
    }

    public int AssignedCount => Assignments().Count;

    /// <summary>
    /// The titleblock types loaded in the model. Called once — the model cannot
    /// change while the window is open.
    /// </summary>
    public void SetTypes(IReadOnlyList<string> names, string savedDefault)
    {
        _names = names;

        List<string> options = [NoneLabel, .. names];
        _default.ItemsSource = options;

        // A saved default that is no longer loaded must not silently become
        // "placeholder" — it selects nothing, and the status line says so.
        _default.SelectedItem = names.Contains(savedDefault)
            ? savedDefault
            : savedDefault.Length == 0 ? NoneLabel : null;

        _bulk.ItemsSource = options;
        _bulk.SelectedItem = options.Count > 1 ? options[1] : NoneLabel;

        _status.Text = names.Count switch
        {
            0 => "No titleblock types are loaded in this model - every sheet will be a "
                 + "placeholder. Load a titleblock family first if that is not what you want.",
            1 => "1 titleblock type loaded.",
            _ => $"{names.Count} titleblock types loaded.",
        };

        if (savedDefault.Length > 0 && !names.Contains(savedDefault))
        {
            _status.Text += $"  The saved default \"{savedDefault}\" is not among them - "
                            + "choose again.";
        }
    }

    /// <summary>
    /// Rebuilds the per-sheet list from the workbook. Existing choices survive by
    /// sheet number, so re-reading the file does not discard the assignments.
    /// </summary>
    public void SetSheets(IReadOnlyList<(string Number, string Name)> sheets)
    {
        Dictionary<string, string> keep = Assignments();

        _rows.Children.Clear();

        List<string> options = [DefaultLabel, NoneLabel, .. _names];

        int i = 0;
        foreach ((string number, string name) in sheets)
        {
            if (number.Length == 0)
                continue;   // spacer rows have nothing to assign

            SheetRow row = new(number, name, options, _changed);
            if (keep.TryGetValue(number, out string? previous))
                row.Choice = previous;

            _rows.Children.Add(FormParts.RowCard(row.Content, tinted: i++ % 2 == 1));
        }
    }

    private void SetAll(string? choice)
    {
        if (choice is null)
            return;

        foreach (SheetRow row in Rows())
            row.Select(choice);

        _changed();
    }

    private IEnumerable<SheetRow> Rows() => _rows.Children.OfType<Border>()
        .Select(b => b.Child)
        .OfType<DockPanel>()
        .Select(d => d.Tag)
        .OfType<SheetRow>();

    /// <summary>One sheet and the titleblock it will get.</summary>
    private sealed class SheetRow
    {
        private readonly ComboBox _combo;

        public string SheetNumber { get; }
        public DockPanel Content { get; }

        /// <summary>"" when the row is on the default.</summary>
        public string Choice
        {
            get => _combo.SelectedItem is string s && s != DefaultLabel ? s : "";
            set => _combo.SelectedItem = value.Length == 0 ? DefaultLabel : value;
        }

        public SheetRow(
            string number, string name, IReadOnlyList<string> options, Action changed)
        {
            SheetNumber = number;

            _combo = new ComboBox
            {
                ItemsSource = options,
                SelectedItem = DefaultLabel,
                MinWidth = 220,
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            _combo.SelectionChanged += (_, _) => changed();

            TextBlock label = new()
            {
                Text = name.Length > 0 ? $"{number}   {name}" : number,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };

            Content = new DockPanel { Tag = this };
            DockPanel.SetDock(_combo, Dock.Right);
            Content.Children.Add(_combo);
            Content.Children.Add(label);
        }

        public void Select(string option) => _combo.SelectedItem = option;
    }
}
