using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Ribbit.Native.Ui;

/// <summary>
/// The shared visual vocabulary for the add-in's input forms.
///
/// These windows are built in code rather than XAML — their contents depend on
/// what is in the model — which historically meant every one invented its own
/// margins, weights and button sizes. One place for the primitives keeps them
/// looking like one product, and means a spacing decision is made once.
///
/// Deliberately restrained: system colours for anything structural, exactly one
/// accent, and no bespoke control templates. A Revit add-in dialog that tries to
/// look like a web page reads as broken next to Revit's own chrome.
/// </summary>
internal static class FormParts
{
    /// <summary>The single accent, matching the report windows' link colour.</summary>
    public static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(0x0A, 0x66, 0xC2));

    public const double Gutter = 16.0;

    /// <summary>A titled section break. Use once per logical group, not per field.</summary>
    public static TextBlock Section(string text, bool first = false) => new()
    {
        Text = text,
        FontWeight = FontWeights.SemiBold,
        FontSize = 14,
        Margin = new Thickness(0, first ? 0 : 18, 0, 6),
    };

    /// <summary>The label directly above a control.</summary>
    public static TextBlock FieldLabel(string text) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 0, 0, 4),
    };

    /// <summary>Secondary explanation — never load-bearing on its own.</summary>
    public static TextBlock Hint(string text) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        FontSize = 11,
        Foreground = SystemColors.GrayTextBrush,
        Margin = new Thickness(0, 0, 0, 8),
    };

    /// <summary>A status line the form writes into as it learns things.</summary>
    public static TextBlock Status() => new()
    {
        TextWrapping = TextWrapping.Wrap,
        FontSize = 11,
        Foreground = SystemColors.GrayTextBrush,
        Margin = new Thickness(0, 0, 0, 10),
    };

    /// <summary>
    /// A status line for something that will stop the run, or silently spoil it.
    ///
    /// Distinct from <see cref="Status"/>: grey reads as commentary and gets
    /// skipped. Starts collapsed — an empty red line still occupies space and
    /// makes a healthy form look like it is holding something back.
    /// </summary>
    public static TextBlock Problem() => new()
    {
        TextWrapping = TextWrapping.Wrap,
        FontSize = 11,
        Foreground = new SolidColorBrush(Color.FromRgb(0xB3, 0x26, 0x1E)),
        Visibility = Visibility.Collapsed,
    };

    public static Border Divider() => new()
    {
        BorderBrush = SystemColors.ControlDarkBrush,
        BorderThickness = new Thickness(0, 1, 0, 0),
        Opacity = 0.35,
        Margin = new Thickness(0, 14, 0, 0),
    };

    public static TextBox Input(string text = "") => new()
    {
        Text = text,
        Padding = new Thickness(6, 4, 6, 4),
        MinHeight = 26,
    };

    /// <summary>
    /// A text box with grey placeholder text inside it while empty.
    ///
    /// WPF has no built-in watermark, and an unlabelled empty box is genuinely
    /// mysterious — a filter box added to Place Views was reported as "the blank
    /// textbox at the top, what is that for?" within minutes of shipping. The
    /// overlay is hit-test invisible so it never intercepts a click.
    /// </summary>
    public static (Grid Host, TextBox Box) PlaceholderInput(string placeholder, string text = "")
    {
        TextBox box = Input(text);

        TextBlock ghost = new()
        {
            Text = placeholder,
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = SystemColors.GrayTextBrush,
            FontStyle = FontStyles.Italic,
            IsHitTestVisible = false,
        };

        void Sync() => ghost.Visibility =
            string.IsNullOrEmpty(box.Text) ? Visibility.Visible : Visibility.Collapsed;

        box.TextChanged += (_, _) => Sync();
        Sync();

        Grid host = new();
        host.Children.Add(box);
        host.Children.Add(ghost);
        return (host, box);
    }

    /// <summary>
    /// A removable chip: a short label, the parameter it came from, and an ×.
    ///
    /// Replaces the one-line "3 value(s) selected: Chilled Water, CHW Sup…"
    /// summary, which truncated at 90 characters and gave no way to drop a
    /// single pick — the only remedy was to reopen Browse and re-tick
    /// everything.
    /// </summary>
    public static Border Chip(string qualifier, string text, Action onRemove)
    {
        StackPanel content = new() { Orientation = Orientation.Horizontal };

        if (qualifier.Length > 0)
        {
            content.Children.Add(new TextBlock
            {
                Text = qualifier,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 10,
                Foreground = SystemColors.GrayTextBrush,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0),
            });
        }

        content.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 220,
        });

        Button remove = new()
        {
            Content = "✕",
            FontSize = 10,
            Foreground = SystemColors.GrayTextBrush,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(6, 0, 2, 0),
            Margin = new Thickness(4, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = $"Remove {text}",
        };
        remove.Click += (_, _) => onRemove();
        content.Children.Add(remove);

        return new Border
        {
            Background = SystemColors.ControlLightBrush,
            BorderBrush = SystemColors.ControlDarkBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(11),
            Padding = new Thickness(9, 2, 3, 2),
            Margin = new Thickness(0, 0, 5, 5),
            Child = content,
            ToolTip = qualifier.Length > 0 ? $"[{qualifier}]  {text}" : text,
        };
    }

    /// <summary>The wrapping host chips live in.</summary>
    public static WrapPanel ChipHost() => new()
    {
        Orientation = Orientation.Horizontal,
        Margin = new Thickness(0, 2, 0, 0),
    };

    /// <summary>Shown in a chip host that has nothing in it.</summary>
    public static TextBlock EmptyChipNote(string text) => new()
    {
        Text = text,
        FontSize = 11,
        FontStyle = FontStyles.Italic,
        Foreground = SystemColors.GrayTextBrush,
        Margin = new Thickness(0, 2, 0, 4),
    };

    public static Button Primary(string text)
    {
        // The colours go in a STYLE, not on the button. Set locally they beat the
        // theme's own disabled trigger — which greys the background but cannot
        // touch a locally-set foreground — leaving a disabled primary button
        // white-on-light-grey and all but unreadable.
        Style style = new(typeof(Button));
        style.Setters.Add(new Setter(Control.BackgroundProperty, Accent));
        style.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.White));

        Trigger disabled = new() { Property = UIElement.IsEnabledProperty, Value = false };
        disabled.Setters.Add(new Setter(Control.BackgroundProperty, SystemColors.ControlBrush));
        disabled.Setters.Add(new Setter(Control.ForegroundProperty, SystemColors.GrayTextBrush));
        style.Triggers.Add(disabled);

        return new Button
        {
            Content = text,
            IsDefault = true,
            MinWidth = 110,
            MinHeight = 30,
            Padding = new Thickness(14, 4, 14, 4),
            Margin = new Thickness(0, 0, 8, 0),
            FontWeight = FontWeights.SemiBold,
            BorderThickness = new Thickness(0),
            Style = style,
        };
    }

    public static Button Secondary(string text, bool isCancel = false) => new()
    {
        Content = text,
        IsCancel = isCancel,
        MinWidth = 92,
        MinHeight = 30,
        Padding = new Thickness(12, 4, 12, 4),
        Margin = new Thickness(0, 0, 8, 0),
    };

    /// <summary>A right-aligned button strip, in the order given.</summary>
    public static StackPanel ButtonBar(params UIElement[] buttons)
    {
        StackPanel bar = new()
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0),
        };

        foreach (UIElement b in buttons)
            bar.Children.Add(b);

        return bar;
    }

    /// <summary>
    /// A bordered "card" for one row of a list — a level picker row, a
    /// sheet-to-view pairing row.
    ///
    /// A bare Grid sitting directly on the window background reads as one long
    /// undifferentiated block once there are more than three or four rows; a
    /// light fill and a hairline border turn each row into a scannable unit
    /// without inventing a new visual language — same system colours as
    /// everything else in this file, just applied as a background instead of a
    /// line.
    /// </summary>
    /// <param name="tinted">
    /// A faint accent wash instead of the plain card fill, for a lightweight
    /// zebra stripe across a long list — alternate <c>true</c>/<c>false</c> per
    /// row rather than reaching for a different brush per row.
    /// </param>
    public static Border RowCard(UIElement content, bool tinted = false) => new()
    {
        Background = tinted
            ? new SolidColorBrush(Color.FromArgb(0x12, 0x0A, 0x66, 0xC2))
            : SystemColors.ControlLightLightBrush,
        BorderBrush = SystemColors.ControlDarkBrush,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(6),
        Padding = new Thickness(10, 8, 10, 8),
        Margin = new Thickness(0, 0, 0, 8),
        Child = content,
    };

    /// <summary>
    /// The small secondary button the clash windows put beside a list - "Clear
    /// all", "+ Keyword...", "Tick suggested".
    ///
    /// One definition: seven windows each carried their own copy, identical but
    /// for width and margin, so a style change had to be made seven times.
    /// </summary>
    public static Button SmallButton(
        string text, Action onClick, string? tip = null, double minWidth = 88, Thickness? margin = null)
    {
        Button b = new()
        {
            Content = text,
            MinWidth = minWidth,
            MinHeight = 24,
            FontSize = 11,
            Padding = new Thickness(8, 2, 8, 2),
            Margin = margin ?? new Thickness(0, 0, 6, 0),
            ToolTip = tip,
        };
        b.Click += (_, _) => onClick();
        return b;
    }

    /// <summary>
    /// A small rounded label for a count or short status — "3 rows",
    /// "existing" — sitting beside a row's main text without competing with it
    /// for attention the way inline bold text would.
    /// </summary>
    public static Border Pill(string text, Brush? foreground = null) => new()
    {
        Background = SystemColors.ControlLightBrush,
        BorderBrush = SystemColors.ControlDarkBrush,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(9),
        Padding = new Thickness(8, 1, 8, 1),
        Child = new TextBlock
        {
            Text = text,
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            Foreground = foreground ?? SystemColors.GrayTextBrush,
        },
    };

    /// <summary>
    /// A path field with its Browse button, sharing one row.
    ///
    /// Takes a UIElement, not a TextBox: a placeholder field is a Grid wrapping
    /// the box, and both forms want the same row.
    /// </summary>
    /// <summary>The panel one tab's fields live in.</summary>
    public static StackPanel TabPanel() => new() { Margin = new Thickness(Gutter) };

    /// <summary>
    /// One step of a form split into tabs.
    ///
    /// A form long enough to scroll hides its own settings: you cannot see the
    /// titleblock choice while editing the mapping, and a variable-height editor
    /// pushes everything below it out of reach. Splitting by step keeps each
    /// page roughly a screenful and lets a long list own its whole tab.
    ///
    /// Each tab scrolls on its own, so a small screen still reaches everything.
    /// </summary>
    public static TabItem Tab(string header, UIElement content) => new()
    {
        Header = header,
        Content = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = content,
        },
    };

    public static DockPanel BrowseRow(UIElement box, Button browse)
    {
        browse.MinWidth = 96;
        browse.MinHeight = 26;
        browse.Margin = new Thickness(8, 0, 0, 0);
        browse.Padding = new Thickness(10, 4, 10, 4);

        DockPanel row = new() { Margin = new Thickness(0, 0, 0, 6) };
        DockPanel.SetDock(browse, Dock.Right);
        row.Children.Add(browse);
        row.Children.Add(box);
        return row;
    }

    /// <summary>
    /// Share a list's width between its stretchy columns (fixed ones keep theirs),
    /// so no column hides behind a sideways scroll - the first render showed
    /// Suggested and "Already on the sheet" off the edge.
    /// </summary>
    public static void FitColumns(ListView list, params double[] shares)
    {
        GridViewColumnCollection cols = ((GridView)list.View).Columns;
        double fixedWidth = 0;
        for (int i = 0; i < cols.Count; i++)
            if (shares[i] <= 0)
                fixedWidth += cols[i].Width;
        double free = list.ActualWidth - fixedWidth - SystemParameters.VerticalScrollBarWidth - 12;
        if (free < 200)
            return;
        double total = shares.Where(s => s > 0).Sum();
        for (int i = 0; i < cols.Count; i++)
            if (shares[i] > 0)
                cols[i].Width = Math.Floor(free * shares[i] / total);
    }
}
