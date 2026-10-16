using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Ribbit.Geometry.Text;
using Ribbit.Native.Revit;

namespace Ribbit.Native.Ui;

/// <summary>How the Number Text Notes window was closed.</summary>
internal enum NotesOutcome { Cancel, Apply, Pick }

/// <summary>
/// Number Text Notes' one window (was the WinForms dialog in NumberTextNotes v6.dyn).
///
/// Left, the notes; right, the CLICKED note as it will read, which you can edit
/// (one line per item) - every note keeps its own edit, where the graph only let
/// you edit when exactly one note was picked. "Pick notes..." closes the window
/// while you pick (a modal window disables Revit's own) and the tool reopens it
/// with everything as it was.
///
/// Enter never applies - it edits the model, and the caret spends its life in
/// the preview, where Enter starts a new item. Apply is a click; Esc cancels.
/// </summary>
internal sealed class NumberNotesWindow : Window
{
    internal const string DialogTitle = "Number Text Notes";

    internal readonly ListBox NoteList;
    internal readonly TextBox SkipBox;
    internal readonly RadioButton ListMode;
    internal readonly RadioButton TypedMode;
    internal readonly RadioButton RemoveMode;
    internal readonly TextBox StartBox;
    internal readonly ComboBox FormatBox;
    internal readonly TextBox Preview;
    internal readonly TextBlock PreviewLabel;
    internal readonly Button UndoEdits;
    internal readonly TextBlock Warning;
    internal readonly TextBlock Problem;
    internal readonly TextBlock Summary;
    internal readonly Button ApplyButton;
    internal readonly Button PickButton;

    private readonly List<NoteInfo> _notes;
    private readonly Dictionary<long, string> _edits;
    private bool _updating;

    internal NotesOutcome Outcome { get; private set; } = NotesOutcome.Cancel;

    /// <summary>The user's own preview text, per note id. Kept across a Pick.</summary>
    internal IReadOnlyDictionary<long, string> Edits => _edits;

    internal IReadOnlyList<NoteInfo> Notes => _notes;

