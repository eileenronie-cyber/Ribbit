using System.Windows;
using System.Windows.Controls;
using Ribbit.Geometry.Sheets;
using Ribbit.Native.Revit;

namespace Ribbit.Native.Ui;

/// <summary>
/// Builds the Revit-parameter-to-Excel-column mapping in the window, so it no
/// longer has to be authored on a MAPPING worksheet inside the workbook.
///
/// The worksheet version was unverifiable until the tool ran: you typed a
/// parameter name from memory, typed a column name from memory, and found out
/// whether either existed by looking at an unchanged model afterwards. Here both
/// sides are dropdowns of things that actually exist — parameters discovered
/// from the model, columns read from the data worksheet — so the two failure
/// modes the preview band reports can mostly no longer be created.
///
/// The MAPPING worksheet is still READ, as a starting point, so workbooks
/// already in circulation keep working.
/// </summary>
internal sealed class MappingEditor
{
    private readonly StackPanel _rows = new();
    private readonly TextBlock _empty = FormParts.EmptyChipNote(
        "No mapping yet - Auto-match fills this in from your column headers.");

    private readonly Action _changed;

    private IReadOnlyList<SheetParameterInfo> _parameters = [];
    private IReadOnlyList<string> _columns = [];

    /// <summary>The panel to drop into the form.</summary>
    public UIElement Content { get; }

    public MappingEditor(Action changed)
    {
        _changed = changed;

        Button auto = FormParts.Secondary("Auto-match");
        auto.ToolTip = "Pair parameters with columns whose names agree. "
                       + "Anything ambiguous is left for you to decide.";
        auto.Click += (_, _) => AutoMatch();

        Button add = FormParts.Secondary("Add row");
        add.Click += (_, _) => { AddRow("", ""); _changed(); };

        Button clear = FormParts.Secondary("Clear");
        clear.Click += (_, _) => { _rows.Children.Clear(); Sync(); _changed(); };

        StackPanel buttons = new()
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 0, 8),
        };
        buttons.Children.Add(auto);
        buttons.Children.Add(add);
        buttons.Children.Add(clear);

        StackPanel host = new();
        host.Children.Add(buttons);
        host.Children.Add(_empty);
        host.Children.Add(_rows);
        Content = host;
    }

    /// <summary>
    /// What the model and the workbook currently offer. Called whenever either
    /// changes; existing rows are left alone so a mapping is never silently
    /// dropped just because a path was being retyped.
    /// </summary>
    public void SetSources(
        IReadOnlyList<SheetParameterInfo> parameters, IReadOnlyList<string> columns)
    {
        _parameters = parameters;
        _columns = columns;

        foreach (MappingRow row in Rows())
            row.Refill(parameters, columns);
    }

    public Dictionary<string, string> Mapping()
    {
        Dictionary<string, string> map = [];
        foreach (MappingRow row in Rows())
        {
            string param = row.Parameter;
            string column = row.Column;

            // Half-finished rows are simply not mapping yet - the form should
            // not refuse to preview while somebody is still filling one in.
            if (param.Length > 0 && column.Length > 0)
                map[param] = column;
        }
        return map;
    }

    public void Load(IReadOnlyDictionary<string, string> mapping)
    {
        _rows.Children.Clear();
        foreach (KeyValuePair<string, string> kv in mapping)
            AddRow(kv.Key, kv.Value);
        Sync();
    }

    public int Count => Rows().Count();

    /// <summary>
    /// Fills only the rows that do not exist yet, so a hand-made pairing is
    /// never overwritten by a guess.
    /// </summary>
    private void AutoMatch()
    {
        Dictionary<string, string> already = Mapping();

        IEnumerable<string> wanted = _parameters
            .Where(p => !p.ReadOnly && !already.ContainsKey(p.Name))
            .Select(p => p.Name);

        IEnumerable<string> free = _columns.Where(c => !already.ContainsValue(c));

        foreach (KeyValuePair<string, string> kv in MappingMatcher.AutoMatch(wanted, free))
            AddRow(kv.Key, kv.Value);

        Sync();
        _changed();
    }

    private IEnumerable<MappingRow> Rows() => _rows.Children.OfType<Border>()
        .Select(b => b.Tag)
        .OfType<MappingRow>();

    private void AddRow(string parameter, string column)
    {
        MappingRow row = new(_parameters, _columns, parameter, column, _changed);

        Border card = FormParts.RowCard(row.Content, tinted: _rows.Children.Count % 2 == 1);
        card.Tag = row;
        card.Margin = new Thickness(0, 0, 0, 4);

        row.OnRemove = () =>
        {
            _rows.Children.Remove(card);
            Sync();
            _changed();
        };

        _rows.Children.Add(card);
        Sync();
    }

    private void Sync() => _empty.Visibility =
        _rows.Children.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>One parameter-to-column pairing.</summary>
    private sealed class MappingRow
    {
        private readonly ComboBox _param;
        private readonly ComboBox _column;

        public Action? OnRemove { get; set; }
        public UIElement Content { get; }

        public string Parameter => (_param.Text ?? "").Trim();
        public string Column => (_column.Text ?? "").Trim();

        public MappingRow(
            IReadOnlyList<SheetParameterInfo> parameters,
            IReadOnlyList<string> columns,
            string parameter,
            string column,
            Action changed)
        {
            // Editable, not locked to the list: a parameter that only exists on
            // sheets this model has not created yet is still a legitimate thing
            // to map, and the preview band reports it either way.
            _param = Editable(parameter);
            _column = Editable(column);

            _param.SelectionChanged += (_, _) => changed();
            _param.LostFocus += (_, _) => changed();
            _column.SelectionChanged += (_, _) => changed();
            _column.LostFocus += (_, _) => changed();

            Button remove = new()
            {
                Content = "✕",
                FontSize = 11,
                Width = 24,
                Background = System.Windows.Media.Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Foreground = SystemColors.GrayTextBrush,
                Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = "Remove this mapping",
                VerticalAlignment = VerticalAlignment.Center,
            };
            remove.Click += (_, _) => OnRemove?.Invoke();

            Grid grid = new();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            TextBlock arrow = new()
            {
                Text = "  ←  ",
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = SystemColors.GrayTextBrush,
            };

            Grid.SetColumn(_param, 0);
            Grid.SetColumn(arrow, 1);
            Grid.SetColumn(_column, 2);
            Grid.SetColumn(remove, 3);

            grid.Children.Add(_param);
            grid.Children.Add(arrow);
            grid.Children.Add(_column);
            grid.Children.Add(remove);

            Content = grid;
            Refill(parameters, columns);
        }

        /// <summary>
        /// Repoints both dropdowns at the current sources, preserving whatever is
        /// typed — reassigning ItemsSource clears the text of an editable combo.
        /// </summary>
        public void Refill(
            IReadOnlyList<SheetParameterInfo> parameters, IReadOnlyList<string> columns)
        {
            string p = Parameter;
            string c = Column;

            _param.ItemsSource = parameters.Where(x => !x.ReadOnly).Select(x => x.Name).ToList();
            _column.ItemsSource = columns;

            _param.Text = p;
            _column.Text = c;
        }

        private static ComboBox Editable(string text) => new()
        {
            IsEditable = true,
            Text = text,
            Padding = new Thickness(4, 2, 4, 2),
            VerticalAlignment = VerticalAlignment.Center,
        };
    }
}
