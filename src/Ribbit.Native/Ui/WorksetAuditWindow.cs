using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Ribbit.Geometry.Clash;

namespace Ribbit.Native.Ui;

/// <summary>
/// The workset audit for one clash set: every workset across the ticked models,
/// how many of THIS set's elements sit on it, and which look like content that
/// clashes without being a clash - typical-floor copies, hidden duplicates,
/// builder's work openings.
///
/// The Federated Clash counterpart of LiveClash's provisional audit, and bound
/// by the same rule: it SUGGESTS, a person decides. Only names that clearly are
/// not coordination content start ticked; "PlantReplacement" or anything merely
/// hidden by default is flagged for a look and left unticked, because leaving
/// real plant out would hide the real clash when it lands. Nothing changes
/// until "Leave these out" is pressed - and then only in the editor, which still
/// has to be saved.
///
/// The count beside each workset is what leaving it out would COST this set.
/// A workset with nothing of the set on it can still be ticked (a set may grow
/// into it) but is listed last.
/// </summary>
internal sealed class WorksetAuditWindow : Window
{
    private const string DialogTitle = "Audit worksets";

    private readonly List<(string Name, int InSet, CheckBox Box)> _rows = [];
    private readonly TextBlock _summary;
    private readonly IReadOnlyList<WorksetUsage> _usage;

    /// <summary>The names ticked when "Leave these out" was pressed.</summary>
    public List<string> Result { get; private set; } = [];

    /// <summary>Every row's name and tick box, for a test to read and drive.</summary>
    internal IReadOnlyList<(string Name, int InSet, CheckBox Box)> Rows => _rows;

    /// <summary>The live summary line, for a test to read.</summary>
    internal string SummaryText => _summary.Text;

    /// <param name="usage">Every workset across the ticked models, by name.</param>
    /// <param name="current">What the set leaves out now - opens ticked exactly as that.</param>
    /// <param name="setName">For the title.</param>
    public WorksetAuditWindow(
        IReadOnlyList<WorksetUsage> usage, IReadOnlyList<string> current, string setName)
    {
        _usage = usage;

        Title = setName.Length > 0 ? $"Audit worksets - {setName}" : DialogTitle;
        Width = 720;
        Height = 640;
        MinWidth = 520;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        new WindowInteropHelper(this).Owner =
            System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;

        StackPanel panel = new() { Margin = new Thickness(FormParts.Gutter) };

        panel.Children.Add(FormParts.Section("Worksets in the ticked models", first: true));
        panel.Children.Add(FormParts.Hint(
            "Tick a workset to leave everything on it out of this set, in every model. Suggestions "
            + "are listed first: typical, hidden and opening worksets start ticked; anything else "
            + "flagged - replacements, existing, hidden by default - is for you to judge."));

        _summary = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Margin = new Thickness(11, 7, 11, 7),
        };

        List<string> ticks = WorksetAudit.InitialTicks(usage, current);
        HashSet<string> ticked = new(ticks, StringComparer.OrdinalIgnoreCase);

        StackPanel list = new();

        foreach (WorksetUsage u in Ordered(usage))
        {
            WorksetSuggestion? s = WorksetAudit.Suggest(u.Name, u.HiddenByDefault);
            list.Children.Add(Row(u.Name, u.ElementsInSet,
                string.Join(", ", u.Models), s, stale: false, ticked.Contains(u.Name)));
        }

        // Left out now, but no ticked model has it - renamed, or in a model that
        // is not ticked. Shown ticked so pressing OK keeps it, and labelled, so
        // it can be unticked on purpose rather than lingering unseen.
        foreach (string gone in WorksetAudit.Stale(current, usage.Select(u => u.Name)))
            list.Children.Add(Row(gone, 0, "", null, stale: true, isTicked: true));

        if (_rows.Count == 0)
            list.Children.Add(FormParts.EmptyChipNote("No worksets - none of the ticked models is workshared."));

        panel.Children.Add(new Border
        {
            BorderBrush = SystemColors.ControlDarkBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            Background = SystemColors.WindowBrush,
            Padding = new Thickness(4),
            Child = new ScrollViewer
            {
                Height = 330,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = list,
            },
        });

