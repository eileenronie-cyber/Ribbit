using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using Ribbit.Geometry.Sheets;
using Ribbit.Native.Settings;

namespace Ribbit.Native.Ui;

/// <summary>One line of the change list.</summary>
internal sealed class ImportLine
{
    public int ExcelRow { get; init; }
    public string Row => ExcelRow > 0 ? ExcelRow.ToString() : "";
    public string Sheet { get; init; } = "";
    public string Action { get; init; } = "";
    public string Details { get; init; } = "";

    /// <summary>ok / warn / bad: the colour of the Action cell.</summary>
    public string Tone { get; init; } = "";
}

/// <summary>
/// Step two of the sheet exchange: read the edited sheet list back and show
/// EVERY change it would make, before anything is written. Nothing happens
/// until Apply.
///
/// The plan is recomputed on every change of option and on Reload, so the
/// list is always what Apply will do - including after saving the workbook
/// again in Excel with this window still open.
/// </summary>
internal sealed class SheetImportWindow : Window
{
    private const string NoTitleblock = "(none - a placeholder sheet)";

    internal readonly ComboBox DefaultTitleblock;
    internal readonly CheckBox ExcelWins;
    internal readonly CheckBox ShowUnchanged;
    internal readonly ListView Changes;
    internal readonly TextBlock Summary;
    internal readonly TextBlock ProblemLine;
    internal readonly TextBlock FileLine;
    internal readonly TextBlock ModelWarning;
    internal readonly Button ApplyButton;

    private readonly string _path;
    private readonly Func<string, ExchangeFile> _read;
    private readonly Func<IReadOnlyList<ExchangeColumn>, IReadOnlyList<ModelSheet>> _snapshot;
    private readonly string _modelKey;
    private readonly IReadOnlyList<string> _titleblocks;
    private bool _built;

    internal ExchangeFile File { get; private set; }

    /// <summary>What Apply will do with the current options. Null only if the file could not be read.</summary>
    internal ImportPlan? Plan { get; private set; }

    internal ImportOptions Options => new(
        DefaultTitleblock.SelectedItem is string s && s != NoTitleblock ? s : "",
        ExcelWins.IsChecked == true);

