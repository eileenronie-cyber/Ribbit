using System.Windows;
using System.Windows.Controls;

namespace Ribbit.Native.Ui;

/// <summary>
/// The clearance-pad input: a value box beside a unit dropdown.
///
/// Split into two controls because a single free-text box made the unit rule
/// invisible — a bare number means MILLIMETRES here, but
/// <see cref="Ribbit.Geometry.Clash.UnitParse"/> reads a bare number as FEET, so
/// the whole tool hangs on a " mm" that the form appends behind your back. Type
/// "25" meaning 25 mm, lose that append, and every element in the model reports
/// as clashing. A dropdown states the unit instead of assuming it.
///
/// All three clash windows own one of these. The value and unit are persisted
/// per product, so the dropdown does not silently reset to mm on every open.
/// </summary>
internal sealed class PadField
{
    /// <summary>Offered units, in the spelling <c>UnitParse</c> accepts.</summary>
    public static readonly string[] Units = ["mm", "cm", "m", "in", "ft"];

    private readonly TextBox _value;
    private readonly ComboBox _unit;
    private readonly string _defaultValue;

    /// <summary>The row to drop into the form.</summary>
    public StackPanel Row { get; }

    /// <param name="defaultValue">
    /// Used when nothing has been saved yet. Differs per tool — Clash Detect and
    /// Clash Detect Link default to 100, MEP vs Structure to 0 — so it is the
    /// caller's to state, not this control's to assume.
    /// </param>
    public PadField(string defaultValue, string savedValue, string savedUnit, Action onChanged)
    {
        _defaultValue = defaultValue;
        _value = FormParts.Input(savedValue.Length > 0 ? savedValue : defaultValue);
        _value.Width = 90;
        _value.TextChanged += (_, _) => onChanged();

        // An unrecognised saved unit (a hand-edited settings file, or a unit
        // dropped from the list later) falls back to mm rather than to a blank
        // selection, which would make Text() emit a padless string.
        int index = Array.IndexOf(Units, savedUnit);

        _unit = new ComboBox
        {
            ItemsSource = Units,
            SelectedIndex = index >= 0 ? index : 0,
            Width = 78,
            MinHeight = 26,
            Margin = new Thickness(8, 0, 0, 0),
        };
        _unit.SelectionChanged += (_, _) => onChanged();

        Row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 0, 4),
        };
        Row.Children.Add(_value);
        Row.Children.Add(_unit);
    }

    /// <summary>
    /// Replaces both halves — how applying a saved test fills this in.
    ///
    /// A blank value falls back to the default rather than emptying the box,
    /// matching what the constructor does with an unsaved preference: an empty
    /// pad box reads as "no pad", which is a different thing from "unset".
    /// </summary>
    public void Set(string value, string unit)
    {
        _value.Text = value.Length > 0 ? value : _defaultValue;

        int index = Array.IndexOf(Units, unit);
        _unit.SelectedIndex = index >= 0 ? index : 0;
    }

    public string Value => (_value.Text ?? "").Trim();

    public string Unit => _unit.SelectedItem as string ?? "mm";

    /// <summary>
    /// The pad exactly as the engine has always parsed it: value then unit.
    /// Empty when the value is blank, which the engine reads as no pad.
    /// </summary>
    public string Text => Value.Length == 0 ? "" : $"{Value} {Unit}";
}
