using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Interop;
using Autodesk.Revit.DB;
using Ribbit.Geometry.Sheets;
using Ribbit.Native.Revit;
using Ribbit.Native.Settings;
// Autodesk.Revit.DB has a Grid too (the datum kind); the WPF one wins here.
using Grid = System.Windows.Controls.Grid;

namespace Ribbit.Native.Ui;

/// <summary>One sheet the export can include.</summary>
internal sealed record ExportableSheet(ElementId Id, string UniqueId, string Number, string Name, bool Placeholder);

/// <summary>A saved sheet set (Print Setup's "View/Sheet Set"), by the sheets it holds.</summary>
internal sealed record ExportSheetSet(string Name, IReadOnlyCollection<ElementId> Sheets);

/// <summary>What the export window hands back: everything the tool needs, and nothing it must re-ask.</summary>
internal sealed record SheetExportRequest(
    List<SheetParameterInfo> Parameters,
    List<ElementId> Sheets,
    bool IncludeTitleblock,
    bool OpenAfter);

/// <summary>One parameter in the export list.</summary>
internal sealed class ExportParamRow(SheetParameterInfo info) : CheckRow
{
    public SheetParameterInfo Info { get; } = info;
    public string Name => Info.Name;
    public string Where => Info.HomeLabel;

    public string Kind => Info.Kind switch
    {
        ValueKind.Integer => "whole number",
        ValueKind.YesNo => "Yes/No",
        ValueKind.Number => "number (project units)",
        _ => "text",
    };

    public string Note => Info.ReadOnly ? "read-only" : "";
}

/// <summary>One sheet in the export list.</summary>
internal sealed class ExportSheetRow(ExportableSheet sheet) : CheckRow
{
    public ExportableSheet Sheet { get; } = sheet;
    public string Number => Sheet.Number;
    public string Name => Sheet.Name;
    public string Note => Sheet.Placeholder ? "placeholder" : "";
}

/// <summary>
/// Step one of the sheet exchange: choose the SHEETS and the PARAMETERS in
/// Revit - both from what the model actually has - and write them out as a
/// sheet list to edit in Excel. Choosing parameters here is the point: a column
/// in the file can then never name a parameter the model lacks, which was
/// SheetMaker's quietest failure. Choosing sheets here keeps the file to the
/// drawings being worked on.
/// </summary>
internal sealed class SheetExportWindow : Window
{
    /// <summary>Ticked on a first export, where the model has them.</summary>
    private static readonly string[] CommonParameters =
        ["Drawn By", "Checked By", "Designed By", "Approved By", "Sheet Issue Date"];

    private const string PickASet = "Tick a sheet set...";

    internal readonly TextBox Filter;
    internal readonly ListView Params;
    internal readonly StackPanel ParamButtons;
    internal readonly TextBox SheetFilter;
    internal readonly ListView SheetList;
    internal readonly StackPanel SheetButtons;
    internal readonly ComboBox SheetSets;
    internal readonly CheckBox Titleblock;
    internal readonly CheckBox OpenAfter;
    internal readonly TextBlock Summary;
    internal readonly TextBlock ProblemLine;
    internal readonly Button ExportButton;

    private readonly List<ExportParamRow> _rows;
    private readonly List<ExportSheetRow> _sheetRows;
    private readonly IReadOnlyList<ExportSheetSet> _sets;
    private bool _built;

    public SheetExportRequest? Result { get; private set; }

    /// <summary>Where the ticks started from, for the hint above the list.</summary>
    internal string StartedFrom { get; }