    public SheetImportWindow(
        string path,
        ExchangeFile file,
        Func<string, ExchangeFile> read,
        Func<IReadOnlyList<ExchangeColumn>, IReadOnlyList<ModelSheet>> snapshot,
        string modelKey,
        IReadOnlyList<string> titleblocks,
        SheetMakerSettings saved,
        string suggestedTitleblock = "")
    {
        _path = path;
        File = file;
        _read = read;
        _snapshot = snapshot;
        _modelKey = modelKey;
        _titleblocks = titleblocks;

        Title = "Sheet Maker - import sheet list";
        Width = 980;
        Height = 720;
        MinWidth = 700;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = false;
        new WindowInteropHelper(this).Owner = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;

        // --- the file ---------------------------------------------------------------
        StackPanel top = new() { Margin = new Thickness(FormParts.Gutter, FormParts.Gutter, FormParts.Gutter, 0) };
        top.Children.Add(FormParts.Section(Path.GetFileName(path), first: true));
        FileLine = FormParts.Hint("");
        top.Children.Add(FileLine);
        ModelWarning = FormParts.Problem();
        ModelWarning.Margin = new Thickness(0, 0, 0, 8);
        top.Children.Add(ModelWarning);

        // --- options ------------------------------------------------------------------
        Grid opts = new() { Margin = new Thickness(0, 0, 0, 8) };
        opts.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        opts.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        TextBlock tbLabel = new() { Text = "Titleblock for new rows that name none:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        List<string> tbItems = [NoTitleblock, .. titleblocks];
        string wanted = saved.ImportDefaultTitleblock.Length > 0 && titleblocks.Contains(saved.ImportDefaultTitleblock)
            ? saved.ImportDefaultTitleblock
            : titleblocks.Contains(suggestedTitleblock) ? suggestedTitleblock : NoTitleblock;
        DefaultTitleblock = new ComboBox { ItemsSource = tbItems, SelectedItem = wanted, MaxWidth = 460, HorizontalAlignment = HorizontalAlignment.Left };
        DefaultTitleblock.SelectionChanged += (_, _) => Replan();
        Grid.SetColumn(DefaultTitleblock, 1);
        opts.Children.Add(tbLabel);
        opts.Children.Add(DefaultTitleblock);
        top.Children.Add(opts);

        ExcelWins = new CheckBox
        {
            Content = "Where a value was changed in BOTH Revit and Excel since the export, use Excel's (otherwise Revit's is kept)",
            IsChecked = saved.ImportExcelWins,
            Margin = new Thickness(0, 0, 0, 6),
        };
        ExcelWins.Click += (_, _) => Replan();
        top.Children.Add(ExcelWins);

        StackPanel listBar = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 6) };
        ShowUnchanged = new CheckBox { Content = "Show rows with nothing to change", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
        ShowUnchanged.Click += (_, _) => Fill();
        Button reload = new() { Content = "Reload file", MinHeight = 26, Padding = new Thickness(10, 2, 10, 2), ToolTip = "Read the workbook again - after saving more edits in Excel." };
        reload.Click += (_, _) => Reload();
        listBar.Children.Add(ShowUnchanged);
        listBar.Children.Add(reload);
        top.Children.Add(listBar);

        // --- the change list ---------------------------------------------------------------
        Changes = MakeList();

        // --- footer --------------------------------------------------------------------------
        Summary = new TextBlock { TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold };
        ProblemLine = FormParts.Problem();
        ProblemLine.Margin = new Thickness(0, 3, 0, 0);
        StackPanel band = new();
        band.Children.Add(Summary);
        band.Children.Add(ProblemLine);

        ApplyButton = FormParts.Primary("Apply");
        ApplyButton.Click += (_, _) => Accept();

        StackPanel footer = new();
        footer.Children.Add(new Border
        {
            Background = SystemColors.ControlLightBrush,
            BorderBrush = SystemColors.ControlDarkBrush,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(FormParts.Gutter, 10, FormParts.Gutter, 10),
            Margin = new Thickness(0, 10, 0, 0),
            Child = band,
        });
        footer.Children.Add(new Border
        {
            Padding = new Thickness(FormParts.Gutter, 0, FormParts.Gutter, FormParts.Gutter),
            Child = FormParts.ButtonBar(ApplyButton, FormParts.Secondary("Cancel", true)),
        });

        Border listHost = new() { Margin = new Thickness(FormParts.Gutter, 0, FormParts.Gutter, 0), Child = Changes };

        DockPanel root = new();
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(top);
        root.Children.Add(footer);
        root.Children.Add(listHost);
        Content = root;

        _built = true;
        Replan();
    }

    private static ListView MakeList()
    {
        GridView gv = new();
        gv.Columns.Add(Column("Row", nameof(ImportLine.Row), 48, wrap: false));
        gv.Columns.Add(Column("Sheet", nameof(ImportLine.Sheet), 190, wrap: false));
        gv.Columns.Add(ActionColumn());
        gv.Columns.Add(Column("What happens", nameof(ImportLine.Details), 560, wrap: true));

        Style item = new(typeof(ListViewItem));
        item.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        item.Setters.Add(new Setter(Control.VerticalContentAlignmentProperty, VerticalAlignment.Top));

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
        text.SetBinding(TextBlock.TextProperty, new Binding(nameof(ImportLine.Action)));
        text.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        DataTemplate template = new() { VisualTree = text };
        foreach ((string tone, Brush brush) in new[] { ("ok", Good), ("warn", Warn), ("bad", Bad) })
        {
            DataTrigger trigger = new() { Binding = new Binding(nameof(ImportLine.Tone)), Value = tone };
            trigger.Setters.Add(new Setter(TextBlock.ForegroundProperty, brush, "t"));
            template.Triggers.Add(trigger);
        }
        return new GridViewColumn { Header = "Action", Width = 110, CellTemplate = template };
    }

