using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Ribbit.Native.Settings;

namespace Ribbit.Native.Ui;

/// <summary>
/// Slope Branch / Slope Through's shared settings dialog.
///
/// ONE FIELD UP FRONT. The slope is the only value a real job changes run to
/// run - its size in the box, its direction on two buttons asked fresh every
/// run; the other eight are tolerances that were chosen once and are almost
/// never retyped. Showing all nine as equals is how a bad value hides — the same
/// lesson spriglib records, where a min-sprig of 1000 mm and a max-armover of 0
/// rejected a perfectly good layout with neither being noticed.
///
/// They are collapsed, NOT removed, for two concrete reasons: the dry-run report
/// tells the user by name to "raise 'Small assembly size'" and "Raise Max group
/// size", and this is the tool whose saved settings actually drifted in the field
/// (max group 40 vs 400, slope 0). So the Advanced section also OPENS ITSELF
/// whenever anything in it differs from the built-in default — a drifted setting
/// is exactly the one you need to see.
///
/// All length fields are MILLIMETRES; angle is degrees; the two group sizes are
/// counts.
/// </summary>
internal sealed class SlopeSettingsWindow : Window
{
    private readonly Dictionary<string, TextBox> _fields = [];

    private readonly RadioButton _rises;
    private readonly RadioButton _falls;

    public SlopeSettings? Result { get; private set; }

    /// <summary>The direction choices' captions, which the in-Revit suite clicks by.</summary>
    internal const string RisesCaption = "Rises away from the fixed end";
    internal const string FallsCaption = "Falls away from the fixed end";

    /// <summary>
    /// <paramref name="askDirection"/>: true when a run is about to happen. The
    /// fall direction then starts UNCHOSEN and Save stays disabled until one is
    /// picked - which way a whole tree tips is not something to inherit from
    /// whatever the last job happened to need. The settings-only screen
    /// (Shift+Click) passes false and starts on the saved direction.
    /// </summary>
    public SlopeSettingsWindow(SlopeSettings saved, bool askDirection = true)
    {
        Title = "Slope Settings (shared by Slope Branch and Slope Through)";
        Width = 600;
        SizeToContent = SizeToContent.Height;
        MaxHeight = 780;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = false;
        new WindowInteropHelper(this).Owner =
            System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;
        if (RibbitUi.TestOwner is not null)
            Owner = RibbitUi.TestOwner;   // tests: so a UiScript can find and drive it

        StackPanel panel = new() { Margin = new Thickness(18) };

        // ------------------------------------------------------------- slope
        panel.Children.Add(new TextBlock
        {
            Text = "Slope",
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 2),
        });

        panel.Children.Add(new TextBlock
        {
            Text = "Millimetres per metre of plan run, measured along the pipe from the fixed end "
                   + "(through every elbow and tee). 2.0 is 1:500. The direction is chosen below.",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.75,
            Margin = new Thickness(0, 0, 0, 6),
        });

        TextBox slope = new()
        {
            Text = Math.Abs(saved.SlopeMmPerM).ToString("0.###"),
            FontSize = 16,
            Padding = new Thickness(6, 5, 6, 5),
            Margin = new Thickness(0, 0, 0, 8),
        };
        slope.SelectAll();
        _fields["slope"] = slope;
        panel.Children.Add(slope);

        _rises = new RadioButton
        {
            Content = RisesCaption,
            GroupName = "slopeDirection",
            Margin = new Thickness(0, 0, 0, 4),
            ToolTip = "The fixed end is the LOW point - e.g. the outlet, or the drain valve the "
                      + "pipe drains back to.",
        };
        _falls = new RadioButton
        {
            Content = FallsCaption,
            GroupName = "slopeDirection",
            Margin = new Thickness(0, 0, 0, 4),
            ToolTip = "The fixed end is the HIGH point.",
        };
        if (!askDirection)
        {
            _rises.IsChecked = saved.SlopeMmPerM >= 0.0;
            _falls.IsChecked = saved.SlopeMmPerM < 0.0;
        }
        panel.Children.Add(_rises);
        panel.Children.Add(_falls);

        TextBlock chooseHint = new()
        {
            Text = "Pick a direction to continue.",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.75,
            FontStyle = FontStyles.Italic,
            Margin = new Thickness(0, 2, 0, 0),
            Visibility = askDirection ? Visibility.Visible : Visibility.Collapsed,
        };
        panel.Children.Add(chooseHint);

