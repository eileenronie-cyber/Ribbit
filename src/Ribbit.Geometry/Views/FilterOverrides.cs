namespace Ribbit.Geometry.Views;

/// <summary>An override colour, or none.</summary>
public sealed record Rgb(byte R, byte G, byte B)
{
    public override string ToString() => $"RGB {R}-{G}-{B}";
}

/// <summary>A line pattern by name ("&lt;solid&gt;" is Revit's built-in solid, which has no element).</summary>
public sealed record LinePatternRef(string Name, string? UniqueId)
{
    public const string SolidName = "<solid>";
    public bool IsSolid => Name == SolidName;
}

/// <summary>A fill pattern by name and target ("Drafting" / "Model") - two patterns may share a name.</summary>
public sealed record FillPatternRef(string Name, string Target, string? UniqueId);

/// <summary>Projection or cut lines: colour, pattern, weight - each may be unset.</summary>
public sealed record LineOverride(Rgb? Color, LinePatternRef? Pattern, int? Weight)
{
    public static LineOverride None { get; } = new(null, null, null);
}

/// <summary>One of the four pattern slots (surface / cut, foreground / background).</summary>
public sealed record PatternOverride(FillPatternRef? Pattern, Rgb? Color, bool Visible)
{
    public static PatternOverride None { get; } = new(null, null, true);
}

/// <summary>
/// Everything a view template says about one filter, as plain data: no Revit
/// element in it, so it survives the source model being closed, and it can be
/// compared and described without one. Ported from the row layout of
/// "Filter v2.dyn" / "Filter_Revit_to_Revit_v2.dyn" (node 2), which reads cut
/// settings, halftone and the pattern-visible flags as well as projection -
/// the version before them wrote projection onto both, and dropped filters
/// with no override at all.
/// </summary>
public sealed record FilterOverrideRow(
    string Name,
    string SourceUniqueId,
    bool Visible,
    bool Enabled,
    bool Halftone,
    int Transparency,
    LineOverride ProjectionLine,
    LineOverride CutLine,
    PatternOverride SurfaceForeground,
    PatternOverride SurfaceBackground,
    PatternOverride CutForeground,
    PatternOverride CutBackground,
    string? DetailLevel)
{
    /// <summary>Which graphic overrides this filter actually carries.</summary>
    public List<string> OverrideBits()
    {
        List<string> bits = [];
        if (ProjectionLine.Color is not null || CutLine.Color is not null)
            bits.Add("line colour");
        if (ProjectionLine.Pattern is not null || CutLine.Pattern is not null)
            bits.Add("line pattern");
        if (ProjectionLine.Weight is not null || CutLine.Weight is not null)
            bits.Add("line weight");
        if (SurfaceForeground.Pattern is not null || CutForeground.Pattern is not null)
            bits.Add("fill");
        if (SurfaceBackground.Pattern is not null || CutBackground.Pattern is not null)
            bits.Add("background");
        if (Transparency > 0)
            bits.Add($"{Transparency}% transparent");
        if (Halftone)
            bits.Add("halftone");
        if (DetailLevel is not null)
            bits.Add("detail level");
        return bits;
    }

    public bool HasOverrides => OverrideBits().Count > 0;

    /// <summary>"hidden, line colour, 50% transparent" - or "no overrides".</summary>
    public string Describe()
    {
        List<string> bits = [];
        if (!Visible)
            bits.Add("hidden");
        if (!Enabled)
            bits.Add("disabled");
        bits.AddRange(OverrideBits());
        return bits.Count == 0 ? "no overrides" : string.Join(", ", bits);
    }

    /// <summary>The source elements this row needs in the target model, by kind.</summary>
    public IEnumerable<LinePatternRef> LinePatterns()
    {
        foreach (LinePatternRef? p in new[] { ProjectionLine.Pattern, CutLine.Pattern })
            if (p is not null && !p.IsSolid)
                yield return p;
    }

    public IEnumerable<FillPatternRef> FillPatterns()
    {
        foreach (PatternOverride slot in new[] { SurfaceForeground, SurfaceBackground, CutForeground, CutBackground })
            if (slot.Pattern is not null)
                yield return slot.Pattern;
    }
}

/// <summary>A view template as the pickers list it.</summary>
public sealed record TemplateEntry(long Id, string ViewType, string Name)
{
    /// <summary>"Floor Plan  |  COORDINATION" - set by <see cref="TemplateLabels.Build"/>.</summary>
    public string Label { get; init; } = "";
}

/// <summary>
/// Labels for view templates: sorted plans, then sections and elevations, then
/// 3D, then the rest, then by name; the view type in the label, because
/// "COORDINATION" as a plan and as a section are two different templates.
/// </summary>
public static class TemplateLabels
{
    public static int Rank(string viewType) => viewType switch
    {
        "FloorPlan" or "CeilingPlan" or "AreaPlan" or "EngineeringPlan" => 0,
        "Section" or "Elevation" or "Detail" => 1,
        "ThreeD" => 2,
        _ => 3,
    };

    /// <summary>"FloorPlan" -> "Floor Plan", "ThreeD" -> "3D".</summary>
    public static string PrettyType(string viewType)
    {
        if (viewType == "ThreeD")
            return "3D";
        System.Text.StringBuilder sb = new();
        for (int i = 0; i < viewType.Length; i++)
        {
            if (i > 0 && char.IsUpper(viewType[i]))
                sb.Append(' ');
            sb.Append(viewType[i]);
        }
        return sb.ToString();
    }

    /// <summary>Sorted, labelled, and every label unique ("... (1)" for a repeat).</summary>
    public static List<TemplateEntry> Build(IEnumerable<TemplateEntry> templates)
    {
        List<TemplateEntry> sorted = [.. templates
            .OrderBy(t => Rank(t.ViewType))
            .ThenBy(t => PrettyType(t.ViewType), StringComparer.Ordinal)
            .ThenBy(t => t.Name.ToLowerInvariant(), StringComparer.Ordinal)
            .ThenBy(t => t.Id)];
        Dictionary<string, int> seen = new(StringComparer.Ordinal);
        List<TemplateEntry> output = [];
        foreach (TemplateEntry t in sorted)
        {
            string label = $"{PrettyType(t.ViewType)}  |  {t.Name}";
            if (seen.TryGetValue(label, out int n))
            {
                seen[label] = n + 1;
                label = $"{label}  ({n + 1})";
            }
            else
            {
                seen[label] = 0;
            }
            output.Add(t with { Label = label });
        }
        return output;
    }
}
