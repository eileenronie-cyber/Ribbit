using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using Ribbit.Geometry.Clash;

namespace Ribbit.Native.Ui;

/// <summary>
/// One flaggable row.
///
/// Either <paramref name="ClashKey"/> is set (the normal case — one row per
/// CLASH, naming both sides) or <paramref name="ElementId"/> is (a report
/// written before status became pair-keyed). Never both.
/// </summary>
internal sealed record StatusRow(
    string ClashKey, string ElementId, string Name, string Status,
    IReadOnlyList<StatusSide>? Sides = null);

/// <summary>
/// One side of a clash, kept in pieces so the id can be made clickable.
///
/// <paramref name="Name"/> is the report's own element name and is NOT parsed
/// back out of <see cref="StatusRow.Name"/> — that string is a display join,
/// and splitting it to recover the id would tie selection to a label's
/// punctuation.
/// </summary>
internal sealed record StatusSide(string ElementId, string Link, string Name);

/// <summary>
/// The status editor. One row per clashing element with three buckets, and Save
/// re-exports the FULL report with the new statuses applied.
///
/// SHOWN MODELESS so the Revit model stays interactive. That is safe here for a
/// specific reason: nothing after this window opens ever touches the Revit API —
/// it is plain CSV I/O only.
///
/// The Python needed __persistentengine__ for this, because pyRevit tears down a
/// script's IronPython engine as soon as its top-level code finishes, and
/// clicking a button afterwards crashed Revit outright rather than raising.
/// That whole failure class simply does not exist here: a WPF window and its
/// handlers are ordinary managed objects with an ordinary lifetime.
///
/// There is no way to clear a status back to blank. The ledger only ever merges
/// NON-EMPTY values forward, so a blank written here would be silently skipped
/// on the next merge rather than clearing anything. Change it to one of the
/// other buckets instead; true clearing would need a tombstone the ledger does
/// not have.
/// </summary>
internal sealed class SetClashStatusWindow : Window
{
    private static readonly StatusBucket[] BucketOrder =
    [
        StatusBucket.Resolved, StatusBucket.Pending,
        StatusBucket.Coordination, StatusBucket.Accepted,
    ];

    private readonly List<(StatusRow Row, Dictionary<StatusBucket, RadioButton> Buttons)> _controls = [];

    private readonly string _sourcePath;

    /// <summary>Raised on Save with every row that has a bucket selected.</summary>
    public event Action<List<(string ClashKey, string ElementId, string Status)>>? Saved;

    /// <summary>
    /// Deletes the open report, returning null on success or why it failed.
    ///
    /// A callback rather than an event because the ANSWER matters: a report open
    /// in Excel cannot be deleted, and the window has to keep itself open and say
    /// so. An event returns nothing, so it could only close and pretend.
    ///
    /// Left null, the Delete button does nothing — it is wired before the window
    /// is shown, so that state is not reachable in practice.
    /// </summary>
    public Func<string?>? DeleteReport { get; set; }

    /// <summary>Reads the note on a clash. Never null — no note is "".</summary>
    public Func<string, string>? ReadComment { get; set; }

    /// <summary>
    /// Stores a note, returning null on success or why it failed.
    ///
    /// Notes save IMMEDIATELY, not on Save with the statuses. A note is typed
    /// into its own dialog and confirmed there, so it reads as already committed
    /// — and losing a carefully worded one to a habitual Cancel is not a trade
    /// worth making for consistency with the radio buttons.
    /// </summary>
    public Func<string, string, string?>? WriteComment { get; set; }