    /// <summary>Read the workbook again - Apply must never act on a file older than the one on disk.</summary>
    internal void Reload()
    {
        try
        {
            File = _read(_path);
        }
        catch (Exception ex)
        {
            Plan = null;
            Summary.Text = "Could not read the workbook.";
            SetProblem(ex.Message);
            Changes.ItemsSource = null;
            ApplyButton.IsEnabled = false;
            return;
        }
        Replan();
    }

    internal void Replan()
    {
        if (!_built)
            return;

        IReadOnlyList<ModelSheet> model = Snapshot();
        Plan = SheetImportPlanner.Plan(File, _modelKey, model, _titleblocks, Options);

        ExchangeMeta m = File.Meta;
        FileLine.Text = $"Exported from '{m.ModelTitle}' on {m.ExportedAt} by {m.ExportedBy}  -  "
                        + $"{File.Rows.Count} row(s), {File.Columns.Count(c => c.Role == ColumnRole.Parameter)} parameter column(s).";
        ModelWarning.Text = Plan.SameModel ? "" : Plan.FileNotes[0];
        ModelWarning.Visibility = Plan.SameModel ? Visibility.Collapsed : Visibility.Visible;

        Fill();

        ImportCounts c = Plan.Counts;
        List<string> parts = [];
        if (c.Create > 0) parts.Add($"{c.Create} to create");
        if (c.Update > 0) parts.Add($"{c.Update} to update");
        if (c.Unchanged > 0) parts.Add($"{c.Unchanged} unchanged");
        if (c.Skip > 0) parts.Add($"{c.Skip} left alone");
        if (c.Locked > 0) parts.Add($"{c.Locked} checked out");
        if (c.Error > 0) parts.Add($"{c.Error} with errors");
        Summary.Text = c.Rows == 0
            ? "The sheet list has no rows."
            : $"{c.Rows} row(s): {string.Join(", ", parts)}."
              + (c.Values > 0 ? $"  {c.Values} value(s) to write." : "");

        List<string> problems = [.. Plan.FileNotes.Skip(Plan.SameModel ? 0 : 1)];
        if (c.Conflicts > 0)
        {
            problems.Add(ExcelWins.IsChecked == true
                ? $"{c.Conflicts} value(s) were changed in Revit AND in Excel since the export - Excel's will be written."
                : $"{c.Conflicts} value(s) were changed in Revit AND in Excel since the export - Revit's are kept (tick the option above to use Excel's).");
        }
        if (c.Problems > 0)
            problems.Add($"{c.Problems} value(s) cannot be written as typed and will be skipped - see the list.");
        if (c.Error > 0)
            problems.Add($"{c.Error} row(s) cannot be imported - see the list.");
        SetProblem(string.Join("\n", problems));

        ApplyButton.IsEnabled = c.Actionable > 0;
        ApplyButton.Content = c.Actionable > 0 ? $"Apply {c.Actionable} sheet change(s)" : "Nothing to apply";
    }

    private IReadOnlyList<ModelSheet>? _model;
    private string _modelColumns = "";

    /// <summary>
    /// The model as the plan needs it, read once per set of columns. The window
    /// is modal, so the model cannot change under it - but reading it costs
    /// ~5 s on a 500-sheet model, and ticking an option used to pay that again.
    /// </summary>
    private IReadOnlyList<ModelSheet> Snapshot()
    {
        string key = string.Join("\u001f", File.Columns.Select(c => $"{c.Header}|{c.Parameter}|{c.Kind}"));
        if (_model is null || key != _modelColumns)
        {
            _model = _snapshot(File.Columns);
            _modelColumns = key;
            ModelReads++;
        }
        return _model;
    }