        panel.Children.Add(new TextBlock
        {
            Text = "Everything else is a tolerance you can leave alone.",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.6,
            Margin = new Thickness(0, 0, 0, 14),
        });

        // ---------------------------------------------------------- advanced
        StackPanel advanced = new();

        Add(advanced, "planOffset", saved.PlanOffsetTolMm,
            "Plan offset tolerance (mm) - a picked segment further than this off the run's "
            + "centreline is a different branch line and is dropped");
        Add(advanced, "angle", saved.AngleTolDeg,
            "Angle tolerance (deg) - a picked segment further off the run's direction than this "
            + "is dropped");
        Add(advanced, "tilt", saved.GroupTiltTolMm,
            "Group tilt tolerance (mm) - a group whose joints want to move by more than this much "
            + "apart is left alone");
        Add(advanced, "foreign", saved.ForeignRunMm,
            "Foreign run length (mm) - a connected group containing a pipe longer than this in "
            + "plan is the rest of the system and is never moved, unless the whole group is small");
        Add(advanced, "small", saved.SmallGroupElements,
            "Small assembly size (elements) - a group containing a long pipe still MOVES when its "
            + "complete closure has no more than this many elements. This is what lets a long "
            + "armover ride with the branch.");
        Add(advanced, "maxGroup", saved.MaxGroupElements,
            "Max group size (elements) - same idea by element count");
        Add(advanced, "maxMove", saved.MaxMoveMm,
            "Move warning (mm) - warns when anything would move further than this");
        Add(advanced, "reconnect", saved.ReconnectTolMm,
            "Reconnect tolerance (mm) - how close two open connectors must land before they are "
            + "joined back up");

        bool drifted = HasDrifted(saved);

