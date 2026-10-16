using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace Ribbit.Native.Ui;

/// <summary>
/// One run on offer: what to show, and the file behind it.
///
/// <paramref name="FileName"/> is displayed as well as the test and the time,
/// because those two are NOT unique — a test rerun twice in one minute produces
/// two rows reading identically, and picking which of them to delete is exactly
/// when you need to tell them apart.
/// </summary>
internal sealed record ReportChoice(
    string Path, string TestName, string When, string FileName);

/// <summary>
/// "Which run?" — pick a report to triage, and prune the ones you are done with.
///
/// Replaces a plain <see cref="RibbitUi.SelectFromList"/>. That control answers
/// with a STRING, which forced two compromises this window drops: rows had to be
/// numbered so two runs of one test in the same minute stayed distinct, and only
/// ever one row could come back — there was no way to select the eleven stale
/// runs and clear them out. Choices here are objects, so identity never rests on
/// a label.
///
/// Deleting does NOT close the window. Pruning and choosing are one sitting: you
/// clear out the old runs and then open the one you actually came for.
/// </summary>
internal sealed class ReportPickWindow : Window
{
    private const int NamesShownInConfirm = 8;

    private readonly ListBox _list = new() { SelectionMode = SelectionMode.Extended };
    private readonly Button _open;
    private readonly Button _delete;

    /// <summary>The chosen report, or null if the window was cancelled.</summary>
    public string? Chosen { get; private set; }

    /// <summary>
    /// Deletes these reports, answering with the ones that FAILED and why.
    /// An empty answer means every one of them is gone.
    ///
    /// Partial failure is the normal case, not an edge case: a report open in
    /// Excel cannot be deleted while its neighbours can. The window keeps the
    /// survivors on the list and names them, rather than claiming all or nothing.
    /// </summary>
    public Func<IReadOnlyList<string>, IReadOnlyList<(string Path, string Error)>>? DeleteReports
    {
        get;
        set;
    }

    public ReportPickWindow(string title, IReadOnlyList<ReportChoice> choices)
    {
        Title = title;
        Width = 780;
        Height = 560;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = false;
        new WindowInteropHelper(this).Owner =
            System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;

        DockPanel root = new() { Margin = new Thickness(16) };

        TextBlock subtitle = new()
        {
            Text = "Pick a run to open. Ctrl-click or Shift-click to select several, "
                   + "then Delete to clear out old runs - your saved statuses are kept "
                   + "either way.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12),
        };
        DockPanel.SetDock(subtitle, Dock.Top);
        root.Children.Add(subtitle);

        _open = new Button
        {
            Content = "Open",
            IsDefault = true,
            MinWidth = 110,
            Padding = new Thickness(10, 4, 10, 4),
            Margin = new Thickness(0, 0, 8, 0),
        };
        _open.Click += (_, _) => OnOpen();

        Button cancel = new()
        {
            Content = "Cancel",
            IsCancel = true,
            MinWidth = 90,
            Padding = new Thickness(10, 4, 10, 4),
        };
        cancel.Click += (_, _) => Close();

        _delete = new Button
        {
            Content = "Delete selected...",
            MinWidth = 150,
            Padding = new Thickness(10, 4, 10, 4),
            ToolTip = "Sends the selected report files to the Recycle Bin. "
                      + "Saved statuses are kept.",
        };

        SolidColorBrush? danger = BrushFromHex("#B00020");
        if (danger is not null)
            _delete.Foreground = danger;

        _delete.Click += (_, _) => OnDelete();

        StackPanel right = new()
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        right.Children.Add(_open);
        right.Children.Add(cancel);

        // Delete at the far left, a window's width from Open. It is the one
        // button here that destroys something, and sitting beside the one people
        // press every time is how it gets pressed by accident.
        DockPanel bottom = new() { Margin = new Thickness(0, 12, 0, 0) };
        DockPanel.SetDock(_delete, Dock.Left);
        bottom.Children.Add(_delete);
        bottom.Children.Add(right);

        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(bottom);

        foreach (ReportChoice choice in choices)
            _list.Items.Add(BuildItem(choice));

        _list.SelectionChanged += (_, _) => UpdateButtons();
        _list.MouseDoubleClick += (_, _) => OnOpen();

        if (_list.Items.Count > 0)
            _list.SelectedIndex = 0;

        root.Children.Add(_list);
        Content = root;

        UpdateButtons();
    }

