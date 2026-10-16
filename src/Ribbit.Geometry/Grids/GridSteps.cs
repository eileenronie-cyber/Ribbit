namespace Ribbit.Geometry.Grids;

/// <summary>
/// What a run does to one view's grids. Offsets and the gap are MILLIMETRES
/// here, exactly as typed; the engine converts to feet.
/// </summary>
public sealed record GridSteps
{
    /// <summary>STEP 1 - convert both ends to the view-specific (2D) extent.</summary>
    public bool Convert { get; init; } = true;

    /// <summary>STEP 2 - stretch to the crop box (or scope box) plus the offsets.</summary>
    public bool Align { get; init; } = true;

    /// <summary>STEP 3 - show the bubble on the chosen end only.</summary>
    public bool Bubbles { get; init; } = true;

    /// <summary>STEP 4 - push clashing bubbles apart onto an elbow.</summary>
    public bool Spread { get; init; }

    public double LeftMm { get; init; } = 3000;
    public double RightMm { get; init; } = 3000;
    public double TopMm { get; init; } = 3000;
    public double BottomMm { get; init; } = 3000;

    /// <summary>Bubble on the LEFT end of horizontal grids (false = right).</summary>
    public bool BubbleLeft { get; init; } = true;

    /// <summary>Bubble on the TOP end of vertical grids (false = bottom).</summary>
    public bool BubbleTop { get; init; } = true;

    /// <summary>STEP 4: how far apart the bubbles must end up.</summary>
    public double GapMm { get; init; } = 3000;

    public bool AnyStep => Convert || Align || Bubbles || Spread;

    /// <summary>
    /// The settings saved against a scope box laid over these, key by key.
    /// Only what the preset holds replaces anything: a set saved before
    /// presets existed carries grids and nothing else, and must not reset
    /// the offsets to something nobody chose. A preset written before STEP 4
    /// existed carries three steps, and the missing fourth reads as off.
    /// </summary>
    public GridSteps With(GridPreset? preset)
    {
        if (preset is null)
            return this;

        GridSteps s = this;
        if (preset.Steps is { Count: >= 3 } steps)
        {
            s = s with
            {
                Convert = steps[0],
                Align = steps[1],
                Bubbles = steps[2],
                Spread = steps.Count > 3 && steps[3],
            };
        }

        if (preset.Offsets is { Count: 4 } o)
            s = s with { LeftMm = o[0], RightMm = o[1], TopMm = o[2], BottomMm = o[3] };

        if (preset.Bubbles is { Count: 2 } b)
            s = s with { BubbleLeft = b[0], BubbleTop = b[1] };

        if (preset.GapMm is double gap)
            s = s with { GapMm = gap };

        return s;
    }

    /// <summary>The first reason these settings would spoil a run, or null.</summary>
    public string? Problem(bool hideOnlyAllowed)
    {
        if (!AnyStep)
            return hideOnlyAllowed ? null : "tick at least one of the steps.";
        if (Spread && !(GapMm > 0))
            return "STEP 4 needs a distance above 0 millimetres to spread the bubbles by.";
        return null;
    }
}

/// <summary>
/// What is remembered against one scope box: its grid set and, optionally,
/// the settings to use for it. Any part can be missing.
/// </summary>
public sealed class GridPreset
{
    public string Name { get; set; } = "";

    /// <summary>Grid UniqueIds. Empty is "no set", never "every grid".</summary>
    public List<string> Grids { get; set; } = [];

    public List<bool>? Steps { get; set; }

    /// <summary>left, right, top, bottom - millimetres.</summary>
    public List<double>? Offsets { get; set; }

    /// <summary>[bubble left, bubble top].</summary>
    public List<bool>? Bubbles { get; set; }

    public double? GapMm { get; set; }

    public bool HasSettings => Steps is not null || Offsets is not null
                               || Bubbles is not null || GapMm is not null;

    /// <summary>A copy that owns its own lists.</summary>
    public GridPreset Clone() => new()
    {
        Name = Name,
        Grids = [.. Grids],
        Steps = Steps is null ? null : [.. Steps],
        Offsets = Offsets is null ? null : [.. Offsets],
        Bubbles = Bubbles is null ? null : [.. Bubbles],
        GapMm = GapMm,
    };

    public void TakeSettingsFrom(GridPreset other)
    {
        Steps = other.Steps is null ? null : [.. other.Steps];
        Offsets = other.Offsets is null ? null : [.. other.Offsets];
        Bubbles = other.Bubbles is null ? null : [.. other.Bubbles];
        GapMm = other.GapMm;
    }

    public void TakeSettingsFrom(GridSteps s)
    {
        Steps = [s.Convert, s.Align, s.Bubbles, s.Spread];
        Offsets = [s.LeftMm, s.RightMm, s.TopMm, s.BottomMm];
        Bubbles = [s.BubbleLeft, s.BubbleTop];
        GapMm = s.GapMm;
    }
}
