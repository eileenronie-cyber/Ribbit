using System.Windows;
using System.Windows.Controls;
using Autodesk.Revit.DB;
using Ribbit.Native.Settings;

namespace Ribbit.Native.Ui;

/// <summary>
/// The sets chosen for one side of a clash, and the control that edits them.
///
/// FLATTENS RATHER THAN EXTENDS. A set is keywords, values and categories — the
/// three things the set builders already take. So choosing sets adds nothing new
/// to the search layer: the window unions each side's sets into the same
/// keyword / exact-pair / forced-category arguments that a typed keyword and a
/// Browse pick have always produced. Naming a group of rules does not need a new
/// kind of query.
///
/// Sets are tracked by stable ID, not by name, so RENAMING a set in the library
/// keeps every selection pointing at it. Tracking a reference rather than a copy
/// also means an edited set takes effect on the next run instead of freezing
/// what it held when chosen. A reference whose set has since been deleted is
/// shown as missing rather than silently dropped — an empty side is otherwise
/// indistinguishable from a side you never filled in.
/// </summary>
internal sealed class SetSidePicker
{
    private readonly ClashSetLibrary _library;
    private readonly Window _owner;
    private readonly string _side;
    private readonly Action _onChanged;

    private readonly List<string> _chosen;
    private readonly WrapPanel _chips;

    /// <summary>The whole control, ready to drop into a set card.</summary>
    public StackPanel Panel { get; }

    public SetSidePicker(
        ClashSetLibrary library,
        Window owner,
        string side,
        IEnumerable<string> chosen,
        Action onChanged)
    {
        _library = library;
        _owner = owner;
        _side = side;
        _onChanged = onChanged;
        _chosen = [.. chosen];

        _chips = FormParts.ChipHost();

        Button choose = new()
        {
            Content = "Choose sets...",
            MinWidth = 106,
            MinHeight = 26,
            FontSize = 11.5,
            Padding = new Thickness(8, 2, 8, 2),
            Margin = new Thickness(0, 5, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            ToolTip = $"Pick named sets from the library to drive {side}",
        };
        choose.Click += (_, _) => Choose();

        Panel = new StackPanel();
        Panel.Children.Add(FormParts.FieldLabel("Sets"));
        Panel.Children.Add(_chips);
        Panel.Children.Add(choose);

        Refresh();
    }

    private void Choose()
    {
        SetPickWindow dlg = new(_library, _side, _chosen) { Owner = _owner };

        if (dlg.ShowDialog() != true)
            return;

        _chosen.Clear();
        _chosen.AddRange(dlg.Result);
        Refresh();
        _onChanged();
    }

    private void Refresh()
    {
        _chips.Children.Clear();

        if (_chosen.Count == 0)
        {
            _chips.Children.Add(FormParts.EmptyChipNote("None chosen - keyword only."));
            return;
        }

        foreach (string id in _chosen)
        {
            string captured = id;
            ClashSet? set = _library.FindById(captured);

            // The chip shows the NAME even though the reference is an id — the
            // id is machinery, and a row of GUIDs would be unreadable.
            Border chip = FormParts.Chip(
                set is null ? "missing" : "",
                set?.Name ?? "deleted set",
                () =>
                {
                    _chosen.Remove(captured);
                    Refresh();
                    _onChanged();
                });

            if (set is null)
            {
                chip.BorderBrush = System.Windows.Media.Brushes.DarkGoldenrod;
                chip.ToolTip = "This set is no longer in the library, so it contributes "
                               + $"nothing to this run.\nReference: {captured}";
            }
            else
            {
                chip.ToolTip = $"{set.Name}\n{ClashSetLibrary.Describe(set)}";
            }

            _chips.Children.Add(chip);
        }
    }

    /// <summary>The sets that still exist, in the order chosen - what a side is composed from.</summary>
    public List<ClashSet> LiveSets() => Live();

    /// <summary>The sets that still exist, in the order chosen.</summary>
    private List<ClashSet> Live()
        => [.. _chosen.Select(_library.FindById).Where(s => s is not null).Select(s => s!)];

    /// <summary>The chosen set IDS, which is what gets stored.</summary>
    public List<string> ChosenIds() => [.. _chosen];

    /// <summary>
    /// Replaces the selection — how applying a saved test fills this side in.
    /// Does NOT raise the change callback: the caller is mid-way through setting
    /// several controls and will refresh once at the end rather than once per
    /// field.
    /// </summary>
    public void SetChosen(IEnumerable<string> ids)
    {
        _chosen.Clear();
        _chosen.AddRange(ids);
        Refresh();
    }

    /// <summary>References whose set has since been deleted, for the pre-run warning.</summary>
    public List<string> MissingIds()
        => [.. _chosen.Where(n => _library.FindById(n) is null)];

    /// <summary>Every keyword across the chosen sets, deduplicated.</summary>
    public List<string> Keywords()
        => [.. Live().SelectMany(s => s.Keywords)
                     .Distinct(StringComparer.OrdinalIgnoreCase)];

    /// <summary>Every exact value across the chosen sets, deduplicated.</summary>
    public List<(string Param, string Value)> Values()
    {
        List<(string Param, string Value)> outp = [];
        HashSet<(string, string)> seen = [];

        foreach (ClashSet set in Live())
        {
            foreach ((string param, string value) in set.Pairs())
            {
                if (seen.Add((param.ToLowerInvariant(), value.ToLowerInvariant())))
                    outp.Add((param, value));
            }
        }

        return outp;
    }

    /// <summary>
    /// Every category across the chosen sets, deduplicated — two sets legitimately
    /// overlap, and a category collected twice would double what it contributes.
    /// </summary>
    public List<BuiltInCategory> Categories()
        => [.. Live().SelectMany(s => s.Resolved()).Distinct()];
}
