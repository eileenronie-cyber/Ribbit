using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using Ribbit.Geometry.Rooms;
using Ribbit.Native.Revit;
using Ribbit.Native.Settings;

namespace Ribbit.Native.Ui;

internal sealed class RtViewRow(RtView view) : INotifyPropertyChanged
{
    public RtView View { get; } = view;
    public string Text => View.Label;
    private bool _checked;
    public bool IsChecked { get => _checked; set { if (_checked != value) { _checked = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked))); } } }
    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>One line of the "what will happen" list.</summary>
internal sealed record RtLine(string View, string Room, string Action, string Tone);

/// <summary>
/// Room Tag Checker (was the WinForms picker in "Room Tag Checker v3.dyn").
///
/// Left, the views - searchable, ticks kept while you search. Right, the link
/// the rooms live in, and the LEVEL PAIRING: one row per host level of the
/// ticked views, each taking its rooms from a level of the link. Names that
/// agree are paired for you; names that do not are NOT SET and their views
/// skipped until you choose - nothing is guessed quietly. A pairing you make by
/// hand is remembered for that link.
///
/// Check shows exactly what will happen - every tag and every deletion, per view
/// - and nothing is written until Apply. Changing anything after a Check asks
/// for a new one, so Apply always does what the list shows.
/// </summary>
internal sealed class RoomTagWindow : Window
{
    internal const string DialogTitle = "Room Tag Checker";
    internal const string NotSet = "NOT SET - these views are skipped";

    internal readonly TextBox ViewSearch;
    internal readonly ListBox ViewList;
    internal readonly TextBlock ViewCount;
    internal readonly ComboBox LinkBox;
    internal readonly StackPanel PairPanel;
    internal readonly TextBlock PairNote;
    internal readonly ComboBox TagTypeBox;
    internal readonly CheckBox TagMissing;
    internal readonly CheckBox OnlyInCrop;
    internal readonly CheckBox DeleteDuplicates;
    internal readonly CheckBox ShowRooms;
    internal readonly Button CheckButton;
    internal readonly ListView Results;
    internal readonly TextBox Details;
    internal readonly TextBlock Summary;
    internal readonly TextBlock Problem;
    internal readonly Button ApplyButton;

    private readonly RtScan _scan;
    private readonly Func<IReadOnlyList<(long ViewId, RtSource Source)>, RtOptions, RtPlan> _planner;
    private readonly List<RtViewRow> _rows;
    private readonly List<(string Label, long LinkId)> _links;
    private readonly Dictionary<string, string> _savedPairs;

    /// <summary>Host level id -> chosen source (null = not set), for the levels on show.</summary>
    private readonly Dictionary<long, RtSource?> _pairing = [];
    internal Dictionary<long, ComboBox> PairBoxes { get; } = [];
    private string _checkedFingerprint = "";
    private bool _building = true;

    internal RtPlan? Plan { get; private set; }
    internal RtOptions Options => new(TagMissing.IsChecked == true, OnlyInCrop.IsChecked == true, DeleteDuplicates.IsChecked == true, ShowRooms.IsChecked == true,
        TagTypeBox.SelectedIndex >= 0 ? _scan.TagTypeList[TagTypeBox.SelectedIndex].Id : -1);
    internal List<RtViewRow> Rows => _rows;
    internal List<RtLine> Lines { get; private set; } = [];