    /// <summary>How many times the model has been read - for the test that it is not re-read per click.</summary>
    internal int ModelReads { get; private set; }

    private void SetProblem(string text)
    {
        ProblemLine.Text = text;
        ProblemLine.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>The lines currently listed (the unchanged ones only when asked for).</summary>
    internal List<ImportLine> Lines { get; private set; } = [];

    private void Fill()
    {
        if (Plan is null)
            return;
        bool all = ShowUnchanged.IsChecked == true;
        Lines = [.. Plan.Rows
            .Where(r => all || r.Action != ImportAction.Unchanged || r.Conflicts.Count > 0 || r.Problems.Count > 0 || r.Notes.Count > 0)
            .Select(r => ToLine(r, _titleblocks))];
        Changes.ItemsSource = Lines;
    }

    internal static ImportLine ToLine(ImportRow r, IReadOnlyList<string>? titleblocks = null)
    {
        List<string> lines = [];

        foreach (CellChange c in r.Changes)
        {
            if (r.Action == ImportAction.Create)
                lines.Add($"{c.Column.Header}: {Show(c.To)}");
            else
                lines.Add($"{c.Column.Header}: {Show(c.From)} -> {Show(c.To)}" + (c.BothChanged ? "  (also changed in Revit)" : ""));
        }
        if (r.Action == ImportAction.Create)
        {
            lines.Add(r.TitleblockIndex is not int tb
                ? "Titleblock: none (placeholder sheet)"
                : titleblocks is not null && tb < titleblocks.Count ? $"Titleblock: {titleblocks[tb]}" : "");
        }
        foreach (CellChange c in r.Conflicts)
            lines.Add($"{c.Column.Header}: kept Revit's {Show(c.From)} - Excel says {Show(c.To)}");
        foreach (CellProblem p in r.Problems)
            lines.Add($"{p.Column.Header}: not written - {p.Reason}");
        lines.AddRange(r.Notes);
        if (r.Reason is not null)
            lines.Insert(0, r.Reason);

        return new ImportLine
        {
            ExcelRow = r.ExcelRow,
            Sheet = r.Name.Length > 0 ? $"{r.Number} - {r.Name}" : r.Number,
            Action = r.Action switch
            {
                ImportAction.Create => "create",
                ImportAction.Update => "update",
                ImportAction.Unchanged => r.Conflicts.Count > 0 ? "kept Revit's" : "unchanged",
                ImportAction.Skip => "left alone",
                ImportAction.Locked => "checked out",
                _ => "error",
            },
            Tone = r.Action switch
            {
                ImportAction.Create or ImportAction.Update => r.Problems.Count > 0 ? "warn" : "ok",
                ImportAction.Error => "bad",
                ImportAction.Unchanged when r.Conflicts.Count == 0 && r.Problems.Count == 0 => "",
                _ => "warn",
            },
            Details = string.Join("\n", lines.Where(l => l.Length > 0)),
        };
    }

    private static string Show(string value) => value.Length == 0 ? "(blank)" : $"\"{value}\"";

    /// <summary>Everything the list says, for telling whether a reload changed it.</summary>
    private string Fingerprint()
        => Plan is null ? "" : string.Join("\u001f", Plan.Rows.Select(r => ToLine(r, _titleblocks)).Select(l => $"{l.Row}|{l.Sheet}|{l.Action}|{l.Details}"));

    private void Accept()
    {
        // The workbook may have been saved again in Excel since it was read.
        // Applying what is on disk NOW without showing it would write changes
        // nobody looked at - so a list that moved is shown again instead.
        string shown = Fingerprint();
        Reload();
        if (Plan is null || Plan.Counts.Actionable == 0)
            return;

        if (Fingerprint() != shown)
        {
            SetProblem("The workbook has changed since this list was shown (saved again in Excel?). "
                       + "The list is now up to date - check it, then Apply again.");
            return;
        }

        DialogResult = true;
    }
}