    /// <summary>
    /// Two lines: the run, and the file it lives in. The filename is the tie
    /// breaker and is deliberately quiet — you read it only when the line above
    /// is not enough.
    /// </summary>
    private static ListBoxItem BuildItem(ReportChoice choice)
    {
        StackPanel panel = new() { Margin = new Thickness(2, 4, 2, 4) };

        panel.Children.Add(new TextBlock
        {
            Text = $"{choice.TestName}   -   {choice.When}",
            FontWeight = FontWeights.SemiBold,
        });

        TextBlock file = new()
        {
            Text = choice.FileName,
            FontSize = 11,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        SolidColorBrush? quiet = BrushFromHex("#666666");
        if (quiet is not null)
            file.Foreground = quiet;

        panel.Children.Add(file);

        return new ListBoxItem { Content = panel, Tag = choice };
    }

    private List<ReportChoice> Picked()
    {
        List<ReportChoice> picked = [];

        foreach (object item in _list.SelectedItems)
        {
            if (item is ListBoxItem { Tag: ReportChoice choice })
                picked.Add(choice);
        }

        return picked;
    }

    /// <summary>
    /// Open takes EXACTLY ONE. The status editor shows one report, so with three
    /// selected there is no honest answer to which one it meant — better a
    /// greyed-out button than a silent guess.
    /// </summary>
    private void UpdateButtons()
    {
        _open.IsEnabled = _list.SelectedItems.Count == 1;
        _delete.IsEnabled = _list.SelectedItems.Count > 0;
    }

    private void OnOpen()
    {
        List<ReportChoice> picked = Picked();
        if (picked.Count != 1)
            return;

        Chosen = picked[0].Path;
        DialogResult = true;
        Close();
    }

    private void OnDelete()
    {
        if (DeleteReports is null)
            return;

        List<ReportChoice> picked = Picked();
        if (picked.Count == 0)
            return;

        string names = string.Join("\n", picked.Take(NamesShownInConfirm).Select(c => c.FileName));
        if (picked.Count > NamesShownInConfirm)
            names += $"\n... and {picked.Count - NamesShownInConfirm} more";

        bool ok = RibbitUi.Confirm(
            $"Delete {picked.Count} report file{(picked.Count == 1 ? "" : "s")}?\n\n"
            + names
            + "\n\nThey go to the Recycle Bin. Your saved statuses are KEPT - they "
            + "live in the ledger, not in these files, so the next run still "
            + "remembers them.",
            "Delete reports");

        if (!ok)
            return;

        IReadOnlyList<(string Path, string Error)> failed =
            DeleteReports([.. picked.Select(c => c.Path)]);

        HashSet<string> survived = [.. failed.Select(f => f.Path)];

        // Only what actually went gets taken off the list. A row still on disk
        // that vanished from the list would be a report the user believes is
        // gone and would meet again on the next open.
        HashSet<string> gone =
            [.. picked.Select(c => c.Path).Where(p => !survived.Contains(p))];

        List<ListBoxItem> remove = [];
        foreach (object item in _list.Items)
        {
            if (item is ListBoxItem { Tag: ReportChoice c } lbi && gone.Contains(c.Path))
                remove.Add(lbi);
        }

        foreach (ListBoxItem item in remove)
            _list.Items.Remove(item);

        if (failed.Count > 0)
        {
            RibbitUi.Alert(
                "Some reports could not be deleted:\n\n"
                + string.Join("\n", failed.Select(f => $"{Path.GetFileName(f.Path)}: {f.Error}"))
                + "\n\nIf one is open in Excel, close it and try again.",
                "Delete reports");
        }

        // Everything is gone, so there is nothing left to choose. Closing beats
        // an empty list with a dead Open button.
        if (_list.Items.Count == 0)
        {
            Close();
            return;
        }

        if (_list.SelectedItems.Count == 0)
            _list.SelectedIndex = 0;

        UpdateButtons();
    }

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
}