    public NumberNotesWindow(IReadOnlyList<NoteInfo> notes, NumberSettings initial, IReadOnlyDictionary<long, string>? edits = null)
    {
        _notes = [.. notes];
        _edits = edits?.Where(kv => notes.Any(n => n.Id == kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value) ?? [];

        Title = DialogTitle;
        Size area = RevitScreen.WorkArea();
        Width = Math.Min(980, area.Width - 40);
        Height = Math.Min(660, area.Height - 40);
        MinWidth = 760;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = false;
        new WindowInteropHelper(this).Owner = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;

        // ---- left: notes and settings ------------------------------------------------
        StackPanel left = new();
        left.Children.Add(FormParts.Section("Notes", first: true));
        NoteList = new ListBox { MinHeight = 120, MaxHeight = 190, DisplayMemberPath = nameof(NoteRow.Text) };
        NoteList.SelectionChanged += (_, _) => Refresh();
        left.Children.Add(NoteList);
        PickButton = FormParts.Secondary("Pick notes...");
        PickButton.HorizontalAlignment = HorizontalAlignment.Left;
        PickButton.Margin = new Thickness(0, 6, 0, 0);
        PickButton.ToolTip = "Closes this window while you click text notes in the view, then opens it again with everything as it was.";
        PickButton.Click += (_, _) => { Outcome = NotesOutcome.Pick; DialogResult = true; };
        left.Children.Add(PickButton);

        left.Children.Add(FormParts.Section("Numbering"));
        Grid skipRow = Row("Header lines to skip:", SkipBox = SmallBox(initial.SkipLines.ToString()),
            "The first N lines (a \"GENERAL NOTES\" title, say) are left as they are. A blank line under them stays.");
        left.Children.Add(skipRow);

        ListMode = Radio("Revit list - hanging indent, renumbers itself", initial.Mode == NumberMode.List);
        TypedMode = Radio("Typed numbers - \"1.<tab>Text\", your start and format", initial.Mode == NumberMode.Typed);
        RemoveMode = Radio("Remove numbering - strip numbers, Revit list off", initial.Mode == NumberMode.Remove);
        left.Children.Add(ListMode);
        left.Children.Add(TypedMode);

        StackPanel typedRow = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(22, 2, 0, 6) };
        typedRow.Children.Add(new TextBlock { Text = "Start at:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        StartBox = SmallBox(initial.Start.ToString());
        typedRow.Children.Add(StartBox);
        typedRow.Children.Add(new TextBlock { Text = "Format:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 6, 0) });
        FormatBox = new ComboBox { IsEditable = true, Width = 90, MinHeight = 26, Text = initial.Format, ToolTip = "Any text with {n} where the number goes, e.g. {n}. or ({n}) or N{n}" };
        foreach (string f in NoteNumbering.Formats)
            FormatBox.Items.Add(f);
        FormatBox.Text = initial.Format;
        FormatBox.AddHandler(System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent, new TextChangedEventHandler((_, _) => Refresh()));
        FormatBox.SelectionChanged += (_, _) => Dispatcher.BeginInvoke(new Action(Refresh));
        typedRow.Children.Add(FormatBox);
        left.Children.Add(typedRow);
        left.Children.Add(RemoveMode);

        left.Children.Add(FormParts.Hint(
            "Numbers typed by hand are stripped first, so running it again is safe. \"3.5 m\" and \"2-way\" are not numbers. "
            + "A line with no number in a hand-numbered note is joined to the item above it."));

        foreach (TextBox b in new[] { SkipBox, StartBox })
            b.TextChanged += (_, _) => Refresh();
        foreach (RadioButton r in new[] { ListMode, TypedMode, RemoveMode })
            r.Checked += (_, _) => Refresh();

        // ---- right: preview --------------------------------------------------------------
        DockPanel right = new();
        DockPanel previewHead = new() { Margin = new Thickness(0, 0, 0, 6) };
        UndoEdits = new Button { Content = "Undo edits", Padding = new Thickness(10, 2, 10, 2), MinHeight = 24, ToolTip = "Throw away your edits to this note's preview." };
        UndoEdits.Click += (_, _) =>
        {
            if (Current() is { } n)
                _edits.Remove(n.Id);
            Refresh();
        };
        DockPanel.SetDock(UndoEdits, Dock.Right);
        previewHead.Children.Add(UndoEdits);
        PreviewLabel = new TextBlock { FontWeight = FontWeights.SemiBold, FontSize = 14, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        previewHead.Children.Add(PreviewLabel);
        DockPanel.SetDock(previewHead, Dock.Top);
        right.Children.Add(previewHead);

        Warning = FormParts.Problem();
        Warning.Margin = new Thickness(0, 6, 0, 0);
        DockPanel.SetDock(Warning, Dock.Bottom);
        right.Children.Add(Warning);

        Preview = new TextBox
        {
            AcceptsReturn = true,
            AcceptsTab = false,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12.5,
            Padding = new Thickness(6),
        };
        Preview.TextChanged += (_, _) => PreviewEdited();
        right.Children.Add(Preview);

        Grid middle = new() { Margin = new Thickness(FormParts.Gutter, FormParts.Gutter, FormParts.Gutter, 0) };
        middle.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(380) });
        middle.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
        middle.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ScrollViewer leftScroll = new() { Content = left, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(0, 0, 4, 0) };
        middle.Children.Add(leftScroll);
        Grid.SetColumn(right, 2);
        middle.Children.Add(right);

        // ---- footer ----------------------------------------------------------------------------
        Summary = new TextBlock { TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold };
        Problem = FormParts.Problem();
        Problem.Margin = new Thickness(0, 4, 0, 0);
        StackPanel band = new();
        band.Children.Add(Summary);
        band.Children.Add(Problem);

        ApplyButton = FormParts.Primary("Apply");
        ApplyButton.IsDefault = false;   // Enter must never apply
        ApplyButton.Click += (_, _) => { Outcome = NotesOutcome.Apply; DialogResult = true; };

        StackPanel footer = new();
        footer.Children.Add(new Border
        {
            Background = SystemColors.ControlLightBrush,
            BorderBrush = SystemColors.ControlDarkBrush,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(FormParts.Gutter, 10, FormParts.Gutter, 10),
            Margin = new Thickness(0, 12, 0, 0),
            Child = band,
        });
        footer.Children.Add(new Border
        {
            Padding = new Thickness(FormParts.Gutter, 0, FormParts.Gutter, FormParts.Gutter),
            Child = FormParts.ButtonBar(ApplyButton, FormParts.Secondary("Cancel", true)),
        });

        DockPanel root = new();
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        root.Children.Add(middle);
        Content = root;

        FillNoteList();
        if (NoteList.Items.Count > 0)
            NoteList.SelectedIndex = 0;
        Refresh();
    }

    // ------------------------------------------------------------------ building bits

    private sealed record NoteRow(NoteInfo Note, string Text);

    private static TextBox SmallBox(string text) => new()
    {
        Text = text,
        Width = 56,
        MinHeight = 26,
        Padding = new Thickness(6, 3, 6, 3),
        VerticalContentAlignment = VerticalAlignment.Center,
    };

    private static RadioButton Radio(string text, bool on) => new()
    {
        Content = text,
        IsChecked = on,
        GroupName = "mode",
        Margin = new Thickness(0, 4, 0, 2),
    };

    private static Grid Row(string label, FrameworkElement box, string tip)
    {
        Grid g = new() { Margin = new Thickness(0, 0, 0, 8), ToolTip = tip };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        TextBlock t = new() { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        g.Children.Add(t);
        Grid.SetColumn(box, 1);
        g.Children.Add(box);
        return g;
    }

    private void FillNoteList()
    {
        int keep = NoteList.SelectedIndex;
        NoteSettingsOrNull(out NumberSettings? s);
        NoteList.Items.Clear();
        foreach (NoteInfo n in _notes)
        {
            string tail = "";
            if (s is not null)
            {
                NotePlan? p = PlanFor(n, s);
                tail = p is null ? "   - nothing below the header"
                    : $"   - {p.Count} item(s){(_edits.ContainsKey(n.Id) ? ", edited" : "")}{(p.Warning.Length > 0 ? ", CHECK" : "")}";
            }
            NoteList.Items.Add(new NoteRow(n, n.Label + tail));
        }
        if (keep >= 0 && keep < NoteList.Items.Count)
            NoteList.SelectedIndex = keep;
    }

    // ------------------------------------------------------------------ state

    private NoteInfo? Current() => (NoteList.SelectedItem as NoteRow)?.Note;

    internal NumberMode Mode => TypedMode.IsChecked == true ? NumberMode.Typed
        : RemoveMode.IsChecked == true ? NumberMode.Remove : NumberMode.List;

    /// <summary>The settings as typed, or why they cannot be used.</summary>
    internal string? NoteSettingsOrNull(out NumberSettings? settings)
    {
        settings = null;
        if (!int.TryParse(SkipBox.Text.Trim(), out int skip) || skip < 0 || skip > 50)
            return "Header lines to skip must be a whole number from 0 to 50.";
        int start = 1;
        if (Mode == NumberMode.Typed && (!int.TryParse(StartBox.Text.Trim(), out start) || start < 0 || start > 9999))
            return "Start at must be a whole number from 0 to 9999.";
        NumberSettings s = new(skip, start, Mode, (FormatBox.Text ?? "").Trim());
        if (Mode != NumberMode.Typed)
            s = s with { Format = NoteNumbering.FormatOk(s.Format) ? s.Format : "{n}." };
        string? why = NoteNumbering.Validate(s);
        if (why is not null)
            return char.ToUpperInvariant(why[0]) + why.Substring(1) + ".";
        settings = s;
        return null;
    }

    internal NumberSettings? Settings => NoteSettingsOrNull(out NumberSettings? s) is null ? s : null;

    private NotePlan? PlanFor(NoteInfo n, NumberSettings s)
        => NoteNumbering.Plan(n.Plain, s, _edits.TryGetValue(n.Id, out string? e) ? EditToSource(e) : null);

    /// <summary>The preview box shows "\r\n"; the planner wants the user's lines as typed.</summary>
    private static string EditToSource(string edited) => edited.Replace("\r\n", "\n");

    /// <summary>Recompute everything the window shows from the settings and the notes.</summary>
    internal void Refresh()
    {
        if (_updating || ApplyButton is null)
            return;
        _updating = true;
        try
        {
            bool typed = Mode == NumberMode.Typed;
            StartBox.IsEnabled = typed;
            FormatBox.IsEnabled = typed;

            string? why = NoteSettingsOrNull(out NumberSettings? s);
            FillNoteList();
            NoteInfo? n = Current();
            bool edited = n is not null && _edits.ContainsKey(n.Id);
            UndoEdits.IsEnabled = edited;

            PreviewLabel.Text = n is null ? "Preview"
                : edited ? "Preview - edited, Apply writes this text"
                : "Preview - you can edit it (one line per item)";
            Preview.IsReadOnly = n is null || s is null;

            if (s is null || n is null)
            {
                Preview.Text = "";
                SetWarning("");
            }
            else
            {
                string text = edited
                    ? NoteNumbering.PreviewText(EditToSource(_edits[n.Id]), s, join: false)
                    : NoteNumbering.PreviewText(n.Plain, s);
                if (!edited)
                    Preview.Text = text.Replace("\n", "\r\n");
                SetWarning(edited ? "" : NoteNumbering.NumberWarning(n.Plain, s.SkipLines, s.Format));
            }

            if (_notes.Count == 0)
            {
                Summary.Text = "No text note picked yet - press Pick notes... and click the notes in the view.";
                SetProblem(why ?? "");
                ApplyButton.Content = "Apply";
                ApplyButton.IsEnabled = false;
                return;
            }

            SetProblem(why ?? "");
            if (s is null)
            {
                Summary.Text = "";
                ApplyButton.IsEnabled = false;
                return;
            }

            List<NotePlan?> plans = [.. _notes.Select(x => PlanFor(x, s))];
            int ready = plans.Count(p => p is not null);
            int items = plans.Sum(p => p?.Count ?? 0);
            int checks = plans.Count(p => p is not null && p.Warning.Length > 0);
            string verb = s.Mode == NumberMode.Remove ? "un-numbered" : "numbered";
            Summary.Text = $"{_notes.Count} note(s): {items} paragraph(s) in {ready} note(s) will be {verb}."
                           + (ready < _notes.Count ? $" {_notes.Count - ready} have nothing below the header and are left alone." : "")
                           + (checks > 0 ? $" {checks} note(s) have a gap in their typed numbers - see CHECK." : "");
            ApplyButton.Content = s.Mode == NumberMode.Remove ? $"Remove numbering ({ready})" : $"Number {ready} note(s)";
            ApplyButton.IsEnabled = ready > 0;
        }
        finally
        {
            _updating = false;
        }
    }

    private void PreviewEdited()
    {
        if (_updating || Preview.IsReadOnly || Current() is not { } n)
            return;
        _edits[n.Id] = Preview.Text;
        // Labels and counts only: Refresh never rewrites an edited preview, which would move the caret while typing.
        Refresh();
    }

    private void SetProblem(string text)
    {
        Problem.Text = text;
        Problem.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetWarning(string text)
    {
        Warning.Text = text;
        Warning.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Tests: pick a note in the list by id.</summary>
    internal void Select(long id)
    {
        for (int i = 0; i < NoteList.Items.Count; i++)
            if (NoteList.Items[i] is NoteRow r && r.Note.Id == id)
                NoteList.SelectedIndex = i;
    }
}