    public RoomTagWindow(RtScan scan, RoomTagSettings saved, Func<IReadOnlyList<(long ViewId, RtSource Source)>, RtOptions, RtPlan> planner)
    {
        _scan = scan;
        _planner = planner;
        _savedPairs = new Dictionary<string, string>(saved.Pairings, StringComparer.Ordinal);
        _rows = [.. scan.Views.Select(v => new RtViewRow(v) { IsChecked = v.Active })];
        _links = [.. scan.Sources.GroupBy(s => s.LinkId).Select(g => (g.First().LinkLabel, g.Key))];

        Title = DialogTitle;
        Size area = RevitScreen.WorkArea();
        Width = Math.Min(1320, area.Width - 40);
        Height = Math.Min(860, area.Height - 40);
        MinWidth = 1000;
        MinHeight = 600;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = false;
        new WindowInteropHelper(this).Owner = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;

        StackPanel head = new() { Margin = new Thickness(FormParts.Gutter, FormParts.Gutter, FormParts.Gutter, 4) };
        TextBlock desc = FormParts.Hint(scan.Description);
        desc.FontSize = 12;
        head.Children.Add(desc);
        foreach (string n in scan.Notes)
        {
            TextBlock t = FormParts.Hint(n);
            t.Margin = new Thickness(0, 0, 0, 3);
            head.Children.Add(t);
        }

        // ---- left: views ----------------------------------------------------------------
        DockPanel left = new();
        StackPanel lHead = new();
        lHead.Children.Add(FormParts.Section("1  Views to check", first: true));
        (Grid sHost, TextBox sBox) = FormParts.PlaceholderInput("Search - view, level or sheet");
        ViewSearch = sBox;
        ViewSearch.TextChanged += (_, _) => FillViews();
        sHost.Margin = new Thickness(0, 0, 0, 6);
        lHead.Children.Add(sHost);
        DockPanel.SetDock(lHead, Dock.Top);
        left.Children.Add(lHead);
        DockPanel lFoot = new() { Margin = new Thickness(0, 6, 0, 0) };
        StackPanel lButtons = new() { Orientation = Orientation.Horizontal };
        lButtons.Children.Add(Small("All shown", () => TickAll([.. ViewList.Items.Cast<RtViewRow>()], true)));
        lButtons.Children.Add(Small("None", () => TickAll(_rows, false)));
        DockPanel.SetDock(lButtons, Dock.Right);
        lFoot.Children.Add(lButtons);
        ViewCount = FormParts.Status();
        ViewCount.Margin = new Thickness(0, 4, 0, 0);
        lFoot.Children.Add(ViewCount);
        DockPanel.SetDock(lFoot, Dock.Bottom);
        left.Children.Add(lFoot);
        ViewList = new ListBox { MinHeight = 160, ItemTemplate = CheckTemplate() };
        left.Children.Add(ViewList);

        // ---- right: link, pairing, options --------------------------------------------------
        StackPanel right = new();
        right.Children.Add(FormParts.Section("2  The link the rooms live in", first: true));
        LinkBox = new ComboBox { MinHeight = 26 };
        foreach ((string label, _) in _links)
            LinkBox.Items.Add(label);
        LinkBox.SelectionChanged += (_, _) => LinkChanged();
        right.Children.Add(LinkBox);
        right.Children.Add(FormParts.Section("3  Each level takes its rooms from"));
        right.Children.Add(FormParts.Hint("Paired by name where the two models agree. Where they do not, choose the linked level - nothing is guessed."));
        PairPanel = new StackPanel();
        right.Children.Add(new ScrollViewer { Content = PairPanel, MaxHeight = 220, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        PairNote = FormParts.Status();
        PairNote.Margin = new Thickness(0, 4, 0, 0);
        right.Children.Add(PairNote);

        right.Children.Add(FormParts.Section("4  What to do"));
        DockPanel typeRow = new() { Margin = new Thickness(0, 0, 0, 6) };
        TextBlock typeLabel = new() { Text = "Tag type:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        DockPanel.SetDock(typeLabel, Dock.Left);
        typeRow.Children.Add(typeLabel);
        TagTypeBox = new ComboBox { MinHeight = 24, ToolTip = "The Room Tag type new tags are placed with. The model's default is chosen until you pick another." };
        foreach ((long id, string name) in scan.TagTypeList)
            TagTypeBox.Items.Add(id == scan.DefaultTagType ? name + "   (default)" : name);
        int remembered = scan.TagTypeList.FindIndex(t => t.Name == saved.TagType);
        int dflt = scan.TagTypeList.FindIndex(t => t.Id == scan.DefaultTagType);
        TagTypeBox.SelectedIndex = remembered >= 0 ? remembered : dflt >= 0 ? dflt : (scan.TagTypeList.Count > 0 ? 0 : -1);
        TagTypeBox.SelectionChanged += (_, _) => Changed();
        typeRow.Children.Add(TagTypeBox);
        right.Children.Add(typeRow);
        TagMissing = Option("Tag missing rooms - one tag at each untagged room", saved.TagMissing);
        DeleteDuplicates = Option("Delete duplicate tags - keep the oldest tag per room", saved.DeleteDuplicates);
        OnlyInCrop = Option("Only rooms inside the view's crop region or scope box", saved.OnlyInCrop);
        ShowRooms = Option("Switch Rooms / Room Tags / RVT Links back on where they are off", saved.ShowRooms);
        foreach (CheckBox c in new[] { TagMissing, DeleteDuplicates, OnlyInCrop, ShowRooms })
        {
            right.Children.Add(c);
            c.Click += (_, _) => Changed();
        }
        CheckButton = FormParts.Secondary("Check");
        CheckButton.ToolTip = "Work out every tag and deletion and list them below. Nothing is written.";
        CheckButton.Click += (_, _) => RunCheck();

        Grid top = new() { Margin = new Thickness(FormParts.Gutter, 0, FormParts.Gutter, 0) };
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(55, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(45, GridUnitType.Star) });
        top.Children.Add(left);
        ScrollViewer rightScroll = new() { Content = right, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(0, 0, 4, 0) };
        Grid.SetColumn(rightScroll, 2);
        top.Children.Add(rightScroll);

        // ---- bottom: what will happen ---------------------------------------------------------
        Results = MakeResults();
        Results.SizeChanged += (_, _) => FormParts.FitColumns(Results, 28, 26, 46);
        Details = new TextBox
        {
            IsReadOnly = true,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 11.5,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            TextWrapping = TextWrapping.NoWrap,
        };
        TabControl tabs = new() { Margin = new Thickness(FormParts.Gutter, 10, FormParts.Gutter, 0), MinHeight = 170 };
        tabs.Items.Add(new TabItem { Header = "What will happen", Content = Results });
        tabs.Items.Add(new TabItem { Header = "Per view", Content = Details });

        Grid body = new();
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(58, GridUnitType.Star) });
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(42, GridUnitType.Star) });
        body.Children.Add(top);
        Grid.SetRow(tabs, 1);
        body.Children.Add(tabs);

        Summary = new TextBlock { TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold };
        Problem = FormParts.Problem();
        Problem.Margin = new Thickness(0, 4, 0, 0);
        StackPanel band = new();
        band.Children.Add(Summary);
        band.Children.Add(Problem);
        ApplyButton = FormParts.Primary("Apply");
        ApplyButton.IsDefault = false;
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
            Child = FormParts.ButtonBar(CheckButton, ApplyButton, FormParts.Secondary("Cancel", true)),
        });

        DockPanel root = new();
        DockPanel.SetDock(head, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(head);
        root.Children.Add(footer);
        root.Children.Add(body);
        Content = root;

        foreach (RtViewRow r in _rows)
            r.PropertyChanged += (_, _) => Changed();
        _building = false;
        FillViews();
        if (_links.Count >= 1)
            LinkBox.SelectedIndex = 0;   // one link is the normal case: nothing to decide
        Changed();
    }

    // ------------------------------------------------------------------ parts

    private static Button Small(string text, Action click)
    {
        Button b = new() { Content = text, Padding = new Thickness(10, 2, 10, 2), MinHeight = 24, Margin = new Thickness(6, 0, 0, 0) };
        b.Click += (_, _) => click();
        return b;
    }

    private static CheckBox Option(string text, bool on) => new() { Content = text, IsChecked = on, Margin = new Thickness(0, 3, 0, 3) };

    private static DataTemplate CheckTemplate()
    {
        FrameworkElementFactory box = new(typeof(CheckBox));
        box.SetBinding(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, new Binding("IsChecked") { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
        box.SetBinding(ContentControl.ContentProperty, new Binding("Text"));
        return new DataTemplate { VisualTree = box };
    }

    private static ListView MakeResults()
    {
        GridView gv = new();
        foreach ((string h, string p, double w) in new[] { ("View", "View", 300.0), ("Room", "Room", 280.0), ("Action", "Action", 460.0) })
        {
            FrameworkElementFactory t = new(typeof(TextBlock));
            t.Name = "t";
            t.SetBinding(TextBlock.TextProperty, new Binding(p));
            t.SetBinding(FrameworkElement.ToolTipProperty, new Binding(p));
            t.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
            DataTemplate template = new() { VisualTree = t };
            if (p == "Action")
            {
                foreach ((string tone, Color c) in new[] { ("ok", Color.FromRgb(0x1E, 0x7B, 0x34)), ("warn", Color.FromRgb(0x9A, 0x60, 0x00)), ("bad", Color.FromRgb(0xB3, 0x26, 0x1E)) })
                {
                    DataTrigger trig = new() { Binding = new Binding("Tone"), Value = tone };
                    trig.Setters.Add(new Setter(TextBlock.ForegroundProperty, new SolidColorBrush(c), "t"));
                    template.Triggers.Add(trig);
                }
            }
            gv.Columns.Add(new GridViewColumn { Header = h, Width = w, CellTemplate = template });
        }
        return new ListView { View = gv };
    }

    // ------------------------------------------------------------------ views and links

    private static bool Matches(string text, string search)
        => search.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).All(w => text.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0);

    private void FillViews()
    {
        string s = ViewSearch.Text ?? "";
        ViewList.ItemsSource = _rows.Where(r => Matches(r.Text, s)).ToList();
        Changed();
    }

    /// <summary>
    /// Tick many rows with ONE refresh. Each row's own change event rebuilds the pairing panel,
    /// so ticking 500 views one by one rebuilt it 500 times.
    /// </summary>
    internal void TickAll(IEnumerable<RtViewRow> rows, bool on)
    {
        _refreshing = true;
        try
        {
            foreach (RtViewRow r in rows)
                r.IsChecked = on;
        }
        finally
        {
            _refreshing = false;
        }
        Changed();
    }

    internal long? LinkId => LinkBox.SelectedIndex >= 0 ? _links[LinkBox.SelectedIndex].LinkId : null;

    private List<RtSource> Candidates() => LinkId is long id ? [.. _scan.Sources.Where(s => s.LinkId == id)] : [];

    /// <summary>A different link means different levels: every pairing against the old one is void.</summary>
    private void LinkChanged()
    {
        _pairing.Clear();
        Changed();
    }

    private string PairKey(RtSource s, string hostLevel) => $"{s.LinkTitle}|{hostLevel}".ToLowerInvariant();

    /// <summary>Rebuild the pairing rows from the ticked views, keeping what was chosen and auto-pairing the rest.</summary>
    /// <summary>How many times the pairing panel has been rebuilt (tests: ticking many views is ONE rebuild).</summary>
    internal int PairingBuilds { get; private set; }

    private void RefreshPairing()
    {
        PairingBuilds++;
        List<RtSource> cands = Candidates();
        List<(long Id, string Name, int Views)> levels = [.. _rows.Where(r => r.IsChecked)
            .GroupBy(r => r.View.LevelId)
            .Select(g => (g.Key, g.First().View.LevelName, g.Count()))
            .OrderBy(l => l.LevelName, StringComparer.OrdinalIgnoreCase)];

        foreach (long gone in _pairing.Keys.Where(k => levels.All(l => l.Id != k)).ToList())
            _pairing.Remove(gone);
        Dictionary<long, int> auto = RoomTagMath.AutoPair(levels.Select(l => (l.Id, l.Name)), [.. cands.Select(c => c.LevelName)]);
        foreach ((long id, string name, _) in levels)
        {
            if (_pairing.ContainsKey(id))
                continue;
            RtSource? remembered = cands.FirstOrDefault(c => _savedPairs.TryGetValue(PairKey(c, name), out string? lvl) && lvl == c.LevelName);
            _pairing[id] = remembered ?? (auto.TryGetValue(id, out int i) ? cands[i] : null);
        }

        PairPanel.Children.Clear();
        PairBoxes.Clear();
        foreach ((long id, string name, int views) in levels)
        {
            Grid row = new() { Margin = new Thickness(0, 0, 0, 6) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.Children.Add(new TextBlock { Text = $"{name}   [{views} view(s)]", VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = name, Margin = new Thickness(0, 0, 8, 0) });
            ComboBox box = new() { MinHeight = 24 };
            box.Items.Add(NotSet);
            foreach (RtSource c in cands)
                box.Items.Add(c.Label);
            RtSource? cur = _pairing[id];
            box.SelectedIndex = cur is null ? 0 : cands.IndexOf(cur) + 1;
            long levelId = id;
            string levelName = name;
            box.SelectionChanged += (_, _) =>
            {
                RtSource? pick = box.SelectedIndex > 0 ? cands[box.SelectedIndex - 1] : null;
                _pairing[levelId] = pick;
                if (pick is not null && RoomTagMath.Normalize(pick.LevelName) != RoomTagMath.Normalize(levelName))
                    _savedPairs[PairKey(pick, levelName)] = pick.LevelName;
                Changed();
            };
            Grid.SetColumn(box, 1);
            row.Children.Add(box);
            PairPanel.Children.Add(row);
            PairBoxes[id] = box;
        }

        int unset = levels.Count(l => _pairing[l.Id] is null);
        PairNote.Text = levels.Count == 0 ? "Tick some views on the left."
            : cands.Count == 0 ? "Pick the link the rooms live in."
            : unset > 0 ? $"{unset} level(s) NOT SET - their views are skipped until you choose."
            : "Every level has a room source.";
    }

    /// <summary>Tests: pair a host level with a linked level by name.</summary>
    internal bool Pair(long hostLevelId, string linkLevel)
    {
        if (!PairBoxes.TryGetValue(hostLevelId, out ComboBox? box))
            return false;
        List<RtSource> cands = Candidates();
        int i = cands.FindIndex(c => c.LevelName == linkLevel);
        box.SelectedIndex = i + 1;
        return i >= 0;
    }

    internal List<(long ViewId, RtSource Source)> Pairs()
        => [.. _rows.Where(r => r.IsChecked && _pairing.TryGetValue(r.View.LevelId, out RtSource? s) && s is not null)
               .Select(r => (r.View.Id, _pairing[r.View.LevelId]!))];

    private string Fingerprint()
        => string.Join(";", Pairs().Select(p => $"{p.ViewId}>{p.Source.LinkId}/{p.Source.LevelId}")) + "|" + (Options with { TagTypeId = -1 });   // the type changes no plan

    // ------------------------------------------------------------------ check and footer

    private bool _refreshing;

    private void Changed()
    {
        if (_building || _refreshing || ApplyButton is null)
            return;
        _refreshing = true;
        try
        {
            RefreshPairing();
            int ticked = _rows.Count(r => r.IsChecked);
            ViewCount.Text = $"{ticked} of {_rows.Count} ticked" + (ViewList.Items.Count != _rows.Count ? $"      showing {ViewList.Items.Count}" : "");
            int pairs = Pairs().Count;
            string? problem = _scan.Sources.Count == 0 ? "No loaded link holds a room - see the note at the top."
                : ticked == 0 ? "Tick at least one view."
                : LinkId is null ? "Pick the link the rooms live in."
                : pairs == 0 ? "No ticked view's level has a room source yet - choose one in step 3."
                : TagMissing.IsChecked != true && DeleteDuplicates.IsChecked != true ? "Tick \"Tag missing rooms\" or \"Delete duplicate tags\" - otherwise there is nothing to do."
                : null;
            CheckButton.IsEnabled = pairs > 0;

            bool stale = Plan is not null && Fingerprint() != _checkedFingerprint;
            if (Plan is null || stale)
            {
                if (stale)
                    ShowPlan(null);
                Summary.Text = problem is null ? "Press Check to see exactly what will change. Nothing is written until Apply." : "";
                SetProblem(problem ?? "");
                ApplyButton.Content = "Apply";
                ApplyButton.IsEnabled = false;
                return;
            }

            int tags = Plan.Tags.Count, deletes = Plan.Deletes.Count;
            Summary.Text = $"{Plan.ViewsChecked} view(s) checked: {Plan.Missing} room(s) without a tag, {Plan.Duplicated} with more than one. "
                           + $"Apply places {tags} tag(s) and deletes {deletes} duplicate(s)."
                           + (Plan.Skipped.Count > 0 ? $" {Plan.Skipped.Count} skipped - see the list." : "");
            SetProblem(problem ?? "");
            ApplyButton.Content = tags + deletes == 0 ? "Nothing to do" : $"Apply ({tags} tag(s), {deletes} deletion(s))";
            ApplyButton.IsEnabled = problem is null && tags + deletes > 0;
        }
        finally
        {
            _refreshing = false;
        }
    }

    internal void RunCheck()
    {
        List<(long, RtSource)> pairs = Pairs();
        if (pairs.Count == 0)
            return;
        try
        {
            Cursor = System.Windows.Input.Cursors.Wait;
            Plan = _planner(pairs, Options);
            _checkedFingerprint = Fingerprint();
        }
        catch (Exception ex)
        {
            Plan = null;
            SetProblem("The check stopped: " + ex.Message);
            return;
        }
        finally
        {
            Cursor = null;
        }
        ShowPlan(Plan);
        Changed();
    }

    private void ShowPlan(RtPlan? plan)
    {
        if (plan is null)
        {
            Plan = null;
            Lines = [];
            Results.ItemsSource = null;
            Details.Text = "";
            return;
        }
        List<RtLine> lines = [];
        lines.AddRange(plan.Tags.Select(t => new RtLine(t.ViewName, t.Room, "tag it", "ok")));
        lines.AddRange(plan.Deletes.Select(d => new RtLine(d.ViewName, d.Room, $"delete duplicate tag {d.TagId} (keep {d.KeepId})", "warn")));
        lines.AddRange(plan.Skipped.Select(s =>
        {
            string[] parts = s.Split(["  |  ", "  -  "], 3, StringSplitOptions.None);
            return parts.Length == 3 ? new RtLine(parts[0], parts[1], "skipped: " + parts[2], "bad") : new RtLine("", "", s, "bad");
        }));
        foreach (KeyValuePair<long, List<string>> h in plan.Hidden)
        {
            string name = _rows.FirstOrDefault(r => r.View.Id == h.Key)?.View.Name ?? h.Key.ToString();
            lines.Add(new RtLine(name, "", ShowRooms.IsChecked == true ? $"switch {string.Join(", ", h.Value)} back on" : $"{string.Join(", ", h.Value)} is off - new tags will not show", ShowRooms.IsChecked == true ? "warn" : "bad"));
        }
        Lines = lines;
        Results.ItemsSource = lines;
        Details.Text = string.Join("\r\n", plan.Report.Concat(plan.Log.Count > 0 ? ["", .. plan.Log] : []));
    }

    private void SetProblem(string text)
    {
        Problem.Text = text;
        Problem.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    internal RoomTagSettings SettingsToSave() => new()
    {
        TagMissing = TagMissing.IsChecked == true,
        OnlyInCrop = OnlyInCrop.IsChecked == true,
        DeleteDuplicates = DeleteDuplicates.IsChecked == true,
        ShowRooms = ShowRooms.IsChecked == true,
        Pairings = new Dictionary<string, string>(_savedPairs),
        TagType = TagTypeBox.SelectedIndex >= 0 ? _scan.TagTypeList[TagTypeBox.SelectedIndex].Name : "",
    };

    private void Accept()
    {
        if (Plan is null || Fingerprint() != _checkedFingerprint || !ApplyButton.IsEnabled)
            return;
        int unsetViews = _rows.Count(r => r.IsChecked && (!_pairing.TryGetValue(r.View.LevelId, out RtSource? s) || s is null));
        if (unsetViews > 0 && Prompt.Show(this, $"{unsetViews} ticked view(s) are on a level with no room source and will be skipped.\n\nApply anyway?",
                DialogTitle, MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        DialogResult = true;
    }
}