    public SetClashStatusWindow(
        string title, IReadOnlyList<StatusRow> rows, string sourcePath)
    {
        _sourcePath = sourcePath;
        Title = title;
        Width = 1240;
        Height = 720;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = false;
        new WindowInteropHelper(this).Owner =
            System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;

        DockPanel root = new() { Margin = new Thickness(16) };

        // The 6 matches the table's own padding, so the heading starts on the same
        // vertical as the "Element" column title rather than a few pixels left.
        StackPanel heading = new() { Margin = new Thickness(6, 0, 0, 12) };
        heading.Children.Add(BuildSummary(rows));

        // The path is reference material, not the headline. The FILE NAME is the
        // part that identifies the run - the folder is the same every time - so
        // that is what is shown, with the whole path on hover.
        TextBlock source = new()
        {
            Text = System.IO.Path.GetFileName(sourcePath),
            FontSize = 11,
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = sourcePath,
            Margin = new Thickness(0, 6, 0, 0),
        };

        SolidColorBrush? quiet = BrushFromHex("#767676");
        if (quiet is not null)
            source.Foreground = quiet;

        heading.Children.Add(source);

        TextBlock instruction = new()
        {
            Text = "Pick a status, then Save - rows you do not touch keep the status "
                   + "they already had. Click an element id to select it in the model.",
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 0),
        };

        if (quiet is not null)
            instruction.Foreground = quiet;

        heading.Children.Add(instruction);

        DockPanel.SetDock(heading, Dock.Top);
        root.Children.Add(heading);

        Button save = new()
        {
            Content = "Save",
            IsDefault = true,
            MinWidth = 110,
            Padding = new Thickness(10, 4, 10, 4),
            Margin = new Thickness(0, 0, 8, 0),
        };
        save.Click += (_, _) => OnSave();

        Button cancel = new()
        {
            Content = "Cancel",
            IsCancel = true,
            MinWidth = 90,
            Padding = new Thickness(10, 4, 10, 4),
        };
        cancel.Click += (_, _) => Close();

        Button delete = new()
        {
            Content = "Delete this report...",
            MinWidth = 150,
            Padding = new Thickness(10, 4, 10, 4),
            ToolTip = "Removes this report file. Saved statuses are kept.",
        };

        SolidColorBrush? danger = BrushFromHex("#B00020");
        if (danger is not null)
            delete.Foreground = danger;

        delete.Click += (_, _) => OnDelete();

        StackPanel buttons = new()
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        buttons.Children.Add(save);
        buttons.Children.Add(cancel);

        // Delete sits at the FAR LEFT, a window's width away from Save. It is the
        // one button here that destroys something, and putting it next to the one
        // people click every time is how it gets clicked by accident.
        DockPanel bottom = new();
        DockPanel.SetDock(delete, Dock.Left);
        bottom.Children.Add(delete);
        bottom.Children.Add(buttons);

        // A rule above the buttons closes the frame - without it the row floats
        // under the list and Save looks like it belongs to the last clash.
        Border bar = new()
        {
            Child = bottom,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(0, 12, 0, 0),
            Margin = new Thickness(0, 12, 0, 0),
        };

        SolidColorBrush? barLine = BrushFromHex("#dcdcdc");
        if (barLine is not null)
            bar.BorderBrush = barLine;

        DockPanel.SetDock(bar, Dock.Bottom);
        root.Children.Add(bar);