    public SheetExportWindow(
        IReadOnlyList<SheetParameterInfo> catalog,
        IReadOnlyList<ExportableSheet> sheets,
        IReadOnlyCollection<ElementId> selectedInRevit,
        SheetMakerSettings saved,
        IReadOnlyList<ExportSheetSet>? sheetSets = null)
    {
        _sets = sheetSets ?? [];

        Title = "Export Sheets to Excel";
        System.Windows.Size area = RevitScreen.WorkArea();
        Width = Math.Min(1180, area.Width - 40);
        Height = Math.Min(760, area.Height - 16);
        MinWidth = Math.Min(860, Width);
        MinHeight = Math.Min(540, Height);
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = false;
        new WindowInteropHelper(this).Owner = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;

        // --- which sheets start ticked ------------------------------------------------
        // Most specific first: what is selected in Revit right now, else the
        // sheets ticked last time (those still in this model), else all.
        HashSet<ElementId> selected = [.. selectedInRevit];
        HashSet<string> last = new(saved.ExportSheetIds, StringComparer.OrdinalIgnoreCase);
        Func<ExportableSheet, bool> ticked;
        if (sheets.Any(s => selected.Contains(s.Id)))
        {
            ticked = s => selected.Contains(s.Id);
            StartedFrom = "Ticked: the sheets selected in Revit.";
        }
        else if (sheets.Any(s => last.Contains(s.UniqueId)))
        {
            ticked = s => last.Contains(s.UniqueId);
            StartedFrom = "Ticked: the sheets you exported last time.";
        }
        else
        {
            ticked = _ => true;
            StartedFrom = "Ticked: every sheet.";
        }

        _sheetRows = [.. sheets.Select(s => new ExportSheetRow(s) { IsChecked = ticked(s) })];
        foreach (ExportSheetRow r in _sheetRows)
            r.CheckChanged = Refresh;

        // --- parameters ---------------------------------------------------------------
        HashSet<string> remembered = new(saved.ExportParameters, StringComparer.OrdinalIgnoreCase);
        bool firstTime = remembered.Count == 0;

        _rows =
        [
            .. catalog.Where(p => p.Kind is not null && !p.IsKey)
                      .Select(p => new ExportParamRow(p)
                      {
                          IsChecked = firstTime
                              ? CommonParameters.Contains(p.Name, StringComparer.OrdinalIgnoreCase) && !p.ReadOnly
                              : remembered.Contains(p.Name),
                      })
        ];
        foreach (ExportParamRow r in _rows)
            r.CheckChanged = Refresh;

        // --- left: sheets -------------------------------------------------------------
        (Grid sheetFilterHost, TextBox sheetFilter) = FormParts.PlaceholderInput("Filter by number or name, e.g. M-2...");
        SheetFilter = sheetFilter;
        SheetList = GridAdjustWindow.MakeList(
        [
            ("Number", nameof(ExportSheetRow.Number), 110, null),
            ("Name", nameof(ExportSheetRow.Name), 250, null),
            ("", nameof(ExportSheetRow.Note), 80, null),
        ]);
        SheetList.ItemsSource = _sheetRows;
        ICollectionView sheetView = CollectionViewSource.GetDefaultView(_sheetRows);
        sheetView.Filter = o => o is ExportSheetRow r && Matches(SheetFilter.Text, r.Number, r.Name);
        SheetFilter.TextChanged += (_, _) => sheetView.Refresh();

        SheetSets = new ComboBox
        {
            ItemsSource = new[] { PickASet }.Concat(_sets.Select(s => $"{s.Name} ({s.Sheets.Count})")).ToList(),
            SelectedIndex = 0,
            IsEnabled = _sets.Count > 0,
            MinWidth = 170,
            Margin = new Thickness(6, 0, 0, 0),
            ToolTip = _sets.Count > 0
                ? "Tick exactly the sheets in a saved sheet set (Print Setup > View/Sheet Set)."
                : "This model has no saved sheet sets.",
        };
        SheetSets.SelectionChanged += (_, _) => ApplySet();

        SheetButtons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        SheetButtons.Children.Add(Small("Tick all shown", () => SetShown(_sheetRows, true)));
        SheetButtons.Children.Add(Small("Untick all shown", () => SetShown(_sheetRows, false)));
        SheetButtons.Children.Add(SheetSets);

        UIElement sheetsPane = Pane(
            "Sheets",
            "Tick the sheets to put in the list. Filter, then 'Tick all shown' to take a whole "
            + "series at once. " + StartedFrom,
            sheetFilterHost, SheetList, SheetButtons);

        // --- right: parameters ------------------------------------------------------------
        (Grid filterHost, TextBox filter) = FormParts.PlaceholderInput("Filter parameters...");
        Filter = filter;
        Params = GridAdjustWindow.MakeList(
        [
            ("Parameter", nameof(ExportParamRow.Name), 210, null),
            ("On", nameof(ExportParamRow.Where), 110, null),
            ("Value", nameof(ExportParamRow.Kind), 130, null),
            ("", nameof(ExportParamRow.Note), 90, null),
        ]);
        Params.ItemsSource = _rows;
        ICollectionView view = CollectionViewSource.GetDefaultView(_rows);
        view.Filter = o => o is ExportParamRow r && Matches(Filter.Text, r.Name);
        Filter.TextChanged += (_, _) => view.Refresh();

        ParamButtons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        ParamButtons.Children.Add(Small("Tick all shown", () => SetShown(_rows, true)));
        ParamButtons.Children.Add(Small("Untick all shown", () => SetShown(_rows, false)));

        UIElement paramsPane = Pane(
            "Parameters",
            "Each ticked parameter becomes a column, named exactly as in Revit. Sheet Number and "
            + "Sheet Name are always included. Read-only ones come out greyed, for reference.",
            filterHost, Params, ParamButtons);

        Grid panes = new() { Margin = new Thickness(FormParts.Gutter, FormParts.Gutter, FormParts.Gutter, 0) };
        panes.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        panes.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(FormParts.Gutter) });
        panes.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.2, GridUnitType.Star) });
        Grid.SetColumn(paramsPane, 2);
        panes.Children.Add(sheetsPane);
        panes.Children.Add(paramsPane);

        // --- options and footer --------------------------------------------------------------
        StackPanel options = new() { Margin = new Thickness(FormParts.Gutter, 10, FormParts.Gutter, 0) };
        Titleblock = new CheckBox
        {
            Content = "Include a Titleblock column (with a dropdown of the model's titleblock types)",
            IsChecked = saved.ExportTitleblock,
            Margin = new Thickness(0, 0, 0, 4),
        };
        OpenAfter = new CheckBox { Content = "Open it in Excel when it is saved", IsChecked = saved.OpenAfterExport };
        Titleblock.Click += (_, _) => Refresh();
        options.Children.Add(Titleblock);
        options.Children.Add(OpenAfter);

        Summary = new TextBlock { TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold };
        ProblemLine = FormParts.Problem();
        ProblemLine.Margin = new Thickness(0, 3, 0, 0);
        StackPanel bandContent = new();
        bandContent.Children.Add(Summary);
        bandContent.Children.Add(ProblemLine);

        ExportButton = FormParts.Primary("Export...");
        ExportButton.Click += (_, _) => Accept();

        StackPanel below = new();
        below.Children.Add(options);
        below.Children.Add(new Border
        {
            Background = SystemColors.ControlLightBrush,
            BorderBrush = SystemColors.ControlDarkBrush,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(FormParts.Gutter, 10, FormParts.Gutter, 10),
            Margin = new Thickness(0, 10, 0, 0),
            Child = bandContent,
        });
        below.Children.Add(new Border
        {
            Padding = new Thickness(FormParts.Gutter, 0, FormParts.Gutter, FormParts.Gutter),
            Child = FormParts.ButtonBar(ExportButton, FormParts.Secondary("Cancel", true)),
        });

        // The lists take whatever height is left, so a long list scrolls inside
        // itself and never pushes the buttons off the window.
        DockPanel root = new();
        DockPanel.SetDock(below, Dock.Bottom);
        root.Children.Add(below);
        root.Children.Add(panes);
        Content = root;

        _built = true;
        Refresh();
    }

    /// <summary>A titled column: heading, hint, filter, then the list filling the rest above its buttons.</summary>
    private static UIElement Pane(string title, string hint, UIElement filter, ListView list, UIElement buttons)
    {
        StackPanel head = new();
        head.Children.Add(FormParts.Section(title, first: true));
        head.Children.Add(FormParts.Hint(hint));
        if (filter is FrameworkElement fe)
            fe.Margin = new Thickness(0, 0, 0, 6);
        head.Children.Add(filter);

        DockPanel pane = new();
        DockPanel.SetDock(head, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        pane.Children.Add(head);
        pane.Children.Add(buttons);
        pane.Children.Add(list);
        return pane;
    }

    /// <summary>Every word typed appears somewhere in the fields (so "M 2" finds "M-201 Level 2").</summary>
    private static bool Matches(string typed, params string[] fields)
    {
        string[] words = typed.Split([' '], StringSplitOptions.RemoveEmptyEntries);
        return words.All(w => fields.Any(f => f.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0));
    }

    private static Button Small(string text, Action onClick)
    {
        Button b = new() { Content = text, MinHeight = 26, Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(0, 0, 6, 0) };
        b.Click += (_, _) => onClick();
        return b;
    }

    /// <summary>Only the rows the filter shows - the whole point of filtering first.</summary>
    private void SetShown<T>(List<T> rows, bool state) where T : CheckRow
    {
        foreach (T r in CollectionViewSource.GetDefaultView(rows).Cast<T>())
            r.IsChecked = state;
        Refresh();
    }

    private void ApplySet()
    {
        int i = SheetSets.SelectedIndex - 1;
        if (i < 0 || i >= _sets.Count)
            return;

        HashSet<ElementId> inSet = [.. _sets[i].Sheets];
        foreach (ExportSheetRow r in _sheetRows)
            r.IsChecked = inSet.Contains(r.Sheet.Id);
        SheetFilter.Text = "";
        SheetSets.SelectedIndex = 0;   // an action, not a state: back to the prompt
        Refresh();
    }

    internal IReadOnlyList<ExportParamRow> Rows => _rows;

    internal IReadOnlyList<ExportSheetRow> SheetRows => _sheetRows;

    /// <summary>
    /// What Export would do right now, or null with the reason in
    /// <paramref name="why"/>. The Export button and the tests both ask this,
    /// so a test sees exactly what a click would send.
    /// </summary>
    internal SheetExportRequest? Prepare(out string why)
    {
        List<ExportableSheet> sheets = [.. _sheetRows.Where(r => r.IsChecked).Select(r => r.Sheet)];
        List<SheetParameterInfo> chosen = [.. _rows.Where(r => r.IsChecked).Select(r => r.Info)];

        if (sheets.Count == 0)
        {
            why = _sheetRows.Count == 0
                ? "This model has no sheets yet - there is nothing to export. Sheet Maker can still create sheets from a list made elsewhere."
                : "Tick at least one sheet.";
            return null;
        }

        why = "";
        return new SheetExportRequest(
            chosen,
            [.. sheets.Select(s => s.Id)],
            Titleblock.IsChecked == true,
            OpenAfter.IsChecked == true);
    }

    private void Refresh()
    {
        if (!_built)
            return;

        SheetExportRequest? request = Prepare(out string why);
        int columns = 2 + (Titleblock.IsChecked == true ? 1 : 0) + _rows.Count(r => r.IsChecked);

        Summary.Text = request is null
            ? "Nothing to export."
            : $"{request.Sheets.Count} of {_sheetRows.Count} sheet(s) x {columns} column(s): Sheet Number, Sheet Name"
              + (Titleblock.IsChecked == true ? ", Titleblock" : "")
              + (request.Parameters.Count > 0 ? $" and {request.Parameters.Count} parameter(s)." : ".");

        ProblemLine.Text = why;
        ProblemLine.Visibility = why.Length > 0 ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
        ExportButton.IsEnabled = request is not null;
    }

    private void Accept()
    {
        Result = Prepare(out _);
        if (Result is not null)
            DialogResult = true;
    }
}