        StackPanel bulk = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 7, 0, 0) };
        bulk.Children.Add(Small("Tick suggested", TickSuggested,
            "Tick only the typical, hidden and opening worksets that hold something of this set"));
        bulk.Children.Add(Small("Clear all", () => SetAll(false), "Leave nothing out"));
        panel.Children.Add(bulk);

        panel.Children.Add(new Border
        {
            Background = SystemColors.ControlLightBrush,
            BorderBrush = FormParts.Accent,
            BorderThickness = new Thickness(3, 0, 0, 0),
            Margin = new Thickness(0, 12, 0, 0),
            Child = _summary,
        });

        panel.Children.Add(FormParts.Divider());

        Button ok = FormParts.Primary("Leave these out");
        ok.Click += (_, _) =>
        {
            Result = WorksetAudit.Normalize(_rows.Where(r => r.Box.IsChecked == true).Select(r => r.Name));
            DialogResult = true;
        };
        panel.Children.Add(FormParts.ButtonBar(ok, FormParts.Secondary("Cancel", true)));

        Content = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = panel,
        };

        RefreshSummary();
    }

    /// <summary>Suggested-and-ticked first, then flagged, then by what the set has on it.</summary>
    private static IEnumerable<WorksetUsage> Ordered(IReadOnlyList<WorksetUsage> usage)
        => usage
            .OrderBy(u => WorksetAudit.Suggest(u.Name, u.HiddenByDefault) switch
            {
                { PreTick: true } => 0,
                not null => 1,
                _ => 2,
            })
            .ThenByDescending(u => u.ElementsInSet)
            .ThenBy(u => u.Name, StringComparer.OrdinalIgnoreCase);

    private CheckBox Row(
        string name, int inSet, string models, WorksetSuggestion? s, bool stale, bool isTicked)
    {
        DockPanel line = new();

        TextBlock count = new()
        {
            Text = stale ? "" : $"{inSet} in this set",
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            FontSize = 11,
            Foreground = inSet > 0 ? SystemColors.ControlTextBrush : SystemColors.GrayTextBrush,
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 104,
            TextAlignment = TextAlignment.Right,
        };
        DockPanel.SetDock(count, Dock.Right);
        line.Children.Add(count);

        StackPanel text = new();
        text.Children.Add(new TextBlock
        {
            Text = name,
            FontSize = 12.5,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });

        string detail = stale
            ? "not found in the ticked models - renamed, or only in a model that is not ticked"
            : s is null
                ? models
                : $"{s.Reason}   ·   {models}";

        text.Children.Add(new TextBlock
        {
            Text = detail,
            FontSize = 10.5,
            Foreground = stale || s is not null
                ? System.Windows.Media.Brushes.DarkGoldenrod
                : SystemColors.GrayTextBrush,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        line.Children.Add(text);

        CheckBox box = new()
        {
            Content = line,
            IsChecked = isTicked,
            Margin = new Thickness(4, 4, 4, 4),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            ToolTip = models.Length > 0 ? $"{name}\nIn: {models}" : name,
        };
        box.Checked += (_, _) => RefreshSummary();
        box.Unchecked += (_, _) => RefreshSummary();

        _rows.Add((name, inSet, box));
        return box;
    }

    private static Button Small(string text, Action onClick, string tip)
        => FormParts.SmallButton(text, onClick, tip, minWidth: 96);

    private void TickSuggested()
    {
        HashSet<string> suggested = new(WorksetAudit.InitialTicks(_usage, []), StringComparer.OrdinalIgnoreCase);
        foreach ((string name, _, CheckBox box) in _rows)
            box.IsChecked = suggested.Contains(name);

        RefreshSummary();
    }

    private void SetAll(bool state)
    {
        foreach ((_, _, CheckBox box) in _rows)
            box.IsChecked = state;

        RefreshSummary();
    }

    private void RefreshSummary()
    {
        List<(string Name, int InSet, CheckBox Box)> on = [.. _rows.Where(r => r.Box.IsChecked == true)];
        int lost = on.Sum(r => r.InSet);
        int models = _usage.SelectMany(u => u.Models).Distinct(StringComparer.OrdinalIgnoreCase).Count();

        _summary.Text = on.Count == 0
            ? $"{_usage.Count} workset(s) across {models} model(s). Nothing left out - every workset is searched."
            : $"{on.Count} of {_rows.Count} workset(s) left out - {lost} element(s) of this set would not be "
              + "searched. Each one is named in every clash report this set is used in.";
    }
}