        // The header must not scroll away: 155 rows of bare radio buttons are
        // unreadable the moment the column titles leave the screen.
        FrameworkElement header = BuildHeader();
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);

        StackPanel rowsPanel = new();

        // Alternating tint per GROUP, not per row. One pipe against eight trays
        // is eight rows that belong together, and striping them individually cuts
        // the group up exactly where the eye should be joining it.
        string previous = "";
        bool shade = false;

        for (int i = 0; i < rows.Count; i++)
        {
            string lead = rows[i].Sides is { Count: > 0 } sides
                ? sides[0].ElementId
                : rows[i].ElementId;

            if (i > 0 && lead != previous)
                shade = !shade;

            previous = lead;
            rowsPanel.Children.Add(BuildRow(i, rows[i], shade));
        }

        root.Children.Add(new ScrollViewer
        {
            // Always visible, so the header's reserved gutter lines up whether or
            // not the list happens to overflow.
            VerticalScrollBarVisibility = ScrollBarVisibility.Visible,
            Content = rowsPanel,
        });

        Content = root;
    }

    // ------------------------------------------------------------- layout

    /// <summary>
    /// "25 clashes" and a chip per status that actually occurs.
    ///
    /// The counts are the thing a person opening a triaged report wants first —
    /// how much is left — and they had to be got by scrolling and counting radio
    /// buttons. A status with nothing in it gets NO chip, so the line stays short
    /// and a chip being there means something.
    /// </summary>
    private static FrameworkElement BuildSummary(IReadOnlyList<StatusRow> rows)
    {
        Dictionary<StatusBucket, int> counts = [];
        int unset = 0;

        foreach (StatusRow row in rows)
        {
            StatusBucket? bucket = ClashStatus.Classify(row.Status);
            if (bucket is null)
            {
                unset++;
                continue;
            }

            counts[bucket.Value] = counts.GetValueOrDefault(bucket.Value) + 1;
        }

        StackPanel line = new() { Orientation = Orientation.Horizontal };

        line.Children.Add(new TextBlock
        {
            Text = $"{rows.Count} clash{(rows.Count == 1 ? "" : "es")}",
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 4, 0),
        });

        foreach (StatusBucket bucket in BucketOrder)
        {
            int count = counts.GetValueOrDefault(bucket);
            if (count > 0)
                line.Children.Add(Chip($"{count} {ClashStatus.Label(bucket)}", bucket));
        }

        if (unset > 0)
            line.Children.Add(Chip($"{unset} not set", null));

        return line;
    }

    private static Border Chip(string text, StatusBucket? bucket)
    {
        TextBlock label = new() { Text = text, FontSize = 11.5 };

        Border chip = new()
        {
            Child = label,
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(9, 2, 9, 3),
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };

        (string text, string background) colors = bucket is null
            ? ("#5c5c5c", "#ececec")
            : ClashStatus.Colors(bucket.Value);

        SolidColorBrush? foreground = BrushFromHex(colors.text);
        if (foreground is not null)
            label.Foreground = foreground;

        SolidColorBrush? fill = BrushFromHex(colors.background);
        if (fill is not null)
            chip.Background = fill;

        return chip;
    }

    private const double IdWidth = 82;
    private const double ArrowWidth = 26;
    private const double StatusWidth = 104;
    private const double NoteWidth = 58;

    /// <summary>
    /// The one column layout, handed out fresh to the header and to every row.
    ///
    /// Element ids get a FIXED width rather than Auto. Auto sizes each row to its
    /// own content, so a 7-digit id and an 8-digit one push their neighbours to
    /// different places and nothing lines up down the page — which is most of
    /// what made the old list hard to read.
    /// </summary>
    private static void AddColumns(Grid grid)
    {
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(IdWidth) });
        grid.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star),
        });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ArrowWidth) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(IdWidth) });
        grid.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star),
        });

        foreach (StatusBucket _ in BucketOrder)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(StatusWidth),
            });
        }

        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(NoteWidth) });
    }

    /// <summary>The note column's index, after the four status columns.</summary>
    private static int NoteColumn => 5 + BucketOrder.Length;

    /// <summary>
    /// Column titles, in each status's own colour, with a rule under them.
    ///
    /// This is what pays for the rows themselves being bare radio buttons. The
    /// old list repeated "Resolved Pending Coordination Accepted" on all 155
    /// rows — six hundred words of label to say four things, and they drowned out
    /// the element names, which are the part you actually read.
    /// </summary>
    private FrameworkElement BuildHeader()
    {
        Grid grid = new() { MinHeight = 26 };
        AddColumns(grid);
        AddDivider(grid);

        Add(grid, "Element", 0);
        Add(grid, "Clashes with", 3);

        int column = 5;
        foreach (StatusBucket bucket in BucketOrder)
        {
            TextBlock cell = new()
            {
                Text = ClashStatus.Label(bucket),
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
            };

            SolidColorBrush? brush = BrushFromHex(ClashStatus.Colors(bucket).Text);
            if (brush is not null)
                cell.Foreground = brush;

            Grid.SetColumn(cell, column++);
            grid.Children.Add(cell);
        }

        TextBlock note = new()
        {
            Text = "Note",
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        Grid.SetColumn(note, NoteColumn);
        grid.Children.Add(note);

        // The gutter is on the GRID, not on the border. The list below always
        // shows its scrollbar, so the titles need that width kept clear to stay
        // over their columns - but the band and its rule should still run the
        // full width of the window, or the header stops short of the right edge.
        grid.Margin = new Thickness(0, 0, SystemParameters.VerticalScrollBarWidth, 0);

        Border rule = new()
        {
            Child = grid,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(6, 0, 6, 0),
        };

        SolidColorBrush? line = BrushFromHex("#c2c2c2");
        if (line is not null)
            rule.BorderBrush = line;

        SolidColorBrush? band = BrushFromHex("#f2f3f5");
        if (band is not null)
            rule.Background = band;

        return rule;

        static void Add(Grid g, string text, int col)
        {
            TextBlock cell = new()
            {
                Text = text,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
            };

            Grid.SetColumn(cell, col);
            Grid.SetColumnSpan(cell, 2);
            g.Children.Add(cell);
        }
    }

    /// <summary>
    /// The hairline between naming and judging, run down the full height of the
    /// table, header included.
    ///
    /// Four columns of unlabelled radio buttons need something tying them to the
    /// titles above; without it they float in the white space to the right of the
    /// names and the eye has nothing to follow.
    /// </summary>
    private static void AddDivider(Grid grid)
    {
        Border divider = new()
        {
            BorderThickness = new Thickness(1, 0, 0, 0),
            IsHitTestVisible = false,
        };

        SolidColorBrush? line = BrushFromHex("#dcdcdc");
        if (line is not null)
            divider.BorderBrush = line;

        Grid.SetColumn(divider, 5);
        Grid.SetColumnSpan(divider, BucketOrder.Length);
        grid.Children.Add(divider);
    }

    /// <summary>
    /// One clash: both sides in aligned columns, then four bare radio buttons.
    ///
    /// The row is TINTED BY ITS STATUS, so a screen of 30 rows tells you at a
    /// glance which are settled and which are not — that used to mean reading
    /// four radio buttons on every line to find the filled one.
    /// </summary>
    private Border BuildRow(int index, StatusRow row, bool shade)
    {
        // The row's height comes from the grid, not from padding on the border,
        // so the divider drawn inside it reaches the full height and the line
        // stays unbroken from row to row instead of dashing.
        Grid grid = new() { MinHeight = 30 };
        AddColumns(grid);
        AddDivider(grid);

        Border border = new()
        {
            Child = grid,
            Padding = new Thickness(6, 0, 6, 0),
        };

        BuildSides(row, grid);

        StatusBucket? current = ClashStatus.Classify(row.Status);
        Dictionary<StatusBucket, RadioButton> buttons = [];

        int column = 5;
        foreach (StatusBucket bucket in BucketOrder)
        {
            RadioButton rb = new()
            {
                GroupName = $"status_group_{index}",
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                IsChecked = bucket == current,
                ToolTip = ClashStatus.Label(bucket),
            };

            StatusBucket captured = bucket;
            rb.Checked += (_, _) => Paint(border, captured, shade);

            Grid.SetColumn(rb, column++);
            grid.Children.Add(rb);
            buttons[bucket] = rb;
        }

        Paint(border, current, shade);

        BuildNoteButton(row, grid);

        _controls.Add((row, buttons));
        return border;
    }

    /// <summary>
    /// The note control for one row: a speech mark that fills in once there is
    /// something to read, with the note itself on hover.
    ///
    /// A button rather than a text box per row. 155 always-editable fields is a
    /// wall of chrome for a thing used on a handful of rows, and it would put a
    /// tab stop between every status and the next.
    ///
    /// Disabled on a LEGACY row. Notes are keyed by clash pair and a legacy row
    /// only knows one element, so there is nothing to key on — better a dead
    /// button that says why than one that silently drops what you type.
    /// </summary>
    private Button BuildNoteButton(StatusRow row, Grid grid)
    {
        Button button = new()
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Padding = new Thickness(6, 1, 6, 1),
            MinWidth = 34,
        };

        Grid.SetColumn(button, NoteColumn);
        grid.Children.Add(button);

        if (row.ClashKey.Length == 0)
        {
            button.Content = "-";
            button.IsEnabled = false;
            button.ToolTip = "This report predates clash keys, so a note has nothing to attach to.";
            return button;
        }

        void Refresh()
        {
            string text = ReadComment?.Invoke(row.ClashKey) ?? "";
            bool has = text.Length > 0;

            button.Content = has ? "“”" : "+";
            button.FontWeight = has ? FontWeights.Bold : FontWeights.Normal;
            button.ToolTip = has ? text : "Add a note";
        }

        button.Click += (_, _) =>
        {
            if (WriteComment is null)
                return;

            string existing = ReadComment?.Invoke(row.ClashKey) ?? "";

            TextPromptWindow prompt = new(
                "Note", existing, $"Why this status? - {row.Name}", null);

            if (prompt.ShowDialog() != true)
                return;

            string? error = WriteComment(row.ClashKey, prompt.Value);
            if (error is not null)
            {
                RibbitUi.Alert($"The note could not be saved:\n\n{error}", "Note");
                return;
            }

            Refresh();
        };

        Refresh();
        return button;
    }

    /// <summary>
    /// The status colour if there is one, otherwise the group's own faint stripe.
    /// </summary>
    private static void Paint(Border border, StatusBucket? bucket, bool shade)
    {
        // Deliberately fainter than Accepted's neutral grey. The stripe means
        // "same element as the row above"; Accepted means "closed business". If
        // the two greys were close, one would keep being read as the other.
        SolidColorBrush? brush = bucket is null
            ? (shade ? BrushFromHex("#f7f8fa") : null)
            : BrushFromHex(ClashStatus.Colors(bucket.Value).Background);

        border.Background = brush ?? Brushes.Transparent;
    }

    /// <summary>
    /// Both sides across the first five columns, with every host id clickable.
    ///
    /// Clicking SELECTS ONLY — no zoom, no view change — the same as clicking an
    /// id in the report, and for the same reason: being thrown into a 3D view
    /// loses the view you were working in.
    ///
    /// An id inside a LINK is drawn plain. It cannot be selected from the host
    /// document, and a link that silently does nothing is worse than text.
    ///
    /// Names are trimmed to one line with the full text on hover. Wrapping gave
    /// rows two different heights, which is what made the list look ragged.
    /// </summary>
    private static void BuildSides(StatusRow row, Grid grid)
    {
        // A legacy row, or a report whose sides did not survive: one span of
        // plain text, as before. Nothing to select against with any confidence.
        if (row.Sides is null || row.Sides.Count == 0)
        {
            TextBlock plain = Text(
                row.ClashKey.Length > 0 ? row.Name : $"{row.ElementId}   {row.Name}");

            Grid.SetColumn(plain, 0);
            Grid.SetColumnSpan(plain, 5);
            grid.Children.Add(plain);
            return;
        }

        for (int i = 0; i < row.Sides.Count && i < 2; i++)
        {
            StatusSide side = row.Sides[i];
            int idColumn = i == 0 ? 0 : 3;

            grid.Children.Add(Place(IdCell(side), idColumn));

            string name = side.Name;
            if (side.Link.Length > 0)
                name = $"{name} (in link: {side.Link})".TrimStart();

            grid.Children.Add(Place(Text(name), idColumn + 1));
        }

        if (row.Sides.Count > 1)
            grid.Children.Add(Place(PairSelector(row), 2));

        static UIElement Place(FrameworkElement element, int column)
        {
            Grid.SetColumn(element, column);
            return element;
        }
    }

    /// <summary>
    /// The ↔ between the two sides, made clickable: selects BOTH elements at
    /// once, so Selection Box frames the clash rather than one thing in it.
    ///
    /// The arrow rather than a new column, because it already means "this pair"
    /// and the row has enough in it.
    ///
    /// SIDES INSIDE A LINK ARE SKIPPED, and the host side is selected on its own.
    /// A linked element cannot be selected from the host document, and selecting
    /// one of two is more useful than refusing to select either.
    /// </summary>
    private static TextBlock PairSelector(StatusRow row)
    {
        List<long> ids = [];

        foreach (StatusSide side in row.Sides ?? [])
        {
            if (side.Link.Length == 0 && long.TryParse(side.ElementId, out long id))
                ids.Add(id);
        }

        TextBlock block = new()
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        if (ids.Count == 0)
        {
            // Both sides are in links: nothing here can be selected at all.
            block.Text = "↔";
            return block;
        }

        Hyperlink link = new(new Run("↔"))
        {
            ToolTip = ids.Count > 1
                ? "Select both elements, then use Selection Box to see the clash"
                : "Select this clash's host element (the other side is in a link)",
        };

        link.Click += (_, _) => RevitLinks.Select(ids);
        block.Inlines.Add(link);

        return block;
    }

    private static TextBlock IdCell(StatusSide side)
    {
        TextBlock block = new()
        {
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        if (side.Link.Length == 0 && long.TryParse(side.ElementId, out long id))
        {
            Hyperlink link = new(new Run(side.ElementId))
            {
                ToolTip = "Select this element in the model",
            };

            link.Click += (_, _) => RevitLinks.Select([id]);
            block.Inlines.Add(link);
        }
        else
        {
            block.Text = side.ElementId;
        }

        return block;
    }

    private static TextBlock Text(string text) => new()
    {
        Text = text,
        VerticalAlignment = VerticalAlignment.Center,
        TextTrimming = TextTrimming.CharacterEllipsis,
        ToolTip = text.Length > 0 ? text : null,
        Margin = new Thickness(0, 0, 8, 0),
    };

    private static SolidColorBrush? BrushFromHex(string hex)
    {
        try
        {
            string h = hex.TrimStart('#');
            return new SolidColorBrush(Color.FromRgb(
                Convert.ToByte(h.Substring(0, 2), 16),
                Convert.ToByte(h.Substring(2, 2), 16),
                Convert.ToByte(h.Substring(4, 2), 16)));
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Confirms, deletes, and closes — or stays open and says why it could not.
    ///
    /// The confirmation spells out that STATUSES SURVIVE, because that is the
    /// thing a user is actually afraid of losing here and nothing on screen says
    /// otherwise. Status lives in the ledger keyed by clash pair; this file is a
    /// snapshot of it, and a later run rebuilds an equivalent one.
    /// </summary>
    private void OnDelete()
    {
        if (DeleteReport is null)
            return;

        bool ok = RibbitUi.Confirm(
            "Delete this report file?\n\n"
            + $"{_sourcePath}\n\n"
            + "Your saved statuses are KEPT - they live in the ledger, not in this "
            + "file, so the next run still remembers them. Only this snapshot goes.",
            "Delete report");

        if (!ok)
            return;

        string? error = DeleteReport();
        if (error is not null)
        {
            RibbitUi.Alert(
                "Could not delete the report:\n\n" + error
                + "\n\nIf it is open in Excel, close it and try again.",
                "Delete report");
            return;
        }

        Close();
    }

    private void OnSave()
    {
        List<(string, string, string)> updates = [];

        foreach ((StatusRow row, Dictionary<StatusBucket, RadioButton> buttons) in _controls)
        {
            foreach (StatusBucket bucket in BucketOrder)
            {
                if (buttons[bucket].IsChecked == true)
                {
                    updates.Add((row.ClashKey, row.ElementId, ClashStatus.Label(bucket)));
                    break;
                }
            }
        }

        // The handler owns everything from here, including reporting failures -
        // the window closes either way rather than sitting open with no feedback.
        try
        {
            Saved?.Invoke(updates);
        }
        finally
        {
            Close();
        }
    }
}
