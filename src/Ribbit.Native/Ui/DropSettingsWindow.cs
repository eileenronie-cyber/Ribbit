using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Ribbit.Native.Settings;

namespace Ribbit.Native.Ui;

/// <summary>
/// Drop Connect's settings dialog. All length fields are MILLIMETRES.
///
/// "Allow direct drops" is a real checkbox here. The Python stored it as a
/// number only because rpw's FlexForm could not offer anything but text boxes.
/// </summary>
internal sealed class DropSettingsWindow : Window
{
    private readonly TextBox _directTol;
    private readonly TextBox _riserHeight;
    private readonly TextBox _margin;
    private readonly TextBox _minDrop;
    private readonly CheckBox _allowDirect;

    public DropSettings? Result { get; private set; }

    public DropSettingsWindow(DropSettings saved)
    {
        Title = "Drop Connect Settings (all mm)";
        Width = 540;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = false;
        new WindowInteropHelper(this).Owner =
            System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;
        if (RibbitUi.TestOwner is not null)
            Owner = RibbitUi.TestOwner;   // tests: so a UiScript can find and drive it

        StackPanel panel = new() { Margin = new Thickness(16) };

        _directTol = AddField(panel,
            "Direct-drop threshold (mm) - head closer than this to the branch line is fed by a "
            + "straight drop instead of a return bend",
            saved.DirectDropTolMm);

        _riserHeight = AddField(panel,
            "Return-bend riser height (mm) - how far the riser climbs above the branch centreline "
            + "before turning into the armover",
            saved.RiserHeightMm);

        _margin = AddField(panel,
            "Keep tees this far off a branch segment's ends (mm)",
            saved.MinMainMarginMm);

        _minDrop = AddField(panel,
            "Minimum drop length (mm) - SKIPS any head with a shorter drop, so keep it small "
            + "(sanity check, not a design rule)",
            saved.MinDropLengthMm);

        _allowDirect = new CheckBox
        {
            Content = "Allow DIRECT drops (a bare straight drop off the branch, no return bend)",
            IsChecked = saved.AllowDirectDrops,
            Margin = new Thickness(0, 4, 0, 0),
        };
        panel.Children.Add(_allowDirect);

        Button save = new()
        {
            Content = "Save",
            IsDefault = true,
            MinWidth = 100,
            Padding = new Thickness(10, 4, 10, 4),
            Margin = new Thickness(0, 0, 8, 0),
        };
        save.Click += (_, _) => Accept(saved);

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
            Margin = new Thickness(0, 14, 0, 0),
        };
        buttons.Children.Add(save);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);

        Content = panel;
    }

    private static TextBox AddField(Panel parent, string label, double value)
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
            Margin = new Thickness(0, 0, 0, 14),
        };
        parent.Children.Add(box);
        return box;
    }

    private void Accept(DropSettings saved)
    {
        DropSettings result = new()
        {
            DirectDropTolMm = SprigSettingsWindow.ParseNumber(_directTol.Text, saved.DirectDropTolMm),
            RiserHeightMm = SprigSettingsWindow.ParseNumber(_riserHeight.Text, saved.RiserHeightMm),
            MinMainMarginMm = SprigSettingsWindow.ParseNumber(_margin.Text, saved.MinMainMarginMm),
            MinDropLengthMm = SprigSettingsWindow.ParseNumber(_minDrop.Text, saved.MinDropLengthMm),
            AllowDirectDrops = _allowDirect.IsChecked == true,
        };

        // Negative tolerances would quietly invert the checks they drive, so
        // they get clamped rather than trusted.
        DropSettings d = DropSettings.Defaults;
        if (result.DirectDropTolMm < 0.0)
        {
            Warn("Direct-drop threshold");
            result.DirectDropTolMm = d.DirectDropTolMm;
        }
        if (result.RiserHeightMm < 0.0)
        {
            Warn("Riser height");
            result.RiserHeightMm = d.RiserHeightMm;
        }
        if (result.MinMainMarginMm < 0.0)
        {
            Warn("Tee margin");
            result.MinMainMarginMm = d.MinMainMarginMm;
        }
        if (result.MinDropLengthMm < 0.0)
        {
            Warn("Minimum drop length");
            result.MinDropLengthMm = d.MinDropLengthMm;
        }

        Result = result;
        DialogResult = true;
    }

    private static void Warn(string what)
        => RibbitUi.Notify($"{what} can't be negative - using the default instead.", "Drop Connect");
}
