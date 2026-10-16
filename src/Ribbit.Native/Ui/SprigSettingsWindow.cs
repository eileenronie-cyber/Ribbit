using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Ribbit.Native.Settings;

namespace Ribbit.Native.Ui;

/// <summary>
/// Sprig Connect's settings dialog — the replacement for rpw's FlexForm, which
/// is exactly the dependency that broke this extension once already (FlexForm
/// went missing in pyRevit 6.5.3).
///
/// All fields are MILLIMETRES. Labels wrap rather than clip: the first Python
/// cut used full-sentence labels that got cut off mid-word, which made the
/// settings unreadable.
/// </summary>
internal sealed class SprigSettingsWindow : Window
{
    private readonly TextBox _direct;
    private readonly TextBox _margin;
    private readonly TextBox _minSprig;

    public SprigSettings? Result { get; private set; }

    public SprigSettingsWindow(SprigSettings saved)
    {
        Title = "Sprig Connect Settings (all mm)";
        Width = 520;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = false;
        new WindowInteropHelper(this).Owner =
            System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;
        if (RibbitUi.TestOwner is not null)
            Owner = RibbitUi.TestOwner;   // tests: so a UiScript can find and drive it

        StackPanel panel = new() { Margin = new Thickness(16) };

        _direct = AddField(panel,
            "Direct-sprig threshold (mm) - head closer than this to the branch line gets a "
            + "straight sprig, not an armover",
            saved.DirectSprigTolMm);

        _margin = AddField(panel,
            "Keep tees this far off a branch segment's ends (mm)",
            saved.MinMainMarginMm);

        _minSprig = AddField(panel,
            "Minimum sprig length (mm) - SKIPS any head with a shorter sprig, so keep it small "
            + "(sanity check, not a design rule)",
            saved.MinSprigLengthMm);

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

    private void Accept(SprigSettings saved)
    {
        SprigSettings result = new()
        {
            DirectSprigTolMm = ParseNumber(_direct.Text, saved.DirectSprigTolMm),
            MinMainMarginMm = ParseNumber(_margin.Text, saved.MinMainMarginMm),
            MinSprigLengthMm = ParseNumber(_minSprig.Text, saved.MinSprigLengthMm),
        };

        // Negative tolerances would quietly invert the checks they drive, so
        // they get clamped rather than trusted.
        SprigSettings defaults = SprigSettings.Defaults;
        if (result.DirectSprigTolMm < 0.0)
        {
            RibbitUi.Notify("Direct-sprig threshold can't be negative - using the default instead.",
                "Sprig Connect");
            result.DirectSprigTolMm = defaults.DirectSprigTolMm;
        }
        if (result.MinMainMarginMm < 0.0)
        {
            RibbitUi.Notify("Tee margin can't be negative - using the default instead.",
                "Sprig Connect");
            result.MinMainMarginMm = defaults.MinMainMarginMm;
        }
        if (result.MinSprigLengthMm < 0.0)
        {
            RibbitUi.Notify("Minimum sprig length can't be negative - using the default instead.",
                "Sprig Connect");
            result.MinSprigLengthMm = defaults.MinSprigLengthMm;
        }

        Result = result;
        DialogResult = true;
    }

    /// <summary>
    /// Reads the leading numeric portion of the field as a plain number —
    /// "25", "25mm", " 25 " all read as 25. This never goes through Revit's own
    /// unit parsing, so a bare number always means MILLIMETRES; there is no path
    /// by which typing "25" could be read as 25 feet.
    /// </summary>
    internal static double ParseNumber(string? text, double fallback)
    {
        if (string.IsNullOrWhiteSpace(text))
            return fallback;

        // IsNullOrWhiteSpace already proved text is non-null. .NET Framework's
        // reference assemblies do not carry the [NotNullWhen] annotation that
        // lets net8/net10 see that themselves, so only the net48 (Revit 2024)
        // leg needs the assertion - asserted rather than left as a standing
        // warning, which is where a real nullability bug would hide.
        Match match = Regex.Match(text!.Trim(), @"^[-+]?\d*\.?\d+");
        return match.Success && double.TryParse(match.Value, out double value) ? value : fallback;
    }
}
