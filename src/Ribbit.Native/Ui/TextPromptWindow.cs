using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;

namespace Ribbit.Native.Ui;

/// <summary>
/// A one- or two-field prompt: an optional parameter chooser and a text box.
///
/// Exists because a value has to be addable BEFORE it exists in the model — a
/// set built for next month's package, or carried to a project where the system
/// is spelled the same but not yet placed. Browse cannot offer what is not
/// there, so this is the other half of adding a value.
/// </summary>
internal sealed class TextPromptWindow : Window
{
    private readonly ComboBox? _param;
    private readonly TextBox _value;

    public string Param => _param?.Text?.Trim() ?? "";

    public string Value => (_value.Text ?? "").Trim();

    public TextPromptWindow(
        string title, string? paramLabel, string valueLabel, IReadOnlyList<string>? paramOptions)
    {
        Title = title;
        Width = 420;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        new WindowInteropHelper(this).Owner =
            System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;

        StackPanel panel = new() { Margin = new Thickness(FormParts.Gutter) };

        if (paramLabel is not null)
        {
            panel.Children.Add(FormParts.FieldLabel(paramLabel));

            // Editable, not a fixed list: System Type and Description are the two
            // the search reads, but typing another is not an error worth blocking
            // - it simply matches nothing until that parameter is read.
            _param = new ComboBox
            {
                IsEditable = true,
                ItemsSource = paramOptions,
                SelectedIndex = paramOptions is { Count: > 0 } ? 0 : -1,
                MinHeight = 26,
                Margin = new Thickness(0, 0, 0, 12),
            };
            panel.Children.Add(_param);
        }

        panel.Children.Add(FormParts.FieldLabel(valueLabel));
        _value = FormParts.Input();
        panel.Children.Add(_value);

        Button ok = FormParts.Primary("Add");
        ok.Click += (_, _) => { DialogResult = true; };

        panel.Children.Add(FormParts.ButtonBar(ok, FormParts.Secondary("Cancel", true)));

        Content = panel;
        Loaded += (_, _) => _value.Focus();
    }
}