        Expander expander = new()
        {
            Header = drifted
                ? "Advanced tolerances  -  one or more differ from the defaults"
                : "Advanced tolerances",
            IsExpanded = drifted,
            Content = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                MaxHeight = 380,
                Margin = new Thickness(0, 8, 0, 0),
                Content = advanced,
            },
        };
        panel.Children.Add(expander);

        if (drifted)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "A value below differs from this tool's built-in default. That may be "
                       + "deliberate - but a stale settings file or a dropped digit looks exactly "
                       + "the same, so it is worth a look.",
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.75,
                Margin = new Thickness(0, 8, 0, 0),
            });
        }

        // ----------------------------------------------------------- buttons
        Button reset = new()
        {
            Content = "Reset tolerances",
            MinWidth = 130,
            Padding = new Thickness(10, 4, 10, 4),
            Margin = new Thickness(0, 0, 8, 0),
            ToolTip = "Put every advanced tolerance back to its default. Leaves the slope alone.",
        };
        reset.Click += (_, _) => ResetAdvanced();

        Button save = new()
        {
            Content = "Save",
            IsDefault = true,
            MinWidth = 100,
            Padding = new Thickness(10, 4, 10, 4),
            Margin = new Thickness(0, 0, 8, 0),
        };
        save.Click += (_, _) => Accept(saved);

        void DirectionChosen()
        {
            bool chosen = _rises.IsChecked == true || _falls.IsChecked == true;
            save.IsEnabled = chosen;
            chooseHint.Visibility = chosen ? Visibility.Collapsed : Visibility.Visible;
        }
        _rises.Checked += (_, _) => DirectionChosen();
        _falls.Checked += (_, _) => DirectionChosen();
        DirectionChosen();

        Button cancel = new()
        {
            Content = "Cancel",
            IsCancel = true,
            MinWidth = 90,
            Padding = new Thickness(10, 4, 10, 4),
        };

        StackPanel buttons = new()
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0),
        };
        buttons.Children.Add(reset);
        buttons.Children.Add(save);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);

        Content = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = panel,
        };

        Loaded += (_, _) => { slope.Focus(); slope.SelectAll(); };
    }

    /// <summary>True when any ADVANCED value differs from the built-in default.</summary>
    private static bool HasDrifted(SlopeSettings s)
    {
        SlopeSettings d = SlopeSettings.Defaults;
        static bool Differs(double a, double b) => Math.Abs(a - b) > 1.0e-9;

        return Differs(s.PlanOffsetTolMm, d.PlanOffsetTolMm)
               || Differs(s.AngleTolDeg, d.AngleTolDeg)
               || Differs(s.GroupTiltTolMm, d.GroupTiltTolMm)
               || Differs(s.ForeignRunMm, d.ForeignRunMm)
               || Differs(s.SmallGroupElements, d.SmallGroupElements)
               || Differs(s.MaxGroupElements, d.MaxGroupElements)
               || Differs(s.MaxMoveMm, d.MaxMoveMm)
               || Differs(s.ReconnectTolMm, d.ReconnectTolMm);
    }

    private void ResetAdvanced()
    {
        SlopeSettings d = SlopeSettings.Defaults;
        _fields["planOffset"].Text = d.PlanOffsetTolMm.ToString("0.###");
        _fields["angle"].Text = d.AngleTolDeg.ToString("0.###");
        _fields["tilt"].Text = d.GroupTiltTolMm.ToString("0.###");
        _fields["foreign"].Text = d.ForeignRunMm.ToString("0.###");
        _fields["small"].Text = d.SmallGroupElements.ToString("0.###");
        _fields["maxGroup"].Text = d.MaxGroupElements.ToString("0.###");
        _fields["maxMove"].Text = d.MaxMoveMm.ToString("0.###");
        _fields["reconnect"].Text = d.ReconnectTolMm.ToString("0.###");
    }

    private void Add(Panel parent, string key, double value, string label)
    {
        parent.Children.Add(new TextBlock
        {
            Text = label,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 4),
        });

        TextBox box = new()
        {
            Text = value.ToString("0.###"),
            Padding = new Thickness(4, 3, 4, 3),
            Margin = new Thickness(0, 0, 0, 12),
        };
        parent.Children.Add(box);
        _fields[key] = box;
    }

    private double Read(string key, double fallback)
        => SprigSettingsWindow.ParseNumber(_fields[key].Text, fallback);

    private void Accept(SlopeSettings saved)
    {
        // The box holds a SIZE; the radio buttons hold the direction, and the
        // stored value stays signed (positive rises away from the fixed end) so
        // everything downstream reads it exactly as before. A minus typed in the
        // box is taken as a size too - the buttons are the one place direction is
        // decided, so two of them can never disagree.
        double size = Math.Abs(Read("slope", Math.Abs(saved.SlopeMmPerM)));
        SlopeSettings result = new()
        {
            SlopeMmPerM = _falls.IsChecked == true ? -size : size,
            PlanOffsetTolMm = Read("planOffset", saved.PlanOffsetTolMm),
            AngleTolDeg = Read("angle", saved.AngleTolDeg),
            GroupTiltTolMm = Read("tilt", saved.GroupTiltTolMm),
            ForeignRunMm = Read("foreign", saved.ForeignRunMm),
            SmallGroupElements = Read("small", saved.SmallGroupElements),
            MaxGroupElements = Read("maxGroup", saved.MaxGroupElements),
            MaxMoveMm = Read("maxMove", saved.MaxMoveMm),
            ReconnectTolMm = Read("reconnect", saved.ReconnectTolMm),
        };

        SlopeSettings d = SlopeSettings.Defaults;
        if (result.PlanOffsetTolMm < 0) { Warn("Plan offset tolerance"); result.PlanOffsetTolMm = d.PlanOffsetTolMm; }
        if (result.AngleTolDeg < 0) { Warn("Angle tolerance"); result.AngleTolDeg = d.AngleTolDeg; }
        if (result.GroupTiltTolMm < 0) { Warn("Group tilt tolerance"); result.GroupTiltTolMm = d.GroupTiltTolMm; }
        if (result.ForeignRunMm < 0) { Warn("Foreign run length"); result.ForeignRunMm = d.ForeignRunMm; }
        if (result.SmallGroupElements < 1) { Warn("Small assembly size"); result.SmallGroupElements = d.SmallGroupElements; }
        if (result.MaxGroupElements < 1) { Warn("Max group size"); result.MaxGroupElements = d.MaxGroupElements; }
        if (result.MaxMoveMm < 0) { Warn("Move warning"); result.MaxMoveMm = d.MaxMoveMm; }
        if (result.ReconnectTolMm < 0) { Warn("Reconnect tolerance"); result.ReconnectTolMm = d.ReconnectTolMm; }

        Result = result;
        DialogResult = true;
    }

    private static void Warn(string what)
        => RibbitUi.Notify($"{what} can't be negative or zero - using the default instead.",
            "Slope Settings");
}
